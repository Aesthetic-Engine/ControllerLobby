namespace ControllerLobby.Lobby;

public sealed record SourceSample(InputSource Source, InputFrame Frame);

// Reconciliation is shared by fixtures and the WinRT reader. A failed/incomplete scan must never
// masquerade as unplugging every device. No source identity is derived from enumeration order.
public sealed class InputSnapshots(LobbySession session)
{
    private readonly HashSet<string> owned = new(StringComparer.Ordinal);
    private bool rebaseline = true;
    public void Suspend()
    {
        session.InputContextChanged();
        rebaseline = true;
    }
    public void Apply(IReadOnlyList<SourceSample> samples)
    {
        if (samples.Any(s => string.IsNullOrWhiteSpace(s.Source.SourceId)) ||
            samples.Select(s => s.Source.SourceId).Distinct(StringComparer.Ordinal).Count() != samples.Count)
            throw new ArgumentException("Input snapshot has missing or duplicate source identities.");
        var current = samples.Select(s => s.Source.SourceId).ToHashSet(StringComparer.Ordinal);
        bool changed = !owned.SetEquals(current);
        foreach (var id in owned.Except(current).ToArray()) session.Remove(id);
        foreach (var s in samples)
        {
            if (!owned.Contains(s.Source.SourceId)) session.Add(s.Source, s.Frame);
            else if (changed || rebaseline) session.Seed(s.Source.SourceId, s.Frame);
            else session.Update(s.Source.SourceId, s.Frame);
        }
        owned.Clear(); owned.UnionWith(current);
        rebaseline = false;
    }
}
