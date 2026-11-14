using ControllerLobby.Xemu;

// Pure checks for the Xemu session rules. Synthetic settings text and observer lists; no emulator, disk or controller.
// With "--dump <file>", also writes each corpus input with its qualification and mapped output, for the
// independent TOML check in harness/test_xemu_toml_oracle.py.
int passed = 0, failed = 0;
void Require(bool ok, string why) { if (!ok) throw new InvalidOperationException(why); }
void Check(string name, Action body) { try { body(); passed++; Console.WriteLine("PASS " + name); } catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e.Message); } }
void Refused(string name, string reasonStart, string text) => Check(name, () => { var q = XemuSession.Qualify(text); Require(!q.Ok && q.Reason.StartsWith(reasonStart), $"expected {reasonStart}, got {(q.Ok ? "qualified" : q.Reason)}"); });

const string G0 = "0300fa675e0400008e02000014017801", G1 = "0300ba665e0400008e02000010017801", G2 = "03007ba65e0400008e02000014017801";
// A real owner's file shape, with synthetic paths.
string Owner(string extra = "", string input = "", string bindings = null!) =>
    "[general]\nshow_welcome = false\n\n[general.updates]\ncheck = false\n\n[input]\n" + input + "gamepad_mappings = [\n" +
    $"    {{ gamepad_id = '{G0}'}},\n    {{ gamepad_id = '{G1}'}},\n    {{ gamepad_id = '{G2}'}}\n    ]\n\n[input.bindings]\n" +
    (bindings ?? $"port1_driver = 'usb-xbox-gamepad'\nport1 = '{G0}'\nport2_driver = 'usb-xbox-gamepad'\nport2 = '{G1}'\nport3_driver = 'usb-xbox-gamepad'\nport3 = '{G2}'\n") +
    "\n[display.window]\nlast_width = 1280\nlast_height = 960\n\n[display.ui]\nshow_menubar = false\nscale = 2\n\n[sys.files]\n" +
    "bootrom_path = 'D:\\Synthetic\\Xemu\\bios\\mcpx_1.0.bin'\neeprom_path = 'D:\\Synthetic\\Xemu\\eeprom.bin'\nhdd_path = 'D:\\Synthetic\\Xemu\\saves\\xbox_hdd.qcow2'\n" + extra;
Dictionary<string, string> Table(string text, string table)
{
    var r = new Dictionary<string, string>(); string? t = null;
    foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
    {
        if (raw.StartsWith('[')) { t = raw.Trim('[', ']'); continue; }
        int eq = raw.IndexOf('='); if (t == table && eq > 0) r[raw[..eq].Trim()] = raw[(eq + 1)..].Trim().Trim('\'');
    }
    return r;
}
ObservedPad P(long id, int slot, string guid) => new(id, "XInput#" + slot, guid);
IReadOnlyList<ObservedPad> Three() => [P(3, 0, G0), P(2, 1, G1), P(1, 2, G2)];

