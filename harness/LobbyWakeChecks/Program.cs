using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ControllerLobby.DirectIndex.HostCore;
using ControllerLobby.DirectIndex.Native;
using ControllerLobby.DirectIndex.Selection;

int pass=0,fail=0;long qpc=100;
void Need(bool value,string detail){if(!value)throw new Exception(detail);}
void Case(string name,Action body){try{body();Console.WriteLine("PASS "+name);pass++;}catch(Exception e){Console.WriteLine("FAIL "+name+": "+e.Message);fail++;}}
EndpointRead Read(int mask,int start=0,int heldA=0,uint bad=0,long? timestamp=null,long frequency=1000)=>new(timestamp??++qpc,frequency,Enumerable.Range(0,4).Select(i=>new SlotRead(i,bad!=0&&i==0?bad:(mask&(1<<i))!=0?0u:1167u,1,(ushort)(((start&(1<<i))!=0?16:0)|((heldA&(1<<i))!=0?0x1000:0)),0,0,0,0,0,0)).ToArray());
void Press(LobbyJoinPresenter p,int mask,int slot){p.Observe(Read(mask));p.Observe(Read(mask,1<<slot));p.Observe(Read(mask));}
LobbyJoinPresenter Full(FakeClock? clock=null){var p=new LobbyJoinPresenter(Read(15),4,clock);foreach(int i in new[]{3,0,1,2})Press(p,15,i);Need(p.Cards.Select(c=>c.Slot).SequenceEqual(new[]{3,0,1,2}),"four-card order wrong");return p;}
Case("zero-to-four-wake-preserves-join-order",()=>{var p=new LobbyJoinPresenter(Read(0),4);int mask=0;foreach(int i in new[]{3,0,1,2}){mask|=1<<i;Press(p,mask,i);}Need(p.Failure==null&&p.Cards.Count==4&&p.Seal==null,"wake collection failed");p.Observe(Read(15,8));p.Observe(Read(15));Need(p.Seal?.PlayerIndices.SequenceEqual(new[]{3,0,1,2})==true,"confirmation lost order");});
Case("initial-held-start-needs-release-and-fresh-press",()=>{var p=new LobbyJoinPresenter(Read(1,1),4);p.Observe(Read(1,1));Need(p.Cards.Count==0,"initial held Start auto-joined");Press(p,1,0);Need(p.Cards.Count==1,"fresh press did not join");});
Case("newly-awake-held-start-needs-release-and-fresh-press",()=>{var p=new LobbyJoinPresenter(Read(0),4);p.Observe(Read(4,4));p.Observe(Read(4,4));Need(p.Cards.Count==0,"wake-held Start auto-joined");Press(p,4,2);Need(p.Cards.Count==1&&p.Cards[0].Slot==2,"released wake did not join");});
Case("add-and-remove-unjoined-pad-preserves-earlier-joins",()=>{var p=new LobbyJoinPresenter(Read(1),4);Press(p,1,0);p.Observe(Read(3));p.Observe(Read(1));Need(p.Cards.Count==1&&p.Cards[0].Slot==0&&p.ResetCount==0,"unjoined change reset player");});
Case("observed-loss-of-joined-pad-clears-all-assignments",()=>{var p=new LobbyJoinPresenter(Read(3),4);Press(p,3,0);Press(p,3,1);p.Observe(Read(2));Need(p.Cards.Count==0&&p.ResetCount==1&&p.Seal==null,"joined loss silently retained assignments");Press(p,2,1);Need(p.Cards[0].Player==1&&p.Cards[0].Slot==1,"fresh join reused old player order");});
Case("replacement-waking-with-held-start-does-not-inherit-player",()=>{var p=new LobbyJoinPresenter(Read(1),4);Press(p,1,0);p.Observe(Read(0));p.Observe(Read(1,1));Need(p.Cards.Count==0,"replacement inherited old join");Press(p,1,0);Need(p.Cards.Count==1,"replacement cannot deliberately join");});
Case("cannot-confirm-before-all-four-players-join",()=>{var p=new LobbyJoinPresenter(Read(1),4);Press(p,1,0);Press(p,1,0);Need(p.Seal==null&&p.Phase!=SelectionPhase.AwaitingRelease,"incomplete players confirmed");});
Case("only-p1-fresh-start-confirms",()=>{var p=Full();p.Observe(Read(15,1));p.Observe(Read(15));Need(p.Seal==null&&p.Failure!=null,"P2 confirmed");});
Case("confirmation-requires-release-of-all-selected-buttons",()=>{var p=Full();p.Observe(Read(15,8));p.Observe(Read(15,heldA:1));Need(p.Seal==null,"held attack allowed launch");p.Observe(Read(15));Need(p.Seal!=null,"release did not seal");});
Case("loss-during-confirmation-returns-to-collection",()=>{var p=Full();p.Observe(Read(15,8));p.Observe(Read(7));Need(p.Seal==null&&p.Cards.Count==0&&p.Phase==SelectionPhase.Selecting,"lost pad allowed confirmation");});
Case("simultaneous-starts-refuse-ambiguous-order",()=>{var p=new LobbyJoinPresenter(Read(3),4);p.Observe(Read(3,3));Need(p.Failure!=null&&p.Seal==null,"ambiguous order accepted");});
Case("true-read-error-refuses-during-collection",()=>{var p=new LobbyJoinPresenter(Read(0),4);p.Observe(Read(0,bad:5));Need(p.Failure!=null,"real read error treated as sleeping pad");});
Case("clock-regression-and-frequency-change-refuse",()=>{var initial=Read(0);var p=new LobbyJoinPresenter(initial,4);p.Observe(Read(0,timestamp:initial.Qpc));Need(p.Failure!=null,"stale clock accepted");p=new LobbyJoinPresenter(Read(0),4);p.Observe(Read(0,frequency:2000));Need(p.Failure!=null,"frequency change accepted");});
Case("collecting-has-no-idle-deadline",()=>{var clock=new FakeClock();var p=new LobbyJoinPresenter(Read(0),4,clock);clock.Ticks=86_400_000;Press(p,1,0);Need(p.Failure==null&&p.Cards.Count==1,"lobby expired before wake");});
Case("release-deadline-and-clock-uncertainty-still-refuse",()=>{foreach(long elapsed in new[]{5000L,-1L}){var clock=new FakeClock();var p=Full(clock);p.Observe(Read(15,8));clock.Ticks=elapsed;p.Observe(Read(15));Need(p.Failure!=null&&p.Seal==null,"uncertain/late release accepted");}});
Case("selection-await-cancel-and-dead-sampler-are-not-infinite",()=>{using var cancel=new CancellationTokenSource();var tcs=new TaskCompletionSource<SelectionSeal>();var pending=LobbySelectionAwaiter.Wait(tcs.Task,()=>false,cancel.Token);bool refused=false;try{pending.WaitAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();}catch(IOException){refused=true;}Need(refused,"dead sampler not detected");cancel.Cancel();refused=false;try{LobbySelectionAwaiter.Wait(tcs.Task,()=>true,cancel.Token).GetAwaiter().GetResult();}catch(OperationCanceledException){refused=true;}Need(refused,"cancel not detected");});

