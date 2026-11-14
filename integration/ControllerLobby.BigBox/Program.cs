#nullable enable
using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using System.Windows;
using ControllerLobby.DirectIndex.HostCore;
using ControllerLobby.MameTrial.HostCore;
using ControllerLobby.Shim;
using ControllerLobby.LobbyLink;

namespace ControllerLobby.BigBox;
internal sealed record EntryConfig(string Kind,string ManifestSha256,string LegacyShim,string Content,string RomDirectory,PayloadFile[] LegacyFiles,string Mode="trial");
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            string root=AppContext.BaseDirectory;
            var config=JsonSerializer.Deserialize<EntryConfig>(File.ReadAllBytes(Path.Combine(root,"bigbox.json")))??throw new IOException("Entry configuration missing");
            if(config.Mode!="normal")throw new IOException("ControllerLobby requires normal mode");
            NormalPackage.RequireBetaKind(config.Kind);
            // File-only validation does not load a reader or exercise controllers.
            bool verify=args.SequenceEqual(new[]{"--verify-only"});
            // Verify stays strict, so Setup still reports updates; a game launch tolerates them unmapped.
            var normal=NormalPackage.Open(root,config.ManifestSha256,allowUpdateDrift:!verify);
            if(config.Kind!=normal.Manifest.Kind)throw new IOException("Entry and manifest emulator kinds differ");
            if(verify)return 0;
            using var frontend=FrontendWatch.ForParent();
            if(!frontend.Alive)throw new IOException("A live LaunchBox or BigBox parent is required");
            var app=new Application();
            var window=new ControllerLobby.DirectIndex.Windows.TrialWindow(null,()=>frontend.Alive,true,
                (view,token,hide)=>NormalPlay.Run(normal,args,view,token,hide));
            appRunStarted=true;
            return app.Run(window);
        }
        catch(Exception e)when(e is not OutOfMemoryException)
        {
            string folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ControllerLobby","BigBox");
            try
            {
                Directory.CreateDirectory(folder);File.WriteAllText(Path.Combine(folder,"refusal-"+DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ")+"-"+Guid.NewGuid().ToString("N")[..8]+".txt"),e.GetType().Name+": "+e.Message);
                foreach(var old in new DirectoryInfo(folder).GetFiles("refusal-*.txt").OrderByDescending(f=>f.CreationTimeUtc).Skip(20))old.Delete(); // guard:prune-refusals
            }
            catch(IOException){}catch(UnauthorizedAccessException){}
            if(!args.SequenceEqual(new[]{"--verify-only"}))ShowRefusal(e);
            return 3;
        }
    }
    // An Application runs once; a refusal raised from inside the lobby's run cannot show a second window.
    private static bool appRunStarted;
    internal static string RefusalMessage(Exception e)=>e.Message switch
    {
        "A live LaunchBox or BigBox parent is required"=>"ControllerLobby starts games from LaunchBox or BigBox. Start this game from LaunchBox or BigBox.",
        "Another ControllerLobby game session is still running"=>"Another game started by ControllerLobby is still running. Close it, then try again.",
        DuckStationLaunch.RunningMessage=>DuckStationLaunch.RunningMessage,
        XemuLaunch.RunningMessage=>XemuLaunch.RunningMessage,
        Pcsx2Launch.RunningMessage=>Pcsx2Launch.RunningMessage,
        XemuLaunch.ChangedMessage=>XemuLaunch.ChangedMessage,
        _=>"ControllerLobby's files for this emulator have changed or are damaged. Close LaunchBox and run \"Verify installation\" in the ControllerLobby.Setup folder inside your LaunchBox folder.",
    };
    // A short, self-closing notice instead of a modal dialog, so a refusal cannot trap the BigBox game page.
    // Returns true only when the notice was shown and has closed again.
    internal static bool ShowRefusal(Exception e,int closeAfterMs=10000)
    {
        bool shown=false;
        try
        {
            if(appRunStarted)return false;
            var app=Application.Current??new Application();
            if(app.Dispatcher.HasShutdownStarted)return false;
            string message=RefusalMessage(e);
            var notice=new ControllerLobby.DirectIndex.Windows.TrialWindow(null,null,true,async(view,token,hide)=>
            {
                view.Status("This game couldn't start",message+" This window closes in 10 seconds.");
                try{await Task.Delay(closeAfterMs,token);}catch(OperationCanceledException){}
                return true;
            },noticeOnly:true);
            notice.Loaded+=(_,_)=>shown=notice.IsVisible;
            // Constructing the notice may already have made it MainWindow; that says nothing about whether it ran.
            app.MainWindow=notice;app.ShutdownMode=ShutdownMode.OnMainWindowClose;
            app.Run(notice); // guard:refusal-notice-runs
        }
        catch{shown=false;} // The refusal record is already written; the notice is best effort.
        return shown;
    }
}
