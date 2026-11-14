using ControllerLobby.DuckStation;

// Pure checks for the DuckStation launch-copy rules. Synthetic settings text only; no emulator, no disk, no controllers.
int passed = 0, failed = 0;
void Require(bool ok, string why) { if (!ok) throw new InvalidOperationException(why); }
void Check(string name, Action body) { try { body(); passed++; Console.WriteLine("PASS " + name); } catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e.Message); } }

const string Root = @"D:\Synthetic\Documents\DuckStation", Copy = @"D:\SyntheticLaunchBox\Emulators\Duck\ControllerLobby.BigBox\DuckStationCopy";
string Pad(int n, string device, string type = "AnalogController") => type == "None" ? $"[Pad{n}]\nType = None\n" :
    $"[Pad{n}]\nType = {type}\nUp = {device}/DPadUp\nCross = {device}/A\nLLeft = {device}/-LeftX\nSmallMotor = {device}/SmallMotor\nAnalogDeadzone = 0.1\n";
string Settings(string pad1 = "XInput-0", string pad2 = "XInput-1", string pad1Type = "AnalogController", string pad2Type = "AnalogController",
    string xinput = "true", string multitap = "Disabled", string extra = "", string pad3 = "None") =>
    "[Main]\nSettingsVersion = 3\nConfirmPowerOff = true\n\n[BIOS]\nSearchDirectory = ..\\..\\Games\\BIOS\n\n" +
    "[MemoryCards]\nCard1Type = PerGameTitle\nCard1Path = sub\\card.mcd\nDirectory = memcards\n\n" +
    $"[ControllerPorts]\nMultitapMode = {multitap}\n\n[Folders]\nCache = cache\nGameSettings = gamesettings\nInputProfiles = inputprofiles\nSaveStates = savestates\nUserResources = resources\n\n" +
    $"[InputSources]\nSDL = true\nXInput = {xinput}\n\n" + Pad(1, pad1, pad1Type) + "\n" + Pad(2, pad2, pad2Type) + "\n" + (pad3 == "None" ? Pad(3, "", "None") : Pad(3, pad3)) +
    "\n[Hotkeys]\nOpenPauseMenu = XInput-0/Guide\n\n[AutoUpdater]\nCheckAtStartup = true\n" + extra;
OwnerFile File(string text) => new("settings.ini", FileState.Present, text);
OwnerFile NoCustom = new("custom_properties.ini", FileState.Absent);
Qualification Q(string text, IEnumerable<OwnerFile>? perGame = null, OwnerFile? custom = null) => DuckStationSession.Qualify(File(text), perGame ?? [], custom ?? NoCustom);
void Refused(string name, string reason, Func<Qualification> run) => Check(name, () => { var q = run(); Require(!q.Ok && q.Reason == reason && q.Ports == 0, $"expected {reason}, got {q.Status}/{q.Reason}"); });
Dictionary<string, string> Values(string text, string section)
{
    var result = new Dictionary<string, string>(); string? current = null;
    foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
    {
        if (raw.StartsWith('[')) { current = raw.Trim('[', ']'); continue; }
        int eq = raw.IndexOf('='); if (current == section && eq > 0) result[raw[..eq].Trim()] = raw[(eq + 1)..].Trim();
    }
    return result;
}
string Build(string text, params int[] joined) => DuckStationSession.Build(text, Q(text), joined, Root, Copy);

