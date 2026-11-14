using System.Text;
using ControllerLobby.Pcsx2;

// Pure checks for the PCSX2 session rules (PCSX2 adapter contract r4). Synthetic settings text
// shaped like a real owner's PCSX2.ini; no emulator, disk or controller.
// With "--qualify <file>", reports only whether that file qualifies (used read-only against the owner's real file).
int passed = 0, failed = 0;
void Require(bool ok, string why) { if (!ok) throw new InvalidOperationException(why); }
void Check(string name, Action body) { try { body(); passed++; Console.WriteLine("PASS " + name); } catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e.Message); } }

int qualifyAt = Array.IndexOf(args, "--qualify");
if (qualifyAt >= 0)
{
    var q = Pcsx2Session.Qualify(File.ReadAllText(args[qualifyAt + 1]), []);
    Console.WriteLine(q.Ok ? "qualified" : "refused: " + q.Reason); return q.Ok ? 0 : 1;
}

const string OwnerRoot = @"D:\Synthetic\PCSX2", CopyRoot = @"D:\Synthetic\PCSX2\ControllerLobby.BigBox\Pcsx2Copy";
string Owner(string pad1 = null!, string pad2 = "Type = DualShock2\n", string extra = "", string folders = null!, string inputs = null!) =>
    "[UI]\nSettingsVersion = 1\n\n[Folders]\n" +
    (folders ?? "Bios = bios\nSnapshots = snaps\nSavestates = sstates\nMemoryCards = memcards\nLogs = logs\nCheats = cheats\nPatches = patches\nUserResources = resources\nCache = cache\nTextures = textures\nInputProfiles = inputprofiles\nVideos = videos\nDebuggerLayouts = debuggerlayouts\nDebuggerSettings = debuggersettings\n") +
    "\n[EmuCore/CPU]\nFPU.DenormalsAreZero = true\nFPU.Roundmode = 3\n\n[EmuCore]\nGzipIsoIndexTemplate = $(f).pindex.tmp\nBlockDumpSaveDirectory = \n\n[InputSources]\n" +
    (inputs ?? "Keyboard = true\nMouse = true\nSDL = true\nDInput = false\nXInput = false\n") +
    "\n[Hotkeys]\nToggleFullscreen = Keyboard/Alt & Keyboard/Return\nScreenshot = Keyboard/F8\nOpenPauseMenu = Keyboard/Escape\n\n[Pad]\nMultitapPort1 = false\nMultitapPort2 = false\nPointerXScale = 8\n\n[Pad1]\n" +
    (pad1 ?? "Type = DualShock2\nInvertL = 0\nDeadzone = 0\nAxisScale = 1.33\nLargeMotorScale = 1\nPressureModifier = 0.5\nUp = Keyboard/Up\nCross = Keyboard/K\nLUp = Keyboard/W\nMacro1 = Keyboard/M\nMacro1Binds = Cross\nPressure = Keyboard/P\n") +
    "\n[Pad2]\n" + pad2 + "\n[Pad3]\nType = None\n\n[USB1]\nType = None\n\n[USB2]\nType = None\n\n[GameList]\nRecursivePaths = E:\\Synthetic\\Games\n" + extra;
string Crlf(string s) => s.Replace("\n", "\r\n");
Dictionary<string, string> Section(string text, string section)
{
    var r = new Dictionary<string, string>(); string? s = null;
    foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
    {
        if (raw.StartsWith('[')) { s = raw.Trim('[', ']'); continue; }
        int eq = raw.IndexOf(" ="); if (s == section && eq > 0) r[raw[..eq]] = raw[(eq + 2)..].Trim(' ');
    }
    return r;
}
void Refused(string name, string reasonStart, string text, IReadOnlyList<PerGameFile>? perGame = null) => Check(name, () =>
{
    var q = Pcsx2Session.Qualify(text, perGame ?? []);
    Require(!q.Ok && q.Reason.StartsWith(reasonStart), $"expected {reasonStart}, got {(q.Ok ? "qualified" : q.Reason)}");
});
PerGameFile Game(string text, string name = "SLUS-20062_ABCD1234.ini") => new(name, Encoding.UTF8.GetBytes(text));
const string GameFix = "[EmuCore/Gamefixes]\nEETimingHack = true\n";

