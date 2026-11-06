using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ControllerLobby.DirectIndex.Selection;

namespace ControllerLobby.Mame;

public sealed record MameDevice(string Class,int Index,string Id,string Name);
public sealed record MameField(string Tag,uint Mask,string Token,int Player,string Name,string Standard,string Increment,string Decrement);
public sealed record MameListingProof(string ProfileSha256,IReadOnlyList<MameDevice> Devices,IReadOnlyList<MameField> Fields,IReadOnlyList<string> UnresolvedUnusedTypes,IReadOnlyDictionary<string,string> FileHashes);

// A consumed-binding check for this exact first-play game, not a product permit.
// Unused types may lack items (e.g. pad BUTTON9); required tmnt2 bindings may not.
public static class MameListing
{
    private static readonly UTF8Encoding utf8=new(false,true);
    public static MameListingProof Validate(string directory,SelectionSeal selection,GeneratedProfile profile)
    {
        if(selection.ConnectedMask!=15||selection.PlayerIndices.Count!=4||selection.PlayerIndices.Distinct().Count()!=4||selection.PlayerIndices.Any(i=>i is <0 or >3))throw new InvalidDataException("Four fixed selected endpoints required");
        var canonical=MameProfile.Generate(selection);
        if(/*guard:mame-canonical-profile*/profile.Sha256!=canonical.Sha256||profile.ControllerXml!=canonical.ControllerXml||profile.DefaultXml!=canonical.DefaultXml||!profile.Bindings.SequenceEqual(canonical.Bindings))throw new InvalidDataException("Listing must bind the generated profile for this selection");
        var hashes=new Dictionary<string,string>(StringComparer.Ordinal);
        string Read(string name)
        {
            string path=Path.Combine(Path.GetFullPath(directory),name);
            for(string? current=path;current!=null;current=Path.GetDirectoryName(current))
                if(File.Exists(current)||Directory.Exists(current))if(File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))throw new IOException("Reparse listing refused");
            var info=new FileInfo(path);if(!info.Exists||info.Length>2*1024*1024)throw new InvalidDataException("Missing/oversized MAME listing");
            byte[] data=File.ReadAllBytes(path);hashes.Add(name,Convert.ToHexString(SHA256.HashData(data)));return utf8.GetString(data);
        }
        if(/*guard:mame-listing-completion*/Read("metadata.complete").TrimEnd('\r','\n')!="MAME_METADATA_20726_COMPLETE")throw new InvalidDataException("MAME listing incomplete");
        static string[][] Rows(string text,int columns)
        {
            var rows=text.Replace("\r\n","\n").TrimEnd('\n').Split('\n').Select(s=>s.Split('\t')).ToArray();
            if(rows.Any(r=>r.Length!=columns||r.Any(s=>s.Contains('\r'))))throw new InvalidDataException("Malformed MAME listing row");return rows;
        }
        var devices=Rows(Read("devices.tsv"),4).Select(r=>new MameDevice(r[0],Number(r[1]),r[2],r[3])).ToArray();
        var joysticks=devices.Where(d=>d.Class=="joystick").ToArray();
        if(/*guard:mame-listing-devices*/joysticks.Length!=4||devices.Any(d=>d.Class is not("keyboard"or"joystick"))||joysticks.Select(d=>d.Index).Distinct().Count()!=4||joysticks.Select(d=>d.Id).Distinct(StringComparer.Ordinal).Count()!=4)throw new InvalidDataException("Unexpected MAME input devices");
        for(int p=0;p<4;p++)
            if(/*guard:mame-listing-map*/!joysticks.Any(d=>d.Index==p&&d.Id=="XInput Player "+(selection.PlayerIndices[p]+1)))throw new InvalidDataException("MAME device/player mapping differs from selection");
        var expected=profile.Bindings.ToDictionary(b=>b.Token,StringComparer.Ordinal);
        var types=Rows(Read("types.tsv"),6);
        if(/*guard:mame-listing-types*/types.Length!=expected.Count||types.Select(r=>r[0]).Distinct(StringComparer.Ordinal).Count()!=types.Length||types.Any(r=>!expected.ContainsKey(r[0])||r[2]!=(expected[r[0]].Analog?"1":"0")))throw new InvalidDataException("MAME general-type listing differs from pinned build");
        var general=new Dictionary<string,ExpectedBinding>(StringComparer.Ordinal);
        foreach(var row in types)
        {
            var playerToken=Regex.Match(row[0],@"^P([1-9][0-9]*)_");int typePlayer=playerToken.Success?int.Parse(playerToken.Groups[1].Value,CultureInfo.InvariantCulture)-1:0;
            if(/*guard:mame-type-player*/Number(row[1])!=typePlayer)throw new InvalidDataException("MAME type/player metadata disagrees");
            var binding=new ExpectedBinding(row[0],row[2]=="1",Sequence(row[3]),Sequence(row[4]),Sequence(row[5]));
            OwnPlayer(binding.Token,binding.Standard);OwnPlayer(binding.Token,binding.Increment);OwnPlayer(binding.Token,binding.Decrement);general.Add(binding.Token,binding);
        }
        var fields=Rows(Read("fields.tsv"),8).Select(r=>new MameField(r[0],(uint)Number(r[1]),r[2],Number(r[3]),r[4],Sequence(r[5]),Sequence(r[6]),Sequence(r[7]))).ToArray();
        var required=RequiredFields();
        if(/*guard:mame-listing-fields*/fields.Length!=33||fields.Select(f=>f.Token).Distinct(StringComparer.Ordinal).Count()!=33||fields.Any(f=>!required.Contains(f.Token))||fields.Select(f=>(f.Tag,f.Mask)).Distinct().Count()!=33)throw new InvalidDataException("Expected all 33 tmnt2 input fields exactly once");
        foreach(var field in fields)
        {
            var identity=Identity(field.Token);
            if(/*guard:mame-field-identity*/field.Tag!=identity.Tag||field.Mask!=identity.Mask||field.Player!=identity.Player)throw new InvalidDataException("MAME field identity differs: "+field.Token);
            var wanted=expected[field.Token];
            if(/*guard:mame-required-bindings*/field.Standard!=wanted.Standard||field.Increment!="NONE"||field.Decrement!="NONE")throw new InvalidDataException("MAME effective field binding differs: "+field.Token);
            OwnPlayer(field.Token,field.Standard);
        }
        var requiredGeneral=required.Concat(Enumerable.Range(1,4).SelectMany(p=>new[]{"START"+p,"P"+p+"_START","P"+p+"_SELECT"})).Concat(profile.Bindings.Where(b=>b.Token.StartsWith("UI_",StringComparison.Ordinal)||b.Token.StartsWith("SERVICE",StringComparison.Ordinal)||b.Token=="TILT").Select(b=>b.Token)).ToHashSet(StringComparer.Ordinal);
        foreach(string token in requiredGeneral)
            if(/*guard:mame-required-general*/general[token].Standard!=expected[token].Standard)throw new InvalidDataException("MAME effective general binding differs: "+token);
        var unresolved=general.Values.Where(b=>!requiredGeneral.Contains(b.Token)&&(b.Standard!=expected[b.Token].Standard||b.Increment!=expected[b.Token].Increment||b.Decrement!=expected[b.Token].Decrement)).Select(b=>b.Token).ToArray();
        return new(profile.Sha256,Array.AsReadOnly(devices),Array.AsReadOnly(fields),Array.AsReadOnly(unresolved),new System.Collections.ObjectModel.ReadOnlyDictionary<string,string>(hashes));
    }
    private static int Number(string value)=>int.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out int result)?result:throw new InvalidDataException("Invalid MAME numeric metadata");
    private static (string Tag,uint Mask,int Player) Identity(string token)
    {
        if(token=="SERVICE")return(":EEPROM",128,0);
        var coin=Regex.Match(token,@"^(COIN|SERVICE)([1-4])$");
        if(coin.Success){int p=int.Parse(coin.Groups[2].Value,CultureInfo.InvariantCulture);return(":COINS",1u<<(p-1+(coin.Groups[1].Value=="SERVICE"?4:0)),0);}
        var player=Regex.Match(token,@"^P([1-4])_(.+)$");
        if(!player.Success)throw new InvalidDataException("Unknown tmnt2 field");
        int number=int.Parse(player.Groups[1].Value,CultureInfo.InvariantCulture);
        uint mask=player.Groups[2].Value switch{"JOYSTICK_LEFT"=>1,"JOYSTICK_RIGHT"=>2,"JOYSTICK_UP"=>4,"JOYSTICK_DOWN"=>8,"BUTTON1"=>16,"BUTTON2"=>32,_=>throw new InvalidDataException("Unknown tmnt2 action")};
        return(":P"+number,mask,number-1);
    }
    private static HashSet<string> RequiredFields()
    {
        var result=new HashSet<string>(StringComparer.Ordinal){"SERVICE"};
        for(int p=1;p<=4;p++)
        {
            result.Add("COIN"+p);result.Add("SERVICE"+p);
            foreach(string action in new[]{"JOYSTICK_UP","JOYSTICK_DOWN","JOYSTICK_LEFT","JOYSTICK_RIGHT","BUTTON1","BUTTON2"})result.Add("P"+p+"_"+action);
        }
        return result;
    }
    private static string Sequence(string source)
    {
        string value=Regex.Replace(source.Trim(),@"\s+"," ");if(value.Length==0)value="NONE";
        if(value!="NONE"&&!Regex.IsMatch(value,@"^(?:JOYCODE_[1-9][0-9]*_[A-Z0-9_]+|KEYCODE_[A-Z0-9_]+|OR|NOT)(?: (?:JOYCODE_[1-9][0-9]*_[A-Z0-9_]+|KEYCODE_[A-Z0-9_]+|OR|NOT))*$"))throw new InvalidDataException("Unqualified input sequence token");return value;
    }
    private static void OwnPlayer(string token,string sequence)
    {
        var match=Regex.Match(token,@"^(?:P([1-9][0-9]*)_|(?:START|COIN)([1-9][0-9]*)$)");
        int player=match.Success?int.Parse(match.Groups[1].Success?match.Groups[1].Value:match.Groups[2].Value,CultureInfo.InvariantCulture):0;
        foreach(Match code in Regex.Matches(sequence,@"JOYCODE_([1-9][0-9]*)_"))
            if(/*guard:mame-general-isolation*/player is <1 or >4||int.Parse(code.Groups[1].Value,CultureInfo.InvariantCulture)!=player)throw new InvalidDataException("Cross-player or UI/service joystick binding: "+token);
    }
}
