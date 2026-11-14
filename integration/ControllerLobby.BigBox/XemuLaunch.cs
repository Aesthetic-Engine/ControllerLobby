#nullable enable
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using ControllerLobby.DirectIndex.HostCore;
using ControllerLobby.DirectIndex.Selection;
using ControllerLobby.Xemu;

namespace ControllerLobby.BigBox;

/// <summary>Connected gamepads as SDL reports them, for the length of one lobby session.</summary>
internal interface IPadObserver : IDisposable
{
    IReadOnlyList<ObservedPad> Snapshot();
    /// <summary>False when Dispose timed out waiting for the observer's worker.</summary>
    bool ShutdownComplete => true;
}

/// <summary>The SDL functions the observer uses. Dispose releases the library. A seam for lifecycle tests.</summary>
internal interface ISdlFunctions : IDisposable
{
    bool Init(uint flags);
    void Quit();
    void Update();
    uint[] Joysticks();
    string Guid(uint id);
    string Path(uint id);
}

/// <summary>SDL3 loaded from a path. Any failure while resolving exports frees the library before rethrowing.</summary>
internal sealed class NativeSdl : ISdlFunctions
{
    [return: MarshalAs(UnmanagedType.U1)] private delegate bool InitFn(uint flags); // SDL3 returns a C bool (one byte)
    private delegate void VoidFn();
    private delegate IntPtr GetJoysticksFn(out int count);
    private delegate Guid128 GuidFn(uint id);
    private delegate IntPtr PathFn(uint id);
    private delegate void FreeFn(IntPtr p);
    [StructLayout(LayoutKind.Sequential)] private struct Guid128 { public ulong A, B; }
    private IntPtr library;
    private readonly Action<IntPtr> release;
    private readonly InitFn init; private readonly VoidFn quit, update; private readonly GetJoysticksFn list; private readonly GuidFn guid; private readonly PathFn path; private readonly FreeFn free;

    public NativeSdl(string sdlPath) : this(sdlPath, NativeLibrary.Load, NativeLibrary.GetExport, NativeLibrary.Free) { }
    /// <summary>Test seam: the load, export and free functions.</summary>
    internal NativeSdl(string sdlPath, Func<string, IntPtr> load, Func<IntPtr, string, IntPtr> export, Action<IntPtr> freeLibrary)
    {
        release = freeLibrary;
        library = load(sdlPath);
        try
        {
            T Fn<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(export(library, name));
            init = Fn<InitFn>("SDL_Init"); quit = Fn<VoidFn>("SDL_Quit"); update = Fn<VoidFn>("SDL_UpdateJoysticks");
            list = Fn<GetJoysticksFn>("SDL_GetJoysticks"); guid = Fn<GuidFn>("SDL_GetJoystickGUIDForID"); path = Fn<PathFn>("SDL_GetJoystickPathForID"); free = Fn<FreeFn>("SDL_free");
        }
        catch { /*guard:xemu-sdl-free-on-export-failure*/release(library); library = IntPtr.Zero; throw; }
    }
    public bool Init(uint flags) => init(flags);
    public void Quit() => quit();
    public void Update() => update();
    public uint[] Joysticks()
    {
        IntPtr ids = list(out int count);
        try { var r = new uint[Math.Max(count, 0)]; for (int i = 0; i < r.Length; i++) r[i] = (uint)Marshal.ReadInt32(ids, i * 4); return r; }
        finally { if (ids != IntPtr.Zero) free(ids); }
    }
    public string Guid(uint id)
    {
        var g = guid(id); byte[] bytes = new byte[16];
        BitConverter.GetBytes(g.A).CopyTo(bytes, 0); BitConverter.GetBytes(g.B).CopyTo(bytes, 8);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
    public string Path(uint id) => Marshal.PtrToStringUTF8(path(id)) ?? "";
    public void Dispose() { if (library != IntPtr.Zero) { release(library); library = IntPtr.Zero; } }
}

/// <summary>An observer that could not start while its worker may still hold native resources. <see cref="Pending"/>
/// is that observer: Dispose it, then read ShutdownComplete; its Snapshot is never called.</summary>
internal sealed class PadObserverStartException(string message, Exception? inner, IPadObserver pending) : IOException(message, inner)
{
    public IPadObserver Pending { get; } = pending;
}

/// <summary>SDL observer for one lobby session. SDL is initialised once and kept for the whole session so that its
/// joystick instance IDs, which are new for every connection, stay comparable. Every SDL call runs on one dedicated
/// worker, which owns the library: it quits SDL only after a successful init and releases the library on every exit
/// path. If Dispose times out, the shutdown is reported incomplete and the library stays loaded rather than being
/// released under a live thread.</summary>
internal sealed class SdlPadObserver : IPadObserver
{
    private readonly BlockingCollection<Action> work = new();
    private readonly Thread thread;
    private readonly TimeSpan shutdown, call;
    private ISdlFunctions? sdl;
    public bool ShutdownComplete { get; private set; } = true;

