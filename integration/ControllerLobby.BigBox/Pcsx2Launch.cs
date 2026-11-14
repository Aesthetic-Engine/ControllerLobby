#nullable enable
using System.Diagnostics;
using System.IO;
using ControllerLobby.DirectIndex.HostCore;
using ControllerLobby.DirectIndex.Selection;
using ControllerLobby.Pcsx2;

namespace ControllerLobby.BigBox;

// Disk and process side of the PCSX2 rules (PCSX2 adapter contract r4). The owner's files are
// only ever read here; every write goes to the launch copy beside this host. Qualified against PCSX2 v2.7.182.
internal static class Pcsx2Launch
{
    public const string Kind = "pcsx2", ExeName = "pcsx2-qt.exe", CopyFolder = "Pcsx2Copy";
    public const string RunningMessage = "PCSX2 is already running. Close it, then start the game again.";
    /// <summary>The owner's files as consumed. SettingsPin hashes to exactly Settings.</summary>
    internal sealed record Owner(string Root, string SettingsPath, byte[] Settings, ProtectedFile SettingsPin, string GameSettings, PerGameFile[] PerGame);
    /// <summary>The read before the lobby. Owner is null when that read failed, which refuses mapping at Start.</summary>
    internal sealed record PreLobbyRead(Owner? Owner);
    public static PreLobbyRead PreLobby(NormalManifest m) => new(ReadOwner(m));

    public static string CopyExecutable(NormalManifest m) => Path.Combine(m.Pcsx2!.CopyRoot, ExeName);