Check("owner-shape-qualifies", () => { var q = XemuSession.Qualify(Owner()); Require(q.Ok, q.Reason); });
Check("crlf-owner-shape-qualifies", () => Require(XemuSession.Qualify(Owner().Replace("\n", "\r\n")).Ok, "CRLF refused"));
Check("comments-blank-lines-numbers-and-double-quotes-qualify", () => Require(XemuSession.Qualify(Owner(extra: "\n# note\n   \n[misc]\nratio = 1.5\nneg = -2\nname = \"x y\"\n")).Ok, "ordinary forms refused"));
Refused("unreadable", "settings-unreadable", null!);
Refused("lone-carriage-return", "outside-grammar:carriage-return", Owner().Replace("[display.ui]", "\r[display.ui]"));
Refused("indented-header", "outside-grammar:line", Owner().Replace("[display.ui]", "  [display.ui]"));
Refused("spaces-inside-header", "outside-grammar:line", Owner().Replace("[display.ui]", "[ display.ui ]"));
Refused("four-level-header", "outside-grammar:line", Owner(extra: "\n[a.b.c.d]\nx = 1\n"));
Refused("dotted-key", "outside-grammar:line", Owner(extra: "\n[misc]\na.b = 1\n"));
Refused("quoted-key", "outside-grammar:line", Owner(extra: "\n[misc]\n'a' = 1\n"));
Refused("trailing-comment", "outside-grammar:value", Owner().Replace("check = false", "check = false # off"));
Refused("multi-line-string", "outside-grammar:value", Owner(extra: "\n[misc]\nnote = '''\nx\n'''\n"));
Refused("escape-in-double-quotes", "outside-grammar:value", Owner(extra: "\n[misc]\nnote = \"a\\\"b\"\n"));
Refused("inline-table-value", "outside-grammar:value", Owner(extra: "\n[misc]\nx = { a = 1 }\n"));
Refused("other-array", "outside-grammar:value", Owner(extra: "\n[misc]\nx = [1, 2]\n"));
Refused("repeated-key", "outside-grammar:repeated-key", Owner().Replace("check = false", "check = false\ncheck = true"));
Refused("repeated-table", "outside-grammar:repeated-table", Owner(extra: "\n[input.bindings]\nport4 = ''\n"));
Refused("setting-before-any-table", "outside-grammar:line", "x = 1\n" + Owner());
Refused("unterminated-array", "outside-grammar:array-item", Owner().Replace("    ]\n\n[input.bindings]", "\n[input.bindings]"));
Refused("nested-array-inner-bracket", "outside-grammar:array-item", Owner().Replace($"    {{ gamepad_id = '{G1}'}},", $"    {{ gamepad_id = '{G1}', x = [1,\n    ]}},"));
Refused("array-item-with-bracket", "outside-grammar:array-item", Owner().Replace($"{{ gamepad_id = '{G1}'}}", $"{{ gamepad_id = '{G1}', x = [1] }}"));
Check("bracket-inside-quoted-array-string-is-content", () => Require(XemuSession.Qualify(Owner().Replace($"{{ gamepad_id = '{G1}'}}", $"{{ gamepad_id = '{G1}', name = 'pad ]['}}")).Ok, "quoted bracket refused"));
Refused("scalar-bindings-key-beside-table", "outside-grammar:key-names-a-table", Owner(input: "bindings = 'x'\n"));
Refused("scalar-bindings-key-without-table", "outside-grammar:key-names-a-table", Owner(input: "bindings = 'x'\n", bindings: "").Replace("[input.bindings]\n", ""));
Refused("key-naming-a-child-table", "outside-grammar:key-names-a-table", Owner(extra: "\n[misc]\nsub = 1\n[misc.sub]\nx = 1\n"));
Refused("auto-bind-not-boolean", "wrong-type input.auto_bind", Owner(input: "auto_bind = 'no'\n"));
Refused("port-not-a-string", "wrong-type input.bindings.port2", Owner(bindings: $"port1 = '{G0}'\nport2 = 5\n"));
Refused("unknown-binding-key", "unknown-binding-key", Owner(bindings: $"port1 = '{G0}'\nport5 = ''\n"));
Refused("relative-system-path", "relative-or-invalid-path sys.files.hdd_path", Owner().Replace("hdd_path = 'D:\\Synthetic\\Xemu\\saves\\xbox_hdd.qcow2'", "hdd_path = 'saves\\xbox_hdd.qcow2'"));
Refused("system-path-not-a-string", "relative-or-invalid-path sys.files.hdd_path", Owner().Replace("hdd_path = 'D:\\Synthetic\\Xemu\\saves\\xbox_hdd.qcow2'", "hdd_path = 3"));
Refused("relative-memory-unit", "relative-or-invalid-path input.peripherals.port1.peripheral_param_0", Owner(extra: "\n[input.peripherals.port1]\nperipheral_type_0 = 1\nperipheral_param_0 = 'mu.bin'\n"));
Refused("memory-unit-type-not-integer", "wrong-type input.peripherals.port1.peripheral_type_0", Owner(extra: "\n[input.peripherals.port1]\nperipheral_type_0 = 'mu'\n"));
Refused("unknown-peripheral-table", "unknown-peripheral-table", Owner(extra: "\n[input.peripherals.port9]\nperipheral_type_0 = 1\n"));
Check("absolute-memory-unit-qualifies", () => Require(XemuSession.Qualify(Owner(extra: "\n[input.peripherals.port1]\nperipheral_type_0 = 1\nperipheral_param_0 = 'D:\\Synthetic\\mu.bin'\n")).Ok, "absolute memory unit refused"));

