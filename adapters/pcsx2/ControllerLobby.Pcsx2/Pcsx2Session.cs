using System.Text;
using System.Text.RegularExpressions;

namespace ControllerLobby.Pcsx2;

// Pure rules for the PCSX2 launch copy (PCSX2 adapter contract r4). Nothing here reads or writes
// a disk: callers pass the bytes of the owner's files and receive the text of the session settings. Qualified against
// PCSX2 v2.7.182; source references are to that tag.
public sealed record Pcsx2Qualification(bool Ok, string Reason)
{
    internal static Pcsx2Qualification Yes { get; } = new(true, "");
    internal static Pcsx2Qualification No(string reason) => new(false, reason);
}

/// <summary>One file of the owner's game-settings folder. Bytes is null when it could not be read. RegularFile is false
/// for anything that is not a plain file, including a link or reparse point.</summary>
public sealed record PerGameFile(string Name, byte[]? Bytes, bool RegularFile = true);

public static class Pcsx2Session
{
    public const string PinnedSha256 = "6143327B5A6727FA30718DE65925EB4ED4350B78702F0D5EB2087B762216A08E";
    public const string Session = "session";
    private const string MultitapOn = "MultitapPort1 = true";

    // EmuFolders::LoadConfig (Pcsx2Config.cpp 2283-2300): the twelve shared folders and their source defaults.
    private static readonly (string Key, string Default)[] SharedFolders =
    [
        ("Bios", "bios"), ("Snapshots", "snaps"), ("Savestates", "sstates"), ("MemoryCards", "memcards"), ("Logs", "logs"),
        ("Cheats", "cheats"), ("Patches", "patches"), ("Covers", "covers"), ("UserResources", "resources"), ("Cache", "cache"),
        ("Textures", "textures"), ("Videos", "videos"),
    ];
    // The four private folders: per-game settings, input profiles and the two debugger folders (contract 3.3).
    private static readonly (string Key, string Folder)[] PrivateFolders =
    [
        ("GameSettings", "gamesettings"), ("InputProfiles", "inputprofiles"), ("DebuggerLayouts", "debuggerlayouts"), ("DebuggerSettings", "debuggersettings"),
    ];
    public static IReadOnlyList<string> PrivateFolderNames { get; } = PrivateFolders.Select(f => Path.Combine(Session, f.Folder)).ToArray();

    // Pad keys kept as the owner set them (contract 3.4); every other key in a rebuilt pad section is removed.
    private static readonly string[] KeptPadKeys = ["InvertL", "InvertR", "Deadzone", "AxisScale", "LargeMotorScale", "SmallMotorScale", "ButtonDeadzone", "PressureModifier"];

    // The layout (contract section 4, the owner's choice): PCSX2's automatic XInput mapping, XInputSource.cpp 108-124
    // and 430-466; pad keys from PadDualshock2.cpp 17-44.
    public static IReadOnlyList<(string Key, string Input)> Layout { get; } =
    [
        ("Up", "DPadUp"), ("Right", "DPadRight"), ("Down", "DPadDown"), ("Left", "DPadLeft"),
        ("Triangle", "Y"), ("Circle", "B"), ("Cross", "A"), ("Square", "X"), ("Select", "Back"), ("Start", "Start"),
        ("L1", "LeftShoulder"), ("L2", "+LeftTrigger"), ("R1", "RightShoulder"), ("R2", "+RightTrigger"),
        ("L3", "LeftStick"), ("R3", "RightStick"), ("Analog", "Guide"),
        ("LUp", "-LeftY"), ("LRight", "+LeftX"), ("LDown", "+LeftY"), ("LLeft", "-LeftX"),
        ("RUp", "-RightY"), ("RRight", "+RightX"), ("RDown", "+RightY"), ("RLeft", "-RightX"),
        ("LargeMotor", "LargeMotor"), ("SmallMotor", "SmallMotor"),
    ];

