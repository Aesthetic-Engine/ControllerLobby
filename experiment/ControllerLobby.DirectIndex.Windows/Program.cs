#nullable enable
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ControllerLobby.DirectIndex.HostCore;
using ControllerLobby.DirectIndex.Native;
using ControllerLobby.Lobby;
using ControllerLobby.Lobby.Windows;
using ControllerLobby.RetroArch;

namespace ControllerLobby.DirectIndex.Windows;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if(args.Length==2&&args[0]=="--render-fixture")return RenderFixture(args[1]);
            if(args.Length==4&&args[0]=="--inspect-target"&&args[2]=="--output")
            {
                var target=JsonSerializer.Deserialize<TrialTarget>(File.ReadAllBytes(args[1]))??throw new InvalidDataException("Target input missing");
                var mapping=ExistingEndpointMapping.Analyze(new(target.Root,target.MainConfig,target.CoreName,target.Content),new("read-only-package-inspection",15,[0,1,2,3],"no-physical-endpoint-claim"),new DiskConfigReader());
                if(mapping.Status!=AnalysisStatus.Candidate)throw new InvalidDataException(mapping.Reason);
                using var output=new FileStream(args[3],FileMode.CreateNew,FileAccess.Write,FileShare.Read);output.Write(JsonSerializer.SerializeToUtf8Bytes(mapping.Inputs));output.Flush(true);
                Console.WriteLine("PASS read-only config-chain inspection; candidate only; no reader/controller/emulator execution");return 0;
            }
            bool verify=args.Length==5&&args[4]=="--verify-only";
            if((args.Length!=4&&!verify)||args[0]!="--manifest"||args[2]!="--sha256")throw new ArgumentException("Use the reviewed Launch GoldenEye Trial.cmd entry point");
            TrialPackage package=TrialPackage.Open(args[1],args[3]);
            package.RequireActualHost();
            if(verify){Console.WriteLine("PASS reviewed first-play package/input hashes; no reader/controller/emulator execution");return 0;}
            var application=new Application();var window=new TrialWindow(package);
            Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;window.RequestStop();};
            return application.Run(window);
        }
        catch(Exception e)when(e is not OutOfMemoryException){Console.Error.WriteLine("GoldenEye trial refused: "+e.Message);return 1;}
    }
    private static int RenderFixture(string folder)
    {
        string path=System.IO.Path.GetFullPath(folder);if(Directory.Exists(path))throw new IOException("New fixture evidence folder required");Directory.CreateDirectory(path);
        _=new Application();var window=new TrialWindow(null);
        var cards=new[]{new JoinedCard(1,2,new SlotRead(2,0,0,0x0010,0,0,0,0,0,0)),new JoinedCard(2,0,new SlotRead(0,0,0,0x1000,0,0,0,0,0,0)),new JoinedCard(3,3,new SlotRead(3,0,0,0x2000,0,0,0,0,0,0)),new JoinedCard(4,1,new SlotRead(1,0,0,0,0,0,0,0,0,0))};
        window.Selecting(cards,"Player 1: press Start again, then release");
        var content=(FrameworkElement)window.Content;content.Measure(new(1400,760));content.Arrange(new(0,0,1400,760));content.UpdateLayout();
        var bitmap=new RenderTargetBitmap(1400,760,96,96,PixelFormats.Pbgra32);bitmap.Render(content);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=new FileStream(System.IO.Path.Combine(path,"four-card-fixture.png"),FileMode.CreateNew,FileAccess.Write))encoder.Save(stream);
        if(!window.DisplayedSlots.SequenceEqual(new[]{2,0,3,1}))throw new Exception("Displayed card order does not match presenter slots");
        Console.WriteLine("PASS offscreen actual four-card window; literal slots 2,0,3,1; synthetic frames only; no launch");return 0;
    }
}

