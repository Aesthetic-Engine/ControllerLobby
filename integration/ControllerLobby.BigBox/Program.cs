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
            if(config.Mode!="normal")throw new IOException("Beta 1 requires normal mode");
            NormalPackage.RequireBetaKind(config.Kind);
            // File-only validation does not load a reader or exercise controllers.
            bool verify=args.SequenceEqual(new[]{"--verify-only"});
            var normal=NormalPackage.Open(root,config.ManifestSha256);
            if(config.Kind!=normal.Manifest.Kind)throw new IOException("Entry and manifest emulator kinds differ");
            if(verify)return 0;
            using var frontend=FrontendWatch.ForParent();
            if(!frontend.Alive)throw new IOException("A live LaunchBox or BigBox parent is required");
            var app=new Application();
            var window=new ControllerLobby.DirectIndex.Windows.TrialWindow(null,()=>frontend.Alive,true,
                (view,token,hide)=>NormalPlay.Run(normal,args,view,token,hide));
            return app.Run(window);
        }
        catch(Exception e)when(e is not OutOfMemoryException)
        {
            // No modal dialog can trap the BigBox game page on a refusal.
            string folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ControllerLobby","BigBox");
            try{Directory.CreateDirectory(folder);File.WriteAllText(Path.Combine(folder,"refusal-"+Guid.NewGuid().ToString("N")+".txt"),e.GetType().Name+": "+e.Message);}catch(IOException){}catch(UnauthorizedAccessException){}
            return 3;
        }
    }
}
