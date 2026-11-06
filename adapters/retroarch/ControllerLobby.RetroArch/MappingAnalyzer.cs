using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ControllerLobby.RetroArch;

// Internal declarations supplied by the future authenticated helper/observer integration, not wire messages.
// Matching these fields checks consistency only; this analyzer cannot authenticate hardware or grant Ready.
public sealed record RouteContext(string Session, long HelperGeneration, long TopologyGeneration, string ObservationEpoch);
public sealed record SelectedSeat(int Player, string Kind, string SourceToken, string? OutputId = null, long OutputGeneration = 0);
public sealed record ObservedOutput(string OutputId, long OutputGeneration, string Backend, int Base, int Index, string Observer, string Receipt);
public sealed record ObservationBatch(RouteContext Context, IReadOnlyList<ObservedOutput?> Outputs);
public sealed record LaunchContext(string Root, string MainConfig, string CoreName, string Content);
public enum ReadState { Present, Absent, Unreadable }
public sealed record ConfigRead(ReadState State, string? Text = null, string? RawSha256 = null);
public interface IConfigReader { ConfigRead Read(string absolutePath); }
public sealed record InputStamp(string Path, bool Present, string? Sha256);
public enum AnalysisStatus { Candidate, Unsupported, Unverified, Invalid }
public sealed record MappingResult(AnalysisStatus Status, string Reason, string? AppendConfig,
    IReadOnlyList<SelectedSeat> Seats, IReadOnlyList<InputStamp> Inputs, string OwnerRoot)
{
    // No qualification receiver or launch-authority interface exists in this standalone slice.
    public bool CanLaunch => false; // control:launch-authority
    public static IReadOnlyList<string> RemainingGates { get; } = Array.AsReadOnly(new[]
        { "Q-A", "Q-O", "Q-B", "Q-D", "shim-preparation-and-command-line", "helper-identity-route-release-recovery-completion" });
}