internal sealed class TrialWindow : Window,ITrialView
{
    private readonly TrialPackage? package;
    private readonly Func<ITrialView,CancellationToken,Action,Task<bool>>? normalRun;
    private readonly CancellationTokenSource stopping=new();
    private readonly TaskCompletionSource<bool> existingControls=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task? ExistingControlsRequested=>normalRun==null?null:existingControls.Task;
    private readonly TextBlock title,detail;
    private readonly ControllerDrawing[] drawings=new ControllerDrawing[4];
    private readonly TextBlock[] labels=new TextBlock[4];
    private readonly Border[] tiles=new Border[4];
    private readonly Button stop;
    private int alive=1,selecting,finished;
    private bool running;
    private readonly Func<bool>? frontendAlive;
    private readonly bool closeAfterRun;
    private int[] displayedSlots=[];
    public bool Alive=>Volatile.Read(ref alive)==1&&(frontendAlive?.Invoke()??true);
    public string EntrySurface=>closeAfterRun?"BigBox private installer":"separate experimental four-card launcher";
    public IReadOnlyList<int> DisplayedSlots=>displayedSlots;
    public TrialWindow(TrialPackage? package,Func<bool>? frontendAlive=null,bool closeAfterRun=false,Func<ITrialView,CancellationToken,Action,Task<bool>>? normalRun=null)
    {
        this.normalRun=normalRun;this.frontendAlive=frontendAlive;this.closeAfterRun=closeAfterRun;this.package=package;Title="Controller Lobby — GoldenEye trial";Width=Math.Min(1400,SystemParameters.WorkArea.Width);Height=Math.Min(800,SystemParameters.WorkArea.Height);MinWidth=900;MinHeight=550;WindowStartupLocation=WindowStartupLocation.CenterScreen;
        Background=Paint("#0B1220");Foreground=Brushes.White;FontFamily=new("Segoe UI");
        var root=new Grid{Margin=new(34,26,34,24),Background=Paint("#0B1220")};
        root.RowDefinitions.Add(new(){Height=GridLength.Auto});root.RowDefinitions.Add(new(){Height=GridLength.Auto});root.RowDefinitions.Add(new(){Height=new(1,GridUnitType.Star)});root.RowDefinitions.Add(new(){Height=GridLength.Auto});
        root.Children.Add(Text(normalRun==null?"CONTROLLER LOBBY  /  GOLDENEYE TRIAL":"CONTROLLER LOBBY",14,"#87A4C0"));
        var heading=new StackPanel{Margin=new(0,20,0,24)};title=Text("Choose your players",34);detail=Text("Checking the reviewed test package",17,"#A9B8CE");detail.TextWrapping=TextWrapping.Wrap;heading.Children.Add(title);heading.Children.Add(detail);Grid.SetRow(heading,1);root.Children.Add(heading);
        var row=new Grid();
        for(int i=0;i<4;i++)
        {
            row.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});
            var body=new Grid{Margin=new(17,18,17,16)};body.RowDefinitions.Add(new(){Height=GridLength.Auto});body.RowDefinitions.Add(new(){Height=GridLength.Auto});body.RowDefinitions.Add(new(){Height=new(1,GridUnitType.Star)});
            body.Children.Add(Text("PLAYER "+(i+1),14,"#9BB1C9"));labels[i]=Text("Waiting to join",20);labels[i].Margin=new(0,20,0,8);Grid.SetRow(labels[i],1);body.Children.Add(labels[i]);
            drawings[i]=new ControllerDrawing{Layout=Layout.GenericPad,Frame=new(Buttons.None),Opacity=.15,MinHeight=150};Grid.SetRow(drawings[i],2);body.Children.Add(drawings[i]);
            tiles[i]=new Border{Background=Paint("#121F31"),BorderBrush=Paint("#293B53"),BorderThickness=new(1),CornerRadius=new(12),Margin=new(i==0?0:8,0,i==3?0:8,0),Child=body};Grid.SetColumn(tiles[i],i);row.Children.Add(tiles[i]);
        }
        Grid.SetRow(row,2);root.Children.Add(row);
        var footer=new DockPanel{Margin=new(0,20,0,0)};stop=new Button{Content="Esc  Cancel",Width=140,Height=42,FontSize=16,Focusable=false};stop.Click+=(_,_)=>{if(Volatile.Read(ref finished)==1)Close();else RequestStop();};DockPanel.SetDock(stop,Dock.Right);footer.Children.Add(stop);
        if(normalRun!=null){var existing=new Button{Content="Use existing controls",Height=42,Margin=new(0,0,16,0),Padding=new(14,0,14,0)};existing.Click+=(_,_)=>existingControls.TrySetResult(true);DockPanel.SetDock(existing,Dock.Right);footer.Children.Add(existing);}
        footer.Children.Add(Text(normalRun==null?"Wake controllers here and press Start to join. Keep joined controllers connected.":"Press Start to join. Keep controllers connected until the game starts.",14,"#9DB0C8"));Grid.SetRow(footer,3);root.Children.Add(footer);Content=root;
        PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape){RequestStop();e.Handled=true;}};
        Deactivated+=(_,_)=>{if(Volatile.Read(ref selecting)==1)RequestStop();};
        Closing+=(_,e)=>{if(running){e.Cancel=true;RequestStop();Status(normalRun==null?"Stopping GoldenEye":"Stopping game","Waiting for the owned game process to end");}else Interlocked.Exchange(ref alive,0);};
        if(normalRun!=null)Title="Controller Lobby";
        if(package!=null||normalRun!=null)Loaded+=async(_,_)=>await Start();
    }
    public void RequestStop()=>stopping.Cancel();
    public void Selecting(IReadOnlyList<JoinedCard> cards,string instruction)
    {
        Interlocked.Exchange(ref selecting,1);
        Dispatch(()=>
        {
            displayedSlots=cards.Select(c=>c.Slot).ToArray();
            title.Text=normalRun==null||cards.Count==0?"Choose your players"
                :instruction=="Release all selected buttons to launch"?"RELEASE BUTTONS TO LAUNCH"
                :"PLAYER 1: PRESS START AGAIN TO LAUNCH";
            title.TextWrapping=TextWrapping.Wrap;detail.Text=instruction;
            for(int i=0;i<4;i++)
            {
                var card=cards.FirstOrDefault(c=>c.Player==i+1);labels[i].Text=card==null?"Waiting to join":"Joined";
                drawings[i].Opacity=card==null ? .15 : 1;drawings[i].Frame=card==null?new(Buttons.None):Map(card.Reading);drawings[i].InvalidateVisual();tiles[i].BorderBrush=Paint(card==null?"#293B53":"#428EA2");
            }
        });
    }
    public void Status(string heading,string message)
    {Interlocked.Exchange(ref selecting,0);Dispatch(()=>{title.Text=heading;detail.Text=message;stop.Content=Volatile.Read(ref finished)==1?"Close":"Stop";});}
    private async Task Start()
    {
        if(running||(package==null&&normalRun==null))return;running=true;bool safeToClose=normalRun==null;
        try
        {
            string state=System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ControllerLobby","GoldenEyeFirstPlay");
            if(normalRun!=null)safeToClose=await normalRun(this,stopping.Token,()=>Dispatcher.Invoke(Hide));
            else await TrialCoordinator.Run(package!,state,this,stopping.Token);
        }
        catch(Exception e)when(e is not OutOfMemoryException){safeToClose=false;Status(normalRun==null?"Trial stopped":"Game launch stopped",e.Message);Show();}
        finally{FinishAfterRun(safeToClose);}
    }
    internal void FinishAfterRun(bool safeToClose=true){running=false;Interlocked.Exchange(ref finished,1);stop.Content="Close";if(closeAfterRun&&safeToClose)Close();}
    private void Dispatch(Action action){if(Dispatcher.CheckAccess())action();else if(!Dispatcher.HasShutdownStarted)Dispatcher.BeginInvoke(action);}
    private static SolidColorBrush Paint(string hex)=>new((Color)ColorConverter.ConvertFromString(hex));
    private static TextBlock Text(string text,double size,string color="#F1F5FC")=>new(){Text=text,FontSize=size,Foreground=Paint(color)};
    private static InputFrame Map(SlotRead s)
    {
        Buttons held=Buttons.None;
        foreach(var pair in new[]{(0x0010,Buttons.Start),(0x0020,Buttons.Back),(0x0001,Buttons.Up),(0x0002,Buttons.Down),(0x0004,Buttons.Left),(0x0008,Buttons.Right),(0x1000,Buttons.A),(0x2000,Buttons.B),(0x4000,Buttons.X),(0x8000,Buttons.Y),(0x0100,Buttons.LB),(0x0200,Buttons.RB),(0x0040,Buttons.LS),(0x0080,Buttons.RS)})if((s.Buttons&pair.Item1)!=0)held|=pair.Item2;
        if(s.LeftTrigger>30)held|=Buttons.LT;if(s.RightTrigger>30)held|=Buttons.RT;
        return new(held,s.LeftX/32768.0,s.LeftY/32768.0,s.RightX/32768.0,s.RightY/32768.0);
    }
}
