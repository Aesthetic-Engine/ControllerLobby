using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using ControllerLobby.WindowIdentity;

// Integration brief I1 section 4: start the lobby executable and wait for its one-line result.
// - The child is created suspended, inside a kill-on-close job (PROC_THREAD_ATTRIBUTE_JOB_LIST), and is resumed only
//   after the job membership is verified. A BigBox exit or crash closes the job handle and ends the lobby.
// - Only the stdout pipe's write end is inherited (PROC_THREAD_ATTRIBUTE_HANDLE_LIST); no stdin, no stderr.
// - The foreground is granted to the child before it runs (AllowSetForegroundWindow).
// - No idle timeout (contract section 12). Two guards only stop a stuck lobby: no visible window within
//   NoWindowSeconds, or a window that stays hung (IsHungAppWindow) for HungSeconds. Two bounds end a lobby that is
//   finished but does not exit: PostResultExitSeconds after its record, WindowGoneExitSeconds after its window closed.
// - The output counts only after end of file with no read error and a finished reader (OutputComplete).
namespace ControllerLobby.LobbyLink
{
    public sealed class LinkOptions
    {
        public int NoWindowSeconds { get; init; } = 15;   // placeholder (brief I1 section 4)
        public int HungSeconds { get; init; } = 10;       // placeholder, counted after Windows reports the window hung
        public int PostResultExitSeconds { get; init; } = 3; // placeholder (4.6): exit required after the record
        public int WindowGoneExitSeconds { get; init; } = 3; // placeholder: exit required once a shown window is gone
        public bool InjectReadFaultForTest { get; init; }    // offline checks only: the reader fails after its first chunk
        public int PollMilliseconds { get; init; } = 200;
        public Func<bool>? AbortRequested { get; init; }  // caller-side stop (for example, its evidence log failed)
        public Action<int>? OnStarted { get; init; }      // offline checks only: receives the child's process id
        public int FocusAcquireSeconds { get; init; } = 5; // placeholder (r3 9.4): startup focus guard after the window shows
        public IWindowSystem Windows { get; init; } = RealWindowSystem.Instance; // offline checks script this seam
        public IWindowGraph Graph { get; init; } = RealWindowGraph.Instance;    // r3 section 12: read-only z-order reads
        public int ZOrderSampleMilliseconds { get; init; } = 500;
    }

