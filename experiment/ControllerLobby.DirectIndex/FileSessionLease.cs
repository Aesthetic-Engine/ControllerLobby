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
    /// <summary>Path of the archived marker when this acquisition recovered an interrupted session, else null.</summary>
    public string? RecoveredMarker { get; private set; }

    public static FileSessionLease? Acquire(string existingPrivateDirectory) => Acquire(existingPrivateDirectory, false);

    // recoverInterrupted: the exclusive lock handle is released by Windows when its owner process ends, so
    // holding it proves no live session remains. A marker found while holding the lock is therefore left by
    // a session that ended without completing (power loss, killed process, launch failure). Its emulator
    // job was KILL_ON_JOB_CLOSE, and normal play never writes the user's emulator configuration, so the
    // marker is archived beside the lease (evidence kept) and a new session starts. Trials keep refusing.
    public static FileSessionLease? Acquire(string existingPrivateDirectory, bool recoverInterrupted)
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
            string? recovered = null;
            if (/*guard:pending-recovery*/ (File.Exists(pending) || Directory.Exists(pending)))
            {
                if (!recoverInterrupted || Directory.Exists(pending) || File.GetAttributes(pending).HasFlag(FileAttributes.ReparsePoint)) { file.Dispose(); return null; }
                recovered = Path.Combine(existingPrivateDirectory, "interrupted-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ") + "-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
                File.Move(pending, recovered); // guard:recover-interrupted
                PruneArchived(existingPrivateDirectory, 10);
            }
            using (var marker = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                marker.Write(Encoding.UTF8.GetBytes("ControllerLobby session in progress; started " + DateTime.UtcNow.ToString("O") + " by process " + Environment.ProcessId + ".\n"));
                marker.Flush(true);
            }
            return new(file, pending) { RecoveredMarker = recovered };
        }
        catch { file.Dispose(); throw; }
    }
    private static void PruneArchived(string directory, int keep)
    {
        foreach (var old in new DirectoryInfo(directory).GetFiles("interrupted-*.txt").OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(keep))
            try { old.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    public void Complete()
    {
        if (!Held) throw new InvalidOperationException("lease lost");
        File.Delete(pending);
    }
    public void Dispose() { handle?.Dispose(); handle = null; }
}
