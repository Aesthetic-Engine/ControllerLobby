using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ControllerLobby.DirectIndex.Selection;
using ControllerLobby.RetroArch;

namespace ControllerLobby.DirectIndex.Preparation;

public sealed record PreparationRequest(SelectionPolicy Policy, string Session, SelectionSeal Selection,
    LaunchContext Launch, string Executable, string CoreLibrary, string SessionDirectory,
    string VisibilityReceipt, IReadOnlyList<string> ExtraArguments);
public sealed record FileStamp(string Path, string Sha256, long Length);
public sealed record LaunchSpecification(string ApplicationName, string CompleteCommandLine,
    string WorkingDirectory, IReadOnlyList<string> Arguments);
public sealed record PreparationResult(AnalysisStatus Status, string Reason, LaunchSpecification? Specification,
    IReadOnlyList<InputStamp> SourceConfigs, IReadOnlyList<FileStamp> Files)
{
    // No gate for native visibility, built-in preparation or live qualification is
    // manufactured by this component. The result cannot authorize target resume.
    public bool CanLaunch => false;
}

public static class RetroArchPreparation
{
    public static PreparationResult Prepare(PreparationRequest request, IConfigReader? configReader = null)
    {
        var configs = new List<InputStamp>();
        var files = new List<FileStamp>();
        PreparationResult Refuse(string reason) => new(AnalysisStatus.Unsupported, reason, null, configs.AsReadOnly(), files.AsReadOnly());
        try
        {
            string[] extraArguments=request.ExtraArguments.ToArray();
            if (/*guard:preparation-mode*/ (request.Policy != SelectionPolicy.DirectIndexExperiment)) return Refuse("experiment-not-selected");
            // This typed experiment request supports only these additional flags.
            // A future host-tail decoder must reject everything else, never drop it.
            if (/*guard:extra-options*/ (extraArguments.Any(a => a is not ("-f" or "--verbose"))
                || extraArguments.Distinct(StringComparer.Ordinal).Count() != extraArguments.Length)) return Refuse("unqualified-extra-arguments");
            string root = Path.GetFullPath(request.Launch.Root), session = Path.GetFullPath(request.SessionDirectory);
            if (/*guard:preparation-paths*/ (new[] { request.Launch.Root, request.SessionDirectory, request.Executable, request.CoreLibrary, request.Launch.MainConfig, request.Launch.Content }
                .Any(p => !Path.IsPathFullyQualified(p) || p.IndexOfAny(['\r', '\n', '"']) >= 0))) return Refuse("absolute-plain-paths-required");
            if (/*guard:target-root*/ (!Within(root, request.Executable) || !Within(root, request.CoreLibrary)
                || !Path.GetDirectoryName(Path.GetFullPath(request.Executable))!.Equals(root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))) return Refuse("target-outside-root");
            if (/*guard:session-outside-owner*/ (session.Equals(root, StringComparison.OrdinalIgnoreCase) || Within(root, session))) return Refuse("session-inside-owner");
            var reader = configReader ?? new DiskConfigReader();
            var mapping = ExistingEndpointMapping.Analyze(request.Launch,
                new(request.Session, request.Selection.ConnectedMask, request.Selection.PlayerIndices, request.VisibilityReceipt), reader);
            configs.AddRange(mapping.Inputs);
            if (/*guard:config-candidate*/ (mapping.Status != AnalysisStatus.Candidate || mapping.AppendConfig == null)) return Refuse(mapping.Reason);
            // Snapshot source files before artifacts. Hashing is detection; it does
            // not constitute isolation or prove a runtime's config semantics.
            foreach (string path in new[] { request.Executable, request.CoreLibrary, request.Launch.Content }) files.Add(Stamp(path));
            if (/*guard:new-session*/ (Directory.Exists(session) || File.Exists(session))) return Refuse("session-path-exists");
            CheckAncestors(Path.GetDirectoryName(session)!);
            Directory.CreateDirectory(session);
            WriteNew(Path.Combine(session,"preparation.marker"),request.Session);
            // Copy only the fully inspected applicable chain. The executable and
            // working directory remain in the original root. Source files are never written.
            string configRoot=Path.Combine(session,"config"),remapRoot=Path.Combine(session,"remaps");
            foreach (var input in configs.Skip(1).Where(s => s.Present))
            {
                var captured=reader.Read(input.Path);
                if (/*guard:copy-source-stamp*/ (captured.State!=ReadState.Present || captured.RawSha256!=input.Sha256)) return Refuse("source-changed-during-copy");
                string folder=input.Path.Equals(mapping.GlobalOptionsPath,StringComparison.OrdinalIgnoreCase)?session:Path.Combine(input.Path.EndsWith(".rmp",StringComparison.OrdinalIgnoreCase)?remapRoot:configRoot,request.Launch.CoreName);
                Directory.CreateDirectory(folder);
                string target=Path.Combine(folder,Path.GetFileName(input.Path));
                WriteNew(target,captured.Text!);
                files.Add(Stamp(target));
            }
            var main=reader.Read(request.Launch.MainConfig);
            if (/*guard:base-copy-stamp*/ (main.State!=ReadState.Present || main.RawSha256!=configs[0].Sha256)) return Refuse("base-changed-during-copy");
            // Redirect the known override/remap roots to session copies. Other
            // parsed settings are retained literally. Their path semantics require
            // actual preparation qualification before this candidate can be launched.
            string baseText=ReplaceDirectory(main.Text!,"rgui_config_directory",configRoot);
            baseText=ReplaceDirectory(baseText,"input_remapping_directory",remapRoot);
            baseText=ReplaceOrAppend(baseText,"core_options_path",Path.Combine(session,Path.GetFileName(mapping.GlobalOptionsPath!)));
            string mainPath=Path.Combine(session,"retroarch.cfg"),appendPath=Path.Combine(session,"retroarch-append.cfg");
            WriteNew(mainPath,baseText); WriteNew(appendPath,mapping.AppendConfig!);
            files.Add(Stamp(mainPath)); files.Add(Stamp(appendPath));
            if (/*guard:source-recheck*/ (!Preserved(configs,files))) return Refuse("inputs-changed-during-preparation");
            var args = new List<string>{"-L",Path.GetFullPath(request.CoreLibrary),"--config",mainPath,"--appendconfig",appendPath};
            args.AddRange(extraArguments); args.Add(Path.GetFullPath(request.Launch.Content));
            string executable=Path.GetFullPath(request.Executable);
            string command=BuildCommandLine(executable,args);
            return new(AnalysisStatus.Candidate,"prepared-candidate-not-launch-permission",
                new(executable,command,root,Array.AsReadOnly(args.ToArray())),configs.AsReadOnly(),files.AsReadOnly());
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return Refuse("preparation-inspection-or-artifact-failed"); }
    }