    public static class LobbyProcess
    {
        // Windows command-line quoting for one argument (the rules CommandLineToArgvW and the C runtime parse).
        public static string Quote(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) return arg;
            var s = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"') { s.Append('\\', backslashes * 2 + 1).Append('"'); backslashes = 0; continue; }
                s.Append('\\', backslashes).Append(c); backslashes = 0;
            }
            return s.Append('\\', backslashes * 2).Append('"').ToString();
        }

        public static string CommandLine(string exePath, Guid session, string title, string platform)
            => Quote(exePath) + " --session " + session.ToString("D") + " --game-title " + Quote(title) + " --platform " + Quote(platform);

        public static RunOutcome Run(string exePath, Guid session, string title, string platform, LinkOptions options, Action<string> log)
        {
            IntPtr job = IntPtr.Zero, readEnd = IntPtr.Zero, writeEnd = IntPtr.Zero, attributes = IntPtr.Zero;
            IntPtr handleList = IntPtr.Zero, jobList = IntPtr.Zero;
            var pi = new PROCESS_INFORMATION();
            bool readerStarted = false;
            var thread = Thread.CurrentThread;
            log("caller pid=" + Environment.ProcessId + " thread=" + thread.ManagedThreadId + " pool=" + thread.IsThreadPoolThread +
                "; foreground at entry " + options.Windows.Foreground().Describe());
            try
            {
                // Opt-in abort is honoured before anything is created : an aborted run starts no child.
                if (options.AbortRequested?.Invoke() == true) // mutation:abort-before-create
                {
                    log("caller requested abort before the lobby was created; nothing started");
                    return new RunOutcome(null, Array.Empty<byte>(), GuardReason: "aborted");
                }
                job = CreateJobObjectW(IntPtr.Zero, null);
                if (job == IntPtr.Zero) return Fail("CreateJobObject", log);
                var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE; // mutation:kill-on-close
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limits, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
                    return Fail("SetInformationJobObject", log);

                var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = true };
                if (!CreatePipe(out readEnd, out writeEnd, ref sa, 0)) return Fail("CreatePipe", log);
                if (!SetHandleInformation(readEnd, HANDLE_FLAG_INHERIT, 0)) return Fail("SetHandleInformation", log);

                IntPtr size = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
                attributes = Marshal.AllocHGlobal(size);
                if (!InitializeProcThreadAttributeList(attributes, 2, 0, ref size)) { Marshal.FreeHGlobal(attributes); attributes = IntPtr.Zero; return Fail("InitializeProcThreadAttributeList", log); }
                handleList = Marshal.AllocHGlobal(IntPtr.Size); Marshal.WriteIntPtr(handleList, writeEnd);
                if (!UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handleList, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                    return Fail("UpdateProcThreadAttribute(handle list)", log);
                jobList = Marshal.AllocHGlobal(IntPtr.Size); Marshal.WriteIntPtr(jobList, job);
                if (!UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_JOB_LIST, jobList, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero)) // mutation:job-list
                    return Fail("UpdateProcThreadAttribute(job list)", log);

                var si = new STARTUPINFOEX();
                si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
                si.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
                si.StartupInfo.hStdOutput = writeEnd;
                si.lpAttributeList = attributes;
                var commandLine = new StringBuilder(CommandLine(exePath, session, title, platform));
                if (!CreateProcessW(exePath, commandLine, IntPtr.Zero, IntPtr.Zero, true,
                        CREATE_SUSPENDED | EXTENDED_STARTUPINFO_PRESENT | CREATE_NO_WINDOW, IntPtr.Zero, Path.GetDirectoryName(exePath), ref si, out pi))
                    return Fail("CreateProcess", log);
                CloseHandle(writeEnd); writeEnd = IntPtr.Zero; // the child now holds the only write end

                if (!IsProcessInJob(pi.hProcess, job, out bool inJob) || !inJob)
                {
                    TerminateProcess(pi.hProcess, 1);
                    log("lobby not in its job before resume; terminated, launch cancelled");
                    return new RunOutcome(null, Array.Empty<byte>(), GuardReason: "not-in-job");
                }
                bool grant = options.Windows.GrantForeground(pi.dwProcessId, out int grantError); // error read inside, before logging
                log("lobby pid " + pi.dwProcessId + " created suspended in its kill-on-close job; foreground grant=" + grant +
                    (grant ? "" : " error=" + grantError));
                options.OnStarted?.Invoke(pi.dwProcessId);

                var capture = new Capture();
                IntPtr readHandle = readEnd;
                bool fault = options.InjectReadFaultForTest;
                var reader = new Thread(() => Read(readHandle, capture, fault)) { IsBackground = true };
                reader.Start();
                readerStarted = true; // the reader's FileStream now owns readEnd
                // ...and again before the suspended child is resumed: an abort raised since creation ends the job, and
                // the child never runs.
                if (options.AbortRequested?.Invoke() == true) // mutation:abort-before-resume
                {
                    TerminateJobObject(job, 1);
                    WaitForSingleObject(pi.hProcess, 5000);
                    reader.Join(2000);
                    log("caller requested abort before the lobby was resumed; its job was ended and it never ran");
                    return new RunOutcome(null, Array.Empty<byte>(), GuardReason: "aborted");
                }
                if (ResumeThread(pi.hThread) == unchecked((uint)-1)) { TerminateJobObject(job, 1); reader.Join(2000); return Fail("ResumeThread", log); }

                string? guard = Watch(pi, capture, options, log, session);
                // Every terminal path ends the job before returning (4.6): nothing the lobby started survives.
                TerminateJobObject(job, 1);
                bool exited = WaitForSingleObject(pi.hProcess, 5000) == WAIT_OBJECT_0;
                bool readerDone = reader.Join(2000);
                // Exit code only from a process proven to have ended; otherwise unknown, which never launches.
                int? exitCode = exited && GetExitCodeProcess(pi.hProcess, out uint code) && code != STILL_ACTIVE ? unchecked((int)code) : null;
                lock (capture)
                {
                    // The output counts only if the reader finished, reached end of file and saw no read error.
                    bool complete = readerDone && capture.Eof && capture.ReadError == null; // mutation:read-complete
                    log("lobby ended exit=" + (exitCode?.ToString() ?? "unknown") + " bytes=" + capture.Bytes.Length +
                        " eof=" + capture.Eof + " readerDone=" + readerDone + (capture.ReadError != null ? " readError=" + capture.ReadError : "") +
                        (guard != null ? " guard=" + guard : "") + (capture.Overflow ? " overflow" : ""));
                    return new RunOutcome(exitCode, capture.Bytes.ToArray(), guard, null, capture.Overflow, complete);
                }
            }
            finally
            {
                if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
                if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
                if (writeEnd != IntPtr.Zero) CloseHandle(writeEnd);
                if (attributes != IntPtr.Zero) { DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); }
                if (handleList != IntPtr.Zero) Marshal.FreeHGlobal(handleList);
                if (jobList != IntPtr.Zero) Marshal.FreeHGlobal(jobList);
                // Closing the job handle ends anything still inside it (kill-on-close), so no lobby outlives this call.
                if (job != IntPtr.Zero) CloseHandle(job);
                // readEnd is owned by the reader's FileStream once the reader started; close it here only otherwise.
                if (readEnd != IntPtr.Zero && !readerStarted) CloseHandle(readEnd);
            }
        }

        private static RunOutcome Fail(string what, Action<string> log)
        {
            string text = what + " failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
            log("lobby start failed: " + text);
            return new RunOutcome(null, Array.Empty<byte>(), StartError: text);
        }

        private sealed class Capture
        {
            internal readonly MemoryStream Bytes = new();
            internal bool Overflow;
            internal Stopwatch? RecordComplete; // started when the first LF arrives
            internal bool Eof;                  // the pipe reported end of file
            internal string? ReadError;         // a read failed
        }

        // Raw bytes, bounded: at most MaxOutputBytes are kept; anything beyond sets Overflow. The reader keeps draining
        // to end of file so the child can never block on a full pipe.
        private static void Read(IntPtr readEnd, Capture capture, bool injectFault)
        {
            try
            {
                using var stream = new FileStream(new SafeFileHandle(readEnd, ownsHandle: true), FileAccess.Read, 1, false);
                var buffer = new byte[4096];
                int n;
                while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    lock (capture)
                    {
                        int room = LobbyResult.MaxOutputBytes - (int)capture.Bytes.Length;
                        if (n > room) capture.Overflow = true;
                        if (room > 0) capture.Bytes.Write(buffer, 0, Math.Min(n, room));
                        if (capture.RecordComplete == null && Array.IndexOf(buffer, (byte)10, 0, n) >= 0) capture.RecordComplete = Stopwatch.StartNew();
                    }
                    if (injectFault) throw new IOException("injected read fault (offline check)");
                }
                lock (capture) capture.Eof = true;
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                lock (capture) capture.ReadError = e.GetType().Name;
            }
        }

        // Returns null when the process exits by itself, otherwise the guard that fired.
        private static string? Watch(PROCESS_INFORMATION pi, Capture capture, LinkOptions options, Action<string> log, Guid session)
        {
            var started = Stopwatch.StartNew();
            Stopwatch? hung = null, gone = null, visible = null;
            bool sawWindow = false, activated = false, focused = false;
            int self = Environment.ProcessId;
            var last = new ForegroundSample(IntPtr.Zero, -1, "");
            Stopwatch? zClock = null;
            string? lastZ = null;
            var observation = new WindowObservation();
            while (WaitForSingleObject(pi.hProcess, (uint)options.PollMilliseconds) == WAIT_TIMEOUT)
            {
                if (options.AbortRequested?.Invoke() == true) // mutation:abort
                {
                    log("caller requested abort; ending the lobby");
                    return "aborted";
                }
                // Foreground sampled every poll, logged when it changes (r3 9.1). Only the sample proves focus (9.3).
                var fg = options.Windows.Foreground();
                if (fg != last) { log("foreground " + fg.Describe() + " at " + started.ElapsedMilliseconds + " ms"); last = fg; }
                if (!focused && fg.OwnerPid == pi.dwProcessId)
                {
                    focused = true;
                    log("lobby has the foreground after " + started.ElapsedMilliseconds + " ms");
                }
                lock (capture)
                {
                    if (capture.RecordComplete != null && capture.RecordComplete.Elapsed.TotalSeconds >= options.PostResultExitSeconds) // mutation:post-result
                    {
                        log("lobby still running " + options.PostResultExitSeconds + " s after its record; ending it");
                        return "no-exit-after-result";
                    }
                }
                IntPtr window = VisibleWindowOf(pi.dwProcessId);
                if (window == IntPtr.Zero)
                {
                    if (!sawWindow && started.Elapsed.TotalSeconds >= options.NoWindowSeconds)
                    {
                        log("lobby showed no window in " + options.NoWindowSeconds + " s; ending it");
                        return "no-window";
                    }
                    if (sawWindow) // mutation:window-gone
                    {
                        gone ??= Stopwatch.StartNew();
                        if (gone.Elapsed.TotalSeconds >= options.WindowGoneExitSeconds)
                        {
                            log("lobby window gone for " + options.WindowGoneExitSeconds + " s without the process ending; ending it");
                            return "no-exit-after-window-closed";
                        }
                    }
                    continue;
                }
                gone = null;
                if (!sawWindow) { sawWindow = true; visible = Stopwatch.StartNew(); log("lobby window visible after " + started.ElapsedMilliseconds + " ms"); }
                // One parent-side activation (r3 9.3), for the validated child window, and only while this process owns the
                // foreground. Never repeated: not after a user switch, not on a timer.
                if (!activated) // mutation:activate-once
                {
                    activated = true;
                    if (fg.OwnerPid == self) // mutation:activate-only-from-host
                    {
                        bool ok = options.Windows.Activate(window, out int activateError);
                        log("parent activation of the lobby window: " + ok + (ok ? "" : " error=" + activateError));
                    }
                    else log("parent activation skipped: the foreground belongs to pid " + fg.OwnerPid + ", not this process");
                }
                // Z-order diagnostic: fresh exact foreground HWND with child/session marker, qualified endpoints, bounded rate,
                // logged on change. It never changes any window.
                if (zClock == null || zClock.ElapsedMilliseconds >= options.ZOrderSampleMilliseconds)
                {
                    zClock = Stopwatch.StartNew();
                    string z = observation.Capture(pi.dwProcessId, options.Windows.Foreground,
                        target => SessionWindowMarker.Matches(target, session, pi.dwProcessId),
                        target => ZOrderDiagnostic.Snapshot(options.Graph, target, pi.dwProcessId, self));
                    if (z != lastZ) { log(z + " at " + started.ElapsedMilliseconds + " ms"); lastZ = z; }
                }
                // Startup focus guard (r3 9.4): a lobby that never had the foreground is ended; once it had it, never.
                if (!focused && visible != null && visible.Elapsed.TotalSeconds >= options.FocusAcquireSeconds) // mutation:focus-guard
                {
                    log("lobby never had the foreground within " + options.FocusAcquireSeconds + " s of its window; ending it");
                    return "no-focus";
                }
                if (IsHungAppWindow(window)) // mutation:hang-guard
                {
                    hung ??= Stopwatch.StartNew();
                    if (hung.Elapsed.TotalSeconds >= options.HungSeconds)
                    {
                        log("lobby window hung: first detected at " + (started.ElapsedMilliseconds - (long)hung.Elapsed.TotalMilliseconds) +
                            " ms, ended at " + started.ElapsedMilliseconds + " ms");
                        return "hung";
                    }
                }
                else hung = null;
            }
            return null;
        }

        private static IntPtr VisibleWindowOf(int pid)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out uint owner);
                if (owner == (uint)pid && IsWindowVisible(hwnd)) { found = hwnd; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private const uint CREATE_SUSPENDED = 0x4, EXTENDED_STARTUPINFO_PRESENT = 0x80000, STARTF_USESTDHANDLES = 0x100;
        private const uint CREATE_NO_WINDOW = 0x08000000; // a console-subsystem lobby gets no console window
        private const uint HANDLE_FLAG_INHERIT = 0x1, WAIT_TIMEOUT = 0x102, WAIT_OBJECT_0 = 0, STILL_ACTIVE = 259;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
        private const int JobObjectExtendedLimitInformation = 9;
        private static readonly IntPtr PROC_THREAD_ATTRIBUTE_HANDLE_LIST = (IntPtr)0x20002;
        private static readonly IntPtr PROC_THREAD_ATTRIBUTE_JOB_LIST = (IntPtr)0x2000D;

        [StructLayout(LayoutKind.Sequential)] private struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle; }
        [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb; public string? lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
            public uint dwFlags; public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }
        [StructLayout(LayoutKind.Sequential)] private struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }
        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit;
            public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)] private struct IO_COUNTERS { public ulong a, b, c, d, e, f; }
        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation; public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }
        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObjectW(IntPtr sa, string? name);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(IntPtr job, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out IntPtr read, out IntPtr write, ref SECURITY_ATTRIBUTES sa, int size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);
        [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessW(string app, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
            bool inheritHandles, uint flags, IntPtr environment, string? directory, ref STARTUPINFOEX startupInfo, out PROCESS_INFORMATION info);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsHungAppWindow(IntPtr hwnd);
    }
}
