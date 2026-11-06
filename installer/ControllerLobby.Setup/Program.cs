using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using Microsoft.Win32;

namespace ControllerLobby.Setup;

internal static class Program
{
    [STAThread] public static int Main()
    {
        using var mutex=new Mutex(false,"Local\\ControllerLobby.GuidedSetup");
        bool owned=false;
        try
        {
            try{owned=mutex.WaitOne(0);}catch(AbandonedMutexException){owned=true;}
            if(!owned){MessageBox.Show("Another ControllerLobby setup window is already open.","ControllerLobby Setup");return 3;}
            var app=new Application();
            app.Run(new SetupWindow(async()=>await Task.Run<ISetupEngine>(()=>new SetupEngine(PackageBundle.OpenEmbedded()))));
            return 0;
        }
        finally{if(owned)mutex.ReleaseMutex();}
    }
}

public sealed class SetupWindow:Window
{
    private readonly Func<Task<ISetupEngine>> factory;
    private readonly Func<string?> picker;
    private SetupController? controller;
    private readonly StackPanel choices=new();
    private readonly TextBlock status=new(){TextWrapping=TextWrapping.Wrap,FontSize=16,Margin=new(0,16,0,10)};
    private readonly TextBlock folder=new(){Text="No folder selected",TextWrapping=TextWrapping.Wrap,Foreground=Brushes.DimGray};
    private readonly TextBox details=new(){IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Height=130};
    private readonly ProgressBar progress=new(){Height=5,IsIndeterminate=true,Visibility=Visibility.Collapsed,Margin=new(0,8,0,8)};
    private readonly StackPanel maintenance=new(){Orientation=Orientation.Horizontal};
    private readonly Button verify,repair,remove;
    internal Button BrowseButton{get;}=Button("Browse…");
    internal Button InstallButton{get;}=Button("Install");
    internal Button CloseButton{get;}=Button("Close");
    internal string StatusText=>status.Text;
    private static Button Button(string text)=>new(){Content=text,Padding=new(20,9,20,9),Margin=new(0,0,10,0),MinWidth=100};
    public SetupWindow(Func<Task<ISetupEngine>> factory,Func<string?>? picker=null)
    {
        this.factory=factory;this.picker=picker??PickFolder;
        Title="ControllerLobby Setup";Width=720;Height=740;MinWidth=620;MinHeight=640;
        WindowStartupLocation=WindowStartupLocation.CenterScreen;FontFamily=new("Segoe UI");FontSize=14;
        Background=new SolidColorBrush(Color.FromRgb(245,247,251));
        var content=new StackPanel{Margin=new(30)};
        content.Children.Add(new TextBlock{Text="ControllerLobby",FontSize=30,FontWeight=FontWeights.SemiBold,Foreground=new SolidColorBrush(Color.FromRgb(31,76,139))});
        content.Children.Add(new TextBlock{Text="Set up the controller join screen for LaunchBox & BigBox",FontSize=16,Margin=new(0,5,0,22),TextWrapping=TextWrapping.Wrap});
        content.Children.Add(new TextBlock{Text="Close LaunchBox, BigBox and your emulators before continuing.",TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,20)});
        content.Children.Add(new TextBlock{Text="1. Choose your LaunchBox folder",FontSize=17,FontWeight=FontWeights.SemiBold});
        var pathRow=new DockPanel{Margin=new(0,10,0,20)};DockPanel.SetDock(BrowseButton,Dock.Right);pathRow.Children.Add(BrowseButton);pathRow.Children.Add(folder);content.Children.Add(pathRow);
        content.Children.Add(new TextBlock{Text="2. Choose your emulators",FontSize=17,FontWeight=FontWeights.SemiBold,Margin=new(0,0,0,8)});
        content.Children.Add(choices);
        content.Children.Add(status);content.Children.Add(progress);
        var actions=new StackPanel{Orientation=Orientation.Horizontal,Margin=new(0,10,0,12)};
        InstallButton.Background=new SolidColorBrush(Color.FromRgb(31,76,139));InstallButton.Foreground=Brushes.White;
        actions.Children.Add(InstallButton);actions.Children.Add(CloseButton);content.Children.Add(actions);
        verify=Button("Verify");repair=Button("Repair");remove=Button("Uninstall");
        maintenance.Children.Add(verify);maintenance.Children.Add(repair);maintenance.Children.Add(remove);content.Children.Add(maintenance);
        content.Children.Add(new Expander{Header="Details and troubleshooting",Content=details,Margin=new(0,12,0,0)});
        Content=new ScrollViewer{Content=content,Background=Background,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        BrowseButton.Click+=async(_,_)=>{string? root=this.picker();if(root!=null&&controller!=null)await controller.ChooseFolder(root);};
        InstallButton.Click+=async(_,_)=>{if(controller!=null)await controller.Execute("install");};
        verify.Click+=async(_,_)=>{if(controller!=null)await controller.Execute("verify");};
        repair.Click+=async(_,_)=>{if(controller!=null)await controller.Execute("repair");};
        remove.Click+=async(_,_)=>
        {
            if(controller!=null&&MessageBox.Show(this,"Remove ControllerLobby and restore its LaunchBox entries?","Uninstall ControllerLobby",MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes)
                await controller.Execute("remove");
        };
        CloseButton.Click+=(_,_)=>Close();
        Closing+=(_,e)=>{if(controller!=null&&!controller.RequestClose()){e.Cancel=true;status.Text="Setup is still working. Please wait for it to finish.";}};
        Loaded+=async(_,_)=>await Initialize();
        Render();
    }
    private string? PickFolder()
    {
        var dialog=new OpenFolderDialog{Title="Choose the folder containing LaunchBox.exe",Multiselect=false,AddToRecent=false};
        return dialog.ShowDialog(this)==true?dialog.FolderName:null;
    }
    internal async Task Initialize()
    {
        if(controller!=null)return;
        status.Text="Preparing the bundled installer…";BrowseButton.IsEnabled=false;InstallButton.IsEnabled=false;
        try{controller=new(await factory());controller.Changed+=Render;Render();}
        catch(Exception error){status.Text="Setup couldn't prepare its files. Open Details for more information.";details.Text=error.Message;}
    }
    internal async Task SelectFolder(string root){if(controller!=null)await controller.ChooseFolder(root);}
    private void Render()
    {
        bool ready=controller!=null,busy=controller?.Busy??false;
        BrowseButton.IsEnabled=ready&&!busy;InstallButton.IsEnabled=controller?.CanInstall??false;
        CloseButton.IsEnabled=!busy;CloseButton.Content=controller?.Complete==true?"Done":"Close";
        progress.Visibility=busy?Visibility.Visible:Visibility.Collapsed;
        maintenance.Visibility=controller?.Installed==true?Visibility.Visible:Visibility.Collapsed;
        verify.IsEnabled=repair.IsEnabled=remove.IsEnabled=controller?.CanMaintain??false;
        choices.Children.Clear();
        if(controller==null)return;
        folder.Text=controller.Root.Length>0?controller.Root:"No folder selected";status.Text=controller.Status;details.Text=controller.Details;
        foreach(var entry in controller.Entries)
        {
            var box=new CheckBox{Content=new TextBlock{Text=entry.Title+" ("+entry.Kind+")\n"+entry.ApplicationPath,TextWrapping=TextWrapping.Wrap},IsEnabled=entry.Eligible&&!busy,IsChecked=controller.Selected.Contains(entry.Id),Margin=new(0,5,0,5),ToolTip=entry.Eligible?null:entry.Reason};
            box.Click+=(_,_)=>controller.Select(entry.Id,box.IsChecked==true);choices.Children.Add(box);
        }
    }
}
