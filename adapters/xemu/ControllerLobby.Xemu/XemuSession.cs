using System.Text.RegularExpressions;

namespace ControllerLobby.Xemu;

// Pure rules for Xemu lobby launches (Xemu adapter contract r3). Nothing here touches a disk, a
// process or a controller: callers pass the owner's settings text and observer records and receive text back.
// Qualified against Xemu 0.8.135, commit 6318bb112091635ef908255019e4d42956bc5fa8.
public sealed record Qualification(bool Ok, string Reason)
{
    internal static Qualification No(string reason) => new(false, reason);
}

/// <summary>One connected gamepad as the persistent observer reports it.</summary>
public sealed record ObservedPad(long InstanceId, string Path, string Guid);

public sealed record Eligibility(bool Ok, string Reason, IReadOnlyList<string> Guids)
{
    internal static Eligibility No(string reason) => new(false, reason, Array.Empty<string>());
}

public static class XemuSession
{
    public const string DefaultDriver = "usb-xbox-gamepad";
    // TOML whitespace is space and tab only; .NET's Trim() would also strip other Unicode spaces.
    private static readonly char[] Ws = [' ', '	'];
    private static readonly Regex Header = new("^\\[([A-Za-z0-9_]+(?:\\.[A-Za-z0-9_]+){0,2})\\]\\z", RegexOptions.CultureInvariant);
    private static readonly Regex Assignment = new("^([A-Za-z0-9_]+)[ \\t]*=[ \\t]*(.*)\\z", RegexOptions.CultureInvariant);
    private static readonly Regex SingleQuoted = new("^'([^'\\n]*)'\\z", RegexOptions.CultureInvariant);
    private static readonly Regex DoubleQuoted = new("^\"([^\"\\\\\\n]*)\"\\z", RegexOptions.CultureInvariant);
    // TOML integers and decimals: no leading zeros, an optional sign, digits on both sides of the point.
    private static readonly Regex Integer = new("^[+-]?(0|[1-9][0-9]*)\\z", RegexOptions.CultureInvariant);
    private static readonly Regex Decimal = new("^[+-]?(0|[1-9][0-9]*)\\.[0-9]+\\z", RegexOptions.CultureInvariant);
    private static readonly Regex BareKey = new("^[A-Za-z0-9_]+\\z", RegexOptions.CultureInvariant);
    private static readonly Regex Guid = new("^[0-9a-f]{32}\\z", RegexOptions.CultureInvariant);
    private static readonly Regex Slot = new("^XInput#([0-3])\\z", RegexOptions.CultureInvariant);
    private static readonly Regex Absolute = new("^([A-Za-z]:\\\\|\\\\\\\\)", RegexOptions.CultureInvariant);

    private enum Kind { Blank, Comment, Header, Value, ArrayStart, ArrayItem, ArrayEnd }
    /// <summary>A decimal value; never a string, so string-only keys refuse it.</summary>
    private sealed record DecimalValue(string Text);
    // Raw is the line without its terminator; End is the terminator exactly as in the file ("\r\n", "\n" or "").
    private sealed record Line(string Raw, string End, Kind Kind, string Table, string? Key = null, string? Value = null, object? Typed = null);

    // A value is a string (single or double quoted, no escapes), a Boolean, an integer or a decimal. Anything else is null.
    private static object? Scalar(string text)
    {
        var s = SingleQuoted.Match(text); if (s.Success) return s.Groups[1].Value;
        var d = DoubleQuoted.Match(text); if (d.Success) return d.Groups[1].Value;
        if (text is "true") return true;
        if (text is "false") return false;
        if (/*guard:toml-integer*/Integer.IsMatch(text)) return long.TryParse(text, out long n) ? n : null;
        if (Decimal.IsMatch(text)) return new DecimalValue(text);
        return null;
    }