Check("owner-shape-two-pads-is-mappable", () => { var q = Q(Settings()); Require(q.Ok && q.PadSlots.SequenceEqual([0, 1]) && q.Ports == 2, q.Reason); });
Check("one-pad-is-one-port", () => { var q = Q(Settings(pad2Type: "None")); Require(q.Ok && q.Ports == 1 && q.PadSlots.SequenceEqual([0]), q.Reason); });
Refused("xinput-source-off", "xinput-off", () => Q(Settings(xinput: "false")));
Refused("pad-on-sdl", "pad-not-single-xinput", () => Q(Settings(pad1: "SDL-0")));
Refused("pad-on-keyboard", "pad-not-single-xinput", () => Q(Settings(pad2: "Keyboard")));
Refused("pad-mixes-two-xinput-slots", "pad-not-single-xinput", () => Q(Settings().Replace("Cross = XInput-0/A", "Cross = XInput-2/A")));
Refused("pad-chord-with-keyboard", "pad-not-single-xinput", () => Q(Settings().Replace("Cross = XInput-0/A", "Cross = XInput-0/A & Keyboard/K")));
Refused("two-pads-on-one-slot", "pads-share-slot", () => Q(Settings(pad2: "XInput-0")));
Refused("multitap-on", "multitap", () => Q(Settings(multitap: "Port1Only")));
Refused("pad3-active", "multitap", () => Q(Settings(pad3: "XInput-2")));
Refused("pad2-without-pad1", "pad1-inactive", () => Q(Settings(pad1Type: "None")));
Refused("unreadable-settings", "settings-unreadable", () => DuckStationSession.Qualify(new("settings.ini", FileState.Unreadable), [], NoCustom));
Refused("not-a-settings-file", "settings-unreadable", () => Q("[Main]\nSettingsVersion = 3\n"));
Refused("unknown-path-setting-fails-closed", "unsupported-path:TextureReplacements/DumpDirectory", () => Q(Settings(extra: "\n[TextureReplacements]\nDumpDirectory = D:\\dumps\n")));
Refused("game-list-path-set", "unsupported-path:GameList/RecursivePaths", () => Q(Settings(extra: "\n[GameList]\nRecursivePaths = D:\\Games\n")));
Refused("pcdrv-root-set", "unsupported-path:PCDrv/Root", () => Q(Settings(extra: "\n[PCDrv]\nRoot = D:\\pcdrv\n")));
Check("empty-optional-paths-stay-mappable", () => Require(Q(Settings(extra: "\n[PIO]\nFlashImagePath = \n[PCDrv]\nRoot =\n[Logging]\nLogToFile = false\n")).Ok, "empty path keys refused"));

Check("per-game-harmless-file-stays-mappable", () => Require(Q(Settings(), [new("SLPS-01995.ini", FileState.Present, "[GPU]\nDeinterlacingMode = Progressive\n")]).Ok, "GPU-only file refused"));
Check("per-game-dormant-pad-and-false-flag-stay-mappable", () => Require(
    Q(Settings(), [new("A.ini", FileState.Present, "[Pad1]\nType = DigitalController\n"), new("B.ini", FileState.Present, "[ControllerPorts]\nUseGameSettingsForController = false\nInputProfileName = \n[Pad1]\nType = None\n")]).Ok, "dormant override refused"));
Refused("per-game-flag-true", "per-game-controller-override", () => Q(Settings(), [new("A.ini", FileState.Present, "[ControllerPorts]\nUseGameSettingsForController = true\n")]));
Refused("per-game-input-profile", "per-game-controller-override", () => Q(Settings(), [new("A.ini", FileState.Present, "[ControllerPorts]\nInputProfileName = probe\n")]));
Refused("per-game-unreadable", "per-game-unreadable", () => Q(Settings(), [new("A.ini", FileState.Unreadable)]));
Check("custom-properties-absent-or-empty-stays-mappable", () => Require(Q(Settings()).Ok && Q(Settings(), custom: new("custom_properties.ini", FileState.Present, "  \n")).Ok, "absent or empty custom properties refused"));
Refused("custom-properties-non-empty", "custom-titles", () => Q(Settings(), custom: new("custom_properties.ini", FileState.Present, "[D:\\g.cue]\nTitle = X\n")));
Refused("custom-properties-unreadable", "custom-titles", () => Q(Settings(), custom: new("custom_properties.ini", FileState.Unreadable)));
Check("custom-properties-appearing-before-commit-changes-the-answer", () =>
{
    string text = Settings(); Require(Q(text).Ok, "pre-lobby"); // read-only look before the lobby
    Require(!Q(text, custom: new("custom_properties.ini", FileState.Present, "[D:\\g.cue]\nRegion = NTSC-J\n")).Ok, "commit-time recheck accepted a new custom properties file");
});

