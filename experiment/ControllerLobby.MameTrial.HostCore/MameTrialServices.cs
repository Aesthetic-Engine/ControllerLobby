using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ControllerLobby.DirectIndex.Selection;
using ControllerLobby.Transaction;

using ControllerLobby.DirectIndex.HostCore;
using ControllerLobby.DirectIndex;

namespace ControllerLobby.MameTrial.HostCore;

// Real caller for the explicitly owner-authorized MAME first-play attempt (20726).
// Diagnostic qualification is deliberately not manufactured here. Current package,
// selection, release, native reader and ownership still gate actual native resume.
internal sealed class MameTrialServices : IExperimentServices
{
    private readonly MameTrialPackage package;
    private readonly MamePrepared prepared;
    private readonly TrialSampler sampler;
    private readonly IExperimentLease outerLease;
    private readonly SelectionSeal selection;
    private readonly TrialLog log;
    private readonly CancellationToken token;
    private readonly Func<bool> frontendAlive;
    private int creationAttempted;
    private string eligibilityCause="not-read";
    public bool TargetCreationAttempted=>Volatile.Read(ref creationAttempted)!=0;
    public TaskCompletionSource<ObservedTrialTarget> Created{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public MameTrialServices(MameTrialPackage package,MamePrepared prepared,TrialSampler sampler,IExperimentLease lease,
        SelectionSeal selection,TrialLog log,CancellationToken token,Func<bool> frontendAlive)
    {
        this.package=package;this.prepared=prepared;this.sampler=sampler;outerLease=lease;this.selection=selection;this.log=log;this.token=token;this.frontendAlive=frontendAlive;
    }
    public IExperimentLease? Acquire()=>outerLease.Held?new BorrowedLease(outerLease):null;
    public MonitorRead Read()
    {
        bool current=package.QuickCurrent(),fresh=sampler.Fresh;
        eligibilityCause=!current?"package-metadata-changed":!fresh?"sampler-stale-or-latched-failure":"eligible";
        return new(frontendAlive(),token.IsCancellationRequested,
            package.Manifest.ScopeDecision==MameTrialPackage.ApprovedScope&&current&&fresh,sampler.Latest.Sample);
    }
    public ITargetFactory? Prepare()
    {
        if(!package.Current()||!prepared.BeforeLaunchCurrent())return null;
        var spec=prepared;
        log.Write("prepared-first-play-specification",new{spec.Executable,spec.WorkingDirectory,spec.Arguments,selection,prepared.SourceFiles,prepared.LaunchFiles,Scope=MameTrialPackage.ApprovedScope,DiagnosticQualified=false});
        return new ObservingFactory(NativeTargetFactory.WithCompleteCommandLine(spec.Executable,spec.CompleteCommandLine,spec.WorkingDirectory),this);
    }
    private bool BeforeNativeResume()
    {
        // Full current hashes, then a fresh real four-slot reading immediately at
        // the native resume boundary. Fast metadata checks are only for monitoring.
        if(!outerLease.Held||!package.Current()||!prepared.BeforeLaunchCurrent()||token.IsCancellationRequested||!frontendAlive()||!sampler.Fresh)return false;
        var sample=sampler.Latest.Sample;
        return sample.Valid&&sample.ConnectedMask==selection.ConnectedMask&&selection.PlayerIndices.All(i=>sample.Buttons[i]==0);
    }
    public bool PreserveCheck()=>package.Current()&&prepared.OwnerPreserved();
    public bool WriteCompletion(ExperimentResult result)
    {
        log.Write("experiment-completion",new{Scope=MameTrialPackage.ApprovedScope,Result=result,EligibilityCause=eligibilityCause,ProtectedInputs=package.ProtectedFiles,prepared.OwnerNvram,TemporaryArtifactsAfter=prepared.LaunchFiles.Select(f=>ProtectedFile.Capture(f.Path)).ToArray(),DiagnosticQualified=false});
        return true;
    }
    private sealed class BorrowedLease(IExperimentLease owner):IExperimentLease
    {public bool Held=>owner.Held;public void Complete(){}public void Dispose(){}}
    private sealed class ObservingFactory(ITargetFactory inner,MameTrialServices owner):ITargetFactory
    {
        public ISuspendedTarget? CreateSuspendedInJob(List<string> failures)
        {
            Interlocked.Exchange(ref owner.creationAttempted,1);
            var target=inner.CreateSuspendedInJob(failures);
            if(target==null)return null;
            if(target is not NativeTarget native){target.Dispose();throw new InvalidOperationException("Real native target required");}
            try
            {
                var wrapped=new ObservedTrialTarget(native,owner.BeforeNativeResume,owner.log,owner.package.Manifest.Target.Executable);
                owner.Created.TrySetResult(wrapped);return wrapped;
            }
            catch{try{native.TerminateJob();native.WaitJobEmpty(TimeSpan.FromSeconds(5));}finally{native.Dispose();}throw;}
        }
    }
}