foreach(int players in new[]{1,2,3,4})Case("normal-p1-confirms-"+players+"-players",()=>{
 var p=new LobbyJoinPresenter(Read(15),4,normalPlay:true);
 foreach(int slot in new[]{2,0,3,1}.Take(players))Press(p,15,slot);
 Need(p.Seal==null&&p.Cards.Count==players,"normal join count wrong");
 if(players>1){Press(p,15,0);Need(p.Failure==null&&p.Seal==null&&p.Cards.Count==players,"non-P1 duplicate ended lobby");}
 Press(p,15,2);Need(p.Seal?.PlayerIndices.Count==players&&p.Failure==null,"P1 did not confirm normal selection");
});
Case("normal-confirm-still-waits-for-selected-button-release",()=>{
 var p=new LobbyJoinPresenter(Read(3),4,normalPlay:true);Press(p,3,0);Press(p,3,1);
 p.Observe(Read(3,1));p.Observe(Read(3,heldA:2));Need(p.Seal==null,"normal mode skipped release");
 p.Observe(Read(3));Need(p.Seal?.PlayerIndices.Count==2,"release did not confirm");
});
Case("normal-unjoined-start-joins-without-confirming",()=>{
 var p=new LobbyJoinPresenter(Read(15),4,normalPlay:true);Press(p,15,2);Press(p,15,3);
 Need(p.Seal==null&&p.Failure==null&&p.Cards.Select(c=>c.Slot).SequenceEqual(new[]{2,3}),"unjoined player confirmed or failed");
});

