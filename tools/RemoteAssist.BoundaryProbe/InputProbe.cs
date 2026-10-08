using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using RemoteAssist.Host.Core.Capture;
using RemoteAssist.Host.Core.Input;
using RemoteAssist.Host.Core.Security;
using RemoteAssist.Protocol;

namespace RemoteAssist.BoundaryProbe;

/// <summary>Only injects into a foreground window created by this process. Never types text or passwords.</summary>
internal static class InputProbe
{
    internal static object SecurePointer()
    {
        if(!GetCursorPos(out var original))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var x=original.X+3<GetSystemMetrics(76)+GetSystemMetrics(78)?original.X+3:original.X-3;
        var y=original.Y;
        Input Move(int px,int py)=>new() {Mouse=new MouseInput {X=(int)Math.Round((px-GetSystemMetrics(76))*65535d/Math.Max(1,GetSystemMetrics(78)-1)),Y=(int)Math.Round((py-GetSystemMetrics(77))*65535d/Math.Max(1,GetSystemMetrics(79)-1)),Flags=0xC001}};
        var sent=SendInput(1,[Move(x,y)],Marshal.SizeOf<Input>());
        var error=Marshal.GetLastWin32Error();
        Thread.Sleep(30);
        var read=GetCursorPos(out var actual);
        var restored=SendInput(1,[Move(original.X,original.Y)],Marshal.SizeOf<Input>());
        return new {sent,restored,error,expectedDx=x-original.X,actualDx=actual.X-original.X,receivingInput=Native.ReceivingInput(Native.GetThreadDesktop(Native.GetCurrentThreadId())),observed=sent==1&&read&&Math.Abs(actual.X-x)<=1&&Math.Abs(actual.Y-y)<=1,clicked=false,typed=false};
    }
    delegate nint WindowProcedure(nint window,uint message,nint w,nint l);
    static int clicks,keys;
    static readonly WindowProcedure procedure=(window,message,w,l)=>
    {
        if(message==0x201)clicks++;
        if(message==0x100 && w==0x7B)keys++;
        return DefWindowProc(window,message,w,l);
    };
    internal static object Run()
    {
        clicks=keys=0;var previous=GetForegroundWindow();
        var instance=GetModuleHandle(null);var name="RemoteAssist-E05-"+Environment.ProcessId;
        var cls=new WindowClass {Size=(uint)Marshal.SizeOf<WindowClass>(),Procedure=Marshal.GetFunctionPointerForDelegate(procedure),Instance=instance,ClassName=name,Background=6};
        if(RegisterClassEx(ref cls)==0)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var window=CreateWindowEx(0x8,name,"RemoteAssist E05 — 격리된 입력 시험 창",0x10CF0000,100,100,640,280,0,0,instance,0);
        if(window==0)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        uint mouseSent=0,keySent=0;
        InputSession? remoteInput=null;
        try
        {
            ShowWindow(window,5);SetForegroundWindow(window);
            var clock=Stopwatch.StartNew();
            while(clock.Elapsed<TimeSpan.FromSeconds(5))
            {
                Pump();if(GetForegroundWindow()==window)break;Thread.Sleep(20);
            }
            if(GetForegroundWindow()!=window)return new {kind="input",foreground=false,mouseSent,keySent,clicks,keys};
            if(!GetWindowRect(window,out var rect))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var x=(rect.Left+rect.Right)/2;var y=(rect.Top+rect.Bottom)/2;
            var monitor=Monitors.Enumerate().Single(m=>x>=m.X&&x<m.X+m.Width&&y>=m.Y&&y<m.Y+m.Height);
            var permissions=new PermissionGate();permissions.Grant("local-input-probe","owned-test-window",Capability.Screen|Capability.Control,TimeSpan.FromMinutes(1));
            var measured=new MeasuredBackend();remoteInput=new InputSession(permissions,monitor,1,measured);
            var move=new InputMsg {T="mouse",A="move",SessionId="local-input-probe",Id="probe",Generation=1,PermissionGeneration=1,Sequence=1,
                TopologyGeneration=1,InputEpoch=1,MonitorIdentity=monitor.DeviceName,X=(x-monitor.X)/(double)(monitor.Width-1),Y=(y-monitor.Y)/(double)(monitor.Height-1)};
            remoteInput.Dispatch("pointer",JsonSerializer.SerializeToUtf8Bytes(move));
            Pump();
            if(GetForegroundWindow()==window && WindowFromPoint(new Point {X=x,Y=y})==window)
            {
                remoteInput.Dispatch("input",JsonSerializer.SerializeToUtf8Bytes(move with {A="down",Btn="left",Sequence=1}));
                remoteInput.Dispatch("input",JsonSerializer.SerializeToUtf8Bytes(move with {A="up",Btn="left",Sequence=2}));
            }
            Pump();
            if(GetForegroundWindow()==window)
            {
                remoteInput.Dispatch("input",JsonSerializer.SerializeToUtf8Bytes(move with {T="key",A="down",Vk=0x7B,Sequence=3}));
                remoteInput.Dispatch("input",JsonSerializer.SerializeToUtf8Bytes(move with {T="key",A="up",Vk=0x7B,Sequence=4}));
            }
            mouseSent=measured.Mouse;keySent=measured.Key;
            clock.Restart();while(clock.Elapsed<TimeSpan.FromMilliseconds(500)){Pump();Thread.Sleep(10);}
            return new {utc=DateTimeOffset.UtcNow,kind="input",backend="product-input-session",foreground=GetForegroundWindow()==window,mouseSent,keySent,clicks,keys,success=mouseSent==5&&keySent==2&&clicks==1&&keys==1};
        }
        finally {try{remoteInput?.Stop();}finally{DestroyWindow(window);UnregisterClass(name,instance);if(previous!=0)SetForegroundWindow(previous);}}
    }
    sealed class MeasuredBackend:IInputBackend
    {
        readonly WindowsInputBackend native=new();public uint Mouse,Key;
        public int Send(IReadOnlyList<InputStroke> strokes)
        {var sent=native.Send(strokes);foreach(var stroke in strokes.Take(sent)){if(stroke.Kind is StrokeKind.Key or StrokeKind.Unicode)Key++;else Mouse++;}return sent;}
    }
    static void Pump(){while(PeekMessage(out var message,0,0,0,1)){TranslateMessage(ref message);DispatchMessage(ref message);}}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct WindowClass {public uint Size,Style;public nint Procedure;public int ClassExtra,WindowExtra;public nint Instance,Icon,Cursor,Background;public string? MenuName,ClassName;public nint SmallIcon;}
    [StructLayout(LayoutKind.Sequential)] struct MouseInput {public int X,Y;public uint Data,Flags,Time;public nuint Extra;}
    [StructLayout(LayoutKind.Sequential)] struct KeyboardInput {public ushort VirtualKey,Scan;public uint Flags,Time;public nuint Extra;}
    [StructLayout(LayoutKind.Explicit,Size=40)] struct Input {[FieldOffset(0)]public uint Type;[FieldOffset(8)]public MouseInput Mouse;[FieldOffset(8)]public KeyboardInput Keyboard;}
    [StructLayout(LayoutKind.Sequential)] struct Point {public int X,Y;}
    [StructLayout(LayoutKind.Sequential)] struct Rectangle {public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)] struct Message {public nint Window;public uint Id;public nuint W;public nint L;public uint Time;public Point Point;public uint Private;}
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern ushort RegisterClassEx(ref WindowClass cls);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern bool UnregisterClass(string name,nint instance);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern nint CreateWindowEx(uint exStyle,string cls,string title,uint style,int x,int y,int width,int height,nint parent,nint menu,nint instance,nint parameter);
    [DllImport("user32.dll")] static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] static extern bool ShowWindow(nint window,int command);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern nint DefWindowProc(nint window,uint message,nint w,nint l);
    [DllImport("user32.dll")] static extern bool PeekMessage(out Message message,nint window,uint min,uint max,uint remove);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint window,out Rectangle rect);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll",SetLastError=true)] static extern uint SendInput(uint count,Input[] inputs,int bytes);
}
