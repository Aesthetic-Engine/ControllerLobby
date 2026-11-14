using ControllerLobby.DirectIndex;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using ControllerLobby.DirectIndex.HostCore;
using ControllerLobby.DirectIndex.Native;
using ControllerLobby.DirectIndex.Preparation;
using ControllerLobby.DirectIndex.Selection;
using ControllerLobby.RetroArch;
using ControllerLobby.Mame;
using ControllerLobby.DuckStation;
using ControllerLobby.Transaction;

namespace ControllerLobby.BigBox;

internal sealed record NormalManifest(int Schema,string Kind,string Executable,string ExecutableSha256,
    ReaderBinding Reader,string[] ReferenceArguments,TrialTarget? RetroArch,PayloadFile[] Payload,PayloadFile[]? ReferenceBinaries=null,ProtectedFile[]? ReferenceConfiguration=null,
    DuckStationCopy? DuckStation=null,DuckStationCopy? Pcsx2=null);
internal sealed class NormalPackage
{
    public NormalManifest Manifest{get;}
    private readonly ProtectedFile[] pins;
    /// <summary>Set when Open(allowUpdateDrift) found the emulator or system XInput updated since setup.
    /// The package is then valid only for launching with existing controls; the reader must not be loaded.</summary>
    public string? UpdateDrift{get;private init;}
    private NormalPackage(NormalManifest manifest,ProtectedFile[] pins){Manifest=manifest;this.pins=pins;}
    public bool Current()=>pins.All(p=>p.Unchanged());
    internal static void RequireBetaKind(string kind)
    {
        if(kind is not ("mame" or "retroarch" or DuckStationLaunch.Kind or XemuLaunch.Kind or Pcsx2Launch.Kind))throw new IOException("ControllerLobby supports MAME, RetroArch, DuckStation, Xemu and PCSX2 only");
    }
    private static void RejectDuplicates(JsonElement value)
    {
        if(value.ValueKind==JsonValueKind.Object)
        {
            var names=new HashSet<string>(StringComparer.Ordinal);
            foreach(var p in value.EnumerateObject()){if(!names.Add(p.Name))throw new IOException("Duplicate manifest field");RejectDuplicates(p.Value);}
        }
        else if(value.ValueKind==JsonValueKind.Array)foreach(var v in value.EnumerateArray())RejectDuplicates(v);
    }
    public static NormalPackage Open(string root,string expected)=>Open(root,expected,false);
    // allowUpdateDrift tolerates only a changed hash of the configured emulator or System32 XInput1_4.dll
    // (an ordinary emulator or Windows update). Everything else, including ControllerLobby's own files,
    // still refuses. The current files are pinned so a further change before launch is still caught.
    public static NormalPackage Open(string root,string expected,bool allowUpdateDrift)
    {
        string path=Path.Combine(root,"normal.json");var stamp=ProtectedFile.Capture(path);
        if(!stamp.Present||stamp.Sha256!=expected)throw new IOException("Normal-play manifest changed");
        byte[] bytes=File.ReadAllBytes(path);
        if(Convert.ToHexString(SHA256.HashData(bytes))!=expected)throw new IOException("Manifest changed during read");
        using(var doc=JsonDocument.Parse(bytes))RejectDuplicates(doc.RootElement);
        var m=JsonSerializer.Deserialize<NormalManifest>(bytes,new JsonSerializerOptions{UnmappedMemberHandling=System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow})??throw new IOException("Missing normal-play manifest");
        RequireBetaKind(m.Kind);
        if(m.Schema!=1||m.Payload==null||m.Payload.Length==0||m.Reader==null||m.ReferenceArguments==null)throw new IOException("Invalid normal-play manifest");
        var files=new List<ProtectedFile>{stamp};
        foreach(var p in m.Payload)
        {
            if(Path.GetFileName(p.Path)!=p.Path||p.Path.Contains(':')||p.Path is "." or "..")throw new IOException("Invalid payload path");
            var file=ProtectedFile.Capture(Path.Combine(root,p.Path));
            if(!file.Present||file.Sha256!=p.Sha256)throw new IOException("Normal-play payload changed");files.Add(file);
        }
        foreach(string actual in new[]{Environment.ProcessPath!,typeof(NormalPackage).Assembly.Location,typeof(PinnedXInputReader).Assembly.Location})
            if(!files.Any(p=>p.Path.Equals(actual,StringComparison.OrdinalIgnoreCase)))throw new IOException("Executing host not pinned");
        string system=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"XInput1_4.dll");
        if(!m.Reader.LibraryPath.Equals(system,StringComparison.OrdinalIgnoreCase)||!m.Reader.FunctionModulePath.Equals(system,StringComparison.OrdinalIgnoreCase)||m.Reader.Export is not(StateExport.NamedGetState or StateExport.Ordinal100))throw new IOException("System XInput reader required");
        string? drift=null;
        var executable=ProtectedFile.Capture(m.Executable);
        if(!executable.Present||executable.Path.Equals(Environment.ProcessPath,StringComparison.OrdinalIgnoreCase))throw new IOException("Configured emulator changed");
        if(executable.Sha256!=m.ExecutableSha256){if(!allowUpdateDrift)throw new IOException("Configured emulator changed");drift="emulator";} // guard:drift-emulator
        files.Add(executable);
        var native=ProtectedFile.Capture(system);
        if(!native.Present)throw new IOException("Pinned reader changed");
        if(native.Sha256!=m.Reader.LibrarySha256||native.Sha256!=m.Reader.FunctionModuleSha256){if(!allowUpdateDrift)throw new IOException("Pinned reader changed");drift??="windows";} // guard:drift-reader
        files.Add(native);
        // The copy is ControllerLobby's own: it is pinned in full whenever it could be launched, and never tolerated as drift.
        if(/*guard:duckstation-block-kind*/(m.Kind==DuckStationLaunch.Kind)!=(m.DuckStation!=null))throw new IOException("Invalid normal-play manifest");
        if(m.DuckStation!=null&&drift!="emulator")files.AddRange(DuckStationLaunch.Pin(root,m));
        if(/*guard:pcsx2-block-kind*/(m.Kind==Pcsx2Launch.Kind)!=(m.Pcsx2!=null)||m.Pcsx2!=null&&m.DuckStation!=null)throw new IOException("Invalid normal-play manifest");
        if(m.Pcsx2!=null&&drift!="emulator")files.AddRange(Pcsx2Launch.Pin(root,m));
        if(files.Select(p=>p.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=files.Count)throw new IOException("Duplicate pinned path");
        var result=new NormalPackage(m,files.ToArray()){UpdateDrift=drift};if(!result.Current())throw new IOException("Package changed during inspection");return result;
    }
}

