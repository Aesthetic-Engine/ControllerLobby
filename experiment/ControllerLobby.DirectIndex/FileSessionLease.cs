using System.Text;

namespace ControllerLobby.DirectIndex;

// The isolated package must supply one fixed per-user directory, never a session-
// specific directory. Exclusive handle covers competing experiment processes.
// An interrupted/unconfirmed run leaves a durable marker; inspection is required
// before another run. We do not infer death from a PID or steal a stale lease.
public sealed class FileSessionLease : IExperimentLease
{
    private FileStream? handle;
    private readonly string pending;
    private FileSessionLease(FileStream file, string marker) { handle = file; pending = marker; }
    public bool Held => handle != null && !handle.SafeFileHandle.IsClosed;

    public static FileSessionLease? Acquire(string existingPrivateDirectory)
    {
        if (!Path.IsPathFullyQualified(existingPrivateDirectory) || !Directory.Exists(existingPrivateDirectory))
            throw new ArgumentException("existing absolute private directory required");
        for (string? path = Path.GetFullPath(existingPrivateDirectory); path != null; path = Path.GetDirectoryName(path))
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("reparse lease directory");
        string lockPath = Path.Combine(existingPrivateDirectory, "direct-index.lock");
        string pending = Path.Combine(existingPrivateDirectory, "direct-index.pending");
        if (File.Exists(lockPath) && File.GetAttributes(lockPath).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("reparse lease file");
        FileStream file;
        try { file = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
        try
        {
            if (/*guard:pending-recovery*/ (File.Exists(pending) || Directory.Exists(pending))) { file.Dispose(); return null; }
            using (var marker = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                marker.Write(Encoding.UTF8.GetBytes("DirectIndexExperiment pending; no new routing; inspection required after interrupted run.\n"));
                marker.Flush(true);
            }
            return new(file, pending);
        }
        catch { file.Dispose(); throw; }
    }
    public void Complete()
    {
        if (!Held) throw new InvalidOperationException("lease lost");
        File.Delete(pending);
    }
    public void Dispose() { handle?.Dispose(); handle = null; }
}
