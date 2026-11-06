using System;
using System.Collections.Generic;
using System.Linq;

// Slice S6: the shim's launch-transaction core at M (LAUNCH-TRANSACTION r4, accepted as a written contract). It runs after the lobby result is sealed and implements: one deciding thread; the target created
// suspended inside a kill-on-close job and held by its handles; the per-input final check; the resume classes by
// previous suspend count; job cleanup and job-empty before Finished; normal versus unconfirmed completion; and the
// reason-to-exit-code table, kept separate from the execution outcome.
//
// Outside this core (not decided or built here): the seal itself (lobbylink's existing boundary), the pre-seal FixIt
// orchestration with H, the H completion signal (OPEN-WIRE), OPEN-ID, and any production entry point. The checks drive
// this core with fake helper, route, completion and resume inputs, and with harmless owned stand-in targets.
namespace ControllerLobby.Transaction
{
    public enum Outcome { NoGame, Committed, CommitUncertain, Finished }

    /// <summary>The lobby's sealed result (LAUNCH-TRANSACTION section 3): a confirm, or a cancel with its reason.</summary>
    /// <summary>The sealed lobby result. A cancel is user or frontend cancellation (exit 0) only when UserOrFrontend is
    /// true; a lobby or seal failure is a cancel with UserOrFrontend false (exit 5).</summary>
    public sealed record SealedResult(bool Confirmed, string CancelReason = "user cancel", bool UserOrFrontend = true);

    /// <summary>One input of the final pre-resume check, read once at its own boundary (section 4).</summary>
    /// <summary>One gate read: passed; the input observed its end (a signalled frontend: user or frontend cancellation);
    /// or the observation failed or was unknown (a failure, exit 5).</summary>
    public enum GateRead { Pass, Ended, Failed }

    /// <summary>A final-check input. The boolean form maps false to Failed: only an explicit Ended is cancellation.</summary>
    public sealed record CheckInput(string Name, Func<GateRead> Observe)
    {
        public CheckInput(string name, Func<bool> read) : this(name, read == null ? null! : () => read() ? GateRead.Pass : GateRead.Failed) { }
    }

    public interface ISuspendedTarget : IDisposable
    {
        /// <summary>ResumeThread's return: the previous suspend count, or uint.MaxValue ((DWORD)-1) on failure.</summary>
        uint Resume();
        /// <summary>Terminates the job through its held handle (ends the target and every descendant).</summary>
        void TerminateJob();
        bool WaitRootExit(TimeSpan bound);
        int RootExitCode { get; }
        /// <summary>True when the job's accounting shows no active process within the bound (S's observation).</summary>
        bool WaitJobEmpty(TimeSpan bound);
    }

    public interface ITargetFactory
    {
        /// <summary>Creates the target suspended inside a kill-on-close job, or null with reasons.</summary>
        ISuspendedTarget? CreateSuspendedInJob(List<string> failures);
    }

    /// <summary>H's confirmation that routing has ended by its own tree and settle rule (OPEN-WIRE; faked in checks).</summary>
    public interface ICompletion { bool AwaitRoutingEnded(TimeSpan bound); }

    /// <summary>The outcome plus typed facts. Diagnostics never relabel the outcome .</summary>
    public sealed record TransactionResult(Outcome Outcome, IReadOnlyList<string> Reasons, int? TargetExitCode, bool? RoutingEndConfirmed,
        IReadOnlyList<string> Events, bool CancelledByUserOrFrontend, bool EvidenceFailed = false, bool CleanupComplete = true);

    public static class TransactionCore
    {
        public const uint ResumeFailed = uint.MaxValue; // (DWORD)-1
        /// <summary>The complete final check, in this order, each exactly once (LAUNCH-TRANSACTION section 4).</summary>
        public static readonly string[] RequiredInputs = { "frontend", "helper", "route", "log" };

