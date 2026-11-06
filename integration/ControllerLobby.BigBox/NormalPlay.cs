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
using ControllerLobby.Transaction;

namespace ControllerLobby.BigBox;

internal sealed record NormalManifest(int Schema,string Kind,string Executable,string ExecutableSha256,
    ReaderBinding Reader,string[] ReferenceArguments,TrialTarget? RetroArch,PayloadFile[] Payload,PayloadFile[]? ReferenceBinaries=null,ProtectedFile[]? ReferenceConfiguration=null);
internal sealed class NormalPackage
{
    public NormalManifest Manifest{get;}
    private readonly ProtectedFile[] pins;
    private NormalPackage(NormalManifest manifest,ProtectedFile[] pins){Manifest=manifest;this.pins=pins;}
    public bool Current()=>pins.All(p=>p.Unchanged());
    internal static void RequireBetaKind(string kind)
    {
        if(kind is not ("mame" or "retroarch"))throw new IOException("Beta 1 supports MAME and RetroArch only");
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
    public static NormalPackage Open(string root,string expected)
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
        var executable=ProtectedFile.Capture(m.Executable);
        if(!executable.Present||executable.Sha256!=m.ExecutableSha256||executable.Path.Equals(Environment.ProcessPath,StringComparison.OrdinalIgnoreCase))throw new IOException("Configured emulator changed");
        files.Add(executable);
        var native=ProtectedFile.Capture(system);
        if(!native.Present||native.Sha256!=m.Reader.LibrarySha256||native.Sha256!=m.Reader.FunctionModuleSha256)throw new IOException("Pinned reader changed");
        files.Add(native);
        if(files.Select(p=>p.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=files.Count)throw new IOException("Duplicate pinned path");
        var result=new NormalPackage(m,files.ToArray());if(!result.Current())throw new IOException("Package changed during inspection");return result;
    }
}

internal sealed record NormalPlan(string[] Arguments,bool Assigned,string Notice,ProtectedFile[] Inputs)
{
    public bool Current()=>Inputs.All(p=>p.Unchanged());
    public static NormalPlan Existing(NormalManifest m,string[] original)
    {
        NormalPackage.RequireBetaKind(m.Kind);
        return new(m.Kind=="mame"?[..original,"-nowindow"]:original.ToArray(),false,"Using this game's existing controls. Lobby join order is not applied.",[]);
    }
    public static NormalPlan Prepare(NormalManifest m,string[] original,SelectionSeal selection,string session,IConfigReader? configReader=null)
    {
        // Unknown games/options retain their exact vector and their ordinary save/config behavior.
        var fallback=Existing(m,original);
        if(selection.PlayerIndices.Count is <1 or >4)return fallback;
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
    public static async Task<bool> Run(NormalPackage package,string[] args,ITrialView view,CancellationToken cancel,Action hide,string? stateRoot=null,ITargetFactory? targetFactory=null)
    {
        string state=stateRoot??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ControllerLobby","GoldenEyeFirstPlay");
        _=ProtectedFile.Capture(Path.Combine(state,".path-check"));
        Directory.CreateDirectory(Path.Combine(state,"lease"));
        using var lease=FileSessionLease.Acquire(Path.Combine(state,"lease"));
        if(lease==null)throw new IOException("Another session is active or an interrupted session needs inspection");
        string session=Path.Combine(state,"normal-runs",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(session);
        using var log=new TrialLog(Path.Combine(session,"events.jsonl"));
        PinnedXInputReader? reader=null;TrialSampler? sampler=null;bool samplerClosed=false,completed=false,lifetimeEntered=false;
        try
        {
            reader=PinnedXInputReader.Open(package.Manifest.Reader);
            sampler=new TrialSampler(reader,Path.Combine(session,"controllers.jsonl"),session,"normal-play",true);
            var model=new LobbyJoinPresenter(sampler.Latest,4,normalPlay:true);
            var selected=new TaskCompletionSource<SelectionSeal>(TaskCreationOptions.RunContinuationsAsynchronously);
            int selecting=1;
            sampler.Observed+=read=>{
                if(Volatile.Read(ref selecting)==0)return;
                model.Observe(read);view.Selecting(model.Cards,model.Instruction);
                if(model.Failure!=null)selected.TrySetException(new IOException(model.Failure));
                if(model.Seal!=null){if(sampler.TryFreeze(read,model.Seal.ConnectedMask))selected.TrySetResult(model.Seal);else selected.TrySetException(new IOException("Controllers changed at confirmation"));}
            };
            sampler.Start();view.Selecting([],model.Instruction);
            SelectionSeal? seal=null;
            using var selectionCancel=CancellationTokenSource.CreateLinkedTokenSource(cancel);
            var selectionWait=LobbySelectionAwaiter.Wait(selected.Task,()=>view.Alive&&sampler.Fresh,selectionCancel.Token);
            bool existing=view.ExistingControlsRequested is {} request&&await Task.WhenAny(selectionWait,request).ConfigureAwait(false)==request;
            Interlocked.Exchange(ref selecting,0);
            if(existing){selectionCancel.Cancel();try{await selectionWait.ConfigureAwait(false);}catch(OperationCanceledException){}}
            else seal=await selectionWait.ConfigureAwait(false);
            var plan=seal==null?NormalPlan.Existing(package.Manifest,args):NormalPlan.Prepare(package.Manifest,args,seal,session);
            view.Status("Starting game",plan.Notice);
            log.Write("normal-play-plan",new{package.Manifest.Kind,Arguments=plan.Arguments,plan.Assigned,plan.Notice,seal});
            // Keep the notice visible before handing focus to the game; Esc still cancels.
            await Task.Delay(1800,cancel).ConfigureAwait(false);
            bool Gate()=>view.Alive&&!cancel.IsCancellationRequested&&package.Current()&&plan.Current()&&(seal==null||sampler.Fresh&&sampler.Latest.Sample.ConnectedMask==seal.ConnectedMask&&seal.PlayerIndices.All(i=>sampler.Latest.Sample.Buttons[i]==0));
            string exe=package.Manifest.Executable;
            var factory=targetFactory??NativeTargetFactory.WithCompleteCommandLine(exe,RetroArchPreparation.BuildCommandLine(exe,plan.Arguments),Path.GetDirectoryName(exe)!);
            lifetimeEntered=true;
            var result=await Task.Run(()=>NormalLifetime.Run(factory,Gate,()=>view.Alive&&!cancel.IsCancellationRequested,()=>{sampler.Dispose();samplerClosed=true;hide();},target=>targetFactory!=null||target is NativeTarget native&&native.QueryIdentity().ImagePath.Equals(exe,StringComparison.OrdinalIgnoreCase))).ConfigureAwait(false);
            log.Write("normal-play-ended",result);completed=result.CleanupConfirmed;
        }
        catch(OperationCanceledException)when(!lifetimeEntered){log.Write("normal-play-cancelled",new{BeforeLaunch=true});completed=true;}
        finally
        {
            // Keep native reader loaded if sampler shutdown cannot be established.
            if(sampler!=null&&!samplerClosed){sampler.Dispose();samplerClosed=true;}else samplerClosed=true;
            if(samplerClosed)reader?.Dispose();
        }
        if(completed){log.Write("normal-play-complete",new{SamplerClosed=samplerClosed});lease.Complete();}
        return completed;
    }
}