// --- Qualification (3.1) ---
Check("owner-shape-qualifies", () => Require(Pcsx2Session.Qualify(Owner(), []).Ok && Pcsx2Session.Qualify(Crlf(Owner()), []).Ok, "the owner's file shape did not qualify"));
Check("per-game-gamefix-qualifies", () => Require(Pcsx2Session.Qualify(Owner(), [Game(GameFix)]).Ok, "a per-game game fix refused"));
foreach (var (name, text) in new[]
{
    ("memorycards-trailing-tab", Owner().Replace("MemoryCards = memcards", "MemoryCards = memcards\t")),
    ("gamesettings-tab-only", Owner(folders: "GameSettings = \t\n")),
    ("folder-leading-tab", Owner(folders: "MemoryCards = \tmemcards\n")),
    ("folder-embedded-tab", Owner(folders: "MemoryCards = mem\tcards\n")),
    ("hotkey-tab-before-chord", Owner().Replace("Keyboard/Alt & Keyboard/Return", "Keyboard/Alt\t & Keyboard/Return")),
    ("hotkey-tab-before-key", Owner().Replace("Keyboard/F8", "\tKeyboard/F8")),
    ("uninterpreted-value-tab", Owner(extra: "[Other]\nValue = a\tb\n")),
})
{
    foreach (var (ending, owner) in new[] { ("lf", text), ("crlf", Crlf(text)) })
    {
        Refused("tabbed-setting-" + name + "-" + ending, "tab-in-setting", owner);
        Check("tabbed-setting-not-built-" + name + "-" + ending, () =>
        {
            bool threw = false; try { Pcsx2Session.Build(owner, [0], OwnerRoot, CopyRoot); } catch (InvalidOperationException) { threw = true; }
            Require(threw && Pcsx2Session.OwnerGameSettings(owner, OwnerRoot) == null, "a tabbed setting was built or resolved");
        });
    }
}
Refused("per-game-tabbed-value", "per-game-tab-in-setting", Owner(), [Game(GameFix.Replace("true", "true\t"))]);
Refused("per-game-tab-only-folder", "per-game-tab-in-setting", Owner(), [Game("[Folders]\nGameSettings = \t\n")]);
Check("tabs-in-comments-stay-comments", () =>
{
    var owner = Owner(extra: "; comment\twith = tab\n# comment\twith = tab\n");
    Require(Pcsx2Session.Qualify(owner, [Game("; comment\twith = tab\n" + GameFix)]).Ok, "a tab in a comment refused");
    Require(Pcsx2Session.Build(owner, [0], OwnerRoot, CopyRoot).Contains("; comment\twith = tab"), "a comment changed");
});
Refused("lone-cr", "line-endings", Owner().Replace("[UI]\n", "[UI]\r"));
Refused("mixed-line-endings", "line-endings", Crlf(Owner()) + "[Extra]\nA = 1\n");
Refused("line-outside-grammar", "outside-grammar", Owner(extra: "  Indented = 1\n"));
Refused("no-space-before-equals", "outside-grammar", Owner(extra: "[X]\nA=1\n"));
Refused("setting-before-any-section", "outside-grammar", "A = 1\n" + Owner());
Refused("repeated-section", "repeated-section", Owner(extra: "[pad1]\nType = None\n"));
Refused("repeated-key", "repeated-key", Owner(extra: "[X]\nA = 1\na = 2\n"));
Refused("input-profile-in-base", "input-profile", Owner(extra: "[EmuCore2]\nInputProfileName = Mine\n"));
Check("empty-input-profile-in-base-is-none", () => Require(Pcsx2Session.Qualify(Owner(extra: "[EmuCore2]\nInputProfileName = \n"), []).Ok, "an empty profile name refused"));
Refused("pad1-type-omitted", "pad-type:Pad1", Owner(pad1: "Up = Keyboard/Up\n"));
Refused("pad1-type-unknown", "pad-type:Pad1", Owner(pad1: "Type = Guitar\n"));
Refused("pad1-type-case-variant", "pad-type:Pad1", Owner(pad1: "Type = dualshock2\n"));
Refused("pad2-type-omitted", "pad-type:Pad2", Owner(pad2: "Deadzone = 0\n"));
Refused("pad3-active", "pad-type:Pad3", Owner().Replace("[Pad3]\nType = None", "[Pad3]\nType = DualShock2"));
Refused("pad3-type-omitted", "pad-type:Pad3", Owner().Replace("[Pad3]\nType = None", "[Pad3]\nDeadzone = 0"));
Refused("multitap-1", "multitap", Owner().Replace("MultitapPort1 = false", "MultitapPort1 = true"));
Refused("multitap-2-not-boolean", "multitap", Owner().Replace("MultitapPort2 = false", "MultitapPort2 = 1"));
Check("multitap-omitted-is-false", () => Require(Pcsx2Session.Qualify(Owner().Replace("MultitapPort1 = false\nMultitapPort2 = false\n", ""), []).Ok, "omitted multitap keys refused"));
Refused("usb-device", "usb-device", Owner().Replace("[USB1]\nType = None", "[USB1]\nType = guncon2"));
Check("usb-type-omitted-is-none", () => Require(Pcsx2Session.Qualify(Owner().Replace("[USB2]\nType = None\n", "[USB2]\n"), []).Ok, "an omitted USB type refused"));
Refused("controller-hotkey", "controller-hotkey", Owner().Replace("Screenshot = Keyboard/F8", "Screenshot = XInput-0/Back"));
Refused("chord-with-one-controller-token", "controller-hotkey", Owner().Replace("Keyboard/Alt & Keyboard/Return", "Keyboard/Alt & SDL-0/Start"));
Check("pointer-hotkey-qualifies", () => Require(Pcsx2Session.Qualify(Owner().Replace("Screenshot = Keyboard/F8", "Screenshot = Pointer-0/LeftButton"), []).Ok, "a pointer hotkey refused"));
Refused("empty-folder-value", "folder-empty:Bios", Owner().Replace("Bios = bios", "Bios = "));
Refused("per-game-pad-section", "per-game-input", Owner(), [Game(GameFix + "[Pad1]\nType = DualShock2\n")]);
Refused("per-game-usb-section", "per-game-input", Owner(), [Game("[USB1]\nType = None\n")]);
Refused("per-game-hotkeys", "per-game-input", Owner(), [Game("[Hotkeys]\nScreenshot = Keyboard/F8\n")]);
Refused("per-game-input-sources", "per-game-input", Owner(), [Game("[inputsources]\nXInput = true\n")]);
Refused("per-game-multitap-via-pad", "per-game-input", Owner(), [Game("[Pad]\nMultitapPort1 = true\n")]);
Refused("per-game-input-profile", "input-profile", Owner(), [Game("[EmuCore]\nInputProfileName = Racing\n")]);
Refused("per-game-unreadable", "per-game-unreadable", Owner(), [new("x.ini", null)]);
Refused("per-game-not-utf8", "per-game-unreadable", Owner(), [new("x.ini", [0xC3, 0x28])]);
Refused("per-game-malformed", "per-game-outside-grammar", Owner(), [Game("not ini\n")]);
Refused("per-game-reparse-point", "per-game-not-regular", Owner(), [new("x.ini", Encoding.UTF8.GetBytes(GameFix), RegularFile: false)]);
// A name the adapter interprets, spelled with other letter case, refuses (Qualify and Build would disagree).
foreach (var (name, owner) in new[]
{
    ("section-pad1", Owner().Replace("[Pad1]", "[pad1]")), ("section-pad2", Owner().Replace("[Pad2]", "[PAD2]")), ("section-pad", Owner().Replace("[Pad]\n", "[pad]\n")),
    ("section-pad3", Owner().Replace("[Pad3]", "[pad3]")), ("section-folders", Owner().Replace("[Folders]", "[folders]")),
    ("section-input-sources", Owner().Replace("[InputSources]", "[inputsources]")), ("section-usb1", Owner().Replace("[USB1]", "[usb1]")),
    ("section-hotkeys", Owner().Replace("[Hotkeys]", "[hotkeys]")), ("section-pad8-added", Owner(extra: "[pad8]\nType = None\n")),
    ("key-pad1-type", Owner(pad1: "type = DualShock2\n")), ("key-pad1-binding", Owner().Replace("Cross = Keyboard/K", "cross = Keyboard/K")),
    ("key-pad1-kept", Owner().Replace("Deadzone = 0\nAxisScale", "deadzone = 0\nAxisScale")), ("key-multitap", Owner().Replace("MultitapPort1 = false", "multitapport1 = false")),
    ("key-usb-type", Owner().Replace("[USB2]\nType = None", "[USB2]\nTYPE = None")),
    ("key-private-folder", Owner(folders: "Bios = bios\ngamesettings = gamesettings\n")), ("key-shared-folder", Owner(folders: "bios = bios\n")),
    ("key-input-profiles-folder", Owner(folders: "inputProfiles = inputprofiles\n")), ("key-xinput", Owner(inputs: "Keyboard = true\nxinput = false\n")),
})
{
    Refused("noncanonical-" + name, "noncanonical-name", owner);
    Check("noncanonical-" + name + "-not-built", () =>
    {
        bool threw = false; try { Pcsx2Session.Build(owner, [0], OwnerRoot, CopyRoot); } catch (InvalidOperationException) { threw = true; }
        Require(threw && Pcsx2Session.OwnerGameSettings(owner, OwnerRoot) == null, "a non-canonical name was built or resolved");
    });
}
Check("names-outside-the-interpreted-sections-keep-their-case", () =>
{
    // PCSX2 itself writes [Filenames] BIOS; other sections are copied as they are.
    string owner = Owner(extra: "[Filenames]\nBIOS = scph39001.bin\n\n[EmuCore/GS]\ntype = 1\n");
    Require(Pcsx2Session.Qualify(owner, []).Ok, "a name outside the interpreted sections refused");
    string s = Pcsx2Session.Build(owner, [0], OwnerRoot, CopyRoot);
    Require(Section(s, "Filenames")["BIOS"] == "scph39001.bin" && Section(s, "EmuCore/GS")["type"] == "1", "a name outside the interpreted sections changed");
});
Check("per-game-input-section-in-any-case-still-refuses-as-input", () =>
{
    foreach (string g in new[] { "[pad1]\nType = DualShock2\n", "[INPUTSOURCES]\nXInput = true\n", "[hotkeys]\nScreenshot = Keyboard/F8\n" })
    { var q = Pcsx2Session.Qualify(Owner(), [Game(g)]); Require(!q.Ok && q.Reason == "per-game-input", "per-game " + g.Split('\n')[0] + ": " + q.Reason); }
});

