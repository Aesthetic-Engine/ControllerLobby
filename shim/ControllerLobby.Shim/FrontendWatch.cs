using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// Shim slice S5 (shim trial 01 finding 4; BACKLOG 34(b)): the shim ends its lobby when the frontend that started it
// exits. The parent is found in a toolhelp snapshot; a handle (synchronize, limited query) is opened first and kept, and
// the parent is accepted only if, read through that handle, its image is BigBox.exe or LaunchBox.exe, it was created
// before this process, and it is still running. Anything else leaves the watch unavailable, which is logged; the
// lobby's other guards still apply. Gone is true only when that same handle is signalled.
namespace ControllerLobby.Shim
{
    internal sealed class FrontendWatch : IDisposable
    {
        public static readonly string[] Frontends = { "BigBox.exe", "LaunchBox.exe" };
        private readonly SafeProcessHandle handle;
        public string Status { get; }

        private FrontendWatch(SafeProcessHandle handle, string status) { this.handle = handle; Status = status; }

        public bool Gone => handle != null && WaitForSingleObject(handle, 0) == 0;
        /// <summary>True only while a verified frontend is held; an unavailable or released watch is not a gate input.</summary>
        public bool Watching => handle != null && !handle.IsClosed;

        /// <summary>Positive evidence that the frontend still runs: the held handle answered WAIT_TIMEOUT. A wait failure,
        /// an unexpected result or a released handle is never treated as alive .</summary>
        public bool Alive => Observe() == ControllerLobby.Transaction.GateRead.Pass;

        /// <summary>One wait on the held handle, classified once : WAIT_TIMEOUT is alive, WAIT_OBJECT_0 is an
        /// observed end, and anything else (WAIT_FAILED, abandoned, a released handle) is a failed observation.</summary>
        public ControllerLobby.Transaction.GateRead Observe()
        {
            if (!Watching) return ControllerLobby.Transaction.GateRead.Failed;
            try { return Classify(WaitForSingleObject(handle, 0)); }
            catch (ObjectDisposedException) { return ControllerLobby.Transaction.GateRead.Failed; }
        }

        internal static ControllerLobby.Transaction.GateRead Classify(uint waitResult) => waitResult switch
        {
            0x102 => ControllerLobby.Transaction.GateRead.Pass,  // WAIT_TIMEOUT // guard:alive-positive
            0 => ControllerLobby.Transaction.GateRead.Ended,     // WAIT_OBJECT_0 // guard:ended-observed
            _ => ControllerLobby.Transaction.GateRead.Failed,
        };

        public static FrontendWatch ForParent() => For(ParentOf(Environment.ProcessId), Environment.ProcessId, Frontends);

        internal static FrontendWatch For(int parentPid, int childPid, string[] allowed)
        {
            if (parentPid <= 0) return new FrontendWatch(null, "unavailable: no parent found");
            var h = OpenProcess(0x00100000 | 0x1000, false, parentPid); // SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION
            if (h.IsInvalid) return new FrontendWatch(null, "unavailable: parent pid " + parentPid + " cannot be opened (" + Marshal.GetLastWin32Error() + ")");
            try
            {
                string image = ImageOf(h);
                long parentCreated = CreatedOf(h), childCreated = CreatedOf(childPid);
                bool allowedImage = Array.Exists(allowed, a => string.Equals(a, image, StringComparison.OrdinalIgnoreCase));
                if (!allowedImage) { h.Dispose(); return new FrontendWatch(null, "unavailable: parent pid " + parentPid + " is " + image + ", not a frontend"); }
                if (!(parentCreated > 0 && childCreated > 0 && parentCreated < childCreated)) // guard:parent-older
                { h.Dispose(); return new FrontendWatch(null, "unavailable: parent pid " + parentPid + " is not older than this process (pid reuse)"); }
                if (WaitForSingleObject(h, 0) == 0) { h.Dispose(); return new FrontendWatch(null, "unavailable: parent pid " + parentPid + " already exited"); }
                return new FrontendWatch(h, "watching " + image + " pid " + parentPid);
            }
            catch (Win32Exception e) { h.Dispose(); return new FrontendWatch(null, "unavailable: parent unreadable (" + e.NativeErrorCode + ")"); }
        }

        public void Dispose() => handle?.Dispose();

        internal static int ParentOf(int pid)
        {
            IntPtr snap = CreateToolhelp32Snapshot(0x2, 0); // TH32CS_SNAPPROCESS
            if (snap == new IntPtr(-1)) return 0;
            try
            {
                var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
                for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
                    if (e.th32ProcessID == (uint)pid) return (int)e.th32ParentProcessID;
                return 0;
            }
            finally { CloseHandle(snap); }
        }

        private static string ImageOf(SafeProcessHandle h)
        {
            var buf = new char[1024];
            uint n = (uint)buf.Length;
            if (!QueryFullProcessImageNameW(h, 0, buf, ref n)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return Path.GetFileName(new string(buf, 0, (int)n));
        }

        private static long CreatedOf(SafeProcessHandle h)
        {
            if (!GetProcessTimes(h, out long created, out _, out _, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return created;
        }

        private static long CreatedOf(int pid)
        {
            using var h = OpenProcess(0x1000, false, pid);
            if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            return CreatedOf(h);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32W
        {
            public uint dwSize, cntUsage, th32ProcessID; public IntPtr th32DefaultHeapID;
            public uint th32ModuleID, cntThreads, th32ParentProcessID; public int pcPriClassBase; public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W e);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W e);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageNameW(SafeProcessHandle h, uint flags, [Out] char[] name, ref uint size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeProcessHandle h, out long created, out long exited, out long kernel, out long user);
        [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeProcessHandle h, uint ms);
    }
}