    public SdlPadObserver(string sdlPath) : this(() => new NativeSdl(sdlPath)) { }
    internal SdlPadObserver(Func<ISdlFunctions> open, TimeSpan? shutdownTimeout = null, TimeSpan? callTimeout = null)
    {
        shutdown = shutdownTimeout ?? TimeSpan.FromSeconds(5); call = callTimeout ?? TimeSpan.FromSeconds(5);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        thread = new Thread(() =>
        {
            bool initialised = false;
            try
            {
                sdl = open();
                if (!sdl.Init(0x00000200 | 0x00002000)) throw new IOException("SDL joystick subsystem unavailable"); // SDL_INIT_JOYSTICK | SDL_INIT_GAMEPAD
                initialised = true;
                ready.SetResult();
                foreach (var action in work.GetConsumingEnumerable()) action();
            }
            catch (Exception e) { ready.TrySetException(e); }
            finally
            {
                if (initialised) try { /*guard:xemu-sdl-quit-after-init*/sdl!.Quit(); } catch { }
                try { /*guard:xemu-sdl-release*/sdl?.Dispose(); } catch { }
            }
        }) { IsBackground = true, Name = "ControllerLobby SDL observer" };
        thread.Start();
        // A bounded wait: an SDL that never finishes starting is reported unavailable. The worker keeps the native
        // resources and releases them when it exits; nothing is freed under it.
        // A start that fails with the worker still running hands this observer to the caller in the exception, so
        // the caller can dispose it and read ShutdownComplete before claiming the session complete.
        bool started;
        try { started = /*guard:xemu-sdl-bounded-start*/ready.Task.Wait(call); }
        catch (AggregateException e)
        {
            work.CompleteAdding();
            ShutdownComplete = thread.Join(shutdown);
            if (/*guard:xemu-failed-start-joined*/ShutdownComplete) throw e.InnerException!;
            throw new PadObserverStartException("SDL did not stop after a failed start", e.InnerException, this);
        }
        if (!started) { work.CompleteAdding(); ShutdownComplete = false; throw new PadObserverStartException("SDL did not start in time", null, this); }
    }

    public IReadOnlyList<ObservedPad> Snapshot()
    {
        var done = new TaskCompletionSource<IReadOnlyList<ObservedPad>>(TaskCreationOptions.RunContinuationsAsynchronously);
        work.Add(() =>
        {
            try
            {
                sdl!.Update();
                done.SetResult(sdl.Joysticks().Select(id => new ObservedPad(id, sdl.Path(id), sdl.Guid(id))).ToList().AsReadOnly());
            }
            catch (Exception e) { /*guard:xemu-sdl-snapshot-normalized*/done.TrySetException(new IOException("SDL snapshot failed", e)); }
        });
        bool finished;
        try { finished = /*guard:xemu-sdl-bounded-snapshot*/done.Task.Wait(call); } catch (AggregateException e) { throw e.InnerException!; }
        if (!finished) throw new IOException("SDL snapshot did not finish in time");
        return done.Task.Result;
    }

    public void Dispose()
    {
        if (!work.IsAddingCompleted) work.CompleteAdding();
        ShutdownComplete = /*guard:xemu-sdl-join-timeout*/thread.Join(shutdown);
    }
}

/// <summary>Disk and process side of the Xemu rules (Xemu adapter contract r3). The owner's
/// xemu.toml is only ever read; every lobby launch that qualifies gets a session file passed with -config_path.</summary>
internal static class XemuLaunch
{
    public const string Kind = "xemu", ExeName = "xemu.exe";
    public const string RunningMessage = "Xemu is already running. Close it, then start the game again.";
    public const string ChangedMessage = "Join order is not available: the controllers changed.";
    /// <summary>Xemu 0.8.135, commit 6318bb1 (contract scope). Any other build starts exactly as LaunchBox would.</summary>
    public const string PinnedSha256 = "C2E70F7B8D7ACF3847AF9C0415D20BE98D801A32D12E0534047D3D3DAFE46C34";
    public const string UnsupportedNotice = "This Xemu version isn't supported by the lobby yet.";
    private static bool Pinned(NormalManifest m, string? pin) =>
        /*guard:xemu-exe-pin*/m.ExecutableSha256.Equals(pin ?? PinnedSha256, StringComparison.OrdinalIgnoreCase);