Check("arguments-whitelist", () =>
{
    Require(XemuSession.ArgumentsQualified(["-full-screen", "-dvd_path", @"E:\Games\Xbox\Game.xiso"]) && XemuSession.ArgumentsQualified(["-dvd_path", @"E:\Games\Game.xiso"]), "the LaunchBox form was refused");
    foreach (var bad in new[] { new[] { "-full-screen" }, ["-dvd_path"], ["-config_path", "x", "-dvd_path", "y"], ["-full-screen", "-dvd_path", "y", "-snapshot"],
        ["-dvd_path", "-drive"], ["-machine", "xbox", "-dvd_path", "y"], ["-dvd_path", ""], ["-full-screen", "-full-screen", "-dvd_path", "y"] })
        Require(!XemuSession.ArgumentsQualified(bad), "accepted: " + string.Join(' ', bad));
});

Check("eligible-join-order-uses-paths-not-enumeration-order", () =>
{
    var e = XemuSession.Eligible(Three(), Three(), [1, 0]); Require(e.Ok && e.Guids.SequenceEqual([G1, G0]), e.Reason);
    var solo = XemuSession.Eligible(Three(), Three(), [2]); Require(solo.Ok && solo.Guids.SequenceEqual([G2]), solo.Reason);
});
Check("same-slot-same-guid-replacement-is-a-change", () =>
{
    IReadOnlyList<ObservedPad> replaced = [P(3, 0, G0), P(9, 1, G1), P(1, 2, G2)];
    var e = XemuSession.Eligible(Three(), replaced, [1, 0]); Require(!e.Ok && e.Reason == "controllers-changed", "replacement accepted: " + e.Reason);
});
Check("added-or-removed-pad-is-a-change", () =>
{
    Require(XemuSession.Eligible(Three(), [P(3, 0, G0), P(2, 1, G1)], [1, 0]).Reason == "controllers-changed", "removal accepted");
    Require(XemuSession.Eligible(Three(), [.. Three(), P(7, 3, "03000000000000000000000000000000")], [1, 0]).Reason == "controllers-changed", "addition accepted");
});
Check("duplicate-guid-among-unjoined-pads-refuses", () =>
{
    IReadOnlyList<ObservedPad> dup = [P(3, 0, G0), P(2, 1, G1), P(1, 2, G1)];
    Require(XemuSession.Eligible(dup, dup, [1, 0]).Reason == "duplicate-guid", "a joined GUID shared with an unjoined pad was accepted");
    Require(XemuSession.Eligible(dup, dup, [0]).Ok, "a unique joined GUID was refused because two other pads share one");
});
Check("joined-slot-must-have-exactly-one-record", () =>
{
    Require(XemuSession.Eligible(Three(), Three(), [3]).Reason == "joined-slot-not-observed", "missing slot accepted");
    IReadOnlyList<ObservedPad> two = [P(3, 0, G0), P(4, 0, G1)];
    Require(XemuSession.Eligible(two, two, [0]).Reason == "joined-slot-ambiguous", "two records on one slot accepted");
});
Check("invalid-guid-or-observer-refuses", () =>
{
    IReadOnlyList<ObservedPad> bad = [P(3, 0, "XYZ")]; Require(XemuSession.Eligible(bad, bad, [0]).Reason == "invalid-guid", "invalid GUID accepted");
    IReadOnlyList<ObservedPad> upper = [P(3, 0, G0.ToUpperInvariant())]; Require(XemuSession.Eligible(upper, upper, [0]).Reason == "invalid-guid", "upper-case GUID accepted");
    IReadOnlyList<ObservedPad> same = [P(3, 0, G0), P(3, 1, G1)]; Require(XemuSession.Eligible(same, same, [0]).Reason == "invalid-observer", "repeated instance ID accepted");
    Require(XemuSession.Eligible(Three(), Three(), [0, 1, 2, 0]).Reason == "invalid-selection", "repeated slot accepted");
});

