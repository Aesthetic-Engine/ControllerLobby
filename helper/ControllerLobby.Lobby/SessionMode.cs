using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ControllerLobby.Lobby;

public sealed record SessionOptions(Guid Session, string GameTitle, string Platform)
{
    public static bool TryParse(string[] args, out SessionOptions? options)
    {
        options = null;
        if (args.Length != 6 || args[0] != "--session" || args[2] != "--game-title" || args[4] != "--platform" ||
            args[3] is null || args[5] is null || !Guid.TryParseExact(args[1], "D", out var id)) return false;
        if (args[1] != id.ToString("D")) return false; // guard:canonical-guid
        options = new(id, args[3], args[5]);
        return true;
    }
    public static Guid ErrorContext(string[] args) => args.Length > 1 && Guid.TryParseExact(args[1], "D", out var id) ? id : Guid.Empty;
}

// These DTOs deliberately have no SourceId, type label, input frame, device.id or output field.
public sealed record SessionSeat(int Player, string Alias, string Kind, string Layout);
public sealed record SessionRecord(int Version, string Session, string Outcome, string Reason, IReadOnlyList<SessionSeat> Seats);
public enum SessionFailure { UiFailed, InvalidArguments, LogUnavailable, InvalidSnapshot }
public enum SessionCancel { UserCancel, WindowClosed }

public static class SessionProtocol
{
    public const int MaxBytes = 16 * 1024;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static SessionRecord Error(Guid session, SessionFailure failure) => new(1, session.ToString("D"), "error", failure switch
    {
        SessionFailure.InvalidArguments => "invalid-arguments", SessionFailure.LogUnavailable => "log-unavailable",
        SessionFailure.InvalidSnapshot => "invalid-snapshot", _ => "ui-failed"
    }, Array.Empty<SessionSeat>());
    public static SessionRecord Cancel(Guid session, SessionCancel reason) => new(1, session.ToString("D"), "cancel",
        reason == SessionCancel.WindowClosed ? "window-closed" : "cancel", Array.Empty<SessionSeat>());

    public static SessionRecord? Snapshot(Guid session, LobbySession model)
    {
        if (model.Phase != LobbyPhase.ConfirmedDraft) return null; // guard:confirmed
        var draft = model.Seats;
        if (draft.Count is < 1 or > 4 || draft.Count(s => s.Source.Kind == SourceKind.KeyboardMouse) > 1 ||
            draft.Select(s => s.Source.SourceId).Distinct(StringComparer.Ordinal).Count() != draft.Count) return null;
        var seats = new List<SessionSeat>();
        for (int i = 0; i < draft.Count; i++)
        {
            var seat = draft[i];
            bool keyboard = seat.Source.Kind == SourceKind.KeyboardMouse;
            if (seat.Player != i + 1 || !Enum.IsDefined(seat.Source.Layout) ||
                seat.Source.Kind is not (SourceKind.Pad or SourceKind.KeyboardMouse)) return null;
            if (keyboard != (seat.Source.Layout == Layout.KeyboardMouse)) return null; // guard:layout-kind
            seats.Add(new(seat.Player, "s" + (i + 1).ToString(CultureInfo.InvariantCulture),
                keyboard ? "keyboard" : "pad", seat.Source.Layout.ToString()));
        }
        return new(1, session.ToString("D"), "confirmed", "p1-confirmed", seats.AsReadOnly());
    }
    public static byte[] Encode(SessionRecord record)
    {
        if (record.Reason.Length > 200) throw new InvalidOperationException("Session reason exceeds schema bound.");
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, Json) + "\n");
        if (bytes.Length > MaxBytes) throw new InvalidOperationException("Session result exceeds output bound.");
        return bytes;
    }
}

public sealed class SessionCompletion
{
    private readonly Guid session;
    private int journalFailed;
    public bool JournalFailed => Volatile.Read(ref journalFailed) != 0;
    public SessionPresentation Presentation { get; }
    public SessionUiFaults UiFaults { get; }
    public SessionCompletion(Guid session, ISessionJournal? journal = null, Action<SessionUiFault>? faultSink = null)
    {
        this.session = session;
        UiFaults = new(() => Result, faultSink);
        Presentation = new(item =>
        {
            if (JournalFailed) return;
            try { journal?.RecordEvent(item); }
            catch (Exception e) when (e is not OutOfMemoryException) { Interlocked.Exchange(ref journalFailed, 1); }
        }, () => JournalFailed);
    }
    private SessionRecord? terminal;
    public SessionRecord? Result => Volatile.Read(ref terminal);
    private bool Set(SessionRecord record) => Interlocked.CompareExchange(ref terminal, record, null) is null;
    public bool TryConfirm(LobbySession model)
    {
        var snapshot = SessionProtocol.Snapshot(session, model);
        return snapshot is not null && Set(snapshot);
    }
    public bool TryCancel(SessionCancel reason = SessionCancel.UserCancel) => Set(SessionProtocol.Cancel(session, reason));
    public bool TryError(SessionFailure reason = SessionFailure.UiFailed) => Set(SessionProtocol.Error(session, reason));
}

public interface ISessionJournal : IDisposable
{
    // Typed, alias-only result; callers cannot accidentally pass raw source ids or exception messages.
    void Record(SessionRecord record);
    void RecordEvent(SessionEvent item);
}

public static class SessionApplication
{
    // Shared by the real entry point and the process fixture. The fixture injects a synthetic UI
    // callback and journal; it cannot construct a Window or controller reader through this module.
    public static int Run(string[] args, Action<SessionOptions, SessionCompletion> show,
        Func<SessionOptions, ISessionJournal> openJournal, Stream output, Action<SessionUiFault>? faultSink = null)
    {
        if (!SessionOptions.TryParse(args, out var parsed))
            return Emit(SessionProtocol.Error(SessionOptions.ErrorContext(args), SessionFailure.InvalidArguments), 2, output);
        var options = parsed!;
        ISessionJournal journal;
        try { journal = openJournal(options) ?? throw new InvalidOperationException(); }
        catch (Exception e) when (e is not OutOfMemoryException)
        { return Emit(SessionProtocol.Error(options.Session, SessionFailure.LogUnavailable), 3, output); }
        var completion = new SessionCompletion(options.Session, journal, faultSink);
        bool fatalUiError = false;
        try { show(options, completion); }
        catch (Exception e) when (e is not OutOfMemoryException)
        { completion.UiFaults.Capture(SessionUiStage.Show, e); completion.TryError(); fatalUiError = true; }
        if (completion.Result is null) completion.TryCancel(SessionCancel.WindowClosed);
        var result = completion.Result!;
        int exitCode = fatalUiError || result.Outcome == "error" ? 1 : 0;
        if (completion.JournalFailed) { result = SessionProtocol.Error(options.Session, SessionFailure.LogUnavailable); exitCode = 3; }
        try { using (journal) journal.Record(result); }
        catch (Exception e) when (e is not OutOfMemoryException)
        { result = SessionProtocol.Error(options.Session, SessionFailure.LogUnavailable); exitCode = 3; } // guard:log-failure
        return Emit(result, exitCode, output);
    }
    private static int Emit(SessionRecord result, int exitCode, Stream output)
    {
        try { var bytes = SessionProtocol.Encode(result); output.Write(bytes); output.Flush(); return exitCode; }
        catch (Exception e) when (e is not OutOfMemoryException) { return 4; } // guard:output-failure
    }
}