    // The section and key names the adapter interprets, in the spelling PCSX2 writes. The owner's file must use exactly
    // these: a name that differs only in letter case refuses, so qualification and Build never disagree about which
    // line is meant. Names outside these sections are not interpreted and are copied as they are.
    private static readonly string[] PadKeys = ["Type", "MultitapPort1", "MultitapPort2", .. KeptPadKeys, .. Layout.Select(l => l.Key)];
    private static readonly Dictionary<string, string[]> CanonicalNames = new(StringComparer.Ordinal)
    {
        ["Pad"] = PadKeys, ["Pad1"] = PadKeys, ["Pad2"] = PadKeys, ["Pad3"] = PadKeys, ["Pad4"] = PadKeys, ["Pad5"] = PadKeys,
        ["Pad6"] = PadKeys, ["Pad7"] = PadKeys, ["Pad8"] = PadKeys, ["USB1"] = ["Type"], ["USB2"] = ["Type"], ["Hotkeys"] = [],
        ["Folders"] = [.. SharedFolders.Select(f => f.Key), .. PrivateFolders.Select(f => f.Key)], ["InputSources"] = ["XInput"],
    };
    private static bool NonCanonical(string name, IEnumerable<string> canonical) =>
        canonical.Any(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase) && !string.Equals(c, name, StringComparison.Ordinal));

    // The exact line forms the adapter interprets; any other line refuses. PCSX2 writes "Key = value".
    private static readonly Regex LineForm = new(@"^(|[;#].*|\[[A-Za-z0-9_/]+\]|[A-Za-z0-9_.]+ =( .*)?)\z", RegexOptions.CultureInvariant);
    private static readonly Regex Header = new(@"^\[([A-Za-z0-9_/]+)\]\z", RegexOptions.CultureInvariant);
    private static readonly Regex HotkeyToken = new(@"^(Keyboard|Pointer-[0-9]{1,2})/[^/&]+\z", RegexOptions.CultureInvariant);
    private static readonly Regex PerGameInputSection = new(@"^(Pad[0-9]*|USB[0-9]+|InputSources|Hotkeys)\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private sealed record Line(string Raw, string? Section, string? Key, string? Value);
    private sealed record Parsed(List<Line> Lines, string Newline, bool FinalNewline);

    // Null when the text is outside the grammar: a lone CR, mixed line endings, a line of another form, a setting
    // before any section, or a section or key repeated (names compared without case, as SimpleIni would). With
    // canonical, also a name of an interpreted section or key spelled with other letter case (the owner's PCSX2.ini;
    // per-game files refuse every input section without case instead).
    private static Parsed? Parse(string text, out string reason, bool canonical = false)
    {
        reason = "";
        bool crlf = text.Contains("\r\n");
        string unified = text.Replace("\r\n", "\n");
        // A lone CR, or CRLF mixed with bare LF, refuses: Build writes one line ending throughout.
        if (/*guard:pcsx2-line-endings*/unified.Contains('\r') || crlf && CountBareLf(text) > 0) { reason = "line-endings"; return null; }
        bool final = unified.EndsWith('\n');
        var raws = (final ? unified[..^1] : unified).Split('\n');
        var lines = new List<Line>(); string? section = null;
        var sections = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in raws)
        {
            if (/*guard:pcsx2-line-grammar*/!LineForm.IsMatch(raw)) { reason = "outside-grammar"; return null; }
            var h = Header.Match(raw);
            if (h.Success)
            {
                section = h.Groups[1].Value;
                if (/*guard:pcsx2-repeated-section*/!sections.Add(section)) { reason = "repeated-section"; return null; }
                if (canonical && /*guard:pcsx2-canonical-section*/NonCanonical(section, CanonicalNames.Keys)) { reason = "noncanonical-name:[" + section + "]"; return null; }
                lines.Add(new(raw, section, null, null)); continue;
            }
            if (raw.Length == 0 || raw[0] is ';' or '#') { lines.Add(new(raw, section, null, null)); continue; }
            // SimpleIni trims tabs as whitespace. Refuse them in settings rather than resolve a different value.
            if (/*guard:pcsx2-setting-tab*/raw.Contains('\t')) { reason = "tab-in-setting"; return null; }
            int eq = raw.IndexOf(" =", StringComparison.Ordinal);
            if (section == null) { reason = "outside-grammar"; return null; }
            string key = raw[..eq], value = raw[(eq + 2)..].Trim(' ');
            if (/*guard:pcsx2-repeated-key*/!keys.Add(section + "/" + key)) { reason = "repeated-key"; return null; }
            if (canonical && CanonicalNames.TryGetValue(section, out var known) && /*guard:pcsx2-canonical-key*/NonCanonical(key, known)) { reason = "noncanonical-name:" + section + "/" + key; return null; }
            lines.Add(new(raw, section, key, value));
        }
        return new(lines, crlf ? "\r\n" : "\n", final);
    }

    private static int CountBareLf(string text) { int n = 0; for (int i = 0; i < text.Length; i++) if (text[i] == '\n' && (i == 0 || text[i - 1] != '\r')) n++; return n; }

    private static string? Get(Parsed p, string section, string key) =>
        p.Lines.FirstOrDefault(l => string.Equals(l.Section, section, StringComparison.OrdinalIgnoreCase) && string.Equals(l.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;
    private static bool HasSection(Parsed p, string section) => p.Lines.Any(l => l.Key == null && string.Equals(l.Section, section, StringComparison.OrdinalIgnoreCase) && Header.IsMatch(l.Raw));
    private static bool HasInputProfile(Parsed p) => p.Lines.Any(l => string.Equals(l.Key, "InputProfileName", StringComparison.OrdinalIgnoreCase) && /*guard:pcsx2-input-profile*/!string.IsNullOrEmpty(l.Value));

    /// <summary>Contract 3.1. The owner's PCSX2.ini and every file of his game-settings folder, as read.</summary>
    public static Pcsx2Qualification Qualify(string settings, IReadOnlyList<PerGameFile> perGame)
    {
        var p = Parse(settings, out string why, canonical: true);
        if (p == null) return Pcsx2Qualification.No(why);
        if (HasInputProfile(p)) return Pcsx2Qualification.No("input-profile");
        for (int pad = 1; pad <= 2; pad++)
        {
            string? type = Get(p, "Pad" + pad, "Type");
            if (/*guard:pcsx2-pad-type*/type is not ("DualShock2" or "None")) return Pcsx2Qualification.No("pad-type:Pad" + pad);
        }
        for (int pad = 3; pad <= 8; pad++)
            if (HasSection(p, "Pad" + pad) && /*guard:pcsx2-extra-pad*/Get(p, "Pad" + pad, "Type") != "None") return Pcsx2Qualification.No("pad-type:Pad" + pad);
        foreach (string key in new[] { "MultitapPort1", "MultitapPort2" })
            if (/*guard:pcsx2-multitap*/(Get(p, "Pad", key) ?? "false") != "false") return Pcsx2Qualification.No("multitap");
        foreach (string usb in new[] { "USB1", "USB2" })
            if (/*guard:pcsx2-usb*/(Get(p, usb, "Type") ?? "None") != "None") return Pcsx2Qualification.No("usb-device");
        foreach (var l in p.Lines.Where(l => l.Key != null && string.Equals(l.Section, "Hotkeys", StringComparison.OrdinalIgnoreCase) && l.Value!.Length > 0))
            if (/*guard:pcsx2-hotkey-tokens*/!l.Value!.Split('&').All(t => HotkeyToken.IsMatch(t.Trim(' '))))
                return Pcsx2Qualification.No("controller-hotkey");
        // An empty folder value would resolve to the data root itself (LoadPathFromSettings, Pcsx2Config.cpp 2264-2270).
        foreach (var l in p.Lines.Where(l => l.Key != null && string.Equals(l.Section, "Folders", StringComparison.OrdinalIgnoreCase)))
            if (/*guard:pcsx2-empty-folder*/l.Value!.Length == 0) return Pcsx2Qualification.No("folder-empty:" + l.Key);
        foreach (var f in perGame)
        {
            if (/*guard:pcsx2-per-game-regular*/!f.RegularFile) return Pcsx2Qualification.No("per-game-not-regular");
            if (f.Bytes == null) return Pcsx2Qualification.No("per-game-unreadable");
            string text;
            try { text = new UTF8Encoding(false, true).GetString(f.Bytes); } catch (DecoderFallbackException) { return Pcsx2Qualification.No("per-game-unreadable"); }
            var g = Parse(text, out string gwhy);
            if (g == null) return Pcsx2Qualification.No("per-game-" + gwhy);
            if (g.Lines.Any(l => l.Key == null && l.Section != null && Header.IsMatch(l.Raw) && /*guard:pcsx2-per-game-input*/PerGameInputSection.IsMatch(l.Section)))
                return Pcsx2Qualification.No("per-game-input");
            if (HasInputProfile(g)) return Pcsx2Qualification.No("input-profile");
        }
        return Pcsx2Qualification.Yes;
    }

    /// <summary>The owner's effective game-settings folder, as LoadPathFromSettings resolves it; null when the settings
    /// are outside the grammar.</summary>
    public static string? OwnerGameSettings(string settings, string ownerRoot)
    {
        var p = Parse(settings, out _, canonical: true);
        return p == null ? null : SharedFolder(Get(p, "Folders", "GameSettings"), "gamesettings", ownerRoot);
    }

    /// <summary>Contract 3.5: exactly "-fullscreen -nogui" and one game path, as LaunchBox passes them.</summary>
    public static bool ArgumentsQualified(IReadOnlyList<string> args) =>
        args.Count == 3 && args[0] == "-fullscreen" && args[1] == "-nogui" && args[2].Length > 0 && /*guard:pcsx2-args*/!args[2].StartsWith('-');

    private static string SharedFolder(string? value, string fallback, string ownerRoot)
    {
        string v = value ?? fallback;
        return Path.IsPathFullyQualified(v) ? v : Path.GetFullPath(Path.Combine(ownerRoot, v));
    }

    /// <summary>The pad section each player takes (contract r4 3.2), in join order: ports 1A and 2A for one or two
    /// players; with three or four, port 1's multitap slots 1A-1D, PCSX2 pad indices 0, 2, 3, 4
    /// (sioConvertPortAndSlotToPad, SIO/Sio.cpp) and so sections Pad1, Pad3, Pad4, Pad5 (Pad::GetConfigSection).</summary>
    public static IReadOnlyList<int> PlayerSections(int players) =>
        /*guard:pcsx2-multitap-threshold*/players > 2 ? /*guard:pcsx2-multitap-slots*/[1, 3, 4, 5] : [1, 2];

    /// <summary>The session PCSX2.ini (contract 3.3 and 3.4). joined = XInput slots in join order (one to four players;
    /// three or four turn on port 1's multitap for this session). Throws when the settings or the request are outside
    /// the qualified shape.</summary>
    public static string Build(string settings, IReadOnlyList<int> joined, string ownerRoot, string copyRoot)
    {
        if (!Qualify(settings, []).Ok) throw new InvalidOperationException("settings not qualified");
        if (joined.Count is < 1 or > 4 || /*guard:pcsx2-joined-slots*/joined.Distinct().Count() != joined.Count || joined.Any(s => s is < 0 or > 3))
            throw new InvalidOperationException("invalid joined slots");
        if (!Path.IsPathFullyQualified(ownerRoot) || !Path.IsPathFullyQualified(copyRoot)) throw new ArgumentException("absolute roots required");
        var p = Parse(settings, out _, canonical: true)!;
        var slots = PlayerSections(joined.Count);
        bool multitap = joined.Count > 2;
        // The sections rebuilt from the allow-list: Pad1 and Pad2 always, and with multitap every section up to Pad5.
        var rebuilt = Enumerable.Range(1, multitap ? 5 : 2).Select(n => "Pad" + n).ToArray();
        var output = new List<string>();
        string? current = null; var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var pending = new List<string>();

        string FolderValue(string key, string? ownerValue)
        {
            foreach (var (k, folder) in PrivateFolders) if (k == key) return /*guard:pcsx2-private-folder*/Path.Combine(copyRoot, Session, folder);
            foreach (var (k, def) in SharedFolders) if (k == key) return SharedFolder(ownerValue, def, ownerRoot);
            throw new InvalidOperationException("not a folder key");
        }
        bool IsFolderKey(string key) => SharedFolders.Any(f => f.Key == key) || PrivateFolders.Any(f => f.Key == key);
        IEnumerable<string> PadSection(int pad)
        {
            int player = IndexOf(slots, pad);
            bool assigned = player >= 0 && player < joined.Count;
            // An absent section is not connected (Pad.cpp 152-154 for Pad2 to Pad8; Pad1 is required by Qualify).
            string ownerType = Get(p, "Pad" + pad, "Type") ?? "None";
            yield return "Type = " + (assigned ? /*guard:pcsx2-joined-type*/"DualShock2" : ownerType);
            foreach (string key in KeptPadKeys) { string? v = Get(p, "Pad" + pad, key); if (v != null) yield return key + " = " + v; }
            if (assigned)
                foreach (var (key, input) in Layout) yield return key + " = XInput-" + /*guard:pcsx2-join-order*/joined[player] + "/" + input;
        }
        static int IndexOf(IReadOnlyList<int> list, int value) { for (int i = 0; i < list.Count; i++) if (list[i] == value) return i; return -1; }
        bool Rebuilt(string? section) => section != null && rebuilt.Contains(section, StringComparer.Ordinal);
        void Close()
        {
            // Keys the section lacks are added before its trailing blank lines.
            var tail = new List<string>(); while (output.Count > 0 && output[^1].Length == 0) { tail.Add(""); output.RemoveAt(output.Count - 1); }
            if (current == "Folders")
            {
                foreach (var (k, _) in SharedFolders) if (seen.Add("Folders/" + k)) output.Add(k + " = " + FolderValue(k, null));
                foreach (var (k, _) in PrivateFolders) if (seen.Add("Folders/" + k)) output.Add(k + " = " + FolderValue(k, null));
            }
            if (current == "InputSources" && seen.Add("InputSources/XInput")) output.Add("XInput = true");
            if (current == "Pad" && multitap && seen.Add("Pad/MultitapPort1")) output.Add(MultitapOn);
            output.AddRange(tail);
        }
        foreach (var l in p.Lines)
        {
            if (l.Key == null && l.Section != null && Header.IsMatch(l.Raw))
            {
                Close(); current = l.Section; output.Add(l.Raw);
                if (Rebuilt(current)) output.AddRange(PadSection(current![3] - '0'));
                continue;
            }
            if (Rebuilt(current)) { if (l.Key == null && l.Raw.Length == 0) output.Add(l.Raw); continue; } // rebuilt above
            if (l.Key == null) { output.Add(l.Raw); continue; }
            seen.Add(current + "/" + l.Key);
            if (current == "Folders" && IsFolderKey(l.Key)) output.Add(l.Key + " = " + FolderValue(l.Key, l.Value));
            else if (current == "InputSources" && l.Key == "XInput") output.Add(/*guard:pcsx2-xinput-on*/"XInput = true");
            else if (current == "Pad" && l.Key == "MultitapPort1" && multitap) output.Add(/*guard:pcsx2-multitap-on*/MultitapOn);
            else output.Add(l.Raw);
        }
        Close();
        foreach (string missing in new[] { "Folders", "InputSources" })
            if (!HasSection(p, missing)) { output.Add("[" + missing + "]"); current = missing; Close(); }
        if (multitap && !HasSection(p, "Pad")) { output.Add("[Pad]"); current = "Pad"; Close(); }
        foreach (string pad in rebuilt)
            if (/*guard:pcsx2-added-pad-sections*/!HasSection(p, pad)) { output.Add("[" + pad + "]"); output.AddRange(PadSection(pad[3] - '0')); }
        string text = string.Join(p.Newline, output);
        return p.FinalNewline ? text + p.Newline : text;
    }
}
