using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ControllerLobby.Transaction
{
    public sealed record LoadedImage(string Path, string Sha256);
    public sealed record HeldProcessIdentity(int ProcessId, long CreationFileTimeUtc, string ImagePath);

    public sealed partial class NativeTarget
    {
        // Reads through the process handle held since suspended creation. No
        // process-by-PID reopen or arbitrary process command-line enumeration.
        public HeldProcessIdentity QueryIdentity()
        {
            if (process == IntPtr.Zero) throw new ObjectDisposedException(nameof(NativeTarget));
            if (!GetProcessTimes(process, out long created, out _, out _, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Process creation identity unreadable");
            return new(Pid, created, QueryImagePath());
        }
        public IReadOnlyList<LoadedImage> QueryLoadedImages()
        {
            if (process == IntPtr.Zero) throw new ObjectDisposedException(nameof(NativeTarget));
            return NativeImageObservation.Capture(process);
        }
        public IReadOnlyList<string> QueryLoadedImagePaths()
        {
            if(process==IntPtr.Zero)throw new ObjectDisposedException(nameof(NativeTarget));
            return NativeImageObservation.CapturePaths(process);
        }
        public bool RequestWindowClose()
        {
            if (process == IntPtr.Zero) throw new ObjectDisposedException(nameof(NativeTarget));
            if (WaitRootExit(TimeSpan.Zero)) return true;
            bool sent=false, failed=false;
            EnumWindowsProc visit=(window, _) =>
            {
                GetWindowThreadProcessId(window,out uint pid);
                if(pid==(uint)Pid)
                {
                    if(PostMessageW(window,0x0010,IntPtr.Zero,IntPtr.Zero))sent=true; // WM_CLOSE only to owned root
                    else failed=true;
                }
                return true;
            };
            if (!EnumWindows(visit,IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error(),"Owned-window enumeration failed");
            return sent&&!failed;
        }
        private delegate bool EnumWindowsProc(IntPtr window,IntPtr parameter);
        [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetProcessTimes(IntPtr process,out long created,out long exited,out long kernel,out long user);
        [DllImport("user32.dll",SetLastError=true)]private static extern bool EnumWindows(EnumWindowsProc callback,IntPtr parameter);
        [DllImport("user32.dll",SetLastError=true)]private static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
        [DllImport("user32.dll",SetLastError=true)]private static extern bool PostMessageW(IntPtr window,uint message,IntPtr wparam,IntPtr lparam);
    }
    public static class NativeImageObservation
    {
        public static IReadOnlyList<LoadedImage> Current()=>Capture(GetCurrentProcess());
        internal static IReadOnlyList<LoadedImage> Capture(IntPtr heldProcess)
            =>HashImages(CapturePaths(heldProcess));
        public static IReadOnlyList<LoadedImage> HashImages(IReadOnlyList<string> paths)
        {
            var result=new List<LoadedImage>();
            foreach(string absolute in paths)
            {
                for(string? ancestor=absolute;ancestor!=null;ancestor=Path.GetDirectoryName(ancestor))
                    if(File.GetAttributes(ancestor).HasFlag(FileAttributes.ReparsePoint))throw new IOException("Reparse loaded-image path");
                using var file=new FileStream(absolute,FileMode.Open,FileAccess.Read,FileShare.Read);
                result.Add(new(absolute,Convert.ToHexString(SHA256.HashData(file))));
            }
            return result.AsReadOnly();
        }
        internal static IReadOnlyList<string> CapturePaths(IntPtr heldProcess)
        {
            var handles=new IntPtr[4096];
            if(!K32EnumProcessModulesEx(heldProcess,handles,handles.Length*IntPtr.Size,out uint bytes,3)
                || bytes==0||bytes%IntPtr.Size!=0||bytes>handles.Length*IntPtr.Size)
                throw new Win32Exception(Marshal.GetLastWin32Error(),"Loaded-image observation incomplete");
            var result=new List<string>();
            for(int index=0;index<bytes/IntPtr.Size;index++)
            {
                var path=new StringBuilder(32768);
                uint count=K32GetModuleFileNameExW(heldProcess,handles[index],path,path.Capacity);
                if(count==0||count>=path.Capacity-1)throw new Win32Exception(Marshal.GetLastWin32Error(),"Loaded-image path incomplete");
                string absolute=Path.GetFullPath(path.ToString());
                result.Add(absolute);
            }
            return result.AsReadOnly();
        }
        [DllImport("kernel32.dll")]private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll",SetLastError=true)]private static extern bool K32EnumProcessModulesEx(IntPtr process,[Out]IntPtr[] modules,int bytes,out uint needed,uint filter);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern uint K32GetModuleFileNameExW(IntPtr process,IntPtr module,StringBuilder path,int length);
    }
}