Case("normal-held-P2-Start-plus-fresh-P1-confirms-after-release",()=>{
 var p=new LobbyJoinPresenter(Read(3),4,normalPlay:true);Press(p,3,0);Press(p,3,1);
 p.Observe(Read(3,2));Need(p.Failure==null&&p.Seal==null,"P2 repeat failed");
 p.Observe(Read(3,3));Need(p.Failure==null&&p.Phase==SelectionPhase.AwaitingRelease&&p.Seal==null,"held P2 prevented fresh P1 confirmation");
 p.Observe(Read(3,2));Need(p.Seal==null,"P2 held Start allowed seal");p.Observe(Read(3));Need(p.Seal?.PlayerIndices.SequenceEqual(new[]{0,1})==true,"release lost assignments");
});
string dll=Path.GetFullPath(args[0]);nint library=NativeLibrary.Load(dll);
try{
var set=Marshal.GetDelegateForFunctionPointer<SetSlot>(NativeLibrary.GetExport(library,"SetSlot"));
PinnedXInputReader Reader()=>PinnedXInputReader.Open(new(dll,Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dll))),StateExport.NamedGetState,dll,Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dll)))));
string Log()=>Path.Combine(AppContext.BaseDirectory,"wake-"+Guid.NewGuid().ToString("N")+".jsonl");
Case("collecting-sampler-allows-wakes-and-freezes-exact-current-read",()=>{
 for(uint i=0;i<4;i++)set(i,1167,0);using var reader=Reader();using var sampler=new TrialSampler(reader,Log(),"fixture","fixture",true);sampler.Start();
 for(uint i=0;i<4;i++)set(i,0,0);Need(SpinWait.SpinUntil(()=>sampler.Latest.Sample.ConnectedMask==15,TimeSpan.FromSeconds(2)),"wake not observed");Need(sampler.Fresh&&sampler.Failure==null,"wake latched a fault");
 var stale=sampler.Latest;Need(SpinWait.SpinUntil(()=>!ReferenceEquals(stale,sampler.Latest),TimeSpan.FromSeconds(2)),"sampler stalled");Need(!sampler.TryFreeze(stale,15),"stale read froze topology");
 Need(!sampler.TryFreeze(sampler.Latest,7),"mismatched mask frozen");Need(!sampler.TryFreeze(sampler.Latest,0),"empty topology frozen");
 bool frozen=false;sampler.Observed+=r=>{if(!frozen)frozen=sampler.TryFreeze(r,15);};Need(SpinWait.SpinUntil(()=>Volatile.Read(ref frozen),TimeSpan.FromSeconds(2)),"current read did not freeze");Need(!sampler.TryFreeze(sampler.Latest,15),"double freeze accepted");
});
foreach(uint error in new uint[]{1167,5})Case("post-freeze-"+error+"-then-recovery-keeps-fault",()=>{
 for(uint i=0;i<4;i++)set(i,0,0);using var reader=Reader();using var sampler=new TrialSampler(reader,Log(),"fixture","fixture",true);
 Need(sampler.TryFreeze(sampler.Latest,15),"initial current freeze failed");sampler.Start();set(0,error,0);Need(SpinWait.SpinUntil(()=>sampler.Latest.Slots[0].Result==error,TimeSpan.FromSeconds(2)),"bad read not observed");set(0,0,0);Need(SpinWait.SpinUntil(()=>sampler.Latest.Sample.ConnectedMask==15&&sampler.Latest.Slots[0].Result==0,TimeSpan.FromSeconds(2)),"recovery not observed");Need(sampler.Failure!=null&&!sampler.Fresh,"observed post-seal fault disappeared");
});
Case("true-read-error-is-sticky-even-before-freeze",()=>{
 for(uint i=0;i<4;i++)set(i,1167,0);using var reader=Reader();using var sampler=new TrialSampler(reader,Log(),"fixture","fixture",true);sampler.Start();set(0,5,0);Need(SpinWait.SpinUntil(()=>sampler.Latest.Slots[0].Result==5,TimeSpan.FromSeconds(2)),"error not observed");set(0,0,0);Need(SpinWait.SpinUntil(()=>sampler.Latest.Slots[0].Result==0,TimeSpan.FromSeconds(2)),"recovery not observed");Need(sampler.Failure!=null&&!sampler.TryFreeze(sampler.Latest,1),"error forgotten at freeze");
});
}finally{NativeLibrary.Free(library);}
Console.WriteLine($"Cases={pass+fail} Passed={pass} Failed={fail}; M/synthetic endpoint states and private native fixture; no physical input");return fail==0?0:1;
[UnmanagedFunctionPointer(CallingConvention.Winapi)]delegate void SetSlot(uint slot,uint error,ushort buttons);
sealed class FakeClock:TimeProvider{public long Ticks;public override long TimestampFrequency=>1000;public override long GetTimestamp()=>Ticks;}
