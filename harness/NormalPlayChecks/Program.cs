using ControllerLobby.BigBox;
using ControllerLobby.DirectIndex;
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
 try{foreach(string kind in new[]{"duckstation","pcsx2","dolphin","ppsspp","redream","kega","4do","pc","dosbox","native","MAME",""}){
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

// --- v1 fixes: update drift, interrupted-session recovery, session pruning ---
(NormalPackage? package,Exception? error) OpenPackage(string exe,string exeSha,bool allow,bool corruptReader=false,bool keep=false){
 string Sha(string p)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)));
 string system=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"XInput1_4.dll");string rsha=corruptReader?new string('0',64):Sha(system);
 var payload=Directory.GetFiles(AppContext.BaseDirectory).Where(p=>p.EndsWith(".dll")||p.EndsWith(".exe")||p.EndsWith(".deps.json")||p.EndsWith(".runtimeconfig.json")).Select(p=>new PayloadFile(Path.GetFileName(p),Sha(p))).ToArray();
 var manifest=new NormalManifest(1,"mame",exe,exeSha,new(system,rsha,StateExport.NamedGetState,system,rsha),[],null,payload,[],null);
 string path=Path.Combine(AppContext.BaseDirectory,"normal.json");
 try{File.WriteAllText(path,System.Text.Json.JsonSerializer.Serialize(manifest));try{return (NormalPackage.Open(AppContext.BaseDirectory,Sha(path),allow),null);}catch(IOException e){return (null,e);}}
 finally{if(!keep&&File.Exists(path))File.Delete(path);}
}
string DriftExe(){string d=Path.Combine(Path.GetTempPath(),"cl-drift-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(d);string e=Path.Combine(d,"mame.exe");File.WriteAllText(e,"updated emulator");return e;}
Check("update-drift-refused-when-strict-and-classified-when-allowed",()=>{
 string exe=DriftExe();string stale=new string('A',64);
 var strict=OpenPackage(exe,stale,false);Need(strict.package==null&&strict.error?.Message=="Configured emulator changed","strict open accepted updated emulator");
 var loose=OpenPackage(exe,stale,true,keep:true);
 try{Need(loose.package?.UpdateDrift=="emulator"&&loose.package!.Current(),"updated emulator not classified as drift");
 File.AppendAllText(exe,"changed again");Need(!loose.package!.Current(),"further change after open not caught");}
 finally{File.Delete(Path.Combine(AppContext.BaseDirectory,"normal.json"));}
});
Check("update-drift-classifies-windows-xinput-change",()=>{
 string exe=DriftExe();string sha=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(exe)));
 var strict=OpenPackage(exe,sha,false,corruptReader:true);Need(strict.error?.Message=="Pinned reader changed","strict open accepted changed reader");
 var loose=OpenPackage(exe,sha,true,corruptReader:true);Need(loose.package?.UpdateDrift=="windows","changed XInput not classified as Windows drift");
 var clean=OpenPackage(exe,sha,true);Need(clean.package?.UpdateDrift==null,"unchanged package reported drift");
});
Check("drift-tolerance-does-not-cover-controllerlobby-files",()=>{
 string exe=DriftExe();string Sha(string p)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)));
 string system=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"XInput1_4.dll");
 var payload=Directory.GetFiles(AppContext.BaseDirectory).Where(p=>p.EndsWith(".dll")).Select(p=>new PayloadFile(Path.GetFileName(p),new string('B',64))).ToArray();
 var manifest=new NormalManifest(1,"mame",exe,new string('A',64),new(system,Sha(system),StateExport.NamedGetState,system,Sha(system)),[],null,payload,[],null);
 string path=Path.Combine(AppContext.BaseDirectory,"normal.json");bool refused=false;
 try{File.WriteAllText(path,System.Text.Json.JsonSerializer.Serialize(manifest));try{NormalPackage.Open(AppContext.BaseDirectory,Sha(path),true);}catch(IOException){refused=true;}}finally{if(File.Exists(path))File.Delete(path);}
 Need(refused,"changed ControllerLobby payload accepted under drift tolerance");
});
Check("interrupted-marker-blocks-trials-and-is-recovered-for-normal-play",()=>{
 string dir=Path.Combine(Path.GetTempPath(),"cl-lease-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
 File.WriteAllText(Path.Combine(dir,"direct-index.pending"),"left by a session that ended without completing");
 using(var trial=FileSessionLease.Acquire(dir))Need(trial==null,"trial lease ignored the interrupted marker");
 using(var normal=FileSessionLease.Acquire(dir,recoverInterrupted:true)){
  Need(normal!=null&&normal.RecoveredMarker!=null&&File.ReadAllText(normal.RecoveredMarker).StartsWith("left by"),"interrupted marker not archived and recovered");
  using(var second=FileSessionLease.Acquire(dir,recoverInterrupted:true))Need(second==null,"recovery stole a live session's lease");
  normal.Complete();}
 using(var next=FileSessionLease.Acquire(dir))Need(next!=null&&next.RecoveredMarker==null,"completed session left a marker");
});
Check("drift-launch-skips-reader-recovers-marker-and-starts-unmapped",()=>{
 string exe=DriftExe();var package=OpenPackage(exe,new string('A',64),true,keep:true).package!;
 try{
 string state=Path.Combine(Path.GetTempPath(),"cl-state-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(state,"lease"));
 File.WriteAllText(Path.Combine(state,"lease","direct-index.pending"),"power loss");
 var view=new View();var f=new Factory();
 bool done=NormalPlay.Run(package,["simpsons"],view,CancellationToken.None,()=>{},state,f).GetAwaiter().GetResult();
 Need(done&&f.Creates==1&&f.Target.Resumes==1,"drift launch did not start the game");
 Need(view.Statuses.Any(s=>s.Contains("MAME was updated")&&s.Contains("Upgrade or Repair"))&&view.SelectingCalls==0,"update notice missing or lobby shown");
 Need(!File.Exists(Path.Combine(state,"lease","direct-index.pending"))&&Directory.GetFiles(Path.Combine(state,"lease"),"interrupted-*.txt").Length==1,"marker not recovered/archived");
 var events=File.ReadAllText(Directory.GetFiles(state,"events.jsonl",SearchOption.AllDirectories).Single());
 Need(events.Contains("recovered-interrupted-session")&&events.Contains("\"-nowindow\"")&&!Directory.GetFiles(state,"controllers.jsonl",SearchOption.AllDirectories).Any(),"reader/sampler ran or plan not logged");
 }finally{File.Delete(Path.Combine(AppContext.BaseDirectory,"normal.json"));}
});
Check("session-pruning-keeps-newest-and-current",()=>{
 string runs=Path.Combine(Path.GetTempPath(),"cl-runs-"+Guid.NewGuid().ToString("N"));
 for(int i=0;i<30;i++){var d=Directory.CreateDirectory(Path.Combine(runs,"s"+i.ToString("00")));d.CreationTimeUtc=DateTime.UtcNow.AddMinutes(-100+i);}
 string current=Path.Combine(runs,"s00");NormalPlay.PruneSessions(runs,current,NormalPlay.KeptSessionLogs);
 var left=Directory.GetDirectories(runs).Select(Path.GetFileName).ToHashSet();
 Need(left.Count==NormalPlay.KeptSessionLogs&&left.Contains("s00")&&left.Contains("s29")&&!left.Contains("s10"),"pruning kept "+left.Count+" or dropped current/newest");
});

// --- DuckStation launch copy (contract r6). Synthetic folders and stand-in files; no emulator is started. ---
string DuckSha(string p)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)));
const string DuckSettings="[Main]\nConfirmPowerOff = true\n\n[ControllerPorts]\nMultitapMode = Disabled\n\n[InputSources]\nXInput = true\n\n[Pad1]\nType = AnalogController\nCross = XInput-0/A\n\n[Pad2]\nType = AnalogController\nCross = XInput-1/A\n\n[Pad3]\nType = None\n";
// A synthetic install: emulator folder (settings beside the exe, so it is its own data root) and a launch copy.
(NormalManifest m,string emu,string copy) Duck(string? settings=DuckSettings,string? copyRoot=null){
 string root=Path.Combine(Path.GetTempPath(),"cl-duck-"+Guid.NewGuid().ToString("N")),emu=Path.Combine(root,"Emulators","Duck"),copy=copyRoot??Path.Combine(emu,"ControllerLobby.BigBox","DuckStationCopy");
 Directory.CreateDirectory(emu);Directory.CreateDirectory(copy);string exe=Path.Combine(emu,DuckStationLaunch.ExeName);
 File.WriteAllText(exe,"synthetic duckstation program "+root);if(settings!=null)File.WriteAllText(Path.Combine(emu,"settings.ini"),settings);
 File.Copy(exe,Path.Combine(copy,DuckStationLaunch.ExeName),true);File.WriteAllText(Path.Combine(copy,"SDL3.dll"),"synthetic library AAAA");File.WriteAllText(Path.Combine(copy,"portable.txt"),"");
 var inventory=new[]{DuckStationLaunch.ExeName,"SDL3.dll","portable.txt"}.Select(n=>new PayloadFile(n,DuckSha(Path.Combine(copy,n)))).ToArray();
 return (new NormalManifest(1,"duckstation",exe,DuckSha(exe),null!,[],null,[],null,null,new(copy,inventory)),emu,copy);
}
string[] DuckArgs=["-fullscreen","-batch","Games\\Sony Playstation\\Synthetic (USA).cue"];
Check("duckstation-existing-controls-keep-arguments-and-owner-program",()=>{
 var (m,emu,_)=Duck();var p=NormalPlan.Existing(m,DuckArgs);var t=NormalPlay.LaunchTarget(m,p);
 Need(!p.Assigned&&p.Executable==null&&p.Arguments.SequenceEqual(DuckArgs)&&t.Executable==m.Executable&&t.WorkingDirectory==emu,"existing-controls launch changed the program, arguments or folder");
});
Check("duckstation-mapped-launch-writes-only-the-copy",()=>{
 var (m,emu,copy)=Duck();string games=Path.Combine(emu,"gamesettings");Directory.CreateDirectory(games);File.WriteAllText(Path.Combine(games,"SLUS-00001.ini"),"[GPU]\nDeinterlacingMode = Progressive\n");
 string stale=Path.Combine(copy,"session","gamesettings");Directory.CreateDirectory(stale);File.WriteAllText(Path.Combine(stale,"OLD.ini"),"[ControllerPorts]\nUseGameSettingsForController = true\n");
 string before=DuckSha(Path.Combine(emu,"settings.ini"));var ownerFiles=Directory.GetFiles(emu,"*",SearchOption.AllDirectories).Where(f=>!f.StartsWith(copy)).ToDictionary(f=>f,DuckSha);
 var p=NormalPlan.Prepare(m,DuckArgs,new SelectionSeal(3,[1,0]),Path.GetTempPath());
 Need(p.Assigned&&p.Executable==Path.Combine(copy,DuckStationLaunch.ExeName)&&p.Arguments.SequenceEqual(DuckArgs)&&p.Current(),"mapped plan not prepared: "+p.Notice);
 string session=File.ReadAllText(Path.Combine(copy,"settings.ini"));
 Need(session.Contains("[Pad1]\nType = AnalogController\nCross = XInput-1/A")&&session.Contains("[Pad2]\nType = AnalogController\nCross = XInput-0/A"),"session settings do not follow join order");
 Need(session.Contains("GameSettings = "+Path.Combine(copy,"session","gamesettings"))&&session.Contains("SaveStates = "+Path.Combine(emu,"savestates"))&&session.Contains("Directory = "+Path.Combine(emu,"memcards")),"session paths wrong");
 Need(File.Exists(Path.Combine(stale,"SLUS-00001.ini"))&&!File.Exists(Path.Combine(stale,"OLD.ini")),"snapshot not replaced with the owner's current files");
 Need(DuckSha(Path.Combine(emu,"settings.ini"))==before&&Directory.GetFiles(emu,"*",SearchOption.AllDirectories).Where(f=>!f.StartsWith(copy)).ToDictionary(f=>f,DuckSha).OrderBy(k=>k.Key).SequenceEqual(ownerFiles.OrderBy(k=>k.Key)),"an owner file changed or appeared");
 var t=NormalPlay.LaunchTarget(m,p);Need(t.Executable==p.Executable&&t.WorkingDirectory==emu,"mapped launch does not start the copy from the emulator directory");
 File.AppendAllText(Path.Combine(copy,"settings.ini"),"\n");Need(!p.Current(),"a changed session file still passes the launch gate");
});
Check("duckstation-relative-argument-resolves-the-same-on-both-paths",()=>{
 var (m,emu,_)=Duck();var mapped=NormalPlan.Prepare(m,DuckArgs,new SelectionSeal(1,[0]),Path.GetTempPath());var existing=NormalPlan.Existing(m,DuckArgs);
 var a=NormalPlay.LaunchTarget(m,mapped);var b=NormalPlay.LaunchTarget(m,existing);
 Need(mapped.Assigned&&a.Executable!=b.Executable&&a.WorkingDirectory==b.WorkingDirectory&&a.WorkingDirectory==emu&&mapped.Arguments.SequenceEqual(existing.Arguments),"relative arguments would resolve differently on the mapped path");
 string onlyHere=Path.Combine(emu,"Games","Sony Playstation");Directory.CreateDirectory(onlyHere);File.WriteAllText(Path.Combine(onlyHere,"Synthetic (USA).cue"),"x");
 Need(File.Exists(Path.Combine(a.WorkingDirectory,DuckArgs[2]))&&!File.Exists(Path.Combine(Path.GetDirectoryName(a.Executable)!,DuckArgs[2])),"fixture: the relative game path must resolve in the emulator directory only");
});
Check("duckstation-solo-leaves-port-two-inactive",()=>{
 var (m,_,copy)=Duck();var p=NormalPlan.Prepare(m,DuckArgs,new SelectionSeal(2,[1]),Path.GetTempPath());string s=File.ReadAllText(Path.Combine(copy,"settings.ini"));
 Need(p.Assigned&&s.Contains("[Pad1]\nType = AnalogController\nCross = XInput-1/A")&&s.Contains("[Pad2]\nType = None"),"solo launch left port 2 active");
});
Check("duckstation-more-players-than-ports-uses-existing-controls-and-says-so",()=>{
 var (m,_,copy)=Duck();var p=NormalPlan.Prepare(m,DuckArgs,new SelectionSeal(7,[0,1,2]),Path.GetTempPath());
 Need(!p.Assigned&&p.Executable==null&&p.Notice.Contains("2 controllers")&&p.Notice.Contains("3 players")&&p.Notice.Contains("not applied")&&!File.Exists(Path.Combine(copy,"settings.ini"))&&!Directory.Exists(Path.Combine(copy,"session")),"too many players mapped, silent, or wrote the copy: "+p.Notice);
});
foreach(var (name,reason,arrange) in new (string,string,Action<string>)[]{
 ("custom-titles","custom game titles",emu=>File.WriteAllText(Path.Combine(emu,"custom_properties.ini"),"[D:\\g.cue]\nTitle = X\n")),
 ("per-game-controller-override","its own controller settings",emu=>{Directory.CreateDirectory(Path.Combine(emu,"gamesettings"));File.WriteAllText(Path.Combine(emu,"gamesettings","A.ini"),"[controllerports]\nusegamesettingsforcontroller = true\n");}),
 ("xinput-off","can't take lobby join order",emu=>File.WriteAllText(Path.Combine(emu,"settings.ini"),DuckSettings.Replace("XInput = true","XInput = false"))),
 ("missing-settings","can't take lobby join order",emu=>{File.Delete(Path.Combine(emu,"settings.ini"));File.WriteAllText(Path.Combine(emu,"portable.txt"),"");}),
})Check("duckstation-"+name+"-uses-existing-controls-and-writes-nothing",()=>{
 var (m,emu,copy)=Duck();arrange(emu);var p=NormalPlan.Prepare(m,DuckArgs,new SelectionSeal(3,[1,0]),Path.GetTempPath());
 Need(!p.Assigned&&p.Executable==null&&p.Arguments.SequenceEqual(DuckArgs)&&p.Notice.Contains(reason)&&p.Notice.Contains("not applied"),"non-mappable configuration mapped or unexplained: "+p.Notice);
 Need(!File.Exists(Path.Combine(copy,"settings.ini"))&&!Directory.Exists(Path.Combine(copy,"session")),"the copy was written for a non-mappable configuration");
 Need(NormalPlay.LobbyNote(DuckStationLaunch.Qualify(DuckStationLaunch.ReadOwner(m)))?.Contains("will not be applied")==true,"the lobby would not say that join order is unavailable");
});
Check("duckstation-mappable-settings-add-no-lobby-note",()=>{var (m,_,_)=Duck();Need(NormalPlay.LobbyNote(DuckStationLaunch.Qualify(DuckStationLaunch.ReadOwner(m)))==null,"mappable settings produced a lobby note");});
Check("duckstation-pre-lobby-look-writes-nothing",()=>{
 var (m,emu,copy)=Duck();var before=Directory.GetFileSystemEntries(Path.GetDirectoryName(Path.GetDirectoryName(emu))!,"*",SearchOption.AllDirectories).OrderBy(x=>x).ToArray();
 _=DuckStationLaunch.Qualify(DuckStationLaunch.ReadOwner(m));
 Need(Directory.GetFileSystemEntries(Path.GetDirectoryName(Path.GetDirectoryName(emu))!,"*",SearchOption.AllDirectories).OrderBy(x=>x).SequenceEqual(before),"the read-only look created a file or folder");
});
Check("duckstation-settings-changed-after-snapshot-uses-existing-controls",()=>{
 var (m,emu,copy)=Duck();
 var p=DuckStationLaunch.Prepare(m,DuckArgs,new SelectionSeal(3,[1,0]),null,()=>File.WriteAllText(Path.Combine(emu,"custom_properties.ini"),"[D:\\g.cue]\nRegion = NTSC-J\n"));
 Need(!p.Assigned&&p.Executable==null&&p.Notice.Contains("custom game titles")&&!File.Exists(Path.Combine(copy,"settings.ini")),"a custom-properties file that appeared after selection was ignored");
 var (m2,emu2,copy2)=Duck();
 var p2=DuckStationLaunch.Prepare(m2,DuckArgs,new SelectionSeal(3,[1,0]),null,()=>File.WriteAllText(Path.Combine(emu2,"settings.ini"),DuckSettings.Replace("ConfirmPowerOff = true","ConfirmPowerOff = false")));
 Need(!p2.Assigned&&!File.Exists(Path.Combine(copy2,"settings.ini")),"settings that changed between the two reads were used");
});
Check("duckstation-documents-layout-reads-the-documents-folder",()=>{
 var (m,emu,copy)=Duck(settings:null);string docs=Path.Combine(Path.GetTempPath(),"cl-duck-docs-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(docs,"DuckStation"));
 File.WriteAllText(Path.Combine(docs,"DuckStation","settings.ini"),DuckSettings);
 var p=DuckStationLaunch.Prepare(m,DuckArgs,new SelectionSeal(1,[0]),()=>docs);
 Need(p.Assigned&&File.ReadAllText(Path.Combine(copy,"settings.ini")).Contains("SaveStates = "+Path.Combine(docs,"DuckStation","savestates")),"Documents data root not used");
 Need(!DuckStationLaunch.Prepare(m,DuckArgs,new SelectionSeal(1,[0]),()=>null).Assigned,"a missing Documents folder was mapped");
});
Check("duckstation-running-detection",()=>{
 var (m,emu,copy)=Duck();string owner=m.Executable,copyExe=Path.Combine(copy,DuckStationLaunch.ExeName);
 Need(DuckStationLaunch.Running(m,()=>[new(10,owner.ToUpperInvariant())])&&DuckStationLaunch.Running(m,()=>[new(10,copyExe)])&&DuckStationLaunch.Running(m,()=>[new(10,null)]),"a running DuckStation was not detected");
 Need(!DuckStationLaunch.Running(m,()=>[])&&!DuckStationLaunch.Running(m,()=>[new(10,@"D:\Elsewhere\"+DuckStationLaunch.ExeName)]),"an unrelated DuckStation install counted as running");
 Need(!DuckStationLaunch.Running(m,()=>[new(10,copyExe)],except:10),"the host's own held target counted as a second DuckStation");
 Need(DuckStationLaunch.Running(m,()=>[new(10,copyExe),new(11,owner)],except:10)&&DuckStationLaunch.Running(m,()=>[new(10,copyExe),new(11,null)],except:10),"excluding the owned target hid another DuckStation");
});
// Packages opened from this test host's folder, like OpenPackage above, with the copy beside it.
(NormalPackage? package,Exception? error,string copy,string exe) OpenDuck(bool allow=false,Action<string,NormalManifest>? damage=null,Func<NormalManifest,NormalManifest>? alter=null,bool staleExe=false,string? settings=DuckSettings){
 string system=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"XInput1_4.dll"),rsha=DuckSha(system),copy=Path.Combine(AppContext.BaseDirectory,"DuckStationCopy");
 if(Directory.Exists(copy))Directory.Delete(copy,true);
 var (m,emu,_)=Duck(settings,copy);
 var payload=Directory.GetFiles(AppContext.BaseDirectory).Where(p=>p.EndsWith(".dll")||p.EndsWith(".exe")||p.EndsWith(".deps.json")||p.EndsWith(".runtimeconfig.json")).Select(p=>new PayloadFile(Path.GetFileName(p),DuckSha(p))).ToArray();
 m=m with{Reader=new(system,rsha,StateExport.NamedGetState,system,rsha),Payload=payload,ReferenceBinaries=[]};if(staleExe)m=m with{ExecutableSha256=new string('A',64)};
 if(alter!=null)m=alter(m);damage?.Invoke(copy,m);
 string path=Path.Combine(AppContext.BaseDirectory,"normal.json");File.WriteAllText(path,System.Text.Json.JsonSerializer.Serialize(m));
 try{return (NormalPackage.Open(AppContext.BaseDirectory,DuckSha(path),allow),null,copy,m.Executable);}catch(IOException e){return (null,e,copy,m.Executable);}
}
void CloseDuck(){File.Delete(Path.Combine(AppContext.BaseDirectory,"normal.json"));string copy=Path.Combine(AppContext.BaseDirectory,"DuckStationCopy");if(Directory.Exists(copy))Directory.Delete(copy,true);}
Check("manifest-shapes-two-kind-manifest-without-the-block-and-duckstation-with-it",()=>{
 var json=System.Text.Json.JsonSerializer.Serialize(new NormalManifest(1,"mame","x","y",null!,[],null,[]));
 var old=json.Replace(",\"DuckStation\":null","");Need(old!=json&&!old.Contains("DuckStation"),"fixture: older manifest text still names the block");
 var options=new System.Text.Json.JsonSerializerOptions{UnmappedMemberHandling=System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow};
 Need(System.Text.Json.JsonSerializer.Deserialize<NormalManifest>(old,options)!.DuckStation==null,"a manifest written before this change no longer reads");
 try{var d=OpenDuck();Need(d.error==null&&d.package!.Manifest.DuckStation!.Inventory.Length==3&&d.package.Manifest.DuckStation.CopyRoot==d.copy&&d.package.Current(),"DuckStation manifest did not open: "+d.error?.Message);
 var again=OpenPackage(DriftExe(),new string('A',64),true);Need(again.package?.Manifest.Kind=="mame"&&again.package.Manifest.DuckStation==null,"a MAME manifest gained a DuckStation block");}
 finally{CloseDuck();}
});
foreach(var (name,message,alter,damage) in new (string,string,Func<NormalManifest,NormalManifest>?,Action<string,NormalManifest>?)[]{
 ("same-length-changed-dll","DuckStation copy changed",null,(copy,_)=>File.WriteAllText(Path.Combine(copy,"SDL3.dll"),"synthetic library BBBB")),
 ("missing-inventory-file","DuckStation copy changed",null,(copy,_)=>File.Delete(Path.Combine(copy,"SDL3.dll"))),
 ("copy-exe-differs-from-the-pinned-emulator","DuckStation copy changed",m=>{string exe=Path.Combine(m.DuckStation!.CopyRoot,DuckStationLaunch.ExeName);File.WriteAllText(exe,"another program");return m with{DuckStation=m.DuckStation with{Inventory=m.DuckStation.Inventory.Select(i=>i.Path==DuckStationLaunch.ExeName?i with{Sha256=DuckSha(exe)}:i).ToArray()}};},null),
 ("copy-root-elsewhere","Invalid DuckStation copy",m=>m with{DuckStation=m.DuckStation! with{CopyRoot=Path.Combine(Path.GetTempPath(),"elsewhere")}},null),
 ("inventory-names-a-data-file","Invalid DuckStation copy path",m=>{string data=Path.Combine(m.DuckStation!.CopyRoot,"settings.ini");File.WriteAllText(data,"present and correctly hashed, but not a program file");return m with{DuckStation=m.DuckStation with{Inventory=[..m.DuckStation.Inventory,new("settings.ini",DuckSha(data))]}};},null),
 ("inventory-path-escapes","Invalid DuckStation copy path",m=>m with{DuckStation=m.DuckStation! with{Inventory=[..m.DuckStation.Inventory,new("..\\escape.dll",new string('0',64))]}},null),
 ("inventory-without-portable-marker","Invalid DuckStation copy",m=>m with{DuckStation=m.DuckStation! with{Inventory=m.DuckStation.Inventory.Where(i=>i.Path!="portable.txt").ToArray()}},null),
 ("duckstation-kind-without-the-block","Invalid normal-play manifest",m=>m with{DuckStation=null},null),
 ("mame-kind-with-the-block","Invalid normal-play manifest",m=>m with{Kind="mame"},null),
})Check("duckstation-open-refuses-"+name,()=>{
 try{var d=OpenDuck(allow:true,damage:damage,alter:alter);Need(d.package==null&&d.error?.Message==message,"expected '"+message+"', got "+(d.error?.Message??"an opened package"));}finally{CloseDuck();}
});
Check("duckstation-copy-change-after-open-fails-the-launch-gate",()=>{
 try{var d=OpenDuck();Need(d.package!.Current(),"fresh package not current");File.WriteAllText(Path.Combine(d.copy,"SDL3.dll"),"synthetic library CCCC");Need(!d.package.Current(),"a DLL changed after open still passes the gate");}finally{CloseDuck();}
});
Check("duckstation-updated-emulator-starts-the-owner-program-with-a-notice",()=>{
 try{var d=OpenDuck(allow:true,staleExe:true,damage:(copy,_)=>File.WriteAllText(Path.Combine(copy,"SDL3.dll"),"left over from the old version"));
 Need(d.package?.UpdateDrift=="emulator","an updated DuckStation was not classified as drift: "+d.error?.Message);
 string state=Path.Combine(Path.GetTempPath(),"cl-state-"+Guid.NewGuid().ToString("N"));var view=new View();var f=new Factory();
 bool done=NormalPlay.Run(d.package!,DuckArgs,view,CancellationToken.None,()=>{},state,f,()=>[]).GetAwaiter().GetResult();
 var events=File.ReadAllText(Directory.GetFiles(state,"events.jsonl",SearchOption.AllDirectories).Single());
 Need(done&&f.Creates==1&&view.Statuses.Any(s=>s.Contains("DuckStation was updated")&&s.Contains("Upgrade or Repair"))&&view.SelectingCalls==0,"update launch did not start with the notice");
 Need(events.Contains(System.Text.Json.JsonSerializer.Serialize(d.exe))&&!events.Contains("DuckStationCopy")&&!events.Contains("-nowindow"),"update launch used the copy or changed the arguments");}
 finally{CloseDuck();}
});
Check("duckstation-already-running-starts-nothing-on-an-update-launch",()=>{
 try{var d=OpenDuck(allow:true,staleExe:true);string state=Path.Combine(Path.GetTempPath(),"cl-state-"+Guid.NewGuid().ToString("N"));var view=new View();var f=new Factory();string? refused=null;
 try{NormalPlay.Run(d.package!,DuckArgs,view,CancellationToken.None,()=>{},state,f,()=>[new(10,d.exe)]).GetAwaiter().GetResult();}catch(IOException e){refused=e.Message;}
 Need(refused==DuckStationLaunch.RunningMessage&&f.Creates==0,"a second DuckStation was started: "+refused);
 Need(!view.Statuses.Any(s=>s.StartsWith("Starting game")),"a known-busy update launch showed Starting game before refusing");}
 finally{CloseDuck();}
});
// The Y path through the real lobby loop: the request for existing controls is already pending when the lobby opens.
foreach(bool running in new[]{false,true})Check("duckstation-existing-controls-request-"+(running?"is-refused-while-running":"starts-the-owner-program"),()=>{
 try{var d=OpenDuck(settings:DuckSettings.Replace("XInput = true","XInput = false"));Need(d.package!=null&&d.package.UpdateDrift==null,"package did not open: "+d.error?.Message);
 string state=Path.Combine(Path.GetTempPath(),"cl-state-"+Guid.NewGuid().ToString("N"));var view=new View{Existing=Task.CompletedTask};var f=new Factory();string? refused=null;bool done=false;
 try{done=NormalPlay.Run(d.package!,DuckArgs,view,CancellationToken.None,()=>{},state,f,()=>running?[new(10,Path.Combine(d.copy,DuckStationLaunch.ExeName))]:[]).GetAwaiter().GetResult();}catch(IOException e){refused=e.Message;}
 Need(view.Instructions.Count>0&&view.Instructions.All(i=>i.StartsWith("Join order will not be applied to this game.")),"the lobby did not say that join order is unavailable");
 if(running)Need(refused==DuckStationLaunch.RunningMessage&&f.Creates==0&&!view.Statuses.Any(s=>s.StartsWith("Starting game")),"existing-controls request started a second DuckStation or showed Starting game first: "+refused);
 else{var events=File.ReadAllText(Directory.GetFiles(state,"events.jsonl",SearchOption.AllDirectories).Single());
  Need(done&&refused==null&&f.Creates==1&&events.Contains(System.Text.Json.JsonSerializer.Serialize(d.exe))&&!events.Contains("DuckStationCopy\\\\"+DuckStationLaunch.ExeName)&&view.Statuses.Any(s=>s.Contains("not applied")),"existing-controls request did not start the owner's program: "+refused);}}
 finally{CloseDuck();}
});

// --- Xemu launch wiring (Xemu adapter contract r3). Synthetic folders, a fake observer and fake targets. ---
const string XG0 = "0300fa675e0400008e02000014017801", XG1 = "0300ba665e0400008e02000010017801", XG2 = "03007ba65e0400008e02000014017801";
string XemuToml() => "[general]\nshow_welcome = false\n\n[input]\ngamepad_mappings = [\n    { gamepad_id = '" + XG0 + "'},\n    { gamepad_id = '" + XG1 + "'}\n    ]\n\n[input.bindings]\nport1_driver = 'usb-xbox-gamepad'\nport1 = '" + XG0 + "'\nport2_driver = 'usb-xbox-gamepad'\nport2 = '" + XG1 + "'\n\n[sys.files]\nhdd_path = 'D:\\Synthetic\\xbox_hdd.qcow2'\n";
string[] XemuArgs = ["-full-screen", "-dvd_path", @"E:\LaunchBox\Games\Microsoft Xbox\Synthetic.xiso"];
List<ControllerLobby.Xemu.ObservedPad> XThree() => [new(3, "XInput#0", XG0), new(2, "XInput#1", XG1), new(1, "XInput#2", XG2)];
(NormalPackage? package, Exception? error, string emu, string owner) OpenXemu(string? toml = null, bool staleExe = false)
{
    string system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "XInput1_4.dll"), rsha = DuckSha(system);
    string emu = Path.Combine(Path.GetTempPath(), "cl-xemu-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(emu); string exe = Path.Combine(emu, XemuLaunch.ExeName); File.WriteAllText(exe, "synthetic xemu " + emu);
    string owner = Path.Combine(emu, "xemu.toml"); File.WriteAllText(owner, toml ?? XemuToml());
    var payload = Directory.GetFiles(AppContext.BaseDirectory).Where(p => p.EndsWith(".dll") || p.EndsWith(".exe") || p.EndsWith(".deps.json") || p.EndsWith(".runtimeconfig.json")).Select(p => new PayloadFile(Path.GetFileName(p), DuckSha(p))).ToArray();
    var m = new NormalManifest(1, "xemu", exe, staleExe ? new string('A', 64) : DuckSha(exe), new(system, rsha, StateExport.NamedGetState, system, rsha), [], null, payload, [], null);
    string path = Path.Combine(AppContext.BaseDirectory, "normal.json"); File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(m));
    try { return (NormalPackage.Open(AppContext.BaseDirectory, DuckSha(path), staleExe), null, emu, owner); } catch (IOException e) { return (null, e, emu, owner); }
}
(string? refused, Factory f, string events, string state, FakeObserver obs, View view) XemuRun(NormalPackage package, FakeObserver obs, Func<IEnumerable<DuckProcess>>? processes = null, SelectionSeal? seal = null, string[]? args = null, string? pin = null, bool yRoute = false)
{
    string state = Path.Combine(Path.GetTempPath(), "cl-state-" + Guid.NewGuid().ToString("N")); var view = new View(); var f = new Factory(); string? refused = null;
    try { NormalPlay.Run(package, args ?? XemuArgs, view, CancellationToken.None, () => { }, state, f, processes ?? (() => []), () => Task.FromResult<SelectionSeal?>(yRoute ? null : seal ?? new SelectionSeal(3, [1, 0])), () => obs, pin ?? package.Manifest.ExecutableSha256).GetAwaiter().GetResult(); }
    catch (IOException e) { refused = e.Message; }
    return (refused, f, File.ReadAllText(Directory.GetFiles(state, "events.jsonl", SearchOption.AllDirectories).Single()), state, obs, view);
}
string SessionToml(string state) => File.ReadAllText(Directory.GetFiles(state, "xemu-session.toml", SearchOption.AllDirectories).Single());
Check("xemu-start-maps-ports-through-a-session-file", () =>
{
    try
    {
        var x = OpenXemu(); Need(x.package != null, "package did not open: " + x.error?.Message); string before = DuckSha(x.owner);
        var r = XemuRun(x.package!, new FakeObserver(_ => XThree()));
        Need(r.refused == null && r.f.Creates == 1 && r.f.Target.Resumes == 1, "mapped Xemu launch did not start: " + r.refused);
        string s = SessionToml(r.state);
        Need(s.Contains("port1 = '" + XG1 + "'") && s.Contains("port2 = '" + XG0 + "'") && s.Contains("port3 = ''") && s.Contains("port4 = ''") && s.Contains("auto_bind = false"), "session ports not in join order: " + s);
        Need(r.events.Contains("\"-config_path\"") && r.events.Contains("xemu-session.toml") && r.events.Contains("\"Assigned\":true"), "launch did not pass the session file");
        Need(DuckSha(x.owner) == before && r.obs.Disposed && r.obs.Calls == 4, "owner settings changed, observer not disposed, or not observed at seal, before writing and at both gates (" + r.obs.Calls + ")");
    }
    finally { CloseDuck(); }
});
Check("xemu-existing-controls-use-an-unchanged-copy", () =>
{
    try
    {
        var x = OpenXemu(); var r = XemuRun(x.package!, new FakeObserver(_ => XThree()), seal: null);
        string state = Path.Combine(Path.GetTempPath(), "cl-state-" + Guid.NewGuid().ToString("N"));
        var plan = XemuLaunch.Existing(x.package!.Manifest, XemuArgs, Directory.CreateDirectory(state).FullName, NormalPlan.ExistingNotice, pin: x.package!.Manifest.ExecutableSha256);
        Need(!plan.Assigned && plan.Arguments[0] == "-config_path" && File.ReadAllText(plan.Arguments[1]) == File.ReadAllText(x.owner) && plan.Arguments.Skip(2).SequenceEqual(XemuArgs), "existing-controls launch did not use an unchanged copy");
    }
    finally { CloseDuck(); }
});
Check("xemu-unqualified-arguments-pass-through-unchanged", () =>
{
    try
    {
        var x = OpenXemu(); string[] odd = ["-full-screen", "-snapshot", "-dvd_path", "x.xiso"];
        var r = XemuRun(x.package!, new FakeObserver(_ => XThree()), args: odd);
        Need(r.refused == null && r.f.Creates == 1 && !r.events.Contains("-config_path") && r.events.Contains("\"-snapshot\"") && r.view.Statuses.Any(s => s.Contains("not applied")), "unqualified arguments were mapped or changed");
    }
    finally { CloseDuck(); }
});
Check("xemu-unqualified-settings-pass-through-unchanged", () =>
{
    try
    {
        var x = OpenXemu(XemuToml() + "\n[misc]\na.b = 1\n"); var r = XemuRun(x.package!, new FakeObserver(_ => XThree()));
        Need(r.refused == null && r.f.Creates == 1 && !r.events.Contains("-config_path") && !r.events.Contains("\"Assigned\":true"), "settings outside the grammar were mapped");
    }
    finally { CloseDuck(); }
});
Check("xemu-set-changed-after-the-seal-uses-existing-controls", () =>
{
    try
    {
        var x = OpenXemu(); var changed = new List<ControllerLobby.Xemu.ObservedPad> { new(3, "XInput#0", XG0), new(9, "XInput#1", XG1), new(1, "XInput#2", XG2) };
        var r = XemuRun(x.package!, new FakeObserver(n => n == 1 ? XThree() : changed));
        Need(r.refused == null && r.f.Creates == 1 && !r.events.Contains("\"Assigned\":true") && r.view.Statuses.Any(s => s.Contains("the controllers changed")), "a replacement controller after the seal was mapped");
        Need(File.ReadAllText(Directory.GetFiles(r.state, "xemu-session.toml", SearchOption.AllDirectories).Single()) == File.ReadAllText(x.owner), "existing-controls copy differs from the owner's settings");
    }
    finally { CloseDuck(); }
});
Check("xemu-set-changed-at-the-gate-stops-the-launch", () =>
{
    try
    {
        var x = OpenXemu(); var r = XemuRun(x.package!, new FakeObserver(n => n <= 2 ? XThree() : XThree().Take(2).ToList()));
        Need(r.refused == XemuLaunch.ChangedMessage && r.f.Creates == 0 && r.events.Contains("normal-play-complete"), "a change before create did not stop the mapped launch: " + r.refused);
        var r2 = XemuRun(x.package!, new FakeObserver(n => n <= 3 ? XThree() : XThree().Take(2).ToList()));
        Need(r2.refused == XemuLaunch.ChangedMessage && r2.f.Creates == 1 && r2.f.Target.Resumes == 0 && r2.f.Target.Stops == 1, "a change before resume did not stop the held target: " + r2.refused);
    }
    finally { CloseDuck(); }
});
Check("xemu-running-refuses-before-any-write", () =>
{
    try
    {
        var x = OpenXemu(); var r = XemuRun(x.package!, new FakeObserver(_ => XThree()), () => [new(10, x.package!.Manifest.Executable)]);
        Need(r.refused == XemuLaunch.RunningMessage && r.f.Creates == 0 && Directory.GetFiles(r.state, "xemu-session.toml", SearchOption.AllDirectories).Length == 0 && !r.view.Statuses.Any(s => s.StartsWith("Starting game")), "a second Xemu was prepared or started: " + r.refused);
        var other = XemuRun(x.package!, new FakeObserver(_ => XThree()), () => [new(10, @"D:\Elsewhere\xemu.exe")]);
        Need(other.refused == XemuLaunch.RunningMessage && other.f.Creates == 0, "an Xemu from another install did not refuse the launch: " + other.refused);
    }
    finally { CloseDuck(); }
});
Check("xemu-y-route-uses-an-unchanged-copy", () =>
{
    try
    {
        var x = OpenXemu(); var obs = new FakeObserver(_ => XThree()); var r = XemuRun(x.package!, obs, yRoute: true);
        Need(r.refused == null && r.f.Creates == 1 && r.f.Target.Resumes == 1 && !r.events.Contains("\"Assigned\":true") && r.events.Contains("\"-config_path\""), "the Y route did not start with existing controls: " + r.refused);
        Need(SessionToml(r.state) == File.ReadAllText(x.owner) && obs.Calls == 0, "the Y copy differs from the owner's settings, or the observer was used");
    }
    finally { CloseDuck(); }
});
Check("xemu-y-route-refuses-while-running-before-any-write", () =>
{
    try
    {
        var x = OpenXemu(); var r = XemuRun(x.package!, new FakeObserver(_ => XThree()), () => [new(10, null)], yRoute: true);
        Need(r.refused == XemuLaunch.RunningMessage && r.f.Creates == 0 && Directory.GetFiles(r.state, "xemu-session.toml", SearchOption.AllDirectories).Length == 0, "Y wrote or started a session while Xemu was running: " + r.refused);
    }
    finally { CloseDuck(); }
});
// An Xemu that appears after the n-th running check: before the notice (1), before create (2), before resume (3).
foreach (bool y in new[] { false, true })
    foreach (int after in new[] { 1, 2, 3 })
        Check("xemu-arrival-" + (y ? "y" : "start") + "-after-check-" + after, () =>
        {
            try
            {
                var x = OpenXemu(); int calls = 0;
                var r = XemuRun(x.package!, new FakeObserver(_ => XThree()), () => ++calls > after ? [new(10, null)] : [], yRoute: y);
                Need(r.refused == XemuLaunch.RunningMessage && r.f.Target.Resumes == 0, "a late Xemu did not stop the launch: " + r.refused + " after " + calls + " checks");
                Need(after < 3 ? r.f.Creates == 0 : r.f.Creates == 1 && r.f.Target.Stops == 1, "wrong gate: creates " + r.f.Creates + ", stops " + r.f.Target.Stops + ", checks " + calls);
                Need(r.events.Contains("normal-play-complete"), "the refusal did not complete the session");
            }
            finally { CloseDuck(); }
        });
Check("xemu-snapshot-failure-before-write-uses-existing-controls", () =>
{
    try
    {
        var x = OpenXemu(); var r = XemuRun(x.package!, new FakeObserver(n => n == 1 ? XThree() : throw new IOException("SDL snapshot failed")));
        Need(r.refused == null && r.f.Creates == 1 && !r.events.Contains("\"Assigned\":true") && r.events.Contains("xemu-observer-failed") && r.view.Statuses.Any(s => s.Contains("could not be read")), "a snapshot failure was not normalized to existing controls: " + r.refused);
    }
    finally { CloseDuck(); }
});
Check("xemu-settings-replaced-while-read", () =>
{
    var x = OpenXemu(); var m = x.package!.Manifest; CloseDuck(); string pin = m.ExecutableSha256;
    string session = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "cl-xemu-race-" + Guid.NewGuid().ToString("N"))).FullName;
    void Replace() => File.WriteAllText(x.owner, XemuToml().Replace("show_welcome = false", "show_welcome = true"));
    var p = XemuLaunch.Prepare(m, XemuArgs, new SelectionSeal(3, [1, 0]), XThree(), XThree(), session, null, pin, Replace);
    Need(!p.Assigned && p.Notice.Contains("settings changed"), "a session was mapped from settings replaced while they were read: " + p.Notice);
    File.WriteAllText(x.owner, XemuToml());
    var e = XemuLaunch.Existing(m, XemuArgs, session, NormalPlan.ExistingNotice, null, pin, Replace);
    Need(!e.Assigned && e.Arguments.SequenceEqual(XemuArgs), "an existing-controls copy was made from settings replaced while they were read");
});
Check("xemu-settings-path-resolved-once", () =>
{
    var x = OpenXemu(); var m = x.package!.Manifest; CloseDuck(); string pin = m.ExecutableSha256;
    string appData = Path.Combine(Path.GetTempPath(), "cl-xemu-appdata-" + Guid.NewGuid().ToString("N")), roaming = Path.Combine(appData, "xemu", "xemu", "xemu.toml");
    Directory.CreateDirectory(Path.GetDirectoryName(roaming)!); File.Move(x.owner, roaming);
    string session = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "cl-xemu-race-" + Guid.NewGuid().ToString("N"))).FullName;
    var p = XemuLaunch.Prepare(m, XemuArgs, new SelectionSeal(3, [1, 0]), XThree(), XThree(), session, appData, pin, () => File.WriteAllText(x.owner, "[general]\nshow_welcome = true\n"));
    Need(p.Assigned && p.Inputs.Any(i => i.Path == Path.GetFullPath(roaming)) && !p.Inputs.Any(i => i.Path == Path.GetFullPath(x.owner)), "the pin is not the file the session was built from");
    Need(File.ReadAllText(p.Arguments[1]).Contains("hdd_path"), "the session was not built from the file that was read");
});
// SDL observer lifecycle through the functions seam. No SDL library is loaded.
Check("xemu-observer-lifecycle", () =>
{
    var ok = new FakeSdl(); var o = new SdlPadObserver(() => ok, TimeSpan.FromSeconds(5));
    Need(o.Snapshot().Count == 1, "snapshot through the seam"); o.Dispose();
    Need(o.ShutdownComplete && ok.Quits == 1 && ok.Released == 1, "normal shutdown did not quit once and release once");
    Exception? Throws(Action a) { try { a(); return null; } catch (Exception e) { return e; } }
    Need(Throws(() => new SdlPadObserver(() => throw new DllNotFoundException("no SDL"))) is DllNotFoundException, "a load failure was not reported");
    var noInit = new FakeSdl { InitResult = false };
    Need(Throws(() => new SdlPadObserver(() => noInit)) is IOException && noInit.Quits == 0 && noInit.Released == 1, "an init failure quit SDL or leaked the library");
    var badSnap = new FakeSdl { SnapshotFails = true }; var o2 = new SdlPadObserver(() => badSnap);
    Need(Throws(() => o2.Snapshot()) is IOException, "a snapshot failure was not normalized to IOException"); o2.Dispose();
    Need(badSnap.Quits == 1 && badSnap.Released == 1, "after a snapshot failure, shutdown did not quit and release");
    var hang = new FakeSdl(); hang.Block.Reset(); var o3 = new SdlPadObserver(() => hang, TimeSpan.FromMilliseconds(200)); o3.Dispose();
    Need(!o3.ShutdownComplete && hang.Released == 0, "a timed-out shutdown was reported complete, or the library was released under a live worker");
    hang.Block.Set(); for (int i = 0; i < 50 && hang.Released == 0; i++) Thread.Sleep(20);
    Need(hang.Released == 1, "the worker did not release the library once it finished");
    Need(Throws(() => new NativeSdl(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "XInput1_4.dll"))) is EntryPointNotFoundException, "a library without SDL exports was accepted");
});
Check("xemu-incomplete-observer-shutdown-is-not-a-completed-session", () =>
{
    try
    {
        var x = OpenXemu(); var obs = new FakeObserver(_ => XThree()) { Incomplete = true }; var r = XemuRun(x.package!, obs);
        Need(r.f.Creates == 1 && r.events.Contains("xemu-observer-shutdown-incomplete") && !r.events.Contains("normal-play-complete"), "an incomplete observer shutdown was reported as a completed session");
        var lease = FileSessionLease.Acquire(Path.Combine(r.state, "lease"), recoverInterrupted: true);
        Need(lease != null && lease.RecoveredMarker != null, "the next start did not recover the session as interrupted"); lease!.Dispose();
    }
    finally { CloseDuck(); }
});
Check("xemu-observer-bounded-waits", () =>
{
    Exception? Throws(Action a) { try { a(); return null; } catch (Exception e) { return e; } }
    var slowInit = new FakeSdl(); slowInit.InitGate.Reset();
    var t0 = DateTime.UtcNow; var e1 = Throws(() => new SdlPadObserver(() => slowInit, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(300)));
    Need(e1 is IOException && DateTime.UtcNow - t0 < TimeSpan.FromSeconds(4) && slowInit.Released == 0, "a blocked Init was not bounded, or the library was released under the worker");
    slowInit.InitGate.Set(); for (int i = 0; i < 100 && slowInit.Released == 0; i++) Thread.Sleep(20);
    Need(slowInit.Released == 1 && slowInit.Quits == 1, "after a late start the worker did not quit and release");
    var slowSnap = new FakeSdl(); var o = new SdlPadObserver(() => slowSnap, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(300)); slowSnap.UpdateGate.Reset();
    t0 = DateTime.UtcNow; Need(Throws(() => o.Snapshot()) is IOException && DateTime.UtcNow - t0 < TimeSpan.FromSeconds(4), "a blocked Update was not bounded");
    o.Dispose(); Need(!o.ShutdownComplete && slowSnap.Released == 0, "shutdown under a blocked Update was reported complete or released early");
    slowSnap.UpdateGate.Set(); for (int i = 0; i < 100 && slowSnap.Released == 0; i++) Thread.Sleep(20);
    Need(slowSnap.Released == 1, "the worker did not release after the blocked Update cleared");
});
Check("xemu-native-sdl-frees-on-export-failure", () =>
{
    var freed = new List<IntPtr>(); var handle = new IntPtr(0x1234); bool threw = false;
    try { new NativeSdl("x", _ => handle, (_, name) => name == "SDL_GetJoysticks" ? throw new EntryPointNotFoundException(name) : new IntPtr(0x1000), freed.Add); } // never invoked
    catch (EntryPointNotFoundException) { threw = true; }
    Need(threw && freed.Count == 1 && freed[0] == handle, "an export failure did not free the loaded library exactly once (" + freed.Count + ")");
});
Check("xemu-observer-unavailable-uses-existing-controls", () =>
{
    try
    {
        var x = OpenXemu(); string state = Path.Combine(Path.GetTempPath(), "cl-state-" + Guid.NewGuid().ToString("N")); var f = new Factory(); string? refused = null;
        try { NormalPlay.Run(x.package!, XemuArgs, new View(), CancellationToken.None, () => { }, state, f, () => [], () => Task.FromResult<SelectionSeal?>(new SelectionSeal(3, [1, 0])), () => throw new DllNotFoundException("no SDL")).GetAwaiter().GetResult(); }
        catch (IOException e) { refused = e.Message; }
        string events = File.ReadAllText(Directory.GetFiles(state, "events.jsonl", SearchOption.AllDirectories).Single());
        Need(refused == null && f.Creates == 1 && events.Contains("xemu-observer-unavailable") && !events.Contains("\"Assigned\":true"), "a missing observer did not fall back to existing controls: " + refused);
    }
    finally { CloseDuck(); }
});
Check("xemu-blocked-observer-start-is-not-a-completed-session", () =>
{
    // The real observer blocked in Init past its start timeout. The fallback launch still runs, but the
    // worker holds native resources, so the session must not complete and the next start recovers it as interrupted.
    var slowInit = new FakeSdl(); slowInit.InitGate.Reset();
    try
    {
        var x = OpenXemu(); string state = Path.Combine(Path.GetTempPath(), "cl-state-" + Guid.NewGuid().ToString("N")); var f = new Factory(); string? refused = null;
        try { NormalPlay.Run(x.package!, XemuArgs, new View(), CancellationToken.None, () => { }, state, f, () => [], () => Task.FromResult<SelectionSeal?>(new SelectionSeal(3, [1, 0])), () => new SdlPadObserver(() => slowInit, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(300))).GetAwaiter().GetResult(); }
        catch (IOException e) { refused = e.Message; }
        string events = File.ReadAllText(Directory.GetFiles(state, "events.jsonl", SearchOption.AllDirectories).Single());
        Need(refused == null && f.Creates == 1 && events.Contains("xemu-observer-unavailable") && !events.Contains("\"Assigned\":true"), "the blocked start did not fall back to existing controls: " + refused);
        Need(events.Contains("xemu-observer-shutdown-incomplete") && !events.Contains("normal-play-complete"), "a session whose observer was still starting was reported complete");
        Need(slowInit.Released == 0, "the library was released under the blocked worker");
        var lease = FileSessionLease.Acquire(Path.Combine(state, "lease"), recoverInterrupted: true);
        Need(lease != null && lease.RecoveredMarker != null, "the next start did not recover the session as interrupted"); lease!.Dispose();
    }
    finally { slowInit.InitGate.Set(); CloseDuck(); }
    for (int i = 0; i < 100 && slowInit.Released == 0; i++) Thread.Sleep(20);
    Need(slowInit.Released == 1 && slowInit.Quits == 1, "after the late start the worker did not quit once and release once (" + slowInit.Quits + "/" + slowInit.Released + ")");
});
Check("xemu-failed-start-hands-over-a-stuck-cleanup", () =>
{
    // A start that fails (Init false) with a cleanup that does not finish in time hands the observer over as pending;
    // one whose cleanup finishes throws the plain failure (nothing left to track).
    var stuck = new FakeSdl { InitResult = false }; stuck.ReleaseGate.Reset(); Exception? e1 = null;
    try { new SdlPadObserver(() => stuck, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(300)); } catch (Exception e) { e1 = e; }
    Need(e1 is PadObserverStartException p && !p.Pending.ShutdownComplete && stuck.Released == 0, "a failed start with a stuck cleanup was not handed over as pending: " + e1?.GetType().Name);
    stuck.ReleaseGate.Set(); for (int i = 0; i < 100 && stuck.Released == 0; i++) Thread.Sleep(20);
    ((PadObserverStartException)e1!).Pending.Dispose();
    Need(stuck.Released == 1 && stuck.Quits == 0 && ((PadObserverStartException)e1).Pending.ShutdownComplete, "the stuck cleanup did not finish once, without Quit, and report complete");
    var clean = new FakeSdl { InitResult = false }; Exception? e2 = null;
    try { new SdlPadObserver(() => clean, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500)); } catch (Exception e) { e2 = e; }
    Need(e2 is IOException && e2 is not PadObserverStartException && clean.Released == 1, "a failed start with a finished cleanup was not the plain failure: " + e2?.GetType().Name);
});
Check("xemu-update-drift-passes-through-without-an-observer", () =>
{
    try
    {
        var x = OpenXemu(staleExe: true); Need(x.package?.UpdateDrift == "emulator", "drift not classified");
        var obs = new FakeObserver(_ => XThree()); var r = XemuRun(x.package!, obs);
        Need(r.refused == null && r.f.Creates == 1 && obs.Calls == 0 && !r.events.Contains("-config_path") && r.view.Statuses.Any(s => s.Contains("Xemu was updated")), "update launch used the observer or a session file");
    }
    finally { CloseDuck(); }
});
Check("xemu-running-detection-excludes-only-the-owned-target", () =>
{
    var x = OpenXemu(); var m = x.package!.Manifest; CloseDuck(); string exe = m.Executable;
    Need(!XemuLaunch.Running(m, () => [new(10, exe)], except: 10), "the host's own held Xemu counted as running");
    Need(XemuLaunch.Running(m, () => [new(10, exe), new(11, exe)], except: 10) && XemuLaunch.Running(m, () => [new(10, exe), new(11, null)], except: 10), "excluding the owned target hid another Xemu");
    Need(XemuLaunch.Running(m, () => [new(10, exe.ToUpperInvariant())]) && XemuLaunch.Running(m, () => [new(10, @"D:\Elsewhere\xemu.exe")]), "owner image not matched, or another install not counted");
});
Check("xemu-other-build-passes-through-unchanged", () =>
{
    try
    {
        var x = OpenXemu(); string before = DuckSha(x.owner); var obs = new FakeObserver(_ => XThree());
        var r = XemuRun(x.package!, obs, pin: XemuLaunch.PinnedSha256);
        Need(r.refused == null && r.f.Creates == 1 && !r.events.Contains("-config_path") && !r.events.Contains("\"Assigned\":true") && r.events.Contains("\"-dvd_path\"")
            && r.view.Statuses.Any(s => s.Contains(XemuLaunch.UnsupportedNotice)) && DuckSha(x.owner) == before, "an Xemu build other than the pinned one was mapped or its arguments changed: " + r.refused);
        Need(Directory.GetFiles(r.state, "xemu-session.toml", SearchOption.AllDirectories).Length == 0, "a session file was written for an unsupported build");
    }
    finally { CloseDuck(); }
});
Check("xemu-pin-is-0.8.135", () => Need(XemuLaunch.PinnedSha256 == "C2E70F7B8D7ACF3847AF9C0415D20BE98D801A32D12E0534047D3D3DAFE46C34", "pin is not the probed 0.8.135 exe"));
Check("xemu-settings-path-rule", () =>
{
    var x = OpenXemu(); var m = x.package!.Manifest; CloseDuck();
    Need(XemuLaunch.SettingsPath(m) == x.owner, "portable xemu.toml not used");
    File.Delete(x.owner); Need(XemuLaunch.SettingsPath(m, @"D:\Synthetic\AppData") == @"D:\Synthetic\AppData\xemu\xemu\xemu.toml", "AppData rule not used");
});

