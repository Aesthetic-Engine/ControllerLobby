using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ControllerLobby.DirectIndex.Selection;
using ControllerLobby.Transaction;

namespace ControllerLobby.DirectIndex.HostCore;

public sealed class TrialLog : IDisposable
{
    private readonly object sync=new();
    private readonly FileStream file;
    public TrialLog(string path)=>file=new(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read);
    public void Write<T>(string kind,T value)
    {
        lock(sync){file.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{Kind=kind,Qpc=Stopwatch.GetTimestamp(),Value=value})+"\n"));file.Flush(true);}
    }
    public void Dispose(){lock(sync)file.Dispose();}
}

// Real caller for the explicitly owner-authorized first-play attempt (20681).
// Diagnostic qualification is deliberately not manufactured here. Current package,
// selection, release, native reader and ownership still gate actual native resume.
public sealed class TrialServices : IExperimentServices
{
    private readonly TrialPackage package;
    private readonly FirstPlayPrepared prepared;
    private readonly TrialSampler sampler;
    private readonly IExperimentLease outerLease;
    private readonly SelectionSeal selection;
    private readonly TrialLog log;
    private readonly CancellationToken token;
    private readonly Func<bool> frontendAlive;
    private int creationAttempted;
    public bool TargetCreationAttempted=>Volatile.Read(ref creationAttempted)!=0;
    public TaskCompletionSource<ObservedTrialTarget> Created{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TrialServices(TrialPackage package,FirstPlayPrepared prepared,TrialSampler sampler,IExperimentLease lease,
        SelectionSeal selection,TrialLog log,CancellationToken token,Func<bool> frontendAlive)
    {
        this.package=package;this.prepared=prepared;this.sampler=sampler;outerLease=lease;this.selection=selection;this.log=log;this.token=token;this.frontendAlive=frontendAlive;
    }
    public IExperimentLease? Acquire()=>outerLease.Held?new BorrowedLease(outerLease):null;
    public MonitorRead Read()=>new(frontendAlive(),token.IsCancellationRequested,
        package.Manifest.ScopeDecision==TrialPackage.ApprovedScope&&package.QuickCurrent()&&sampler.Fresh,sampler.Latest.Sample);
    public ITargetFactory? Prepare()
    {
        if(!package.Current()||!prepared.Preserved())return null;
        var spec=prepared.Candidate.Specification!;
        log.Write("prepared-first-play-specification",new{spec.ApplicationName,spec.WorkingDirectory,spec.Arguments,selection,prepared.Candidate.SourceConfigs,prepared.Candidate.Files,Scope=TrialPackage.ApprovedScope,DiagnosticQualified=false});
        return new ObservingFactory(NativeTargetFactory.WithCompleteCommandLine(spec.ApplicationName,spec.CompleteCommandLine,spec.WorkingDirectory),this);
    }
    private bool BeforeNativeResume()
    {
        // Full current hashes, then a fresh real four-slot reading immediately at
        // the native resume boundary. Fast metadata checks are only for monitoring.
        if(!outerLease.Held||!package.Current()||!prepared.Preserved()||token.IsCancellationRequested||!frontendAlive()||!sampler.Fresh)return false;
        var sample=sampler.Latest.Sample;
        return sample.Valid&&sample.ConnectedMask==selection.ConnectedMask&&selection.PlayerIndices.All(i=>sample.Buttons[i]==0);
    }
    public bool PreserveCheck()=>package.Current()&&prepared.OwnerPreserved();
    public bool WriteCompletion(ExperimentResult result)
    {
        log.Write("experiment-completion",new{Scope=TrialPackage.ApprovedScope,Result=result,ProtectedInputs=package.ProtectedFiles,prepared.OriginalSave,TemporaryArtifactsAfter=prepared.Candidate.Files.Select(f=>ProtectedFile.Capture(f.Path)).ToArray(),TrialSaveAfter=ProtectedFile.Capture(Path.Combine(prepared.SaveDirectory,Path.GetFileName(prepared.OriginalSave.Path))),DiagnosticQualified=false});
        return true;
    }
    private sealed class BorrowedLease(IExperimentLease owner):IExperimentLease
    {public bool Held=>owner.Held;public void Complete(){}public void Dispose(){}}
    private sealed class ObservingFactory(ITargetFactory inner,TrialServices owner):ITargetFactory
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

public sealed class ObservedTrialTarget : ISuspendedTarget
{
    private readonly NativeTarget target;
    private readonly Func<bool> beforeResume;
    private readonly TrialLog log;
    private bool rootEnded,jobEmpty;
    private readonly object sync=new();
    private bool disposed;
    public HeldProcessIdentity Identity{get;}
    public TaskCompletionSource<bool> Resumed{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal ObservedTrialTarget(NativeTarget target,Func<bool> beforeResume,TrialLog log,string? expectedImage=null)
    {
        this.target=target;this.beforeResume=beforeResume;this.log=log;Identity=target.QueryIdentity();
        if (/*guard:held-image*/ (expectedImage!=null&&!Identity.ImagePath.Equals(expectedImage,StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("Held suspended process image differs from reviewed target");
        log.Write("native-created-suspended",Identity);
    }
    public uint Resume()
    {
        if (/*guard:firstplay-native-resume*/ !beforeResume())throw new InvalidOperationException("First-play native resume check refused");
        uint count=target.Resume();log.Write("native-resume-result",new{Identity,PreviousSuspendCount=count});
        Resumed.TrySetResult(count==1);return count;
    }
    public void TerminateJob(){lock(sync){target.TerminateJob();log.Write("owned-job-termination-requested",Identity);}}
    public bool WaitRootExit(TimeSpan bound)
    {
        lock(sync)
        {
            bool ended=target.WaitRootExit(bound);
            if(ended&&!rootEnded){rootEnded=true;log.Write("native-root-exit",new{Identity,Code=target.RootExitCode});}
            return ended;
        }
    }
    public int RootExitCode{get{lock(sync)return target.RootExitCode;}}
    public bool WaitJobEmpty(TimeSpan bound)
    {
        lock(sync)
        {
            bool empty=target.WaitJobEmpty(bound);
            if(empty&&!jobEmpty){jobEmpty=true;log.Write("native-job-empty",new{Identity,target.LastActiveProcesses,target.LastTotalProcesses});}
            return empty;
        }
    }
    public IReadOnlyList<LoadedImage> QueryLoadedImages()
    {
        IReadOnlyList<string> paths;
        lock(sync){if(disposed)throw new ObjectDisposedException(nameof(ObservedTrialTarget));paths=target.QueryLoadedImagePaths();}
        return NativeImageObservation.HashImages(paths); // never hold the ownership/stop lock while hashing images
    }
    public IReadOnlyList<string> QueryLoadedImagePaths(){lock(sync){if(disposed)throw new ObjectDisposedException(nameof(ObservedTrialTarget));return target.QueryLoadedImagePaths();}}
    public void Dispose(){lock(sync){disposed=true;target.Dispose();log.Write("native-handles-closed",new{Identity,target.RootEndedAtDispose,target.HandlesReleased,JobEmptyObserved=jobEmpty});}}
}
