#nullable enable
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ControllerLobby.DirectIndex.HostCore;
using ControllerLobby.DirectIndex.Selection;
using ControllerLobby.DuckStation;

namespace ControllerLobby.BigBox;

// The launch copy: a second set of DuckStation program files beside this host, pinned file by file.
internal sealed record DuckStationCopy(string CopyRoot,PayloadFile[] Inventory);
internal readonly record struct DuckProcess(int Pid,string? Image);

// Disk and process side of the DuckStation rules. The owner's files are only ever read here; every write goes to
// the launch copy. Qualified against DuckStation 0.1-9903-g8f0c9dd17.
internal static class DuckStationLaunch
{
    public const string Kind="duckstation",ExeName="duckstation-qt-x64-ReleaseLTCG.exe",CopyFolder="DuckStationCopy";
    public const string RunningMessage="DuckStation is already running. Close it, then start the game again.";
    internal sealed record Owner(string DataRoot,OwnerFile Settings,OwnerFile[] PerGame,OwnerFile Custom,string GameSettings,string InputProfiles);

    public static string CopyExecutable(NormalManifest m)=>Path.Combine(m.DuckStation!.CopyRoot,ExeName);

    // Manifest shape and the whole pinned inventory, DLLs included. A changed copy is a damaged installation.
    public static ProtectedFile[] Pin(string packageRoot,NormalManifest m)
    {
        var copy=m.DuckStation??throw new IOException("DuckStation copy missing from manifest");
        string expected=Path.Combine(Path.GetFullPath(packageRoot),CopyFolder);
        if(copy.Inventory==null||copy.Inventory.Length==0||/*guard:copy-root-fixed*/!Path.GetFullPath(copy.CopyRoot).Equals(expected,StringComparison.OrdinalIgnoreCase))throw new IOException("Invalid DuckStation copy");
        var pins=new List<ProtectedFile>();bool exe=false,portable=false;
        foreach(var item in copy.Inventory)
        {
            bool marker=item.Path=="portable.txt";
            if(!marker&&/*guard:inventory-paths*/!DuckStationSession.IsProgramFile(item.Path))throw new IOException("Invalid DuckStation copy path");
            var file=ProtectedFile.Capture(Path.Combine(expected,item.Path));
            if(!file.Present||/*guard:inventory-hash*/file.Sha256!=item.Sha256)throw new IOException("DuckStation copy changed");
            pins.Add(file);exe|=item.Path.Equals(ExeName,StringComparison.OrdinalIgnoreCase);portable|=marker;
        }
        if(!exe||!portable)throw new IOException("Invalid DuckStation copy");
        // The copy must run the same program the owner installed.
        if(/*guard:copy-exe-matches-pin*/pins.First(p=>Path.GetFileName(p.Path).Equals(ExeName,StringComparison.OrdinalIgnoreCase)).Sha256!=m.ExecutableSha256)throw new IOException("DuckStation copy changed");
        return pins.ToArray();
    }

