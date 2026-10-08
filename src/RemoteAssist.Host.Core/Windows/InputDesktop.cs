using System.Runtime.InteropServices;

namespace RemoteAssist.Host.Core.Windows;

public static class InputDesktop
{
    public static bool IsDefault()
    {
        var desktop=OpenInputDesktop(0,false,1);
        if(desktop==0)return false;
        try
        {
            GetUserObjectInformation(desktop,2,0,0,out var length);
            if(length is <2 or >4096)return false;
            var buffer=Marshal.AllocHGlobal((int)length);
            try{return GetUserObjectInformation(desktop,2,buffer,length,out _)&&string.Equals(Marshal.PtrToStringUni(buffer),"Default",StringComparison.Ordinal);}
            finally{Marshal.FreeHGlobal(buffer);}
        }
        finally{CloseDesktop(desktop);}
    }
    [DllImport("user32.dll",SetLastError=true)]static extern nint OpenInputDesktop(uint flags,bool inherit,uint access);
    [DllImport("user32.dll",EntryPoint="GetUserObjectInformationW",SetLastError=true)]static extern bool GetUserObjectInformation(nint handle,int index,nint buffer,uint size,out uint needed);
    [DllImport("user32.dll")]static extern bool CloseDesktop(nint desktop);
}