internal sealed record NormalPlan(string[] Arguments,bool Assigned,string Notice,ProtectedFile[] Inputs)
{
    /// <summary>The program to start when it is not the manifest's own executable (the DuckStation launch copy).</summary>
    public string? Executable{get;init;}
    public bool Current()=>Inputs.All(p=>p.Unchanged());
    public const string ExistingNotice="Using this game's existing controls. Lobby join order is not applied.";
    public static NormalPlan Existing(NormalManifest m,string[] original)=>Existing(m,original,ExistingNotice);
    public static NormalPlan Existing(NormalManifest m,string[] original,string notice)
    {
        NormalPackage.RequireBetaKind(m.Kind);
        return new(m.Kind=="mame"?[..original,"-nowindow"]:original.ToArray(),false,notice,[]);
    }
    public static NormalPlan Prepare(NormalManifest m,string[] original,SelectionSeal selection,string session,IConfigReader? configReader=null,Func<IEnumerable<DuckProcess>>? duckStationProcesses=null,Pcsx2Launch.PreLobbyRead? pcsx2PreLobby=null,string? pcsx2Pin=null)
    {
        // Unknown games/options retain their exact vector and their ordinary save/config behavior.
        var fallback=Existing(m,original);
        if(selection.PlayerIndices.Count is <1 or >4)return fallback;
        if(m.Kind==DuckStationLaunch.Kind)
        {
            // Only a manifest that carries the pinned launch copy may be mapped; a bare one is refused before any write.
            if(/*guard:duckstation-plan-needs-copy*/m.DuckStation==null)throw new IOException("Invalid normal-play manifest");
            // Before any write: a running DuckStation may be using the copy's settings and the shared saves.
            if(/*guard:running-before-writes*/DuckStationLaunch.Running(m,duckStationProcesses))throw new IOException(DuckStationLaunch.RunningMessage);
            try{return DuckStationLaunch.Prepare(m,original,selection);}
            catch(Exception e)when(e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException){return Existing(m,original,DuckStationLaunch.Notice("unavailable"));}
        }
        if(m.Kind==Pcsx2Launch.Kind)
        {
            if(/*guard:pcsx2-plan-needs-copy*/m.Pcsx2==null)throw new IOException("Invalid normal-play manifest");
            // Before any write: a running PCSX2 may be using the shared memory cards.
            if(/*guard:pcsx2-running-before-writes*/Pcsx2Launch.Running(m,duckStationProcesses))throw new IOException(Pcsx2Launch.RunningMessage);
            try{return Pcsx2Launch.Prepare(m,original,selection,pcsx2PreLobby,null,pcsx2Pin);}
            catch(Exception e)when(e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException){return Existing(m,original,Pcsx2Launch.Notice("unavailable"));}
        }
        if(m.ReferenceBinaries!=null&&m.ReferenceBinaries.Any(p=>ProtectedFile.Capture(p.Path).Sha256!=p.Sha256))return fallback;
        try
        {
        if(m.Kind=="retroarch"&&TryResolveRetroArch(m,original,out var t,out var extraArgs,out var infoPin))
        {
            // Qualify explicit executable-relative or absolute resource/save roots.
            // Other configs launch unchanged with the fallback notice.
            var qualified=ProtectedFile.Capture(t.MainConfig);
            if(!qualified.Present)return fallback;
            string configText=File.ReadAllText(t.MainConfig).Replace("\r\n","\n");
            if(!qualified.Unchanged()||(infoPin!=null&&!infoPin.Unchanged()))return fallback;
            foreach(string key in new[]{"system_directory","joypad_autoconfig_dir","savefile_directory","savestate_directory"})
            {
                var matches=Regex.Matches(configText,"(?m)^[ \t]*"+key+"[ \t]*=[ \t]*\"([^\"]*)\"[ \t]*(?:#.*)?$");
                if(matches.Count!=1)return fallback;
                string value=matches[0].Groups[1].Value;
                if(!value.StartsWith(":/",StringComparison.Ordinal)&&!value.StartsWith(":\\",StringComparison.Ordinal)&&!Path.IsPathFullyQualified(value))return fallback;
            }
            string raSession=Path.Combine(session,"retroarch");
            if(Directory.Exists(raSession)||File.Exists(raSession))raSession=Path.Combine(session,"retroarch-"+Guid.NewGuid().ToString("N"));
            var prepared=RetroArchPreparation.Prepare(new(SelectionPolicy.DirectIndexExperiment,"normal-play",selection,
                new(t.Root,t.MainConfig,t.CoreName,t.Content),t.Executable,t.CoreLibrary,raSession,"reference profile; normal integration candidate",extraArgs),configReader);
            if(prepared.Specification==null||!prepared.SourceConfigs.Any(p=>p.Path.Equals(qualified.Path,StringComparison.OrdinalIgnoreCase)&&p.Sha256==qualified.Sha256))return fallback;
            if(m.ReferenceBinaries!=null&&m.ReferenceBinaries.Any(b=>prepared.Files.Any(f=>f.Path.Equals(b.Path,StringComparison.OrdinalIgnoreCase)&&f.Sha256!=b.Sha256)||ProtectedFile.Capture(b.Path).Sha256!=b.Sha256))return fallback;
            if(!RetroArchPreparation.Preserved(prepared.SourceConfigs,prepared.Files))throw new IOException("Preparation inputs changed");
            var inputs=new List<ProtectedFile>();
            if(infoPin!=null){if(!infoPin.Unchanged())return fallback;inputs.Add(infoPin);}
            foreach(var p in prepared.SourceConfigs)
            {
                var captured=ProtectedFile.Capture(p.Path);
                if(captured.Present!=p.Present||captured.Sha256!=p.Sha256)throw new IOException("Source config changed during binding");
                inputs.Add(captured);
            }
            foreach(var p in prepared.Files)
            {
                var captured=ProtectedFile.Capture(p.Path);
                if(!captured.Present||captured.Sha256!=p.Sha256||captured.Length!=p.Length)throw new IOException("Prepared file changed during binding");
                inputs.Add(captured);
            }
            // Use the inspected copied chain, without the trial's disposable save overrides.
            return new(prepared.Specification.Arguments.ToArray(),true,"Player assignments applied for this session. Game saves use your normal location.",inputs.ToArray());
        }
        if(m.Kind=="mame"&&TryResolveMameMachine(m,original,out string machine))
        {
            if(m.ReferenceConfiguration!=null&&m.ReferenceConfiguration.Any(p=>!p.Unchanged()))return fallback;
            string root=Path.GetDirectoryName(Path.GetFullPath(m.Executable))!;
            var profile=MameProfile.Generate(selection,allowPartialPlayers:true);
            string game=Path.Combine(root,"cfg",machine+".cfg");var before=ProtectedFile.Capture(game);
            string filtered=MameProfile.FilterGame(machine,before.Present?File.ReadAllText(game):null);
            string mameSession=Directory.Exists(Path.Combine(session,"controller"))||Directory.Exists(Path.Combine(session,"cfg"))?Path.Combine(session,"mame-"+Guid.NewGuid().ToString("N")):session;
            string controllers=Path.Combine(mameSession,"controller"),configs=Path.Combine(mameSession,"cfg");Directory.CreateDirectory(controllers);Directory.CreateDirectory(configs);
            string ctrl=Path.Combine(controllers,"players.cfg"),def=Path.Combine(configs,"default.cfg"),cfg=Path.Combine(configs,machine+".cfg");
            File.WriteAllText(ctrl,profile.ControllerXml);File.WriteAllText(def,profile.DefaultXml);File.WriteAllText(cfg,filtered);
            if(m.ReferenceBinaries!=null&&m.ReferenceBinaries.Any(p=>ProtectedFile.Capture(p.Path).Sha256!=p.Sha256))return fallback;
            // Retain emulator working directory and normal NVRAM/save paths. Only input configuration is private.
            return new([..original,"-ctrlrpath",controllers,"-ctrlr","players","-cfg_directory",configs,"-joystick","-joystickprovider","xinput","-nowindow"],true,
                "Player assignments applied for this session. Game saves use your normal location.",[before,ProtectedFile.Capture(ctrl),ProtectedFile.Capture(def),ProtectedFile.Capture(cfg)]);
        }
        return fallback;
        }
        catch(Exception e)when(e is IOException or UnauthorizedAccessException or ArgumentException)
        {return fallback;}
    }
    private static bool TryResolveRetroArch(NormalManifest m,string[] original,out TrialTarget target,out string[] extraArgs,out ProtectedFile? infoPin)
    {
        target=null!;extraArgs=[];infoPin=null;
        bool isRef=m.ReferenceArguments.Length>0&&original.SequenceEqual(m.ReferenceArguments,StringComparer.OrdinalIgnoreCase);
        if(isRef&&(m.ReferenceBinaries==null||m.ReferenceBinaries.Length==0))return false;
        if(isRef&&m.RetroArch is {} refTarget){target=refTarget;extraArgs=["-f"];return true;}
        if(original.Length<3)return false;
        int lIndex=Array.FindIndex(original,a=>a.Equals("-L",StringComparison.Ordinal));
        if(lIndex<0||lIndex>=original.Length-2)return false;
        string coreArg=original[lIndex+1],contentArg=original[^1];
        var extras=new List<string>();
        for(int i=0;i<original.Length-1;i++)
        {
            if(i==lIndex||i==lIndex+1)continue;
            if(original[i] is not("-f" or "--verbose")||extras.Contains(original[i],StringComparer.Ordinal))return false;
            extras.Add(original[i]);
        }
        string? root=m.RetroArch?.Root??(Path.IsPathFullyQualified(m.Executable)?Path.GetDirectoryName(m.Executable):null);
        if(string.IsNullOrEmpty(root)||!Directory.Exists(root))return false;
        string exe=m.RetroArch?.Executable??m.Executable;
        if(!Path.IsPathFullyQualified(exe)||!File.Exists(exe))return false;
        string mainConfig=m.RetroArch?.MainConfig??Path.Combine(root,"retroarch.cfg");
        if(!Path.IsPathFullyQualified(mainConfig)||!File.Exists(mainConfig))return false;
        string corePath=Path.IsPathFullyQualified(coreArg)?Path.GetFullPath(coreArg):Path.GetFullPath(Path.Combine(root,coreArg));
        string contentPath=Path.IsPathFullyQualified(contentArg)?Path.GetFullPath(contentArg):Path.GetFullPath(Path.Combine(root,contentArg));
        if(!File.Exists(corePath)||!File.Exists(contentPath))return false;
        string coreName;
        if(m.RetroArch!=null&&corePath.Equals(m.RetroArch.CoreLibrary,StringComparison.OrdinalIgnoreCase))coreName=m.RetroArch.CoreName;
        else
        {
            string infoPath=Path.Combine(root,"info",Path.GetFileNameWithoutExtension(corePath)+".info");
            if(!File.Exists(infoPath))return false;
            infoPin=ProtectedFile.Capture(infoPath);
            var match=Regex.Match(File.ReadAllText(infoPath).Replace("\r\n","\n"),"(?m)^[ \t]*corename[ \t]*=[ \t]*\"([^\"]+)\"[ \t]*(?:#.*)?$");
            if(!match.Success||!infoPin.Unchanged())return false;
            coreName=match.Groups[1].Value;
        }
        target=new(root,exe,corePath,contentPath,mainConfig,coreName);extraArgs=extras.ToArray();return true;
    }
    private static bool TryResolveMameMachine(NormalManifest m,string[] original,out string machine)
    {
        machine="";
        if(original.Length==0)return false;
        string[] forbidden=["-ctrlr","-ctrlrpath","-cfg_directory","-inipath","-state","-playback","-record","-autoboot_script"];
        if(original.Any(a=>forbidden.Contains(a,StringComparer.OrdinalIgnoreCase)))return false;
        if(original.Length>=2&&original[^2].Equals("-rompath",StringComparison.OrdinalIgnoreCase))return false;
        string candidate=original[^1];
        if(!Regex.IsMatch(candidate,@"^[a-z0-9_]+$"))return false;
        bool isRef=m.ReferenceArguments.Length>0&&original.SequenceEqual(m.ReferenceArguments,StringComparer.OrdinalIgnoreCase);
        if(isRef)
        {
            if(m.ReferenceBinaries==null||m.ReferenceBinaries.Length==0)return false;
            machine=candidate;return true;
        }
        if(!Path.IsPathFullyQualified(m.Executable)||!File.Exists(m.Executable))return false;
        string root=Path.GetDirectoryName(Path.GetFullPath(m.Executable))!;
        int romIdx=Array.FindIndex(original,a=>a.Equals("-rompath",StringComparison.OrdinalIgnoreCase));
        string[] romDirs=romIdx>=0&&romIdx+1<original.Length-1?original[romIdx+1].Split(';',StringSplitOptions.RemoveEmptyEntries):[Path.Combine(root,"roms")];
        bool romExists=romDirs.Any(d=>{
            string dir=Path.IsPathFullyQualified(d)?d:Path.Combine(root,d);
            return File.Exists(Path.Combine(dir,candidate+".zip"))||File.Exists(Path.Combine(dir,candidate+".7z"))||Directory.Exists(Path.Combine(dir,candidate));
        });
        if(!romExists)return false;
        machine=candidate;return true;
    }
}

