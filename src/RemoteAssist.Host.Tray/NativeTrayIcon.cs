using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace RemoteAssist.Host.Tray;

internal sealed class NativeTrayIcon : IDisposable
{
    const int Callback=0x8001;
    readonly Window window;
    readonly HwndSource source;
    NotifyData data;
    public NativeTrayIcon(Window window)
    {
        this.window=window;source=HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);source.AddHook(Hook);
        data=new NotifyData {Size=(uint)Marshal.SizeOf<NotifyData>(),Window=source.Handle,Id=1,Flags=7,Callback=Callback,Icon=LoadIcon(0,32512),Tip="RemoteAssist — 지원 대기",Info="",Title=""};
        if(!ShellNotifyIcon(0,ref data))throw new InvalidOperationException("트레이 상태 표시를 만들 수 없다.");
    }
    public void SetActive(bool active){data.Tip=active?"RemoteAssist — 화면 공유 중 / 클릭하여 즉시 종료":"RemoteAssist — 지원 대기";ShellNotifyIcon(1,ref data);}
    nint Hook(nint hwnd,int message,nint wParam,nint lParam,ref bool handled)
    {
        if(message==Callback&&((int)lParam==0x202||(int)lParam==0x205)){window.Show();window.WindowState=WindowState.Normal;window.Activate();handled=true;}
        return 0;
    }
    public void Dispose(){ShellNotifyIcon(2,ref data);source.RemoveHook(Hook);}
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
    [DllImport("user32.dll",EntryPoint="LoadIconW")]static extern nint LoadIcon(nint instance,nint name);
}
