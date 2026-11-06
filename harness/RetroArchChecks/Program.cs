using System.Security.Cryptography;
using System.Text;
using ControllerLobby.RetroArch;

int passed = 0, failed = 0;
void Require(bool ok, string why) { if (!ok) throw new InvalidOperationException(why); }
void Check(string name, Action body) { try { body(); passed++; Console.WriteLine("PASS " + name); } catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e.Message); } }
void Reject(string name, Action<Fixture> change, AnalysisStatus status, string reason) => Check(name, () =>
{
    var f = new Fixture(); change(f); var result = f.Run();
    Require(result.Status == status && result.Reason == reason && result.AppendConfig == null && !result.CanLaunch,
        $"expected {status}/{reason}, got {result.Status}/{result.Reason}");
});
Check("literal-P1-index2-P2-index0-eight-user-collision", () =>
{
    var f = new Fixture(); var result = f.Run(); Require(result.Status == AnalysisStatus.Candidate, result.Reason);
    var generated = MappingAnalyzer.Parse(result.AppendConfig!);
    Require(generated.Count == 5 && generated["input_max_users"] == "2" && generated["input_player1_joypad_index"] == "2"
        && generated["input_player2_joypad_index"] == "0" && generated["config_save_on_exit"] == "false"
        && generated["remap_save_on_exit"] == "false", "exact five-line candidate");
    var effective = MappingAnalyzer.Parse(f.Main);
    foreach (var pair in generated) effective[pair.Key] = pair.Value;
    int users = int.Parse(effective["input_max_users"]);
    int[] active = Enumerable.Range(1, users).Select(p => int.Parse(effective[$"input_player{p}_joypad_index"])).ToArray();
    Require(active.SequenceEqual([2, 0]) && active.Count(i => i == 2) == 1 && active.Count(i => i == 0) == 1, "literal effective-port collision");
    Require(!result.CanLaunch && !MappingResult.RemainingGates.Contains("Q-C") && new[] { "Q-A", "Q-O", "Q-B", "Q-D" }.All(MappingResult.RemainingGates.Contains), "candidate never grants launch; no shim Q-C");
});
Check("permuted-seats-use-output-identity-not-list-order", () => { var f = new Fixture(); Array.Reverse(f.Seats!); var text = f.Run().AppendConfig!; Require(text.IndexOf("input_player1", StringComparison.Ordinal) < text.IndexOf("input_player2", StringComparison.Ordinal) && text.Contains("input_player1_joypad_index = \"2\""), "player/identity preserved"); });
Check("one-and-four-players", () =>
{
    foreach (int n in new[] { 1, 4 })
    {
        var f = new Fixture(); f.Seats = Enumerable.Range(1, n).Select(p => new SelectedSeat(p, "pad", "s" + p, "o" + p, 1)).ToArray();
        f.Observed = new(f.Context, Enumerable.Range(1, n).Select(p => new ObservedOutput("o" + p, 1, "xinput", 0, p - 1, "observer", "receipt")).ToArray());
        Require(f.Run().AppendConfig!.Contains($"input_max_users = \"{n}\""), "one/four players");
    }
});
Reject("seat-shape", f => f.Seats = null, AnalysisStatus.Invalid, "seat-shape");
Reject("duplicate-selection", f => f.Seats![1] = f.Seats[1]! with { SourceToken = "a" }, AnalysisStatus.Invalid, "duplicate-selection");
Reject("contiguous-players", f => f.Seats![1] = f.Seats[1]! with { Player = 3 }, AnalysisStatus.Unsupported, "player-gap");
Check("native-unqualified-retains-the-seat", () => { var f = new Fixture(); f.Seats![1] = new(2, "native", "keyboard"); var r = f.Run(); Require(r.Status == AnalysisStatus.Unverified && r.Reason == "native-profile-and-capability-unqualified" && r.Seats.Count == 2 && r.Seats[1].Kind == "native" && r.AppendConfig == null && !r.CanLaunch, "native seat must not disappear or qualify"); });
Reject("output-shape", f => f.Seats![1] = f.Seats[1]! with { OutputId = "OA" }, AnalysisStatus.Invalid, "output-shape");
Reject("session-context", f => f.Observed = f.Observed! with { Context = f.Context with { ObservationEpoch = "stale" } }, AnalysisStatus.Unverified, "observation-context");
foreach (string field in new[] { "session", "helper", "topology" }) Reject("stale-" + field, f => f.Observed = f.Observed! with { Context = field switch { "session" => f.Context with { Session = "other" }, "helper" => f.Context with { HelperGeneration = 2 }, _ => f.Context with { TopologyGeneration = 2 } } }, AnalysisStatus.Unverified, "observation-context");
Reject("one-observation", f => f.Observed = f.Observed! with { Outputs = [] }, AnalysisStatus.Unverified, "missing-or-ambiguous-output");
Reject("ambiguous-observation", f => f.Observed = f.Observed! with { Outputs = [f.Observed.Outputs[0], f.Observed.Outputs[0]] }, AnalysisStatus.Unverified, "missing-or-ambiguous-output");
foreach (string field in new[] { "generation", "backend", "base", "index", "observer", "receipt" }) Reject("output-evidence-" + field, f =>
{
    var output = f.Observed!.Outputs[0]!; output = field switch { "generation" => output with { OutputGeneration = 2 }, "backend" => output with { Backend = "dinput" }, "base" => output with { Base = 1 }, "index" => output with { Index = 4 }, "observer" => output with { Observer = "" }, _ => output with { Receipt = "" } };
    f.Observed = f.Observed with { Outputs = [output, f.Observed.Outputs[1]] };
}, AnalysisStatus.Unverified, "output-observation");
Reject("unique-indices", f => f.Observed = f.Observed! with { Outputs = [f.Observed.Outputs[0], f.Observed.Outputs[1]! with { Index = 2 }] }, AnalysisStatus.Unverified, "duplicate-observed-index");
Reject("launch-identity", f => f.Launch = f.Launch with { Root = "relative" }, AnalysisStatus.Unsupported, "unresolved-launch-context");
Reject("reserved-core-name", f => f.Launch = f.Launch with { CoreName = "CON" }, AnalysisStatus.Unsupported, "unresolved-launch-context");
Reject("main-location", f => f.Launch = f.Launch with { MainConfig = Path.GetFullPath(Path.Combine(f.Root, "../outside.cfg")) }, AnalysisStatus.Unsupported, "main-config-outside-root");
Reject("read-stamp", f => f.Reader.Files[f.Launch.MainConfig] = new(ReadState.Present, f.Main, null), AnalysisStatus.Unsupported, "unreadable-config-chain");
Reject("xinput-driver", f => f.Main = f.Main.Replace("xinput", "dinput"), AnalysisStatus.Unsupported, "joypad-driver");
Reject("reservation", f => f.Main += "input_player1_device_reservation_type = \"1\"\n", AnalysisStatus.Unsupported, "device-reservation");
Reject("config-directories", f => f.Main = f.Main.Replace("rgui_config_directory", "ignored_directory"), AnalysisStatus.Unsupported, "missing-config-directories");
Reject("chain-components", f => f.Launch = f.Launch with { Content = Path.Combine(f.Root, "Roms/CON.nes") }, AnalysisStatus.Unsupported, "unresolved-content-name");
foreach (string key in new[] { "config_save_on_exit", "remap_save_on_exit", "input_max_users", "input_joypad_driver", "input_player1_joypad_index", "input_player4_reserved_device", "input_player2_device_reservation_type" }) Reject("protected-override-" + key, f => f.Put(f.OverrideGame, key + " = \"1\""), AnalysisStatus.Unsupported, "override-sets-protected-key");
Reject("remap-port", f => f.Put(f.RemapGame, "input_remap_port_p1 = \"1\""), AnalysisStatus.Unsupported, "nonidentity-or-invalid-remap");
Check("identity-remap-candidate-not-supported", () => { var f = new Fixture(); f.Put(f.RemapGame, "input_remap_port_p1 = \"0\"\ninput_remap_port_p2 = \"1\""); Require(f.Run().Status == AnalysisStatus.Candidate && !f.Run().CanLaunch, "identity candidate"); });
Reject("directory-syntax", f => f.Main = f.Main.Replace(":\\config", "%APPDATA%/config"), AnalysisStatus.Unsupported, "unresolved-config-location");
Reject("directory-traversal", f => f.Main = f.Main.Replace(":\\config", "../config"), AnalysisStatus.Unsupported, "unresolved-config-location");
Reject("directory-scope", f => f.Main = f.Main.Replace(":\\config", Path.GetPathRoot(f.Root) + "outside-config"), AnalysisStatus.Unsupported, "config-location-outside-root");
Reject("include", f => f.Put(f.OverrideGame, "#include \"other.cfg\""), AnalysisStatus.Unsupported, "unresolved-include");
Reject("assignment-syntax", f => f.Put(f.OverrideGame, "not a config assignment"), AnalysisStatus.Unsupported, "config-syntax");
Reject("quoted-value", f => f.Put(f.OverrideGame, "other_key = \"closed\" trailing garbage"), AnalysisStatus.Unsupported, "config-syntax");
Reject("unique-key", f => f.Put(f.OverrideGame, "other_key = 1\nother_key = 2"), AnalysisStatus.Unsupported, "duplicate-config-key");
Reject("unreadable-optional-chain", f => f.Reader.Files[f.RemapGame] = new(ReadState.Unreadable), AnalysisStatus.Unsupported, "unreadable-config-chain");
Check("complete-chain-stamps-absence-and-content", () => { var f = new Fixture(); f.Put(f.OverrideGame, "# comment\nother_key = \"x#y\" # trailing comment"); var r = f.Run(); Require(r.Status == AnalysisStatus.Candidate && r.Inputs.Count == 7 && r.Inputs.Count(i => i.Present) == 2 && r.Inputs.Where(i => i.Present).All(i => i.Sha256?.Length == 64), "closed chain manifest"); });
Check("reparse-policy-walks-all-ancestors", () => { string path = Path.GetFullPath("fixture/a/b.cfg"); string parent = Path.GetDirectoryName(path)!; Require(FileTreeGuard.HasReparse(path, p => p == parent ? FileAttributes.ReparsePoint : null), "parent reparse refuses"); Require(!FileTreeGuard.HasReparse(path, _ => null), "missing ancestors are not reparse evidence"); });
Check("disk-read-size-and-raw-byte-stamp", () =>
{
    using var t = new TempScope(); string path = Path.Combine(t.Root, "base.cfg");
    byte[] raw = [0xef, 0xbb, 0xbf, ..Encoding.UTF8.GetBytes("input_joypad_driver = \"xinput\"\n")]; File.WriteAllBytes(path, raw);
    var read = new DiskConfigReader().Read(path); Require(read.State == ReadState.Present && read.RawSha256 == Convert.ToHexString(SHA256.HashData(raw)), "stamp includes BOM/raw bytes");
    File.WriteAllBytes(path, new byte[4 * 1024 * 1024 + 1]); Require(new DiskConfigReader().Read(path).State == ReadState.Unreadable, "read-size");
    File.WriteAllBytes(path, [0xff]); Require(new DiskConfigReader().Read(path).State == ReadState.Unreadable, "strict UTF8");
});
foreach (string guard in new[] { "write-candidate", "write-absolute", "write-owner-match", "write-outside-owner", "write-new-directory" }) Check(guard, () =>
{
    using var t = new TempScope(); var f = new Fixture(); var result = f.Run(); string owner = f.Root, session = Path.Combine(t.Root, "new-session");
    if (guard == "write-candidate") result = result with { Status = AnalysisStatus.Unverified };
    if (guard == "write-absolute") session = "relative-session-" + Guid.NewGuid().ToString("N");
    if (guard == "write-owner-match") owner = Path.Combine(t.Root, "other-owner");
    if (guard == "write-outside-owner") { owner = Path.Combine(t.Root, "owner"); result = result with { OwnerRoot = owner }; session = Path.Combine(owner, "session"); }
    if (guard == "write-new-directory") Directory.CreateDirectory(session);
    bool caught = false; try { CandidateFile.WriteNew(result, session, owner); } catch (Exception e) when (e is IOException or ArgumentException) { caught = true; }
    Require(caught, "writer guard " + guard);
});
Check("writes-only-new-session-owner-files-readonly-unchanged", () =>
{
    using var t = new TempScope(); var f = new Fixture(); string owner = Path.Combine(t.Root, "owner"); Directory.CreateDirectory(owner);
    f.Root = owner; f.Launch = new(owner, Path.Combine(owner, "retroarch.cfg"), "FCEUmm", Path.Combine(owner, "Roms/Retro/SMB3.nes"));
    File.WriteAllText(f.Launch.MainConfig, f.Main, new UTF8Encoding(false)); File.SetAttributes(f.Launch.MainConfig, FileAttributes.ReadOnly);
    byte[] before = File.ReadAllBytes(f.Launch.MainConfig); var result = MappingAnalyzer.Analyze(f.Launch, f.Context, f.Seats, f.Observed, new DiskConfigReader());
    string target = CandidateFile.WriteNew(result, Path.Combine(t.Root, "session"), owner);
    Require(File.ReadAllText(target) == result.AppendConfig && Directory.GetFiles(Path.GetDirectoryName(target)!).Length == 1, "single artifact");
    Require(File.ReadAllBytes(f.Launch.MainConfig).SequenceEqual(before) && result.Status == AnalysisStatus.Candidate && !result.CanLaunch, "owner unchanged, candidate only");
});
Check("invalid-launch-context-performs-zero-config-reads", () => { var f = new Fixture(); f.Launch = f.Launch with { Root = "relative" }; Require(f.Run().Status == AnalysisStatus.Unsupported && f.Reader.Requested.Count == 0, "reject before any read"); });
Check("game-override-readonly-file-preserved", () =>
{
    using var t = new TempScope(); var f = new Fixture(); string owner = Path.Combine(t.Root, "owner"); Directory.CreateDirectory(owner);
    f.Root = owner; f.Launch = new(owner, Path.Combine(owner, "retroarch.cfg"), "FCEUmm", Path.Combine(owner, "Roms/Retro/SMB3.nes"));
    File.WriteAllText(f.Launch.MainConfig, f.Main, new UTF8Encoding(false)); Directory.CreateDirectory(Path.GetDirectoryName(f.OverrideGame)!);
    File.WriteAllText(f.OverrideGame, "input_player1_joypad_index = \"0\"\n", new UTF8Encoding(false));
    File.SetAttributes(f.Launch.MainConfig, FileAttributes.ReadOnly); File.SetAttributes(f.OverrideGame, FileAttributes.ReadOnly);
    byte[] original = File.ReadAllBytes(f.OverrideGame); var result = MappingAnalyzer.Analyze(f.Launch, f.Context, f.Seats, f.Observed, new DiskConfigReader());
    Require(result.Status == AnalysisStatus.Unsupported && result.Reason == "override-sets-protected-key"
        && result.Inputs.Any(i => i.Path.Equals(f.OverrideGame, StringComparison.OrdinalIgnoreCase) && i.Present)
        && File.ReadAllBytes(f.OverrideGame).SequenceEqual(original), "real resolved game override is read and rejected, never written");
});
Check("reparse-attributes-captured-once", () => { string path = Path.GetFullPath("fixture/file.cfg"); int reads = 0; bool found = FileTreeGuard.HasReparse(path, p => p == path ? (++reads == 1 ? FileAttributes.ReparsePoint : FileAttributes.Normal) : null); Require(found && reads == 1, "do not read the same attribute twice across a race"); });
Check("metadata-denied-is-unreadable", () => {
    using var t = new TempScope(); string path = Path.Combine(t.Root, "optional.cfg");
    var reader = new DiskConfigReader(p => p == path ? throw new UnauthorizedAccessException() : File.GetAttributes(p));
    Require(reader.Read(path).State == ReadState.Unreadable, "metadata denial cannot be absence");
});
Check("missing-leaf-denied-ancestor-is-unreadable", () => {
    using var t = new TempScope(); string parent = Path.Combine(t.Root, "denied"), path = Path.Combine(parent, "optional.cfg");
    var reader = new DiskConfigReader(p => p == parent ? throw new UnauthorizedAccessException() : File.GetAttributes(p));
    Require(reader.Read(path).State == ReadState.Unreadable, "missing leaf does not bypass denied ancestor");
});
Check("uncertain-metadata-io-is-unreadable", () => {
    using var t = new TempScope(); string path = Path.Combine(t.Root, "optional.cfg");
    Require(new DiskConfigReader(p => p == path ? throw new IOException("metadata unavailable") : File.GetAttributes(p)).Read(path).State == ReadState.Unreadable, "unknown IO cannot be absence");
});
Check("actual-reader-denial-propagates-to-analyzer", () => {
    using var t = new TempScope(); var f = new Fixture(); f.Root=t.Root;
    f.Launch=new(t.Root,Path.Combine(t.Root,"retroarch.cfg"),"FCEUmm",Path.Combine(t.Root,"Roms/Retro/SMB3.nes"));
    File.WriteAllText(f.Launch.MainConfig, f.Main, new UTF8Encoding(false));
    var reader = new DiskConfigReader(p => p == f.OverrideGame ? throw new UnauthorizedAccessException() : File.GetAttributes(p));
    var result = MappingAnalyzer.Analyze(f.Launch,f.Context,f.Seats,f.Observed,reader);
    Require(result.Status == AnalysisStatus.Unsupported && result.Reason == "unreadable-config-chain"
        && !result.Inputs.Any(i => i.Path == f.OverrideGame), "do not stamp uncertain optional path as missing");
});
Check("known-missing-optional-file-is-absent", () => {
    using var t = new TempScope(); Require(new DiskConfigReader().Read(Path.Combine(t.Root,"missing/deep/optional.cfg")).State == ReadState.Absent, "positively missing parents remain supported");
});
Console.WriteLine($"Cases done; Passed={passed}; Failed={failed}; layer=M; isolated configs/declared observations; no emulator or device run");
return failed == 0 ? 0 : 1;