foreach (var (a, b) in new[] { (0, 1), (1, 0), (2, 0), (3, 1) })
    Check($"join-order-{a}-{b}", () =>
    {
        string built = Build(Settings(), a, b); var p1 = Values(built, "Pad1"); var p2 = Values(built, "Pad2");
        Require(p1.Where(v => v.Value.Contains('/')).All(v => v.Value.StartsWith($"XInput-{a}/")) && p2.Where(v => v.Value.Contains('/')).All(v => v.Value.StartsWith($"XInput-{b}/")), "pad sections do not follow join order");
        Require(p1["Type"] == "AnalogController" && p2["Type"] == "AnalogController" && p1["AnalogDeadzone"] == "0.1" && p1["LLeft"] == $"XInput-{a}/-LeftX", "pad section changed beyond the index");
    });
foreach (int slot in new[] { 0, 1, 2 })
    Check($"solo-on-slot-{slot}-leaves-port-2-inactive", () =>
    {
        string built = Build(Settings(), slot); var p1 = Values(built, "Pad1"); var p2 = Values(built, "Pad2");
        Require(p1["Cross"] == $"XInput-{slot}/A" && p2["Type"] == "None", "solo launch left port 2 active or port 1 on the wrong slot");
        // Port 2's binding lines are left as the owner wrote them; with Type = None no controller is attached to the port.
        Require(p2["Cross"] == "XInput-1/A", "inactive port's own lines were rewritten");
    });
Check("solo-with-one-active-pad", () => { string text = Settings(pad2Type: "None"); var p1 = Values(Build(text, 3), "Pad1"); Require(p1["Cross"] == "XInput-3/A", "single pad not remapped"); });
Check("more-players-than-ports-is-refused", () =>
{
    string one = Settings(pad2Type: "None");
    try { DuckStationSession.Build(one, Q(one), [0, 1], Root, Copy); } catch (InvalidOperationException) { return; }
    throw new InvalidOperationException("two players accepted on one port");
});
Check("non-mappable-settings-never-build", () =>
{
    string text = Settings(xinput: "false");
    try { DuckStationSession.Build(text, Q(text), [0], Root, Copy); } catch (InvalidOperationException) { return; }
    throw new InvalidOperationException("built a session file for a refused configuration");
});
Check("hotkeys-stay-on-their-original-slot", () => Require(Values(Build(Settings(), 1, 0), "Hotkeys")["OpenPauseMenu"] == "XInput-0/Guide", "hotkey followed the join order"));

