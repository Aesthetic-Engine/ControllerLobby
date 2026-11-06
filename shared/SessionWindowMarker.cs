using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Buffers.Binary;

namespace ControllerLobby.WindowIdentity;

public interface IMarkerNative
{
    bool Alive(IntPtr hwnd);
    int Pid(IntPtr hwnd);
    IntPtr Get(IntPtr hwnd, string name);
    bool Set(IntPtr hwnd, string name, IntPtr value);
    void Remove(IntPtr hwnd, string name);
}

// A session provenance marker, not a security boundary. Only the session main window publishes it.
// The value is a nonzero 64-bit session digest, not an allocated HANDLE or pointer. The publisher removes its own property
// in the HWND's WM_NCDESTROY hook, before that hook returns. The observer never writes another process's window.
public sealed class SessionWindowMarker : IDisposable
{
    private readonly IMarkerNative native;
    private readonly IntPtr hwnd;
    private readonly string name;
    private bool attached;
    // Fixed spelling avoids a new property-name atom per launch, including forced child termination.
    public static string Name(Guid session) => "ControllerLobby.SessionWindow";
    public static IntPtr Value(Guid session)
    {
        // This candidate requires a 64-bit process. Refuse rather than silently truncate on a 32-bit host.
        if (IntPtr.Size != 8) throw new PlatformNotSupportedException("64-bit session marker required.");
        long value = BinaryPrimitives.ReadInt64LittleEndian(SHA256.HashData(session.ToByteArray()));
        return new IntPtr(value == 0 ? 1 : value);
    }
    public SessionWindowMarker(IntPtr hwnd, Guid session, IMarkerNative? native = null)
    {
        this.native = native ?? NativeMarker.Instance;
        this.hwnd = hwnd;
        name = Name(session);
        if (hwnd == IntPtr.Zero || !this.native.Alive(hwnd)) throw new InvalidOperationException("No session window.");
        if (this.native.Get(hwnd, name) != IntPtr.Zero) throw new InvalidOperationException("Session window already marked.");
        if (!this.native.Set(hwnd, name, Value(session))) throw new InvalidOperationException("Session window marker failed."); // guard:publish
        attached = true;
    }
    public void Dispose()
    {
        if (!attached) return;
        attached = false;
        native.Remove(hwnd, name); // guard:remove
    }
    public static bool Matches(IntPtr hwnd, Guid session, int childPid, IMarkerNative? native = null)
    {
        var n = native ?? NativeMarker.Instance;
        if (hwnd == IntPtr.Zero || childPid <= 0 || !n.Alive(hwnd) || n.Pid(hwnd) != childPid) return false;
        if (n.Get(hwnd, Name(session)) != Value(session)) return false; // guard:marker
        return n.Alive(hwnd) && n.Pid(hwnd) == childPid; // guard:revalidate
    }
}

public sealed class NativeMarker : IMarkerNative
{
    public static readonly NativeMarker Instance = new();
    public bool Alive(IntPtr hwnd) => IsWindow(hwnd);
    public int Pid(IntPtr hwnd) => GetWindowThreadProcessId(hwnd, out uint pid) != 0 ? unchecked((int)pid) : 0;
    public IntPtr Get(IntPtr hwnd, string name) => GetPropW(hwnd, name);
    public bool Set(IntPtr hwnd, string name, IntPtr value) => SetPropW(hwnd, name, value);
    public void Remove(IntPtr hwnd, string name) => RemovePropW(hwnd, name);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern IntPtr GetPropW(IntPtr hwnd, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern bool SetPropW(IntPtr hwnd, string name, IntPtr value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern IntPtr RemovePropW(IntPtr hwnd, string name);
}