    // An inline-table array item: "{}" or "{ key = scalar, key = scalar }", optional trailing comma after the brace.
    private static bool InlineItem(string item)
    {
        string body = item.EndsWith("},") ? item[..^1] : item;
        if (!body.StartsWith('{') || !body.EndsWith('}')) return false;
        string inner = body[1..^1].Trim(Ws);
        if (inner.Length == 0) return true;
        var parts = new List<string>(); var current = new System.Text.StringBuilder(); char? quote = null;
        foreach (char c in inner)
        {
            if (quote != null) { current.Append(c); if (c == quote) quote = null; continue; }
            if (c is '\'' or '"') { quote = c; current.Append(c); continue; }
            if (c == ',') { parts.Add(current.ToString()); current.Clear(); continue; }
            current.Append(c);
        }
        if (quote != null) return false;
        parts.Add(current.ToString());
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string part in parts)
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) return false;
            string key = part[..eq].Trim(Ws), value = part[(eq + 1)..].Trim(Ws);
            if (/*guard:inline-item-syntax*/!BareKey.IsMatch(key) || !seen.Add(key) || Scalar(value) == null) return false;
        }
        return true;
    }

    // True when the text outside quoted strings contains the character.
    private static bool OutsideQuotes(string text, char wanted)
    {
        char? quote = null;
        foreach (char c in text)
        {
            if (quote != null) { if (c == quote) quote = null; continue; }
            if (c is '\'' or '"') { quote = c; continue; }
            if (c == wanted) return true;
        }
        return false;
    }

    private static bool BalancedQuotes(string text)
    {
        char? quote = null;
        foreach (char c in text) { if (quote != null) { if (c == quote) quote = null; } else if (c is '\'' or '"') quote = c; }
        return quote == null;
    }

    // Splits keeping each line's own terminator; a carriage return not followed by a line feed refuses.
    private static List<(string Raw, string End)>? Split(string text)
    {
        var lines = new List<(string, string)>(); int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (/*guard:lone-cr*/i + 1 >= text.Length || text[i + 1] != '\n') return null;
                lines.Add((text[start..i], "\r\n")); i++; start = i + 1;
            }
            else if (text[i] == '\n') { lines.Add((text[start..i], "\n")); start = i + 1; }
        }
        lines.Add((text[start..], ""));
        return lines;
    }

    // The strict subset of TOML the adapter reads exactly; null when any line is outside it.
    private static List<Line>? Parse(string text, out string reason)
    {
        reason = "";
        var split = Split(text);
        if (split == null) { reason = "outside-grammar:carriage-return"; return null; }
        var lines = new List<Line>(); string table = ""; bool inArray = false, needComma = false;
        var tables = new HashSet<string>(StringComparer.Ordinal); var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (raw, end) in split)
        {
            if (/*guard:control-characters*/raw.Any(c => c < ' ' && c != '\t' || c == '\u007f')) { reason = "outside-grammar:control-character"; return null; }
            if (inArray)
            {
                string item = raw.Trim(Ws);
                if (item == "]") { inArray = false; needComma = false; lines.Add(new(raw, end, Kind.ArrayEnd, table)); continue; }
                if (/*guard:item-separator*/needComma) { reason = "outside-grammar:array-item"; return null; }
                // One level only: an inline-table item on its own line, no other bracket outside quotes.
                if (!BalancedQuotes(item) || /*guard:array-one-level*/OutsideQuotes(item, '[') || OutsideQuotes(item, ']') || !InlineItem(item))
                { reason = "outside-grammar:array-item"; return null; }
                needComma = !item.EndsWith("},");
                lines.Add(new(raw, end, Kind.ArrayItem, table)); continue;
            }
            if (/*guard:toml-whitespace*/raw.Trim(Ws).Length == 0) { lines.Add(new(raw, end, Kind.Blank, table)); continue; }
            if (raw.TrimStart(Ws).StartsWith('#')) { lines.Add(new(raw, end, Kind.Comment, table)); continue; }
            var h = Header.Match(raw);
            if (h.Success)
            {
                table = h.Groups[1].Value;
                if (/*guard:repeated-table*/!tables.Add(table)) { reason = "outside-grammar:repeated-table " + table; return null; }
                lines.Add(new(raw, end, Kind.Header, table)); continue;
            }
            var a = Assignment.Match(raw);
            if (!a.Success || table.Length == 0) { reason = "outside-grammar:line"; return null; }
            string key = a.Groups[1].Value, value = a.Groups[2].Value;
            if (!keys.Add(table + "/" + key)) { reason = "outside-grammar:repeated-key " + table + "." + key; return null; }
            if (table == "input" && key == "gamepad_mappings" && value == "[") { inArray = true; lines.Add(new(raw, end, Kind.ArrayStart, table, key)); continue; }
            object? typed = Scalar(value);
            if (typed == null) { reason = "outside-grammar:value " + table + "." + key; return null; }
            lines.Add(new(raw, end, Kind.Value, table, key, value, typed));
        }
        if (inArray) { reason = "outside-grammar:unterminated-array"; return null; }
        // A key may not share its name with a table, present (as a header or as the implicit parent of a deeper
        // header) or one the adapter would add.
        foreach (var l in lines.Where(l => l.Kind is Kind.Value or Kind.ArrayStart))
        {
            string child = l.Table + "." + l.Key;
            if (/*guard:key-table-collision*/tables.Any(x => x == child || x.StartsWith(child + ".", StringComparison.Ordinal))
                || child is "input.bindings" or "input.peripherals" || l.Table == "input.peripherals")
            { reason = "outside-grammar:key-names-a-table " + child; return null; }
        }
        return lines;
    }

    private static bool IsPort(string key, out int port, out bool driver)
    {
        port = 0; driver = false;
        var m = Regex.Match(key, "^port([1-4])(_driver)?\\z", RegexOptions.CultureInvariant);
        if (!m.Success) return false;
        port = m.Groups[1].Value[0] - '0'; driver = m.Groups[2].Success; return true;
    }

    /// <summary>Read-only qualification of the owner's settings before the lobby (rule 3.1).</summary>
    public static Qualification Qualify(string? ownerText)
    {
        if (ownerText == null) return Qualification.No("settings-unreadable");
        var lines = Parse(ownerText, out string reason);
        if (lines == null) return Qualification.No(reason);
        foreach (var l in lines.Where(l => l.Kind == Kind.Value))
        {
            string id = l.Table + "." + l.Key;
            if (l.Table == "input" && l.Key == "auto_bind" && /*guard:typed-auto-bind*/l.Typed is not bool) return Qualification.No("wrong-type " + id);
            if (l.Table == "input.bindings")
            {
                if (!IsPort(l.Key!, out _, out _)) return Qualification.No("unknown-binding-key " + id);
                if (/*guard:typed-port*/l.Typed is not string) return Qualification.No("wrong-type " + id);
            }
            if (l.Table == "sys.files" && (/*guard:system-paths-absolute*/l.Typed is not string path || path.Length > 0 && !Absolute.IsMatch(path)))
                return Qualification.No("relative-or-invalid-path " + id);
            if (Regex.IsMatch(l.Table, "^input\\.peripherals\\.port[1-4]\\z"))
            {
                if (l.Key is "peripheral_type_0" or "peripheral_type_1") { if (l.Typed is not long) return Qualification.No("wrong-type " + id); }
                else if (l.Key is "peripheral_param_0" or "peripheral_param_1")
                { if (/*guard:peripheral-paths-absolute*/l.Typed is not string p || p.Length > 0 && !Absolute.IsMatch(p)) return Qualification.No("relative-or-invalid-path " + id); }
                else return Qualification.No("unknown-peripheral-key " + id);
            }
            else if (l.Table.StartsWith("input.peripherals", StringComparison.Ordinal)) return Qualification.No("unknown-peripheral-table " + l.Table);
        }
        if (lines.Any(l => l.Kind == Kind.Header && l.Table.StartsWith("input.peripherals", StringComparison.Ordinal)
            && !Regex.IsMatch(l.Table, "^input\\.peripherals(\\.port[1-4])?\\z"))) return Qualification.No("unknown-peripheral-table");
        return new(true, "");
    }

    /// <summary>LaunchBox arguments a mapped launch accepts (rule 3.4): optional -full-screen, then -dvd_path and one path.</summary>
    public static bool ArgumentsQualified(IReadOnlyList<string> args)
    {
        int i = 0;
        if (args.Count > 0 && args[0] == "-full-screen") i = 1;
        return /*guard:argument-whitelist*/args.Count == i + 2 && args[i] == "-dvd_path" && args[i + 1].Length > 0 && !args[i + 1].StartsWith('-');
    }

    /// <summary>Rule 3.2: the joined slots' GUIDs in join order, or why join order cannot be applied.</summary>
    public static Eligibility Eligible(IReadOnlyList<ObservedPad> baseline, IReadOnlyList<ObservedPad> now, IReadOnlyList<int> joinedSlots)
    {
        if (joinedSlots.Count is < 1 or > 4 || joinedSlots.Distinct().Count() != joinedSlots.Count) return Eligibility.No("invalid-selection");
        // Any difference from the sealed list, including a new instance ID on the same slot with the same GUID, refuses.
        if (/*guard:baseline-unchanged*/baseline.Count != now.Count || baseline.Except(now).Any() || now.Except(baseline).Any()) return Eligibility.No("controllers-changed");
        if (/*guard:valid-guid*/now.Any(p => !Guid.IsMatch(p.Guid))) return Eligibility.No("invalid-guid");
        // SDL_JoystickID is a non-zero Uint32; anything else is not a record from the observer.
        if (/*guard:instance-id-range*/now.Any(p => p.InstanceId is < 1 or > uint.MaxValue) || now.Select(p => p.InstanceId).Distinct().Count() != now.Count)
            return Eligibility.No("invalid-observer");
        var guids = new List<string>();
        foreach (int slot in joinedSlots)
        {
            var records = now.Where(p => Slot.Match(p.Path) is { Success: true } m && m.Groups[1].Value[0] - '0' == slot).ToList();
            if (/*guard:one-record-per-slot*/records.Count != 1) return Eligibility.No(records.Count == 0 ? "joined-slot-not-observed" : "joined-slot-ambiguous");
            string g = records[0].Guid;
            if (/*guard:unique-guid*/now.Count(p => p.Guid == g) != 1) return Eligibility.No("duplicate-guid");
            guids.Add(g);
        }
        return new(true, "", guids.AsReadOnly());
    }

    /// <summary>Rule 3.6: the existing-controls session file, an unchanged copy of the owner's settings.</summary>
    public static string BuildExisting(string ownerText)
    {
        if (!Qualify(ownerText).Ok) throw new InvalidOperationException("not qualified");
        return ownerText;
    }

    /// <summary>Rule 3.3: the owner's text with all four ports written in join order, auto_bind false, every other
    /// byte unchanged (each untouched line keeps its own terminator; new lines use the terminator of the line they
    /// follow, or the file's first terminator).</summary>
    public static string BuildMapped(string ownerText, IReadOnlyList<string> guids)
    {
        if (!Qualify(ownerText).Ok) throw new InvalidOperationException("not qualified");
        if (guids.Count is < 1 or > 4 || guids.Any(g => !Guid.IsMatch(g)) || guids.Distinct().Count() != guids.Count) throw new ArgumentException("invalid players");
        var lines = Parse(ownerText, out _)!;
        string fallback = lines.Select(l => l.End).FirstOrDefault(e => e.Length > 0) ?? "\n";
        // The owner's driver tokens are kept exactly as written (they are qualified strings).
        var drivers = new string?[5];
        foreach (var l in lines.Where(l => l.Kind == Kind.Value && l.Table == "input.bindings"))
            if (IsPort(l.Key!, out int port, out bool driver) && driver) drivers[port] = /*guard:raw-driver-token*/l.Value;
        var bindings = new List<string>();
        for (int port = 1; port <= 4; port++)
        {
            bool used = port <= guids.Count;
            if (used || drivers[port] != null)
                bindings.Add($"port{port}_driver = " + (drivers[port] is { } d && (string)Scalar(d)! is { Length: > 0 } ? d : "'" + DefaultDriver + "'"));
            bindings.Add($"port{port} = '{/*guard:join-order*/(used ? guids[port - 1] : "")}'");
        }
        bool hasAuto = lines.Any(x => x.Kind == Kind.Value && x.Table == "input" && x.Key == "auto_bind");
        bool hasInput = lines.Any(l => l.Kind == Kind.Header && l.Table == "input"), hasBindings = lines.Any(l => l.Kind == Kind.Header && l.Table == "input.bindings");
        var output = new System.Text.StringBuilder(); bool autoWritten = false;
        void Add(string line, string end) => output.Append(line).Append(end);
        foreach (var l in lines)
        {
            if (l.Kind == Kind.Value && l.Table == "input.bindings" && IsPort(l.Key!, out _, out _)) continue; // replaced below
            string end = l.End.Length > 0 ? l.End : "";
            if (l.Kind == Kind.Value && l.Table == "input" && l.Key == "auto_bind") { Add("auto_bind = false", end); autoWritten = true; continue; }
            Add(l.Raw, l.End);
            string inner = l.End.Length > 0 ? l.End : fallback;
            if (l.Kind == Kind.Header && l.Table == "input" && !hasAuto)
            { if (l.End.Length == 0) output.Append(inner); Add(/*guard:auto-bind-off*/"auto_bind = false", inner); autoWritten = true; }
            if (l.Kind == Kind.Header && l.Table == "input.bindings")
            { if (l.End.Length == 0) output.Append(inner); foreach (string b in bindings) Add(b, inner); }
        }
        if (!hasInput || !hasBindings)
        {
            string text = output.ToString();
            if (text.Length > 0 && !text.EndsWith('\n')) output.Append(fallback);
            if (!hasInput) { Add("", fallback); Add("[input]", fallback); Add("auto_bind = false", fallback); autoWritten = true; }
            if (!hasBindings) { Add("", fallback); Add("[input.bindings]", fallback); foreach (string b in bindings) Add(b, fallback); }
        }
        if (!autoWritten) throw new InvalidOperationException("auto_bind not written");
        return output.ToString();
    }
}
