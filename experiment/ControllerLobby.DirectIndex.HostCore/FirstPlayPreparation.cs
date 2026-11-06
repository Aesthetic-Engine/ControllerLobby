using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ControllerLobby.DirectIndex.Preparation;

namespace ControllerLobby.DirectIndex.HostCore;

public sealed record ProtectedFile(string Path,bool Present,string? Sha256,long Length)
{
    public static ProtectedFile Capture(string input)
    {
        string path=System.IO.Path.GetFullPath(input);
        for(string? ancestor=path;ancestor!=null;ancestor=System.IO.Path.GetDirectoryName(ancestor))
        {
            try {if(File.GetAttributes(ancestor).HasFlag(FileAttributes.ReparsePoint))throw new IOException("Reparse protected path");}
            catch(FileNotFoundException){}catch(DirectoryNotFoundException){}
        }
        try{using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);return new(path,true,Convert.ToHexString(SHA256.HashData(file)),file.Length);}
        catch(FileNotFoundException){return new(path,false,null,0);}catch(DirectoryNotFoundException){return new(path,false,null,0);}
    }
    public bool Unchanged()=>Capture(Path)==this;
}
public sealed record FirstPlayPrepared(PreparationResult Candidate,ProtectedFile OriginalSave,string SaveDirectory,string StateDirectory)
{
    public bool Preserved()=>OriginalSave.Unchanged()&&RetroArchPreparation.Preserved(Candidate.SourceConfigs,Candidate.Files);
    public bool OwnerPreserved()
    {
        var reader=new ControllerLobby.RetroArch.DiskConfigReader();
        foreach(var input in Candidate.SourceConfigs)
        {
            var actual=reader.Read(input.Path);
            if(input.Present?actual.State!=ControllerLobby.RetroArch.ReadState.Present||actual.RawSha256!=input.Sha256:actual.State!=ControllerLobby.RetroArch.ReadState.Absent)return false;
        }
        string session=System.IO.Path.GetDirectoryName(SaveDirectory)!+System.IO.Path.DirectorySeparatorChar;
        return OriginalSave.Unchanged()&&Candidate.Files.Where(f=>!f.Path.StartsWith(session,StringComparison.OrdinalIgnoreCase)).All(f=>{var current=ProtectedFile.Capture(f.Path);return current.Present&&current.Sha256==f.Sha256&&current.Length==f.Length;});
    }
}

