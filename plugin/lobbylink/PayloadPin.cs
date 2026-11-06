using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

// Integration brief I1 r2 section 4.1: the lobby is a complete file set. The install manifest pins every file's
// SHA256, and the folder must contain exactly those files. Verify opens every file with read sharing only (no write,
// no delete) and keeps the handles until disposed, so the verified bytes cannot be swapped before the lobby has ended.
namespace ControllerLobby.LobbyLink
{
    public sealed class PayloadLock : IDisposable
    {
        private readonly List<FileStream> held;
        internal PayloadLock(List<FileStream> held, IReadOnlyCollection<string> files) { this.held = held; Files = files; }
        // The verified relative paths (forward slashes). The child may only be one of these.
        public IReadOnlyCollection<string> Files { get; }
        public void Dispose() { foreach (var f in held) f.Dispose(); held.Clear(); }
    }

    public static class PayloadPin
    {
        // Manifest lines: "<64 hex>  <relative path>", forward or back slashes. Returns null and a lock on success,
        // otherwise the reason. Any changed, missing or extra file is a mismatch.
        public static string? Verify(string folder, IEnumerable<string> manifestLines, out PayloadLock? payloadLock)
        {
            payloadLock = null;
            var pins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in manifestLines)
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                int split = line.IndexOf("  ", StringComparison.Ordinal);
                if (split != 64) return "payload-manifest-malformed";
                string hash = line.Substring(0, 64), path = Normal(line.Substring(66));
                if (!hash.All(Uri.IsHexDigit) || path.Length == 0 || path.Contains("..") || Path.IsPathRooted(path) || pins.ContainsKey(path))
                    return "payload-manifest-malformed";
                pins[path] = hash.ToUpperInvariant();
            }
            if (pins.Count == 0) return "payload-manifest-malformed";
            string root = Path.GetFullPath(folder);
            if (!Directory.Exists(root)) return "payload-mismatch: folder missing";
            var present = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(f => Normal(Path.GetRelativePath(root, f))).ToList();
            var extra = present.Where(p => !pins.ContainsKey(p)).ToList();
            if (extra.Count > 0) return "payload-mismatch: extra " + extra[0];
            var held = new List<FileStream>();
            try
            {
                foreach (var (path, hash) in pins) // mutation:payload-scope
                {
                    string full = Path.Combine(root, path);
                    if (!File.Exists(full)) { Release(held); return "payload-mismatch: missing " + path; }
                    var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
                    held.Add(stream);
                    string actual = Convert.ToHexString(SHA256.HashData(stream));
                    if (actual != hash) { Release(held); return "payload-mismatch: changed " + path; }
                }
            }
            catch (IOException e) { Release(held); return "payload-mismatch: unreadable (" + e.GetType().Name + ")"; }
            catch (UnauthorizedAccessException) { Release(held); return "payload-mismatch: unreadable"; }
            payloadLock = new PayloadLock(held, pins.Keys.ToList());
            return null;
        }

        private static string Normal(string path) => path.Replace('\\', '/').Trim();
        private static void Release(List<FileStream> held) { foreach (var f in held) f.Dispose(); }
    }
}
