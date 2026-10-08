using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace RemoteAssist.Host.Service;

internal static class SasBroker
{
    public static void Send()
    {
        using var policy=Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
        if(policy?.GetValue("SoftwareSASGeneration") is not int value||value is not (1 or 3))throw new UnauthorizedAccessException("서비스 SAS 허용 정책을 조직 관리자가 설정해야 한다.");
        SendSAS(false); // A void return is an attempted invocation, never proof of desktop transition.
    }
    [DllImport("sas.dll")]static extern void SendSAS([MarshalAs(UnmanagedType.Bool)]bool asUser);
}