Check("paths-follow-the-contract-table", () =>
{
    string built = Build(Settings(), 1, 0); var folders = Values(built, "Folders"); var cards = Values(built, "MemoryCards");
    Require(folders["Cache"] == Root + @"\cache" && folders["SaveStates"] == Root + @"\savestates" && folders["UserResources"] == Root + @"\resources", "shared folders not absolute under the owner data root");
    Require(folders["Covers"] == Root + @"\covers" && folders["Textures"] == Root + @"\textures" && folders.Count == 14, "folders left at their default were not written absolute: " + folders.Count);
    Require(folders["GameSettings"] == Copy + @"\session\gamesettings" && folders["InputProfiles"] == Copy + @"\session\inputprofiles", "per-game settings or input profiles not private to the copy");
    Require(cards["Directory"] == Root + @"\memcards" && cards["Card1Path"] == @"sub\card.mcd" && cards["Card1Type"] == "PerGameTitle", "memory-card rows wrong");
    Require(Values(built, "BIOS")["SearchDirectory"] == @"D:\Synthetic\Games\BIOS", "BIOS directory not resolved against the data root");
    Require(Values(built, "AutoUpdater")["CheckAtStartup"] == "false", "updater left on in the copy");
});
Check("absolute-owner-paths-are-kept", () =>
{
    string text = Settings().Replace("Directory = memcards", @"Directory = E:\Cards").Replace("SaveStates = savestates", @"SaveStates = E:\States");
    string built = Build(text, 0); Require(Values(built, "MemoryCards")["Directory"] == @"E:\Cards" && Values(built, "Folders")["SaveStates"] == @"E:\States", "absolute path rebased");
});
Check("missing-sections-get-explicit-paths", () =>
{
    string text = "[InputSources]\nXInput = true\n\n" + Pad(1, "XInput-0") + "\n" + Pad(2, "", "None");
    string built = Build(text, 2); var folders = Values(built, "Folders");
    Require(folders.Count == 14 && folders["GameSettings"].StartsWith(Copy) && Values(built, "MemoryCards")["Directory"] == Root + @"\memcards"
        && Values(built, "BIOS")["SearchDirectory"] == Root + @"\bios" && Values(built, "AutoUpdater")["CheckAtStartup"] == "false", "defaults not pinned to the owner data root");
});
Check("every-other-line-is-byte-identical", () =>
{
    string text = Settings(); var before = text.Split('\n'); var after = Build(text, 0, 1).Split('\n');
    // join order (0,1) equals the owner's own indices, so only path, private-folder and updater lines may differ.
    var changed = before.Where(l => !after.Contains(l)).ToArray();
    string[] allowed = ["SearchDirectory = ", "Directory = ", "Cache = ", "GameSettings = ", "InputProfiles = ", "SaveStates = ", "UserResources = ", "CheckAtStartup = "];
    Require(changed.All(l => allowed.Any(l.StartsWith)), "unexpected changed line: " + string.Join(" | ", changed.Where(l => !allowed.Any(l.StartsWith))));
    Require(after.Contains("ConfirmPowerOff = true") && after.Contains("Card1Path = sub\\card.mcd") && after.Contains("SDL = true"), "owner line lost");
});
Check("crlf-settings-stay-crlf", () => { string built = Build(Settings().Replace("\n", "\r\n"), 1, 0); Require(built.Contains("\r\n") && !built.Replace("\r\n", "").Contains('\n') && !built.Contains("\r\r"), "line endings changed"); });
Check("relative-roots-are-refused", () =>
{
    string text = Settings();
    try { DuckStationSession.Build(text, Q(text), [0], "relative", Copy); } catch (ArgumentException) { return; }
    throw new InvalidOperationException("relative data root accepted");
});