public static class MappingAnalyzer
{
    private static readonly Regex Assignment = new("^([a-z0-9_]+)\\s*=\\s*(.*)$", RegexOptions.CultureInvariant);
    private static readonly Regex Include = new("^#\\s*include\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ProtectedPlayer = new("^input_player[0-9]+_(joypad_index|device_reservation_type|reserved_device)$", RegexOptions.CultureInvariant);
    private static readonly Regex Port = new("^input_remap_port_p([0-9]+)$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Protected = ["input_max_users", "input_joypad_driver", "config_save_on_exit", "remap_save_on_exit", "rgui_config_directory", "input_remapping_directory", "core_options_path"] ; // control:override-path-protection

    public static MappingResult Analyze(LaunchContext launch, RouteContext context, IReadOnlyList<SelectedSeat?>? selected,
        ObservationBatch? observed, IConfigReader reader)
    {
        var inputs = new List<InputStamp>();
        var seats = selected?.Where(s => s != null).Select(s => s!).ToArray() ?? [];
        MappingResult Result(AnalysisStatus status, string reason, string? text = null) =>
            new(status, reason, text, Array.AsReadOnly(seats), inputs.AsReadOnly(), launch.Root);
        try
        {
            if (/*guard:seat-shape*/ (selected == null || selected.Count is < 1 or > 4 || seats.Length != selected.Count
                || seats.Any(s => s.Player is < 1 or > 4 || s.Kind is not ("pad" or "native") || string.IsNullOrWhiteSpace(s.SourceToken))))
                return Result(AnalysisStatus.Invalid, "seat-shape");
            if (/*guard:unique-selection*/ (seats.Select(s => s.Player).Distinct().Count() != seats.Length
                || seats.Select(s => s.SourceToken).Distinct(StringComparer.Ordinal).Count() != seats.Length))
                return Result(AnalysisStatus.Invalid, "duplicate-selection");
            if (/*guard:contiguous-players*/ (!seats.Select(s => s.Player).Order().SequenceEqual(Enumerable.Range(1, seats.Length))))
                return Result(AnalysisStatus.Unsupported, "player-gap");
            if (/*guard:native-unqualified*/ (seats.Any(s => s.Kind == "native")))
                return Result(AnalysisStatus.Unverified, "native-profile-and-capability-unqualified");
            if (/*guard:output-shape*/ (seats.Any(s => string.IsNullOrWhiteSpace(s.OutputId) || s.OutputGeneration <= 0)
                || seats.Select(s => s.OutputId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != seats.Length))
                return Result(AnalysisStatus.Invalid, "output-shape");
            if (/*guard:session-context*/ (string.IsNullOrWhiteSpace(context.Session) || context.HelperGeneration <= 0 || context.TopologyGeneration <= 0
                || string.IsNullOrWhiteSpace(context.ObservationEpoch) || observed == null || observed.Context != context || observed.Outputs == null))
                return Result(AnalysisStatus.Unverified, "observation-context");
            var indices = new List<(int Player, int Index)>();
            var observations = observed!.Outputs.ToArray();
            foreach (var seat in seats.OrderBy(s => s.Player))
            {
                var matches = observations.Where(o => o != null && string.Equals(o.OutputId, seat.OutputId, StringComparison.OrdinalIgnoreCase)).Select(o => o!).ToArray();
                if (/*guard:one-observation*/ (matches.Length != 1)) return Result(AnalysisStatus.Unverified, "missing-or-ambiguous-output");
                var output = matches[0];
                if (/*guard:output-evidence*/ (output.OutputGeneration != seat.OutputGeneration || output.Backend != "xinput" || output.Base != 0
                    || output.Index is < 0 or > 3 || string.IsNullOrWhiteSpace(output.Observer) || string.IsNullOrWhiteSpace(output.Receipt)))
                    return Result(AnalysisStatus.Unverified, "output-observation");
                indices.Add((seat.Player, output.Index));
            }
            if (/*guard:unique-indices*/ (indices.Select(p => p.Index).Distinct().Count() != indices.Count))
                return Result(AnalysisStatus.Unverified, "duplicate-observed-index");
            var plan = AnalyzeConfiguration(launch, indices.OrderBy(p => p.Player).Select(p => p.Index).ToArray(), reader);
            return new(plan.Status, plan.Reason, plan.AppendConfig, Array.AsReadOnly(seats), plan.Inputs, plan.OwnerRoot);
        }
        catch (ConfigException error) { return Result(AnalysisStatus.Unsupported, error.Message); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return Result(AnalysisStatus.Unsupported, "unresolved-config-chain"); }
    }

    // Shared config-chain inspection/emission. Neither caller gains launch authority.
    internal static ConfigurationPlan AnalyzeConfiguration(LaunchContext launch, IReadOnlyList<int> indices, IConfigReader reader)
    {
        var inputs = new List<InputStamp>();
        ConfigurationPlan Result(AnalysisStatus status, string reason, string? text = null) =>
            new(status, reason, text, inputs.AsReadOnly(), launch.Root);
        try
        {
            if (/*guard:launch-identity*/ (!Path.IsPathFullyQualified(launch.Root) || !Path.IsPathFullyQualified(launch.MainConfig)
                || !Path.IsPathFullyQualified(launch.Content) || !Component(launch.CoreName) || launch.Content.IndexOfAny(['#', '|']) >= 0))
                return Result(AnalysisStatus.Unsupported, "unresolved-launch-context");
            string root = Path.GetFullPath(launch.Root), mainPath = Path.GetFullPath(launch.MainConfig);
            if (/*guard:main-location*/ (!Within(root, mainPath))) return Result(AnalysisStatus.Unsupported, "main-config-outside-root");
            Dictionary<string, string> Load(string path, bool required)
            {
                var file = reader.Read(path);
                if (file.State == ReadState.Unreadable || (required && file.State == ReadState.Absent)) throw new ConfigException("unreadable-config-chain");
                if (file.State == ReadState.Absent) { inputs.Add(new(path, false, null)); return []; }
                if (/*guard:read-stamp*/ (file.State != ReadState.Present || file.Text == null || file.RawSha256 == null
                    || file.RawSha256.Length != 64 || !file.RawSha256.All(Uri.IsHexDigit))) throw new ConfigException("unreadable-config-chain");
                inputs.Add(new(path, true, file.RawSha256));
                return Parse(file.Text);
            }
            var main = Load(mainPath, true);
            if (/*guard:xinput-driver*/ (!main.TryGetValue("input_joypad_driver", out string? driver) || driver != "xinput"))
                return Result(AnalysisStatus.Unsupported, "joypad-driver");
            if (/*guard:reservation*/ (Enumerable.Range(1, 4).Any(p => main.TryGetValue($"input_player{p}_device_reservation_type", out var value) && value != "0")))
                return Result(AnalysisStatus.Unsupported, "device-reservation");
            main.TryGetValue("rgui_config_directory", out string? overrides);
            main.TryGetValue("input_remapping_directory", out string? remaps);
            if (/*guard:config-directories*/ (overrides == null || remaps == null))
                return Result(AnalysisStatus.Unsupported, "missing-config-directories");
            string overrideRoot = DirectoryPath(root, overrides!), remapRoot = DirectoryPath(root, remaps!);
            string contentDirectory = new DirectoryInfo(Path.GetDirectoryName(launch.Content)!).Name;
            string gameName = Path.GetFileNameWithoutExtension(launch.Content);
            if (/*guard:chain-components*/ (!Component(contentDirectory) || !Component(gameName)))
                return Result(AnalysisStatus.Unsupported, "unresolved-content-name");
            var names = new[] { launch.CoreName, contentDirectory, gameName }.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var (directory, extension, isRemap) in new[] { (overrideRoot, ".cfg", false), (remapRoot, ".rmp", true) })
            foreach (string name in names)
            {
                var config = Load(Path.Combine(directory, launch.CoreName, name + extension), false);
                if (/*guard:protected-override*/ (config.Keys.Any(key => Protected.Contains(key) || ProtectedPlayer.IsMatch(key))))
                    return Result(AnalysisStatus.Unsupported, "override-sets-protected-key");
                if (!isRemap) continue;
                foreach (var entry in config)
                {
                    var port = Port.Match(entry.Key);
                    if (!port.Success) continue;
                    if (/*guard:remap-port*/ (!int.TryParse(port.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int player)
                        || player is < 1 or > 8 || !int.TryParse(entry.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int target)
                        || target is < 0 or > 7 || (player <= indices.Count && target != player - 1)))
                        return Result(AnalysisStatus.Unsupported, "nonidentity-or-invalid-remap");
                }
            }
            var text = new StringBuilder();
            text.AppendLine($"input_max_users = \"{indices.Count}\""); // control:unjoined-ports
            foreach (var pair in indices.Select((index, offset) => (Player: offset + 1, Index: index))) text.AppendLine($"input_player{pair.Player}_joypad_index = \"{pair.Index}\""); // control:observed-index
            text.AppendLine("config_save_on_exit = \"false\""); // control:config-save
            text.AppendLine("remap_save_on_exit = \"false\""); // control:remap-save
            return Result(AnalysisStatus.Candidate, "candidate-config-not-launch-permission", text.ToString());
        }
        catch (ConfigException error) { return Result(AnalysisStatus.Unsupported, error.Message); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return Result(AnalysisStatus.Unsupported, "unresolved-config-chain"); }
    }

    private static bool Component(string value) => !string.IsNullOrWhiteSpace(value) && value is not ("." or "..")
        && !value.EndsWith('.') && !value.EndsWith(' ') && !Regex.IsMatch(value, "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && value.IndexOfAny(['/', '\\', '\r', '\n']) < 0;
    private static bool Within(string root, string path) => path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static string DirectoryPath(string root, string value)
    {
        if (/*guard:directory-syntax*/ (string.IsNullOrWhiteSpace(value) || value.IndexOfAny(['%', '$', '~', '|', '#']) >= 0
            || value.Replace('\\', '/').Split('/').Contains(".."))) throw new ConfigException("unresolved-config-location");
        string path = value.StartsWith(":\\", StringComparison.Ordinal) || value.StartsWith(":/", StringComparison.Ordinal)
            ? Path.Combine(root, value[2..]) : Path.IsPathFullyQualified(value) ? value : Path.Combine(root, value);
        path = Path.GetFullPath(path);
        if (/*guard:directory-scope*/ (!Within(root, path))) throw new ConfigException("config-location-outside-root");
        return path;
    }
    internal static Dictionary<string, string> Parse(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in text.TrimStart('\uFEFF').Replace("\r\n", "\n").Split('\n'))
        {
            string trimmed = line.Trim();
            if (/*guard:include*/ (Include.IsMatch(trimmed))) throw new ConfigException("unresolved-include");
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            var assignment = Assignment.Match(trimmed);
            if (/*guard:assignment-syntax*/ (!assignment.Success)) throw new ConfigException("config-syntax");
            string key = assignment.Groups[1].Value, rest = assignment.Groups[2].Value.Trim();
            string value;
            if (rest.StartsWith('"'))
            {
                int close = rest.IndexOf('"', 1);
                if (/*guard:quoted-value*/ (close < 0 || (rest[(close + 1)..].Trim().Length != 0 && !rest[(close + 1)..].TrimStart().StartsWith('#'))))
                    throw new ConfigException("config-syntax");
                value = rest[1..close]; // deliberately no escape/environment interpretation
            }
            else value = rest.Split('#', 2)[0].Trim();
            if (/*guard:unique-key*/ (values.ContainsKey(key))) throw new ConfigException("duplicate-config-key");
            values[key] = value;
        }
        return values;
    }
    private sealed class ConfigException(string reason) : Exception(reason);
}

internal sealed record ConfigurationPlan(AnalysisStatus Status, string Reason, string? AppendConfig, IReadOnlyList<InputStamp> Inputs, string OwnerRoot);
