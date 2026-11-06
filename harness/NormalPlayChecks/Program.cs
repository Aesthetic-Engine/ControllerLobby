using ControllerLobby.BigBox;
using ControllerLobby.Transaction;
using ControllerLobby.DirectIndex.Native;
using ControllerLobby.DirectIndex.HostCore;
using ControllerLobby.DirectIndex.Selection;
using System.IO;

int passed=0,failed=0;
void Need(bool v,string why){if(!v)throw new Exception(why);}
void Check(string name,Action run){try{run();Console.WriteLine("PASS "+name);passed++;}catch(Exception e){Console.WriteLine("FAIL "+name+": "+e.Message);failed++;}}
Check("cancel-before-create",()=>{var f=new Factory();var r=NormalLifetime.Run(f,()=>false,()=>true,()=>{});Need(!r.Started&&r.CleanupConfirmed&&f.Creates==0,"cancel created target");});
Check("changed-gate-after-create-terminates-suspended-owned-target",()=>{var f=new Factory();int reads=0;var r=NormalLifetime.Run(f,()=>++reads==1,()=>true,()=>{});Need(!r.Started&&r.CleanupConfirmed&&f.Target.Resumes==0&&f.Target.Stops==1&&f.Target.Disposed,"second gate bypassed cleanup");});
Check("normal-play-has-no-poll-count-limit",()=>{var f=new Factory();f.Target.Remaining=7000;var r=NormalLifetime.Run(f,()=>true,()=>true,()=>{});Need(r.Started&&r.CleanupConfirmed&&f.Target.Stops==0&&f.Target.Polls>6000,"long play stopped");});
Check("sleeping-pad-does-not-end-normal-play",()=>{var f=new Factory();f.Target.Remaining=50;int mask=15;var r=NormalLifetime.Run(f,()=>mask==15,()=>true,()=>mask=0);Need(r.Started&&r.CleanupConfirmed&&f.Target.Stops==0,"postlaunch pad topology stopped game");});
Check("frontend-loss-stops-owned-job",()=>{var f=new Factory();f.Target.Remaining=100;var r=NormalLifetime.Run(f,()=>true,()=>false,()=>{});Need(r.CleanupConfirmed&&f.Target.Stops==1&&f.Target.Disposed,"frontend loss leaked game");});
Check("held-image-mismatch-refuses-before-resume",()=>{var f=new Factory();bool threw=false;try{NormalLifetime.Run(f,()=>true,()=>true,()=>{},_=>false);}catch(IOException){threw=true;}Need(threw&&f.Target.Resumes==0&&f.Target.Stops==1&&f.Target.Disposed,"wrong held image resumed");});
Check("resume-failure-stops-owned-job",()=>{var f=new Factory();f.Target.ResumeResult=uint.MaxValue;bool threw=false;try{NormalLifetime.Run(f,()=>true,()=>true,()=>{});}catch(IOException){threw=true;}Need(threw&&f.Target.Stops==1&&f.Target.Disposed,"resume failure escaped");});
Check("after-resume-callback-failure-stops-owned-job",()=>{var f=new Factory();bool threw=false;try{NormalLifetime.Run(f,()=>true,()=>true,()=>throw new IOException("UI fault"));}catch(IOException){threw=true;}Need(threw&&f.Target.Stops==1&&f.Target.Disposed,"UI callback failure leaked game");});
Check("cleanup-uncertainty-is-not-success",()=>{var f=new Factory();f.Target.Empty=false;bool threw=false;try{NormalLifetime.Run(f,()=>true,()=>false,()=>{});}catch(IOException){threw=true;}Need(threw&&f.Target.Disposed,"unconfirmed cleanup accepted");});
foreach(string kind in new[]{"retroarch","mame"})Check(kind+"-arbitrary-game-arguments-and-notice",()=>{
 string[] original=kind=="retroarch"?["-L","cores\\other.dll","--appendconfig","owner custom.cfg","G:\\Games\\A \"quoted\" game.zip"]:["mk","-rompath","G:\\Games;D:\\Other","-state","existing"];
 var copy=original.ToArray();var m=new NormalManifest(1,kind,"unused","unused",null!,["reference"],null,[]);
 var p=NormalPlan.Prepare(m,original,new SelectionSeal(3,[1,0]),Path.GetTempPath());
 original[0]="changed after prepare";
 Need(!p.Assigned&&p.Arguments.SequenceEqual(kind=="mame"?[..copy,"-nowindow"]:copy)&&p.Notice.Contains("not applied")&&p.Inputs.Length==0,"fallback modified args or hid mapping limitation");
});
Check("mapped-retroarch-retains-save-locations-and-protects-source-config",()=>{
 string root=Path.Combine(Path.GetTempPath(),"cl-normal-"+Guid.NewGuid().ToString("N")),owner=Path.Combine(root,"owner");Directory.CreateDirectory(owner);
 string config=Path.Combine(owner,"retroarch.cfg"),exe=Path.Combine(owner,"retroarch.exe"),core=Path.Combine(owner,"core.dll"),content=Path.Combine(root,"Reference.n64");
 foreach(var p in new[]{exe,core,content})File.WriteAllText(p,"synthetic nonexecuted input");
 string settings="input_joypad_driver = \"xinput\"\ninput_max_users = \"8\"\nrgui_config_directory = \":/config\"\ninput_remapping_directory = \":/remaps\"\ncore_options_path = \"\"\nsystem_directory = \":/system\"\njoypad_autoconfig_dir = \":/autoconfig\"\nsavefile_directory = \":/saves\"\nsavestate_directory = \":/states\"\n";File.WriteAllText(config,settings);
 string[] original=["-L",core,"-f",content];var pin=ProtectedFile.Capture(core);
 var m=new NormalManifest(1,"retroarch",exe,"unused",null!,original,new(owner,exe,core,content,config,"ReferenceCore"),[],[new(core,pin.Sha256!)]);
 string session=Path.Combine(root,"session");Directory.CreateDirectory(session);var p1=NormalPlan.Prepare(m,original,new SelectionSeal(5,[2,0]),session);
 Need(p1.Assigned&&p1.Current(),"reference mapping did not prepare");
 int index=Array.IndexOf(p1.Arguments,"--config");string copied=File.ReadAllText(p1.Arguments[index+1]);
 Need(copied.Contains("savefile_directory = \":/saves\"")&&copied.Contains("savestate_directory = \":/states\"")&&File.ReadAllText(config)==settings,"save locations or owner config changed");
 int append=Array.IndexOf(p1.Arguments,"--appendconfig");string assignments=File.ReadAllText(p1.Arguments[append+1]);
 Need(assignments.Contains("input_player1_joypad_index = \"2\"")&&assignments.Contains("input_player2_joypad_index = \"0\"")&&assignments.Contains("config_save_on_exit = \"false\""),"assignment or source-write protection missing");
 File.AppendAllText(config,"# owner changed\n");Need(!p1.Current(),"late source config change ignored");
 File.AppendAllText(core,"changed core");string next=Path.Combine(root,"next");Directory.CreateDirectory(next);Need(!NormalPlan.Prepare(m,original,new SelectionSeal(5,[2,0]),next).Assigned,"changed reference core was mapped");
});
Check("mapped-mame-is-fullscreen-with-original-save-paths-and-provider",()=>{
 string root=Path.Combine(Path.GetTempPath(),"cl-mame-normal-"+Guid.NewGuid().ToString("N")),owner=Path.Combine(root,"owner"),session=Path.Combine(root,"session");Directory.CreateDirectory(Path.Combine(owner,"cfg"));Directory.CreateDirectory(session);
 string exe=Path.Combine(owner,"mame.exe"),game=Path.Combine(owner,"cfg/tmnt2.cfg");File.WriteAllText(exe,"synthetic");string originalConfig="<mameconfig version=\"10\"><system name=\"tmnt2\"><input /></system></mameconfig>";File.WriteAllText(game,originalConfig);
 string[] original=["tmnt2"];var pin=ProtectedFile.Capture(exe);var cfg=ProtectedFile.Capture(game);
 var m=new NormalManifest(1,"mame",exe,pin.Sha256!,null!,original,null,[],[new(exe,pin.Sha256!)],[cfg]);
 var plan=NormalPlan.Prepare(m,original,new SelectionSeal(15,[2,0,3,1]),session);
 Need(plan.Assigned&&plan.Current()&&plan.Arguments.Contains("-nowindow")&&plan.Arguments.Contains("xinput"),"mapped provider/fullscreen missing");
 Need(!plan.Arguments.Any(a=>new[]{"-video","-window","-noreadconfig","-nvram_directory","-state_directory"}.Contains(a))&&File.ReadAllText(game)==originalConfig,"trial renderer/save redirects or owner config writes leaked into normal play");
 var fallback=NormalPlan.Prepare(m,["tmnt2a"],new SelectionSeal(3,[1,0]),session);
 Need(!fallback.Assigned&&fallback.Arguments.SequenceEqual(new[]{"tmnt2a","-nowindow"}),"clone/fallback altered controls or omitted fullscreen");
 File.AppendAllText(game,"<!-- owner change -->");var changed=NormalPlan.Prepare(m,original,new SelectionSeal(15,[2,0,3,1]),session);
 Need(!changed.Assigned,"changed reference MAME configuration was mapped");
});
NormalManifest ThroughPackage(NormalManifest manifest){
 string Sha(string p)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)));
 string system=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"XInput1_4.dll");
 var payload=Directory.GetFiles(AppContext.BaseDirectory).Where(p=>p.EndsWith(".dll")||p.EndsWith(".exe")||p.EndsWith(".deps.json")||p.EndsWith(".runtimeconfig.json")).Select(p=>new PayloadFile(Path.GetFileName(p),Sha(p))).ToArray();
 manifest=manifest with{ExecutableSha256=Sha(manifest.Executable),Reader=new(system,Sha(system),StateExport.NamedGetState,system,Sha(system)),Payload=payload};
 string path=Path.Combine(AppContext.BaseDirectory,"normal.json");
 try{File.WriteAllText(path,System.Text.Json.JsonSerializer.Serialize(manifest));return NormalPackage.Open(AppContext.BaseDirectory,Sha(path)).Manifest;}
 finally{if(File.Exists(path))File.Delete(path);}
}
Check("general-retroarch-core-and-mame-partial-players-map-cleanly",()=>{
 string root=Path.Combine(Path.GetTempPath(),"cl-general-"+Guid.NewGuid().ToString("N")),raOwner=Path.Combine(root,"ra"),mameOwner=Path.Combine(root,"mame"),roms=Path.Combine(root,"roms"),session=Path.Combine(root,"session");
 Directory.CreateDirectory(Path.Combine(raOwner,"cores"));Directory.CreateDirectory(Path.Combine(raOwner,"info"));Directory.CreateDirectory(Path.Combine(mameOwner,"cfg"));Directory.CreateDirectory(roms);Directory.CreateDirectory(session);
 string raExe=Path.Combine(raOwner,"retroarch.exe"),raCfg=Path.Combine(raOwner,"retroarch.cfg"),snesCore=Path.Combine(raOwner,"cores","snes9x_libretro.dll"),snesInfo=Path.Combine(raOwner,"info","snes9x_libretro.info"),snesRom=Path.Combine(roms,"Mario.sfc");
 foreach(var p in new[]{raExe,snesCore,snesRom})File.WriteAllText(p,"synthetic");
 File.WriteAllText(snesInfo,"corename = \"Snes9x\"\n");
 File.WriteAllText(raCfg,"input_joypad_driver = \"xinput\"\ninput_max_users = \"8\"\nrgui_config_directory = \":/config\"\ninput_remapping_directory = \":/remaps\"\ncore_options_path = \"\"\nsystem_directory = \":/system\"\njoypad_autoconfig_dir = \":/autoconfig\"\nsavefile_directory = \":/saves\"\nsavestate_directory = \":/states\"\n");
 var raManifest=new NormalManifest(1,"retroarch",raExe,"unused",null!,[],null,[],[],null);
 var raPlan=NormalPlan.Prepare(ThroughPackage(raManifest),["-L",@"cores\snes9x_libretro.dll","-f",snesRom],new SelectionSeal(7,[2,0]),session);
 Need(raPlan.Assigned&&raPlan.Current(),"general RetroArch core did not map");
 string mameExe=Path.Combine(mameOwner,"mame.exe"),simpsonsRom=Path.Combine(roms,"simpsons.zip");File.WriteAllText(mameExe,"synthetic");File.WriteAllText(simpsonsRom,"rom");
 var mameManifest=new NormalManifest(1,"mame",mameExe,"unused",null!,[],null,[],[],null);
 var mamePlan=NormalPlan.Prepare(ThroughPackage(mameManifest),["-skip_gameinfo","-rompath",roms,"simpsons"],new SelectionSeal(7,[2,0]),session);
 Need(mamePlan.Assigned&&mamePlan.Current()&&mamePlan.Arguments.Contains("-nowindow"),"general 2P MAME machine did not map");
 int ctrlIdx=Array.IndexOf(mamePlan.Arguments,"-ctrlrpath");string ctrlXml=File.ReadAllText(Path.Combine(mamePlan.Arguments[ctrlIdx+1],"players.cfg"));
 int cfgIdx=Array.IndexOf(mamePlan.Arguments,"-cfg_directory");string defXml=File.ReadAllText(Path.Combine(mamePlan.Arguments[cfgIdx+1],"default.cfg"));
 Need(ctrlXml.Contains("XInput Player 3")&&defXml.Contains("JOYCODE_1_")&&defXml.Contains("JOYCODE_2_")&&!defXml.Contains("JOYCODE_3_")&&!defXml.Contains("JOYCODE_4_"),"unjoined MAME slots were not isolated from P3/P4 bindings");
});