// --- PCSX2 launch copy (PCSX2 adapter contract r3). Synthetic folders; no emulator is started. ---
const string P2Settings = "[UI]\nSettingsVersion = 1\n\n[Folders]\nBios = bios\nMemoryCards = memcards\nSavestates = sstates\n\n[InputSources]\nKeyboard = true\nSDL = true\nXInput = false\n\n[Hotkeys]\nScreenshot = Keyboard/F8\n\n[Pad]\nMultitapPort1 = false\nMultitapPort2 = false\n\n[Pad1]\nType = DualShock2\nDeadzone = 0\nCross = Keyboard/K\n\n[Pad2]\nType = DualShock2\n\n[USB1]\nType = None\n";
string[] P2Args = ["-fullscreen", "-nogui", @"E:\LaunchBox\Games\Sony Playstation 2\Synthetic.iso"];
// A synthetic portable install and a launch copy holding every release name (stand-in contents) plus portable.ini.
(NormalManifest m, string emu, string copy) P2(string? settings = P2Settings, string? copyRoot = null)
{
    string root = Path.Combine(Path.GetTempPath(), "cl-pcsx2-" + Guid.NewGuid().ToString("N")), emu = Path.Combine(root, "Emulators", "PCSX2"), copy = copyRoot ?? Path.Combine(emu, "ControllerLobby.BigBox", Pcsx2Launch.CopyFolder);
    Directory.CreateDirectory(Path.Combine(emu, "inis")); string exe = Path.Combine(emu, Pcsx2Launch.ExeName);
    File.WriteAllText(exe, "synthetic pcsx2 program " + root); File.WriteAllText(Path.Combine(emu, "portable.ini"), "");
    if (settings != null) File.WriteAllText(Path.Combine(emu, "inis", "PCSX2.ini"), settings);
    var inventory = new List<PayloadFile>();
    foreach (string name in ControllerLobby.Pcsx2.Pcsx2Inventory.Files.Keys.Append("portable.ini"))
    {
        string path = Path.Combine(copy, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (name == Pcsx2Launch.ExeName) File.Copy(exe, path, true); else File.WriteAllText(path, name == "portable.ini" ? "" : "stand-in " + name);
        inventory.Add(new(name, DuckSha(path)));
    }
    return (new NormalManifest(1, "pcsx2", exe, DuckSha(exe), null!, [], null, [], null, null, null, new(copy, inventory.ToArray())), emu, copy);
}
NormalPlan P2Plan(NormalManifest m, int[] players, Pcsx2Launch.PreLobbyRead? pre = null, Func<IEnumerable<DuckProcess>>? processes = null, string[]? args = null, bool realPin = false) =>
    NormalPlan.Prepare(m, args ?? P2Args, new SelectionSeal(3, players), Path.GetTempPath(), null, processes ?? (() => []), pre, realPin ? null : m.ExecutableSha256);
Check("pcsx2-mapped-launch-writes-only-the-copy", () =>
{
    var (m, emu, copy) = P2(); string games = Path.Combine(emu, "gamesettings"); Directory.CreateDirectory(games);
    File.WriteAllText(Path.Combine(games, "SLUS-20062_ABCD1234.ini"), "[EmuCore/Gamefixes]\nEETimingHack = true\n");
    var before = Directory.GetFiles(emu, "*", SearchOption.AllDirectories).Where(f => !f.StartsWith(copy)).ToDictionary(f => f, DuckSha);
    var p = P2Plan(m, [2, 0], Pcsx2Launch.PreLobby(m));
    Need(p.Assigned && p.Executable == Path.Combine(copy, Pcsx2Launch.ExeName) && p.Arguments.SequenceEqual(P2Args) && p.Current(), "mapped plan not prepared: " + p.Notice);
    var t = NormalPlay.LaunchTarget(m, p); Need(t.WorkingDirectory == emu, "working directory is not the owner's PCSX2 folder");
    string s = File.ReadAllText(Path.Combine(copy, "inis", "PCSX2.ini"));
    Need(s.Contains("Cross = XInput-2/A") && s.Contains("[Pad2]\nType = DualShock2\nUp = XInput-0/DPadUp\n") && s.Contains("[Pad1]\nType = DualShock2\nDeadzone = 0\nUp = XInput-2/DPadUp\n") &&s.Contains("XInput = true") && !s.Contains("Keyboard/K"), "session settings not mapped: " + s);
    Need(s.Contains("MemoryCards = " + Path.Combine(emu, "memcards")) && s.Contains("GameSettings = " + Path.Combine(copy, "session", "gamesettings")), "folders not absolute or not private");
    Need(File.ReadAllText(Path.Combine(copy, "session", "gamesettings", "SLUS-20062_ABCD1234.ini")) == File.ReadAllText(Path.Combine(games, "SLUS-20062_ABCD1234.ini")), "per-game file not copied byte for byte");
    var after = Directory.GetFiles(emu, "*", SearchOption.AllDirectories).Where(f => !f.StartsWith(copy)).ToDictionary(f => f, DuckSha);
    Need(after.Count == before.Count && after.All(kv => before.TryGetValue(kv.Key, out var h) && h == kv.Value), "a file outside the copy was written");
});
Check("pcsx2-fresh-session-leaves-no-stale-file", () =>
{
    var (m, emu, copy) = P2(); string games = Path.Combine(emu, "gamesettings"); Directory.CreateDirectory(games);
    File.WriteAllText(Path.Combine(games, "OLD.ini"), "[EmuCore]\nA = 1\n");
    Need(P2Plan(m, [0], Pcsx2Launch.PreLobby(m)).Assigned, "first launch not mapped");
    File.Delete(Path.Combine(games, "OLD.ini")); File.WriteAllText(Path.Combine(copy, "session", "debuggerlayouts", "stale.json"), "{}");
    Need(P2Plan(m, [0], Pcsx2Launch.PreLobby(m)).Assigned, "second launch not mapped");
    Need(!Directory.EnumerateFiles(Path.Combine(copy, "session"), "*", SearchOption.AllDirectories).Any(), "a file from the earlier launch remained in the session folder");
});
Check("pcsx2-changed-since-the-lobby-uses-existing-controls", () =>
{
    var (m, emu, _) = P2(); var pre = Pcsx2Launch.PreLobby(m);
    File.AppendAllText(Path.Combine(emu, "inis", "PCSX2.ini"), "\n[Extra]\nA = 1\n");
    var p = P2Plan(m, [0], pre); Need(!p.Assigned && p.Executable == null && p.Arguments.SequenceEqual(P2Args), "a settings change between the lobby and Start was mapped");
});
Check("pcsx2-failed-pre-lobby-read-uses-existing-controls", () =>
{
    // The pre-lobby read failed (no settings file yet); a readable file at Start must not be mapped.
    var (m, emu, _) = P2(settings: null); var pre = Pcsx2Launch.PreLobby(m);
    Need(pre.Owner == null, "the stand-in pre-lobby read did not fail");
    File.WriteAllText(Path.Combine(emu, "inis", "PCSX2.ini"), P2Settings);
    var p = P2Plan(m, [0], pre); Need(!p.Assigned && p.Executable == null && p.Arguments.SequenceEqual(P2Args), "a failed pre-lobby read was treated as no baseline and mapped");
    Need(P2Plan(m, [0], Pcsx2Launch.PreLobby(m)).Assigned, "control: a readable pre-lobby read did not map");
});
Check("pcsx2-owner-replaced-while-read-is-not-consumed", () =>
{
    var (m, emu, _) = P2(); string ini = Path.Combine(emu, "inis", "PCSX2.ini");
    Need(Pcsx2Launch.ReadOwner(m) is { } o && o.SettingsPin.Sha256 == DuckSha(ini), "control: the pin does not hash to the file read");
    Need(Pcsx2Launch.ReadOwner(m, () => File.WriteAllText(ini, P2Settings + "\n[Extra]\nA = 1\n")) == null, "a file replaced between its read and its pin was consumed");
});
Check("pcsx2-owner-replaced-after-requalify-is-not-current", () =>
{
    // A replacement after the last owner read, before the plan, must not become the plan's owner pin.
    var (m, emu, copy) = P2(); string ini = Path.Combine(emu, "inis", "PCSX2.ini");
    var p = Pcsx2Launch.Prepare(m, P2Args, new SelectionSeal(1, [0]), null, null, m.ExecutableSha256, () => File.WriteAllText(ini, P2Settings.Replace("Deadzone = 0", "Deadzone = 0.25")));
    Need(p.Assigned && File.ReadAllText(Path.Combine(copy, "inis", "PCSX2.ini")).Contains("Deadzone = 0\n"), "control: the plan was not built from the consumed bytes");
    Need(!p.Current(), "the plan accepted an owner file replaced after it was consumed");
});
Check("pcsx2-changed-after-the-snapshot-uses-existing-controls", () =>
{
    var (m, emu, _) = P2(); string games = Path.Combine(emu, "gamesettings"); Directory.CreateDirectory(games);
    var p = Pcsx2Launch.Prepare(m, P2Args, new SelectionSeal(1, [0]), null, () => File.WriteAllText(Path.Combine(games, "NEW.ini"), "[Pad1]\nType = DualShock2\n"), m.ExecutableSha256);
    Need(!p.Assigned && p.Executable == null, "a per-game file added during the snapshot was not re-qualified");
});
foreach (var (name, arrange, reason) in new (string, Action<string>, string)[] {
    ("input-profile", emu => File.AppendAllText(Path.Combine(emu, "inis", "PCSX2.ini"), "\n[EmuCore]\nInputProfileName = Mine\n"), "own controller settings"),
    ("per-game-pad", emu => { Directory.CreateDirectory(Path.Combine(emu, "gamesettings")); File.WriteAllText(Path.Combine(emu, "gamesettings", "G.ini"), "[Pad1]\nType = DualShock2\n"); }, "own controller settings"),
    ("settings-missing", emu => File.Delete(Path.Combine(emu, "inis", "PCSX2.ini")), "can't take lobby join order"),
    ("per-game-subfolder", emu => Directory.CreateDirectory(Path.Combine(emu, "gamesettings", "sub")), "can't take lobby join order"),
})
    Check("pcsx2-not-mappable-" + name, () =>
    {
        var (m, emu, _) = P2(); arrange(emu); var p = P2Plan(m, [0], Pcsx2Launch.PreLobby(m));
        Need(!p.Assigned && p.Executable == null && p.Arguments.SequenceEqual(P2Args) && p.Notice.Contains(reason) && p.Notice.Contains("not applied"), "non-mappable configuration mapped or unexplained: " + p.Notice);
    });
Check("pcsx2-too-many-players-and-arguments", () =>
{
    var (m, _, _) = P2();
    // Five cannot reach the adapter: NormalPlan.Prepare takes existing controls for more than four (NormalPlay.cs 103).
    var five = P2Plan(m, [0, 1, 2, 3, 0]); Need(!five.Assigned && five.Notice.Contains("not applied"), "five players mapped: " + five.Notice);
    var odd = P2Plan(m, [0], args: ["-fullscreen", "-nogui", "-statefile", "x.p2s"]); Need(!odd.Assigned && odd.Arguments.SequenceEqual(["-fullscreen", "-nogui", "-statefile", "x.p2s"]), "unqualified arguments mapped or changed");
});
Check("pcsx2-tabbed-save-folder-uses-existing-controls-before-writes", () =>
{
    foreach (string setting in new[] { "MemoryCards = memcards\t", "GameSettings = \t" })
    {
        var (m, emu, copy) = P2(P2Settings.Replace("MemoryCards = memcards", setting));
        byte[] before = File.ReadAllBytes(Path.Combine(emu, "inis", "PCSX2.ini"));
        var p = P2Plan(m, [0], Pcsx2Launch.PreLobby(m));
        Need(!p.Assigned && p.Executable == null && p.Arguments.SequenceEqual(P2Args), "a tabbed save-folder setting was mapped: " + setting);
        Need(!Directory.Exists(Path.Combine(copy, "session")) && !Directory.Exists(Path.Combine(copy, "inis")), "a refused tabbed setting wrote session files");
        Need(File.ReadAllBytes(Path.Combine(emu, "inis", "PCSX2.ini")).AsSpan().SequenceEqual(before), "the owner's settings changed");
    }
});
// Exercise the actual lobby view; the pending bypass request avoids taking selection from real controller input.
Check("pcsx2-pre-lobby-note-keeps-the-initial-read", () =>
{
    var (m, emu, _) = P2(P2Settings.Replace("MemoryCards = memcards", "MemoryCards = memcards\t"));
    var initial = Pcsx2Launch.PreLobby(m); string ini = Path.Combine(emu, "inis", "PCSX2.ini");
    File.WriteAllText(ini, P2Settings);
    Need(Pcsx2Launch.Qualify(Pcsx2Launch.ReadOwner(m)).Ok && NormalPlay.LobbyNote(initial) != null, "the initial refusal was replaced by a later read");
    initial = Pcsx2Launch.PreLobby(m); File.WriteAllText(ini, P2Settings.Replace("MultitapPort1 = false", "MultitapPort1 = true"));
    Need(!Pcsx2Launch.Qualify(Pcsx2Launch.ReadOwner(m)).Ok && NormalPlay.LobbyNote(initial) == null, "the initial qualification was replaced by a later read");
    Need(NormalPlay.LobbyNote(new Pcsx2Launch.PreLobbyRead(null)) != null, "a failed initial read added no note");
});
foreach (var (name, settings, unmapped) in new (string, string?, bool)[]
{
    ("tabbed-settings", P2Settings.Replace("MemoryCards = memcards", "MemoryCards = memcards\t"), true),
    ("missing-settings", null, true),
    ("qualified-settings", P2Settings, false),
})
    Check("pcsx2-pre-lobby-note-" + name, () =>
    {
        string copy = Path.Combine(AppContext.BaseDirectory, Pcsx2Launch.CopyFolder);
        try
        {
            if (Directory.Exists(copy)) Directory.Delete(copy, true);
            var (m0, _, _) = P2(settings, copy);
            string system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "XInput1_4.dll"), rsha = DuckSha(system);
            var payload = Directory.GetFiles(AppContext.BaseDirectory).Where(p => p.EndsWith(".dll") || p.EndsWith(".exe") || p.EndsWith(".deps.json") || p.EndsWith(".runtimeconfig.json")).Select(p => new PayloadFile(Path.GetFileName(p), DuckSha(p))).ToArray();
            var m = m0 with { Reader = new(system, rsha, StateExport.NamedGetState, system, rsha), Payload = payload };
            string manifest = Path.Combine(AppContext.BaseDirectory, "normal.json"); File.WriteAllText(manifest, System.Text.Json.JsonSerializer.Serialize(m));
            var package = NormalPackage.Open(AppContext.BaseDirectory, DuckSha(manifest));
            var view = new View { Existing = Task.CompletedTask }; var f = new Factory();
            string state = Path.Combine(Path.GetTempPath(), "cl-pcsx2-note-" + Guid.NewGuid().ToString("N"));
            bool done = NormalPlay.Run(package, P2Args, view, CancellationToken.None, () => { }, state, f, () => []).GetAwaiter().GetResult();
            const string note = "Join order will not be applied to this game.";
            Need(view.Instructions.Count > 0 && view.Instructions.All(i => i.StartsWith(note) == unmapped), "the PCSX2 lobby did not reflect the initial qualification: " + name);
            Need(done && f.Creates == 1 && f.Target.Resumes == 1, "the note changed bypass/launch behavior");
        }
        finally { CloseDuck(); if (Directory.Exists(copy)) Directory.Delete(copy, true); }
    });
