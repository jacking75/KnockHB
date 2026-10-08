using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace RemoteAssist.Host.Tray;

internal sealed class NativeTrayIcon : IDisposable
{
    const int Callback=0x8001;
    readonly Window window;
    readonly HwndSource source;
    readonly Action stop;
    readonly int taskbarCreated;
    readonly nint idleIcon,activeIcon;
    bool active,hotkey;
    NotifyData data;
    public NativeTrayIcon(Window window,Action stop)
    {
        this.window=window;this.stop=stop;source=HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        try
        {
            taskbarCreated=(int)RegisterWindowMessage("TaskbarCreated");if(taskbarCreated==0)throw new InvalidOperationException("트레이 재시작 감시 등록 실패다.");
            idleIcon=Circle(false);activeIcon=Circle(true);
            data=new NotifyData {Size=(uint)Marshal.SizeOf<NotifyData>(),Window=source.Handle,Id=1,Flags=7,Callback=Callback,Icon=idleIcon,Tip="RemoteAssist — 지원 대기",Info="",Title=""};
            if(!ShellNotifyIcon(0,ref data))throw new InvalidOperationException("트레이 상태 표시를 만들 수 없다.");
            source.AddHook(Hook);
        }
        catch{if(idleIcon!=0)DestroyIcon(idleIcon);if(activeIcon!=0)DestroyIcon(activeIcon);throw;}
    }
    public void SetActive(bool value)
    {
        active=value;
        if(active&&!hotkey){if(!RegisterHotKey(source.Handle,1,0x4007,0x79))throw new InvalidOperationException("긴급 종료 단축키가 다른 앱과 충돌했다.");hotkey=true;}
        if(!active&&hotkey){UnregisterHotKey(source.Handle,1);hotkey=false;}
        data.Icon=active?activeIcon:idleIcon;data.Tip=active?"RemoteAssist — 공유 중 / 우클릭 즉시 종료 / Ctrl+Alt+Shift+F10":"RemoteAssist — 지원 대기";
        if(!ShellNotifyIcon(1,ref data))throw new InvalidOperationException("트레이 공유 상태 표시가 상실됐다.");
    }
    nint Hook(nint hwnd,int message,nint wParam,nint lParam,ref bool handled)
    {
        if(message==0x312&&(int)wParam==1){stop();handled=true;}
        if(message==taskbarCreated&&!ShellNotifyIcon(0,ref data))stop();
        if(message==Callback&&(int)lParam==0x205&&active){stop();handled=true;}
        else if(message==Callback&&((int)lParam==0x202||(int)lParam==0x205)){window.Show();window.WindowState=WindowState.Normal;window.Activate();handled=true;}
        return 0;
    }
    public void Dispose(){if(hotkey)UnregisterHotKey(source.Handle,1);ShellNotifyIcon(2,ref data);source.RemoveHook(Hook);DestroyIcon(idleIcon);DestroyIcon(activeIcon);}
    static nint Circle(bool active)
    {
        var mask=Enumerable.Repeat((byte)255,32).ToArray();var pixels=new byte[16*16*4];
        for(var y=0;y<16;y++)for(var x=0;x<16;x++)if((x-7.5)*(x-7.5)+(y-7.5)*(y-7.5)<=42)
        {mask[y*2+x/8]&=(byte)~(1<<(7-x%8));var p=(y*16+x)*4;pixels[p]=active?(byte)50:(byte)180;pixels[p+1]=active?(byte)30:(byte)110;pixels[p+2]=active?(byte)220:(byte)25;pixels[p+3]=255;}
        var icon=CreateIcon(0,16,16,1,32,mask,pixels);if(icon==0)throw new InvalidOperationException("트레이 상태 아이콘 생성 실패다.");return icon;
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    struct NotifyData
    {
        public uint Size;public nint Window;public uint Id,Flags,Callback;public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string Tip;
        public uint State,StateMask;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)]public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)]public string Title;
        public uint InfoFlags;public Guid Guid;public nint BalloonIcon;
    }
    [DllImport("shell32.dll",EntryPoint="Shell_NotifyIconW",CharSet=CharSet.Unicode)]static extern bool ShellNotifyIcon(uint message,ref NotifyData data);
    [DllImport("user32.dll",EntryPoint="RegisterWindowMessageW",CharSet=CharSet.Unicode)]static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll",SetLastError=true)]static extern bool RegisterHotKey(nint window,int id,uint modifiers,uint key);
    [DllImport("user32.dll")]static extern bool UnregisterHotKey(nint window,int id);
    [DllImport("user32.dll",SetLastError=true)]static extern nint CreateIcon(nint instance,int width,int height,byte planes,byte bits,byte[] mask,byte[] pixels);
    [DllImport("user32.dll")]static extern bool DestroyIcon(nint icon);
}
