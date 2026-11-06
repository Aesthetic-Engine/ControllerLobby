using System;

// Integration brief I1 r2 section 2: the lobby opens only for the two pinned games on their pinned emulators. Another
// game on a claimed emulator goes to the built-in preparation once, with no lobby, and does not depend on the mode
// file, the lobby payload or a lobby result. Unclaimed emulators are left alone. Pure; no LaunchBox types.
namespace ControllerLobby.LobbyLink
{
    public enum GateStep { LeaveAlone, BuiltInOnly, ShowLobby }

    public static class I1Gate
    {
        // Synthetic IDs for the retained legacy integration gate. Normal Beta play discovers IDs dynamically.
        public const string Smb3GameId = "33333333-3333-4333-8333-333333333333";
        public const string MkGameId = "44444444-4444-4444-8444-444444444444";
        public const string RetroArchId = "11111111-1111-4111-8111-111111111111";
        public const string MameId = "22222222-2222-4222-8222-222222222222";

        // emulatorClaimed is the probe's existing emulator id-and-path check (Rules.IsTargetPair).
        public static GateStep Decide(bool emulatorClaimed, string? emulatorId, string? gameId)
        {
            if (!emulatorClaimed) return GateStep.LeaveAlone;
            bool pinned = (Same(gameId, Smb3GameId) && Same(emulatorId, RetroArchId)) ||
                          (Same(gameId, MkGameId) && Same(emulatorId, MameId));
            return pinned ? GateStep.ShowLobby : GateStep.BuiltInOnly; // mutation:game-gate
        }

        private static bool Same(string? a, string b) => a != null && string.Equals(a.Trim(), b, StringComparison.OrdinalIgnoreCase);
    }
}