    private static OwnerFile ReadFile(string path)
    {
        try{return File.Exists(path)?new(Path.GetFileName(path),FileState.Present,File.ReadAllText(path)):new(Path.GetFileName(path),FileState.Absent);}
        catch(Exception e)when(e is IOException or UnauthorizedAccessException){return new(Path.GetFileName(path),FileState.Unreadable);}
    }
    private static OwnerFile[] ReadFolder(string folder)
    {
        try{return Directory.Exists(folder)?Directory.GetFiles(folder,"*.ini").OrderBy(p=>p,StringComparer.OrdinalIgnoreCase).Select(ReadFile).ToArray():[];}
        catch(Exception e)when(e is IOException or UnauthorizedAccessException){return [new(folder,FileState.Unreadable)];}
    }
    private static string? Documents(){string d=Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);return d.Length==0?null:d;}

    // Read-only look at the owner's files. Null when DuckStation's data folder cannot be determined.
    public static Owner? ReadOwner(NormalManifest m,Func<string?>? documents=null)
    {
        string emulator=Path.GetDirectoryName(Path.GetFullPath(m.Executable))!;
        string? root=DuckStationSession.DataRoot(emulator,File.Exists,documents??Documents);
        if(root==null)return null;
        var settings=ReadFile(Path.Combine(root,"settings.ini"));
        string games=Path.Combine(root,"gamesettings"),profiles=Path.Combine(root,"inputprofiles");
        if(settings.Text!=null){games=DuckStationSession.OwnerFolder(settings.Text,root,"GameSettings")??games;profiles=DuckStationSession.OwnerFolder(settings.Text,root,"InputProfiles")??profiles;}
        return new(root,settings,ReadFolder(games),ReadFile(Path.Combine(root,"custom_properties.ini")),games,profiles);
    }
    public static Qualification Qualify(Owner? owner)=>owner==null?new(Mappable.No,"no-data-folder",[]):DuckStationSession.Qualify(owner.Settings,owner.PerGame,owner.Custom);

    public static string Notice(string reason,int ports=0,int players=0)=>(reason switch
    {
        "custom-titles"=>"Join order is not available because this DuckStation uses custom game titles.",
        "per-game-controller-override"=>"Join order is not available because a DuckStation game has its own controller settings.",
        "too-many-players"=>"DuckStation is set up for "+ports+(ports==1?" controller":" controllers")+", and "+players+" players joined.",
        _=>"DuckStation's controller settings can't take lobby join order.",
    })+" "+NormalPlan.ExistingNotice;

    // After Start has committed the selection: snapshot, qualify the snapshot, write the session settings.
    public static NormalPlan Prepare(NormalManifest m,string[] original,SelectionSeal selection,Func<string?>? documents=null,Action? afterSnapshot=null)
    {
        NormalPlan Fallback(string reason,int ports=0)=>NormalPlan.Existing(m,original,Notice(reason,ports,selection.PlayerIndices.Count));
        var owner=ReadOwner(m,documents);var first=Qualify(owner);
        if(owner==null||!first.Ok)return Fallback(first.Reason);
        if(/*guard:ports-before-snapshot*/selection.PlayerIndices.Count>first.Ports)return Fallback("too-many-players",first.Ports);
        string copy=Path.GetFullPath(m.DuckStation!.CopyRoot),games=Path.Combine(copy,DuckStationSession.SessionGameSettings),profiles=Path.Combine(copy,DuckStationSession.SessionInputProfiles);
        // Nothing is created, deleted, copied or written until the destination is known to be the copy itself.
        if(/*guard:snapshot-destination-safe*/!SafeDestination(copy,[owner.GameSettings,owner.InputProfiles]))return Fallback("unavailable");
        Snapshot(owner.GameSettings,games,copy);Snapshot(owner.InputProfiles,profiles,copy);afterSnapshot?.Invoke();
        // Qualify what DuckStation will actually load: the snapshot, and the owner's files read again now.
        var settings=ReadFile(Path.Combine(owner.DataRoot,"settings.ini"));
        var second=DuckStationSession.Qualify(settings,ReadFolder(games),ReadFile(Path.Combine(owner.DataRoot,"custom_properties.ini")));
        if(/*guard:requalify-snapshot*/!second.Ok||settings.Text!=owner.Settings.Text||!second.PadSlots.SequenceEqual(first.PadSlots))return Fallback(second.Ok?"settings-changed":second.Reason);
        string session=Path.Combine(copy,"settings.ini");
        if(!PlainTree(copy))return Fallback("unavailable");
        // A fresh file replaces the directory entry, so a hard link left at settings.ini never carries the write
        // to the file it shares data with.
        string fresh=Path.Combine(copy,"settings.ini."+Guid.NewGuid().ToString("N")+".tmp");
        File.WriteAllText(fresh,DuckStationSession.Build(settings.Text!,second,selection.PlayerIndices.ToArray(),owner.DataRoot,copy));
        try{/*guard:replace-not-overwrite*/File.Move(fresh,session,overwrite:true);}finally{if(File.Exists(fresh))File.Delete(fresh);}
        var inputs=new List<ProtectedFile>{ProtectedFile.Capture(session),ProtectedFile.Capture(Path.Combine(owner.DataRoot,"settings.ini"))};
        inputs.AddRange(Directory.GetFiles(games).Select(ProtectedFile.Capture));
        return new(original.ToArray(),true,"Player assignments applied for this session. Game saves use your normal location.",inputs.ToArray()){Executable=CopyExecutable(m)};
    }

    private static bool Reparse(string path)
    {
        try{return (File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0;}
        catch(FileNotFoundException){return false;}catch(DirectoryNotFoundException){return false;}
    }
    // No link or junction anywhere the host writes: the copy root and its ancestors, the session folders, the
    // snapshot folders and the copy's settings file. A junction at any of them could point into the owner's files.
    private static bool PlainTree(string copy)
    {
        for(string? ancestor=copy;ancestor!=null;ancestor=Path.GetDirectoryName(ancestor))if(/*guard:destination-ancestry*/Reparse(ancestor))return false;
        string session=Path.Combine(copy,"session");
        return !new[]{session,Path.Combine(copy,DuckStationSession.SessionGameSettings),Path.Combine(copy,DuckStationSession.SessionInputProfiles),Path.Combine(copy,"settings.ini")}.Any(Reparse)
            &&!(File.Exists(session)||File.Exists(Path.Combine(copy,DuckStationSession.SessionGameSettings))||File.Exists(Path.Combine(copy,DuckStationSession.SessionInputProfiles)));
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle CreateFileW(string name,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle,[Out]char[] path,uint length,uint flags);
    // The directory a path really names, with every link and junction resolved. Null when it cannot be opened.
    internal static string? FinalDirectory(string path)
    {
        using var handle=CreateFileW(path,0,7,IntPtr.Zero,3,0x02000000,IntPtr.Zero); // share all, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS
        if(handle.IsInvalid)return null;
        var buffer=new char[32768];uint length=GetFinalPathNameByHandleW(handle,buffer,(uint)buffer.Length,0);
        return length==0||length>=buffer.Length?null:new string(buffer,0,(int)length).TrimEnd('\\');
    }
    private static bool Within(string path,string parent)=>path.Equals(parent,StringComparison.OrdinalIgnoreCase)||path.StartsWith(parent+"\\",StringComparison.OrdinalIgnoreCase);
    // The copy must be a plain folder tree, and no source folder may be, contain, or sit inside the copy once links
    // are resolved; otherwise emptying a snapshot folder could delete the owner's files.
    internal static bool SafeDestination(string copy,IEnumerable<string> sources)
    {
        if(!Directory.Exists(copy)||!PlainTree(copy))return false;
        string? real=FinalDirectory(copy);if(real==null)return false;
        foreach(string source in sources)
        {
            if(!Directory.Exists(source))continue;
            string? from=FinalDirectory(source);
            if(from==null||/*guard:source-destination-separate*/Within(from,real)||Within(real,from))return false;
        }
        return true;
    }

    // The snapshot folders belong to the copy: emptied, then filled with the owner's current files.
    private static void Snapshot(string source,string destination,string copy)
    {
        Directory.CreateDirectory(destination);
        if(!PlainTree(copy)||!Within(FinalDirectory(destination)??"",FinalDirectory(copy)??"?"))throw new IOException("Snapshot folder is not inside the copy");
        foreach(string old in Directory.GetFiles(destination))File.Delete(old); // guard:snapshot-replaced
        if(!Directory.Exists(source))return;
        foreach(string file in Directory.GetFiles(source,"*.ini"))File.Copy(file,Path.Combine(destination,Path.GetFileName(file)));
    }

    private static IEnumerable<DuckProcess> Processes()
    {
        foreach(var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
        {
            string? image;
            try{image=p.MainModule?.FileName;}catch(Exception e)when(e is System.ComponentModel.Win32Exception or InvalidOperationException){image=null;}
            int pid=p.Id;p.Dispose();
            yield return new(pid,image);
        }
    }
    // Two DuckStations on one game share its memory card and save states, so no second one is started. A process
    // with that program name whose image cannot be read counts as running. except is the host's own held target.
    public static bool Running(NormalManifest m,Func<IEnumerable<DuckProcess>>? processes=null,int except=-1)
    {
        string owner=Path.GetFullPath(m.Executable),copy=Path.GetFullPath(CopyExecutable(m));
        return (processes??Processes)().Any(p=>/*guard:own-target-excluded*/p.Pid!=except&&(p.Image==null||p.Image.Equals(owner,StringComparison.OrdinalIgnoreCase)||p.Image.Equals(copy,StringComparison.OrdinalIgnoreCase)));
    }
}
