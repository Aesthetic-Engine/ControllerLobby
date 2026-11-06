using System.IO;
using ControllerLobby.Transaction;

namespace ControllerLobby.BigBox;

internal sealed record NormalCompletion(bool Started,bool CleanupConfirmed,int? ExitCode,bool Forced=false);
internal static class NormalLifetime
{
    // No run deadline and no controller observations are inputs after resume.
    public static NormalCompletion Run(ITargetFactory factory,Func<bool> beforeResume,Func<bool> keepRunning,Action afterResume,Func<ISuspendedTarget,bool>? identity=null)
    {
        if(!beforeResume())return new(false,true,null);
        ISuspendedTarget? target=null;bool empty=false,started=false,forced=false;int? exit=null;
        try
        {
            target=factory.CreateSuspendedInJob([])??throw new IOException("Owned emulator creation failed");
            if(identity!=null&&!identity(target))throw new IOException("Held emulator identity mismatch");
            if(beforeResume())
            {
                if(target.Resume()!=1)throw new IOException("Emulator resume refused");
                started=true;afterResume();
                while(!target.WaitRootExit(TimeSpan.FromMilliseconds(100)))
                    if(!keepRunning()){forced=true;target.TerminateJob();break;}
                if(target.WaitRootExit(TimeSpan.Zero))exit=target.RootExitCode;
                empty=target.WaitJobEmpty(TimeSpan.FromSeconds(5));
                if(!empty){forced=true;target.TerminateJob();empty=target.WaitJobEmpty(TimeSpan.FromSeconds(5));}
                if(!empty)throw new IOException("Emulator cleanup unconfirmed");
            }
        }
        finally
        {
            if(target!=null)
            {
                try{if(!empty){forced=true;target.TerminateJob();empty=target.WaitJobEmpty(TimeSpan.FromSeconds(5));}}
                finally{target.Dispose();}
                if(!empty)throw new IOException("Emulator cleanup unconfirmed; recovery marker retained");
            }
        }
        return new(started,empty,exit,forced);
    }
}