Check("data-root-rule", () =>
{
    const string emu = @"D:\SyntheticLaunchBox\Emulators\Duck";
    string? With(params string[] present) => DuckStationSession.DataRoot(emu, p => present.Contains(Path.GetFileName(p)), () => @"D:\Synthetic\Documents");
    Require(With("portable.txt") == emu && With("settings.ini") == emu, "marker or settings beside the exe did not select the emulator folder");
    Require(With() == @"D:\Synthetic\Documents\DuckStation", "Documents layout not selected");
    Require(DuckStationSession.DataRoot(emu, _ => false, () => null) == null && DuckStationSession.DataRoot(emu, _ => false, () => "") == null, "missing Documents folder not refused");
});
Check("owner-folder-resolution", () =>
{
    string text = Settings().Replace("GameSettings = gamesettings", @"GameSettings = E:\DuckData\gs").Replace("InputProfiles = inputprofiles", "InputProfiles = ");
    Require(DuckStationSession.OwnerFolder(text, Root, "GameSettings") == @"E:\DuckData\gs" && DuckStationSession.OwnerFolder(text, Root, "InputProfiles") == Root + @"\inputprofiles"
        && DuckStationSession.OwnerFolder(Settings(), Root, "GameSettings") == Root + @"\gamesettings" && DuckStationSession.OwnerFolder("[Main]\nA = 1\n", Root, "SaveStates") == Root + @"\savestates"
        && DuckStationSession.OwnerFolder(Settings(), Root, "NotAFolder") == null, "owner folder not resolved as DuckStation does");
});
Check("program-inventory-is-an-allow-list", () =>
{
    string[] program = ["duckstation-qt-x64-ReleaseLTCG.exe", "SDL3.dll", "qt.conf", "LICENSE.txt", @"QtPlugins\platforms\qwindows.dll", @"resources\gamedb.yaml", @"translations\duckstation-qt_de.qm"];
    string[] never = ["updater.exe", "settings.ini", "portable.txt", "playtime.dat", "duckstation.log", "custom_properties.ini", @"memcards\a.mcd", @"savestates\a.sav",
        @"ControllerLobby.BigBox\ControllerLobby.BigBox.exe", @"ControllerLobby.BigBox\DuckStationCopy\SDL3.dll", @"..\escape.dll", @"cache\x.bin", "crash-2026-01-01-00-00-00-000.dmp"];
    Require(program.All(DuckStationSession.IsProgramFile), "program file missing from the inventory: " + string.Join(",", program.Where(p => !DuckStationSession.IsProgramFile(p))));
    Require(!never.Any(DuckStationSession.IsProgramFile), "data or foreign file in the inventory: " + string.Join(",", never.Where(DuckStationSession.IsProgramFile)));
});
Check("portable-source-with-colliding-user-folder-is-refused", () =>
{
    const string emu = @"D:\SyntheticLaunchBox\Emulators\Duck";
    bool Collides(string text) => DuckStationSession.HasProgramFolderCollision(text, emu);
    string distinct = Settings().Replace("UserResources = resources", "UserResources = userresources");
    Require(Collides(Settings()), "UserResources = resources inside the program folder not detected");
    Require(Collides("[Folders]\nCache = cache\n"), "default UserResources not detected");
    Require(Collides(Settings().Replace("UserResources = resources", "UserResources = ")), "explicit empty UserResources (default resources) not detected");
    Require(Collides(Settings().Replace("UserResources = resources", @"UserResources = D:\SyntheticLaunchBox\Emulators\Duck\resources\user")), "absolute path inside a program folder not detected");
    Require(Collides(distinct.Replace("Cache = cache", @"Cache = QtPlugins\cache")), "data folder inside QtPlugins not detected");
    Require(Collides(distinct.Replace("Cache = cache", "Cache = .")), "data folder equal to the emulator folder not detected");
    Require(Collides(distinct.Replace("Directory = memcards", @"Directory = translations")), "memory cards inside translations not detected");
    Require(!Collides(distinct), "distinct user folder refused");
    Require(!Collides(Settings().Replace("UserResources = resources", @"UserResources = E:\DuckData\resources")), "external absolute user folder refused");
});

