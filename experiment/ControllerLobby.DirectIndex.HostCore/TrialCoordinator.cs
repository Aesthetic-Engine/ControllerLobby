using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ControllerLobby.DirectIndex.Native;
using ControllerLobby.DirectIndex.Preparation;
using ControllerLobby.DirectIndex.Selection;
using ControllerLobby.Transaction;

namespace ControllerLobby.DirectIndex.HostCore;

public interface ITrialView
{
    bool Alive{get;}
    Task? ExistingControlsRequested=>null;
    string EntrySurface=>"separate experimental four-card launcher";
    void Selecting(IReadOnlyList<JoinedCard> cards,string instruction);
    void Status(string title,string detail);
}
public sealed record TrialRunResult(string RunId,string EvidenceDirectory,ExperimentResult? Experiment,bool ModuleViewObserved,bool PreservationConfirmed,string Reason);

public static class TrialCoordinator
{
    public static async Task<TrialRunResult> Run(TrialPackage package,string stateRoot,ITrialView view,CancellationToken cancel)
    {
        package.RequireActualHost();
        using var stopping=CancellationTokenSource.CreateLinkedTokenSource(cancel);
        string runId=Guid.NewGuid().ToString("D"),run=Path.Combine(Path.GetFullPath(stateRoot),"runs",runId),leaseDirectory=Path.Combine(Path.GetFullPath(stateRoot),"lease");
        for(string? ancestor=Path.GetFullPath(stateRoot);ancestor!=null;ancestor=Path.GetDirectoryName(ancestor))
        {
            try{if(File.GetAttributes(ancestor).HasFlag(FileAttributes.ReparsePoint))throw new IOException("Reparse trial state directory");}
            catch(FileNotFoundException){}catch(DirectoryNotFoundException){}
        }
        Directory.CreateDirectory(leaseDirectory);Directory.CreateDirectory(run);
        File.WriteAllText(Path.Combine(run,"run-id.txt"),runId+"\n",new UTF8Encoding(false));
        FileSessionLease? lease=null;PinnedXInputReader? reader=null;TrialSampler? sampler=null;TrialLog? log=null;
        ExperimentResult? result=null;TrialServices? services=null;
        bool observed=false,preserved=false,samplerClosed=true;string reason="setup-not-complete";
        try
        {
            lease=FileSessionLease.Acquire(leaseDirectory);
            if(lease==null)throw new InvalidOperationException("Another trial is active, or an interrupted trial needs inspection");
            log=new TrialLog(Path.Combine(run,"events.jsonl"));
            log.Write("owner-first-play-scope",new{Scope=TrialPackage.ApprovedScope,package.ManifestPath,package.ManifestSha256,RunId=runId,Source=package.Manifest.SourceManifestSha256,DiagnosticQualified=false,EntrySurface=view.EntrySurface});
            if(!package.Current())throw new IOException("Reviewed package or protected input changed");
            stopping.Token.ThrowIfCancellationRequested();
            reader=PinnedXInputReader.Open(package.Manifest.Reader);
            sampler=new(reader,Path.Combine(run,"controller-observations.jsonl"),runId,package.ManifestSha256,collectUntilConfirmation:true);
            var model=new LobbyJoinPresenter(sampler.Latest,4);
            if(model.Failure!=null)throw new IOException(model.Failure);
            var selected=new TaskCompletionSource<SelectionSeal>(TaskCreationOptions.RunContinuationsAsynchronously);
            int selecting=1;
            sampler.Observed+=read=>
            {
                if(Volatile.Read(ref selecting)==0)return;
                if(read.Slots.Any(s=>(s.Buttons&0x20)!=0)){selected.TrySetCanceled();stopping.Cancel();return;}
                model.Observe(read);
                if(model.Failure!=null){selected.TrySetException(new InvalidOperationException(model.Failure));return;}
                view.Selecting(model.Cards,model.Instruction);
                if(model.Seal!=null)
                {
                    if(!sampler.TryFreeze(read,model.Seal.ConnectedMask))selected.TrySetException(new IOException("Controller set changed at confirmation"));
                    else selected.TrySetResult(model.Seal);
                }
            };
            sampler.Start();
            view.Selecting([],model.Instruction);
            SelectionSeal seal=await LobbySelectionAwaiter.Wait(selected.Task,()=>view.Alive&&sampler.Fresh,stopping.Token).ConfigureAwait(false);
            Interlocked.Exchange(ref selecting,0);
            if(!view.Alive||!sampler.Fresh||stopping.IsCancellationRequested)throw new OperationCanceledException("Selection ended before preparation");
            view.Status("Preparing GoldenEye","Applying temporary player assignments");
            var target=package.Manifest.Target;
            var request=new PreparationRequest(SelectionPolicy.DirectIndexExperiment,runId,seal,
                new(target.Root,target.MainConfig,target.CoreName,target.Content),target.Executable,target.CoreLibrary,
                Path.Combine(run,"preparation"),Path.Combine(run,"controller-observations.jsonl"),["-f","--verbose"]);
            FirstPlayPrepared prepared=FirstPlayPreparation.Prepare(request);
            log.Write("real-selection",new{seal,Reader=reader.Binding,FirstPlaySourceSave=prepared.OriginalSave});
            services=new(package,prepared,sampler,lease,seal,log,stopping.Token,()=>view.Alive);
            var experiment=Task.Run(()=>Experiment.Run(ExperimentPolicy.DirectIndexExperiment,seal,services,TimeSpan.FromSeconds(package.Manifest.MaximumSeconds)),CancellationToken.None);
            var modules=ObserveModules(services,experiment,package,log,view,stopping);
            result=await experiment.ConfigureAwait(false);
            observed=await modules.ConfigureAwait(false);
            reason=result.Reason;
        }
        catch(Exception e)when(e is not OutOfMemoryException)
        {reason=e is OperationCanceledException?"cancelled-before-launch":e.Message;try{log?.Write("trial-error",new{Type=e.GetType().Name,Reason=reason});}catch(IOException){} }
        finally
        {
            try{sampler?.Dispose();}catch(Exception e)when(e is not OutOfMemoryException){samplerClosed=false;reason="sampler-close-unconfirmed:"+e.GetType().Name;}
            // Never unload a native library while an unjoined sampler may use it.
            if(samplerClosed)try{reader?.Dispose();}catch(Exception e)when(e is not OutOfMemoryException){samplerClosed=false;reason="reader-close-unconfirmed:"+e.GetType().Name;}
            try{preserved=package.Current()&&(services==null||services.PreserveCheck());}catch(Exception){preserved=false;}
            bool noCreatedTarget=services==null||!services.TargetCreationAttempted;
            bool publishedTarget=services?.Created.Task.IsCompletedSuccessfully==true;
            bool cleanup=noCreatedTarget||publishedTarget&&result?.CleanupConfirmed==true;
            try
            {
                log?.Write("first-play-final",new{RunId=runId,Result=result,ModuleViewObserved=observed,PreservationConfirmed=preserved,SamplerClosed=samplerClosed,Scope=TrialPackage.ApprovedScope,DiagnosticQualified=false});
                log?.Dispose();log=null;
                WriteNew(Path.Combine(run,"result.json"),new TrialRunResult(runId,run,result,observed,preserved,reason));
                if(lease!=null&&cleanup&&samplerClosed&&preserved&&(noCreatedTarget||result?.EvidenceDurable==true))lease.Complete();
            }
            catch(Exception e)when(e is not OutOfMemoryException){reason="completion-evidence-unconfirmed:"+e.GetType().Name;}
            finally{try{log?.Dispose();}finally{lease?.Dispose();}}
        }
        var final=new TrialRunResult(runId,run,result,observed,preserved,reason);
        view.Status(result?.Outcome==ExperimentOutcome.Finished&&preserved?"GoldenEye trial ended":"GoldenEye trial stopped",reason+"\nEvidence: "+run);
        return final;
    }
    private static async Task<bool> ObserveModules(TrialServices services,Task<ExperimentResult> experiment,TrialPackage package,TrialLog log,ITrialView view,CancellationTokenSource stopping)
    {
        if(await Task.WhenAny(services.Created.Task,experiment).ConfigureAwait(false)!=services.Created.Task)return false;
        var target=await services.Created.Task.ConfigureAwait(false);
        if(await Task.WhenAny(target.Resumed.Task,experiment).ConfigureAwait(false)!=target.Resumed.Task||!await target.Resumed.Task.ConfigureAwait(false))return false;
        view.Status("GoldenEye is running","Verify each controller operates its assigned player. Close GoldenEye normally, or use Stop here.");
        string[] required=[package.Manifest.Target.Executable,package.Manifest.Target.CoreLibrary,package.Manifest.Reader.LibraryPath];
        var timer=Stopwatch.StartNew();
        try
        {
            while(timer.Elapsed<TimeSpan.FromSeconds(10)&&!experiment.IsCompleted&&!stopping.IsCancellationRequested)
            {
                IReadOnlyList<string>? paths=null;
                try{paths=target.QueryLoadedImagePaths();}catch(System.ComponentModel.Win32Exception){}
                if(paths!=null&&required.All(wanted=>paths.Any(p=>p.Equals(wanted,StringComparison.OrdinalIgnoreCase))))
                {
                    var actual=NativeImageObservation.HashImages(paths.Where(p=>required.Any(wanted=>p.Equals(wanted,StringComparison.OrdinalIgnoreCase))).ToArray());
                    if(!package.Current())throw new IOException("Inputs changed after target resume");
                    log.Write("actual-held-target-modules",new{target.Identity,Required=actual,ObservedPaths=paths});return true;
                }
                await Task.Delay(50,CancellationToken.None).ConfigureAwait(false);
            }
            if(!experiment.IsCompleted)StopForModuleFailure(stopping,log,"module-observation-refused",new{target.Identity,Reason="Expected executable/core/XInput module view was not observed"});
            return false;
        }
        catch(Exception e)when(e is not OutOfMemoryException){StopForModuleFailure(stopping,log,"module-observation-failed",new{Type=e.GetType().Name});return false;}
    }
    internal static void StopForModuleFailure(CancellationTokenSource stopping,TrialLog log,string kind,object detail)
    {
        stopping.Cancel(); // stop is independent of evidence/reporting success
        try{log.Write(kind,detail);}catch(Exception e)when(e is not OutOfMemoryException){}
    }
    private static void WriteNew<T>(string path,T value)
    {using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read);file.Write(JsonSerializer.SerializeToUtf8Bytes(value,new JsonSerializerOptions{WriteIndented=true}));file.Flush(true);}
}
