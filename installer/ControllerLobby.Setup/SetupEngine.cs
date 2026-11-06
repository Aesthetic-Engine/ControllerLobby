using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace ControllerLobby.Setup;

public sealed record EmulatorChoice(string Id,string Title,string Kind,string ApplicationPath,bool Eligible,string Reason);
public sealed record SetupDiscovery(bool Installed,IReadOnlyList<EmulatorChoice> Entries);
public sealed record SetupResult(int ExitCode,string Details){public bool Success=>ExitCode==0;}
public interface ISetupEngine
{
    Task<SetupDiscovery> Discover(string root);
    Task<SetupResult> Run(string action,string root,IReadOnlyList<string> ids);
}
public sealed class SetupEngine(PackageBundle bundle):ISetupEngine
{
    private async Task<SetupResult> Process(string script,IEnumerable<string> arguments)
    {
        await Task.Run(bundle.Validate);
        var start=new ProcessStartInfo(Path.Combine(bundle.Root,"python","python.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=bundle.Root};
        start.ArgumentList.Add("-I");start.ArgumentList.Add("-B");start.ArgumentList.Add(Path.Combine(bundle.Root,script));
        foreach(string argument in arguments)start.ArgumentList.Add(argument);
        using var process=System.Diagnostics.Process.Start(start)??throw new IOException("Unable to start the bundled installer");
        Task<string> stdout=process.StandardOutput.ReadToEndAsync(),stderr=process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new(process.ExitCode,(await stdout)+"\n"+(await stderr));
    }
    public async Task<SetupDiscovery> Discover(string root)
    {
        var result=await Process("setup_bridge.py",[root]);
        if(!result.Success)throw new IOException(result.Details.Trim());
        using var document=JsonDocument.Parse(result.Details);
        var data=document.RootElement;
        return new(data.GetProperty("Installed").GetBoolean(),data.GetProperty("Entries").EnumerateArray().Select(e=>new EmulatorChoice(
            e.GetProperty("Id").GetString()!,e.GetProperty("Title").GetString()!,e.GetProperty("Kind").GetString()!,
            e.GetProperty("ApplicationPath").GetString()!,e.GetProperty("Eligible").GetBoolean(),e.GetProperty("Reason").GetString()!)).ToArray());
    }
    public Task<SetupResult> Run(string action,string root,IReadOnlyList<string> ids)
    {
        if(action is not ("preview" or "install" or "verify" or "repair" or "remove"))throw new ArgumentException("Unknown setup action");
        bool fresh=action is "preview" or "install";
        var args=new List<string>{action,"--root",root,"--receipt",fresh?bundle.Root:Path.Combine(root,"ControllerLobby.Setup")};
        if(fresh){args.AddRange(["--package-sha256",bundle.PackagePin,"--entries",string.Join(',',ids)]);}
        return Process("bigbox_install.py",args);
    }
}

public sealed class SetupController(ISetupEngine engine)
{
    public event Action? Changed;
    public string Root{get;private set;}="";
    public string Status{get;private set;}="Choose your LaunchBox folder to get started.";
    public string Details{get;private set;}="";
    public bool Busy{get;private set;}
    public bool Installed{get;private set;}
    public bool Complete{get;private set;}
    public IReadOnlyList<EmulatorChoice> Entries{get;private set;}=[];
    public HashSet<string> Selected{get;}=new(StringComparer.Ordinal);
    public bool CanInstall=>!Busy&&!Installed&&!Complete&&Selected.Count>0;
    public bool CanMaintain=>!Busy&&Installed&&!Complete;
    public bool RequestClose()=>!Busy;
    private void Update(string status){Status=status;Changed?.Invoke();}
    public void Select(string id,bool selected)
    {
        if(Busy||!Entries.Any(e=>e.Id==id&&e.Eligible))return;
        if(selected)Selected.Add(id);else Selected.Remove(id);Changed?.Invoke();
    }
    public async Task ChooseFolder(string root)
    {
        if(Busy)return;
        Busy=true;Complete=false;Installed=false;Selected.Clear();Entries=[];Root="";Details="";Update("Checking your LaunchBox setup…");
        try
        {
            Root=Path.GetFullPath(root);
            var result=await engine.Discover(Root);Installed=result.Installed;Entries=result.Entries;
            foreach(var entry in Entries.Where(e=>e.Eligible))
                if(Entries.Count(e=>e.Eligible&&e.Kind==entry.Kind)==1)Selected.Add(entry.Id);
            Status=Installed?"ControllerLobby is already installed. Choose a maintenance action.":Entries.Any(e=>e.Eligible)?"Choose the emulators that should show the controller lobby.":"No available MAME or RetroArch entries were found. Check their setup in LaunchBox.";
        }
        catch(Exception error){Status="We couldn't use that LaunchBox folder.";Details=error.Message;}
        finally{Busy=false;Changed?.Invoke();}
    }
    public async Task Execute(string action)
    {
        if(action is not ("install" or "verify" or "repair" or "remove"))return;
        if(action=="install"?!CanInstall:!CanMaintain)return;
        Busy=true;Details="";Update(action=="install"?"Checking installation changes…":"Working on your installation…");
        try
        {
            var ids=Selected.ToArray();
            if(action=="install")
            {
                var preview=await engine.Run("preview",Root,ids);Details=preview.Details;
                if(!preview.Success){Status="Setup couldn't finish its checks. Open Details for the next step.";return;}
                Update("Installing ControllerLobby. Please wait…");
            }
            var result=await engine.Run(action,Root,ids);Details+="\n"+result.Details;
            if(result.Success)
            {
                Complete=action is "install" or "remove";
                Status=action switch{"install"=>"You're all set! Open LaunchBox or BigBox and start a MAME or RetroArch game.","remove"=>"ControllerLobby has been removed.","verify"=>"Your installation checked out successfully.",_=>"Your installation has been repaired."};
            }
            else Status="Setup couldn't finish. Open Details for recovery or troubleshooting information.";
        }
        catch(Exception error){Status="Setup couldn't finish. Open Details for more information.";Details+="\n"+error.Message;}
        finally{Busy=false;Changed?.Invoke();}
    }
}
