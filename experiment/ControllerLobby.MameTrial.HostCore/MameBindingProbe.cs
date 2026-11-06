using System.Diagnostics;
using ControllerLobby.DirectIndex.HostCore;
using ControllerLobby.DirectIndex.Selection;
using ControllerLobby.Transaction;
using ControllerLobby.Mame;

namespace ControllerLobby.MameTrial.HostCore;

public sealed record MameProbeResult(bool Verified,bool CleanupConfirmed,string Reason,MameListingProof? Listing);

public static class MameBindingProbe
{
    // Called only by the owner trial after selection. Unlike a cached TSV, this
    // receipt binds a newly owned process to this preparation and selection.
    public static MameProbeResult Run(MamePrepared prepared,SelectionSeal selection,string expectedReader,Func<bool> eligible,TrialLog log)
    {
        NativeTarget? target=null;bool empty=false,closed=false,observed=false;int exit=-1;
        string reason="probe-not-created";MameListingProof? listing=null;HeldProcessIdentity? identity=null;
        try
        {
            foreach(string name in new[]{"types.tsv","fields.tsv","devices.tsv","metadata.complete","keyboard.disabled"})
                if(File.Exists(Path.Combine(prepared.WorkingDirectory,name)))throw new IOException("Fresh probe outputs required");
            if(!eligible()||!prepared.BeforeLaunchCurrent())throw new IOException("Probe preparation gate refused");
            var failures=new List<string>();
            target=NativeTargetFactory.WithCompleteCommandLine(prepared.Executable,prepared.CompleteCommandLine,prepared.WorkingDirectory).CreateSuspendedInJob(failures) as NativeTarget;
            if(target==null)throw new IOException("Native probe creation unconfirmed: "+string.Join(",",failures));
            identity=target.QueryIdentity();
            if(!identity.ImagePath.Equals(prepared.Executable,StringComparison.OrdinalIgnoreCase)||!eligible()||!prepared.BeforeLaunchCurrent())throw new IOException("Probe resume gate refused");
            log.Write("mame-probe-before-resume",new{identity,selection,prepared.Profile.Sha256,prepared.SourceFiles,prepared.LaunchFiles,prepared.OwnerNvram,prepared.Arguments});
            if(!eligible()||!prepared.BeforeLaunchCurrent()||target.Resume()!=1)throw new IOException("Probe native resume refused");
            var timer=Stopwatch.StartNew();
            while(!target.WaitRootExit(TimeSpan.FromMilliseconds(20)))
            {
                if(!eligible()||timer.Elapsed>TimeSpan.FromSeconds(15))throw new IOException("Probe cancelled, stale, changed or timed out");
                if(!observed)
                {
                    try
                    {
                        var paths=target.QueryLoadedImagePaths();
                        if(paths.Any(p=>Path.GetFileName(p).Equals("xinput1_4.dll",StringComparison.OrdinalIgnoreCase)&&!p.Equals(expectedReader,StringComparison.OrdinalIgnoreCase)))throw new IOException("Probe loaded an unexpected XInput provider");
                        observed=paths.Any(p=>p.Equals(expectedReader,StringComparison.OrdinalIgnoreCase));
                    }
                    catch(System.ComponentModel.Win32Exception){}
                }
            }
            exit=target.RootExitCode;empty=target.WaitJobEmpty(TimeSpan.FromSeconds(5));reason="probe-exit-observed";
        }
        catch(Exception e)when(e is not OutOfMemoryException){reason=e.GetType().Name+":"+e.Message;}
        finally
        {
            if(target!=null)
            {
                try{if(!empty){target.TerminateJob();empty=target.WaitJobEmpty(TimeSpan.FromSeconds(5));}}
                catch(Exception e)when(e is not OutOfMemoryException){reason="probe-cleanup-unconfirmed:"+e.GetType().Name;}
                finally{try{target.Dispose();closed=target.HandlesReleased;}catch(Exception e)when(e is not OutOfMemoryException){reason="probe-close-unconfirmed:"+e.GetType().Name;}}
            }
        }
        if(empty&&closed&&exit==0&&observed)
        {
            try
            {
                if(!eligible()||!prepared.OwnerPreserved())throw new IOException("Probe completion gate refused");
                if(File.ReadAllText(Path.Combine(prepared.WorkingDirectory,"keyboard.disabled"))!="false")throw new IOException("Probe keyboard disable not confirmed");
                listing=MameListing.Validate(prepared.WorkingDirectory,selection,prepared.Profile);reason="fresh-owned-consumption-verified";
            }
            catch(Exception e)when(e is not OutOfMemoryException){reason="probe-listing-refused:"+e.Message;}
        }
        log.Write("mame-probe-completion",new{identity,ExitCode=exit,JobEmpty=empty,HandlesReleased=closed,ReaderObserved=observed,Reason=reason,Listing=listing});
        return new(listing!=null,empty&&closed,reason,listing);
    }
}
