using ControllerLobby.Setup;
using System.IO;
using System.Text.Json;

// Real bundled Python and transaction engine against new synthetic roots only.
string output=Path.GetFullPath(args[1]);
if(Directory.Exists(output))throw new IOException("Use a fresh fixture directory");
Directory.CreateDirectory(output);
string local=Path.Combine(output,"local"),temp=Path.Combine(output,"temp");
Directory.CreateDirectory(local);Directory.CreateDirectory(temp);
Environment.SetEnvironmentVariable("LOCALAPPDATA",local);
Environment.SetEnvironmentVariable("TEMP",temp);Environment.SetEnvironmentVariable("TMP",temp);
byte[] zip=File.ReadAllBytes(args[0]);
var bundle=PackageBundle.Open(zip,PackageBundle.Sha(zip),Path.Combine(output,"cache"));
var engine=new SetupEngine(bundle);
string root=Path.Combine(output,"Synthetic LaunchBox"),data=Path.Combine(root,"Data");
Directory.CreateDirectory(data);
string xml="<LaunchBox>";
foreach(var item in new[]{("synthetic-retro","RetroArch","retroarch.exe"),("synthetic-mame","MAME","mame.exe")})
{
    string folder=Path.Combine(root,"Emulators",item.Item2);Directory.CreateDirectory(folder);
    File.WriteAllText(Path.Combine(folder,item.Item3),"synthetic emulator; never executed");
    xml+=$"<Emulator><ID>{item.Item1}</ID><Title>{item.Item2}</Title><ApplicationPath>Emulators\\{item.Item2}\\{item.Item3}</ApplicationPath></Emulator>";
}
xml+="</LaunchBox>";File.WriteAllText(Path.Combine(data,"Emulators.xml"),xml);
var discovery=await engine.Discover(root);
if(discovery.Installed||discovery.Entries.Count!=2||discovery.Entries.Any(e=>!e.Eligible))throw new Exception("discovery failed");
Console.WriteLine("PASS actual bundled discovery");
var ids=discovery.Entries.Select(e=>e.Id).ToArray();
foreach(string action in new[]{"preview","install","verify","repair","verify","remove"})
{
    var result=await engine.Run(action,root,ids);
    string label=action=="verify"&&File.Exists(Path.Combine(output,"verify.log"))?"verify-after-repair":action;
    File.WriteAllText(Path.Combine(output,label+".log"),result.Details);
    if(!result.Success)throw new Exception(action+" failed: "+result.ExitCode+"; see log");
    if(action=="preview"&&File.ReadAllText(Path.Combine(data,"Emulators.xml"))!=xml)throw new Exception("preview mutated XML");
    if(action=="install"&&!(await engine.Discover(root)).Installed)throw new Exception("maintenance discovery failed");
    Console.WriteLine("PASS actual bundled "+label);
}
var restored=System.Xml.Linq.XDocument.Load(Path.Combine(data,"Emulators.xml"));
if(restored.Descendants("ApplicationPath").Any(e=>e.Value.Contains("ControllerLobby")))throw new Exception("remove did not restore paths");
File.WriteAllText(Path.Combine(output,"receipt.json"),JsonSerializer.Serialize(new{layer="M",bundle_sha256=PackageBundle.Sha(zip),result="PASS",boundary="real bundled interpreter, wrapper and transaction engine; synthetic emulator files never launched"}));