    /// <summary>Xemu's settings file as 6318bb1 resolves it: xemu.toml beside the program (portable), else %APPDATA%\xemu\xemu.</summary>
    public static string SettingsPath(NormalManifest m, string? appData = null)
    {
        string portable = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(m.Executable))!, "xemu.toml");
        return File.Exists(portable) ? portable : Path.Combine(appData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "xemu", "xemu", "xemu.toml");
    }

    /// <summary>The owner's settings as consumed: the path resolved once, the bytes read once, and a pin that must hash
    /// to exactly those bytes. Text is null when the file cannot be read or changed while it was read.</summary>
    internal sealed record OwnerSnapshot(string Path, string? Text, ProtectedFile? Pin, bool Changed);
    internal static OwnerSnapshot ReadOwner(NormalManifest m, string? appData, Action? afterRead = null)
    {
        string path = SettingsPath(m, appData);
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(path, null, null, false); }
        afterRead?.Invoke();
        ProtectedFile pin;
        try { pin = ProtectedFile.Capture(path); } catch (IOException) { return new(path, null, null, true); }
        if (/*guard:xemu-pin-matches-bytes*/!pin.Present || pin.Sha256 != Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))) return new(path, null, null, true);
        using var reader = new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true);
        return new(path, reader.ReadToEnd(), pin, false);
    }

    public static string Notice(string reason) => (reason switch
    {
        "controllers-changed" => "Join order is not available: the controllers changed.",
        "duplicate-guid" or "joined-slot-ambiguous" => "Join order is not available: two connected controllers can't be told apart.",
        "observer-failed" => "Join order is not available: the controllers could not be read.",
        "settings-changed" => "Join order is not available: Xemu's settings changed while the game was starting.",
        _ => "Xemu's settings can't take lobby join order.",
    }) + " " + NormalPlan.ExistingNotice;

    private static string WriteSession(string session, string text)
    {
        string file = Path.Combine(session, "xemu-session.toml");
        File.WriteAllText(file, text, new System.Text.UTF8Encoding(false));
        return file;
    }

    /// <summary>Existing controls (rule 3.6): an unchanged copy of the owner's settings, or pass-through when the file
    /// or the arguments are outside what the adapter qualifies (Xemu then rewrites the owner's file at exit, as in normal use).</summary>
    public static NormalPlan Existing(NormalManifest m, string[] original, string session, string notice, string? appData = null, string? pin = null, Action? afterRead = null)
    {
        if (!Pinned(m, pin)) return new(original.ToArray(), false, UnsupportedNotice + " " + NormalPlan.ExistingNotice, []);
        var owner = ReadOwner(m, appData, afterRead);
        // A file that changed while it was read is not copied: Xemu then starts exactly as LaunchBox would.
        if (owner.Text == null || !XemuSession.Qualify(owner.Text).Ok || /*guard:xemu-existing-args*/!XemuSession.ArgumentsQualified(original))
            return new(original.ToArray(), false, notice, []);
        string file = WriteSession(session, XemuSession.BuildExisting(owner.Text));
        return new(["-config_path", file, .. original], false, notice, [ProtectedFile.Capture(file)]);
    }

    /// <summary>After Start (rule 3.2-3.4): eligibility against the sealed baseline, then the mapped session file.</summary>
    public static NormalPlan Prepare(NormalManifest m, string[] original, SelectionSeal selection, IReadOnlyList<ObservedPad> baseline,
        IReadOnlyList<ObservedPad> now, string session, string? appData = null, string? pin = null, Action? afterRead = null)
    {
        if (!Pinned(m, pin)) return Existing(m, original, session, "", appData, pin);
        var owner = ReadOwner(m, appData, afterRead);
        if (/*guard:xemu-changed-while-read*/owner.Changed) return Existing(m, original, session, Notice("settings-changed"), appData, pin);
        if (owner.Text == null || !XemuSession.Qualify(owner.Text).Ok || !XemuSession.ArgumentsQualified(original))
            return Existing(m, original, session, Notice("unqualified"), appData, pin);
        var eligible = XemuSession.Eligible(baseline, now, selection.PlayerIndices);
        if (/*guard:xemu-eligible*/!eligible.Ok) return Existing(m, original, session, Notice(eligible.Reason), appData, pin);
        string file = WriteSession(session, XemuSession.BuildMapped(owner.Text, eligible.Guids));
        // The owner's pin is the one taken from the bytes the session file was built from.
        return new(["-config_path", file, .. original], true, "Player assignments applied for this session. Game saves use your normal location.",
            [ProtectedFile.Capture(file), /*guard:xemu-consumed-pin*/owner.Pin!]);
    }

    /// <summary>Any process named xemu counts, wherever it is installed: [sys.files] paths are absolute, so another
    /// install can hold the same disk image (contract 3.5). Only the suspended target this host owns is excluded.</summary>
    public static bool Running(NormalManifest m, Func<IEnumerable<DuckProcess>>? processes = null, int except = -1)
    {
        IEnumerable<DuckProcess> Live()
        {
            foreach (var p in Process.GetProcessesByName("xemu"))
            {
                string? image;
                try { image = p.MainModule?.FileName; } catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { image = null; }
                int pid = p.Id; p.Dispose(); yield return new(pid, image);
            }
        }
        return (processes ?? Live)().Any(p => /*guard:xemu-own-target-excluded*/p.Pid != except);
    }
}