sealed class MemoryReader : IConfigReader
{
    public Dictionary<string, ConfigRead> Files = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Requested = [];
    public ConfigRead Read(string path) { path = Path.GetFullPath(path); Requested.Add(path); return Files.GetValueOrDefault(path, new(ReadState.Absent)); }
}
sealed class Fixture
{
    public string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "retroarch-mapping-synthetic-owner"));
    public LaunchContext Launch;
    public RouteContext Context = new("session", 1, 1, "epoch");
    public SelectedSeat?[]? Seats = [new(1, "pad", "a", "oa", 1), new(2, "pad", "b", "ob", 1)];
    public ObservationBatch? Observed;
    public MemoryReader Reader = new();
    private string main = "input_joypad_driver = \"xinput\"\nrgui_config_directory = \":\\config\"\ninput_remapping_directory = \":\\remaps\"\ninput_max_users = \"8\"\nconfig_save_on_exit = \"true\"\nremap_save_on_exit = \"true\"\n" + string.Join('\n', Enumerable.Range(1, 8).Select(p => $"input_player{p}_joypad_index = \"{p-1}\"")) + "\n";
    public string Main { get => main; set { main = value; Put(Launch.MainConfig, value); } }
    public string OverrideGame => Path.GetFullPath(Path.Combine(Root, "config/FCEUmm/SMB3.cfg"));
    public string RemapGame => Path.GetFullPath(Path.Combine(Root, "remaps/FCEUmm/SMB3.rmp"));
    public Fixture() { Launch = new(Root, Path.Combine(Root, "retroarch.cfg"), "FCEUmm", Path.Combine(Root, "Roms/Retro/SMB3.nes")); Observed = new(Context, [new("oa", 1, "xinput", 0, 2, "observer", "receipt"), new("ob", 1, "xinput", 0, 0, "observer", "receipt")]); Put(Launch.MainConfig, Main); }
    public void Put(string path, string text) => Reader.Files[Path.GetFullPath(path)] = new(ReadState.Present, text, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))));
    public MappingResult Run() => MappingAnalyzer.Analyze(Launch, Context, Seats, Observed, Reader);
}
sealed class TempScope : IDisposable
{
    public string Root = Path.Combine(Path.GetTempPath(), "ra-config-check-" + Guid.NewGuid().ToString("N"));
    public TempScope() { Directory.CreateDirectory(Root); File.WriteAllText(Path.Combine(Root, ".owned-fixture"), "RetroArchChecks"); }
    public void Dispose()
    {
        string full = Path.GetFullPath(Root), parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("ra-config-check-", StringComparison.Ordinal)
            || File.ReadAllText(Path.Combine(full, ".owned-fixture")) != "RetroArchChecks") throw new IOException("unsafe fixture cleanup");
        foreach (var path in Directory.GetFiles(full, "*", SearchOption.AllDirectories)) File.SetAttributes(path, FileAttributes.Normal);
        Directory.Delete(full, true);
    }
}
