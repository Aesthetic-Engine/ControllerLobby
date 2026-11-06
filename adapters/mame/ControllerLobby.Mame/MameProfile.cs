using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using ControllerLobby.DirectIndex.Selection;

namespace ControllerLobby.Mame;

public sealed record InputType(string Token,int Player,bool Analog,string[] Keyboard);
public sealed record DefaultTable(string Source,string SourceSha256,string WindowsSource,string WindowsSourceSha256,string ObservedTypesSha256,InputType[] Types);
public sealed record ExpectedBinding(string Token,bool Analog,string Standard,string Increment,string Decrement);
public sealed record GeneratedProfile(string ControllerXml,string DefaultXml,IReadOnlyList<ExpectedBinding> Bindings,string Sha256);

// Exact-build first-play profile. This emits candidates; MAME's actual consumed
// sequences/device IDs and the owner's distinct-player test provide other proof.
public static class MameProfile
{
    private static readonly DefaultTable defaults=Load();
    public static GeneratedProfile Generate(SelectionSeal selection)=>Generate(selection,false);
    public static GeneratedProfile Generate(SelectionSeal selection,bool allowPartialPlayers)
    {
        if(allowPartialPlayers)
        {
            if(selection.ConnectedMask is <1 or >15||selection.PlayerIndices.Count is <1 or >4||selection.PlayerIndices.Distinct().Count()!=selection.PlayerIndices.Count||selection.PlayerIndices.Any(i=>i is <0 or >3||(selection.ConnectedMask&(1<<i))==0))throw new ArgumentException("Valid 1-4 XInput slot selection required");
        }
        else if(/*guard:mame-four-selection*/selection.ConnectedMask!=15||selection.PlayerIndices.Count!=4||selection.PlayerIndices.Distinct().Count()!=4||selection.PlayerIndices.Any(i=>i is <0 or >3))throw new ArgumentException("Four distinct fixed XInput slots required");
        int joined=selection.PlayerIndices.Count;
        int[] ordered=[..selection.PlayerIndices,..Enumerable.Range(0,4).Where(i=>!selection.PlayerIndices.Contains(i))];
        var controller=new XElement("input");
        for(int p=1;p<=4;p++)controller.Add(new XElement("mapdevice",new XAttribute("device","XInput Player "+(ordered[p-1]+1)),new XAttribute("controller","JOYCODE_"+p)));
        string controllerXml=Document("default",controller);
        var input=new XElement("input");var bindings=new List<ExpectedBinding>();
        foreach(var entry in defaults.Types)
        {
            string standard=entry.Keyboard[0],increment=entry.Keyboard[1],decrement=entry.Keyboard[2];
            var player=Regex.Match(entry.Token,@"^P([1-9][0-9]*)_(.+)$");
            if(player.Success&&int.Parse(player.Groups[1].Value) is int p&&p>=1&&p<=joined)
            {
                string kind=player.Groups[2].Value;
                string? joystick=Joystick(p,kind,entry.Analog);
                if(joystick!=null)standard=Or(joystick,standard);
            }
            var coin=Regex.Match(entry.Token,@"^(START|COIN)([1-4])$");
            if(coin.Success&&int.Parse(coin.Groups[2].Value)<=joined)standard=Or("JOYCODE_"+coin.Groups[2].Value+"_"+(coin.Groups[1].Value=="START"?"START":"SELECT"),standard);
            // Every observed general type is explicit. Other players and UI/service
            // keep keyboard defaults; they cannot inherit a pad assignment.
            var binding=new ExpectedBinding(entry.Token,entry.Analog,standard,increment,decrement);bindings.Add(binding);
            var port=new XElement("port",new XAttribute("type",entry.Token),new XElement("newseq",new XAttribute("type","standard"),standard));
            if(entry.Analog){port.Add(new XElement("newseq",new XAttribute("type","increment"),increment));port.Add(new XElement("newseq",new XAttribute("type","decrement"),decrement));}
            input.Add(port);
        }
        string defaultXml=Document("default",input);
        return new(controllerXml,defaultXml,bindings.AsReadOnly(),Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(controllerXml+"\n"+defaultXml))));
    }
    private static string? Joystick(int player,string type,bool analog)
    {
        string prefix="JOYCODE_"+player+"_";
        if(analog)
        {
            string axis=type switch{"PEDAL"=>"SLIDER1","PEDAL2"=>"SLIDER2","PEDAL3"=>"YAXIS","AD_STICK_Z"=>"ZAXIS",_=>type.EndsWith("_Y")||type.EndsWith("_V")?"YAXIS":"XAXIS"};
            return prefix+axis;
        }
        var direction=Regex.Match(type,@"^JOYSTICK(LEFT|RIGHT)?_(UP|DOWN|LEFT|RIGHT)$");
        if(direction.Success)
        {
            bool right=direction.Groups[1].Value=="RIGHT",vertical=direction.Groups[2].Value is "UP"or"DOWN";
            string axis=right?(vertical?"RZAXIS":"ZAXIS"):(vertical?"YAXIS":"XAXIS"),way=direction.Groups[2].Value;
            // MAME's directional LEFT/RIGHT/UP/DOWN modifiers invoke its paired
            // primary X/Y joystick map. Other axes require NEG/POS modifiers.
            string modifier=right?(way is "UP"or"LEFT"?"NEG":"POS"):way;
            string result=prefix+axis+"_"+modifier+"_SWITCH";
            if(!right)result+=" OR "+prefix+"HAT1"+way;
            return result;
        }
        var button=Regex.Match(type,@"^BUTTON([1-9][0-9]*)$");
        if(button.Success)return int.Parse(button.Groups[1].Value)<=10?prefix+"BUTTON"+button.Groups[1].Value:null;
        return type switch{"START"=>prefix+"START","SELECT"=>prefix+"SELECT",_=>null};
    }
    private static string Or(string joystick,string keyboard)=>keyboard=="NONE"?joystick:joystick+" OR "+keyboard;
    public static string FilterGame(string machine,string? source)
    {
        if(!Regex.IsMatch(machine,@"^[a-z0-9_]+$"))throw new ArgumentException("Machine name invalid");
        if(source==null)return Document(machine,new XElement("input"));
        if(source.Length>4*1024*1024)throw new InvalidDataException("Game cfg too large");
        using var reader=XmlReader.Create(new StringReader(source),new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=4*1024*1024});
        var doc=XDocument.Load(reader,LoadOptions.PreserveWhitespace);
        if(doc.Root?.Name!="mameconfig"||(string?)doc.Root.Attribute("version")!="10"||doc.Root.Elements("system").Count()!=1||(string?)doc.Root.Element("system")!.Attribute("name")!=machine)throw new InvalidDataException("One matching MAME game config required");
        foreach(var input in doc.Root.Element("system")!.Elements("input"))
        {
            if(input.Elements("mapdevice").Any())throw new InvalidDataException("Device remaps belong only in reviewed controller config");
            foreach(var port in input.Elements("port"))foreach(var seq in port.Elements("newseq").ToArray())seq.Remove();
        }
        if(doc.Descendants("newseq").Any())throw new InvalidDataException("Unknown binding location");
        return doc.ToString(SaveOptions.DisableFormatting);
    }
    private static string Document(string name,XElement input)=>new XDocument(new XDeclaration("1.0","utf-8",null),new XElement("mameconfig",new XAttribute("version","10"),new XElement("system",new XAttribute("name",name),input))).ToString();
    private static DefaultTable Load()
    {
        using var stream=typeof(MameProfile).Assembly.GetManifestResourceStream("ControllerLobby.Mame.Mame0279Defaults.json")??throw new InvalidDataException("MAME defaults absent");
        var table=JsonSerializer.Deserialize<DefaultTable>(stream)??throw new InvalidDataException("MAME defaults empty");
        if(table.Types.Length!=758||table.Types.Select(t=>t.Token).Distinct(StringComparer.Ordinal).Count()!=758||table.Types.Any(t=>t.Keyboard.Length!=3||t.Keyboard.Any(s=>s!="NONE"&&!Regex.IsMatch(s,@"^(?:KEYCODE_[A-Z0-9_]+|NOT|OR)(?: (?:KEYCODE_[A-Z0-9_]+|NOT|OR))*$"))))throw new InvalidDataException("Pinned defaults malformed");
        return table;
    }
}
