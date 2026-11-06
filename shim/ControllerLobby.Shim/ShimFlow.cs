using System;
using System.Collections.Generic;
using System.IO;
using ControllerLobby.LobbyLink;
using ControllerLobby.Transaction;

// The shim's decision flow, separate from Main so the offline checks can drive it with a failing log and a counting
// lobby runner . Evidence rules: the lobby opens only if every log line before it was written; the exit is
// 0 only if every line, including the terminal one, was written. Otherwise the exit is 4 (evidence failed), or 3 when
// the configuration is refused. Under the production esc-only policy no outcome starts the target (S7, LAUNCH-TRANSACTION
// r5 section 15); the routed branch is reachable only through a check-constructed LaunchSeam.
namespace ControllerLobby.Shim
{
    internal delegate LinkDecision LobbyRunner(string payloadFolder, string[] manifest, Guid session, string title, LinkOptions options, Action<string> log);

    internal static class ShimFlow
    {
        public static int Run(string shimPath, string rawCommandLine, Func<string, ShimLog> openLog, LobbyRunner runLobby,
            FrontendWatch frontend = null, LaunchSeam seam = null, Func<string> currentDirectory = null)
        {
            string dir = Path.GetDirectoryName(shimPath);
            string why = ShimRules.ReadSidecar(shimPath, out var sidecar);
            string logDir = sidecar?.LogDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ControllerLobbyTest");
            var log = openLog(logDir);
            // The log is the measurement's evidence: without it, no lobby opens .
            if (!log.Available) return (int)ShimRules.Exit.Refused; // guard:log-required
            // The shim's own working directory, as its parent started it, on the same start line (DECISIONS 2026-11-03,
            // game locations;). Diagnostic only: a failure to read it is logged and changes nothing else.
            log.Write("start pid=" + Environment.ProcessId + " shim=" + shimPath + " cwd=" + WorkingDirectory(currentDirectory)); // guard:cwd-logged
            log.Write("frontend watch: " + (frontend?.Status ?? "not configured"));
            if (why != null) return Refuse(log, "sidecar: " + why);
            why = ShimRules.SplitFirstToken(rawCommandLine, out _, out string tail);
            if (why != null) return Refuse(log, "command line: " + why);
            // The policy is decided once, here, before the lobby (r5 section 15 item 1). The production entry point passes
            // no seam, and the only sidecar mode accepted is esc-only.
            LaunchPolicy policy = seam == null ? LaunchPolicy.EscOnly : seam.Policy; // guard:production-esc-only
            log.Write("incoming tail chars=" + tail.Length + " target=" + Path.GetFileName(sidecar.TargetPath) + " mode=" + sidecar.Mode
                + " policy=" + (policy == LaunchPolicy.EscOnly ? "esc-only" : "routed"));

            string lobbyRoot = Path.Combine(dir, "ControllerLobby");
            string[] manifest;
            try { manifest = File.ReadAllLines(Path.Combine(lobbyRoot, "lobby.sha256")); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Refuse(log, "lobby manifest unreadable (" + e.GetType().Name + ")"); }
            // Every line before the lobby must have been written .
            if (log.Failed) return (int)ShimRules.Exit.EvidenceFailed; // guard:pre-lobby-evidence
            bool frontendAbort = false; // latched only when the lobby's abort predicate itself observed the frontend's exit
            LinkDecision decision = runLobby(Path.Combine(lobbyRoot, "lobby"), manifest, Guid.NewGuid(), ShimRules.TitleFromTail(tail),
                new LinkOptions { AbortRequested = () => log.Failed || (frontendAbort |= frontend?.Gone ?? false) }, log.Write); // guard:abort-on-log-failure, guard:frontend-exit
            if (log.Failed) return (int)ShimRules.Exit.EvidenceFailed;
            if (policy != LaunchPolicy.EscOnly) // guard:esc-only-no-target: no esc-only result reaches the core
                return Routed(log, seam, decision, frontendAbort, frontend, sidecar.TargetPath, tail);
            // Esc-only: no outcome starts the target (forwarding disabled by construction; there is no launch code here).
            // report what was observed, and keep it apart from evidence that a lobby existed or ended.
            if (frontendAbort) log.Write("abort requested: the lobby watch observed the frontend's exit");
            else if (frontend?.Gone ?? false) log.Write("frontend exit observed after the lobby call");
            log.Write("exit 0: " + (decision.Action == LinkAction.Launch ? "confirmed, forwarding disabled in esc-only mode" : "no game (" + decision.Reason + ")"));
            return log.Failed ? (int)ShimRules.Exit.EvidenceFailed : (int)ShimRules.Exit.NoGameCancelled; // guard:terminal-evidence
        }

