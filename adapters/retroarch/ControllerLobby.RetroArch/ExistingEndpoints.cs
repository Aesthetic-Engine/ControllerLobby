namespace ControllerLobby.RetroArch;

// Existing endpoint declarations, not helper-created outputs or authenticated
// identity. The caller must separately qualify the selector/emulator visibility.
public sealed record ExistingEndpointMap(string Session, int ConnectedMask,
    IReadOnlyList<int> PlayerIndices, string VisibilityReceipt);
public sealed record ExistingMappingResult(AnalysisStatus Status, string Reason, string? AppendConfig,
    IReadOnlyList<int> PlayerIndices, IReadOnlyList<InputStamp> Inputs, string OwnerRoot, string? GlobalOptionsPath = null)
{
    public bool CanLaunch => false;
}

public static class ExistingEndpointMapping
{
    public static ExistingMappingResult Analyze(LaunchContext launch, ExistingEndpointMap mapping, IConfigReader reader)
    {
        int[] indices = mapping.PlayerIndices.ToArray();
        ExistingMappingResult Invalid(string reason) => new(AnalysisStatus.Unverified, reason, null,
            Array.AsReadOnly(indices), Array.Empty<InputStamp>(), launch.Root);
        if (/*guard:existing-context*/ (string.IsNullOrWhiteSpace(mapping.Session) || string.IsNullOrWhiteSpace(mapping.VisibilityReceipt)))
            return Invalid("existing-endpoint-context");
        if (/*guard:existing-shape*/ (mapping.ConnectedMask is < 1 or > 15 || indices.Length is < 1 or > 4
            || indices.Distinct().Count() != indices.Length || indices.Any(i => i is < 0 or > 3 || (mapping.ConnectedMask & (1 << i)) == 0)))
            return Invalid("existing-endpoint-selection");
        var plan = MappingAnalyzer.AnalyzeConfiguration(launch, indices, reader);
        if(plan.Status!=AnalysisStatus.Candidate) return new(plan.Status,plan.Reason,null,Array.AsReadOnly(indices),plan.Inputs,plan.OwnerRoot);
        var inputs=plan.Inputs.ToList();
        try
        {
            var main=reader.Read(launch.MainConfig);
            if(main.State!=ReadState.Present||main.Text==null||main.RawSha256!=inputs[0].Sha256)return Invalid("base-changed-during-inspection");
            var settings=MappingAnalyzer.Parse(main.Text!);
            settings.TryGetValue("core_options_path",out var optionSetting);
            string root=Path.GetFullPath(launch.Root);
            string value=string.IsNullOrEmpty(optionSetting)?"retroarch-core-options.cfg":optionSetting;
            if(value.IndexOfAny(['%','$','~','|','#'])>=0||value.Replace('\\','/').Split('/').Contains(".."))return Invalid("unresolved-core-options-path");
            string global=Path.GetFullPath(value.StartsWith(":/")||value.StartsWith(":\\")?Path.Combine(root,value[2..]):Path.IsPathFullyQualified(value)?value:Path.Combine(root,value));
            if(!global.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)
                ||inputs.Any(s=>s.Path.Equals(global,StringComparison.OrdinalIgnoreCase)))return Invalid("unresolved-core-options-path");
            var options=inputs.Skip(1).Where(s=>s.Path.EndsWith(".cfg",StringComparison.OrdinalIgnoreCase)).Select(s=>Path.ChangeExtension(s.Path,".opt")).Append(global).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach(var path in options)
            {
                var read=reader.Read(path);
                if(read.State is not (ReadState.Present or ReadState.Absent))return Invalid("unreadable-core-options");
                if(read.State==ReadState.Present&&(read.Text==null||read.RawSha256==null||read.RawSha256.Length!=64||!read.RawSha256.All(Uri.IsHexDigit)))return Invalid("unreadable-core-options");
                inputs.Add(new(path,read.State==ReadState.Present,read.RawSha256));
            }
            return new(plan.Status,plan.Reason,plan.AppendConfig,Array.AsReadOnly(indices),inputs.AsReadOnly(),plan.OwnerRoot,global);
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {return Invalid("unresolved-core-options");}
    }
}
