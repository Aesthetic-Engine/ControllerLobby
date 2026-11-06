using ControllerLobby.DirectIndex.Native;
using ControllerLobby.DirectIndex.Selection;

namespace ControllerLobby.DirectIndex.HostCore;

// Collection is mutable only before a seal. It identifies observed XInput slots,
// never durable physical devices. Every observed loss of a joined slot clears
// the selection; an unnoticed disconnect/replacement cannot be identified here.
public sealed class LobbyJoinPresenter
{
    private readonly List<int> selected=[];
    private readonly int requiredPlayers;
    private readonly bool normalPlay;
    private readonly long frequency;
    private readonly TimeProvider clock;
    private EndpointRead latest;
    private int mask,previousStart,armed;
    private long qpc,confirmationStarted;
    public string? Failure{get;private set;}
    public SelectionPhase Phase{get;private set;}=SelectionPhase.Selecting;
    public SelectionSeal? Seal{get;private set;}
    public int ResetCount{get;private set;}
    public IReadOnlyList<JoinedCard> Cards=>selected.Select((slot,p)=>new JoinedCard(p+1,slot,latest.Slots[slot])).ToArray();
    public LobbyJoinPresenter(EndpointRead initial,int requiredPlayers,TimeProvider? clock=null,bool normalPlay=false)
    {
        if(requiredPlayers is <1 or >4)throw new ArgumentOutOfRangeException(nameof(requiredPlayers));
        this.normalPlay=normalPlay;this.requiredPlayers=requiredPlayers;this.clock=clock??TimeProvider.System;
        latest=initial;mask=initial.Sample.ConnectedMask;frequency=initial.QpcFrequency;qpc=initial.Qpc;
        if(!initial.Sample.Valid||qpc<=0||frequency<=0){Fail("invalid-initial-observation");return;}
        previousStart=StartMask(initial.Sample);armed=mask&~previousStart;
    }
    private void Fail(string reason){Failure??=reason;Phase=SelectionPhase.Failed;Seal=null;}
    public void Observe(EndpointRead read)
    {
        if(Failure!=null||Seal!=null)return;
        if(/*guard:collect-clock*/read.Qpc<=qpc||read.QpcFrequency!=frequency){Fail("stale-or-changed-observation-clock");return;}
        qpc=read.Qpc;var sample=read.Sample;
        if(/*guard:collect-readable*/!sample.Valid){Fail("endpoint-read-failure");return;}
        int current=sample.ConnectedMask,down=StartMask(sample),added=current&~mask;
        bool lostJoined=selected.Any(i=>(current&(1<<i))==0);
        latest=read;
        if(/*guard:collect-loss-reset*/lostJoined||Phase==SelectionPhase.AwaitingRelease&&current!=mask)
        {
            selected.Clear();Seal=null;Phase=SelectionPhase.Selecting;ResetCount++;
            mask=current;previousStart=down;armed=current&~down;return;
        }
        mask=current;
        // A newly present slot with Start held is deliberately unarmed. Seeing
        // a release is required before a later edge can join or confirm.
        armed&=current&~added;armed|=current&~down;
        if(Phase==SelectionPhase.AwaitingRelease)
        {
            var elapsed=clock.GetElapsedTime(confirmationStarted);
            if(elapsed<TimeSpan.Zero||elapsed>=TimeSpan.FromSeconds(5)){Fail("release-timeout-or-clock-uncertainty");return;}
            if(selected.Any(i=>sample.Buttons[i]!=0))return;
            Seal=new(current,Array.AsReadOnly(selected.ToArray()));Phase=SelectionPhase.Confirmed;return;
        }
        int edges=down&~previousStart&armed;previousStart=down;
        if(normalPlay)
        {
            // Ignore joined P2-P4 repeats even when held alongside a fresh P1 edge.
            int ignored=selected.Skip(1).Aggregate(0,(value,i)=>value|(1<<i));
            edges&=~ignored;
            if(edges==0||(edges&(edges-1))!=0)return; // simultaneous new joins have no defined order
        }
        else
        {
            if(edges==0)return;
            if(/*guard:collect-one-edge*/(edges&(edges-1))!=0||down!=edges){Fail("ambiguous-start");return;}
        }
        int slot=Enumerable.Range(0,4).Single(i=>(edges&(1<<i))!=0);
        if(normalPlay&&selected.Contains(slot))
        {
            // A second fresh P1 press confirms however many players have joined.
            // Other joined players pressing Start again do not cancel the lobby.
            if(slot!=selected[0])return;
            confirmationStarted=clock.GetTimestamp();Phase=SelectionPhase.AwaitingRelease;return;
        }
        if(selected.Count==requiredPlayers)
        {
            if(/*guard:collect-p1-confirm*/slot!=selected[0]){Fail("only-player-one-confirms");return;}
            confirmationStarted=clock.GetTimestamp();Phase=SelectionPhase.AwaitingRelease;return;
        }
        if(selected.Contains(slot)){Fail("endpoint-already-selected");return;}
        selected.Add(slot);
    }
    public string Instruction=>ResetCount>0&&selected.Count==0?"A joined controller disconnected. Press Start to choose the players again."
        :Phase==SelectionPhase.AwaitingRelease?"Release all selected buttons to launch"
        :normalPlay&&selected.Count>0?"Player 1: press Start again to play, then release. Other players may press Start to join ("+selected.Count+"/4)"
        :selected.Count==requiredPlayers?"Player 1: press Start again, then release"
        :"Wake a controller, release Start, then press Start to join ("+selected.Count+"/"+requiredPlayers+")";
    private static int StartMask(EndpointSample s)=>Enumerable.Range(0,4).Aggregate(0,(m,i)=>m|((s.Buttons[i]&StartSelection.Start)!=0?1<<i:0));
}
