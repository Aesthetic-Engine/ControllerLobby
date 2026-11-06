using System.Security.Cryptography;
using System.Text.Json;
using ControllerLobby.DirectIndex.HostCore;
using ControllerLobby.DirectIndex.Native;
using ControllerLobby.MameTrial.HostCore;

int passed=0,failed=0;
void Check(string name,Action run){try{run();Console.WriteLine("PASS "+name);passed++;}catch(Exception e){Console.WriteLine("FAIL "+name+": "+e.Message);failed++;}}
void Need(bool value,string message){if(!value)throw new Exception(message);}
string Sha(string file)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
string root=Path.Combine(AppContext.BaseDirectory,"scope-fixtures",Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
string Make(string name){string p=Path.Combine(root,name);Directory.CreateDirectory(Path.GetDirectoryName(p)!);File.WriteAllText(p,"synthetic text; never executed");return p;}
string source=Make("source-manifest.json"),lua=Make("mame-metadata.lua"),exe=Make("target/emulator.exe"),core=Make("target/core.dll"),content=Make("target/game.bin"),cfg=Make("target/retroarch.cfg"),rom=Make("target/tmnt2.zip"),reader=Make("target/fixture.dll");
string target=Path.GetDirectoryName(exe)!,systemReader=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"XInput1_4.dll");
var fixtureReader=new ReaderBinding(reader,Sha(reader),StateExport.NamedGetState,reader,Sha(reader));
var sysReader=new ReaderBinding(systemReader,Sha(systemReader),StateExport.NamedGetState,systemReader,Sha(systemReader));
var pins=new[]{exe,core,content,cfg,rom,reader,systemReader,Path.Combine(target,"mame.ini"),Path.Combine(target,"cfg/default.cfg"),Path.Combine(target,"cfg/tmnt2.cfg")}.Select(ProtectedFile.Capture).ToArray();
var trial=new TrialManifest(1,"direct-index-four-player-v1",4,600,fixtureReader,new(target,exe,core,content,cfg,"fixture"),pins,[new("source-manifest.json",Sha(source))],Sha(source));
var mame=new MameTrialManifest(1,"mame-four-player-v1",4,600,sysReader,new(target,exe,target),pins,[new("source-manifest.json",Sha(source)),new("mame-metadata.lua",Sha(lua))],Sha(source));

void Open(object manifest){string file=Path.Combine(root,"manifest.json");File.WriteAllText(file,JsonSerializer.Serialize(manifest));if(manifest is TrialManifest)Need(TrialPackage.Open(file,Sha(file)).Current(),"trial inputs changed");else Need(MameTrialPackage.Open(file,Sha(file)).Current(),"MAME inputs changed");}
void Refuse(object manifest){bool refused=false;try{Open(manifest);}catch(InvalidDataException e){refused=e.Message.Contains("approved four-player first-play scope");}Need(refused,"scope guard failed to refuse");}

Check("direct-valid-neutral-scope",()=>Open(trial));
Check("mame-valid-neutral-scope",()=>Open(mame));
Check("direct-unknown-scope-refused",()=>Refuse(trial with{ScopeDecision="unapproved-scope"}));
Check("mame-unknown-scope-refused",()=>Refuse(mame with{ScopeDecision="unapproved-scope"}));
Check("direct-cross-kind-scope-refused",()=>Refuse(trial with{ScopeDecision="mame-four-player-v1"}));
Check("mame-cross-kind-scope-refused",()=>Refuse(mame with{ScopeDecision="direct-index-four-player-v1"}));
Check("direct-schema-still-required",()=>Refuse(trial with{Schema=2}));
Check("mame-schema-still-required",()=>Refuse(mame with{Schema=2}));
Check("direct-four-players-still-required",()=>Refuse(trial with{RequiredPlayers=3}));
Check("mame-four-players-still-required",()=>Refuse(mame with{RequiredPlayers=3}));
Check("direct-duration-upper-bound-retained",()=>Refuse(trial with{MaximumSeconds=601}));
Check("mame-duration-lower-bound-retained",()=>Refuse(mame with{MaximumSeconds=0}));
Console.WriteLine($"Cases={passed+failed} Passed={passed} Failed={failed}; M/synthetic manifests and read-only system-file pins; no emulator or input API invoked");
return failed==0?0:1;