    public static bool Preserved(IReadOnlyList<InputStamp> configs,IReadOnlyList<FileStamp> files)
    {
        try
        {
            var reader=new DiskConfigReader();
            foreach(var stamp in configs)
            {
                var actual=reader.Read(stamp.Path);
                if(stamp.Present ? actual.State!=ReadState.Present||actual.RawSha256!=stamp.Sha256 : actual.State!=ReadState.Absent) return false;
            }
            return files.All(expected=>Stamp(expected.Path)==expected);
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException){return false;}
    }
    private static bool Within(string root,string path)=>Path.GetFullPath(path).StartsWith(root.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    private static void CheckAncestors(string path)
    {
        for(string? current=Path.GetFullPath(path);current!=null;current=Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try{attributes=File.GetAttributes(current);}catch(FileNotFoundException){continue;}catch(DirectoryNotFoundException){continue;}
            if(attributes.HasFlag(FileAttributes.ReparsePoint))throw new IOException("reparse input/artifact path");
        }
    }
    private static FileStamp Stamp(string path)
    {
        path=Path.GetFullPath(path);CheckAncestors(path);
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(stream.Length>512L*1024*1024)throw new IOException("binary input exceeds experiment bound");
        return new(path,Convert.ToHexString(SHA256.HashData(stream)),stream.Length);
    }
    private static void WriteNew(string path,string text)
    {
        using var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);
        stream.Write(new UTF8Encoding(false,true).GetBytes(text));stream.Flush(true);
    }
    private static string ReplaceDirectory(string text,string key,string path)
    {
        string value=path.Replace('\\','/');
        return Regex.Replace(text,"(?m)^(?:\\uFEFF)?[ \\t]*"+key+"[ \\t]*=.*$",_=>key+" = \""+value+"\"");
    }
    private static string ReplaceOrAppend(string text,string key,string path)
    {
        string replaced=ReplaceDirectory(text,key,path);
        return Regex.IsMatch(text,"(?m)^(?:\\uFEFF)?[ \\t]*"+key+"[ \\t]*=")?replaced:replaced+"\n"+key+" = \""+path.Replace('\\','/')+"\"\n";
    }
    public static string Quote(string value)
    {
        if(value.IndexOfAny(['\0','\r','\n'])>=0)throw new ArgumentException("invalid command token");
        var result=new StringBuilder("\"");int slashes=0;
        foreach(char c in value)
        {
            if(c=='\\'){slashes++;continue;}
            if(c=='"')result.Append('\\',slashes*2+1);else result.Append('\\',slashes);
            result.Append(c);slashes=0;
        }
        result.Append('\\',slashes*2);return result.Append('"').ToString();
    }
    public static string BuildCommandLine(string executable,IReadOnlyList<string> arguments)
    {
        string command=string.Join(" ",new[]{executable}.Concat(arguments).Select(Quote));
        if (/*guard:command-length*/ (command.Length>=32767)) throw new ArgumentException("command-line-too-long");
        return command;
    }
}
