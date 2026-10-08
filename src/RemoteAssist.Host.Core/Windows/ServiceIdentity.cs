using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RemoteAssist.Host.Core.Windows;

internal static class ServiceIdentity
{
    public static void Demand(uint expectedPid,string protectedImage)
    {
        using var manager=OpenSCManager(null,null,1);Check(!manager.IsInvalid);
        using var service=OpenService(manager,"RemoteAssistHost",5);Check(!service.IsInvalid);
        var size=Marshal.SizeOf<ServiceStatus>();var statusMemory=Marshal.AllocHGlobal(size);
        try
        {
            Check(QueryServiceStatusEx(service,0,statusMemory,(uint)size,out _));
            var status=Marshal.PtrToStructure<ServiceStatus>(statusMemory);
            if(status.Type!=0x10||status.State!=4||status.Pid!=expectedPid)throw new UnauthorizedAccessException("현재 감독 서비스 PID/유형 불일치다.");
        }
        finally{Marshal.FreeHGlobal(statusMemory);}
        QueryServiceConfig(service,0,0,out var needed);
        if(Marshal.GetLastWin32Error()!=122||needed is 0 or >8192)throw new Win32Exception(Marshal.GetLastWin32Error());
        var configMemory=Marshal.AllocHGlobal((int)needed);
        try
        {
            Check(QueryServiceConfig(service,configMemory,needed,out _));
            var config=Marshal.PtrToStructure<ServiceConfig>(configMemory);
            var command=Marshal.PtrToStringUni(config.BinaryPath)??"";
            var account=Marshal.PtrToStringUni(config.StartName)??"";
            if(config.Type!=0x10||!account.Equals("LocalSystem",StringComparison.OrdinalIgnoreCase)||
                !command.Equals("\""+protectedImage+"\"",StringComparison.OrdinalIgnoreCase))throw new UnauthorizedAccessException("감독 서비스 보호 실행 계약 불일치다.");
        }
        finally{Marshal.FreeHGlobal(configMemory);}
    }
    static void Check(bool result){if(!result)throw new Win32Exception(Marshal.GetLastWin32Error());}
    [StructLayout(LayoutKind.Sequential)]struct ServiceStatus {public uint Type,State,Accepted,Win32Exit,SpecificExit,Checkpoint,WaitHint,Pid,Flags;}
    [StructLayout(LayoutKind.Sequential)]struct ServiceConfig {public uint Type,StartType,ErrorControl;public nint BinaryPath,Group;public uint Tag;public nint Dependencies,StartName,DisplayName;}
    sealed class ServiceHandle:SafeHandleZeroOrMinusOneIsInvalid
    {public ServiceHandle():base(true){}protected override bool ReleaseHandle()=>CloseServiceHandle(handle);}
    [DllImport("advapi32.dll",EntryPoint="OpenSCManagerW",CharSet=CharSet.Unicode,SetLastError=true)]static extern ServiceHandle OpenSCManager(string? machine,string? database,uint access);
    [DllImport("advapi32.dll",EntryPoint="OpenServiceW",CharSet=CharSet.Unicode,SetLastError=true)]static extern ServiceHandle OpenService(ServiceHandle manager,string name,uint access);
    [DllImport("advapi32.dll",SetLastError=true)]static extern bool QueryServiceStatusEx(ServiceHandle service,uint level,nint buffer,uint size,out uint needed);
    [DllImport("advapi32.dll",EntryPoint="QueryServiceConfigW",SetLastError=true)]static extern bool QueryServiceConfig(ServiceHandle service,nint buffer,uint size,out uint needed);
    [DllImport("advapi32.dll")]static extern bool CloseServiceHandle(nint service);
}
