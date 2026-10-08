using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using RemoteAssist.Protocol;

namespace RemoteAssist.Host.Tray;

internal sealed class SharingIndicator:IDisposable
{
    readonly Window border,stop;
    bool disposed;
    public SharingIndicator(MonitorDescriptor monitor,Action end)
    {
        if(monitor.Width<2||monitor.Height<2)throw new InvalidDataException("공유 모니터 표시 오류다.");
        border=new Window {Title="RemoteAssist 공유 테두리",WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,AllowsTransparency=true,
            Background=Brushes.Transparent,ShowInTaskbar=false,ShowActivated=false,Topmost=true,IsHitTestVisible=false,
            Content=new Border {BorderBrush=Brushes.Crimson,BorderThickness=new Thickness(5)}};
        stop=new Window {Title="RemoteAssist 공유 표시",WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,ShowInTaskbar=false,ShowActivated=false,Topmost=true,
            Background=Brushes.Crimson,Width=360,Height=52};
        var button=new Button {Content="원격 지원 중 · 즉시 종료\nCtrl+Alt+Shift+F10",Background=Brushes.Crimson,Foreground=Brushes.White,FontWeight=FontWeights.Bold};
        System.Windows.Automation.AutomationProperties.SetAutomationId(button,"SharedMonitorStop");button.Click+=(_,_)=>end();stop.Content=button;
        border.Closed+=(_,_)=>{if(!disposed)end();};stop.Closed+=(_,_)=>{if(!disposed)end();};
        try
        {
            var borderHandle=new WindowInteropHelper(border).EnsureHandle();
            SetStyles(borderHandle,0x080000A0); // NOACTIVATE | TOOLWINDOW | TRANSPARENT
            border.Show();
            Check(SetWindowPos(borderHandle,-1,monitor.X,monitor.Y,monitor.Width,monitor.Height,0x10));
            var stopHandle=new WindowInteropHelper(stop).EnsureHandle();
            SetStyles(stopHandle,0x08000080);
            stop.Show();Check(SetWindowPos(stopHandle,-1,monitor.X+16,monitor.Y+16,360,52,0x10));
        }
        catch{Dispose();throw;}
    }
    public void Dispose(){if(disposed)return;disposed=true;stop.Close();border.Close();}
    static void Check(bool success){if(!success)throw new Win32Exception(Marshal.GetLastWin32Error());}
    static void SetStyles(nint handle,nint flags)
    {
        Marshal.SetLastPInvokeError(0);var style=GetWindowLongPtr(handle,-20);var error=Marshal.GetLastPInvokeError();
        if(style==0&&error!=0)throw new Win32Exception(error);
        Marshal.SetLastPInvokeError(0);var previous=SetWindowLongPtr(handle,-20,style|flags);error=Marshal.GetLastPInvokeError();
        if(previous==0&&error!=0)throw new Win32Exception(error);
    }
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW",SetLastError=true)]static extern nint GetWindowLongPtr(nint window,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW",SetLastError=true)]static extern nint SetWindowLongPtr(nint window,int index,nint value);
    [DllImport("user32.dll",SetLastError=true)]static extern bool SetWindowPos(nint window,nint after,int x,int y,int width,int height,uint flags);
}