public static class FirstPlayPreparation
{
    public static FirstPlayPrepared Prepare(PreparationRequest request)
    {
        var candidate=RetroArchPreparation.Prepare(request);
        if(candidate.Specification==null)throw new InvalidDataException(candidate.Reason);
        string root=Path.GetFullPath(request.Launch.Root),main=Path.GetFullPath(request.Launch.MainConfig);
        // The inspected GoldenEye install has no cfg/rmp overrides. Do not silently
        // apply these extra write-boundary overrides to an unqualified custom chain.
        if (/*guard:firstplay-simple-chain*/ candidate.SourceConfigs.Any(s=>s.Present
            &&!s.Path.Equals(main,StringComparison.OrdinalIgnoreCase)
            &&(s.Path.EndsWith(".rmp",StringComparison.OrdinalIgnoreCase)
                ||s.Path.EndsWith(".cfg",StringComparison.OrdinalIgnoreCase)&&!s.Path.Equals(System.IO.Path.Combine(root,"retroarch-core-options.cfg"),StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("First-play custom cfg/remap chain is not qualified");
        string text=File.ReadAllText(main,Encoding.UTF8).Replace("\r\n","\n");
        string Value(string key)
        {
            var match=Regex.Match(text,"(?m)^[ \\t]*"+Regex.Escape(key)+"[ \\t]*=[ \\t]*\"([^\"]*)\"[ \\t]*(?:#.*)?$");
            if(!match.Success)throw new InvalidDataException("Explicit first-play path/setting required: "+key);
            return match.Groups[1].Value;
        }
        string Resolve(string key)
        {
            string value=Value(key);
            if(value.StartsWith(":/",StringComparison.Ordinal)||value.StartsWith(":\\",StringComparison.Ordinal))return System.IO.Path.GetFullPath(System.IO.Path.Combine(root,value[2..]));
            if(System.IO.Path.IsPathFullyQualified(value))return System.IO.Path.GetFullPath(value);
            throw new InvalidDataException("First-play path is unresolved: "+key);
        }
        _=Resolve("system_directory");_=Resolve("joypad_autoconfig_dir");_=Resolve("savestate_directory");
        string saveRoot=Resolve("savefile_directory");
        if (/*guard:firstplay-save-location*/ (Value("savefiles_in_content_dir")!="false"||Value("sort_savefiles_by_content_enable")!="false"))
            throw new InvalidDataException("First-play content-based save location is not qualified");
        string sorted=Value("sort_savefiles_enable");
        if(sorted is not("true"or"false"))throw new InvalidDataException("Unresolved save sorting");
        if(sorted=="true")saveRoot=System.IO.Path.Combine(saveRoot,request.Launch.CoreName);
        string basename=System.IO.Path.GetFileNameWithoutExtension(request.Launch.Content);
        var original=ProtectedFile.Capture(System.IO.Path.Combine(saveRoot,basename+".srm"));
        string session=System.IO.Path.GetFullPath(request.SessionDirectory);
        string saves=System.IO.Path.Combine(session,"saves"),states=System.IO.Path.Combine(session,"states");
        var directories=new Dictionary<string,string>{["savefile_directory"]=saves,["savestate_directory"]=states,["runtime_log_directory"]=System.IO.Path.Combine(session,"runtime"),["log_dir"]=System.IO.Path.Combine(session,"logs"),["screenshot_directory"]=System.IO.Path.Combine(session,"screenshots"),["cache_directory"]=System.IO.Path.Combine(session,"cache"),["recording_output_directory"]=System.IO.Path.Combine(session,"recordings")};
        foreach(string directory in directories.Values)Directory.CreateDirectory(directory);
        if(original.Present)
        {
            string copy=System.IO.Path.Combine(saves,basename+".srm");
            using(var input=new FileStream(original.Path,FileMode.Open,FileAccess.Read,FileShare.Read))
            using(var output=new FileStream(copy,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {input.CopyTo(output);output.Flush(true);}
            if(!original.Unchanged()||ProtectedFile.Capture(copy).Sha256!=original.Sha256)throw new IOException("Source save changed during copy");
        }
        string append=System.IO.Path.Combine(session,"retroarch-append.cfg");
        var additions=new StringBuilder();
        foreach(var pair in directories)additions.AppendLine(pair.Key+" = \""+pair.Value.Replace('\\','/')+"\"");
        foreach(string key in new[]{"history_list_enable","content_runtime_log","content_runtime_log_aggregate","sort_savefiles_enable","sort_savestates_enable","sort_savefiles_by_content_enable","sort_savestates_by_content_enable","savefiles_in_content_dir","savestates_in_content_dir","savestate_auto_load","savestate_auto_save","log_to_file_timestamp"})additions.AppendLine(key+" = \"false\"");
        additions.AppendLine("log_to_file = \"true\"");
        // This artifact was exclusively created by the accepted preparer in this
        // invocation. Extend it before any native factory/launch authority exists.
        using(var file=new FileStream(append,FileMode.Append,FileAccess.Write,FileShare.None)){file.Write(new UTF8Encoding(false,true).GetBytes(additions.ToString()));file.Flush(true);}
        var pin=ProtectedFile.Capture(append);
        var files=candidate.Files.Select(f=>f.Path.Equals(append,StringComparison.OrdinalIgnoreCase)?new FileStamp(append,pin.Sha256!,pin.Length):f).ToArray();
        var result=new FirstPlayPrepared(candidate with{Files=Array.AsReadOnly(files)},original,saves,states);
        if (/*guard:firstplay-final-preservation*/ !result.Preserved())throw new IOException("First-play inputs changed during preparation");
        return result;
    }
}
