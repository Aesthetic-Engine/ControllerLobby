namespace ControllerLobby.Lobby;

// Both CreateNew streams exist before the real UI is constructed. The optional factory is an
// offline failure seam; production supplies no factory and never truncates an existing journal.
public sealed class SessionFileJournal : ISessionJournal
{
    private readonly Stream result, events;
    public SessionFileJournal(string path, Func<string, Stream>? openForTest = null)
    {
        var open = openForTest ?? (p => new FileStream(p, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
        result = open(path);
        try { events = open(Path.ChangeExtension(path, ".events.jsonl")); }
        catch { result.Dispose(); throw; }
    }
    public void Record(SessionRecord record) { result.Write(SessionProtocol.Encode(record)); result.Flush(); }
    public void RecordEvent(SessionEvent item) { events.Write(SessionEvents.Encode(item)); events.Flush(); }
    public void Dispose() { try { events.Dispose(); } finally { result.Dispose(); } }
}