Check("mapped-session-writes-all-four-ports-in-join-order", () =>
{
    string s = XemuSession.BuildMapped(Owner(), [G1, G0]); var b = Table(s, "input.bindings");
    Require(b["port1"] == G1 && b["port2"] == G0 && b["port3"] == "" && b["port4"] == "", "ports not in join order: " + string.Join(",", b));
    Require(b["port1_driver"] == "usb-xbox-gamepad" && b["port2_driver"] == "usb-xbox-gamepad" && b["port3_driver"] == "usb-xbox-gamepad" && !b.ContainsKey("port4_driver"), "drivers wrong");
    Require(Table(s, "input")["auto_bind"] == "false", "auto_bind not false");
    Require(XemuSession.Qualify(s).Ok, "the session file is outside the grammar");
});
Check("mapped-session-changes-nothing-else", () =>
{
    string owner = Owner(); string s = XemuSession.BuildMapped(owner, [G2]);
    var kept = owner.Split('\n').Where(l => !l.StartsWith("port")).ToArray(); var made = s.Split('\n').Where(l => !l.StartsWith("port") && l != "auto_bind = false").ToArray();
    Require(kept.SequenceEqual(made), "a line other than ports and auto_bind changed");
});
Check("mapped-session-replaces-an-existing-auto-bind-and-keeps-crlf", () =>
{
    string s = XemuSession.BuildMapped(Owner(input: "auto_bind = true\n").Replace("\n", "\r\n"), [G0]);
    Require(s.Contains("\r\n") && !s.Replace("\r\n", "").Contains('\n') && Table(s, "input")["auto_bind"] == "false" && s.Split("auto_bind").Length == 2, "auto_bind not replaced once, or line endings changed");
});
Check("mapped-session-adds-missing-tables", () =>
{
    string bare = "[general]\nshow_welcome = false\n\n[sys.files]\nhdd_path = 'D:\\Synthetic\\hdd.qcow2'\n";
    string s = XemuSession.BuildMapped(bare, [G1]);
    Require(Table(s, "input")["auto_bind"] == "false" && Table(s, "input.bindings")["port1"] == G1 && Table(s, "input.bindings")["port4"] == "" && XemuSession.Qualify(s).Ok, "missing tables not added: " + s);
});
Check("keyboard-or-owner-guid-never-left-on-a-port", () =>
{
    string s = XemuSession.BuildMapped(Owner(bindings: "port1 = 'keyboard'\nport4 = '" + G2 + "'\n"), [G1]); var b = Table(s, "input.bindings");
    Require(b["port1"] == G1 && b["port2"] == "" && b["port3"] == "" && b["port4"] == "" && !s.Contains("keyboard"), "an old binding survived");
});
Check("owner-driver-is-kept", () => Require(Table(XemuSession.BuildMapped(Owner(bindings: "port1_driver = 'usb-xbox-gamepad-s'\nport1 = ''\n"), [G0]), "input.bindings")["port1_driver"] == "usb-xbox-gamepad-s", "owner driver replaced"));
Check("existing-controls-copy-is-unchanged", () => { string o = Owner(); Require(XemuSession.BuildExisting(o) == o, "existing-controls copy differs"); });
Check("builders-refuse-unqualified-input", () =>
{
    foreach (Action a in new Action[] { () => XemuSession.BuildMapped("x = 1\n", [G0]), () => XemuSession.BuildExisting("x = 1\n"), () => XemuSession.BuildMapped(Owner(), []), () => XemuSession.BuildMapped(Owner(), [G0, G0]), () => XemuSession.BuildMapped(Owner(), ["bad"]) })
    { bool threw = false; try { a(); } catch (Exception e) when (e is InvalidOperationException or ArgumentException) { threw = true; } Require(threw, "a builder accepted input outside the rules"); }
});

