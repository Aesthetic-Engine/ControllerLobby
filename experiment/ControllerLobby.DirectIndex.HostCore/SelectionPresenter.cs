using ControllerLobby.DirectIndex.Native;
using ControllerLobby.DirectIndex.Selection;

namespace ControllerLobby.DirectIndex.HostCore;

public sealed record JoinedCard(int Player, int Slot, SlotRead Reading);

// The graphical view presents these actual reader slots. It does not ask WinRT
// to name/reorder pads or maintain a second independent assignment state machine.
public sealed class SelectionPresenter
{
    private readonly StartSelection selector;
    private readonly int requiredPlayers, mask;
    private readonly long frequency;
    private long previousQpc;
    private int previousStart;
    private EndpointRead latest;
    private string? failure;
    public string? Failure => failure ?? selector.Failure;
    public SelectionSeal? Seal { get; private set; }
    public SelectionPhase Phase => Failure != null ? SelectionPhase.Failed : selector.Phase;
    public IReadOnlyList<JoinedCard> Cards => selector.Indices.Select((slot, player) => new JoinedCard(player+1,slot,latest.Slots[slot])).ToArray();

    public SelectionPresenter(EndpointRead initial, int requiredPlayers, TimeProvider? clock = null)
    {
        if (/*guard:required-player-count*/ requiredPlayers is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(requiredPlayers));
        this.requiredPlayers=requiredPlayers; latest=initial;mask=initial.Sample.ConnectedMask;frequency=initial.QpcFrequency;
        previousQpc=initial.Qpc;previousStart=StartMask(initial.Sample);
        selector=new(SelectionPolicy.DirectIndexExperiment,initial.Sample,TimeSpan.FromSeconds(5),clock);
        if(initial.Qpc<=0||frequency<=0)failure="invalid-observation-clock";
    }
    public void Observe(EndpointRead read)
    {
        if(Failure!=null||Seal!=null)return;
        if (/*guard:presenter-clock*/ (read.Qpc<=previousQpc||read.QpcFrequency!=frequency)) {failure="stale-or-changed-observation-clock";return;}
        previousQpc=read.Qpc;
        var sample=read.Sample;
        if (/*guard:presenter-topology*/ (!sample.Valid||sample.ConnectedMask!=mask)) {failure="endpoint-change-or-read-failure";return;}
        latest=read;
        if(selector.Phase==SelectionPhase.AwaitingRelease){Seal=selector.TryConfirm(sample);return;}
        int down=StartMask(sample),edges=down&~previousStart;previousStart=down;
        if (/*guard:p1-fresh-confirmation*/ (selector.Indices.Count==requiredPlayers&&edges==(1<<selector.Indices[0])&&down==edges))
        {
            if(selector.BeginConfirmation())Seal=selector.TryConfirm(sample);
            return;
        }
        selector.Observe(sample);
    }
    public void Cancel(){failure??="cancelled";Seal=null;selector.Cancel();}
    private static int StartMask(EndpointSample sample)=>sample.Valid?Enumerable.Range(0,4).Aggregate(0,(m,i)=>m|((sample.Buttons[i]&StartSelection.Start)!=0?1<<i:0)):0;
}
