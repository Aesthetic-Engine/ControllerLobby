using System.Text.RegularExpressions;

namespace ControllerLobby.DuckStation;

// Pure rules for the DuckStation launch copy. Nothing here reads or writes a disk: callers pass the text of the
// owner's files and receive the text of the session settings. Qualified against DuckStation 0.1-9903-g8f0c9dd17.
public enum Mappable { Yes, No }
public sealed record Qualification(Mappable Status, string Reason, IReadOnlyList<int> PadSlots)
{
    public bool Ok => Status == Mappable.Yes;
    public int Ports => PadSlots.Count;
    internal static Qualification Refuse(string reason) => new(Mappable.No, reason, Array.Empty<int>());
}
public enum FileState { Absent, Present, Unreadable }
public sealed record OwnerFile(string Name, FileState State, string? Text = null);

public static class DuckStationSession
{
    public const string SessionGameSettings = "session\\gamesettings", SessionInputProfiles = "session\\inputprofiles";
    private static readonly Regex Section = new("^\\[(.+)\\]\\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex XInputBinding = new("^XInput-([0-3])/[^/]+$", RegexOptions.CultureInvariant);
    private static readonly Regex PathLike = new("(Path|Root|Directory|Dir|File|Image|Folder)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex Scalar = new("^(true|false|-?[0-9]+(\\.[0-9]+)?)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex Rooted = new("^([A-Za-z]:|[\\\\/]|\\.\\.?[\\\\/])", RegexOptions.CultureInvariant);
    // DuckStation reads its INI files case-insensitively (CSimpleIniA). The adapter compares names exactly, so a name
    // that differs from these only in case, a repeated key, or a split section is refused rather than interpreted.
    // SimpleIni skips leading whitespace and trims names, so "  [Folders]" and "[ Folders ]" are sections to DuckStation.
    // The adapter reads only these exact line forms and refuses every other non-blank line: a header "[Name]", a
    // "Key = value" line starting in the first column (not a "<<<" multi-line value), a comment, or a blank line.
    private static readonly Regex StrictLine = new("^(\\s*|[;#].*|\\[[A-Za-z0-9_]+\\]|[A-Za-z0-9_.]+[ \\t]*=(?![ \\t]*<<<).*)$", RegexOptions.CultureInvariant);
    private static readonly string[] StrictSections = ["InputSources", "ControllerPorts", "Pad1", "Pad2", "Pad3", "Pad4", "Pad5", "Pad6", "Pad7", "Pad8", "Folders", "MemoryCards", "BIOS", "AutoUpdater", "PIO", "PCDrv"];
    private static readonly string[] CanonicalSections = [.. StrictSections, "GameList", "Hotkeys", "Logging"];
    private static readonly string[] CanonicalKeys = ["XInput", "MultitapMode", "Type", "SearchDirectory", "Directory", "Card1Path", "Card2Path", "CheckAtStartup", "GameSettings", "InputProfiles",
        "FlashImagePath", "Root", "Paths", "RecursivePaths", "ExcludedPaths", "UseGameSettingsForController", "InputProfileName",
        "Cache", "Cheats", "Covers", "GameIcons", "Patches", "SaveStates", "Screenshots", "Shaders", "Subchannels", "Textures", "UserResources", "Videos"];
    private static readonly HashSet<string> LoggingScalars = new(StringComparer.Ordinal) { "LogLevel", "LogTimestamps", "LogToConsole", "LogToDebug", "LogToWindow", "LogToFile", "LogFileTimestamps" };
    // [Folders] keys resolved against the data root (settings.cpp EmuFolders::LoadConfig at 8f0c9dd17), shared with the owner.
    private static readonly Dictionary<string, string> FolderDefaults = new(StringComparer.Ordinal)
    {
        ["Cache"] = "cache", ["Cheats"] = "cheats", ["Covers"] = "covers", ["GameIcons"] = "gameicons", ["Patches"] = "patches",
        ["SaveStates"] = "savestates", ["Screenshots"] = "screenshots", ["Shaders"] = "shaders", ["Subchannels"] = "subchannels",
        ["Textures"] = "textures", ["UserResources"] = "resources", ["Videos"] = "videos",
    };
    private static readonly HashSet<string> MustBeEmpty = new(StringComparer.Ordinal) { "PIO/FlashImagePath", "PCDrv/Root", "GameList/Paths", "GameList/RecursivePaths", "GameList/ExcludedPaths" };
    private static readonly HashSet<string> KnownPaths = new(StringComparer.Ordinal) { "BIOS/SearchDirectory", "MemoryCards/Directory", "MemoryCards/Card1Path", "MemoryCards/Card2Path", "Folders/GameSettings", "Folders/InputProfiles" };

    private sealed record Line(string Raw, string? SectionName, string? Key, string? Value);

    private static List<Line> Read(string text)
    {
        var lines = new List<Line>(); string? section = null;
        // SimpleIni ends a line at LF or at a lone CR, so both are line breaks here too (per-game scan). The global
        // settings file is refused outright when a lone CR remains (LoneCarriageReturn), because Build keeps raw lines.
        foreach (string raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var m = Section.Match(raw);
            if (m.Success) { section = m.Groups[1].Value; lines.Add(new(raw, section, null, null)); continue; }
            int eq = raw.IndexOf('=');
            if (eq > 0 && !raw.TrimStart().StartsWith(';') && !raw.TrimStart().StartsWith('#'))
                lines.Add(new(raw, section, raw[..eq].Trim(), raw[(eq + 1)..].Trim()));
            else lines.Add(new(raw, section, null, null));
        }
        return lines;
    }

    private static string? Get(List<Line> lines, string section, string key) =>
        lines.LastOrDefault(l => l.SectionName == section && l.Key == key)?.Value;

    // Data root as ComputeDataDirectory does (host.cpp at 8f0c9dd17): the emulator folder when it holds portable.txt
    // or settings.ini, else <Documents>\DuckStation. No Documents folder is refused rather than reproduced.
    public static string? DataRoot(string emulatorDirectory, Func<string, bool> fileExists, Func<string?> documents)
    {
        if (/*guard:dataroot-markers*/fileExists(Path.Combine(emulatorDirectory, "portable.txt")) || fileExists(Path.Combine(emulatorDirectory, "settings.ini")))
            return emulatorDirectory;
        string? docs = documents();
        return string.IsNullOrEmpty(docs) ? null : Path.Combine(docs, "DuckStation");
    }

    // The owner's effective [Folders] directory for a key such as GameSettings: as written when absolute, joined to
    // the data root when relative, the default name when empty or missing. Null when the key is not a folder key.
    public static string? OwnerFolder(string ownerSettings, string dataRoot, string key)
    {
        string? fallback = key switch { "GameSettings" => "gamesettings", "InputProfiles" => "inputprofiles", _ => FolderDefaults.GetValueOrDefault(key) };
        if (fallback == null) return null;
        string? value = Get(Read(ownerSettings), "Folders", key);
        string v = string.IsNullOrEmpty(value) ? fallback : value;
        return Path.GetFullPath(Path.IsPathFullyQualified(v) ? v : Path.Combine(dataRoot, v));
    }

    private static bool LoneCarriageReturn(string text) => text.Replace("\r\n", "\n").Contains('\r');

    // An override is active only with UseGameSettingsForController = true or a non-empty InputProfileName (system.cpp
    // at 8f0c9dd17; gate D-5). A dormant [PadN] section or an explicit false flag is not one. Names are compared
    // without case and in any section, as DuckStation's reader would find them.
    public static bool HasActiveControllerOverride(string perGameText)
    {
        var lines = Read(perGameText);
        return lines.Any(l => string.Equals(l.Key, "UseGameSettingsForController", StringComparison.OrdinalIgnoreCase) && /*guard:override-any-case*/!string.Equals(l.Value, "false", StringComparison.OrdinalIgnoreCase))
            || lines.Any(l => string.Equals(l.Key, "InputProfileName", StringComparison.OrdinalIgnoreCase) && /*guard:profile-override*/!string.IsNullOrEmpty(l.Value));
    }

    // True when the file is outside the grammar the adapter interprets exactly.
    private static bool Ambiguous(List<Line> lines)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var closed = new HashSet<string>(StringComparer.OrdinalIgnoreCase); string? run = null;
        foreach (var l in lines)
        {
            if (/*guard:line-grammar*/!StrictLine.IsMatch(l.Raw)) return true; // indentation, "[ Name ]", multi-line values, stray text
            if (l.SectionName == null) { if (l.Key != null) return true; continue; } // a setting before any section
            string? canonical = CanonicalSections.FirstOrDefault(s => s.Equals(l.SectionName, StringComparison.OrdinalIgnoreCase));
            if (/*guard:section-case*/canonical != null && canonical != l.SectionName) return true;
            bool strict = canonical != null && StrictSections.Contains(canonical);
            if (l.Key == null)
            {
                if (Section.IsMatch(l.Raw)) { if (run != null) closed.Add(run); run = l.SectionName; if (strict && /*guard:split-section*/closed.Contains(run)) return true; }
                continue;
            }
            string? key = CanonicalKeys.FirstOrDefault(k => k.Equals(l.Key, StringComparison.OrdinalIgnoreCase));
            if (/*guard:key-case*/key != null && key != l.Key) return true;
            if (strict && /*guard:duplicate-key*/!keys.Add(l.SectionName + "/" + l.Key)) return true;
            if (strict && l.Key == "Type" && l.Value != "None" && string.Equals(l.Value, "None", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static Qualification Qualify(OwnerFile settings, IEnumerable<OwnerFile> perGameSettings, OwnerFile customProperties)
    {
        if (settings.State != FileState.Present || settings.Text == null) return Qualification.Refuse("settings-unreadable");
        if (/*guard:custom-properties*/customProperties.State == FileState.Unreadable || customProperties.State == FileState.Present && !string.IsNullOrWhiteSpace(customProperties.Text))
            return Qualification.Refuse("custom-titles");
        var lines = Read(settings.Text);
        if (/*guard:lone-cr*/LoneCarriageReturn(settings.Text) || Ambiguous(lines)) return Qualification.Refuse("ambiguous-setting");
        if (!lines.Any(l => l.SectionName == "Pad1")) return Qualification.Refuse("settings-unreadable");
        if (!string.Equals(Get(lines, "InputSources", "XInput"), "true", StringComparison.OrdinalIgnoreCase)) return Qualification.Refuse("xinput-off");
        if (/*guard:multitap*/!string.Equals(Get(lines, "ControllerPorts", "MultitapMode") ?? "Disabled", "Disabled", StringComparison.Ordinal)) return Qualification.Refuse("multitap");
        for (int pad = 3; pad <= 8; pad++)
            if (Active(lines, pad)) return Qualification.Refuse("multitap");
        if (/*guard:pad1-required*/!Active(lines, 1)) return Qualification.Refuse("pad1-inactive");
        var slots = new List<int>();
        for (int pad = 1; pad <= 2; pad++)
        {
            if (!Active(lines, pad)) continue;
            int? slot = PadSlot(lines, pad);
            if (slot == null) return Qualification.Refuse("pad-not-single-xinput");
            if (/*guard:distinct-slots*/slots.Contains(slot.Value)) return Qualification.Refuse("pads-share-slot");
            slots.Add(slot.Value);
        }
        foreach (var l in lines.Where(l => l.Key != null && l.SectionName != null))
        {
            string id = l.SectionName + "/" + l.Key;
            if (MustBeEmpty.Contains(id) && !string.IsNullOrEmpty(l.Value)) return Qualification.Refuse("unsupported-path:" + id);
            bool known = KnownPaths.Contains(id) || l.SectionName == "Folders" && FolderDefaults.ContainsKey(l.Key!) || MustBeEmpty.Contains(id);
            if (/*guard:unknown-path-fails-closed*/!known && CouldBePath(l)) return Qualification.Refuse("unsupported-path:" + id);
        }
        foreach (var file in perGameSettings)
        {
            if (file.State == FileState.Unreadable) return Qualification.Refuse("per-game-unreadable");
            if (file.Text != null && HasActiveControllerOverride(file.Text)) return Qualification.Refuse("per-game-controller-override");
        }
        return new(Mappable.Yes, "", slots.AsReadOnly());
    }

    // A setting the contract table does not name is carried through unchanged only when it cannot be a path: a
    // controller binding in a pad or hotkey section, a known logging switch, or a plain scalar under a name that does
    // not read like a path. This is a filter, not proof that every unknown setting is safe (contract gate D-2).
    private static bool CouldBePath(Line l)
    {
        string value = l.Value!;
        if (value.Length == 0) return false;
        bool input = l.SectionName!.StartsWith("Pad", StringComparison.Ordinal) && l.SectionName.Length == 4 || l.SectionName == "Hotkeys";
        if (input && value.Split('&', StringSplitOptions.TrimEntries).All(part => BindingToken.IsMatch(part) || Scalar.IsMatch(part) || Word.IsMatch(part))) return false;
        if (l.SectionName == "Logging" && (LoggingScalars.Contains(l.Key!) || Scalar.IsMatch(value))) return false;
        if (value.Contains('\\') || Rooted.IsMatch(value)) return true;
        return PathLike.IsMatch(l.Key!) && !Scalar.IsMatch(value);
    }
    private static readonly Regex BindingToken = new("^(XInput-[0-3]|SDL-[0-9]{1,2}|DInput-[0-9]{1,2}|RawInput-[0-9]{1,2}|Keyboard|Pointer-[0-9]{1,2}|Sensor)/[^/\\\\]+$", RegexOptions.CultureInvariant);
    private static readonly Regex Word = new("^[A-Za-z][A-Za-z0-9]*$", RegexOptions.CultureInvariant);

    private static bool Active(List<Line> lines, int pad) { string? type = Get(lines, "Pad" + pad, "Type"); return type != null && type != "None"; }

    // Every binding in the pad section must be on one XInput index: the whole section moves with its controller.
    // A value is a binding when it contains a slash; each one must be exactly XInput-<0..3>/<name>, which is also the
    // only form Rewrite changes, so every accepted binding is rewritable.
    private static int? PadSlot(List<Line> lines, int pad)
    {
        int? slot = null;
        foreach (var l in lines.Where(l => l.SectionName == "Pad" + pad && l.Key != null))
            foreach (string part in l.Value!.Split('&', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (!part.Contains('/')) continue; // a plain value such as Type or a deadzone
                var m = XInputBinding.Match(part);
                if (/*guard:xinput-only*/!m.Success) return null;
                int index = m.Groups[1].Value[0] - '0';
                if (slot != null && slot != index) return null;
                slot = index;
            }
        return slot;
    }

    // The session settings: the owner's text with only the pad indices, the path rows of the contract table and the
    // updater flag changed. joined = XInput slots in join order. Throws when the request is outside the qualified shape.
    public static string Build(string ownerSettings, Qualification qualification, IReadOnlyList<int> joined, string ownerDataRoot, string copyRoot)
    {
        if (!qualification.Ok) throw new InvalidOperationException("not mappable: " + qualification.Reason);
        if (joined.Count == 0 || /*guard:players-within-ports*/joined.Count > qualification.Ports) throw new InvalidOperationException("players outside mappable ports");
        if (joined.Distinct().Count() != joined.Count || joined.Any(s => s < 0 || s > 3)) throw new InvalidOperationException("invalid joined slots");
        if (!Path.IsPathFullyQualified(ownerDataRoot) || !Path.IsPathFullyQualified(copyRoot)) throw new ArgumentException("absolute roots required");
        var lines = Read(ownerSettings); var output = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        string? current = null;
        void CloseSection()
        {
            // Keys the owner's file leaves at their default still need an explicit absolute value in the copy.
            if (current == "Folders")
            {
                foreach (var pair in FolderDefaults) if (seen.Add("Folders/" + pair.Key)) output.Add(pair.Key + " = " + Path.Combine(ownerDataRoot, pair.Value));
                if (seen.Add("Folders/GameSettings")) output.Add("GameSettings = " + Path.Combine(copyRoot, SessionGameSettings));
                if (seen.Add("Folders/InputProfiles")) output.Add("InputProfiles = " + Path.Combine(copyRoot, SessionInputProfiles));
            }
            if (current == "MemoryCards" && seen.Add("MemoryCards/Directory")) output.Add("Directory = " + Path.Combine(ownerDataRoot, "memcards"));
            if (current == "BIOS" && seen.Add("BIOS/SearchDirectory")) output.Add("SearchDirectory = " + Path.Combine(ownerDataRoot, "bios"));
            if (current == "AutoUpdater" && seen.Add("AutoUpdater/CheckAtStartup")) output.Add("CheckAtStartup = false");
        }
        var sections = new HashSet<string>(StringComparer.Ordinal);
        int trailing = lines.Count; while (trailing > 0 && lines[trailing - 1].Raw.Length == 0 && lines[trailing - 1].Key == null) trailing--;
        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            if (l.SectionName != current || l.Key == null && Section.IsMatch(l.Raw)) { if (Section.IsMatch(l.Raw)) { TrimBlank(output, out var blanks); CloseSection(); output.AddRange(blanks); current = l.SectionName; sections.Add(current!); output.Add(l.Raw); continue; } }
            if (l.Key == null) { output.Add(l.Raw); continue; }
            string id = l.SectionName + "/" + l.Key; seen.Add(id);
            output.Add(Rewrite(l, id, qualification, joined, ownerDataRoot, copyRoot));
        }
        TrimBlank(output, out var tail); CloseSection(); output.AddRange(tail);
        foreach (var (name, body) in new[] { ("Folders", ""), ("MemoryCards", ""), ("BIOS", ""), ("AutoUpdater", "") })
            if (!sections.Contains(name)) { output.Add("[" + name + "]"); current = name; CloseSection(); output.Add(body); }
        return string.Join(ownerSettings.Contains("\r\n") ? "\r\n" : "\n", output);
    }

    private static void TrimBlank(List<string> output, out List<string> blanks)
    {
        blanks = new();
        while (output.Count > 0 && output[^1].Length == 0) { blanks.Add(""); output.RemoveAt(output.Count - 1); }
    }

    private static string Rewrite(Line l, string id, Qualification q, IReadOnlyList<int> joined, string ownerDataRoot, string copyRoot)
    {
        string Absolute(string value, string fallback) { string v = value.Length == 0 ? fallback : value; return Path.GetFullPath(Path.IsPathFullyQualified(v) ? v : Path.Combine(ownerDataRoot, v)); }
        if (l.SectionName == "Folders" && FolderDefaults.TryGetValue(l.Key!, out string? def)) return l.Key + " = " + Absolute(l.Value!, def);
        if (id == "Folders/GameSettings") return "GameSettings = " + /*guard:private-game-settings*/Path.Combine(copyRoot, SessionGameSettings);
        if (id == "Folders/InputProfiles") return "InputProfiles = " + Path.Combine(copyRoot, SessionInputProfiles);
        if (id == "MemoryCards/Directory") return "Directory = " + Absolute(l.Value!, "memcards");
        if (id == "BIOS/SearchDirectory") return "SearchDirectory = " + Absolute(l.Value!, "bios");
        if (id == "AutoUpdater/CheckAtStartup") return "CheckAtStartup = false";
        // Card1Path/Card2Path stay as written: DuckStation resolves a relative one against the memory-card directory.
        if (l.SectionName is "Pad1" or "Pad2")
        {
            int pad = l.SectionName[3] - '0', port = pad - 1;
            if (pad == 2 && q.Ports < 2) return l.Raw;
            if (/*guard:unused-port-inactive*/port >= joined.Count) return l.Key == "Type" ? "Type = None" : l.Raw;
            int from = q.PadSlots[port], to = joined[port];
            return l.Raw.Replace("XInput-" + from + "/", /*guard:join-order-index*/"XInput-" + to + "/", StringComparison.Ordinal);
        }
        return l.Raw;
    }

    // Program inventory: an explicit allow-list of the emulator folder, never the folder itself (contract section 2).
    public static bool IsProgramFile(string relativePath)
    {
        string path = relativePath.Replace('/', '\\');
        string[] parts = path.Split('\\');
        if (parts.Any(p => p is "" or "." or "..")) return false;
        if (parts.Length == 1)
            return /*guard:inventory-allow-list*/path.Equals("duckstation-qt-x64-ReleaseLTCG.exe", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || path.Equals("qt.conf", StringComparison.OrdinalIgnoreCase)
                || path.Equals("LICENSE.txt", StringComparison.OrdinalIgnoreCase);
        return parts[0].Equals("QtPlugins", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("resources", StringComparison.OrdinalIgnoreCase)
            || parts[0].Equals("translations", StringComparison.OrdinalIgnoreCase);
    }

    // A source that keeps its data in the emulator folder may name a data folder that is, or sits inside, a program
    // directory, or is the emulator folder itself; program files and user data then cannot be told apart, and Setup
    // refuses the entry. Effective paths are resolved as DuckStation does: an empty or missing value is the default.
    public static bool HasProgramFolderCollision(string ownerSettings, string emulatorDirectory)
    {
        var lines = Read(ownerSettings);
        if (LoneCarriageReturn(ownerSettings) || Ambiguous(lines)) return true;
        string root = Path.GetFullPath(emulatorDirectory).TrimEnd('\\');
        var effective = new List<string>();
        foreach (var pair in FolderDefaults.Concat([new("GameSettings", "gamesettings"), new("InputProfiles", "inputprofiles")])) effective.Add(Effective(Get(lines, "Folders", pair.Key), pair.Value));
        effective.Add(Effective(Get(lines, "MemoryCards", "Directory"), "memcards"));
        string Effective(string? value, string fallback) { string v = string.IsNullOrEmpty(value) ? fallback : value; return Path.GetFullPath(Path.IsPathFullyQualified(v) ? v : Path.Combine(root, v)).TrimEnd('\\'); }
        bool Within(string path, string parent) => path.Equals(parent, StringComparison.OrdinalIgnoreCase) || path.StartsWith(parent + "\\", StringComparison.OrdinalIgnoreCase);
        return effective.Any(path => /*guard:collision-resolved*/path.Equals(root, StringComparison.OrdinalIgnoreCase)
            || new[] { "QtPlugins", "resources", "translations" }.Any(program => Within(path, Path.Combine(root, program))));
    }
}