// Review oracle cases, literal inputs.
const string Gx = "11111111111111111111111111111111";
Refused("oracle-decimal-port", "wrong-type input.bindings.port1", "[input.bindings]\nport1 = 1.5\n");
Refused("oracle-decimal-driver", "wrong-type input.bindings.port1_driver", "[input.bindings]\nport1_driver = 1.5\n");
foreach (var (name, token) in new[] { ("oracle-apostrophe-driver", "\"owner's-driver\""), ("oracle-quote-comment-driver", "\"usb-xbox-gamepad-s' #\""), ("oracle-ordinary-driver-control", "'usb-xbox-gamepad-s'") })
    Check(name, () =>
    {
        string owner = (name.Contains("ordinary") ? "[input]\nauto_bind = true\n" : "") + "[input.bindings]\nport1_driver = " + token + "\n";
        string s = XemuSession.BuildMapped(owner, [Gx]);
        Require(s.Contains("port1_driver = " + token + "\n") && XemuSession.Qualify(s).Ok, "driver token not kept or output outside the grammar: " + s);
    });
Refused("oracle-invalid-inline-table", "outside-grammar:array-item", "[input]\ngamepad_mappings = [\n{ invalid }\n]\n");
Refused("oracle-leading-zero-number", "outside-grammar:value display.window.last_width", "[display.window]\nlast_width = 01280\n");
Refused("oracle-implicit-parent-collision", "outside-grammar:key-names-a-table misc.branch", "[misc]\nbranch = 1\n[misc.branch.child]\nx = 1\n");
Check("oracle-untouched-mixed-newline-tail", () =>
{
    const string tail = "[display.window]\r\nlast_width = 1280\n\n\n";
    string s = XemuSession.BuildMapped("[input]\r\nauto_bind = true\n[input.bindings]\r\nport1 = 'keyboard'\n" + tail, [Gx]);
    Require(s.EndsWith(tail) && s.StartsWith("[input]\r\nauto_bind = false\n[input.bindings]\r\n"), "untouched line endings or trailing blank lines changed: " + s.Replace("\r", "\\r").Replace("\n", "\\n"));
});
foreach (long id in new[] { 0L, -1L, 4294967296L })
    Check("oracle-invalid-instance-" + id, () => { IReadOnlyList<ObservedPad> l = [P(id, 0, G0)]; Require(XemuSession.Eligible(l, l, [0]).Reason == "invalid-observer", "instance ID accepted"); });
