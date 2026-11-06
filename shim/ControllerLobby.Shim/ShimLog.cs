using System;
using System.IO;

// The shim's evidence log (LAUNCHER-SHIM.md section 12). Opened once for the shim's whole life with read/write sharing,
// so another reader never makes a line fail. If it cannot be opened, Available is false and the shim refuses before
// any lobby opens. A later write failure sets Failed: the shim ends the lobby and exits 4 (evidence failed), never 0.
namespace ControllerLobby.Shim
{
    internal sealed class ShimLog
    {
        private readonly StreamWriter writer;
        private volatile bool failed;
        public bool Available => writer != null;
        public bool Failed => failed;

        public ShimLog(string folder, Func<string, Stream> openForTest = null)
        {
            try
            {
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, "shim-log-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Environment.ProcessId + ".txt");
                Stream stream = openForTest != null ? openForTest(path)
                    : new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                writer = new StreamWriter(stream) { AutoFlush = true };
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException) { writer = null; }
        }

        public void Write(string line)
        {
            if (writer == null || failed) return;
            try { lock (writer) writer.WriteLine(DateTime.UtcNow.ToString("o") + " " + line); }
            catch (Exception e) when (e is IOException or ObjectDisposedException or NotSupportedException) { failed = true; } // guard:write-failure
        }
    }
}
