using System.Windows;
using ControllerLobby.DirectIndex.HostCore;
internal static class Program
{
 [STAThread] static void Main()
 {
  _=new Application();int passed=0;bool frontend=true;
  void Need(bool condition,string name){if(!condition)throw new Exception(name);Console.WriteLine("PASS "+name);passed++;}
  var golden=new ControllerLobby.DirectIndex.Windows.TrialWindow(null,()=>frontend,true);
  var turtles=new ControllerLobby.MameTrial.Windows.TrialWindow(null,()=>frontend,true);
  Need(golden.Alive&&turtles.Alive,"both hosts initially live");
  frontend=false;Need(!golden.Alive&&!turtles.Alive,"frontend loss reaches both actual ITrialView callers");
  Need(golden.EntrySurface=="BigBox private installer"&&turtles.EntrySurface==golden.EntrySurface,"BigBox evidence surface named");
  bool gc=false,tc=false;golden.Closed+=(_,_)=>gc=true;turtles.Closed+=(_,_)=>tc=true;
  golden.FinishAfterRun();turtles.FinishAfterRun();Need(gc&&tc,"both BigBox windows close after coordinator completion");
  var legacyG=new ControllerLobby.DirectIndex.Windows.TrialWindow(null);var legacyT=new ControllerLobby.MameTrial.Windows.TrialWindow(null);
  bool lg=false,lt=false;legacyG.Closed+=(_,_)=>lg=true;legacyT.Closed+=(_,_)=>lt=true;
  Need(legacyG.Alive&&legacyT.Alive,"standalone windows retain default liveness");
  legacyG.FinishAfterRun();legacyT.FinishAfterRun();Need(!lg&&!lt,"standalone result windows stay open");
  legacyG.Close();legacyT.Close();Need(lg&&lt,"measurement observes explicit standalone close");
  var normal=new ControllerLobby.DirectIndex.Windows.TrialWindow(null,()=>true,true,(view,token,hide)=>Task.FromResult(false));
  normal.Hide();Need(normal.Alive,"hiding lobby does not imply frontend loss");
  bool nc=false;normal.Closed+=(_,_)=>nc=true;normal.FinishAfterRun(false);
  Need(!nc,"unconfirmed normal completion keeps recovery window open");
  Need(normal.Title=="Controller Lobby"&&normal.ExistingControlsRequested!=null,"normal window exposes existing-controls choice");
  var cards=new[]{new JoinedCard(1,2,new ControllerLobby.DirectIndex.Native.SlotRead(2,0,0,0,0,0,0,0,0,0))};
  normal.Selecting(cards,"Player 1: press Start again to play, then release. Other players may press Start to join (1/4)");
  var root=(System.Windows.Controls.Grid)normal.Content;
  var heading=(System.Windows.Controls.StackPanel)root.Children[1];
  var title=(System.Windows.Controls.TextBlock)heading.Children[0];
  Need(title.Text=="PLAYER 1: PRESS START AGAIN TO LAUNCH"&&title.FontSize>=30,"normal launch cue prominent after join");
  normal.Selecting(cards,"Release all selected buttons to launch");
  Need(title.Text=="RELEASE BUTTONS TO LAUNCH","release instruction replaces launch cue");
  normal.Selecting([],"A joined controller disconnected. Press Start to choose the players again.");
  Need(title.Text=="Choose your players","disconnect clears launch cue");
  normal.Status("Starting game","Using existing controls");
  Need(title.Text=="Starting game","launch cue clears for status");
  normal.Close();
  Console.WriteLine(passed+"/"+passed+" actual offscreen WPF checks; no frontend/controller/game");
 }
}
