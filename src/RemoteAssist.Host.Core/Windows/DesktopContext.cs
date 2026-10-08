using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace RemoteAssist.Host.Core.Windows;

/// <summary>Thread-affine input desktop lease. Destroy all windows/hooks before disposal.</summary>
public sealed class DesktopContext:IDisposable
{
    readonly nint original;
    nint desktop;
    public string Name {get;}
    public bool IsReceivingInput=>desktop!=0&&GetUserObjectInformation(desktop,6,out int value,4,out _)&&value!=0;
    public DesktopContext()
    {
        original=GetThreadDesktop(GetCurrentThreadId());desktop=OpenInputDesktop(0,false,0x1FF);
        try
        {
            if(desktop==0||!SetThreadDesktop(desktop))throw new Win32Exception(Marshal.GetLastWin32Error());
            GetUserObjectInformation(desktop,2,null,0,out var bytes);
            if(bytes==0||bytes>512)throw new InvalidDataException("desktop 이름 크기 오류다.");
            var name=new StringBuilder((int)bytes/2);if(!GetUserObjectInformation(desktop,2,name,bytes,out _))throw new Win32Exception(Marshal.GetLastWin32Error());Name=name.ToString();
            if(Name is not ("Default" or "Winlogon"))throw new UnauthorizedAccessException("지원하지 않는 input desktop이다.");
        }
        catch{Dispose();throw;}
    }
    public void Dispose(){if(desktop==0)return;SetThreadDesktop(original);CloseDesktop(desktop);desktop=0;}
    [DllImport("kernel32.dll")]static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]static extern nint GetThreadDesktop(uint thread);
    [DllImport("user32.dll",SetLastError=true)]static extern nint OpenInputDesktop(uint flags,bool inherit,uint access);
    [DllImport("user32.dll",SetLastError=true)]static extern bool SetThreadDesktop(nint desktop);
    [DllImport("user32.dll")]static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll",EntryPoint="GetUserObjectInformationW",SetLastError=true)]static extern bool GetUserObjectInformation(nint handle,int index,out int value,uint size,out uint needed);
    [DllImport("user32.dll",EntryPoint="GetUserObjectInformationW",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool GetUserObjectInformation(nint handle,int index,StringBuilder? value,uint size,out uint needed);
}