        /// <summary>The routed branch: the sealed lobby result and the exact section 4 gate (frontend, helper, route, log),
        /// with no fallback, through the accepted core. Reachable only with a check-constructed seam.</summary>
        private static int Routed(ShimLog log, LaunchSeam seam, LinkDecision decision, bool frontendAbort, FrontendWatch frontend, string target, string tail)
        {
            // Provenance : only the lobby's own cancel ("cancel" or "cancel: ...") and a frontend exit are user or
            // frontend cancellation (exit 0); every other lobby or seal result is a failure (exit 5).
            bool confirmed = decision.Action == LinkAction.Launch && !frontendAbort;
            bool userCancel = decision.Action == LinkAction.Cancel && decision.Reason != null
                && (decision.Reason == "cancel" || decision.Reason.StartsWith("cancel: ", StringComparison.Ordinal));
            var sealedResult = confirmed ? new SealedResult(true)
                : frontendAbort ? new SealedResult(false, "frontend exit", UserOrFrontend: true)
                : new SealedResult(false, "lobby: " + (decision.Reason ?? "no reason"), UserOrFrontend: userCancel); // guard:routed-provenance
            // A confirm needs a verified, held frontend for the gate: without one nothing is created (a preparation failure,
            // section 9 row 5), never a degraded gate. A cancel creates nothing either way and keeps its own code.
            if (confirmed && (frontend == null || !frontend.Watching)) // guard:routed-needs-frontend-watch
            {
                log.Write("routed launch refused, nothing created: no frontend watch");
                log.Write("exit 5");
                return log.Failed ? (int)ShimRules.Exit.EvidenceFailed : 5;
            }
            log.Write("routed launch: sealed " + (confirmed ? "confirm" : "cancel (" + sealedResult.CancelReason + ")"));
            var gate = new List<CheckInput>
            {
                new CheckInput("frontend", (Func<GateRead>)(() => frontend == null ? GateRead.Failed : frontend.Observe())), // guard:routed-frontend: one classified wait per read
                new("helper", () => seam.Helper != null && seam.Helper()), // guard:routed-helper
                new("route", () => seam.Route != null && seam.Route()),
                new("log", () => !log.Failed),
            };
            ITargetFactory factory = new DeferredFactory(() => seam.FactoryFor(target, tail)); // guard:factory-inside-core
            var r = TransactionCore.Run(sealedResult, gate, factory, seam.Completion, seam.TargetBound, seam.CompletionBound);
            int code = ExitCodes.For(r, log.Failed, false);
            log.Write("routed outcome " + r.Outcome + " target code=" + (r.TargetExitCode?.ToString() ?? "none") + " cleanup="
                + (r.CleanupComplete ? "complete" : "incomplete") + " routing end=" + (r.RoutingEndConfirmed?.ToString() ?? "n/a")
                + " reasons=" + string.Join("; ", r.Reasons));
            log.Write("exit " + code);
            return log.Failed ? (int)ShimRules.Exit.EvidenceFailed : code; // guard:routed-terminal-evidence
        }

        private static string WorkingDirectory(Func<string> currentDirectory)
        {
            try { return "\"" + (currentDirectory ?? (() => Environment.CurrentDirectory))() + "\""; }
            catch (Exception e) when (e is not OutOfMemoryException) { return "unavailable (" + e.GetType().Name + ")"; } // guard:cwd-error-contained
        }

        private static int Refuse(ShimLog log, string why)
        {
            log.Write("exit 3: refused, nothing started: " + why);
            return (int)ShimRules.Exit.Refused;
        }
    }
}