Check("reference-core-changes-between-qualification-and-capture",()=>{
 string root=Path.Combine(Path.GetTempPath(),"cl-normal-"+Guid.NewGuid().ToString("N")),owner=Path.Combine(root,"owner");Directory.CreateDirectory(owner);
 string config=Path.Combine(owner,"retroarch.cfg"),exe=Path.Combine(owner,"retroarch.exe"),core=Path.Combine(owner,"core.dll"),content=Path.Combine(root,"Reference.n64");
 foreach(var p in new[]{exe,core,content})File.WriteAllText(p,"synthetic nonexecuted input");
 string settings="input_joypad_driver = \"xinput\"\ninput_max_users = \"8\"\nrgui_config_directory = \":/config\"\ninput_remapping_directory = \":/remaps\"\ncore_options_path = \"\"\nsystem_directory = \":/system\"\njoypad_autoconfig_dir = \":/autoconfig\"\nsavefile_directory = \":/saves\"\nsavestate_directory = \":/states\"\n";File.WriteAllText(config,settings);
 string[] original=["-L",core,"-f",content];var pin=ProtectedFile.Capture(core);
 var m=new NormalManifest(1,"retroarch",exe,"unused",null!,original,new(owner,exe,core,content,config,"ReferenceCore"),[],[new(core,pin.Sha256!)]);

 string session=Path.Combine(root,"race");Directory.CreateDirectory(session);var reader=new RaceReader(core);
 var plan=NormalPlan.Prepare(m,original,new SelectionSeal(5,[2,0]),session,reader);
 Need(reader.Mutated&&ProtectedFile.Capture(core).Sha256!=pin.Sha256,"race intervention did not occur");
 Need(!plan.Assigned,"qualification-to-capture race accepted changed core mapping");
});
Check("normal-package-open-accepts-both-xinput-exports",()=>{
 string root=Path.Combine(Path.GetTempPath(),"cl-pkg-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
 string exe=Path.Combine(root,"emulator.exe");File.WriteAllText(exe,"synthetic");
 string system=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"XInput1_4.dll");
 string Sha(string p)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)));
 var payload=Directory.GetFiles(AppContext.BaseDirectory).Where(p=>p.EndsWith(".dll")||p.EndsWith(".exe")||p.EndsWith(".deps.json")||p.EndsWith(".runtimeconfig.json")).Select(p=>new PayloadFile(Path.GetFileName(p),Sha(p))).ToArray();
 string path=Path.Combine(AppContext.BaseDirectory,"normal.json");
 try{
  foreach(var exp in new[]{StateExport.NamedGetState,StateExport.Ordinal100}){
   var manifest=new NormalManifest(1,"retroarch",exe,Sha(exe),new(system,Sha(system),exp,system,Sha(system)),["reference"],null,payload);
   File.WriteAllText(path,System.Text.Json.JsonSerializer.Serialize(manifest));
   Need(NormalPackage.Open(AppContext.BaseDirectory,Sha(path)).Current(),"NormalPackage.Open rejected "+exp);
  }
 }finally{if(File.Exists(path))File.Delete(path);}
});
Check("beta-refuses-excluded-manifest-kinds",()=>{
 string root=Path.Combine(Path.GetTempPath(),"cl-beta-kinds-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
 string exe=Path.Combine(root,"emulator.exe");File.WriteAllText(exe,"synthetic nonexecuted executable");
 string Sha(string p)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)));
 string system=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"XInput1_4.dll");
 var payload=Directory.GetFiles(AppContext.BaseDirectory).Where(p=>p.EndsWith(".dll")||p.EndsWith(".exe")||p.EndsWith(".deps.json")||p.EndsWith(".runtimeconfig.json")).Select(p=>new PayloadFile(Path.GetFileName(p),Sha(p))).ToArray();
 string path=Path.Combine(AppContext.BaseDirectory,"normal.json");
 try{foreach(string kind in new[]{"duckstation","pcsx2","dolphin","ppsspp","xemu","redream","kega","4do","pc","dosbox","native","MAME",""}){
  var manifest=new NormalManifest(1,kind,exe,Sha(exe),new(system,Sha(system),StateExport.NamedGetState,system,Sha(system)),[],null,payload,[],null);
  File.WriteAllText(path,System.Text.Json.JsonSerializer.Serialize(manifest));
  bool refused=false;try{NormalPackage.Open(AppContext.BaseDirectory,Sha(path));}catch(IOException){refused=true;}
  Need(refused,"excluded manifest accepted: "+kind);
 }}finally{if(File.Exists(path))File.Delete(path);}
});
Check("beta-direct-plan-refuses-before-any-config-write",()=>{
 string root=Path.Combine(Path.GetTempPath(),"cl-beta-plan-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
 string exe=Path.Combine(root,"duckstation.exe"),cfg=Path.Combine(root,"settings.ini");File.WriteAllText(exe,"synthetic");
 const string original="[Pad1]\nType = AnalogController\nUp = Keyboard/Up\n";File.WriteAllText(cfg,original);
 var m=new NormalManifest(1,"duckstation",exe,"unused",null!,[],null,[]);
 bool refused=false;try{NormalPlan.Prepare(m,["game.cue"],new SelectionSeal(3,[1,0]),root);}catch(IOException){refused=true;}
 Need(refused&&File.ReadAllText(cfg)==original,"excluded direct plan accepted or modified config");
});
Check("beta-existing-controls-refuses-excluded-kind",()=>{
 var m=new NormalManifest(1,"pc","unused","unused",null!,[],null,[]);bool refused=false;
 try{NormalPlan.Existing(m,["game.exe"]);}catch(IOException){refused=true;}
 Need(refused,"existing-controls bypass accepted native PC");
});

Console.WriteLine($"Cases={passed+failed} Passed={passed} Failed={failed}; M synthetic targets and configuration; no physical controller or real emulator");return failed==0?0:1;

sealed class Factory:ITargetFactory{public int Creates;public FakeTarget Target=new();public ISuspendedTarget CreateSuspendedInJob(List<string> f){Creates++;return Target;}}
sealed class FakeTarget:ISuspendedTarget
{
 public int Remaining=2,Polls,Stops,Resumes;public bool Disposed,Empty=true;public uint ResumeResult=1;
 public uint Resume(){Resumes++;return ResumeResult;}
 public void TerminateJob(){Stops++;Remaining=0;}
 public bool WaitRootExit(TimeSpan b){Polls++;return Remaining--<=0;}
 public int RootExitCode=>0;
 public bool WaitJobEmpty(TimeSpan b)=>Empty;
 public void Dispose(){Disposed=true;}
}

sealed class RaceReader(string core):ControllerLobby.RetroArch.IConfigReader{
 public bool Mutated;private readonly ControllerLobby.RetroArch.DiskConfigReader reader=new();
 public ControllerLobby.RetroArch.ConfigRead Read(string path){if(!Mutated){File.AppendAllText(core,"deterministic qualification-to-capture replacement");Mutated=true;}return reader.Read(path);}
}