// Contract r4 3.2: three or four players map through a session-only multitap on port 1.
Check("pcsx2-three-and-four-players-map-through-port-1-multitap", () =>
{
    foreach (var joined in new[] { new[] { 2, 0, 1 }, [3, 1, 0, 2] })
    {
        var (m, emu, copy) = P2(); byte[] ownerBefore = File.ReadAllBytes(Path.Combine(emu, "inis", "PCSX2.ini"));
        var p = P2Plan(m, joined, Pcsx2Launch.PreLobby(m));
        string s = File.ReadAllText(Path.Combine(copy, "inis", "PCSX2.ini"));
        Need(p.Assigned && p.Executable != null && s.Contains("MultitapPort1 = true") && s.Contains("[Pad5]"), "players were not mapped through the multitap: " + p.Notice);
        Need(File.ReadAllBytes(Path.Combine(emu, "inis", "PCSX2.ini")).AsSpan().SequenceEqual(ownerBefore), "the owner's PCSX2.ini changed");
    }
});
Check("pcsx2-other-build-uses-existing-controls", () =>
{
    var (m, _, copy) = P2(); var p = P2Plan(m, [0], realPin: true);
    Need(!p.Assigned && p.Notice.Contains("version isn't supported") && !File.Exists(Path.Combine(copy, "inis", "PCSX2.ini")), "a build other than 2.7.182 was mapped or written: " + p.Notice);
    Need(ControllerLobby.Pcsx2.Pcsx2Inventory.Files[Pcsx2Launch.ExeName] == ControllerLobby.Pcsx2.Pcsx2Session.PinnedSha256, "the inventory exe is not the pinned build");
});
Check("pcsx2-running-refuses-before-any-write", () =>
{
    var (m, _, copy) = P2(); string? refused = null;
    try { P2Plan(m, [0], processes: () => [new(10, null)]); } catch (IOException e) { refused = e.Message; }
    Need(refused == Pcsx2Launch.RunningMessage && !Directory.Exists(Path.Combine(copy, "session")), "a second PCSX2 was prepared: " + refused);
    Need(!Pcsx2Launch.Running(m, () => [new(10, null)], except: 10) && Pcsx2Launch.Running(m, () => [new(10, null), new(11, @"D:\Other\pcsx2-qt.exe")], except: 10), "own target not excluded, or another install not counted");
    Need(Pcsx2Launch.Running(m, () => throw new InvalidOperationException("listing failed")), "a failed process listing did not count as running");
});
Check("pcsx2-copy-pin", () =>
{
    var (m, emu, copy) = P2(); string package = Path.GetDirectoryName(copy)!;
    Need(Pcsx2Launch.Pin(package, m).Length == ControllerLobby.Pcsx2.Pcsx2Inventory.Files.Count + 1, "the full copy was not pinned");
    void Refuses(string why, NormalManifest bad) { bool threw = false; try { Pcsx2Launch.Pin(package, bad); } catch (IOException) { threw = true; } Need(threw, why); }
    Refuses("a copy outside the package folder was accepted", m with { Pcsx2 = m.Pcsx2! with { CopyRoot = emu } });
    // Same count, one release name swapped for a name outside the inventory, present and correctly hashed.
    string outside = Path.Combine(copy, "updater.exe"); File.WriteAllText(outside, "an updater");
    Refuses("a name outside the release inventory was accepted", m with { Pcsx2 = m.Pcsx2! with { Inventory = m.Pcsx2!.Inventory.Select(i => i.Path == "SDL3.dll" ? new PayloadFile("updater.exe", DuckSha(outside)) : i).ToArray() } });
    Refuses("an incomplete copy was accepted", m with { Pcsx2 = m.Pcsx2! with { Inventory = m.Pcsx2!.Inventory.Where(i => i.Path != "SDL3.dll").ToArray() } });
    File.WriteAllText(Path.Combine(copy, "SDL3.dll"), "changed"); Refuses("a changed copy file was accepted", m);
});
Check("pcsx2-gamesettings-inside-the-session-folder-is-never-deleted", () =>
{
    string root = Path.Combine(Path.GetTempPath(), "cl-pcsx2-dest-" + Guid.NewGuid().ToString("N")), copy = Path.Combine(root, "copy"), games = Path.Combine(copy, "session", "gamesettings");
    var (m, _, _) = P2(P2Settings.Replace("Bios = bios\n", "Bios = bios\nGameSettings = " + games + "\n"), copy);
    Directory.CreateDirectory(games); File.WriteAllText(Path.Combine(games, "SLUS-20062_ABCD1234.ini"), "[EmuCore/Gamefixes]\nEETimingHack = true\n");
    var p = P2Plan(m, [0], Pcsx2Launch.PreLobby(m));
    Need(!p.Assigned && File.Exists(Path.Combine(games, "SLUS-20062_ABCD1234.ini")), "an owner game-settings folder inside the copy's session folder was deleted or mapped: " + p.Notice);
});
Check("pcsx2-gamesettings-inside-the-copy-refuses-before-any-write", () =>
{
    string root = Path.Combine(Path.GetTempPath(), "cl-pcsx2-dest-" + Guid.NewGuid().ToString("N")), copy = Path.Combine(root, "copy");
    var (m, emu, _) = P2(P2Settings.Replace("Bios = bios\n", "Bios = bios\nGameSettings = " + copy + "\n"), copy);
    File.WriteAllText(Path.Combine(copy, "keep.ini"), "[EmuCore]\nA = 1\n");
    var p = P2Plan(m, [0], Pcsx2Launch.PreLobby(m));
    Need(!p.Assigned && !Directory.Exists(Path.Combine(copy, "session")) && File.Exists(Path.Combine(copy, "keep.ini")), "a game-settings folder overlapping the copy was written into: " + p.Notice);
});
Check("pcsx2-manifest-without-a-copy-is-refused", () =>
{
    var (m, _, _) = P2(); bool threw = false;
    try { P2Plan(m with { Pcsx2 = null }, [0]); } catch (IOException e) { threw = e.Message == "Invalid normal-play manifest"; }
    Need(threw, "a PCSX2 manifest without its copy was prepared");
});
// A full lobby run through the package: a PCSX2 that appears before create, or before resume, stops the launch.
foreach (int after in new[] { 2, 3 })
    Check("pcsx2-arrival-at-gate-after-check-" + after, () =>
    {
        string copy = Path.Combine(AppContext.BaseDirectory, Pcsx2Launch.CopyFolder);
        try
        {
            if (Directory.Exists(copy)) Directory.Delete(copy, true);
            var (m0, _, _) = P2(copyRoot: copy);
            string system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "XInput1_4.dll"), rsha = DuckSha(system);
            var payload = Directory.GetFiles(AppContext.BaseDirectory).Where(p => p.EndsWith(".dll") || p.EndsWith(".exe") || p.EndsWith(".deps.json") || p.EndsWith(".runtimeconfig.json")).Select(p => new PayloadFile(Path.GetFileName(p), DuckSha(p))).ToArray();
            var m = m0 with { Reader = new(system, rsha, StateExport.NamedGetState, system, rsha), Payload = payload };
            string path = Path.Combine(AppContext.BaseDirectory, "normal.json"); File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(m));
            var package = NormalPackage.Open(AppContext.BaseDirectory, DuckSha(path), false);
            string state = Path.Combine(Path.GetTempPath(), "cl-state-" + Guid.NewGuid().ToString("N")); var f = new Factory(); string? refused = null; int calls = 0;
            try { NormalPlay.Run(package, P2Args, new View(), CancellationToken.None, () => { }, state, f, () => ++calls > after ? [new(10, null)] : [], () => Task.FromResult<SelectionSeal?>(new SelectionSeal(1, [0])), pcsx2Pin: m.ExecutableSha256).GetAwaiter().GetResult(); }
            catch (IOException e) { refused = e.Message; }
            Need(refused == Pcsx2Launch.RunningMessage && f.Target.Resumes == 0 && (after == 2 ? f.Creates == 0 : f.Creates == 1 && f.Target.Stops == 1), "a late PCSX2 did not stop the launch: " + refused + ", creates " + f.Creates + ", checks " + calls);
        }
        finally { CloseDuck(); if (Directory.Exists(copy)) Directory.Delete(copy, true); }
    });
