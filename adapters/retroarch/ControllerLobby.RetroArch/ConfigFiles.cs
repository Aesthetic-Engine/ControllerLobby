using System.Text;
using System.Security.Cryptography;

namespace ControllerLobby.RetroArch;

// Explicit read-only source; no environment expansion, recursive includes or writes to owner files.
public sealed class DiskConfigReader : IConfigReader
{
    private readonly Func<string, FileAttributes> query;
    public DiskConfigReader() : this(File.GetAttributes) { }
    internal DiskConfigReader(Func<string, FileAttributes> query) { this.query = query; }
    public ConfigRead Read(string absolutePath)
    {
        try
        {
            string path = Path.GetFullPath(absolutePath);
            if (FileTreeGuard.HasReparse(path, item => FileTreeGuard.Attributes(item, query))) return new(ReadState.Unreadable);
            var attributes = FileTreeGuard.Attributes(path, query);
            if (attributes == null) return new(ReadState.Absent);
            if (attributes.Value.HasFlag(FileAttributes.Directory)) return new(ReadState.Unreadable);
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (/*guard:read-size*/ (file.Length > 4 * 1024 * 1024)) return new(ReadState.Unreadable);
            byte[] raw = new byte[(int)file.Length]; file.ReadExactly(raw);
            return new(ReadState.Present, new UTF8Encoding(false, true).GetString(raw), Convert.ToHexString(SHA256.HashData(raw)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or DecoderFallbackException)
        { return new(ReadState.Unreadable); }
    }
}

public static class CandidateFile
{
    // This is artifact creation only. No command line, process start, qualification or Ready is produced.
    public static string WriteNew(MappingResult result, string newSessionDirectory, string ownerRoot)
    {
        if (/*guard:write-candidate*/ (result.Status != AnalysisStatus.Candidate || result.AppendConfig == null)) throw new ArgumentException("not a candidate");
        if (/*guard:write-absolute*/ (!Path.IsPathFullyQualified(newSessionDirectory) || !Path.IsPathFullyQualified(ownerRoot))) throw new ArgumentException("absolute paths required");
        string session = Path.GetFullPath(newSessionDirectory), owner = Path.GetFullPath(ownerRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (/*guard:write-owner-match*/ (!Path.GetFullPath(result.OwnerRoot).TrimEnd(Path.DirectorySeparatorChar).Equals(owner, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("owner root mismatch");
        if (/*guard:write-outside-owner*/ (session.Equals(owner, StringComparison.OrdinalIgnoreCase)
            || session.StartsWith(owner + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("session inside owner root");
        if (/*guard:write-new-directory*/ (Directory.Exists(session) || File.Exists(session))) throw new IOException("session path exists");
        if (FileTreeGuard.HasReparse(Path.GetDirectoryName(session)!)) throw new IOException("reparse session parent");
        Directory.CreateDirectory(session);
        string target = Path.Combine(session, "retroarch-append.cfg");
        using var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        byte[] raw = new UTF8Encoding(false, true).GetBytes(result.AppendConfig);
        file.Write(raw); file.Flush(true);
        return target; // partial-write failures retain their new folder/file for inspection
    }
}

internal static class FileTreeGuard
{
    // Only explicit missing-file/path errors establish absence. Denied metadata and
    // every other I/O error propagate; DiskConfigReader classifies them Unreadable.
    internal static FileAttributes? Attributes(string path, Func<string, FileAttributes> query)
    {
        try { return query(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        // control:metadata-denied must never be converted to absence
    }
    internal static bool HasReparse(string path) => HasReparse(path, item => Attributes(item, File.GetAttributes));
    internal static bool HasReparse(string path, Func<string, FileAttributes?> attributes)
    {
        for (string? item = Path.GetFullPath(path); item != null; item = Path.GetDirectoryName(item))
        {
            var captured = attributes(item);
            if (/*guard:reparse*/ (captured.HasValue && captured.Value.HasFlag(FileAttributes.ReparsePoint))) return true;
        }
        return false;
    }
}
