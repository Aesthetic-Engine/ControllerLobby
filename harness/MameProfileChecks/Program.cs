using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using ControllerLobby.DirectIndex.Selection;
using ControllerLobby.Mame;

int passed=0,failed=0;
void Need(bool value){if(!value)throw new Exception("assertion failed");}
void Check(string name,Action body){try{body();passed++;Console.WriteLine("PASS "+name);}catch(Exception e){failed++;Console.WriteLine("FAIL "+name+": "+e.GetType().Name+" "+e.Message);}}
void Refuses(Action body){bool refused=false;try{body();}catch(Exception e)when(e is ArgumentException or InvalidDataException or XmlException){refused=true;}Need(refused);}
var profile=MameProfile.Generate(new(15,[3,0,1,2]));
ExpectedBinding Binding(string type)=>profile.Bindings.Single(b=>b.Token==type);
Check("literal-join-order-3-0-1-2-is-not-compacted",()=>{
 var devices=XDocument.Parse(profile.ControllerXml).Descendants("mapdevice").ToArray();Need(devices.Select(d=>(string?)d.Attribute("device")).SequenceEqual(new[]{"XInput Player 4","XInput Player 1","XInput Player 2","XInput Player 3"}));Need(devices.Select(d=>(string?)d.Attribute("controller")).SequenceEqual(new[]{"JOYCODE_1","JOYCODE_2","JOYCODE_3","JOYCODE_4"}));
});
foreach(var selection in new[]{new SelectionSeal(7,[0,1,2,3]),new(15,[0,1,2,2]),new(15,[0,1,2]),new(15,[0,1,2,4])})Check("invalid-selection-"+selection.ConnectedMask+"-"+string.Join("",selection.PlayerIndices),()=>Refuses(()=>MameProfile.Generate(selection)));
Check("all-observed-types-explicit-and-no-cross-player-joystick",()=>{
 var ports=XDocument.Parse(profile.DefaultXml).Descendants("port").ToArray();Need(ports.Length==758&&ports.Select(p=>(string?)p.Attribute("type")).Distinct().Count()==758);
 foreach(var b in profile.Bindings){var player=Regex.Match(b.Token,@"^(?:P([1-9][0-9]*)_|(?:START|COIN)([1-9][0-9]*)$)");int p=player.Success?int.Parse(player.Groups[1].Success?player.Groups[1].Value:player.Groups[2].Value):0;
 foreach(string sequence in new[]{b.Standard,b.Increment,b.Decrement})foreach(Match joystick in Regex.Matches(sequence,@"JOYCODE_(\d+)_"))Need(p is >=1 and <=4&&int.Parse(joystick.Groups[1].Value)==p);}
});
Check("all-four-start-coin-and-primary-actions-bound",()=>{for(int p=1;p<=4;p++){Need(Binding("START"+p).Standard.Contains("JOYCODE_"+p+"_START"));Need(Binding("COIN"+p).Standard.Contains("JOYCODE_"+p+"_SELECT"));foreach(int button in new[]{1,2})Need(Binding("P"+p+"_BUTTON"+button).Standard.Contains("JOYCODE_"+p+"_BUTTON"+button));}});
Check("p1-left-stick-dpad-and-original-keyboard-up",()=>Need(Binding("P1_JOYSTICK_UP").Standard=="JOYCODE_1_YAXIS_UP_SWITCH OR JOYCODE_1_HAT1UP OR KEYCODE_UP"));
Check("right-stick-z-rz-use-polarity-not-primary-joystick-map",()=>Need(Binding("P1_JOYSTICKRIGHT_UP").Standard.StartsWith("JOYCODE_1_RZAXIS_NEG_SWITCH")&&Binding("P1_JOYSTICKRIGHT_DOWN").Standard.StartsWith("JOYCODE_1_RZAXIS_POS_SWITCH")&&Binding("P1_JOYSTICKRIGHT_LEFT").Standard.StartsWith("JOYCODE_1_ZAXIS_NEG_SWITCH")&&Binding("P1_JOYSTICKRIGHT_RIGHT").Standard.StartsWith("JOYCODE_1_ZAXIS_POS_SWITCH")));
Check("pedal-keyboard-increment-decrement-order-and-trigger-axis",()=>Need(Binding("P1_PEDAL").Standard=="JOYCODE_1_SLIDER1"&&Binding("P1_PEDAL").Increment=="KEYCODE_LCONTROL"&&Binding("P1_PEDAL").Decrement=="NONE"));
Check("ui-alt-tab-and-escape-remain-keyboard-only",()=>Need(Binding("UI_MENU").Standard=="KEYCODE_TAB NOT KEYCODE_LALT NOT KEYCODE_RALT"&&Binding("UI_CANCEL").Standard=="KEYCODE_ESC"&&!profile.Bindings.Where(b=>b.Token.StartsWith("UI_")||b.Token.StartsWith("SERVICE")||b.Token=="TILT").Any(b=>b.Standard.Contains("JOYCODE"))));
Check("balonfgt-start2-and-start6-collision-sequences-both-removed",()=>{
 string source="<mameconfig version=\"10\"><system name=\"balonfgt\"><input><port tag=\":IN0\" type=\"START2\" mask=\"1\" defvalue=\"0\"><newseq type=\"standard\">JOYCODE_2_BUTTON12</newseq></port><port tag=\":IN1\" type=\"START6\" mask=\"2\" defvalue=\"0\"><newseq type=\"standard\">JOYCODE_2_BUTTON12</newseq></port></input><counters><coins index=\"0\" number=\"17\"/></counters></system></mameconfig>";
 var doc=XDocument.Parse(MameProfile.FilterGame("balonfgt",source));Need(!doc.Descendants("newseq").Any()&&doc.Descendants("port").Count()==2&&(string?)doc.Descendants("coins").Single().Attribute("number")=="17");
});
Check("arknoid2u-analog-and-dip-bookkeeping-preserved",()=>{
 string source="<mameconfig version=\"10\"><system name=\"arknoid2u\"><input><port tag=\":P1\" type=\"P1_DIAL\" mask=\"255\" defvalue=\"0\" keydelta=\"20\" sensitivity=\"85\" reverse=\"yes\"><newseq type=\"standard\">JOYCODE_2_XAXIS</newseq></port><port tag=\":DSW\" type=\"DIPSWITCH\" mask=\"3\" defvalue=\"2\" value=\"1\"/></input><bookkeeping marker=\"preserve\"/></system></mameconfig>";
 var doc=XDocument.Parse(MameProfile.FilterGame("arknoid2u",source));var dial=doc.Descendants("port").Single(p=>(string?)p.Attribute("type")=="P1_DIAL");Need((string?)dial.Attribute("keydelta")=="20"&&(string?)dial.Attribute("sensitivity")=="85"&&(string?)dial.Attribute("reverse")=="yes"&&!dial.Elements("newseq").Any());Need((string?)doc.Descendants("port").Single(p=>(string?)p.Attribute("type")=="DIPSWITCH").Attribute("value")=="1"&&doc.Descendants("bookkeeping").Any());
});
Check("unsafe-or-ambiguous-game-config-refuses",()=>{
 foreach(string source in new[]{"<!DOCTYPE mameconfig [<!ENTITY x SYSTEM 'file:///must-not-read'>]><mameconfig version='10'><system name='tmnt2'>&x;</system></mameconfig>","<mameconfig version='10'><system name='other'/></mameconfig>","<mameconfig version='10'><system name='tmnt2'/><system name='tmnt2'/></mameconfig>","<mameconfig version='10'><system name='tmnt2'><newseq>JOYCODE_1_BUTTON1</newseq></system></mameconfig>"})Refuses(()=>MameProfile.FilterGame("tmnt2",source));
});
if(args.Length==1){Directory.CreateDirectory(args[0]);File.WriteAllText(Path.Combine(args[0],"session.cfg"),profile.ControllerXml);File.WriteAllText(Path.Combine(args[0],"default.cfg"),profile.DefaultXml);File.WriteAllBytes(Path.Combine(args[0],"expected.json"),JsonSerializer.SerializeToUtf8Bytes(profile.Bindings));}
Console.WriteLine($"Cases={passed+failed} Passed={passed} Failed={failed}; M/XML only; no emulator/controller");return failed==0?0:1;
