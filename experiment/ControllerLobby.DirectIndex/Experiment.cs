using ControllerLobby.Transaction;
using ControllerLobby.DirectIndex.Selection;

namespace ControllerLobby.DirectIndex;

// Isolated first-play caller only. Explicit experiment policy; the existing shim's EscOnly
// and Routed enums/gates are not changed or reused as a fallback.
public enum ExperimentPolicy { Disabled, DirectIndexExperiment }
public enum ExperimentOutcome { NoGame, CommitUncertain, Aborted, Finished }
public sealed record ExperimentResult(ExperimentOutcome Outcome, bool CleanupConfirmed,
    bool EvidenceDurable, bool ConfigurationPreserved, bool ForcedCompletion, string Reason)
{
    public bool RoutingApplied => false;
    public int NewVirtualOutputs => 0;
}
public sealed record MonitorRead(bool FrontendAlive, bool Cancel, bool Eligible, EndpointSample Endpoints);
public interface IExperimentLease : IDisposable
{
    bool Held { get; }
    // Clear crash/recovery marker only after confirmed cleanup and durable completion.
    void Complete();
}
public interface IExperimentServices
{
    IExperimentLease? Acquire();
    // Preparation verifies the qualified executable/core/content/config/command path.
    // It does not start any target. The returned factory creates only inside its job.
    ITargetFactory? Prepare();
    MonitorRead Read();
    bool PreserveCheck();
    bool WriteCompletion(ExperimentResult result);
}

public static class Experiment
{
    public static ExperimentResult Run(ExperimentPolicy policy, SelectionSeal? selection,
        IExperimentServices services,
        TimeSpan runBound)
    {
        // The first-play host supplies actual reader/selection and explicit 20681
        // attempt authority. This does not declare diagnostic qualification.
        if (/*guard:deliberate-policy*/ (policy != ExperimentPolicy.DirectIndexExperiment))
            return new(ExperimentOutcome.NoGame, true, false, false, false, "experiment-not-selected");
        if (selection == null) return new(ExperimentOutcome.NoGame, true, false, false, false, "cancel-before-preparation");
        var selected = selection.PlayerIndices;
        int baselineMask = selection.ConnectedMask;
        if (/*guard:selection-shape*/ (selected.Count is < 1 or > 4 || selected.Distinct().Count() != selected.Count
            || baselineMask is < 1 or > 15 || selected.Any(i => i is < 0 or > 3 || (baselineMask & (1 << i)) == 0)
            || runBound <= TimeSpan.Zero || runBound > TimeSpan.FromMinutes(10)))
            return new(ExperimentOutcome.NoGame, true, false, false, false, "invalid-selection-or-bound");
        // Copy caller-owned selection before preparation.
        int[] indices = selected.ToArray();
        IExperimentLease? lease = null;
        ISuspendedTarget? target = null;
        var outcome = ExperimentOutcome.NoGame;
        bool cleanup = true, durable = false, preserved = false, forced = false;
        string reason = "not-prepared";
        string gateCause = "not-checked";
        bool Gate(bool release)
        {
            try
            {
                var read = services.Read();
                gateCause = !lease!.Held ? "lease-lost" : !read.FrontendAlive ? "frontend-ended"
                    : read.Cancel ? "cancel" : !read.Eligible ? "eligibility-lost"
                    : !read.Endpoints.Valid ? "endpoint-read-failure"
                    : read.Endpoints.ConnectedMask != baselineMask ? "topology-changed"
                    : release && indices.Any(i => read.Endpoints.Buttons[i] != 0) ? "input-not-released" : "";
                return gateCause.Length == 0;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            { gateCause = "monitor-threw:" + error.GetType().Name; return false; }
        }
        void Stop()
        {
            if (target == null) return;
            try { target.TerminateJob(); }
            catch (Exception) { cleanup = false; }
            try { cleanup &= target.WaitJobEmpty(TimeSpan.FromSeconds(5)); }
            catch (Exception) { cleanup = false; }
        }
        ExperimentResult Result() => new(outcome, cleanup, durable, preserved, forced, reason);
        try
        {
            lease = services.Acquire();
            if (/*guard:exclusive-lease*/ (lease == null || !lease.Held))
            { reason = "session-busy-or-recovery-required"; }
            else if (/*guard:before-preparation*/ (!Gate(true))) { reason = "preparation-gate-refused"; }
            else
            {
                var factory = services.Prepare();
                if (factory == null) reason = "preparation-refused";
                else if (/*guard:before-creation*/ (!Gate(true))) reason = "creation-gate-refused";
                else
                {
                    target = factory.CreateSuspendedInJob([]);
                    if (target == null) reason = "target-creation-failed";
                    else if (/*guard:before-resume*/ (!Gate(true)))
                    { reason = "resume-gate-refused"; Stop(); }
                    else
                    {
                        // Classify as uncertain before the call: a throwing native seam
                        // cannot prove that no thread ran.
                        outcome = ExperimentOutcome.CommitUncertain;
                        uint count = target.Resume();
                        if (/*guard:resume-count*/ (count != 1))
                        { reason = "unexpected-resume-count"; if (count > 1 && count != uint.MaxValue) outcome = ExperimentOutcome.NoGame; Stop(); }
                        else
                        {
                            outcome = ExperimentOutcome.Aborted;
                            var timer = System.Diagnostics.Stopwatch.StartNew();
                            while (true)
                            {
                                if (/*guard:play-monitor*/ (!Gate(false))) { reason = "play-monitor-aborted:" + gateCause; Stop(); break; }
                                if (target.WaitRootExit(TimeSpan.FromMilliseconds(20)))
                                {
                                    var grace = System.Diagnostics.Stopwatch.StartNew();
                                    bool descendantAbort = false;
                                    while (/*guard:descendant-grace*/ (!target.WaitJobEmpty(TimeSpan.Zero)))
                                    {
                                        if (!Gate(false))
                                        {
                                            descendantAbort = true;
                                            reason = "descendant-abort:" + gateCause;
                                            forced = true; Stop(); break;
                                        }
                                        if (grace.Elapsed >= TimeSpan.FromSeconds(5))
                                        { reason = "descendant-grace-expired"; forced = true; Stop(); break; }
                                        System.Threading.Thread.Sleep(20);
                                    }
                                    if (!forced) reason = "normal-root-and-job-exit";
                                    if (/*guard:finished-empty*/ (cleanup && !descendantAbort)) outcome = ExperimentOutcome.Finished;
                                    break;
                                }
                                if (timer.Elapsed >= runBound) { reason = "trial-bound"; Stop(); break; }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        { reason = "operation-failed:" + e.GetType().Name; Stop(); }
        finally
        {
            if (target != null)
            {
                try { target.Dispose(); } catch (Exception) { cleanup = false; }
            }
            if (lease != null)
            {
                try { preserved = services.PreserveCheck(); } catch (Exception) { preserved = false; }
                try { durable = services.WriteCompletion(Result()); } catch (Exception) { durable = false; }
                try { if (/*guard:complete-after-proof*/ (lease.Held && cleanup && durable && preserved)) lease.Complete(); }
                catch (Exception) { durable = false; }
                try { lease.Dispose(); } catch (Exception) { cleanup = false; }
            }
        }
        return Result();
    }
}
