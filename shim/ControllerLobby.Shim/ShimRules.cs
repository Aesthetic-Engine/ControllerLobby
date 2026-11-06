using System;
using System.IO;
using System.Text.Json;

// ControllerLobby launcher shim, pure rules (design contract/LAUNCHER-SHIM.md r3; LAUNCH-STRATEGY.md
// section 3, measurement 3): the first-token grammar of the shim's own command line (section 3) and the sidecar
// (sections 1, 4). This first slice is Esc-only: the sidecar mode must be "esc-only", and no outcome starts the target.
// No I/O except reading the sidecar file.
namespace ControllerLobby.Shim
{
    public static class ShimRules
    {
        // Splits the raw command line (GetCommandLineW) into the first token and the unmodified tail.
        // Accepted forms only:
        //   "..."  nonempty, closing quote followed by end of line, space or tab; the tail starts after the quote;
        //   text   nonempty, runs to the first space or tab or the end; the tail starts at that space or tab.
        // Returns null on success, otherwise the reason (the shim then refuses and launches nothing).
        public static string SplitFirstToken(string raw, out string token, out string tail)
        {
            token = null; tail = null;
            if (string.IsNullOrEmpty(raw)) return "empty-command-line";
            if (raw[0] == '"')
            {
                int close = raw.IndexOf('"', 1);
                if (close < 0) return "unterminated-quote";
                if (close == 1) return "empty-token";
                int after = close + 1;
                if (after < raw.Length && raw[after] != ' ' && raw[after] != '\t') return "text-after-closing-quote";
                token = raw.Substring(1, close - 1);
                tail = raw.Substring(after);
                return null;
            }
            int end = raw.IndexOfAny(new[] { ' ', '\t' });
            if (end == 0) return "empty-token";
            token = end < 0 ? raw : raw.Substring(0, end);
            tail = end < 0 ? "" : raw.Substring(end);
            return null;
        }

        // The child's command line: a quoted resolved target (argv0) followed by the unmodified tail.
        public static string BuildChildCommandLine(string resolvedTarget, string tail)
        {
            return "\"" + resolvedTarget + "\"" + tail;
        }

        public sealed class Sidecar
        {
            public string TargetPath;     // absolute, inside the shim's folder; validated, never started in this slice
            public string Mode;           // only "esc-only" is accepted
            public string LogDirectory;   // optional; default %LOCALAPPDATA%\ControllerLobbyTest
        }

        // Reads and validates the sidecar next to the shim. Returns null on success, otherwise the reason.
        // Target rules: a bare file name, resolved only in the shim's folder, existing, not the shim itself, no PATH.
        // Qualification: only a JSON boolean true qualifies; anything else (missing, null, string, malformed) does not.
        public static string ReadSidecar(string shimPath, out Sidecar sidecar)
        {
            sidecar = null;
            string dir = Path.GetDirectoryName(Path.GetFullPath(shimPath));
            string file = Path.Combine(dir, "ControllerLobby.Shim.json");
            if (!File.Exists(file)) return "sidecar-missing";
            JsonDocument doc;
            string text;
            try { text = File.ReadAllText(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return "sidecar-unreadable"; } // guard:sidecar-io
            try { doc = JsonDocument.Parse(text); }
            catch (JsonException) { return "sidecar-malformed"; }
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return "sidecar-malformed";
                JsonElement t;
                if (!root.TryGetProperty("target", out t) || t.ValueKind != JsonValueKind.String) return "target-missing";
                string target = t.GetString();
                string why = ResolveTarget(dir, shimPath, target, out string resolved);
                if (why != null) return why;
                JsonElement m;
                if (!root.TryGetProperty("mode", out m) || m.ValueKind != JsonValueKind.String || m.GetString() != "esc-only") // guard:mode
                    return "mode-not-esc-only";
                var s = new Sidecar { TargetPath = resolved, Mode = "esc-only" };
                JsonElement ld;
                if (root.TryGetProperty("logDirectory", out ld) && ld.ValueKind == JsonValueKind.String) s.LogDirectory = ld.GetString();
                sidecar = s;
                return null;
            }
        }

        public static string ResolveTarget(string dir, string shimPath, string target, out string resolved)
        {
            resolved = null;
            if (string.IsNullOrEmpty(target)) return "target-empty";
            if (target.IndexOfAny(new[] { '\\', '/', ':' }) >= 0 || target == "." || target == "..") return "target-not-a-bare-name";
            if (target.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "target-invalid-name";
            string full = Path.GetFullPath(Path.Combine(dir, target));
            if (!string.Equals(Path.GetDirectoryName(full), dir, StringComparison.OrdinalIgnoreCase)) return "target-outside-folder";
            if (string.Equals(full, Path.GetFullPath(shimPath), StringComparison.OrdinalIgnoreCase)) return "target-is-the-shim";
            if (!File.Exists(full)) return "target-missing-file";
            resolved = full;
            return null;
        }

        // The shim's exit for each lobby decision. Esc-only slice: there is no launching outcome at all, so a confirm in
        // the lobby also ends with no game (LAUNCH-STRATEGY section 3, measurement 3; forwarding disabled by construction).
        public enum Exit { NoGameCancelled = 0, NoGameConfirmedForwardingDisabled = 0, Refused = 3, EvidenceFailed = 4 }

        // A display title from BigBox's command-line tail: the file name of the last quoted argument, without its
        // extension. Purely cosmetic for the lobby window; it never selects anything.
        public static string TitleFromTail(string tail)
        {
            if (string.IsNullOrEmpty(tail)) return "Game";
            int end = tail.LastIndexOf('"');
            if (end <= 0) return "Game";
            int start = tail.LastIndexOf('"', end - 1);
            if (start < 0) return "Game";
            string arg = tail.Substring(start + 1, end - start - 1);
            string name;
            try { name = Path.GetFileNameWithoutExtension(arg); } catch (ArgumentException) { return "Game"; }
            return string.IsNullOrWhiteSpace(name) ? "Game" : name;
        }
    }
}
