using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

// Integration brief I1 r3 section 12: a read-only z-order diagnostic. While the lobby owns the foreground, record the
// lobby's native window state, a WindowFromPoint hit-test candidate at its centre, and a bounded snapshot of the windows
// above it that belong to BigBox or the lobby or overlap it. Only whitelisted metadata: handle, PID, class, flags and
// rectangle. Never titles, paths, command lines or text. No call here changes any window.
namespace ControllerLobby.LobbyLink
{
    public readonly record struct WinRect(int Left, int Top, int Right, int Bottom)
    {
        public bool Positive => Right > Left && Bottom > Top;
        public bool Overlaps(WinRect o) => Positive && o.Positive && Left < o.Right && o.Left < Right && Top < o.Bottom && o.Top < Bottom;
        public override string ToString() => Left + "," + Top + "," + Right + "," + Bottom;
    }

    // A read that failed is null with its error, never a false flag.
    public sealed record WinInfo(IntPtr Hwnd, int? Pid, string? Class, bool? Topmost, bool? Visible, bool? Enabled,
        bool? Iconic, bool? Cloaked, IntPtr? Owner, WinRect? Rect, string? Error = null);

    public interface IWindowGraph
    {
        IntPtr Top();
        IntPtr Next(IntPtr hwnd);
        WinInfo Info(IntPtr hwnd);
        (IntPtr Child, IntPtr Root) HitTest(int x, int y);
    }

    public static class ZOrderDiagnostic
    {
        public const int MaxNodes = 64;
        public static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(20);

        // One canonical line. The caller logs it only when it differs from the previous line.
        // Budget: checked between graph calls (after each read and before declaring the walk complete). A synchronous OS
        // call cannot be pre-empted, so 20 ms is not a hard runtime maximum; an overrun is reported, never hidden.
        public static string Snapshot(IWindowGraph graph, IntPtr lobby, int lobbyPid, int hostPid, Func<TimeSpan>? elapsed = null)
        {
            var clock = Stopwatch.StartNew();
            Func<TimeSpan> spent = elapsed ?? (() => clock.Elapsed);
            bool Over() => spent() > Budget;
            var s = new StringBuilder("zorder");
            if (lobby == IntPtr.Zero) return s.Append(" lobby=unknown(zero-handle)").ToString();
            var above = new List<string>();
            string Finish(string status) => s.Append(" above=[").Append(string.Join(";", above)).Append("] walk=").Append(status).ToString();
            WinInfo me = Safe(graph, lobby);
            s.Append(" lobby=").Append(Describe(me));
            if (Over()) return Finish("incomplete:budget"); // mutation:budget-early
            if (me.Rect is WinRect r && r.Positive)
            {
                int x = r.Left + (r.Right - r.Left) / 2, y = r.Top + (r.Bottom - r.Top) / 2;
                (IntPtr child, IntPtr root) hit = (IntPtr.Zero, IntPtr.Zero);
                bool failed = false;
                try { hit = graph.HitTest(x, y); }
                catch (Exception e) { s.Append(" hit-candidate=unknown(" + e.GetType().Name + ")"); failed = true; }
                if (Over()) return Finish("incomplete:budget"); // mutation:budget-after-hit
                if (hit.root != IntPtr.Zero || hit.child != IntPtr.Zero)
                    s.Append(" hit-candidate=child:").Append(Hex(hit.child)).Append(",root:").Append(Describe(Safe(graph, hit.root))); // mutation:hit-root
                else if (!failed) s.Append(" hit-candidate=none(no-hit)");
                if (Over()) return Finish("incomplete:budget");
            }
            else s.Append(" hit-candidate=none(invalid-rect)");

            var seen = new HashSet<IntPtr>();
            IntPtr h;
            try { h = graph.Top(); } catch (Exception e) { return Finish("incomplete:unknown(" + e.GetType().Name + ")"); }
            int nodes = 0;
            while (true)
            {
                if (Over()) return Finish("incomplete:budget");
                if (h == IntPtr.Zero) return Finish("incomplete:lobby-not-reached");
                if (h == lobby) return Finish("complete");
                if (!seen.Add(h)) return Finish("incomplete:repeat"); // mutation:repeat
                if (++nodes > MaxNodes) return Finish("incomplete:truncated"); // mutation:max-nodes
                WinInfo w = Safe(graph, h);
                // Unknown PID or rectangle (a failed or partial read) is kept as a candidate, never dropped as irrelevant.
                bool relevant = w.Error != null || w.Pid == null || w.Rect == null || // mutation:unknown-kept
                                w.Pid == hostPid || w.Pid == lobbyPid ||
                                (w.Rect is WinRect wr && me.Rect is WinRect mr && wr.Overlaps(mr));
                if (relevant) above.Add(Describe(w)); // disabled windows are kept; mutation:disabled-kept
                if (Over()) return Finish("incomplete:budget"); // mutation:budget-after-walk-info
                try { h = graph.Next(h); } catch (Exception e) { return Finish("incomplete:unknown(" + e.GetType().Name + ")"); }
            }
        }

        private static WinInfo Safe(IWindowGraph graph, IntPtr hwnd)
        {
            try { return graph.Info(hwnd); }
            catch (Exception e) { return new WinInfo(hwnd, null, null, null, null, null, null, null, null, null, e.GetType().Name); }
        }

        private static string Hex(IntPtr h) => "0x" + h.ToString("X");
        private static string Flag(bool? b) => b == null ? "?" : b.Value ? "1" : "0";

