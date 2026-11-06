using ControllerLobby.Setup;
using System.IO;
using System.IO.Compression;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class Program
{
    private static int pass,fail;
    private static readonly EmulatorChoice[] Choices=[new("synthetic-retro","RetroArch","retroarch",@"Emulators\RetroArch\retroarch.exe",true,""),new("synthetic-mame","MAME 0.279","mame",@"Emulators\MAME\mame.exe",true,"")];
    private static void Need(bool value,string why){if(!value)throw new Exception(why);}
    private static void Check(string name,Action action){try{action();pass++;Console.WriteLine("PASS "+name);}catch(Exception e){fail++;Console.WriteLine("FAIL "+name+": "+e.Message);}}
    private static void Pump(Func<bool> done)
    {
        DateTime until=DateTime.UtcNow.AddSeconds(8);
        while(!done()&&DateTime.UtcNow<until){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);Thread.Sleep(5);}
        Need(done(),"UI operation did not finish");
    }
    [STAThread] public static int Main(string[] args)
    {
        string output=args.Length>0?Path.GetFullPath(args[0]):Path.Combine(AppContext.BaseDirectory,"setup-fixtures",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        Check("cancel-before-selection-starts-no-install",()=>{var e=new Fake();var c=new SetupController(e);Need(c.RequestClose()&&!c.CanInstall&&e.Calls.Count==0,"cancel started work");});
        Check("unique-entries-selected-with-display-names",()=>{var c=new SetupController(new Fake());c.ChooseFolder(@"D:\SyntheticLaunchBox").GetAwaiter().GetResult();Need(c.Selected.Count==2&&c.CanInstall,"unique defaults missing");});
        Check("multiple-live-kind-needs-explicit-choice",()=>{var e=new Fake{Entries=[Choices[0],Choices[0] with{Id="synthetic-second",Title="Second RetroArch"}]};var c=new SetupController(e);c.ChooseFolder(@"D:\SyntheticLaunchBox").GetAwaiter().GetResult();Need(!c.CanInstall&&c.Selected.Count==0,"ambiguous choices selected");c.Select("synthetic-second",true);Need(c.CanInstall,"explicit choice ignored");});
        Check("unavailable-entry-cannot-be-selected",()=>{var e=new Fake{Entries=[Choices[0] with{Eligible=false,Reason="missing"}]};var c=new SetupController(e);c.ChooseFolder(@"D:\SyntheticLaunchBox").GetAwaiter().GetResult();c.Select(Choices[0].Id,true);Need(!c.CanInstall,"unavailable entry selected");});
        Check("preview-refusal-never-installs",()=>{var e=new Fake{PreviewExit=3};var c=new SetupController(e);c.ChooseFolder(@"D:\SyntheticLaunchBox").GetAwaiter().GetResult();c.Execute("install").GetAwaiter().GetResult();Need(e.Calls.SequenceEqual(new[]{"preview"})&&!c.Complete&&!c.Busy,"preview gate bypassed");});
        Check("incomplete-recovery-is-not-success",()=>{var e=new Fake{InstallExit=5};var c=new SetupController(e);c.ChooseFolder(@"D:\SyntheticLaunchBox").GetAwaiter().GetResult();c.Execute("install").GetAwaiter().GetResult();Need(!c.Complete&&!c.Status.Contains("all set")&&c.Details.Contains("RECOVERY INCOMPLETE"),"failure shown as success");});
        Check("busy-setup-retains-root-and-refuses-close",()=>{var e=new Fake{Pending=new()};var c=new SetupController(e);c.ChooseFolder(@"D:\SyntheticLaunchBox").GetAwaiter().GetResult();var operation=c.Execute("install");Need(c.Busy&&!c.RequestClose(),"busy close allowed");c.ChooseFolder(@"D:\Other").GetAwaiter().GetResult();Need(c.Root==@"D:\SyntheticLaunchBox","busy root changed");e.Pending.SetResult(new(0,"installed"));operation.GetAwaiter().GetResult();Need(c.Complete&&c.RequestClose(),"completion not released");});
        Check("maintenance-uses-maintenance-action-only",()=>{var e=new Fake{Installed=true};var c=new SetupController(e);c.ChooseFolder(@"D:\SyntheticLaunchBox").GetAwaiter().GetResult();Need(!c.CanInstall&&c.CanMaintain,"maintenance state wrong");c.Execute("verify").GetAwaiter().GetResult();Need(e.Calls.SequenceEqual(new[]{"verify"}),"maintenance installed again");});
        Check("unknown-action-starts-no-process",()=>{var e=new Fake{Installed=true};var c=new SetupController(e);c.ChooseFolder(@"D:\SyntheticLaunchBox").GetAwaiter().GetResult();c.Execute("unknown").GetAwaiter().GetResult();Need(e.Calls.Count==0,"unknown action ran");});
        Check("invalid-path-does-not-latch-busy",()=>{var c=new SetupController(new Fake());c.ChooseFolder("bad\0folder").GetAwaiter().GetResult();Need(!c.Busy&&!c.CanInstall&&c.Details.Length>0,"invalid path escaped state cleanup");});

        byte[] MakeBundle(string? extra=null,bool badInventory=false,bool linked=false)
        {
            var files=new Dictionary<string,byte[]>{{"package.json",Encoding.UTF8.GetBytes("{}")},{"bigbox_install.py",[1,2]},{"shim_install.py",[3,4]},{"setup_bridge.py",[5,6]},{"python/python.exe",[7,8]}};
            if(extra!=null)files[extra]=[9];
            var hashes=files.ToDictionary(x=>x.Key.Replace('/','\\'),x=>PackageBundle.Sha(x.Value));
            if(badInventory)hashes.Remove("bigbox_install.py");
            files["PACKAGE_HASHES.json"]=JsonSerializer.SerializeToUtf8Bytes(hashes);
            using var memory=new MemoryStream();using(var zip=new ZipArchive(memory,ZipArchiveMode.Create,true))foreach(var file in files){var entry=zip.CreateEntry(file.Key);if(linked&&file.Key=="bigbox_install.py")entry.ExternalAttributes=unchecked((int)0xA1FF0000);using var stream=entry.Open();stream.Write(file.Value);}
            return memory.ToArray();
        }
        void Refuse(Action run){bool refused=false;try{run();}catch(Exception e)when(e is IOException or InvalidDataException){refused=true;}Need(refused,"unsafe package accepted");}
        var bundle=MakeBundle();
        Check("bundle-wrong-hash-refused-before-cache",()=>{string p=Path.Combine(output,"bad-hash");Refuse(()=>PackageBundle.Open(bundle,new string('0',64),p));Need(!Directory.Exists(p),"bad hash wrote cache");});
        Check("bundle-path-escape-refused",()=>{string sentinel=Path.Combine(output,"outside.txt");File.WriteAllText(sentinel,"owner fixture");byte[] bad=MakeBundle("../outside.txt");string cache=Path.Combine(output,"escape");Refuse(()=>PackageBundle.Open(bad,PackageBundle.Sha(bad),cache));Need(!Directory.Exists(cache),"unsafe path wrote cache before refusal");Need(File.ReadAllText(sentinel)=="owner fixture","outside file changed");});
        Check("bundle-incomplete-inventory-refused",()=>{byte[] bad=MakeBundle(badInventory:true);Refuse(()=>PackageBundle.Open(bad,PackageBundle.Sha(bad),Path.Combine(output,"bad-inventory")));});
        Check("bundle-cache-reuse-and-tamper-refusal",()=>{string p=Path.Combine(output,"cache");var a=PackageBundle.Open(bundle,PackageBundle.Sha(bundle),p);var b=PackageBundle.Open(bundle,PackageBundle.Sha(bundle),p);Need(a.Root==b.Root,"cache not reused");File.WriteAllText(Path.Combine(a.Root,"bigbox_install.py"),"foreign bytes");Refuse(b.Validate);});
        Check("bundle-extra-cache-module-refused",()=>{var a=PackageBundle.Open(bundle,PackageBundle.Sha(bundle),Path.Combine(output,"extra-cache"));File.WriteAllText(Path.Combine(a.Root,"python/extra.py"),"synthetic foreign module");Refuse(a.Validate);});
        Check("bundle-linked-archive-entry-refused",()=>{byte[] bad=MakeBundle(linked:true);Refuse(()=>PackageBundle.Open(bad,PackageBundle.Sha(bad),Path.Combine(output,"linked-archive")));});
        Check("bundle-cache-junction-refused",()=>
        {
            var a=PackageBundle.Open(bundle,PackageBundle.Sha(bundle),Path.Combine(output,"linked-cache"));
            string from=Path.GetFullPath(Path.Combine(a.Root,"python")),to=Path.GetFullPath(Path.Combine(output,"synthetic-outside","python"));
            string boundary=Path.GetFullPath(output)+Path.DirectorySeparatorChar;
            Need(from.StartsWith(boundary,StringComparison.OrdinalIgnoreCase)&&to.StartsWith(boundary,StringComparison.OrdinalIgnoreCase),"fixture move escaped scratch");
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);Directory.Move(from,to);
            var start=new ProcessStartInfo("cmd.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(string arg in new[]{"/d","/c","mklink","/J",from,to})start.ArgumentList.Add(arg);
            using var process=Process.Start(start)!;process.WaitForExit();Need(process.ExitCode==0,"fixture junction unavailable");Refuse(a.Validate);
        });

        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        Check("actual-window-browse-and-selection-render",()=>
        {
            var e=new Fake();var window=new SetupWindow(()=>Task.FromResult<ISetupEngine>(e),()=>@"D:\SyntheticLaunchBox");window.Initialize().GetAwaiter().GetResult();
            window.BrowseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump(()=>window.InstallButton.IsEnabled);
            Need(window.StatusText.Contains("Choose the emulators"),"actual view not ready");Render(window,Path.Combine(output,"setup-ready.png"));window.Close();
        });
        Check("actual-window-failed-install-does-not-show-done",()=>
        {
            var e=new Fake{InstallExit=5};var window=new SetupWindow(()=>Task.FromResult<ISetupEngine>(e));window.Initialize().GetAwaiter().GetResult();window.SelectFolder(@"D:\SyntheticLaunchBox").GetAwaiter().GetResult();
            window.InstallButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump(()=>window.CloseButton.IsEnabled);
            Need(window.StatusText.Contains("couldn't finish")&&window.CloseButton.Content.ToString()=="Close","view claims success");Render(window,Path.Combine(output,"setup-failure.png"));window.Close();
        });
        Check("actual-window-success-shows-done",()=>
        {
            var e=new Fake();var window=new SetupWindow(()=>Task.FromResult<ISetupEngine>(e));window.Initialize().GetAwaiter().GetResult();window.SelectFolder(@"D:\SyntheticLaunchBox").GetAwaiter().GetResult();
            window.InstallButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump(()=>window.CloseButton.Content.ToString()=="Done");
            Need(!window.InstallButton.IsEnabled&&window.StatusText.Contains("all set"),"success view not complete");Render(window,Path.Combine(output,"setup-done.png"));window.Close();
        });
        app.Shutdown();
        Console.WriteLine($"Cases={pass+fail} Passed={pass} Failed={fail}; M/synthetic engine and archive fixtures, actual offscreen WPF view; no live configuration used");
        return fail==0?0:1;
    }
    private static void Render(SetupWindow window,string output)
    {
        var visual=(FrameworkElement)window.Content;visual.Width=720;visual.Height=740;visual.Measure(new(720,740));visual.Arrange(new(0,0,720,740));visual.UpdateLayout();
        var bitmap=new RenderTargetBitmap(720,740,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(output);png.Save(stream);
    }
    private sealed class Fake:ISetupEngine
    {
        public IReadOnlyList<EmulatorChoice> Entries=Choices;public bool Installed;public int PreviewExit,InstallExit;public TaskCompletionSource<SetupResult>? Pending;public List<string> Calls=[];
        public Task<SetupDiscovery> Discover(string root)=>Task.FromResult(new SetupDiscovery(Installed,Entries));
        public Task<SetupResult> Run(string action,string root,IReadOnlyList<string> ids){Calls.Add(action);if(action=="preview")return Task.FromResult(new SetupResult(PreviewExit,"preview fixture"));if(action=="install"&&Pending!=null)return Pending.Task;return Task.FromResult(new SetupResult(InstallExit,InstallExit==0?"complete":"RECOVERY INCOMPLETE fixture"));}
    }
}
