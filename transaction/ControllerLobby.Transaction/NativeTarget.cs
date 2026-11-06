using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

// S6: the native target, created suspended and already inside a kill-on-close job (PROC_THREAD_ATTRIBUTE_JOB_LIST, so
// there is no window between creation and assignment; LAUNCHER-SHIM section 5). The process, thread and job handles
// from creation are held for the whole transaction. Used by the checks with harmless stand-in targets only.
namespace ControllerLobby.Transaction
{
    public sealed class NativeTargetFactory : ITargetFactory
    {
        private readonly string applicationName, completeCommandLine, workingDirectory;

        private NativeTargetFactory(string applicationName, string completeCommandLine, string workingDirectory)
        {
            this.applicationName = applicationName; this.completeCommandLine = completeCommandLine; this.workingDirectory = workingDirectory;
        }

        /// <summary>S8 (LAUNCHER-SHIM section 3): lpApplicationName is exactly <paramref name="applicationName"/>, and
        /// lpCommandLine is a writable UTF-16 copy of <paramref name="completeCommandLine"/>, passed unchanged: nothing is
        /// added, quoted or trimmed. The caller builds the whole line, argv0 included. lpCurrentDirectory is the explicit
        /// <paramref name="workingDirectory"/> (the shim keeps the target's folder); the environment is
        /// inherited.</summary>
        public static NativeTargetFactory WithCompleteCommandLine(string applicationName, string completeCommandLine, string workingDirectory) =>
            new(applicationName ?? throw new ArgumentNullException(nameof(applicationName)),
                completeCommandLine ?? throw new ArgumentNullException(nameof(completeCommandLine)),
                workingDirectory ?? throw new ArgumentNullException(nameof(workingDirectory)));

        /// <summary>The native call itself, bound directly to the kernel32 import with nothing in between. Internal: only
        /// the offline checks (InternalsVisibleTo) wrap it to record the exact arguments at the call boundary.</summary>
        internal delegate bool CreateProcessFn(string? app, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
            bool inheritHandles, uint flags, IntPtr environment, string? directory, ref STARTUPINFOEX startupInfo, out PROCESS_INFORMATION info);
        internal CreateProcessFn CreateProcess { get; set; } = CreateProcessW;

        public ISuspendedTarget? CreateSuspendedInJob(List<string> failures)
        {
            IntPtr job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero) { failures.Add("CreateJobObject " + Marshal.GetLastWin32Error()); return null; }
            IntPtr attributes = IntPtr.Zero, jobList = IntPtr.Zero;
            try
            {
                var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                limits.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                if (!SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
                { failures.Add("SetInformationJobObject " + Marshal.GetLastWin32Error()); CloseHandle(job); return null; }
                IntPtr size = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                attributes = Marshal.AllocHGlobal(size);
                if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) { failures.Add("InitializeProcThreadAttributeList"); CloseHandle(job); return null; }
                jobList = Marshal.AllocHGlobal(IntPtr.Size); Marshal.WriteIntPtr(jobList, job);
                if (!UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x2000D, jobList, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                { failures.Add("UpdateProcThreadAttribute(job list)"); CloseHandle(job); return null; }
                var si = new STARTUPINFOEX();
                si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
                si.lpAttributeList = attributes;
                var commandLine = new StringBuilder(completeCommandLine, completeCommandLine.Length + 1); // writable, unchanged
                if (!CreateProcess(applicationName, commandLine, IntPtr.Zero, IntPtr.Zero, false, 0x4 | 0x80000 | 0x08000000, // guard:application-name // SUSPENDED | EXTENDED_STARTUPINFO | NO_WINDOW
                        IntPtr.Zero, workingDirectory, ref si, out var pi))
                { failures.Add("CreateProcess " + Marshal.GetLastWin32Error()); CloseHandle(job); return null; }
                if (!IsProcessInJob(pi.hProcess, job, out bool inJob) || !inJob)
                {
                    TerminateProcess(pi.hProcess, 1); CloseHandle(pi.hThread); CloseHandle(pi.hProcess); CloseHandle(job);
                    failures.Add("target not in its job before resume; terminated"); return null;
                }
                return new NativeTarget(job, pi.hProcess, pi.hThread, pi.dwProcessId);
            }
            finally
            {
                if (attributes != IntPtr.Zero) { DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); }
                if (jobList != IntPtr.Zero) Marshal.FreeHGlobal(jobList);
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct STARTUPINFO
        {
            public int cb; public string? lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
            public uint dwFlags; public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }
        [StructLayout(LayoutKind.Sequential)] internal struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
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
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObjectW(IntPtr sa, string? name);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);
        [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessW(string? app, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
            bool inheritHandles, uint flags, IntPtr environment, string? directory, ref STARTUPINFOEX startupInfo, out PROCESS_INFORMATION info);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool CloseHandle(IntPtr handle);
    }

