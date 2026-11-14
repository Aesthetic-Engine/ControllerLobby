using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace ControllerLobby.Setup;

public sealed record EmulatorChoice(string Id,string Title,string Kind,string ApplicationPath,bool Eligible,string Reason);
// RecoveryPending: true when install state is left in the folder without an active installation; null when not reported.
public sealed record SetupDiscovery(bool Installed,IReadOnlyList<EmulatorChoice> Entries,string? InstalledPackagePin=null,IReadOnlyList<string>? InstalledEntryIds=null,bool? RecoveryPending=null);
public sealed record SetupResult(int ExitCode,string Details){public bool Success=>ExitCode==0;}
public interface ISetupEngine
{
    /// <summary>The package this Setup carries; compared with the installed package to offer an upgrade.</summary>
    string PackagePin=>"";
    Task<SetupDiscovery> Discover(string root);
    Task<SetupResult> Run(string action,string root,IReadOnlyList<string> ids);
}
public sealed class SetupEngine(PackageBundle bundle):ISetupEngine
{
    public string PackagePin=>bundle.PackagePin;
    private static async Task<SetupResult> Capture(ProcessStartInfo start)
    {
        using var process=System.Diagnostics.Process.Start(start)??throw new IOException("Unable to start the installer");
        Task<string> stdout=process.StandardOutput.ReadToEndAsync(),stderr=process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new(process.ExitCode,(await stdout)+"\n"+(await stderr));
    }
    private async Task<SetupResult> Process(string script,IEnumerable<string> arguments)
    {
        await Task.Run(bundle.Validate);
        var start=new ProcessStartInfo(Path.Combine(bundle.Root,"python","python.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=bundle.Root};
        start.ArgumentList.Add("-I");start.ArgumentList.Add("-B");start.ArgumentList.Add(Path.Combine(bundle.Root,script));
        foreach(string argument in arguments)start.ArgumentList.Add(argument);
        return await Capture(start);
    }
    // The installed copy's own Uninstall.cmd removes an installation made by another version, so that
    // version's engine reads its own install record. It copies its runtime to TEMP before deleting itself.
    internal static string UninstallArguments(string uninstall)=>"/d /s /c \"\""+uninstall+"\"\"";
    private static Task<SetupResult> RemoveInstalled(string root)
    {
        string setup=Path.Combine(root,"ControllerLobby.Setup"),uninstall=Path.Combine(setup,"Uninstall.cmd");
        PackageBundle.NoReparse(setup);
        if(!File.Exists(uninstall)||(File.GetAttributes(uninstall)&FileAttributes.ReparsePoint)!=0)throw new IOException("The installed Uninstall.cmd is missing: "+uninstall);
        var start=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"cmd.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=Path.GetTempPath()};
        // /s with doubled outer quotes keeps the path quoted for cmd even when it holds spaces, brackets or &.
        if(uninstall.Contains('"')||uninstall.Contains('%'))throw new IOException("The LaunchBox folder name contains a character the uninstaller cannot be started with: "+root);
        start.Arguments=UninstallArguments(uninstall);
        start.Environment["CONTROLLERLOBBY_NO_PAUSE"]="1";
        return Capture(start);
    }
    public async Task<SetupDiscovery> Discover(string root)
    {
        var result=await Process("setup_bridge.py",[root]);
        if(!result.Success)throw new IOException(result.Details.Trim());
        using var document=JsonDocument.Parse(result.Details);
        var data=document.RootElement;
        string? pin=data.TryGetProperty("InstalledPackagePin",out var p)&&p.GetString() is {Length:64} value?value:null;
        string[]? ids=data.TryGetProperty("InstalledEntryIds",out var i)?i.EnumerateArray().Select(x=>x.GetString()!).ToArray():null;
        bool? pending=data.TryGetProperty("RecoveryPending",out var r)&&r.ValueKind is JsonValueKind.True or JsonValueKind.False?r.GetBoolean():null;
        return new(data.GetProperty("Installed").GetBoolean(),data.GetProperty("Entries").EnumerateArray().Select(e=>new EmulatorChoice(
            e.GetProperty("Id").GetString()!,e.GetProperty("Title").GetString()!,e.GetProperty("Kind").GetString()!,
            e.GetProperty("ApplicationPath").GetString()!,e.GetProperty("Eligible").GetBoolean(),e.GetProperty("Reason").GetString()!)).ToArray(),pin,ids,pending);
    }
    public Task<SetupResult> Run(string action,string root,IReadOnlyList<string> ids)
    {
        if(action=="remove-installed")return RemoveInstalled(root);
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
    private IReadOnlyList<string> installedIds=[];
    private string? installedPin;
    /// <summary>Installed by a different ControllerLobby version (its package differs from this Setup's).</summary>
    public bool NeedsUpgrade=>Installed&&installedPin!=null&&engine.PackagePin.Length==64&&!installedPin.Equals(engine.PackagePin,StringComparison.OrdinalIgnoreCase);
    /// <summary>An install in this folder did not confirm its rollback; nothing new is installed until recovery is confirmed.</summary>
    public bool RecoveryBlocked{get;private set;}
    // Set by a RECOVERY INCOMPLETE report or an unreadable state after a failed install. Only a folder check that
    // explicitly reports no leftover install state clears it; checkboxes and a refresh without that report cannot.
    private string? unconfirmedRoot;
    public bool CanInstall=>!Busy&&!Installed&&!Complete&&!RecoveryBlocked&&Selected.Count>0;
    public bool CanMaintain=>!Busy&&Installed&&!Complete;
    // Verify and Repair by this Setup's engine are only for its own version; another version is replaced instead.
    public bool CanRepair=>CanMaintain&&!NeedsUpgrade;
    public bool CanUpgrade=>CanMaintain&&NeedsUpgrade;
    private void Apply(SetupDiscovery result,bool confirmsRecovery=false)
    {
        Installed=result.Installed;Entries=result.Entries;installedPin=result.InstalledPackagePin;installedIds=result.InstalledEntryIds??[];Selected.Clear();
        if(confirmsRecovery&&result.RecoveryPending==false&&SameRoot(unconfirmedRoot))unconfirmedRoot=null;
        RecoveryBlocked=/*guard:recovery-block-held*/result.RecoveryPending==true||SameRoot(unconfirmedRoot);
        if(RecoveryBlocked)return;
        foreach(var entry in Entries.Where(e=>e.Eligible))
            if(Entries.Count(e=>e.Eligible&&e.Kind==entry.Kind)==1)Selected.Add(entry.Id);
    }
    private bool SameRoot(string? root)=>root!=null&&root.Equals(Root,StringComparison.OrdinalIgnoreCase);
    private string ChooseStatus()=>RecoveryBlocked&&!Installed?"An earlier ControllerLobby install in this folder did not finish undoing its changes. Setup won't install again until that is recovered. Follow the recovery steps in the setup guide, then choose this folder again."
        :NeedsUpgrade?"A different version of ControllerLobby is installed. Click Upgrade to replace it with this version; your emulator choices are kept."
        :Installed?"ControllerLobby is already installed. Choose a maintenance action.":Entries.Any(e=>e.Eligible)?"Choose the emulators that should show the controller lobby.":"No available MAME, RetroArch, DuckStation, Xemu or PCSX2 entries were found. Check their setup in LaunchBox.";
    public bool RequestClose()=>!Busy;
    private void Update(string status){Status=status;Changed?.Invoke();}
    public void Select(string id,bool selected)
    {
        if(Busy||RecoveryBlocked||!Entries.Any(e=>e.Id==id&&e.Eligible))return;
        if(selected)Selected.Add(id);else Selected.Remove(id);Changed?.Invoke();
    }
    public async Task ChooseFolder(string root)
    {
        if(Busy)return;
        Busy=true;Complete=false;Installed=false;RecoveryBlocked=false;installedPin=null;installedIds=[];Selected.Clear();Entries=[];Root="";Details="";Update("Checking your LaunchBox setup…");
        try
        {
            Root=Path.GetFullPath(root);
            Apply(await engine.Discover(Root),confirmsRecovery:true);
            Status=ChooseStatus();
        }
        catch(Exception error){Status="We couldn't use that LaunchBox folder.";Details=error.Message;}
        finally{Busy=false;Changed?.Invoke();}
    }
    private async Task<bool> InstallSelected(bool upgrade=false)
    {
        var ids=Selected.ToArray();
        var preview=await engine.Run("preview",Root,ids);Details+=(Details.Length>0?"\n":"")+preview.Details;
        if(!preview.Success){Status="Setup couldn't finish its checks. Open Details for the next step.";return false;}
        Update("Installing ControllerLobby. Please wait…");
        var result=await engine.Run("install",Root,ids);Details+="\n"+result.Details;
        if(!result.Success){await AfterFailedInstall(ids,upgrade,result.Details);return false;}
        Complete=true;Status="You're all set! Open LaunchBox or BigBox and start a game with a selected emulator.";return true;
    }
    // A failed install may roll back fully, or leave files, a record or redirected entries behind (RECOVERY INCOMPLETE,
    // possibly before any record is activated). Only a full rollback is described as LaunchBox using emulators directly.
    private async Task AfterFailedInstall(string[] ids,bool upgrade,string report)
    {
        string prefix=upgrade?"The installed version was removed, but this version couldn't be installed. ":"Setup couldn't finish. ";
        const string help="Open Details for recovery or troubleshooting information.";
        if(/*guard:incomplete-rollback-blocks-retry*/report.Contains("RECOVERY INCOMPLETE",StringComparison.Ordinal))unconfirmedRoot=Root;
        try{Apply(await engine.Discover(Root));}
        catch(Exception error)
        {
            unconfirmedRoot=Root;RecoveryBlocked=true;Installed=false;Entries=[];Selected.Clear();Details+="\n"+error.Message;
            Status=prefix+"Setup couldn't check what was left behind. "+help;return;
        }
        if(Installed){Status=prefix+"Part of the installation is still recorded, so LaunchBox may not use your emulators directly yet. "+help;return;}
        if(RecoveryBlocked){Status=prefix+"Setup couldn't confirm that your emulator entries were put back. "+help;return;}
        Selected.Clear();
        foreach(string id in ids)if(Entries.Any(e=>e.Id==id&&e.Eligible))Selected.Add(id);
        Status=upgrade?prefix+"Its changes were rolled back, so LaunchBox uses your emulators directly. Open Details, then click Install to try again."
            :prefix+help;
    }
    public async Task Execute(string action)
    {
        if(action is not ("install" or "verify" or "repair" or "remove" or "upgrade"))return;
        bool allowed=action switch{"install"=>CanInstall,"repair" or "verify"=>CanRepair,"upgrade"=>CanUpgrade,_=>CanMaintain};
        if(!allowed)return;
        Busy=true;Details="";Update(action=="install"?"Checking installation changes…":action=="upgrade"?"Removing the installed version…":"Working on your installation…");
        try
        {
            if(action=="install"){await InstallSelected();return;}
            var keep=installedIds.ToArray();
            // An installation from another version is removed by its own uninstaller.
            var result=await engine.Run(action is "upgrade" || action=="remove"&&NeedsUpgrade?"remove-installed":action,Root,Selected.ToArray());Details+=result.Details;
            if(!result.Success)
            {
                Status=action=="upgrade"?"Setup couldn't remove the installed version, so nothing new was installed. Open Details for recovery or troubleshooting information."
                    :"Setup couldn't finish. Open Details for recovery or troubleshooting information.";
                return;
            }
            if(action is "verify" or "repair"){Status=action=="verify"?"Your installation checked out successfully.":"Your installation has been repaired.";return;}
            // Removed: rediscover so the same window can install again (a new version, or other emulators).
            Apply(await engine.Discover(Root));
            if(Installed){Status="Setup removed ControllerLobby, but an installation is still recorded. Open Details for recovery or troubleshooting information.";return;}
            if(action=="remove"){Status="ControllerLobby has been removed. You can close Setup, or choose emulators and click Install to set it up again.";return;}
            Selected.Clear();
            foreach(string id in keep)if(Entries.Any(e=>e.Id==id&&e.Eligible))Selected.Add(id);
            if(/*guard:upgrade-keeps-choices*/Selected.Count==0||Selected.Count!=keep.Length)
            {
                Status="The installed version was removed, but not every emulator you had chosen can be set up now. Choose your emulators and click Install.";return;
            }
            Update("Installing this version…");
            if(await InstallSelected(upgrade:true))Status="Upgrade complete! Open LaunchBox or BigBox and start a game with a selected emulator.";
        }
        catch(Exception error){Status="Setup couldn't finish. Open Details for more information.";Details+="\n"+error.Message;}
        finally{Busy=false;Changed?.Invoke();}
    }
}