    // The whole pinned copy: exactly the release inventory's names plus portable.ini, each with the hash recorded at
    // install. Setup copies a file only when it equals the release's (contract 2.1), so this pins that result.
    public static ProtectedFile[] Pin(string packageRoot, NormalManifest m)
    {
        var copy = m.Pcsx2 ?? throw new IOException("PCSX2 copy missing from manifest");
        string expected = Path.Combine(Path.GetFullPath(packageRoot), CopyFolder);
        if (copy.Inventory == null || copy.Inventory.Length == 0 || /*guard:pcsx2-copy-root-fixed*/!Path.GetFullPath(copy.CopyRoot).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Invalid PCSX2 copy");
        var pins = new List<ProtectedFile>(); bool portable = false;
        foreach (var item in copy.Inventory)
        {
            bool marker = item.Path == "portable.ini";
            if (!marker && /*guard:pcsx2-inventory-paths*/!Pcsx2Inventory.Files.ContainsKey(item.Path))
                throw new IOException("Invalid PCSX2 copy path");
            var file = ProtectedFile.Capture(Path.Combine(expected, item.Path));
            if (!file.Present || /*guard:pcsx2-inventory-hash*/file.Sha256 != item.Sha256) throw new IOException("PCSX2 copy changed");
            pins.Add(file); portable |= marker;
        }
        if (!portable || /*guard:pcsx2-inventory-complete*/copy.Inventory.Select(i => i.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Pcsx2Inventory.Files.Count + 1)
            throw new IOException("Invalid PCSX2 copy");
        // The copy must run the same program the owner installed.
        if (/*guard:pcsx2-copy-exe-matches*/pins.First(p => Path.GetFileName(p.Path).Equals(ExeName, StringComparison.OrdinalIgnoreCase)).Sha256 != m.ExecutableSha256) throw new IOException("PCSX2 copy changed");
        return pins.ToArray();
    }

    private static bool Reparse(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (FileNotFoundException) { return false; } catch (DirectoryNotFoundException) { return false; }
    }

    private static PerGameFile[] ReadPerGame(string folder)
    {
        if (!Directory.Exists(folder)) return File.Exists(folder) ? [new(folder, null, RegularFile: false)] : [];
        if (Reparse(folder)) return [new(folder, null, RegularFile: false)];
        var files = new List<PerGameFile>();
        foreach (string entry in Directory.GetFileSystemEntries(folder).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            string name = Path.GetFileName(entry);
            // Anything that is not a plain file refuses mapping (contract 3.1), so it is never copied.
            if (Directory.Exists(entry) || Reparse(entry)) { files.Add(new(name, null, RegularFile: false)); continue; }
            try { files.Add(new(name, File.ReadAllBytes(entry))); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { files.Add(new(name, null)); }
        }
        return files.ToArray();
    }

    /// <summary>Read-only look at the owner's PCSX2: its settings bytes and every entry of its game-settings folder.</summary>
    /// The settings pin is taken after the bytes and must hash to them: a file replaced while it was read is null.
    public static Owner? ReadOwner(NormalManifest m, Action? afterRead = null)
    {
        string root = Path.GetDirectoryName(Path.GetFullPath(m.Executable))!, settings = Path.Combine(root, "inis", "PCSX2.ini");
        byte[] bytes;
        try { bytes = File.ReadAllBytes(settings); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        afterRead?.Invoke();
        ProtectedFile pin;
        try { pin = ProtectedFile.Capture(settings); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        if (/*guard:pcsx2-pin-matches-bytes*/!pin.Present || pin.Sha256 != Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))) return null;
        string? text = Text(bytes); if (text == null) return null;
        string games = Pcsx2Session.OwnerGameSettings(text, root) ?? Path.Combine(root, "gamesettings");
        return new(root, settings, bytes, pin, games, ReadPerGame(games));
    }
    private static string? Text(byte[] bytes)
    {
        try { return new System.Text.UTF8Encoding(false, true).GetString(bytes); } catch (System.Text.DecoderFallbackException) { return null; }
    }
    public static Pcsx2Qualification Qualify(Owner? owner) =>
        owner == null || Text(owner.Settings) is not { } text ? new(false, "settings-unreadable") : Pcsx2Session.Qualify(text, owner.PerGame);

    public static string Notice(string reason, int players = 0) => (reason switch
    {
        "too-many-players" => "PCSX2 takes up to 4 controllers, and " + players + " players joined.",
        "input-profile" or "per-game-input" => "Join order is not available because a PCSX2 game has its own controller settings.",
        "unsupported-version" => "This PCSX2 version isn't supported by the lobby yet.",
        _ => "PCSX2's controller settings can't take lobby join order.",
    }) + " " + NormalPlan.ExistingNotice;

    private static bool Same(Owner a, Owner b) =>
        a.Settings.AsSpan().SequenceEqual(b.Settings) && a.GameSettings.Equals(b.GameSettings, StringComparison.OrdinalIgnoreCase)
        && a.PerGame.Length == b.PerGame.Length && a.PerGame.Zip(b.PerGame).All(p => p.First.Name == p.Second.Name && p.First.RegularFile == p.Second.RegularFile
            && (p.First.Bytes == null ? p.Second.Bytes == null : p.Second.Bytes != null && p.First.Bytes.AsSpan().SequenceEqual(p.Second.Bytes)));

    /// <summary>After Start (contract 3.1-3.5): qualify, rebuild the session folder from the bytes qualified, write the
    /// copy's PCSX2.ini, and launch the copy. Any refusal gives the owner's PCSX2 with existing controls.</summary>
    /// preLobby null is the test seam's "no lobby read"; a PreLobbyRead whose Owner is null refuses.
    /// afterRequalify is a test seam between the last owner read and the launch plan.
    public static NormalPlan Prepare(NormalManifest m, string[] original, SelectionSeal selection, PreLobbyRead? preLobby = null, Action? afterSnapshot = null, string? pin = null, Action? afterRequalify = null)
    {
        NormalPlan Fallback(string reason) => NormalPlan.Existing(m, original, Notice(reason, selection.PlayerIndices.Count));
        if (/*guard:pcsx2-exe-pin*/!m.ExecutableSha256.Equals(pin ?? Pcsx2Session.PinnedSha256, StringComparison.OrdinalIgnoreCase)) return Fallback("unsupported-version");
        if (!Pcsx2Session.ArgumentsQualified(original)) return Fallback("unqualified");
        var owner = ReadOwner(m); var first = Qualify(owner);
        if (owner == null || !first.Ok) return Fallback(first.Reason);
        if (/*guard:pcsx2-pre-lobby-unchanged*/preLobby != null && (/*guard:pcsx2-pre-lobby-read-failed*/preLobby.Owner == null || !Same(preLobby.Owner, owner))) return Fallback("settings-changed");
        if (/*guard:pcsx2-players-within-ports*/selection.PlayerIndices.Count > 4) return Fallback("too-many-players");
        string copy = Path.GetFullPath(m.Pcsx2!.CopyRoot), session = Path.Combine(copy, Pcsx2Session.Session), inis = Path.Combine(copy, "inis");
        // Nothing is created, deleted or written until the destination is known to be the copy itself.
        if (/*guard:pcsx2-destination-safe*/!DuckStationLaunch.SafeDestination(copy, [owner.GameSettings]) || Reparse(session) || Reparse(inis)) return Fallback("unavailable");
        // A fresh session folder: nothing from an earlier launch remains (contract 3.3).
        if (Directory.Exists(session)) Directory.Delete(session, recursive: true); // guard:pcsx2-fresh-session
        foreach (string folder in Pcsx2Session.PrivateFolderNames) Directory.CreateDirectory(Path.Combine(copy, folder));
        string games = Path.Combine(copy, Pcsx2Session.Session, "gamesettings");
        foreach (var f in owner.PerGame) File.WriteAllBytes(Path.Combine(games, f.Name), /*guard:pcsx2-qualified-bytes*/f.Bytes!);
        afterSnapshot?.Invoke();
        // The owner's files read again now must be exactly the ones qualified and copied.
        var again = ReadOwner(m);
        if (/*guard:pcsx2-requalify*/again == null || !Same(owner, again)) return Fallback("settings-changed");
        afterRequalify?.Invoke();
        Directory.CreateDirectory(inis);
        string target = Path.Combine(inis, "PCSX2.ini"), fresh = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(fresh, Pcsx2Session.Build(Text(owner.Settings)!, selection.PlayerIndices.ToArray(), owner.Root, copy), new System.Text.UTF8Encoding(false));
        try { File.Move(fresh, target, overwrite: true); } finally { if (File.Exists(fresh)) File.Delete(fresh); }
        // The owner's pin is the one taken with the bytes the session settings were built from.
        var inputs = new List<ProtectedFile> { ProtectedFile.Capture(target), /*guard:pcsx2-consumed-pin*/owner.SettingsPin };
        inputs.AddRange(Directory.GetFiles(games).Select(ProtectedFile.Capture));
        return new(original.ToArray(), true, "Player assignments applied for this session. Game saves use your normal location.", inputs.ToArray()) { Executable = CopyExecutable(m) };
    }

    private static IEnumerable<DuckProcess> Processes()
    {
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
        {
            int pid = p.Id; p.Dispose(); yield return new(pid, null);
        }
    }
    /// <summary>Any process named pcsx2-qt counts, wherever it is installed: two instances would share memory cards.
    /// A listing that fails counts as running. except is the host's own held target.</summary>
    public static bool Running(NormalManifest m, Func<IEnumerable<DuckProcess>>? processes = null, int except = -1)
    {
        try { return (processes ?? Processes)().Any(p => /*guard:pcsx2-own-target-excluded*/p.Pid != except); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException) { return /*guard:pcsx2-listing-failure*/true; }
    }
}
