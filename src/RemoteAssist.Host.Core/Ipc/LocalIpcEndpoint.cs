using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using RemoteAssist.Host.Core.Windows;

namespace RemoteAssist.Host.Core.Ipc;

public static class LocalIpcEndpoint
{
    public static NamedPipeServerStream CreateServer(string name,string userSid)
    {
        if(!Guid.TryParseExact(name,"N",out _))throw new ArgumentException("고유한 pipe 이름이 필요하다.");
        using var identity=WindowsIdentity.GetCurrent();
        if(!identity.IsSystem||Process.GetCurrentProcess().SessionId!=0)throw new UnauthorizedAccessException("IPC 서버는 Session 0 SYSTEM 감독이다.");
        var sid=new SecurityIdentifier(userSid).Value;
        Check(ConvertStringSecurityDescriptorToSecurityDescriptor($"D:P(A;;GA;;;SY)(A;;GRGW;;;{sid})",1,out var descriptor,out _));
        try
        {
            var attributes=new SecurityAttributes {Size=Marshal.SizeOf<SecurityAttributes>(),Descriptor=descriptor};
            // FILE_FLAG_FIRST_PIPE_INSTANCE; byte mode; PIPE_REJECT_REMOTE_CLIENTS. No network IPC.
            var handle=CreateNamedPipe("\\\\.\\pipe\\"+name,0x40080003,8,1,32768,32768,1000,ref attributes);
            if(handle.IsInvalid){handle.Dispose();throw new Win32Exception(Marshal.GetLastWin32Error());}
            return new NamedPipeServerStream(PipeDirection.InOut,true,false,handle);
        }
        finally{LocalFree(descriptor);}
    }
    public static async Task<LocalIpcChannel> AcceptAsync(NamedPipeServerStream server,SessionProcess child,string nonce,CancellationToken ct)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            while(true)
            {
                await server.WaitForConnectionAsync(deadline.Token);
                Check(GetNamedPipeClientProcessId(server.SafePipeHandle,out var pid));
                Check(GetNamedPipeClientSessionId(server.SafePipeHandle,out var session));
                if(pid!=child.Pid||session!=child.SessionId){server.Disconnect();continue;}
                using var process=OpenProcess(0x1000,false,pid);if(process.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
                Check(OpenProcessToken(process,8,out var token));
                using(token)using(var identity=new WindowsIdentity(token.DangerousGetHandle()))
                    if(identity.User?.Value!=child.OwnerSid)throw new UnauthorizedAccessException("IPC 자식 SID가 바뀌었다.");
                var role=child.Role.ToString().ToLowerInvariant();
                var expected=ProtectedInstallation.ResolveExecutable(child.Role switch {HostChildRole.Tray=>"RemoteAssist.Host.Tray.exe",HostChildRole.Helper=>"RemoteAssist.Host.Helper.exe",HostChildRole.Recovery=>"RemoteAssist.Host.Recovery.exe",HostChildRole.RunbookWorker or HostChildRole.AdminRunbookWorker=>"RemoteAssist.Host.RunbookWorker.exe",_=>"RemoteAssist.Host.AdminHelper.exe"});
                if(!Image(process).Equals(expected,StringComparison.OrdinalIgnoreCase))throw new UnauthorizedAccessException("IPC 자식 실행 파일 불일치다.");
                var channel=new LocalIpcChannel(server);
                await channel.AuthenticateAsync("service",role,nonce,deadline.Token);return channel;
            }
        }
        catch{await server.DisposeAsync();throw;}
    }
    public static async Task<LocalIpcChannel> ConnectAsync(string name,string nonce,int servicePid,string role,CancellationToken ct)
    {
        Deployment.InstallationState.CheckCandidate();
        if(!Guid.TryParseExact(name,"N",out _)||servicePid<=0||role is not ("runbookworker" or "adminrunbookworker" or "tray" or "helper" or "adminhelper" or "recovery"))throw new ArgumentException("IPC bootstrap 오류다.");
        var pipe=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous,TokenImpersonationLevel.Identification);
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await pipe.ConnectAsync(deadline.Token);
            var (pid,session)=ServerIdentity(pipe.SafePipeHandle);
            if(pid!=servicePid||session!=0)throw new UnauthorizedAccessException("IPC 서비스 PID/Session이 다르다.");
            ServiceIdentity.Demand(pid,ProtectedInstallation.ResolveExecutable("RemoteAssist.Host.Service.exe"));
            var channel=new LocalIpcChannel(pipe);await channel.AuthenticateAsync(role,"service",nonce,deadline.Token);return channel;
        }
        catch(Exception error){LocalDiagnostic.Failure("ipc-connect",error);await pipe.DisposeAsync();throw;}
    }
    static string Image(SessionProcess.KernelHandle process)
    {
        var text=new StringBuilder(32768);var length=(uint)text.Capacity;Check(QueryFullProcessImageName(process,0,text,ref length));return Path.GetFullPath(text.ToString());
    }
    internal static (uint Pid,uint Session) ServerIdentity(SafePipeHandle handle)
    {Check(GetNamedPipeServerProcessId(handle,out var pid));Check(GetNamedPipeServerSessionId(handle,out var session));return (pid,session);}
    static void Check(bool result){if(!result)throw new Win32Exception(Marshal.GetLastWin32Error());}
    [StructLayout(LayoutKind.Sequential)] struct SecurityAttributes {public int Size;public nint Descriptor;public int Inherit;}
    [DllImport("advapi32.dll",EntryPoint="ConvertStringSecurityDescriptorToSecurityDescriptorW",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl,uint revision,out nint descriptor,out uint bytes);
    [DllImport("kernel32.dll")] static extern nint LocalFree(nint pointer);
    [DllImport("kernel32.dll",EntryPoint="CreateNamedPipeW",CharSet=CharSet.Unicode,SetLastError=true)] static extern SafePipeHandle CreateNamedPipe(string name,uint openMode,uint pipeMode,uint instances,uint outbound,uint inbound,uint timeout,ref SecurityAttributes attributes);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe,out uint pid);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe,out uint session);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe,out uint pid);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetNamedPipeServerSessionId(SafePipeHandle pipe,out uint session);
    [DllImport("kernel32.dll",SetLastError=true)] static extern SessionProcess.KernelHandle OpenProcess(uint access,bool inherit,uint pid);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool OpenProcessToken(SessionProcess.KernelHandle process,uint access,out SessionProcess.KernelHandle token);
    [DllImport("kernel32.dll",EntryPoint="QueryFullProcessImageNameW",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool QueryFullProcessImageName(SessionProcess.KernelHandle process,uint flags,StringBuilder image,ref uint length);
}