internal static class NormalPlay
{
    public const int KeptSessionLogs=20;
    public static string DefaultStateRoot=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ControllerLobby","Sessions");
    public static string UpdateNotice(string kind,string drift)=>(drift=="windows"?"Windows":kind=="mame"?"MAME":kind==DuckStationLaunch.Kind?"DuckStation":kind==XemuLaunch.Kind?"Xemu":kind==Pcsx2Launch.Kind?"PCSX2":"RetroArch")
        +" was updated since ControllerLobby was set up, so this game starts with its existing controls. To use the lobby again, close LaunchBox and run \"Upgrade or Repair\" in the ControllerLobby.Setup folder inside your LaunchBox folder.";
    /// <summary>The program to start and where: the plan's executable when it names one, always in the owner's emulator directory.</summary>
    internal static (string Executable,string WorkingDirectory) LaunchTarget(NormalManifest m,NormalPlan plan)=>
        (/*guard:plan-executable*/plan.Executable??m.Executable,/*guard:working-directory-is-emulator*/Path.GetDirectoryName(Path.GetFullPath(m.Executable))!);
    internal static string? LobbyNote(Qualification q)=>q.Ok?null:"Join order will not be applied to this game.";
    internal static string? LobbyNote(Pcsx2Launch.PreLobbyRead? initial)=>
        /*guard:pcsx2-note-qualified*/Pcsx2Launch.Qualify(initial?.Owner).Ok?null:"Join order will not be applied to this game.";
    /// <summary>Deletes all but the newest session folders. Runs while the exclusive lease is held, so no live session is pruned.</summary>
    internal static void PruneSessions(string runs,string current,int keep)
    {
        // Housekeeping never stops a launch: an unreadable folder is left as it is.
        try
        {
            if(!Directory.Exists(runs))return;
            foreach(var old in new DirectoryInfo(runs).GetDirectories().Where(d=>!d.FullName.Equals(current,StringComparison.OrdinalIgnoreCase)&&(d.Attributes&FileAttributes.ReparsePoint)==0)
                .OrderByDescending(d=>d.CreationTimeUtc).Skip(keep-1)) // guard:prune-sessions
                try{old.Delete(true);}catch(IOException){}catch(UnauthorizedAccessException){}
        }
        catch(IOException){}catch(UnauthorizedAccessException){}
    }
    public static async Task<bool> Run(NormalPackage package,string[] args,ITrialView view,CancellationToken cancel,Action hide,string? stateRoot=null,ITargetFactory? targetFactory=null,
        Func<IEnumerable<DuckProcess>>? duckStationProcesses=null,Func<Task<SelectionSeal?>>? selectForTest=null,Func<IPadObserver>? observerFactory=null,string? xemuPin=null,string? pcsx2Pin=null)
    {
        // duckStationProcesses stands in for the process list of whichever emulator this package launches.
        bool duck=package.Manifest.Kind==DuckStationLaunch.Kind,xemu=package.Manifest.Kind==XemuLaunch.Kind,pcsx2=package.Manifest.Kind==Pcsx2Launch.Kind;string? refusal=null;
        string busyMessage=xemu?XemuLaunch.RunningMessage:pcsx2?Pcsx2Launch.RunningMessage:DuckStationLaunch.RunningMessage;
        bool DuckRunning(int except=-1)=>duck&&DuckStationLaunch.Running(package.Manifest,duckStationProcesses,except)
            ||xemu&&/*guard:xemu-running*/XemuLaunch.Running(package.Manifest,duckStationProcesses,except)
            ||pcsx2&&/*guard:pcsx2-running*/Pcsx2Launch.Running(package.Manifest,duckStationProcesses,except);
        // PCSX2: the owner's files as read before the lobby; Start must find exactly these (contract 3.3).
        Pcsx2Launch.PreLobbyRead? pcsx2PreLobby=pcsx2&&package.UpdateDrift==null?Pcsx2Launch.PreLobby(package.Manifest):null;
        IPadObserver? observer=null,pendingObserver=null;IReadOnlyList<ControllerLobby.Xemu.ObservedPad>? baseline=null;
        // The connected set at the seal is the Xemu baseline (contract 3.2); one function for the lobby loop and the test seam.
        void SealBaseline(){try{if(observer!=null)baseline=/*guard:xemu-baseline-at-seal*/observer.Snapshot();}catch(Exception e)when(e is IOException or InvalidOperationException){baseline=null;}}
        // Prepare checks again before its first write; a DuckStation that started in between is the same busy refusal.
        string state=stateRoot??DefaultStateRoot;
        _=ProtectedFile.Capture(Path.Combine(state,".path-check"));
        Directory.CreateDirectory(Path.Combine(state,"lease"));
        using var lease=FileSessionLease.Acquire(Path.Combine(state,"lease"),recoverInterrupted:true);
        if(lease==null)throw new IOException("Another ControllerLobby game session is still running");
        string runs=Path.Combine(state,"normal-runs"),session=Path.Combine(runs,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(session);
        PruneSessions(runs,session,KeptSessionLogs);
        // Beta 1 kept its unpruned session logs under the prototype folder name; apply the same limit there.
        if(stateRoot==null)PruneSessions(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ControllerLobby","GoldenEyeFirstPlay","normal-runs"),"",KeptSessionLogs+1);
        using var log=new TrialLog(Path.Combine(session,"events.jsonl"));
        NormalPlan PrepareOrRefuse(SelectionSeal s)
        {
            if(xemu)
            {
                // Before the session file is written: a running Xemu holds the shared disk image.
                if(DuckRunning()){refusal=busyMessage;return NormalPlan.Existing(package.Manifest,args);}
                if(observer==null||baseline==null)return XemuLaunch.Existing(package.Manifest,args,session,XemuLaunch.Notice("unqualified"),null,xemuPin);
                IReadOnlyList<ControllerLobby.Xemu.ObservedPad> now;
                try{now=/*guard:xemu-reobserve*/observer.Snapshot();}
                catch(Exception e)when(/*guard:xemu-snapshot-failure-normalized*/e is IOException or InvalidOperationException or ObjectDisposedException){log.Write("xemu-observer-failed",new{e.Message});return XemuLaunch.Existing(package.Manifest,args,session,XemuLaunch.Notice("observer-failed"),null,xemuPin);}
                return XemuLaunch.Prepare(package.Manifest,args,s,baseline,now,session,null,xemuPin);
            }
            try{return NormalPlan.Prepare(package.Manifest,args,s,session,null,duckStationProcesses,pcsx2PreLobby,pcsx2Pin);}
            catch(IOException e)when(/*guard:prepare-refusal-normalized*/(duck||pcsx2)&&e.Message==busyMessage){refusal=e.Message;return NormalPlan.Existing(package.Manifest,args);}
        }
        if(lease.RecoveredMarker!=null)log.Write("recovered-interrupted-session",new{Marker=lease.RecoveredMarker});
        PinnedXInputReader? reader=null;TrialSampler? sampler=null;bool samplerClosed=false,completed=false,lifetimeEntered=false;
        NormalPlan ExistingPlan()
        {
            // Y and an abandoned lobby: a running Xemu refuses before the existing-controls session file is written.
            if(xemu&&/*guard:xemu-y-running-before-write*/DuckRunning()){refusal=busyMessage;return NormalPlan.Existing(package.Manifest,args);}
            return xemu?XemuLaunch.Existing(package.Manifest,args,session,NormalPlan.ExistingNotice,null,xemuPin):NormalPlan.Existing(package.Manifest,args);
        }
        try
        {
            SelectionSeal? seal=null;NormalPlan plan;
            // Xemu: the observer runs for the whole lobby session so SDL instance IDs stay comparable (contract 3.2).
            if(xemu&&package.UpdateDrift==null)
                try{observer=observerFactory!=null?observerFactory():new SdlPadObserver(Path.Combine(AppContext.BaseDirectory,"SDL3.dll"));}
                catch(Exception e)when(e is IOException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
                {
                    log.Write("xemu-observer-unavailable",new{e.Message});observer=null;
                    // A start that left its worker running keeps the cleanup status for the finally below.
                    if(e is PadObserverStartException p)/*guard:xemu-pending-start-kept*/pendingObserver=p.Pending;
                }
            if(package.UpdateDrift is {} drift)
            {
                // The updated emulator or XInput is not qualified: never load the reader; launch unmapped.
                plan=NormalPlan.Existing(package.Manifest,args,UpdateNotice(package.Manifest.Kind,drift));
                samplerClosed=true;
            }
            else if(selectForTest!=null)
            {
                // Test seam: a synthetic selection in place of the XInput reader and the lobby loop.
                seal=await selectForTest().ConfigureAwait(false);samplerClosed=true;
                if(seal!=null)SealBaseline();
                // Prepare's own check is the first one on the Start path and runs before any write.
                plan=seal==null?ExistingPlan():PrepareOrRefuse(seal);
            }
            else
            {
            reader=PinnedXInputReader.Open(package.Manifest.Reader);
            sampler=new TrialSampler(reader,Path.Combine(session,"controllers.jsonl"),session,"normal-play",true,compactLog:true);
            var model=new LobbyJoinPresenter(sampler.Latest,4,normalPlay:true);
            // Read-only look before the lobby, so it can say that join order will not apply. Nothing is written here.
            string? lobbyNote=duck?LobbyNote(DuckStationLaunch.Qualify(DuckStationLaunch.ReadOwner(package.Manifest))):/*guard:pcsx2-note-in-lobby*/pcsx2?LobbyNote(pcsx2PreLobby):null;
            string Say(string instruction)=>lobbyNote==null?instruction:lobbyNote+" "+instruction;
            var selected=new TaskCompletionSource<SelectionSeal>(TaskCreationOptions.RunContinuationsAsynchronously);
            var padExisting=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var selectionCancel=CancellationTokenSource.CreateLinkedTokenSource(cancel);
            int selecting=1;
            sampler.Observed+=read=>{
                if(Volatile.Read(ref selecting)==0)return;
                model.Observe(read);view.Selecting(model.Cards,Say(model.Instruction));
                if(model.CancelRequested){try{selectionCancel.Cancel();}catch(ObjectDisposedException){}return;}
                if(model.ExistingControlsRequested){padExisting.TrySetResult(true);return;}
                if(model.Failure!=null)selected.TrySetException(new IOException(model.Failure));
                if(model.Seal!=null)
                {
                    if(!sampler.TryFreeze(read,model.Seal.ConnectedMask)){selected.TrySetException(new IOException("Controllers changed at confirmation"));return;}
                    SealBaseline();
                    selected.TrySetResult(model.Seal);
                }
            };
            sampler.Start();view.Selecting([],Say(model.Instruction));
            var selectionWait=LobbySelectionAwaiter.Wait(selected.Task,()=>view.Alive&&sampler.Fresh,selectionCancel.Token);
            var existingRequests=new List<Task>{padExisting.Task};if(view.ExistingControlsRequested is {} request)existingRequests.Add(request);
            var first=await Task.WhenAny([selectionWait,..existingRequests]).ConfigureAwait(false);
            bool existing=first!=selectionWait;
            Interlocked.Exchange(ref selecting,0);
            if(existing){selectionCancel.Cancel();try{await selectionWait.ConfigureAwait(false);}catch(OperationCanceledException){}}
            else seal=await selectionWait.ConfigureAwait(false);
            plan=seal==null?ExistingPlan():PrepareOrRefuse(seal);
            }
            // One point for Start, Y and an update launch: a second DuckStation would share the first one's saves.
            if(refusal==null&&/*guard:duckstation-running-refusal*/DuckRunning())refusal=busyMessage;
            if(refusal!=null){log.Write("normal-play-refused",new{Reason=refusal});completed=true;}
            else
            {
            view.Status("Starting game",plan.Notice);
            log.Write("normal-play-plan",new{package.Manifest.Kind,Arguments=plan.Arguments,plan.Assigned,plan.Notice,seal,package.UpdateDrift});
            // Keep the notice visible before handing focus to the game; Esc still cancels. The update
            // notice carries an instruction, so it stays longer.
            await Task.Delay(package.UpdateDrift==null?1800:6000,cancel).ConfigureAwait(false);
            // The gate runs before the target is created and again before it is resumed. A DuckStation that appeared
            // during the notice, or between those two points, stops the launch; the host's own held target is excluded.
            bool lateRunning=false,lateChanged=false;int ownPid=-1;
            bool NotRunning(){if(/*guard:running-rechecked-at-gate*/DuckRunning(ownPid)){lateRunning=true;return false;}return true;}
            // A mapped Xemu launch also requires the connected set to be the sealed one at both gates.
            bool Unchanged()
            {
                if(!xemu||!plan.Assigned)return true;
                try{var now=observer!.Snapshot();if(/*guard:xemu-gate-unchanged*/now.Count==baseline!.Count&&!now.Except(baseline).Any())return true;}
                catch(Exception e)when(e is IOException or InvalidOperationException){}
                lateChanged=true;return false;
            }
            bool Gate()=>view.Alive&&!cancel.IsCancellationRequested&&package.Current()&&plan.Current()&&(seal==null||sampler==null||sampler.Fresh&&sampler.Latest.Sample.ConnectedMask==seal.ConnectedMask&&seal.PlayerIndices.All(i=>sampler.Latest.Sample.Buttons[i]==0))&&NotRunning()&&Unchanged();
            // The plan may name the launch copy; the working directory stays the owner's emulator directory either way.
            var (exe,workingDirectory)=LaunchTarget(package.Manifest,plan);
            var factory=targetFactory??NativeTargetFactory.WithCompleteCommandLine(exe,RetroArchPreparation.BuildCommandLine(exe,plan.Arguments),workingDirectory);
            log.Write("normal-play-launch",new{Executable=exe,WorkingDirectory=workingDirectory});
            lifetimeEntered=true;
            var result=await Task.Run(()=>NormalLifetime.Run(factory,Gate,()=>view.Alive&&!cancel.IsCancellationRequested,()=>{sampler?.Dispose();samplerClosed=true;hide();},target=>
                {
                    if(targetFactory!=null)return true;
                    if(target is not NativeTarget native)return false;
                    var held=native.QueryIdentity();ownPid=held.ProcessId;return held.ImagePath.Equals(exe,StringComparison.OrdinalIgnoreCase);
                })).ConfigureAwait(false);
            log.Write("normal-play-ended",result);completed=result.CleanupConfirmed;
            if(!result.Started&&lateRunning)refusal=busyMessage;
            else if(!result.Started&&lateChanged)refusal=XemuLaunch.ChangedMessage;
            }
        }
        catch(OperationCanceledException)when(!lifetimeEntered){log.Write("normal-play-cancelled",new{BeforeLaunch=true});completed=true;}
        finally
        {
            // Keep native reader loaded if sampler shutdown cannot be established.
            if(sampler!=null&&!samplerClosed){sampler.Dispose();samplerClosed=true;}else samplerClosed=true;
            if(samplerClosed)reader?.Dispose();
            observer?.Dispose();pendingObserver?.Dispose();
            // An observer worker still running means native cleanup is not confirmed: the session is not complete, so
            // the next start recovers it as interrupted. The same holds for one whose start failed.
            if(observer!=null&&!observer.ShutdownComplete||pendingObserver!=null&&!pendingObserver.ShutdownComplete){log.Write("xemu-observer-shutdown-incomplete",new{});/*guard:xemu-incomplete-shutdown-not-complete*/completed=false;}
        }
        if(completed){log.Write("normal-play-complete",new{SamplerClosed=samplerClosed});lease.Complete();}
        // A refusal is a finished session with a message for the lobby window, not an interrupted one.
        if(refusal!=null)throw new IOException(refusal);
        return completed;
    }
}
