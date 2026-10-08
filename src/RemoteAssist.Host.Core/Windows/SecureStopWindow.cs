using System.ComponentModel;
using System.Runtime.InteropServices;
using RemoteAssist.Protocol;

namespace RemoteAssist.Host.Core.Windows;

/// <summary>A fixed stop-only window on the approved secure desktop. No remote command dispatch or text from messages.</summary>
public sealed class SecureStopWindow:IDisposable
{
    readonly string className="RemoteAssistStop"+Guid.NewGuid().ToString("N");
    readonly WindowProc procedure;
    readonly Action stop;
    nint window,button;
    bool registered,hotkey,disposed;
    public SecureStopWindow(MonitorDescriptor monitor,Action stop)
    {
        this.stop=stop;procedure=Handle;
        try
        {
            var klass=new WindowClass {Size=(uint)Marshal.SizeOf<WindowClass>(),Procedure=procedure,Instance=GetModuleHandle(null),Cursor=LoadCursor(0,32512),Background=GetSysColorBrush(5),ClassName=className};
            if(RegisterClassEx(ref klass)==0)throw new Win32Exception(Marshal.GetLastWin32Error());registered=true;
            window=CreateWindowEx(0x08000088,className,"RemoteAssist 관리자 지원",0x90000000,monitor.X+16,monitor.Y+16,430,64,0,0,klass.Instance,0);
            if(window==0)throw new Win32Exception(Marshal.GetLastWin32Error());
            button=CreateWindowEx(0,"BUTTON","관리자 지원 중 · 즉시 종료\nCtrl+Alt+Shift+F9 · 최대 30초",0x50002000,0,0,430,64,window,1,klass.Instance,0);
            if(button==0||!RegisterHotKey(window,1,0x4007,0x78))throw new Win32Exception(Marshal.GetLastWin32Error());hotkey=true;
            if(!SetWindowPos(window,-1,monitor.X+16,monitor.Y+16,430,64,0x50))throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch{Dispose();throw;}
    }
    public void Pump(){while(PeekMessage(out var message,0,0,0,1)){TranslateMessage(ref message);DispatchMessage(ref message);}}
    nint Handle(nint hwnd,uint message,nuint wparam,nint lparam)
    {
        if(!disposed&&(message==0x10||message==0x312&&(int)wparam==1||message==0x111&&(wparam&0xffff)==1))
        {try{stop();}catch{}return 0;}
        return DefWindowProc(hwnd,message,wparam,lparam);
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;if(hotkey)UnregisterHotKey(window,1);if(window!=0)DestroyWindow(window);
        if(registered)UnregisterClass(className,GetModuleHandle(null));GC.KeepAlive(procedure);
    }
    delegate nint WindowProc(nint hwnd,uint message,nuint wparam,nint lparam);
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]struct WindowClass
    {public uint Size,Style;public WindowProc Procedure;public int ClassExtra,WindowExtra;public nint Instance,Icon,Cursor,Background;public string? MenuName;public string ClassName;public nint SmallIcon;}
    [StructLayout(LayoutKind.Sequential)]struct Message{public nint Window;public uint Id;public nuint WParam;public nint LParam;public uint Time;public int X,Y;public uint Private;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll",EntryPoint="RegisterClassExW",CharSet=CharSet.Unicode,SetLastError=true)]static extern ushort RegisterClassEx(ref WindowClass klass);
    [DllImport("user32.dll",EntryPoint="CreateWindowExW",CharSet=CharSet.Unicode,SetLastError=true)]static extern nint CreateWindowEx(uint exStyle,string klass,string title,uint style,int x,int y,int width,int height,nint parent,nint menu,nint instance,nint parameter);
    [DllImport("user32.dll",EntryPoint="DefWindowProcW")]static extern nint DefWindowProc(nint hwnd,uint message,nuint wparam,nint lparam);
    [DllImport("user32.dll")]static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll",EntryPoint="UnregisterClassW",CharSet=CharSet.Unicode)]static extern bool UnregisterClass(string klass,nint instance);
    [DllImport("user32.dll",EntryPoint="LoadCursorW")]static extern nint LoadCursor(nint instance,nint resource);
    [DllImport("user32.dll")]static extern nint GetSysColorBrush(int color);
    [DllImport("user32.dll",SetLastError=true)]static extern bool RegisterHotKey(nint window,int id,uint modifiers,uint key);
    [DllImport("user32.dll")]static extern bool UnregisterHotKey(nint window,int id);
    [DllImport("user32.dll",SetLastError=true)]static extern bool SetWindowPos(nint window,nint after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll",EntryPoint="PeekMessageW")]static extern bool PeekMessage(out Message message,nint window,uint min,uint max,uint flags);
    [DllImport("user32.dll")]static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll",EntryPoint="DispatchMessageW")]static extern nint DispatchMessage(ref Message message);
}