Check("pcsx2-copy-exe-must-be-the-owners", () =>
{
    var (m, _, copy) = P2(); string package = Path.GetDirectoryName(copy)!; string exe = Path.Combine(copy, Pcsx2Launch.ExeName);
    File.WriteAllText(exe, "another program");
    var bad = m with { Pcsx2 = m.Pcsx2! with { Inventory = m.Pcsx2!.Inventory.Select(i => i.Path == Pcsx2Launch.ExeName ? i with { Sha256 = DuckSha(exe) } : i).ToArray() } };
    bool threw = false; try { Pcsx2Launch.Pin(package, bad); } catch (IOException) { threw = true; } Need(threw, "a copy exe that differs from the owner's was accepted");
});

// --- Snapshot destination safety, and the running refusal before every write and at the launch gate ---
bool Junction(string link,string target){var s=new System.Diagnostics.ProcessStartInfo("cmd.exe","/d /c mklink /J \""+link+"\" \""+target+"\""){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};using var p=System.Diagnostics.Process.Start(s)!;p.WaitForExit();return p.ExitCode==0&&File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint);}
Dictionary<string,string> Tree(string root)=>Directory.GetFiles(root,"*",new EnumerationOptions{RecurseSubdirectories=true,AttributesToSkip=FileAttributes.ReparsePoint}).ToDictionary(f=>f,DuckSha);
bool Same(Dictionary<string,string> a,Dictionary<string,string> b)=>a.OrderBy(k=>k.Key).SequenceEqual(b.OrderBy(k=>k.Key));
foreach(var (name,arrange) in new (string,Func<string,string,bool>)[]{
 ("copy-root",(emu,copy)=>{string real=Path.Combine(emu,"elsewhere");Directory.CreateDirectory(real);Directory.Delete(copy,true);return Junction(copy,real);}),
 ("session-folder",(emu,copy)=>Junction(Path.Combine(copy,"session"),emu)),
 ("game-settings-snapshot-folder",(emu,copy)=>{Directory.CreateDirectory(Path.Combine(copy,"session"));return Junction(Path.Combine(copy,"session","gamesettings"),Path.Combine(emu,"gamesettings"));}),
 ("input-profiles-snapshot-folder",(emu,copy)=>{Directory.CreateDirectory(Path.Combine(copy,"session","gamesettings"));File.WriteAllText(Path.Combine(copy,"session","gamesettings","KEEP.ini"),"left from an earlier session");return Junction(Path.Combine(copy,"session","inputprofiles"),Path.Combine(emu,"inputprofiles"));}),
})Check("duckstation-junction-at-"+name+"-writes-and-deletes-nothing",()=>{
 var (m,emu,copy)=Duck();foreach(string d in new[]{"gamesettings","inputprofiles"}){Directory.CreateDirectory(Path.Combine(emu,d));File.WriteAllText(Path.Combine(emu,d,"SYNTHETIC.ini"),"[GPU]\nDeinterlacingMode = Progressive\n");}
 Need(arrange(emu,copy),"fixture: junction not created");var source=Tree(emu);var entries=Directory.GetFileSystemEntries(copy,"*",SearchOption.TopDirectoryOnly).OrderBy(x=>x).ToArray();
 var p=NormalPlan.Prepare(m,DuckArgs,new SelectionSeal(1,[0]),Path.GetTempPath(),null,()=>[]);
 Need(!p.Assigned&&p.Executable==null&&p.Notice.Contains("not applied"),"a launch through a junction was mapped");
 Need(Same(Tree(emu),source)&&File.Exists(Path.Combine(emu,"gamesettings","SYNTHETIC.ini"))&&File.Exists(Path.Combine(emu,"inputprofiles","SYNTHETIC.ini")),"a source file was deleted or changed through the junction");
 Need(!File.Exists(Path.Combine(copy,"settings.ini"))&&Directory.GetFileSystemEntries(copy,"*",SearchOption.TopDirectoryOnly).OrderBy(x=>x).SequenceEqual(entries),"the copy was written before the destination was validated");
 if(name=="input-profiles-snapshot-folder")Need(File.Exists(Path.Combine(copy,"session","gamesettings","KEEP.ini")),"the first snapshot folder was emptied although the second was unsafe");
});
Check("duckstation-source-aliased-to-the-snapshot-folder-writes-and-deletes-nothing",()=>{
 var (m,emu,copy)=Duck();string dest=Path.Combine(copy,"session","gamesettings");Directory.CreateDirectory(dest);File.WriteAllText(Path.Combine(dest,"SYNTHETIC.ini"),"[GPU]\nDeinterlacingMode = Progressive\n");
 File.WriteAllText(Path.Combine(emu,"settings.ini"),DuckSettings+"\n[Folders]\nGameSettings = "+dest+"\n");var before=Tree(emu);
 var p=NormalPlan.Prepare(m,DuckArgs,new SelectionSeal(1,[0]),Path.GetTempPath(),null,()=>[]);
 Need(!p.Assigned&&Same(Tree(emu),before)&&File.Exists(Path.Combine(dest,"SYNTHETIC.ini"))&&!File.Exists(Path.Combine(copy,"settings.ini")),"source and destination were the same folder and it was emptied or written");
 var (m2,emu2,copy2)=Duck();string link=Path.Combine(emu2,"gamesettings");Directory.CreateDirectory(Path.Combine(copy2,"session","gamesettings"));File.WriteAllText(Path.Combine(copy2,"session","gamesettings","SYNTHETIC.ini"),"owner file reached through a junction");
 Need(Junction(link,Path.Combine(copy2,"session","gamesettings")),"fixture: junction not created");var before2=Tree(copy2);
 var p2=NormalPlan.Prepare(m2,DuckArgs,new SelectionSeal(1,[0]),Path.GetTempPath(),null,()=>[]);
 Need(!p2.Assigned&&Same(Tree(copy2),before2),"a source junction into the copy let the snapshot empty the owner's folder");
});
[System.Runtime.InteropServices.DllImport("kernel32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode,SetLastError=true)]static extern bool CreateHardLinkW(string link,string existing,IntPtr security);
Check("duckstation-hard-linked-copy-settings-never-write-the-owner-file",()=>{
 var (m,emu,copy)=Duck();string owner=Path.Combine(emu,"settings.ini"),link=Path.Combine(copy,"settings.ini");
 Need(CreateHardLinkW(link,owner,IntPtr.Zero),"fixture: hard link not created");string before=DuckSha(owner);
 var p=NormalPlan.Prepare(m,DuckArgs,new SelectionSeal(3,[1,0]),Path.GetTempPath(),null,()=>[]);
 Need(DuckSha(owner)==before&&File.ReadAllText(owner)==DuckSettings,"the owner's settings.ini was written through the hard link");
 Need(p.Assigned&&File.ReadAllText(link).Contains("Cross = XInput-1/A")&&!Directory.GetFiles(copy,"*.tmp").Any(),"the copy's settings were not replaced by a fresh file, or a temporary file was left");
});
Check("duckstation-running-refuses-prepare-before-any-write",()=>{
 var (m,emu,copy)=Duck();string file=Path.Combine(copy,"settings.ini");File.WriteAllText(file,"sentinel settings belonging to the running copy");var before=Tree(Path.GetDirectoryName(Path.GetDirectoryName(emu))!);string? refused=null;
 try{NormalPlan.Prepare(m,DuckArgs,new SelectionSeal(1,[0]),Path.GetTempPath(),null,()=>[new(10,Path.Combine(copy,DuckStationLaunch.ExeName))]);}catch(IOException e){refused=e.Message;}
 Need(refused==DuckStationLaunch.RunningMessage&&Same(Tree(Path.GetDirectoryName(Path.GetDirectoryName(emu))!),before)&&!Directory.Exists(Path.Combine(copy,"session")),"Prepare wrote the running copy's settings or snapshot before refusing: "+refused);
});
// Start through Run with a synthetic selection in place of the XInput reader. calls counts the running queries in order.
(string? refused,bool done,Factory f,string copy,string events,int calls,View view) StartRun(Func<int,IEnumerable<DuckProcess>> processes,bool sentinel=false){
 var d=OpenDuck();if(d.package==null)throw new Exception("package did not open: "+d.error?.Message);
 if(sentinel)File.WriteAllText(Path.Combine(d.copy,"settings.ini"),"sentinel settings belonging to the running copy");
 string state=Path.Combine(Path.GetTempPath(),"cl-state-"+Guid.NewGuid().ToString("N"));var view=new View();var f=new Factory();string? refused=null;bool done=false;int calls=0;
 try{done=NormalPlay.Run(d.package,DuckArgs,view,CancellationToken.None,()=>{},state,f,()=>processes(++calls),()=>Task.FromResult<SelectionSeal?>(new SelectionSeal(3,[1,0]))).GetAwaiter().GetResult();}catch(IOException e){refused=e.Message;}
 return (refused,done,f,d.copy,File.ReadAllText(Directory.GetFiles(state,"events.jsonl",SearchOption.AllDirectories).Single()),calls,view);
}
Check("duckstation-start-launches-the-copy-when-nothing-is-running",()=>{
 try{var r=StartRun(_=>[]);
 Need(r.refused==null&&r.done&&r.f.Creates==1&&r.f.Target.Resumes==1&&r.events.Contains("DuckStationCopy\\\\"+DuckStationLaunch.ExeName)&&File.ReadAllText(Path.Combine(r.copy,"settings.ini")).Contains("Cross = XInput-1/A"),"Start did not launch the copy with join order: "+r.refused);
 Need(r.calls==4,"the running check was not repeated at the launch gate ("+r.calls+" queries)");}finally{CloseDuck();}
});
Check("duckstation-start-while-running-writes-nothing-and-starts-nothing",()=>{
 try{var r=StartRun(_=>[new(10,@"X:\never")],sentinel:true);Need(r.refused==null&&r.f.Creates==1,"fixture: an unrelated process blocked the launch");CloseDuck();
 string copyExe=Path.Combine(AppContext.BaseDirectory,"DuckStationCopy",DuckStationLaunch.ExeName);
 r=StartRun(_=>[new(10,copyExe)],sentinel:true);
 Need(r.refused==DuckStationLaunch.RunningMessage&&r.f.Creates==0&&r.calls==1,"Start with DuckStation running created a target or kept going: "+r.refused+" after "+r.calls+" queries");
 Need(File.ReadAllText(Path.Combine(r.copy,"settings.ini")).StartsWith("sentinel")&&!Directory.Exists(Path.Combine(r.copy,"session")),"the running copy's settings or snapshot were written before the refusal");
 Need(r.events.Contains("normal-play-refused")&&r.events.Contains("normal-play-complete")&&!r.events.Contains("normal-play-launch"),"the refusal was not recorded as a finished session");}finally{CloseDuck();}
});
Check("duckstation-appearing-before-the-notice-is-a-prompt-finished-refusal",()=>{
 // Query 1 is Prepare's own check before its first write; query 2 is the shared point before the notice.
 try{string copyExe=Path.Combine(AppContext.BaseDirectory,"DuckStationCopy",DuckStationLaunch.ExeName);var r=StartRun(n=>n>=2?[new(10,copyExe)]:[]);
 Need(r.refused==DuckStationLaunch.RunningMessage&&r.f.Creates==0&&r.calls>=2,"refusal before the notice: "+r.refused+" creates="+r.f.Creates+" queries="+r.calls);
 Need(r.events.Contains("normal-play-refused")&&r.events.Contains("normal-play-complete")&&!r.events.Contains("normal-play-launch"),"the refusal before the notice skipped the finished-session record");
 // DuckStation started after Prepare's check, so the copy was written before it existed; the launch still stops promptly.
 Need(!r.view.Statuses.Any(s=>s.StartsWith("Starting game")),"Starting game was shown before the refusal");}finally{CloseDuck();}
});
Check("duckstation-appearing-during-the-notice-starts-nothing",()=>{
 // Queries 1 and 2 are before the notice (Prepare's own check, the shared point); query 3 is the gate before the target is created, query 4 the gate before resume.
 try{string copyExe=Path.Combine(AppContext.BaseDirectory,"DuckStationCopy",DuckStationLaunch.ExeName);var r=StartRun(n=>n>=3?[new(10,copyExe)]:[]);
 Need(r.refused==DuckStationLaunch.RunningMessage&&r.f.Creates==0&&r.calls==3,"a DuckStation that appeared during the notice did not stop the launch: "+r.refused+" after "+r.calls+" queries, creates="+r.f.Creates);
 Need(r.events.Contains("normal-play-complete"),"the stopped launch was left as an interrupted session");}finally{CloseDuck();}
});
Check("duckstation-appearing-between-create-and-resume-stops-the-held-target",()=>{
 try{string copyExe=Path.Combine(AppContext.BaseDirectory,"DuckStationCopy",DuckStationLaunch.ExeName);var r=StartRun(n=>n>=4?[new(10,copyExe)]:[]);
 Need(r.refused==DuckStationLaunch.RunningMessage&&r.f.Creates==1&&r.f.Target.Resumes==0&&r.f.Target.Stops==1&&r.f.Target.Disposed&&r.calls==4,"the held target was resumed or not cleaned up: "+r.refused+" resumes="+r.f.Target.Resumes+" stops="+r.f.Target.Stops);
 Need(r.events.Contains("normal-play-ended")&&r.events.Contains("normal-play-complete"),"cleanup was not confirmed before the refusal was reported");}finally{CloseDuck();}
});
Check("duckstation-update-launch-refused-when-duckstation-appears-during-the-notice",()=>{
 try{var d=OpenDuck(allow:true,staleExe:true);string state=Path.Combine(Path.GetTempPath(),"cl-state-"+Guid.NewGuid().ToString("N"));var f=new Factory();int calls=0;string? refused=null;
 try{NormalPlay.Run(d.package!,DuckArgs,new View(),CancellationToken.None,()=>{},state,f,()=>++calls>=2?[new(10,d.exe)]:[]).GetAwaiter().GetResult();}catch(IOException e){refused=e.Message;}
 Need(refused==DuckStationLaunch.RunningMessage&&f.Creates==0&&calls==2,"an update launch started beside a DuckStation that appeared during the notice: creates="+f.Creates+" queries="+calls);}
 finally{CloseDuck();}
});

Console.WriteLine($"Cases={passed+failed} Passed={passed} Failed={failed}; M synthetic targets and configuration; no emulator. Two duckstation-existing-controls-request cases and three pcsx2-pre-lobby-note cases load the real system XInput reader and sample connected controllers; no input is used.");return failed==0?0:1;

sealed class View:ITrialView{public List<string> Statuses=[];public List<string> Instructions=[];public Task? Existing;public Task? ExistingControlsRequested=>Existing;public int SelectingCalls;public bool Alive=>true;public void Selecting(IReadOnlyList<JoinedCard> c,string i){SelectingCalls++;lock(Instructions)Instructions.Add(i);}public void Status(string t,string d)=>Statuses.Add(t+": "+d);}

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

sealed class FakeSdl : ISdlFunctions
{
    public bool InitResult = true, SnapshotFails; public int Quits, Released; public readonly ManualResetEventSlim Block = new(true), InitGate = new(true), UpdateGate = new(true);
    public bool Init(uint flags) { InitGate.Wait(); return InitResult; }
    public void Quit() { Block.Wait(); Quits++; }
    public void Update() => UpdateGate.Wait();
    public uint[] Joysticks() => SnapshotFails ? throw new InvalidOperationException("native failure") : [7];
    public string Guid(uint id) => "0300fa675e0400008e02000014017801";
    public string Path(uint id) => "XInput#0";
    public readonly ManualResetEventSlim ReleaseGate = new(true);
    public void Dispose() { ReleaseGate.Wait(); Released++; }
}
sealed class FakeObserver(Func<int, IReadOnlyList<ControllerLobby.Xemu.ObservedPad>> next) : IPadObserver
{
    public int Calls; public bool Disposed, Incomplete;
    public bool ShutdownComplete => !Incomplete;
    public IReadOnlyList<ControllerLobby.Xemu.ObservedPad> Snapshot() => next(++Calls);
    public void Dispose() => Disposed = true;
}
