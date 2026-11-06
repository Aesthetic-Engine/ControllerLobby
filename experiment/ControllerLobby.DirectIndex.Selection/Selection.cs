namespace ControllerLobby.DirectIndex.Selection;

public enum SelectionPolicy { Disabled, DirectIndexExperiment }
public enum SelectionPhase { Selecting, AwaitingRelease, Confirmed, Failed }

// Only current XInput endpoint observations. No durable physical identity or
// helper/output evidence. A sample must describe all four slots coherently.
public sealed record EndpointSample(int ConnectedMask, IReadOnlyList<ushort> Buttons, bool Readable)
{
    public bool Valid => Readable && ConnectedMask is >= 0 and <= 15 && Buttons.Count == 4
        && Enumerable.Range(0, 4).All(i => (ConnectedMask & (1 << i)) != 0 || Buttons[i] == 0);
}

// This is a candidate selection snapshot, never Supported/Ready or launch authority.
public sealed record SelectionSeal(int ConnectedMask, IReadOnlyList<int> PlayerIndices);

public sealed class StartSelection
{
    public const ushort Start = 0x0010;
    private readonly int mask;
    private readonly TimeProvider clock;
    private readonly TimeSpan releaseTimeout;
    private readonly List<int> selected = [];
    private int previous;
    private bool armed;
    private long confirmationStarted;
    public SelectionPhase Phase { get; private set; } = SelectionPhase.Selecting;
    public string? Failure { get; private set; }
    public IReadOnlyList<int> Indices => selected.AsReadOnly();

    public StartSelection(SelectionPolicy policy, EndpointSample initial,
        TimeSpan releaseTimeout, TimeProvider? clock = null)
    {
        this.clock = clock ?? TimeProvider.System;
        this.releaseTimeout = releaseTimeout;
        mask = initial.ConnectedMask;
        if (/*guard:explicit-mode*/ (policy != SelectionPolicy.DirectIndexExperiment)) Fail("experiment-not-selected");
        else if (/*guard:initial-shape*/ (!initial.Valid || mask == 0)) Fail("initial-endpoints-unreadable-or-empty");
        else if (/*guard:timeout-bound*/ (releaseTimeout <= TimeSpan.Zero || releaseTimeout > TimeSpan.FromSeconds(30))) Fail("invalid-release-timeout");
    }

    private void Fail(string reason) { Failure ??= reason; Phase = SelectionPhase.Failed; }
    private bool Validate(EndpointSample sample)
    {
        if (/*guard:topology*/ (!sample.Valid || sample.ConnectedMask != mask))
        { Fail("endpoint-change-or-read-failure"); return false; }
        return true;
    }

    public bool Observe(EndpointSample sample)
    {
        if (Phase is SelectionPhase.Failed or SelectionPhase.Confirmed) return false;
        if (!Validate(sample)) return false;
        if (/*guard:selecting-only*/ (Phase != SelectionPhase.Selecting)) return false;
        int down = Enumerable.Range(0, 4).Aggregate(0, (bits, i) =>
            bits | ((sample.Buttons[i] & Start) != 0 ? 1 << i : 0));
        if (/*guard:released-before-selection*/ (!armed))
        { if (down == 0) armed = true; previous = down; return false; }
        int edges = down & ~previous; previous = down;
        if (edges == 0) return false;
        if (/*guard:one-edge*/ ((edges & (edges - 1)) != 0 || down != edges))
        { Fail("ambiguous-start"); return false; }
        int slot = Enumerable.Range(0, 4).Single(i => (edges & (1 << i)) != 0);
        if (/*guard:unique-endpoint*/ (selected.Contains(slot)))
        { Fail("endpoint-already-selected"); return false; }
        selected.Add(slot); return true;
    }

    public bool BeginConfirmation()
    {
        if (/*guard:nonempty-confirmation*/ (Phase != SelectionPhase.Selecting || selected.Count == 0)) return false;
        confirmationStarted = clock.GetTimestamp();
        Phase = SelectionPhase.AwaitingRelease;
        return true;
    }

    public SelectionSeal? TryConfirm(EndpointSample sample)
    {
        if (/*guard:awaiting-only*/ (Phase != SelectionPhase.AwaitingRelease)) return null;
        if (!Validate(sample)) return null;
        var elapsed = clock.GetElapsedTime(confirmationStarted);
        if (/*guard:release-deadline*/ (elapsed < TimeSpan.Zero || elapsed >= releaseTimeout))
        { Fail("release-timeout-or-clock-uncertainty"); return null; }
        if (/*guard:released-buttons*/ (selected.Any(i => sample.Buttons[i] != 0))) return null;
        Phase = SelectionPhase.Confirmed;
        return new(mask, Array.AsReadOnly(selected.ToArray()));
    }

    public void Cancel()
    {
        if (Phase != SelectionPhase.Confirmed && Phase != SelectionPhase.Failed) Fail("cancelled");
    }
}