Check("oracle-valid-instance-control", () => { IReadOnlyList<ObservedPad> l = [P(4294967295L, 0, G0)]; Require(XemuSession.Eligible(l, l, [0]).Ok, "largest valid instance ID refused"); });
Check("inline-item-forms", () =>
{
    foreach (string ok in new[] { "{}", "{ gamepad_id = 'x', enable_rumble = true }," })
        Require(XemuSession.Qualify("[input]\ngamepad_mappings = [\n" + ok + "\n]\n").Ok, "accepted form refused: " + ok);
    foreach (string bad in new[] { "{ a = 1, a = 2 }", "{ a.b = 1 }", "{ a = }", "{ a = 01 }", "{ a = 'x' " })
        Require(!XemuSession.Qualify("[input]\ngamepad_mappings = [\n" + bad + "\n]\n").Ok, "invalid item accepted: " + bad);
});
// Review edge cases, literal inputs.
Refused("edge-array-items-without-comma", "outside-grammar:array-item", "[input]\ngamepad_mappings = [\n{ gamepad_id = 'a' }\n{ gamepad_id = 'b' }\n]\n");
Check("edge-array-items-with-commas-and-last-without", () => Require(XemuSession.Qualify("[input]\ngamepad_mappings = [\n{ gamepad_id = 'a' },\n{ gamepad_id = 'b' }\n]\n").Ok, "comma-separated items refused"));
Refused("edge-nul-in-string", "outside-grammar:control-character", "[display.ui]\nlabel = 'a\0b'\n");
Refused("edge-nul-in-comment", "outside-grammar:control-character", "[input]\n# note\0ignored\nauto_bind = true\n");
Refused("edge-delete-character", "outside-grammar:control-character", "[display.ui]\nlabel = 'a\u007fb'\n");
Check("edge-tab-is-allowed", () => Require(XemuSession.Qualify("[display.ui]\nlabel\t=\t'a\tb'\n").Ok, "tab refused"));
Check("edge-guid-with-final-linefeed", () => { IReadOnlyList<ObservedPad> l = [P(3, 0, G0 + "\n")]; Require(XemuSession.Eligible(l, l, [0]).Reason == "invalid-guid", "GUID with a final line feed accepted"); });
Check("edge-slot-with-final-linefeed", () => { IReadOnlyList<ObservedPad> l = [new(3, "XInput#0\n", G0)]; Require(XemuSession.Eligible(l, l, [0]).Reason == "joined-slot-not-observed", "slot path with a final line feed accepted"); });
Check("edge-mapped-guid-with-final-linefeed", () =>
{
    bool threw = false; try { XemuSession.BuildMapped(Owner(), [G0 + "\n"]); } catch (ArgumentException) { threw = true; }
    Require(threw, "the builder accepted a GUID with a final line feed");
});
// Review whitespace cases: only space and tab are TOML whitespace.
Refused("ws-line-of-only-nbsp", "outside-grammar:line", "[general]\n \nshow_welcome = false\n");
Refused("ws-nbsp-before-comment", "outside-grammar:line", "[general]\n # note\nshow_welcome = false\n");
Refused("ws-nbsp-in-inline-item", "outside-grammar:array-item", "[input]\ngamepad_mappings = [\n{ gamepad_id = 'a' }\n]\n");
Check("ws-unicode-inside-quoted-value-is-kept", () => Require(XemuSession.Qualify("[display.ui]\nlabel = 'café x'\n").Ok, "Unicode inside a quoted value refused"));
Check("ws-unicode-inside-comment-is-kept", () => Require(XemuSession.Qualify("[general]\n# café note\nshow_welcome = false\n").Ok, "Unicode inside a comment refused"));
Check("file-without-final-newline-is-preserved", () =>
{
    string s = XemuSession.BuildMapped("[general]\nshow_welcome = false", [G0]);
    Require(s.StartsWith("[general]\nshow_welcome = false\n\n[input]\nauto_bind = false\n") && XemuSession.Qualify(s).Ok, "no-final-newline file mishandled: " + s);
});

