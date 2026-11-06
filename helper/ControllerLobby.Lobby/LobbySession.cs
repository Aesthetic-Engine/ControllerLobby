namespace ControllerLobby.Lobby;

[Flags]
public enum Buttons
{
    None = 0, Start = 1, Back = 2, Left = 4, Right = 8, Up = 16, Down = 32,
    A = 64, B = 128, X = 256, Y = 512, LB = 1024, RB = 2048,
    LS = 4096, RS = 8192, LT = 16384, RT = 32768,
    MouseLeft = 65536, MouseRight = 131072,
    // Focused native-keyboard feedback only; these are distinct from pad buttons and seat arrows.
    KeyW = 262144, KeyA = 524288, KeyS = 1048576, KeyD = 2097152
}
public enum Layout { GenericPad, XboxSeries, Sn30Pro, ArcadeStick, KeyboardMouse }
public enum SourceKind { Pad, KeyboardMouse }
public enum LobbyPhase { Joining, ConfirmedDraft, Cancelled }

// U1 identity is local to this discovery session, NOT contract device.id or a virtual output identity.
public sealed record InputSource(string SourceId, string TypeLabel, Layout Layout, SourceKind Kind);
public sealed record InputFrame(Buttons Held, double LeftX = 0, double LeftY = 0,
    double RightX = 0, double RightY = 0);
public sealed record SeatDraft(int Player, InputSource Source, InputFrame Frame);

/// <summary>One deterministic state machine shared by fixtures and the read-only Windows view.</summary>
public sealed class LobbySession
{
    private sealed class SourceState(InputSource source, InputFrame frame)
    {
        public InputSource Source { get; } = source;
        public InputFrame Frame { get; set; } = frame;
    }
    private readonly Dictionary<string, SourceState> sources = new(StringComparer.Ordinal);
    private readonly string?[] seats = new string?[4];
    public LobbyPhase Phase { get; private set; } = LobbyPhase.Joining;
    // Fresh mapped edges only. No reader id or label crosses this diagnostics boundary.
    public event Action<SourceKind, Buttons>? FreshInput;
    public IReadOnlyList<InputSource> Sources => sources.Values.Select(s => s.Source).ToArray();
    public IReadOnlyList<SeatDraft> Seats => seats.Select((id, index) => (id, index))
        .Where(s => s.id is not null)
        .Select(s => new SeatDraft(s.index + 1, sources[s.id!].Source, sources[s.id!].Frame)).ToArray();

    // Seed the first reading without treating a button already held at discovery as a fresh press.
    public bool Add(InputSource source, InputFrame? initial = null)
    {
        if (Phase == LobbyPhase.Cancelled || string.IsNullOrWhiteSpace(source.SourceId) ||
            string.IsNullOrWhiteSpace(source.TypeLabel) || sources.ContainsKey(source.SourceId)) return false;
        if (source.Kind == SourceKind.KeyboardMouse && sources.Values.Any(s => s.Source.Kind == SourceKind.KeyboardMouse))
            return false;
        sources.Add(source.SourceId, new SourceState(source, initial ?? new(Buttons.None)));
        Invalidate();
        // mutation:auto-seat
        return true;
    }

    public bool Remove(string sourceId)
    {
        if (Phase == LobbyPhase.Cancelled || !sources.Remove(sourceId)) return false;
        for (int i = 0; i < seats.Length; i++) if (seats[i] == sourceId) seats[i] = null;
        Compact(); // mutation:compact
        Invalidate();
        return true;
    }

    public void Update(string sourceId, InputFrame frame)
    {
        if (Phase == LobbyPhase.Cancelled || !sources.TryGetValue(sourceId, out var state)) return;
        var pressed = frame.Held & ~state.Frame.Held;
        state.Frame = frame;
        if (pressed != Buttons.None) FreshInput?.Invoke(state.Source.Kind, pressed);
        if (pressed.HasFlag(Buttons.Back)) { Cancel(); return; }
        int player = Array.IndexOf(seats, sourceId);
        if (player < 0)
        {
            if (pressed.HasFlag(Buttons.Start))
            {
                Join(sourceId);
                // mutation:join-confirms
            }
            return;
        }
        // Moving and confirming in the same sample cannot commit the new order.
        if (pressed.HasFlag(Buttons.Left) || pressed.HasFlag(Buttons.Right))
        {
            if (pressed.HasFlag(Buttons.Left) != pressed.HasFlag(Buttons.Right))
                Move(sourceId, pressed.HasFlag(Buttons.Left) ? -1 : 1);
            return;
        }
        if (player == 0 && pressed.HasFlag(Buttons.Start) && Phase == LobbyPhase.Joining)
            Phase = LobbyPhase.ConfirmedDraft;
    }

    public bool Move(string sourceId, int direction)
    {
        if (Phase == LobbyPhase.Cancelled || direction is not (-1 or 1)) return false;
        int from = Array.IndexOf(seats, sourceId), to = from + direction;
        int count = seats.Count(id => id is not null);
        if (from < 0 || to < 0 || to >= count) return false;
        (seats[from], seats[to]) = (seats[to], seats[from]);
        Invalidate();
        return true;
    }

    public void InputContextChanged()
    {
        // Focus loss/reader uncertainty invalidates a confirmation; reseeding is performed by the reader.
        if (Phase != LobbyPhase.Cancelled) Invalidate();
    }
    public void Seed(string sourceId, InputFrame frame)
    {
        if (Phase != LobbyPhase.Cancelled && sources.TryGetValue(sourceId, out var s)) s.Frame = frame;
    }
    public void Cancel() => Phase = LobbyPhase.Cancelled;

    private void Join(string sourceId)
    {
        int free = Array.IndexOf(seats, null);
        if (free < 0 || Array.IndexOf(seats, sourceId) >= 0) return;
        seats[free] = sourceId;
        Invalidate();
    }
    private void Invalidate()
    {
        if (Phase == LobbyPhase.ConfirmedDraft) Phase = LobbyPhase.Joining; // mutation:invalidate
    }
    private void Compact()
    {
        var order = seats.Where(id => id is not null).ToArray();
        Array.Clear(seats);
        Array.Copy(order, seats, order.Length);
    }
}
