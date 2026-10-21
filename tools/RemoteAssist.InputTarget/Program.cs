using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Automation;
using System.Windows.Media;
using Rectangle=System.Windows.Shapes.Rectangle;

internal static class Program
{
    [STAThread] static int Main(string[] args)
    {
        if(args.Length is not (4 or 6)||args[0]!="--output"||args[2]!="--seconds"||!int.TryParse(args[3],out var seconds)||seconds is <30 or >900)return 2;
        string? marker=null;
        if(args.Length==6){if(args[4]!="--marker"||!System.Text.RegularExpressions.Regex.IsMatch(args[5],"^[a-f0-9]{32}$"))return 2;marker=args[5];}
        var directory=Path.GetFullPath(args[1]);Directory.CreateDirectory(directory);Native.SetProcessDpiAwarenessContext(-4);
        try{var app=new Application();app.Run(new Target(directory,seconds,marker));return 0;}
        catch(Exception ex){File.WriteAllText(Path.Combine(directory,"input-target-error.json"),JsonSerializer.Serialize(new {utc=DateTimeOffset.UtcNow,error=ex.GetType().Name,stack=ex.StackTrace}));return 1;}
    }
    sealed class Target:Window
    {
        readonly string directory;
        readonly Button button=new(){Content="시험 클릭",Height=70,Margin=new Thickness(0,20,0,20)};
        readonly TextBox text=new(){Height=80,AcceptsReturn=true,FontSize=24};
        readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromMilliseconds(50)};
        readonly Canvas markerCanvas=new(){Width=128,Height=64,Background=Brushes.White,HorizontalAlignment=HorizontalAlignment.Left};
        readonly string? markerId;
        int clicks,f12Down,f12Up,ctrlDown,ctrlUp,drag,wheel,wheelDeltaSum,dragCount;
        bool dragging;
        readonly List<object> dragPoints=new();
        DateTimeOffset? downUtc,upUtc;
        readonly List<object> events=new();
        readonly long startTick=Stopwatch.GetTimestamp();
        bool observationOverflow;
        void Record(string kind, int code, string action)
        {
            if(events.Count>=4096){observationOverflow=true;return;}
            events.Add(new {tick=Stopwatch.GetTimestamp(),kind,code,action});
        }
        public Target(string directory,int seconds,string? marker)
        {
            markerId=marker;
            this.directory=directory;Title="RemoteAssist 격리 입력 시험";Width=600;Height=380;WindowStartupLocation=WindowStartupLocation.CenterScreen;
            var panel=new StackPanel {Margin=new Thickness(24)};Content=panel;
            if(marker is not null){
                Height=480;
                var bytes=Convert.FromHexString(marker);
                for(var i=0;i<128;i++){
                    var square=new Rectangle{Width=8,Height=8,Fill=(bytes[i/8]&(1<<(7-i%8)))!=0?Brushes.White:Brushes.Black};
                    Canvas.SetLeft(square,(i%16)*8);Canvas.SetTop(square,(i/16)*8);markerCanvas.Children.Add(square);
                }
                panel.Children.Add(markerCanvas);
            }
            panel.Children.Add(new TextBlock {Text="원격 입력·키 복원 검증용 격리 창이다. 실제 앱·비밀번호를 조작하지 않는다.",TextWrapping=TextWrapping.Wrap});
            panel.Children.Add(button);panel.Children.Add(text);
            AutomationProperties.SetAutomationId(button,"FixtureButton");
            AutomationProperties.SetAutomationId(text,"FixtureText");
            button.Click+=(_,_)=>{clicks++;text.Focus();};
            PreviewKeyDown+=(_,e)=>Record("key",KeyInterop.VirtualKeyFromKey(e.Key==Key.System?e.SystemKey:e.Key),"down");
            PreviewKeyUp+=(_,e)=>Record("key",KeyInterop.VirtualKeyFromKey(e.Key==Key.System?e.SystemKey:e.Key),"up");
            PreviewMouseDown+=(_,e)=>{Record("button",(int)e.ChangedButton,"down");if(e.ChangedButton==MouseButton.Left){dragging=true;dragPoints.Clear();var p=PointToScreen(e.GetPosition(this));dragPoints.Add(new {x=p.X,y=p.Y,tick=Stopwatch.GetTimestamp()});}};
            PreviewMouseUp+=(_,e)=>{Record("button",(int)e.ChangedButton,"up");if(e.ChangedButton==MouseButton.Left){if(dragging&&dragPoints.Count>1)dragCount++;dragging=false;}};
            PreviewKeyDown+=(_,e)=>{if(e.Key==Key.F12){f12Down++;e.Handled=true;}if(e.Key==Key.LeftCtrl){ctrlDown++;downUtc=DateTimeOffset.UtcNow;}};
            PreviewKeyUp+=(_,e)=>{if(e.Key==Key.F12){f12Up++;e.Handled=true;}if(e.Key==Key.LeftCtrl){ctrlUp++;upUtc=DateTimeOffset.UtcNow;}};
            PreviewMouseMove+=(_,e)=>{if(e.LeftButton==MouseButtonState.Pressed){drag++;var p=PointToScreen(e.GetPosition(this));if(dragPoints.Count<512)dragPoints.Add(new {x=p.X,y=p.Y,tick=Stopwatch.GetTimestamp()});else observationOverflow=true;}};
            PreviewMouseWheel+=(_,e)=>{wheel++;wheelDeltaSum+=e.Delta;Record("wheel",e.Delta,"delta");};
            var clock=System.Diagnostics.Stopwatch.StartNew();
            timer.Tick+=(_,_)=>
            {
                Snapshot();
                if(File.Exists(Path.Combine(directory,"target-focus"))){Native.SetForegroundWindow(new WindowInteropHelper(this).Handle);File.Delete(Path.Combine(directory,"target-focus"));}
                if(clock.Elapsed.TotalSeconds>=seconds||File.Exists(Path.Combine(directory,"target-finish")))Close();
            };
            Loaded+=(_,_)=>{timer.Start();Activate();};Closing+=(_,_)=>Snapshot();Closed+=(_,_)=>timer.Stop();
        }
        void Snapshot()
        {
            var b=button.PointToScreen(new(button.ActualWidth/2,button.ActualHeight/2));var t=text.PointToScreen(new(text.ActualWidth/2,text.ActualHeight/2));
            var hwnd=new WindowInteropHelper(this).Handle;
            var markerOrigin=markerId is not null?markerCanvas.PointToScreen(new(0,0)):new Point();
            var markerEnd=markerId is not null?markerCanvas.PointToScreen(new(markerCanvas.ActualWidth,markerCanvas.ActualHeight)):new Point();
            var origin=PointToScreen(new(0,0));var corner=PointToScreen(new(ActualWidth,ActualHeight));
            var held=Enumerable.Range(1,254).Where(vk=>(Native.GetAsyncKeyState(vk)&0x8000)!=0).ToArray();
            using var process=Process.GetCurrentProcess();
            var value=new {utc=DateTimeOffset.UtcNow,tick=Stopwatch.GetTimestamp(),startTick,tickFrequency=Stopwatch.Frequency,
                pid=Environment.ProcessId,session=process.SessionId,image=Environment.ProcessPath,createdAt=process.StartTime.ToUniversalTime().Ticks,
                window=hwnd.ToInt64(),foreground=Native.GetForegroundWindow()==hwnd,
                bounds=new {left=origin.X,top=origin.Y,right=corner.X,bottom=corner.Y},
                heldVirtualKeys=held,events,observationOverflow,observationValid=!observationOverflow,
                clicks,f12Down,f12Up,ctrlDown,ctrlUp,ctrlHeld=(Native.GetAsyncKeyState(162)&0x8000)!=0,ctrlDownUtc=downUtc,ctrlUpUtc=upUtc,drag,wheel,
                wheelDeltaSum,dragCount,dragPoints,textFocused=text.IsKeyboardFocused,
                marker=markerId is null?null:new {id=markerId,x=markerOrigin.X,y=markerOrigin.Y,width=markerEnd.X-markerOrigin.X,height=markerEnd.Y-markerOrigin.Y},
                button=new {x=b.X,y=b.Y},textBox=new {x=t.X,y=t.Y},text=text.Text};
            var path=Path.Combine(directory,"input-target.json");
            try{File.WriteAllBytes(path+".tmp",JsonSerializer.SerializeToUtf8Bytes(value));File.Move(path+".tmp",path,true);}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){}
        }
    }
    static class Native
    {
        [DllImport("user32.dll")]public static extern bool SetProcessDpiAwarenessContext(nint context);
        [DllImport("user32.dll")]public static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")]public static extern nint GetForegroundWindow();
        [DllImport("user32.dll")]public static extern bool SetForegroundWindow(nint window);
    }
}
