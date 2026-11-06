using System;
using System.Runtime.InteropServices;
using System.Text;

// Integration brief I1 r3 section 9: the foreground calls the plugin makes, behind one seam, so that the offline checks
// can script grant failure, delayed foreground success, another application owning the foreground and a user switch.
// Window titles are never read: a sample is the handle, the owner process id and the window class only.
namespace ControllerLobby.LobbyLink
{
    public readonly record struct ForegroundSample(IntPtr Hwnd, int OwnerPid, string Class)
    {
        public string Describe() => "hwnd=0x" + Hwnd.ToString("X") + " pid=" + OwnerPid + " class=" + Class;
    }

    public interface IWindowSystem
    {
        // Each failing call reports its Win32 error, read immediately after the call and before any logging.
        bool GrantForeground(int processId, out int error);
        ForegroundSample Foreground();
        bool Activate(IntPtr hwnd, out int error);
    }

    public sealed class RealWindowSystem : IWindowSystem
    {
        public static readonly RealWindowSystem Instance = new();

        public bool GrantForeground(int processId, out int error)
        {
            bool ok = AllowSetForegroundWindow(processId);
            error = ok ? 0 : Marshal.GetLastPInvokeError();
            return ok;
        }

        public ForegroundSample Foreground()
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return new ForegroundSample(IntPtr.Zero, 0, "");
            GetWindowThreadProcessId(hwnd, out uint pid);
            var name = new StringBuilder(256);
            int length = GetClassNameW(hwnd, name, name.Capacity);
            return new ForegroundSample(hwnd, unchecked((int)pid), length > 0 ? name.ToString() : "?");
        }

        public bool Activate(IntPtr hwnd, out int error)
        {
            bool ok = SetForegroundWindow(hwnd);
            error = ok ? 0 : Marshal.GetLastPInvokeError();
            return ok;
        }

        [DllImport("user32.dll", SetLastError = true)] private static extern bool AllowSetForegroundWindow(int processId);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int count);
    }
}
