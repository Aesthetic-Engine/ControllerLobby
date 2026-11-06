using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace ControllerLobby.Setup;

public sealed class PackageBundle
{
    public string Root { get; }
    private readonly IReadOnlyDictionary<string,string> hashes;
    public string PackagePin=>hashes["package.json"];
    private PackageBundle(string root,IReadOnlyDictionary<string,string> hashes){Root=root;this.hashes=hashes;}
    public static string Sha(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes));
    public static string ShaFile(string path){using var file=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(file));}
    private static string Relative(string input)
    {
        string name=input.Replace('\\','/');
        if(Path.IsPathRooted(input)||name.Contains(':')||name.Split('/').Any(p=>p.Length==0||p is "." or ".."||p.EndsWith('.')||p.EndsWith(' ')))
            throw new InvalidDataException("Unsafe installer archive path");
        return name;
    }
    internal static void NoReparse(string path)
    {
        for(var p=new DirectoryInfo(Path.GetFullPath(path));p!=null;p=p.Parent)
            if(p.Exists&&(p.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Installer cache cannot use linked folders");
    }
    public void Validate()
    {
        NoReparse(Root);
        var actual=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(string name in hashes.Keys)
        {
            int slash=name.LastIndexOf('/');
            while(slash>=0){directories.Add(name[..slash]);slash=name.LastIndexOf('/',slash-1);}
        }
        var pending=new Stack<string>();pending.Push(Root);
        while(pending.Count>0)
        {
            string directory=pending.Pop();NoReparse(directory);
            foreach(string path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes=File.GetAttributes(path);
                if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked installer cache entry refused");
                string name=Path.GetRelativePath(Root,path).Replace('\\','/');
                if((attributes&FileAttributes.Directory)!=0)
                {
                    if(!directories.Contains(name))throw new IOException("Unexpected installer cache folder: "+name);
                    pending.Push(path);
                }
                else actual.Add(name);
            }
        }
        if(!actual.SetEquals(hashes.Keys))throw new IOException("Installer cache contains missing or unexpected files: "+Root); // guard:cache-membership
        foreach(var pair in hashes)
        {
            string path=Path.Combine(Root,pair.Key.Replace('/',Path.DirectorySeparatorChar));
            NoReparse(Path.GetDirectoryName(path)!);
            if(!File.Exists(path)||(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0||ShaFile(path)!=pair.Value)
                throw new IOException("Installer cache changed: "+pair.Key+". Remove this cache folder after closing Setup, then run Setup again: "+Root);
        }
    }
    public static PackageBundle OpenEmbedded()
    {
        Assembly assembly=typeof(PackageBundle).Assembly;
        string expected=assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a=>a.Key=="EmbeddedPackageSha256").Value??"";
        using var resource=assembly.GetManifestResourceStream("ControllerLobby.Package.zip")??throw new IOException("This setup executable has no bundled package");
        using var memory=new MemoryStream();resource.CopyTo(memory);
        string parent=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ControllerLobby","SetupCache");
        return Open(memory.ToArray(),expected,parent);
    }
    public static PackageBundle Open(byte[] bytes,string expected,string parent)
    {
        if(expected.Length!=64||Sha(bytes)!=expected.ToUpperInvariant())throw new InvalidDataException("Bundled installer hash does not match"); // guard:bundle-hash
        var files=new Dictionary<string,ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        using var archive=new ZipArchive(new MemoryStream(bytes),ZipArchiveMode.Read);
        foreach(var entry in archive.Entries)
        {
            if(entry.FullName.EndsWith('/'))continue;
            string name=Relative(entry.FullName); // guard:bundle-path
            if(((entry.ExternalAttributes>>16)&0xF000)==0xA000||(entry.ExternalAttributes&0x400)!=0||!files.TryAdd(name,entry))
                throw new InvalidDataException("Linked or duplicate installer archive entry");
        }
        foreach(string name in new[]{"PACKAGE_HASHES.json","package.json","bigbox_install.py","shim_install.py","setup_bridge.py","python/python.exe"})
            if(!files.ContainsKey(name))throw new InvalidDataException("Bundled installer is missing "+name);
        byte[] Read(ZipArchiveEntry entry){using var stream=entry.Open();using var data=new MemoryStream();stream.CopyTo(data);return data.ToArray();}
        byte[] inventoryBytes=Read(files["PACKAGE_HASHES.json"]);
        var hashes=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        using(var doc=JsonDocument.Parse(inventoryBytes))
            foreach(var property in doc.RootElement.EnumerateObject())
            {
                string name=Relative(property.Name),hash=property.Value.GetString()??"";
                if(hash.Length!=64||hash.Any(c=>!Uri.IsHexDigit(c))||!hashes.TryAdd(name,hash.ToUpperInvariant()))throw new InvalidDataException("Invalid installer inventory");
            }
        if(!hashes.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(files.Keys.Where(n=>!n.Equals("PACKAGE_HASHES.json",StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("Installer inventory does not cover the archive"); // guard:bundle-inventory
        hashes.Add("PACKAGE_HASHES.json",Sha(inventoryBytes));
        NoReparse(parent);Directory.CreateDirectory(parent);
        string root=Path.Combine(Path.GetFullPath(parent),expected.ToUpperInvariant());
        var result=new PackageBundle(root,hashes);
        if(Directory.Exists(root)){result.Validate();return result;}
        string stage=Path.Combine(Path.GetFullPath(parent),"stage-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);NoReparse(stage);
        foreach(var pair in files)
        {
            byte[] content=Read(pair.Value);
            if(Sha(content)!=hashes[pair.Key])throw new InvalidDataException("Bundled file hash mismatch: "+pair.Key);
            string target=Path.Combine(stage,pair.Key.Replace('/',Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);NoReparse(Path.GetDirectoryName(target)!);
            using var file=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None);file.Write(content);
        }
        NoReparse(parent);Directory.Move(stage,root);result.Validate();return result;
    }
}