    public sealed partial class NativeTarget : ISuspendedTarget
    {
        private IntPtr job, process, thread;
        public int Pid { get; }

        internal NativeTarget(IntPtr job, IntPtr process, IntPtr thread, int pid) { this.job = job; this.process = process; this.thread = thread; Pid = pid; }

        /// <summary>Facts recorded for the checks, read through the held handles only (never by pid).</summary>
        public bool RootEndedAtDispose { get; private set; }
        /// <summary>True only when Dispose closed every held handle successfully; a failed close keeps it false for good.</summary>
        public bool HandlesReleased { get; private set; }
        private bool releaseFailed;
        /// <summary>The close call, bound to CloseHandle; internal so the offline checks can inject a reported failure.</summary>
        internal Func<IntPtr, bool> CloseFn { get; set; } = NativeTargetFactory.CloseHandle;
        public uint LastTotalProcesses { get; private set; }
        public uint LastActiveProcesses { get; private set; } = uint.MaxValue;

        /// <summary>The image the held process handle actually runs (QueryFullProcessImageNameW on the held handle).</summary>
        public string QueryImagePath()
        {
            var buffer = new StringBuilder(32768);
            int size = buffer.Capacity;
            if (!QueryFullProcessImageNameW(process, 0, buffer, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error(), "image query failed");
            return buffer.ToString(0, size);
        }

        public uint Resume() => ResumeThread(thread);

        public void TerminateJob()
        {
            if (!TerminateJobObject(job, 1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "TerminateJobObject failed"); // guard:terminate-checked
        }

        public bool WaitRootExit(TimeSpan bound)
        {
            uint r = WaitForSingleObject(process, Ms(bound));
            if (r == 0) return true;
            if (r == 0x102) return false;
            throw new Win32Exception(Marshal.GetLastWin32Error(), "wait on the target returned 0x" + r.ToString("X")); // guard:wait-checked
        }

        public int RootExitCode
        {
            get
            {
                if (!GetExitCodeProcess(process, out uint c)) throw new Win32Exception(Marshal.GetLastWin32Error(), "GetExitCodeProcess failed");
                // 259 is STILL_ACTIVE only while the process runs; a signalled handle means the application really
                // exited with 259 .
                if (c == 259 && WaitForSingleObject(process, 0) != 0) throw new InvalidOperationException("target reports STILL_ACTIVE"); // guard:still-active
                return unchecked((int)c);
            }
        }

        public bool WaitJobEmpty(TimeSpan bound)
        {
            var deadline = DateTime.UtcNow + bound;
            while (true)
            {
                if (!QueryInformationJobObject(job, 1, out JOBOBJECT_BASIC_ACCOUNTING_INFORMATION info, Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(), IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "job accounting unreadable");
                LastTotalProcesses = info.TotalProcesses; LastActiveProcesses = info.ActiveProcesses;
                if (info.ActiveProcesses == 0) return true;
                if (DateTime.UtcNow >= deadline) return false;
                System.Threading.Thread.Sleep(20);
            }
        }

        public void Dispose()
        {
            // Termination is asynchronous: allow a short, bounded wait on the held handle before recording the fact.
            if (process != IntPtr.Zero) RootEndedAtDispose = WaitForSingleObject(process, 2000) == 0;
            int failedCloses = 0, error = 0;
            void Close(ref IntPtr h) { if (h != IntPtr.Zero) { if (!CloseFn(h)) { failedCloses++; error = Marshal.GetLastWin32Error(); } h = IntPtr.Zero; } }
            Close(ref thread);
            Close(ref process);
            Close(ref job); // kill-on-close: anything left ends here
            if (failedCloses > 0) releaseFailed = true; // sticky: a later Dispose with cleared fields is not a success
            HandlesReleased = !releaseFailed; // guard:released-only-when-confirmed
            if (failedCloses > 0) throw new Win32Exception(error, failedCloses + " handle close(s) failed"); // guard:close-checked
        }

        private static uint Ms(TimeSpan t) => (uint)Math.Max(0, Math.Min(uint.MaxValue - 1, t.TotalMilliseconds));

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
        {
            public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
            public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
        }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(IntPtr job, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint ms);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool QueryInformationJobObject(IntPtr job, int infoClass, out JOBOBJECT_BASIC_ACCOUNTING_INFORMATION info, int size, IntPtr returnLength);
    }
}
