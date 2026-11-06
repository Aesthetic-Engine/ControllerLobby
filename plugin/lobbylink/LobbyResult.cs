using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

// Integration brief I1 r2 section 4: the plugin's reading of the lobby process's result. Pure: no process, no LaunchBox.
// Only one well-formed "confirmed" record for this session (4.4), followed by end of file and exit code 0 (4.6),
// launches. Everything else cancels (4.7).
namespace ControllerLobby.LobbyLink
{
    public enum LinkAction { Launch, Cancel }

    // A seat draft entry (4.4, 4.5): a session-local alias, never a device identity.
    public sealed record DraftSeat(int Player, string Alias, string Kind, string Layout);

    public sealed record LinkDecision(LinkAction Action, string Reason, IReadOnlyList<DraftSeat>? Seats)
    {
        // What the plugin logs: the validated fields only, re-serialized as one JSON object (4.5). JSON escapes every
        // control character and quote, so a lobby-supplied reason can never add a line or forge a field in the log.
        public string Describe() => JsonSerializer.Serialize(new
        {
            action = Action.ToString(), reason = Reason, // mutation:describe
            seats = Seats?.Select(s => new { player = s.Player, alias = s.Alias, kind = s.Kind, layout = s.Layout })
        });
    }

    // What the runner observed. Output is the child's raw stdout (at most MaxOutputBytes, Overflow if more arrived).
    // GuardReason is set when the plugin ended the lobby (no window, hung, no exit after the result, not in job).
    // OutputComplete: the reader finished and reached end of file with no read error.
    public sealed record RunOutcome(int? ExitCode, byte[] Output, string? GuardReason = null,
        string? StartError = null, bool Overflow = false, bool OutputComplete = true);

    public static class LobbyResult
    {
        public const int MaxOutputBytes = 16384;
        public const int MaxReasonChars = 200;
        private static readonly string[] RecordProperties = { "version", "session", "outcome", "reason", "seats" };
        private static readonly string[] SeatProperties = { "player", "alias", "kind", "layout" };
        private static readonly string[] Layouts = { "XboxSeries", "Sn30Pro", "ArcadeStick", "GenericPad", "KeyboardMouse" };

        public static LinkDecision Decide(Guid session, RunOutcome run)
        {
            if (run.StartError != null) return Cancel("start-failed: " + run.StartError);
            if (run.GuardReason != null) return Cancel(run.GuardReason);
            if (run.Overflow || run.Output.Length > MaxOutputBytes) return Cancel("output-overflow");
            if (!run.OutputComplete) return Cancel("output-incomplete"); // mutation:incomplete
            byte[] bytes = run.Output;
            if (bytes.Length == 0) return Cancel("no-output");
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Cancel("malformed-output");
            string text;
            try { text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
            catch (DecoderFallbackException) { return Cancel("malformed-output"); }
            if (!text.EndsWith("\n", StringComparison.Ordinal)) return Cancel("unterminated-output");
            string body = text.Substring(0, text.Length - 1);
            if (body.EndsWith("\r", StringComparison.Ordinal)) body = body.Substring(0, body.Length - 1);
            if (body.IndexOf('\n') >= 0) return Cancel("extra-output");
            if (body.IndexOf('\r') >= 0) return Cancel("malformed-output");
            if (body.Trim().Length == 0) return Cancel("no-output");

            JsonDocument doc;
            try { doc = JsonDocument.Parse(body); }
            catch (JsonException) { return Cancel("malformed-output"); }
            using (doc)
            {
                var root = doc.RootElement;
                if (!ExactProperties(root, RecordProperties)) return Cancel("malformed-output");
                var version = root.GetProperty("version");
                if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int v) || v != 1) return Cancel("malformed-output");
                var s = root.GetProperty("session");
                // Canonical form only (4.2): lowercase "D", compared as text, not Guid.TryParse's wider set of formats.
                if (s.ValueKind != JsonValueKind.String || !string.Equals(s.GetString(), session.ToString("D"), StringComparison.Ordinal)) // mutation:session-check
                    return Cancel("wrong-session");
                var o = root.GetProperty("outcome");
                var r = root.GetProperty("reason");
                var seats = root.GetProperty("seats");
                if (o.ValueKind != JsonValueKind.String || r.ValueKind != JsonValueKind.String || seats.ValueKind != JsonValueKind.Array)
                    return Cancel("malformed-output");
                string reason = r.GetString() ?? "";
                if (reason.Length > MaxReasonChars) return Cancel("malformed-output");
                switch (o.GetString())
                {
                    case "cancel":
                    case "error":
                        if (seats.GetArrayLength() != 0) return Cancel("malformed-output");
                        return Cancel((o.GetString() == "cancel" ? "cancel" : "lobby-error") + (reason.Length > 0 ? ": " + reason : ""));
                    case "confirmed":
                        var list = Seats(seats);
                        if (list == null) return Cancel("invalid-seats");
                        if (run.ExitCode != 0) return Cancel("nonzero-exit");
                        return new LinkDecision(LinkAction.Launch, "confirmed", list);
                    default:
                        return Cancel("unknown-outcome");
                }
            }
        }

        // 1 to 4 entries; player 1..k in order; unique aliases s1..s4; kind pad or keyboard (at most one keyboard);
        // layout from the five, KeyboardMouse exactly for the keyboard seat. Null when anything is off.
        private static List<DraftSeat>? Seats(JsonElement seats)
        {
            int count = seats.GetArrayLength();
            if (count < 1 || count > 4) return null;
            var list = new List<DraftSeat>();
            foreach (var e in seats.EnumerateArray())
            {
                if (!ExactProperties(e, SeatProperties)) return null;
                var p = e.GetProperty("player"); var a = e.GetProperty("alias"); var k = e.GetProperty("kind"); var l = e.GetProperty("layout");
                if (p.ValueKind != JsonValueKind.Number || !p.TryGetInt32(out int player) || player != list.Count + 1) return null;
                if (a.ValueKind != JsonValueKind.String || k.ValueKind != JsonValueKind.String || l.ValueKind != JsonValueKind.String) return null;
                string alias = a.GetString()!, kind = k.GetString()!, layout = l.GetString()!;
                if (alias is not ("s1" or "s2" or "s3" or "s4") || list.Any(x => x.Alias == alias)) return null;
                if (kind is not ("pad" or "keyboard") || !Layouts.Contains(layout)) return null;
                if ((kind == "keyboard") != (layout == "KeyboardMouse")) return null;
                list.Add(new DraftSeat(player, alias, kind, layout));
            }
            return list.Count(x => x.Kind == "keyboard") <= 1 ? list : null;
        }

        private static bool ExactProperties(JsonElement e, string[] names)
        {
            if (e.ValueKind != JsonValueKind.Object) return false;
            var seen = e.EnumerateObject().Select(x => x.Name).ToList();
            return seen.Count == names.Length && names.All(n => seen.Count(x => x == n) == 1);
        }

        private static LinkDecision Cancel(string reason) => new(LinkAction.Cancel, reason, null);
    }
}
