using System.Text;
using ControllerLobby.DirectIndex.HostCore;
using ControllerLobby.DirectIndex.Preparation;
using ControllerLobby.DirectIndex.Selection;
using ControllerLobby.Mame;

namespace ControllerLobby.MameTrial.HostCore;

// A tree stamp includes absence and the full relative file set. New/deleted
// owner save files therefore invalidate preservation, as do changed bytes.
public sealed record TreeStamp(string Root,bool Present,IReadOnlyList<ProtectedFile> Files)
{
    public static TreeStamp Capture(string input)
    {
        string root=Path.GetFullPath(input);
        _=ProtectedFile.Capture(Path.Combine(root,".path-check"));
        if(File.Exists(root))throw new IOException("Expected a directory");
        if(!Directory.Exists(root))return new(root,false,Array.AsReadOnly(Array.Empty<ProtectedFile>()));
        var files=new List<ProtectedFile>();
        void Walk(string directory)
        {
            foreach(string path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.OrdinalIgnoreCase))
            {
                if(File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))throw new IOException("Reparse save entry");
                if(Directory.Exists(path))Walk(path);else files.Add(ProtectedFile.Capture(path));
            }
        }
        Walk(root);return new(root,true,files.AsReadOnly());
    }
    public bool Unchanged()
    {
        var current=Capture(Root);
        return current.Present==Present&&current.Files.SequenceEqual(Files);
    }
}

public sealed record MamePreparationRequest(string OwnerRoot,string Executable,string RomDirectory,string SessionDirectory,SelectionSeal Selection);
public sealed record MamePrepared(string Executable,string WorkingDirectory,IReadOnlyList<string> Arguments,string CompleteCommandLine,
    GeneratedProfile Profile,IReadOnlyList<ProtectedFile> SourceFiles,TreeStamp OwnerNvram,IReadOnlyList<ProtectedFile> LaunchFiles)
{
    public bool OwnerPreserved()=>SourceFiles.All(f=>f.Unchanged())&&OwnerNvram.Unchanged();
    public bool BeforeLaunchCurrent()=>OwnerPreserved()&&LaunchFiles.All(f=>f.Unchanged());
}

public static class MamePreparation
{
    public const string Machine="tmnt2";
    public static MamePrepared Prepare(MamePreparationRequest request,bool metadata=false,string? metadataScript=null)
    {
        string owner=Path.GetFullPath(request.OwnerRoot),session=Path.GetFullPath(request.SessionDirectory);
        bool Within(string path,string root)=>path.Equals(root,StringComparison.OrdinalIgnoreCase)||path.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
        if(/*guard:mame-session-boundary*/Within(session,owner)||Within(owner,session)||Directory.Exists(session)||File.Exists(session))throw new IOException("New separate session directory required");
        _=ProtectedFile.Capture(Path.Combine(session,".path-check"));
        var profile=MameProfile.Generate(request.Selection);
        string[] sourceNames=[request.Executable,Path.Combine(request.RomDirectory,Machine+".zip"),Path.Combine(owner,"mame.ini"),Path.Combine(owner,"cfg","default.cfg"),Path.Combine(owner,"cfg",Machine+".cfg")];
        var sources=sourceNames.Select(ProtectedFile.Capture).ToArray();
        if(!sources[0].Present||!sources[1].Present)throw new IOException("Installed MAME and tmnt2 ROM required");
        var nvram=TreeStamp.Capture(Path.Combine(owner,"nvram",Machine));
        string? game=sources[4].Present?File.ReadAllText(sources[4].Path,new UTF8Encoding(false,true)):null;
        string filtered=MameProfile.FilterGame(Machine,game);
        Directory.CreateDirectory(session);
        var launchFiles=new List<ProtectedFile>();
        void Write(string name,string text)
        {
            string path=Path.Combine(session,name);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read)){file.Write(new UTF8Encoding(false,true).GetBytes(text));file.Flush(true);}
            launchFiles.Add(ProtectedFile.Capture(path));
        }
        Write("controller/session.cfg",profile.ControllerXml);Write("cfg/default.cfg",profile.DefaultXml);Write("cfg/"+Machine+".cfg",filtered);
        foreach(var source in nvram.Files)
        {
            string dest=Path.Combine(session,"nvram",Machine,Path.GetRelativePath(nvram.Root,source.Path));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using(var input=new FileStream(source.Path,FileMode.Open,FileAccess.Read,FileShare.Read))
            using(var output=new FileStream(dest,FileMode.CreateNew,FileAccess.Write,FileShare.Read)){input.CopyTo(output);output.Flush(true);}
            var copied=ProtectedFile.Capture(dest);
            if(/*guard:mame-seed-copy*/copied.Sha256!=source.Sha256||!source.Unchanged())throw new IOException("Owner save changed during copy");
            launchFiles.Add(copied);
        }
        var args=new List<string>{Machine,"-noreadconfig","-rompath",Path.GetFullPath(request.RomDirectory),"-skip_gameinfo","-noplugins","-noautosave","-joystick","-nomouse","-nolightgun","-keyboardprovider","dinput","-mouseprovider","none","-lightgunprovider","none","-joystickprovider","xinput","-output","none","-ctrlrpath",Path.Combine(session,"controller"),"-ctrlr","session"};
        foreach(var pair in new Dictionary<string,string>{{"homepath","home"},{"inipath","ini"},{"cfg_directory","cfg"},{"nvram_directory","nvram"},{"state_directory","state"},{"input_directory","input"},{"snapshot_directory","snap"},{"diff_directory","diff"},{"comment_directory","comments"},{"share_directory","share"},{"cheatpath","cheat"}})
        {
            string directory=Path.Combine(session,pair.Value);Directory.CreateDirectory(directory);args.Add("-"+pair.Key);args.Add(directory);
        }
        if(metadata)
        {
            if(metadataScript==null)throw new ArgumentException("Pinned metadata script required");
            Write("metadata.lua",metadataScript);
            args.AddRange(["-video","none","-sound","none","-autoboot_delay","0","-autoboot_script",Path.Combine(session,"metadata.lua")]);
        }
        // The attended trial uses the built-in GDI renderer so a private working
        // directory does not depend on external shader/chain asset paths.
        else args.AddRange(["-video","gdi","-window","-nomaximize"]);
        var result=new MamePrepared(Path.GetFullPath(request.Executable),session,args.AsReadOnly(),RetroArchPreparation.BuildCommandLine(Path.GetFullPath(request.Executable),args),profile,Array.AsReadOnly(sources),nvram,launchFiles.AsReadOnly());
        if(/*guard:mame-preparation-current*/!result.BeforeLaunchCurrent())throw new IOException("MAME inputs changed during preparation");
        return result;
    }
}
