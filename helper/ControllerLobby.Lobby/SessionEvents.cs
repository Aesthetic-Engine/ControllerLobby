using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ControllerLobby.Lobby;

public enum SessionEventType { Topmost, Activation, Activated, Deactivated, FirstInput, RestorationDecision, TopmostRestored }
public enum SessionInputKind { Pad, Keyboard }
public enum SessionRestoreDecision { Restore, Stale, Inactive, NotLoaded, NotReturned, AlreadyRestored, NotForeground, Expired }
// No constructor parameter can carry a reader identity, type label, arbitrary key text or seat alias.
public sealed record SessionEvent(DateTimeOffset Utc, SessionEventType Event, bool? Topmost = null,
    bool? Succeeded = null, SessionInputKind? Kind = null, Buttons? Buttons = null, SessionRestoreDecision? Decision = null)
{
    public string? Alias => Event == SessionEventType.FirstInput ? "input-1" : null;
}

public static class SessionEvents
{
    public const Buttons PadButtons = (Buttons)65535;
    public const Buttons NativeButtons = Buttons.Start | Buttons.Back | Buttons.Left | Buttons.Right |
        Buttons.Up | Buttons.Down | Buttons.MouseLeft | Buttons.MouseRight;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    public static byte[] Encode(SessionEvent item)
    {
        if (!Enum.IsDefined(item.Event) || item.Utc.Offset != TimeSpan.Zero) throw new InvalidOperationException("Invalid event.");
        bool input = item.Event == SessionEventType.FirstInput;
        if (input)
        {
            if (item.Kind is null || !Enum.IsDefined(item.Kind.Value) || item.Buttons is null || item.Buttons == Buttons.None ||
                (item.Buttons.Value & ~(item.Kind == SessionInputKind.Pad ? PadButtons : NativeButtons)) != 0 ||
                item.Topmost is not null || item.Succeeded is not null || item.Decision is not null) throw new InvalidOperationException("Invalid mapped input.");
        }
        else if (item.Kind is not null || item.Buttons is not null || !(item.Event switch
        {
            SessionEventType.Topmost or SessionEventType.TopmostRestored => item.Topmost == true && item.Succeeded is null && item.Decision is null,
            SessionEventType.Activation => item.Succeeded is not null && item.Topmost is null && item.Decision is null,
            SessionEventType.Activated => item.Topmost is null && item.Succeeded is null && item.Decision is null,
            SessionEventType.Deactivated => item.Topmost == false && item.Succeeded is null && item.Decision is null,
            SessionEventType.RestorationDecision => item.Decision is not null && Enum.IsDefined(item.Decision.Value) && item.Topmost is null && item.Succeeded is null,
            _ => false
        }))
            throw new InvalidOperationException("Invalid event shape.");
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(item, Json) + "\n");
    }
}

public interface ISessionWindow
{
    void SetTopmost(bool value);
    bool Activate();
    bool OwnsForeground();
}

public static class SessionWindowOwnership
{
    public static bool IsOwn(nint own, nint foreground) => own != 0 && own == foreground; // guard:own-hwnd
}

// Pure lifecycle used by the real Window's events. This never creates a Window or reads input itself.
public sealed class SessionPresentation(Action<SessionEvent> write, Func<bool> failed, Func<DateTimeOffset>? clock = null,
    Func<long>? monotonicMilliseconds = null)
{
    public const long RestoreSampleMaxAgeMilliseconds = 1000; // bounded deferred sample, candidate placeholder
    private bool loaded, switchedAway, focused, firstInput, restored;
    private int tenure;
    private long enteredAt;
    private long Tick => monotonicMilliseconds?.Invoke() ?? Environment.TickCount64;
    private bool Expired => Tick - enteredAt > RestoreSampleMaxAgeMilliseconds; // guard:deferred-bound
    private DateTimeOffset Now => (clock?.Invoke() ?? DateTimeOffset.UtcNow).ToUniversalTime();
    public void Loaded(Action<bool> topmost, Func<bool> activate)
    {
        if (loaded || failed()) return; // guard:activate-once
        loaded = true;
        if (switchedAway) return;
        topmost(true);
        write(new(Now, SessionEventType.Topmost, Topmost: true));
        if (failed()) return;
        bool result = activate();
        write(new(Now, SessionEventType.Activation, Succeeded: result));
    }
    public int Activated()
    {
        if (!focused) { tenure++; restored = false; enteredAt = Tick; }
        focused = true;
        write(new(Now, SessionEventType.Activated));
        return tenure;
    }
    public void Deactivated(Action<bool> topmost)
    {
        focused = false;
        tenure++; restored = false;
        switchedAway = true;
        topmost(false); // guard:release-topmost
        write(new(Now, SessionEventType.Deactivated, Topmost: false));
    }
    private bool IsCurrent(int ticket) => ticket == tenure && focused; // guard:focus-tenure
    private static bool HasForeground(ISessionWindow window) => window.OwnsForeground(); // guard:foreground-restore
    private void Decision(SessionRestoreDecision decision) => write(new(Now, SessionEventType.RestorationDecision, Decision: decision));
    public void RestoreTopmost(int ticket, ISessionWindow window)
    {
        if (failed()) return;
        if (!IsCurrent(ticket)) { Decision(focused ? SessionRestoreDecision.Stale : SessionRestoreDecision.Inactive); return; }
        if (Expired) { Decision(SessionRestoreDecision.Expired); return; }
        if (!loaded) { Decision(SessionRestoreDecision.NotLoaded); return; }
        if (!switchedAway) { Decision(SessionRestoreDecision.NotReturned); return; }
        if (restored) { Decision(SessionRestoreDecision.AlreadyRestored); return; }
        if (!HasForeground(window)) { Decision(SessionRestoreDecision.NotForeground); return; }
        Decision(SessionRestoreDecision.Restore);
        // Journalling can take time. Recheck ownership and tenure at the actual z-order change.
        if (failed()) return;
        if (!IsCurrent(ticket)) { Decision(focused ? SessionRestoreDecision.Stale : SessionRestoreDecision.Inactive); return; }
        if (Expired) { Decision(SessionRestoreDecision.Expired); return; }
        if (!HasForeground(window)) { Decision(SessionRestoreDecision.NotForeground); return; }
        restored = true;
        window.SetTopmost(true); // guard:restoration-only
        write(new(Now, SessionEventType.TopmostRestored, Topmost: true));
    }
    public void Input(SourceKind kind, Buttons pressed)
    {
        if (!focused || firstInput || failed()) return; // guard:first-input-once-focused
        if (kind is not (SourceKind.Pad or SourceKind.KeyboardMouse)) return;
        var mapped = pressed & (kind == SourceKind.Pad ? SessionEvents.PadButtons : SessionEvents.NativeButtons);
        if (mapped == Buttons.None) return; // guard:mapped-only
        firstInput = true;
        write(new(Now, SessionEventType.FirstInput, Kind: kind == SourceKind.Pad ? SessionInputKind.Pad : SessionInputKind.Keyboard, Buttons: mapped));
    }
}