var corpus = new (string Name, string Text)[] {
    ("owner-shape", Owner()), ("owner-crlf", Owner().Replace("\n", "\r\n")), ("owner-extra-forms", Owner(extra: "\n# note\n   \n[misc]\nratio = 1.5\nneg = -2\nname = \"x y\"\n")),
    ("owner-auto-bind-true", Owner(input: "auto_bind = true\n")), ("owner-memory-unit", Owner(extra: "\n[input.peripherals.port1]\nperipheral_type_0 = 1\nperipheral_param_0 = 'D:\\Synthetic\\mu.bin'\n")),
    ("owner-keyboard", Owner(bindings: "port1 = 'keyboard'\nport4 = '" + G2 + "'\n")), ("owner-driver-s", Owner(bindings: "port1_driver = 'usb-xbox-gamepad-s'\nport1 = ''\n")),
    ("bare", "[general]\nshow_welcome = false\n\n[sys.files]\nhdd_path = 'D:\\Synthetic\\hdd.qcow2'\n"), ("no-final-newline", "[general]\nshow_welcome = false"),
    ("apostrophe-driver", "[input.bindings]\nport1_driver = \"owner's-driver\"\n"), ("quote-comment-driver", "[input.bindings]\nport1_driver = \"usb-xbox-gamepad-s' #\"\n"),
    ("mixed-newline-tail", "[input]\r\nauto_bind = true\n[input.bindings]\r\nport1 = 'keyboard'\n[display.window]\r\nlast_width = 1280\n\n\n"),
    ("items-with-commas", "[input]\ngamepad_mappings = [\n{ gamepad_id = 'a' },\n{ gamepad_id = 'b' }\n]\n"), ("empty-item", "[input]\ngamepad_mappings = [\n{}\n]\n"),
    ("tab", "[display.ui]\nlabel\t=\t'a\tb'\n"), ("quoted-bracket", Owner().Replace($"{{ gamepad_id = '{G1}'}}", $"{{ gamepad_id = '{G1}', name = 'pad ]['}}")),
    ("decimal-port", "[input.bindings]\nport1 = 1.5\n"), ("invalid-inline-table", "[input]\ngamepad_mappings = [\n{ invalid }\n]\n"),
    ("leading-zero", "[display.window]\nlast_width = 01280\n"), ("implicit-parent", "[misc]\nbranch = 1\n[misc.branch.child]\nx = 1\n"),
    ("items-without-comma", "[input]\ngamepad_mappings = [\n{ gamepad_id = 'a' }\n{ gamepad_id = 'b' }\n]\n"),
    ("nul-in-string", "[display.ui]\nlabel = 'a\0b'\n"), ("nul-in-comment", "[input]\n# note\0ignored\nauto_bind = true\n"),
    ("repeated-key", Owner().Replace("check = false", "check = false\ncheck = true")), ("dotted-key", Owner(extra: "\n[misc]\na.b = 1\n")),
    ("ws-line-of-only-nbsp", "[general]\n \nshow_welcome = false\n"), ("ws-nbsp-before-comment", "[general]\n # note\nshow_welcome = false\n"),
    ("ws-nbsp-in-inline-item", "[input]\ngamepad_mappings = [\n{ gamepad_id = 'a' }\n]\n"),
    ("ws-unicode-in-quoted-value", "[display.ui]\nlabel = 'café x'\n"), ("ws-unicode-in-comment", "[general]\n# café note\nshow_welcome = false\n"),
};
int dumpAt = Array.IndexOf(args, "--dump");
if (dumpAt >= 0)
{
    var rows = corpus.Select(c =>
    {
        var q = XemuSession.Qualify(c.Text);
        return new Dictionary<string, object?> { ["name"] = c.Name, ["text"] = c.Text, ["qualified"] = q.Ok, ["reason"] = q.Reason,
            ["mapped"] = q.Ok ? XemuSession.BuildMapped(c.Text, [G1, G0]) : null, ["guids"] = new[] { G1, G0 } };
    }).ToList();
    File.WriteAllText(args[dumpAt + 1], System.Text.Json.JsonSerializer.Serialize(rows));
}

Console.WriteLine($"Cases={passed + failed} Passed={passed} Failed={failed}; M/pure rules on synthetic settings text and observer lists; no emulator, disk or controller");
return failed == 0 ? 0 : 1;