        public static TransactionResult Run(SealedResult result, IReadOnlyList<CheckInput> finalCheck, ITargetFactory factory, ICompletion completion,
            TimeSpan targetBound, TimeSpan completionBound)
        {
            var events = new List<string>();
            var reasons = new List<string>();
            bool evidenceFailed = false, cleanupComplete = true;
            TransactionResult Done(Outcome o, int? code = null, bool? routingEnd = null, bool userOrFrontend = false) =>
                new(o, reasons, code, routingEnd, events, userOrFrontend, evidenceFailed, cleanupComplete);

            // Pending -> sealed: a cancel never creates a target.
            if (!result.Confirmed) { reasons.Add(result.CancelReason); events.Add("sealed: cancel"); return Done(Outcome.NoGame, userOrFrontend: result.UserOrFrontend); } // guard:cancel-provenance
            events.Add("sealed: confirm");
            // The gate must be complete before anything is created .
            if (finalCheck == null || finalCheck.Count != RequiredInputs.Length
                || !finalCheck.Select(i => i?.Name).SequenceEqual(RequiredInputs) || finalCheck.Any(i => i.Observe == null)) // guard:complete-gate
            { reasons.Add("final check incomplete or out of order"); return Done(Outcome.NoGame); }

            // Preparing: the target is created suspended in its job.
            var failures = new List<string>();
            ISuspendedTarget? target;
            try { target = factory.CreateSuspendedInJob(failures); }
            catch (Exception e) when (e is not OutOfMemoryException) { target = null; failures.Add("target creation threw (" + e.GetType().Name + ")"); }
            if (target == null) { reasons.Add("target creation failed: " + string.Join("; ", failures)); return Done(Outcome.NoGame); }

            Outcome outcome = Outcome.NoGame;
            int? exitCode = null; bool? routingEnd = null; bool userOrFrontend = false;
            TransactionResult res;
            try { res = Body(); }
            catch (Exception e) when (e is not OutOfMemoryException) // anything unforeseen keeps the facts gathered so far
            {
                cleanupComplete = false;
                reasons.Add("transaction step failed (" + e.GetType().Name + ")");
                res = Done(outcome, exitCode, routingEnd, userOrFrontend);
            }
            // Dispose last, and fold its result into the returned record : a failure here is cleanup
            // uncertainty, never a different execution outcome.
            try { target.Dispose(); }
            catch (Exception e) when (e is not OutOfMemoryException) { cleanupComplete = false; reasons.Add("target dispose failed (" + e.GetType().Name + ")"); } // guard:dispose-folded
            return res with { CleanupComplete = res.CleanupComplete && cleanupComplete, EvidenceFailed = res.EvidenceFailed || evidenceFailed };

            TransactionResult Body()
            {
                events.Add("created: suspended in job");
                bool gatePassed = true;
                foreach (var input in finalCheck)
                {
                    GateRead read;
                    try { read = input.Observe(); } catch (Exception e) when (e is not OutOfMemoryException) { read = GateRead.Failed; reasons.Add(input.Name + " read failed (" + e.GetType().Name + ")"); }
                    bool ok = read == GateRead.Pass;
                    events.Add("check " + input.Name + ": " + (ok ? "ok" : read == GateRead.Ended ? "ended" : "failed"));
                    if (!ok) // guard:before-read-failure-stops
                    {
                        TerminateAndConfirm(target, reasons, ref cleanupComplete); // guard:no-game-terminates-job
                        reasons.Add("final check failed: " + input.Name);
                        if (input.Name == "log") evidenceFailed = true; // guard:log-check-is-evidence
                        userOrFrontend = input.Name == "frontend" && read == GateRead.Ended; // guard:ended-only-is-cancellation: frontend only, from this same read
                        gatePassed = false;
                        break;
                    }
                }
                if (!gatePassed) return Done(Outcome.NoGame, userOrFrontend: userOrFrontend);

                uint previous;
                try { previous = target.Resume(); } catch (Exception e) when (e is not OutOfMemoryException) { previous = ResumeFailed; reasons.Add("resume threw (" + e.GetType().Name + ")"); }
                events.Add("resume returned " + (previous == ResumeFailed ? "(DWORD)-1" : previous.ToString()));
                if (previous != 1) // guard:resume-count-one
                {
                    TerminateAndConfirm(target, reasons, ref cleanupComplete);
                    if (previous == ResumeFailed) { reasons.Add("resume failed"); return Done(Outcome.CommitUncertain); }
                    if (previous == 0) { reasons.Add("target was not suspended (previous count 0)"); return Done(Outcome.CommitUncertain); }
                    reasons.Add("unexpected suspend count " + previous + "; still suspended");
                    return Done(Outcome.NoGame);
                }
                outcome = Outcome.Committed; // from here on, every failure keeps 'it ran'
                events.Add("committed");

                // Finishing (section 7). Each step is guarded; a failure records cleanup uncertainty, never No game.
                bool rootExited;
                try
                {
                    rootExited = target.WaitRootExit(targetBound);
                    if (!rootExited)
                    {
                        reasons.Add("target still running at the harness bound; terminating the job");
                        Terminate(target, reasons, ref cleanupComplete);
                        rootExited = target.WaitRootExit(TimeSpan.FromSeconds(5)); // guard:exit-code-after-confirmed-end
                    }
                }
                catch (Exception e) when (e is not OutOfMemoryException) { rootExited = false; reasons.Add("wait on the target failed (" + e.GetType().Name + ")"); }
                if (!rootExited) { cleanupComplete = false; reasons.Add("target end not confirmed"); return Done(outcome); }
                try { exitCode = target.RootExitCode; }
                catch (Exception e) when (e is not OutOfMemoryException) { reasons.Add("exit code unreadable (" + e.GetType().Name + ")"); }
                Terminate(target, reasons, ref cleanupComplete); // guard:job-terminated-after-root-exit: ends any descendant
                bool empty;
                try { empty = target.WaitJobEmpty(TimeSpan.FromSeconds(5)); }
                catch (Exception e) when (e is not OutOfMemoryException) { empty = false; reasons.Add("job accounting failed (" + e.GetType().Name + ")"); }
                if (!empty) // guard:finished-needs-empty-job
                {
                    cleanupComplete = false;
                    reasons.Add("job not empty after termination");
                    return Done(outcome, exitCode);
                }
                events.Add("job empty");
                try { routingEnd = completion.AwaitRoutingEnded(completionBound); } // guard:routing-end-recorded
                catch (Exception e) when (e is not OutOfMemoryException) { routingEnd = false; reasons.Add("completion wait failed (" + e.GetType().Name + ")"); }
                if (routingEnd != true) reasons.Add("routing end unconfirmed");
                events.Add("finished");
                outcome = Outcome.Finished;
                return Done(outcome, exitCode, routingEnd);
            }
        }