        // Whitelisted keys only: h, pid, cls, top, vis, en, min, cloak, own, rect, err.
        public static string Describe(WinInfo w)
        {
            var s = new StringBuilder("{h=").Append(Hex(w.Hwnd));
            if (w.Error != null) return s.Append(",err=unknown(").Append(Clean(w.Error)).Append(")}").ToString();
            s.Append(",pid=").Append(w.Pid?.ToString(CultureInfo.InvariantCulture) ?? "?")
             .Append(",cls=").Append(Clean(w.Class ?? "?"))
             .Append(",top=").Append(Flag(w.Topmost)).Append(",vis=").Append(Flag(w.Visible))
             .Append(",en=").Append(Flag(w.Enabled)).Append(",min=").Append(Flag(w.Iconic))
             .Append(",cloak=").Append(Flag(w.Cloaked))
             .Append(",own=").Append(w.Owner is IntPtr o ? Hex(o) : "?")
             .Append(",rect=").Append(w.Rect is WinRect r ? (r.Positive ? r.ToString() : "invalid(" + r + ")") : "?");
            return s.Append('}').ToString();
        }

        // A class name is the only text read; keep it to a safe alphabet and bound its length.
        private static string Clean(string text)
        {
            var s = new StringBuilder();
            foreach (char c in text) if (char.IsLetterOrDigit(c) || c is '_' or '.' or '-' or '[' or ']' or ':') s.Append(c); else s.Append('_');
            return s.Length > 96 ? s.ToString(0, 96) : s.ToString();
        }
    }

    // The native reads one window's metadata needs, behind a seam so the adapter's failure handling is testable.
    public interface INativeReads
    {
        bool IsWindow(IntPtr hwnd);
        int? Pid(IntPtr hwnd);
        string? Class(IntPtr hwnd);
        bool? Topmost(IntPtr hwnd);
        bool Visible(IntPtr hwnd);
        bool Enabled(IntPtr hwnd);
        bool Iconic(IntPtr hwnd);
        bool? Cloaked(IntPtr hwnd);
        IntPtr Owner(IntPtr hwnd);
        WinRect? Rect(IntPtr hwnd);
    }

    public sealed class RealWindowGraph : IWindowGraph
    {
        public static readonly RealWindowGraph Instance = new(new RealNativeReads());
        private readonly INativeReads reads;
        public RealWindowGraph(INativeReads reads) { this.reads = reads; }
        public IntPtr Top() => GetTopWindow(IntPtr.Zero);
        public IntPtr Next(IntPtr hwnd) => GetWindow(hwnd, GW_HWNDNEXT);
        public (IntPtr Child, IntPtr Root) HitTest(int x, int y)
        {
            IntPtr child = WindowFromPoint(new POINT { X = x, Y = y });
            return (child, child == IntPtr.Zero ? IntPtr.Zero : GetAncestor(child, GA_ROOT));
        }

        // Liveness and owner PID are checked before and after the reads. A window destroyed or reused while it was read
        // is reported unstable, so its flags never become false values. A failed single read stays null (unknown).
        public WinInfo Info(IntPtr hwnd)
        {
            if (!reads.IsWindow(hwnd)) return new WinInfo(hwnd, null, null, null, null, null, null, null, null, null, "destroyed");
            int? pid = reads.Pid(hwnd);
            string? cls = reads.Class(hwnd);
            bool? topmost = reads.Topmost(hwnd);
            bool visible = reads.Visible(hwnd), enabled = reads.Enabled(hwnd), iconic = reads.Iconic(hwnd);
            bool? cloaked = reads.Cloaked(hwnd);
            IntPtr owner = reads.Owner(hwnd);
            WinRect? rect = reads.Rect(hwnd);
            if (!reads.IsWindow(hwnd) || reads.Pid(hwnd) != pid) // mutation:revalidate
                return new WinInfo(hwnd, null, null, null, null, null, null, null, null, null, "unstable(changed-during-read)");
            return new WinInfo(hwnd, pid, cls, topmost, visible, enabled, iconic, cloaked, owner, rect);
        }

        private const uint GW_HWNDNEXT = 2, GA_ROOT = 2;
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [DllImport("user32.dll")] private static extern IntPtr GetTopWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT point);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    }

    public sealed class RealNativeReads : INativeReads
    {
        public bool IsWindow(IntPtr hwnd) => IsWindowNative(hwnd);
        public int? Pid(IntPtr hwnd) => GetWindowThreadProcessId(hwnd, out uint pid) != 0 ? unchecked((int)pid) : null;
        public string? Class(IntPtr hwnd)
        {
            var name = new StringBuilder(256);
            return GetClassNameW(hwnd, name, name.Capacity) > 0 ? name.ToString() : null;
        }
        public bool? Topmost(IntPtr hwnd)
        {
            Marshal.SetLastPInvokeError(0);
            long ex = (long)GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
            return ex == 0 && Marshal.GetLastPInvokeError() != 0 ? null : (ex & WS_EX_TOPMOST) != 0;
        }
        public bool Visible(IntPtr hwnd) => IsWindowVisible(hwnd);
        public bool Enabled(IntPtr hwnd) => IsWindowEnabled(hwnd);
        public bool Iconic(IntPtr hwnd) => IsIconic(hwnd);
        public bool? Cloaked(IntPtr hwnd) => DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int c, sizeof(int)) == 0 ? c != 0 : null;
        public IntPtr Owner(IntPtr hwnd) => GetWindow(hwnd, GW_OWNER);
        public WinRect? Rect(IntPtr hwnd) => GetWindowRect(hwnd, out RECT r) ? new WinRect(r.Left, r.Top, r.Right, r.Bottom) : null;

        private const uint GW_OWNER = 4;
        private const int GWL_EXSTYLE = -20, DWMWA_CLOAKED = 14;
        private const long WS_EX_TOPMOST = 0x8;
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll", EntryPoint = "IsWindow")] private static extern bool IsWindowNative(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int count);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    }
}
