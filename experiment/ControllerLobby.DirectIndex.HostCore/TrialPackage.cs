using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ControllerLobby.DirectIndex.Native;

namespace ControllerLobby.DirectIndex.HostCore;

public sealed record TrialTarget(string Root,string Executable,string CoreLibrary,string Content,string MainConfig,string CoreName);
public sealed record PayloadFile(string Path,string Sha256);
public sealed record TrialManifest(int Schema,string ScopeDecision,int RequiredPlayers,int MaximumSeconds,
    ReaderBinding Reader,TrialTarget Target,ProtectedFile[] ProtectedInputs,PayloadFile[] Payload,string SourceManifestSha256);

// This is authority to attempt the explicitly approved, unqualified first-play
// experiment. It is never a diagnostic pass, Supported, Ready or product permit.
public sealed class TrialPackage
{
    public const string ApprovedScope="direct-index-four-player-v1";
    private readonly ProtectedFile[] protectedFiles;
    private readonly Dictionary<string,(long Length,long Ticks)> metadata;
    public TrialManifest Manifest{get;}
    public string ManifestPath{get;}
    public string ManifestSha256{get;}
    public string PackageRoot{get;}
    private TrialPackage(TrialManifest manifest,string path,string hash,ProtectedFile[] files)
    {
        Manifest=manifest;ManifestPath=path;ManifestSha256=hash;PackageRoot=Path.GetDirectoryName(path)!;protectedFiles=files;
        metadata=files.Where(p=>p.Present).ToDictionary(p=>p.Path,p=>{var f=new FileInfo(p.Path);return(f.Length,f.LastWriteTimeUtc.Ticks);},StringComparer.OrdinalIgnoreCase);
    }
    public static TrialPackage Open(string input,string expectedSha256)
    {
        var stamp=ProtectedFile.Capture(input);
        if (/*guard:package-hash*/ (!stamp.Present||!string.Equals(stamp.Sha256,expectedSha256,StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException("Reviewed manifest hash mismatch");
        byte[] bytes=File.ReadAllBytes(stamp.Path);
        using(var document=JsonDocument.Parse(bytes))CheckDuplicates(document.RootElement);
        var options=new JsonSerializerOptions{UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};
        var manifest=JsonSerializer.Deserialize<TrialManifest>(bytes,options)??throw new InvalidDataException("Manifest is empty");
        if (/*guard:firstplay-scope*/ (manifest.Schema!=1||manifest.ScopeDecision!=ApprovedScope||manifest.RequiredPlayers!=4||manifest.MaximumSeconds is < 1 or > 600))throw new InvalidDataException("Explicit approved four-player first-play scope required");
        if(manifest.Reader==null||manifest.Target==null||manifest.ProtectedInputs==null||manifest.Payload==null||manifest.Payload.Length==0)throw new InvalidDataException("Manifest inputs missing");
        string root=Path.GetDirectoryName(stamp.Path)!;
        var protectedFiles=new List<ProtectedFile>{stamp};
        foreach(var entry in manifest.Payload)
        {
            if(string.IsNullOrEmpty(entry.Path)||Path.IsPathRooted(entry.Path)||entry.Path.Replace('\\','/').Split('/').Any(p=>p is ""or"."or"..")||entry.Path.Contains(':'))throw new InvalidDataException("Payload path must stay inside package");
            string path=Path.GetFullPath(Path.Combine(root,entry.Path));
            var file=ProtectedFile.Capture(path);
            if (/*guard:payload-hash*/ (!file.Present||!string.Equals(file.Sha256,entry.Sha256,StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException("Package payload changed: "+entry.Path);
            protectedFiles.Add(file);
        }
        if(protectedFiles.Select(p=>p.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=protectedFiles.Count)throw new InvalidDataException("Duplicate payload path");
        string source=Path.Combine(root,"source-manifest.json");
        if(!protectedFiles.Any(p=>p.Path.Equals(source,StringComparison.OrdinalIgnoreCase)&&p.Sha256==manifest.SourceManifestSha256))throw new InvalidDataException("Exact-source manifest is not pinned");
        foreach(var file in manifest.ProtectedInputs)
        {
            if(!Path.IsPathFullyQualified(file.Path)||file!=ProtectedFile.Capture(file.Path))throw new InvalidDataException("Protected input changed: "+file.Path);
            if(!protectedFiles.Any(p=>p.Path.Equals(file.Path,StringComparison.OrdinalIgnoreCase)))protectedFiles.Add(file);
        }
        foreach(string path in new[]{manifest.Target.Executable,manifest.Target.CoreLibrary,manifest.Target.Content,manifest.Target.MainConfig,manifest.Reader.LibraryPath,manifest.Reader.FunctionModulePath})
            if (/*guard:target-covered*/ !protectedFiles.Any(p=>p.Present&&p.Path.Equals(Path.GetFullPath(path),StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException("Required target/reader input is not protected");
        var result=new TrialPackage(manifest,stamp.Path,stamp.Sha256!,protectedFiles.ToArray());
        if(!result.Current())throw new IOException("Package changed during verification");
        return result;
    }
    public bool Current()=>protectedFiles.All(p=>p.Unchanged());
    public void RequireActualHost()
    {
        string[] actual=[System.Reflection.Assembly.GetEntryAssembly()!.Location,typeof(TrialPackage).Assembly.Location,typeof(PinnedXInputReader).Assembly.Location,Environment.ProcessPath!];
        if (/*guard:actual-host-payload*/ actual.Any(path=>!protectedFiles.Any(p=>p.Present&&p.Path.Equals(path,StringComparison.OrdinalIgnoreCase))))throw new InvalidDataException("Actual executing host/reader is not in the reviewed package payload");
    }
    public bool QuickCurrent()
    {
        try
        {
            foreach(var file in protectedFiles)
            {
                if(!file.Present){if(ProtectedFile.Capture(file.Path).Present)return false;continue;}
                var info=new FileInfo(file.Path);var expected=metadata[file.Path];
                if(!info.Exists||info.Length!=expected.Length||info.LastWriteTimeUtc.Ticks!=expected.Ticks)return false;
            }
            return true;
        }
        catch(Exception e)when(e is IOException or UnauthorizedAccessException){return false;}
    }
    public IReadOnlyList<ProtectedFile> ProtectedFiles=>Array.AsReadOnly(protectedFiles);
    private static void CheckDuplicates(JsonElement element)
    {
        if(element.ValueKind==JsonValueKind.Object)
        {
            var seen=new HashSet<string>(StringComparer.Ordinal);
            foreach(var item in element.EnumerateObject()){if(!seen.Add(item.Name))throw new InvalidDataException("Duplicate manifest field");CheckDuplicates(item.Value);}
        }
        else if(element.ValueKind==JsonValueKind.Array)foreach(var item in element.EnumerateArray())CheckDuplicates(item);
    }
}