// --- Arguments (3.5) ---
Check("arguments-whitelist", () =>
{
    Require(Pcsx2Session.ArgumentsQualified(["-fullscreen", "-nogui", @"E:\Games\Synthetic.iso"]), "LaunchBox's arguments refused");
    foreach (var a in new[] { new[] { "-nogui", "-fullscreen", "x.iso" }, ["-fullscreen", "-nogui"], ["-fullscreen", "-nogui", "-batch"], ["-fullscreen", "-nogui", "x.iso", "-statefile"], ["-fullscreen", "-nogui", ""] })
        Require(!Pcsx2Session.ArgumentsQualified(a), "accepted: " + string.Join(' ', a));
});

// --- Build (3.3, 3.4) ---
Check("folders-all-sixteen-absolute", () =>
{
    var f = Section(Pcsx2Session.Build(Owner(folders: "Bios = bios\nMemoryCards = E:\\Elsewhere\\cards\nGameSettings = gamesettings\n"), [0], OwnerRoot, CopyRoot), "Folders");
    Require(f.Count == 16 && f.Values.All(Path.IsPathFullyQualified), "not 16 absolute folder keys: " + string.Join("; ", f));
    Require(f["Bios"] == OwnerRoot + @"\bios" && f["MemoryCards"] == @"E:\Elsewhere\cards" && f["Covers"] == OwnerRoot + @"\covers" && f["UserResources"] == OwnerRoot + @"\resources", "shared folders not resolved against the owner's folder");
    foreach (var (k, folder) in new[] { ("GameSettings", "gamesettings"), ("InputProfiles", "inputprofiles"), ("DebuggerLayouts", "debuggerlayouts"), ("DebuggerSettings", "debuggersettings") })
        Require(f[k] == CopyRoot + @"\session\" + folder, k + " is not private: " + f[k]);
});
Check("relative-folder-with-parent-resolves-as-pcsx2-does", () =>
{
    var f = Section(Pcsx2Session.Build(Owner(folders: "Savestates = ..\\shared\\states\n"), [0], OwnerRoot, CopyRoot), "Folders");
    Require(f["Savestates"] == @"D:\Synthetic\shared\states", f["Savestates"]);
});
Check("one-player-join-order", () =>
{
    string s = Pcsx2Session.Build(Owner(), [2], OwnerRoot, CopyRoot);
    var p1 = Section(s, "Pad1"); var p2 = Section(s, "Pad2");
    Require(p1["Type"] == "DualShock2" && p1["Cross"] == "XInput-2/A" && p1["L2"] == "XInput-2/+LeftTrigger" && p1["LUp"] == "XInput-2/-LeftY" && p1["Analog"] == "XInput-2/Guide" && p1["LargeMotor"] == "XInput-2/LargeMotor", "Pad1 layout wrong");
    Require(p1["Deadzone"] == "0" && p1["AxisScale"] == "1.33" && p1["PressureModifier"] == "0.5" && p1["InvertL"] == "0", "allow-listed keys not kept");
    Require(!p1.ContainsKey("Macro1") && !p1.ContainsKey("Macro1Binds") && !p1.ContainsKey("Pressure") && !p1.Values.Any(v => v.StartsWith("Keyboard/")), "a macro, Pressure or keyboard binding survived");
    Require(p2.Count == 1 && p2["Type"] == "DualShock2", "the unjoined Pad2 kept a binding or lost its Type: " + string.Join("; ", p2));
    Require(Section(s, "InputSources")["XInput"] == "true" && Section(s, "InputSources")["SDL"] == "true", "XInput not on, or SDL changed");
});
Check("two-players-both-join-orders", () =>
{
    foreach (var joined in new[] { new[] { 1, 0 }, [0, 1], [3, 2] })
    {
        string s = Pcsx2Session.Build(Owner(), joined, OwnerRoot, CopyRoot);
        var p1 = Section(s, "Pad1"); var p2 = Section(s, "Pad2");
        Require(p1["Start"] == $"XInput-{joined[0]}/Start" && p2["Start"] == $"XInput-{joined[1]}/Start" && p2["Type"] == "DualShock2", "join order not applied: " + string.Join(',', joined));
        Require(p1.Count(kv => kv.Value.StartsWith("XInput-")) == Pcsx2Session.Layout.Count && p2.Count(kv => kv.Value.StartsWith("XInput-")) == Pcsx2Session.Layout.Count, "incomplete layout");
    }
});
Check("joined-pad-qualified-as-none-is-connected", () =>
{
    var p2 = Section(Pcsx2Session.Build(Owner(pad2: "Type = None\nDeadzone = 0.1\n"), [0, 1], OwnerRoot, CopyRoot), "Pad2");
    Require(p2["Type"] == "DualShock2" && p2["Deadzone"] == "0.1" && p2["Cross"] == "XInput-1/A", "a joined pad qualified as None was not written as DualShock2");
});
Check("unjoined-pad-keeps-none", () => Require(Section(Pcsx2Session.Build(Owner(pad2: "Type = None\n"), [0], OwnerRoot, CopyRoot), "Pad2")["Type"] == "None", "the unjoined Pad2 type changed"));
Check("every-other-line-byte-identical", () =>
{
    foreach (string owner in new[] { Owner(), Crlf(Owner()) })
    {
        string s = Pcsx2Session.Build(owner, [0, 1], OwnerRoot, CopyRoot); string nl = owner.Contains("\r\n") ? "\r\n" : "\n";
        string Outside(string t)
        {
            var keep = new List<string>(); string? sec = null;
            foreach (string raw in t.Split(nl))
            {
                if (raw.StartsWith('[')) sec = raw.Trim('[', ']');
                bool mapped = sec is "Folders" or "Pad1" or "Pad2" || sec == "InputSources" && raw.StartsWith("XInput =");
                if (!mapped) keep.Add(raw);
            }
            return string.Join(nl, keep);
        }
        Require(Outside(s) == Outside(owner), "a line outside the mapped sections changed");
        Require(s.EndsWith(nl) && (nl == "\n" ? !s.Contains('\r') : !s.Replace("\r\n", "").Contains('\n')), "line endings changed");
    }
});
Check("missing-sections-added", () =>
{
    string owner = Owner().Replace("[InputSources]\nKeyboard = true\nMouse = true\nSDL = true\nDInput = false\nXInput = false\n", "");
    string folderless = owner.Replace("[Folders]\n", "[NotFolders]\n");
    string s = Pcsx2Session.Build(folderless, [0], OwnerRoot, CopyRoot);
    Require(Section(s, "InputSources")["XInput"] == "true" && Section(s, "Folders").Count == 16, "missing [InputSources] or [Folders] not added");
});
Check("xinput-added-when-omitted", () =>
{
    string s = Pcsx2Session.Build(Owner(inputs: "Keyboard = true\n"), [0], OwnerRoot, CopyRoot);
    Require(Section(s, "InputSources")["XInput"] == "true" && Section(s, "InputSources")["Keyboard"] == "true", "XInput not added");
});
Check("build-refuses-outside-the-shape", () =>
{
    foreach (var (owner, joined) in new (string, int[])[] { (Owner(), []), (Owner(), [0, 1, 2, 3, 0]), (Owner(), [0, 1, 2, 3, 1]), (Owner(), [1, 1]), (Owner(), [0, 1, 2, 2]), (Owner(), [4]), (Owner(pad1: "Type = Guitar\n"), [0]) })
    {
        bool threw = false; try { Pcsx2Session.Build(owner, joined, OwnerRoot, CopyRoot); } catch (InvalidOperationException) { threw = true; }
        Require(threw, "built for " + string.Join(',', joined));
    }
    bool rel = false; try { Pcsx2Session.Build(Owner(), [0], "PCSX2", CopyRoot); } catch (ArgumentException) { rel = true; }
    Require(rel, "a relative owner root was accepted");
});
// --- Multitap (contract r4 3.2 and 3.4) ---
Check("multitap-slots-are-pcsx2-pad-indices", () =>
    Require(Pcsx2Session.PlayerSections(1).SequenceEqual([1, 2]) && Pcsx2Session.PlayerSections(2).SequenceEqual([1, 2])
        && Pcsx2Session.PlayerSections(3).SequenceEqual([1, 3, 4, 5]) && Pcsx2Session.PlayerSections(4).SequenceEqual([1, 3, 4, 5]), "slots are not 1A/2A, or 1A-1D = Pad1, Pad3, Pad4, Pad5"));
Check("three-players-turn-on-port-1-multitap", () =>
{
    string s = Pcsx2Session.Build(Owner(), [2, 0, 1], OwnerRoot, CopyRoot);
    var pad = Section(s, "Pad");
    Require(pad["MultitapPort1"] == "true" && pad["MultitapPort2"] == "false" && pad["PointerXScale"] == "8", "port 1 multitap not on, or port 2 or another [Pad] key changed");
    Require(Section(s, "Pad1")["Start"] == "XInput-2/Start" && Section(s, "Pad3")["Start"] == "XInput-0/Start" && Section(s, "Pad4")["Start"] == "XInput-1/Start", "players not in 1A, 1B, 1C in join order");
    foreach (string n in new[] { "Pad1", "Pad3", "Pad4" })
        Require(Section(s, n)["Type"] == "DualShock2" && Section(s, n).Count(kv => kv.Value.StartsWith("XInput-")) == Pcsx2Session.Layout.Count, n + " not a full DualShock2 layout");
    var p2 = Section(s, "Pad2"); var p5 = Section(s, "Pad5");
    Require(p2.Count == 1 && p2["Type"] == "DualShock2" && p5.Count == 1 && p5["Type"] == "None", "an unjoined 2A or 1D kept a binding or changed type: " + string.Join("; ", p2) + " | " + string.Join("; ", p5));
    Require(!s.Contains("Keyboard/K") && !s.Contains("Macro1"), "an owner binding survived");
});
Check("four-players-fill-1a-to-1d", () =>
{
    string s = Pcsx2Session.Build(Owner(), [3, 1, 0, 2], OwnerRoot, CopyRoot);
    Require(Section(s, "Pad")["MultitapPort1"] == "true", "multitap not on");
    Require(Section(s, "Pad1")["Cross"] == "XInput-3/A" && Section(s, "Pad3")["Cross"] == "XInput-1/A" && Section(s, "Pad4")["Cross"] == "XInput-0/A" && Section(s, "Pad5")["Cross"] == "XInput-2/A", "players not in 1A-1D in join order");
    Require(!Section(s, "Pad2").ContainsKey("Cross") && Section(s, "Pad6").Count == 0, "2A was bound, or a section past 1D was written");
});
Check("two-players-leave-multitap-off", () =>
{
    string s = Pcsx2Session.Build(Owner(), [0, 1], OwnerRoot, CopyRoot);
    Require(Section(s, "Pad")["MultitapPort1"] == "false" && !s.Contains("[Pad4]") && Section(s, "Pad3")["Type"] == "None", "a two-player session changed multitap or the multitap slots");
});
Check("multitap-adds-missing-sections", () =>
{
    string owner = Owner().Replace("[Pad]\nMultitapPort1 = false\nMultitapPort2 = false\nPointerXScale = 8\n\n", "").Replace("[Pad3]\nType = None\n\n", "");
    Require(!owner.Contains("[Pad]") && !owner.Contains("[Pad3]") && Pcsx2Session.Qualify(owner, []).Ok, "fixture still has [Pad] or [Pad3], or does not qualify");
    foreach (string o in new[] { owner, Crlf(owner) })
    {
        string s = Pcsx2Session.Build(o, [0, 1, 2], OwnerRoot, CopyRoot);
        Require(Section(s, "Pad")["MultitapPort1"] == "true" && Section(s, "Pad3")["Start"] == "XInput-1/Start" && Section(s, "Pad4")["Start"] == "XInput-2/Start"
            && Section(s, "Pad5")["Type"] == "None", "missing [Pad], [Pad3], [Pad4] or [Pad5] not added");
        Require(o.Contains("\r\n") ? !s.Replace("\r\n", "").Contains('\n') : !s.Contains('\r'), "line endings changed");
    }
});
Check("multitap-keeps-every-other-line", () =>
{
    foreach (string owner in new[] { Owner(), Crlf(Owner()) })
    {
        string s = Pcsx2Session.Build(owner, [0, 1, 2, 3], OwnerRoot, CopyRoot); string nl = owner.Contains("\r\n") ? "\r\n" : "\n";
        string Outside(string t)
        {
            var keep = new List<string>(); string? sec = null;
            foreach (string raw in t.Split(nl))
            {
                if (raw.StartsWith('[')) sec = raw.Trim('[', ']');
                bool mapped = sec is "Folders" or "Pad1" or "Pad2" or "Pad3" or "Pad4" or "Pad5" || sec == "InputSources" && raw.StartsWith("XInput =") || sec == "Pad" && raw.StartsWith("MultitapPort1 =");
                if (!mapped) keep.Add(raw);
            }
            return string.Join(nl, keep);
        }
        // The added sections come last, so the file's final line ending is compared on its own.
        Require(Outside(s[..^nl.Length]) == Outside(owner[..^nl.Length]) && s.EndsWith(nl), "a line outside the mapped sections changed");
    }
});
Check("layout-is-pcsx2-automatic-mapping", () =>
{
    var l = Pcsx2Session.Layout.ToDictionary(x => x.Key, x => x.Input);
    Require(l.Count == 27 && l["Cross"] == "A" && l["Circle"] == "B" && l["Square"] == "X" && l["Triangle"] == "Y" && l["Select"] == "Back" && l["L1"] == "LeftShoulder"
        && l["R2"] == "+RightTrigger" && l["RLeft"] == "-RightX" && l["SmallMotor"] == "SmallMotor" && !l.ContainsKey("Pressure"), "layout differs from the contract table");
});
Check("pin-is-2.7.182", () => Require(Pcsx2Session.PinnedSha256 == "6143327B5A6727FA30718DE65925EB4ED4350B78702F0D5EB2087B762216A08E", "pin is not the probed 2.7.182 exe"));

Console.WriteLine($"Cases={passed + failed} Passed={passed} Failed={failed}; M/pure rules on synthetic settings text; no emulator, disk or controller");
return failed == 0 ? 0 : 1;