// DuckStation reads INI names without case. Anything the adapter would read differently is refused.
Refused("lowercase-section-and-key-override", "per-game-controller-override", () => Q(Settings(), [new("A.ini", FileState.Present, "[controllerports]\nusegamesettingsforcontroller = true\n")]));
Refused("override-flag-with-unusual-value", "per-game-controller-override", () => Q(Settings(), [new("A.ini", FileState.Present, "[ControllerPorts]\nUseGameSettingsForController = 1\n")]));
Refused("lowercase-input-profile-name", "per-game-controller-override", () => Q(Settings(), [new("A.ini", FileState.Present, "[controllerports]\ninputprofilename = probe\n")]));
Refused("lowercase-pcdrv-root", "ambiguous-setting", () => Q(Settings(extra: "\n[pcdrv]\nroot = C:\\SyntheticShare\n")));
Refused("lowercase-known-key", "ambiguous-setting", () => Q(Settings().Replace("MultitapMode = Disabled", "multitapmode = Port1Only")));
Refused("lowercase-pad-section", "ambiguous-setting", () => Q(Settings() + "\n[pad1]\nCross = SDL-0/A\n"));
Refused("only-lowercase-pad1-section", "ambiguous-setting", () => Q(Settings().Replace("[Pad1]", "[pad1]")));
// Minimal fixture: no canonical [Folders] at all, so without the line-grammar guard the file qualifies and the hidden
// relative save path survives beside the appended section.
const string FoldersBlock = "[Folders]\nCache = cache\nGameSettings = gamesettings\nInputProfiles = inputprofiles\nSaveStates = savestates\nUserResources = resources\n\n";
Refused("indented-folders-header-with-custom-save-path", "ambiguous-setting", () => { Require(Settings().Contains(FoldersBlock), "fixture"); return Q(Settings().Replace(FoldersBlock, "") + "\n  [Folders]\nSaveStates=customstates\n"); });
Refused("per-game-override-on-lone-cr-lines", "per-game-controller-override", () => Q(Settings(), [new("A.ini", FileState.Present, "[ControllerPorts]\rUseGameSettingsForController=true\r")]));
Refused("per-game-profile-on-lone-cr-lines", "per-game-controller-override", () => Q(Settings(), [new("A.ini", FileState.Present, "[Misc]\nNote=x\r[ControllerPorts]\rInputProfileName=probe\n")]));
Refused("global-lone-cr-hides-folders-section", "ambiguous-setting", () => Q(Settings().Replace(FoldersBlock, "") + "\n[Misc]\nNote=hello\r[Folders]\rSaveStates=customstates\n"));
Refused("global-lone-cr-inside-a-value", "ambiguous-setting", () => Q(Settings().Replace("ConfirmPowerOff = true", "ConfirmPowerOff = true\rX = 1")));
Check("crlf-settings-with-no-lone-cr-stay-mappable", () => Require(Q(Settings().Replace("\n", "\r\n")).Ok, "CRLF file refused"));
Check("lone-cr-collision-source-is-refused", () => Require(DuckStationSession.HasProgramFolderCollision(Settings().Replace("UserResources = resources", "UserResources = userresources") + "\rX=1", @"D:\SyntheticLaunchBox\Emulators\Duck"), "lone CR accepted by the collision check"));
Refused("indented-section-header", "ambiguous-setting", () => Q(Settings().Replace("[BIOS]", "  [BIOS]")));
Refused("tab-indented-section-header", "ambiguous-setting", () => Q(Settings().Replace("[MemoryCards]", "\t[MemoryCards]")));
Refused("spaces-inside-section-brackets", "ambiguous-setting", () => Q(Settings().Replace("[Folders]", "[ Folders ]")));
Refused("whitespace-disguised-split-section", "ambiguous-setting", () => Q(Settings() + "\n [Pad1]\nTriangle = SDL-0/Y\n"));
Refused("trailing-space-after-section-header", "ambiguous-setting", () => Q(Settings().Replace("[Pad2]", "[Pad2] ")));
Refused("indented-key", "ambiguous-setting", () => Q(Settings().Replace("MultitapMode = Disabled", "  MultitapMode = Port1Only")));
Refused("multi-line-value", "ambiguous-setting", () => Q(Settings(extra: "\n[Misc]\nNote = <<<END\n[Folders]\nEND\n")));
Refused("stray-text-line", "ambiguous-setting", () => Q(Settings(extra: "\nnot a setting\n")));
Check("comments-blank-lines-and-compact-assignments-stay-mappable", () => Require(Q(Settings(extra: "\n; note\n# note\n   \n[Misc]\nA=1\nB =2\nC.D = x\n")).Ok, "ordinary INI forms refused"));
Check("indented-header-never-reaches-build", () =>
{
    string text = Settings() + "\n  [Folders]\nSaveStates=customstates\n";
    try { DuckStationSession.Build(text, Q(text), [0], Root, Copy); } catch (InvalidOperationException) { return; }
    throw new InvalidOperationException("built a session file whose folders DuckStation would read differently");
});
// Isolating cases: with the named guard removed, each of these would qualify.
Refused("only-lowercase-controllerports-section", "ambiguous-setting", () => Q(Settings().Replace("[ControllerPorts]\nMultitapMode = Disabled", "[controllerports]\nMultitapMode = Port1Only")));
Refused("duplicate-multitap-key-last-one-benign", "ambiguous-setting", () => Q(Settings().Replace("MultitapMode = Disabled", "MultitapMode = Port1Only\nMultitapMode = Disabled")));
Refused("duplicate-key-in-pad", "ambiguous-setting", () => Q(Settings().Replace("Cross = XInput-0/A", "Cross = XInput-0/A\nCross = SDL-0/A")));
Refused("duplicate-type-key", "ambiguous-setting", () => Q(Settings().Replace("[Pad2]\nType = AnalogController", "[Pad2]\nType = AnalogController\nType = None")));
Refused("split-pad-section", "ambiguous-setting", () => Q(Settings() + "\n[Pad1]\nTriangle = SDL-0/Y\n"));
Refused("split-folders-section", "ambiguous-setting", () => Q(Settings() + "\n[Folders]\nCovers = covers2\n"));
Refused("lowercase-none-type", "ambiguous-setting", () => Q(Settings().Replace("[Pad3]\nType = None", "[Pad3]\nType = none")));
Refused("setting-before-any-section", "ambiguous-setting", () => Q("Stray = 1\n" + Settings()));
Refused("huge-xinput-index-is-refused-not-thrown", "pad-not-single-xinput", () => Q(Settings().Replace("Cross = XInput-0/A", "Cross = XInput-2147483648/A")));
Refused("xinput-index-out-of-range", "pad-not-single-xinput", () => Q(Settings(pad1: "XInput-4")));
Refused("lowercase-binding-source", "pad-not-single-xinput", () => Q(Settings().Replace("Cross = XInput-0/A", "Cross = xinput-0/A")));
Refused("binding-with-extra-path-part", "pad-not-single-xinput", () => Q(Settings().Replace("Cross = XInput-0/A", "Cross = XInput-0/A/B")));
Refused("unknown-path-named-key-with-plain-value", "unsupported-path:Assets/AssetPath", () => Q(Settings(extra: "\n[Assets]\nAssetPath = assets\n")));
Refused("unknown-path-key-under-logging", "unsupported-path:Logging/FutureLogFilePath", () => Q(Settings(extra: "\n[Logging]\nLogToFile = false\nFutureLogFilePath = C:\\SyntheticShare\\x.log\n")));
Refused("unknown-key-with-rooted-value", "unsupported-path:Misc/Something", () => Q(Settings(extra: "\n[Misc]\nSomething = D:\\x\n")));
Refused("path-in-a-hotkey-value", "unsupported-path:Hotkeys/Odd", () => Q(Settings().Replace("OpenPauseMenu = XInput-0/Guide", "OpenPauseMenu = XInput-0/Guide\nOdd = C:\\x\\y")));
Check("logging-channels-and-plain-settings-stay-mappable", () => Require(
    Q(Settings(extra: "\n[Logging]\nLogLevel = Info\nLogToFile = false\nFileSystem = true\nFileLoader = true\nCDImage = true\nImage = true\n[Display]\nAspectRatio = 4:3\nScaling = BilinearSmooth\n[Hotkeys2]\nX = 1\n")).Ok, "ordinary settings refused"));
Check("every-accepted-binding-is-rewritten", () =>
{
    string text = Settings().Replace("Cross = XInput-0/A", "Cross = XInput-0/A & XInput-0/B");
    var p1 = Values(Build(text, 3, 2), "Pad1");
    Require(p1.Values.Where(v => v.Contains('/')).All(v => !v.Contains("XInput-0/")) && p1["Cross"] == "XInput-3/A & XInput-3/B", "a binding kept its old slot: " + p1["Cross"]);
});

Console.WriteLine($"Cases={passed + failed} Passed={passed} Failed={failed}; M/pure rules on synthetic settings text; no emulator, disk or controller");
return failed == 0 ? 0 : 1;