        /// <summary>Terminates the job and confirms the root's end on its held handle . An unconfirmed end is
        /// cleanup uncertainty, not a different execution outcome.</summary>
        private static void TerminateAndConfirm(ISuspendedTarget target, List<string> reasons, ref bool cleanupComplete)
        {
            Terminate(target, reasons, ref cleanupComplete);
            bool ended;
            try { ended = target.WaitRootExit(TimeSpan.FromSeconds(5)); }
            catch (Exception e) when (e is not OutOfMemoryException) { ended = false; reasons.Add("termination wait failed (" + e.GetType().Name + ")"); }
            if (!ended) { cleanupComplete = false; reasons.Add("target end not confirmed after termination"); } // guard:precommit-end-confirmed
            // A resumed-then-uncertain root may already have started descendants: the job must be empty too .
            bool empty;
            try { empty = target.WaitJobEmpty(TimeSpan.FromSeconds(5)); }
            catch (Exception e) when (e is not OutOfMemoryException) { empty = false; reasons.Add("job accounting failed after termination (" + e.GetType().Name + ")"); }
            if (!empty) { cleanupComplete = false; reasons.Add("job not empty after termination"); } // guard:precommit-job-empty
        }

        private static void Terminate(ISuspendedTarget target, List<string> reasons, ref bool cleanupComplete)
        {
            try { target.TerminateJob(); }
            catch (Exception e) when (e is not OutOfMemoryException) { cleanupComplete = false; reasons.Add("job termination failed (" + e.GetType().Name + ")"); }
        }
    }

    /// <summary>LAUNCH-TRANSACTION section 9: the exit code by priority. The outcome itself is never changed here.</summary>
    public static class ExitCodes
    {
        /// <summary>Codes 3 to 7 are diagnostics. A Finished target's own exit code can take any value, including these, so
        /// the log, not the code, is authoritative for a finished game.</summary>
        public static int For(TransactionResult r, bool evidenceFailed, bool configurationRefused)
        {
            if (evidenceFailed || r.EvidenceFailed) return 4; // guard:evidence-fact
            if (configurationRefused) return 3;
            if (r.Outcome == Outcome.CommitUncertain) return 6;
            if (!r.CleanupComplete) return 7; // guard:cleanup-fact: on every created-target path, never 0 or the target's code
            switch (r.Outcome)
            {
                case Outcome.NoGame: return r.CancelledByUserOrFrontend ? 0 : 5;
                case Outcome.Committed: return 7; // ran, but its end was not confirmed // guard:committed-incomplete
                case Outcome.Finished: return r.TargetExitCode ?? 7;
                default: return 6;
            }
        }
    }
}
