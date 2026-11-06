using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

// Integration brief I1 r2 section 4: one lobby session from the plugin's side. Verify the pinned payload (4.1), start
// the lobby and wait (4.2, 4.3, 4.6, 4.8), then read its record (4.4, 4.7). The payload stays locked against writes
// and deletion until the lobby has ended. The probe calls this only for GateStep.ShowLobby (section 2).
namespace ControllerLobby.LobbyLink
{
    public static class LinkSession
    {
        public const string LobbyExeName = "ControllerLobby.Lobby.Windows.exe";

        public static LinkDecision Run(string payloadFolder, IEnumerable<string> manifestLines, Guid session, string gameTitle,
            string platform, LinkOptions options, Action<string> log, string exeName = LobbyExeName)
        {
            string? mismatch = PayloadPin.Verify(payloadFolder, manifestLines, out var payloadLock);
            if (mismatch != null)
            {
                log("lobby payload refused: " + JsonSerializer.Serialize(mismatch)); // escaped: file names reach the log
                return new LinkDecision(LinkAction.Cancel, mismatch.StartsWith("payload-manifest") ? mismatch : "payload-mismatch", null);
            }
            using (payloadLock)
            {
                // The child is bound to the verified set: a bare file name that the manifest pins .
                bool bare = exeName.Length > 0 && Path.GetFileName(exeName) == exeName && !Path.IsPathRooted(exeName);
                if (!bare || !payloadLock!.Files.Contains(exeName, StringComparer.OrdinalIgnoreCase)) // mutation:exe-bound
                {
                    log("lobby executable not in the verified payload: " + JsonSerializer.Serialize(exeName));
                    return new LinkDecision(LinkAction.Cancel, "payload-exe-not-pinned", null);
                }
                var outcome = LobbyProcess.Run(Path.Combine(payloadFolder, exeName), session, gameTitle, platform, options, log);
                var decision = LobbyResult.Decide(session, outcome);
                log("lobby decision " + decision.Describe());
                return decision;
            }
        }
    }
}
