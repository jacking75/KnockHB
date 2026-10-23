using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using RemoteAssist.Host.Core.Security;

namespace RemoteAssist.Host.Core.Windows;

public enum HostChildRole {Tray,Helper,AdminHelper,RunbookWorker,AdminRunbookWorker}

/// <summary>SYSTEM supervisor only. Fixed protected executables, no arbitrary command or inherited handles.</summary>
public sealed class SessionProcess : IDisposable
{
    readonly KernelHandle process,job;
    readonly object processGate=new();
    bool disposed;
    public int Pid {get;}
    public uint SessionId {get;}
    public string OwnerSid {get;}
    public HostChildRole Role {get;}
    SessionProcess(int pid,uint session,string sid,HostChildRole role,KernelHandle process,KernelHandle job)
    {Pid=pid;SessionId=session;OwnerSid=sid;Role=role;this.process=process;this.job=job;}
    public bool HasExited {get{lock(processGate)return disposed||WaitForSingleObject(process,0)==0;}}
    public void Dispose(){lock(processGate){if(disposed)return;disposed=true;job.Dispose();process.Dispose();}}
    // A retained handle observes this process object even if its PID is subsequently reused.
    public ExitProbe CaptureExitProbe()
    {
        lock(processGate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            Check(DuplicateHandle(GetCurrentProcess(),process,GetCurrentProcess(),out var retained,0,false,2));
            return new(Pid,Role,retained);
        }
    }
    public sealed class ExitProbe:IDisposable
    {
        readonly KernelHandle retained;
        public int Pid {get;}
        public HostChildRole Role {get;}
        internal ExitProbe(int pid,HostChildRole role,KernelHandle retained){Pid=pid;Role=role;this.retained=retained;}
        public bool WaitUntil(long deadline)
        {
            var remaining=(int)Math.Clamp(Math.Ceiling((deadline-Stopwatch.GetTimestamp())*1000d/Stopwatch.Frequency),0,1000);
            var result=WaitForSingleObject(retained,(uint)remaining);
            if(result==uint.MaxValue)throw new Win32Exception(Marshal.GetLastWin32Error());
            return result==0;
        }
        public void Dispose()=>retained.Dispose();
    }
    public static uint ActiveConsoleSession=>WTSGetActiveConsoleSessionId();
    public static bool IsInteractiveConsole(uint session)
    {
        if(session==uint.MaxValue||session!=ActiveConsoleSession)return false;
        if(!WTSQuerySessionInformation(0,session,8,out var state,out var stateBytes))return false;
        try{if(stateBytes<4||Marshal.ReadInt32(state)!=0)return false;}finally{WTSFreeMemory(state);}
        if(!WTSQuerySessionInformation(0,session,16,out var protocol,out var protocolBytes))return false;
        try{return protocolBytes>=2&&Marshal.ReadInt16(protocol)==0;}finally{WTSFreeMemory(protocol);}
    }
    public static string ConsoleUserSid(uint session)
    {
        DemandSupervisor(session);Enable("SeTcbPrivilege");Check(WTSQueryUserToken(session,out var token));
        using(token)using(var identity=new WindowsIdentity(token.DangerousGetHandle()))return identity.User?.Value??throw new UnauthorizedAccessException("대상 사용자 SID 누락");
    }
    /// <summary>Verify the tokens/privileges needed by all helper roles before offering support.</summary>
    public static void VerifyStartup(uint session)
    {
        DemandSupervisor(session);
        var privileges=new[]{"SeTcbPrivilege","SeIncreaseQuotaPrivilege","SeAssignPrimaryTokenPrivilege"};
        try
        {
            foreach(var privilege in privileges)Enable(privilege);
            Check(WTSQueryUserToken(session,out var user));
            using(user)using(var identity=new WindowsIdentity(user.DangerousGetHandle()))
                if(identity.User is null)throw new UnauthorizedAccessException("대상 사용자 토큰이 없다.");
            Check(OpenProcessToken(GetCurrentProcess(),0xB,out var system));
            using(system)
            {
                Check(DuplicateTokenEx(system,0xF01FF,0,2,1,out var duplicate));
                using(duplicate)Check(SetTokenInformation(duplicate,12,ref session,4));
            }
        }
        finally{foreach(var privilege in privileges)Enable(privilege,false);}
    }
    public static string CurrentConsoleUserSid(uint session)
    {
        using var current=WindowsIdentity.GetCurrent();
        if(!current.IsSystem||Process.GetCurrentProcess().SessionId!=session||!IsInteractiveConsole(session))throw new UnauthorizedAccessException("현재 복원 SYSTEM 세션이 필요하다.");
        Enable("SeTcbPrivilege");
        try
        {
            Check(WTSQueryUserToken(session,out var token));
            using(token)using(var identity=new WindowsIdentity(token.DangerousGetHandle()))return identity.User?.Value??throw new UnauthorizedAccessException("콘솔 사용자 SID 누락");
        }
        finally{Enable("SeTcbPrivilege",false);}
    }
    public static SessionProcess Start(HostChildRole role,uint session,string pipe,string nonce)
    {
        DemandSupervisor(session);
        if(!Enum.IsDefined(role)||!Guid.TryParseExact(pipe,"N",out _)||nonce.Length!=64||!nonce.All(Uri.IsHexDigit))throw new ArgumentException("헬퍼 bootstrap 설정 오류다.");
        var system=role is HostChildRole.AdminHelper or HostChildRole.AdminRunbookWorker;
        var exe=ProtectedInstallation.ResolveExecutable(role switch {HostChildRole.Tray=>"RemoteAssist.Host.Tray.exe",HostChildRole.Helper=>"RemoteAssist.Host.Helper.exe",HostChildRole.RunbookWorker or HostChildRole.AdminRunbookWorker=>"RemoteAssist.Host.RunbookWorker.exe",_=>"RemoteAssist.Host.AdminHelper.exe"});
        Enable("SeTcbPrivilege");Enable("SeIncreaseQuotaPrivilege");Enable("SeAssignPrimaryTokenPrivilege");
        KernelHandle original;
        if(system)Check(OpenProcessToken(GetCurrentProcess(),0xB,out original));else Check(WTSQueryUserToken(session,out original));
        using(original)
        {
            using var owner=new WindowsIdentity(original.DangerousGetHandle());var sid=owner.User?.Value??throw new UnauthorizedAccessException("토큰 SID 누락");
            Check(DuplicateTokenEx(original,0xF01FF,0,2,1,out var token));
            using(token)
            {
                if(system)Check(SetTokenInformation(token,12,ref session,4));
                if(system){var noPrivileges=new TokenPrivileges();Check(AdjustTokenPrivileges(token,true,ref noPrivileges,0,0,0));}
                Check(CreateEnvironmentBlock(out var environment,token,false));
                try
                {
                    using var candidateJob=CreateJobObject(0,null);if(candidateJob.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
                    var limits=new JobLimits {Basic=new BasicJobLimits {Flags=0x2000}};
                    if(role is HostChildRole.RunbookWorker or HostChildRole.AdminRunbookWorker){limits.Basic.Flags|=0x308;limits.Basic.ActiveProcesses=64;limits.ProcessMemory=1024*1024*1024;limits.JobMemory=(nuint)(2048L*1024*1024);/* 원격 명령이 자식 프로세스를 만들 수 있다. */}
                    var size=Marshal.SizeOf<JobLimits>();var ptr=Marshal.AllocHGlobal(size);
                    try{Marshal.StructureToPtr(limits,ptr,false);Check(SetInformationJobObject(candidateJob,9,ptr,(uint)size));}finally{Marshal.FreeHGlobal(ptr);}
                    var startup=new StartupInfo {Size=Marshal.SizeOf<StartupInfo>(),Desktop="winsta0\\default"};
                    DemandSupervisor(session);
                    Check(CreateProcessAsUser(token,exe,new StringBuilder($"\"{exe}\" --connect {pipe} --nonce {nonce} --service-pid {Environment.ProcessId}"),0,0,false,0x08000404,environment,AppContext.BaseDirectory,ref startup,out var info));
                    using var thread=new KernelHandle(info.Thread);var child=new KernelHandle(info.Process);
                    try
                    {
                        Check(AssignProcessToJobObject(candidateJob,child));DemandSupervisor(session);
                        if(ResumeThread(thread)==uint.MaxValue)throw new Win32Exception(Marshal.GetLastWin32Error());
                        var ownedJob=new KernelHandle(candidateJob.DangerousGetHandle());candidateJob.SetHandleAsInvalid();
                        return new((int)info.Pid,session,sid,role,child,ownedJob);
                    }
                    catch{TerminateProcess(child,1);child.Dispose();throw;}
                }
                finally{DestroyEnvironmentBlock(environment);}
            }
        }
    }
    static void DemandSupervisor(uint session)
    {
        using var identity=WindowsIdentity.GetCurrent();
        if(!identity.IsSystem||Process.GetCurrentProcess().SessionId!=0||!IsInteractiveConsole(session))throw new UnauthorizedAccessException("현재 활성 콘솔을 감독하는 Session 0 SYSTEM 서비스가 필요하다.");
    }
    static void Check(bool result){if(!result)throw new Win32Exception(Marshal.GetLastWin32Error());}
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool DuplicateHandle(nint sourceProcess,KernelHandle source,nint targetProcess,out KernelHandle target,uint access,bool inherit,uint options);
    static void Enable(string name,bool enabled=true)
    {
        Check(OpenProcessToken(GetCurrentProcess(),0x28,out var token));using(token)
        {Check(LookupPrivilegeValue(null,name,out var luid));var privilege=new TokenPrivileges {Count=1,Luid=luid,Attributes=enabled?2u:0u};Check(AdjustTokenPrivileges(token,false,ref privilege,0,0,0));if(Marshal.GetLastWin32Error()!=0)throw new Win32Exception(Marshal.GetLastWin32Error());}
    }
    internal sealed class KernelHandle : SafeHandleZeroOrMinusOneIsInvalid
    {public KernelHandle():base(true){}public KernelHandle(nint value):base(true){SetHandle(value);}protected override bool ReleaseHandle()=>CloseHandle(handle);}
    [StructLayout(LayoutKind.Sequential)] struct Luid{public uint Low;public int High;}
    [StructLayout(LayoutKind.Sequential)] struct TokenPrivileges{public uint Count;public Luid Luid;public uint Attributes;}
    [StructLayout(LayoutKind.Sequential)] struct BasicJobLimits{public long ProcessTime,JobTime;public uint Flags;public nuint MinWorkingSet,MaxWorkingSet;public uint ActiveProcesses;public nuint Affinity;public uint Priority,Scheduling;}
    [StructLayout(LayoutKind.Sequential)] struct JobLimits{public BasicJobLimits Basic;public ulong ReadOps,WriteOps,OtherOps,ReadBytes,WriteBytes,OtherBytes;public nuint ProcessMemory,JobMemory,PeakProcessMemory,PeakJobMemory;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct StartupInfo{public int Size;public string? Reserved,Desktop,Title;public uint X,Y,Width,Height,XChars,YChars,Fill,Flags;public ushort Show,ReservedBytes;public nint ReservedPtr,StdIn,StdOut,StdErr;}
    [StructLayout(LayoutKind.Sequential)] struct ProcessInfo{public nint Process,Thread;public uint Pid,Tid;}
    [DllImport("kernel32.dll")] static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("kernel32.dll")] static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(KernelHandle handle,uint milliseconds);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool OpenProcessToken(nint process,uint access,out KernelHandle token);
    [DllImport("advapi32.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern bool LookupPrivilegeValue(string? system,string name,out Luid luid);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool AdjustTokenPrivileges(KernelHandle token,bool disable,ref TokenPrivileges privileges,uint length,nint previous,nint needed);
    [DllImport("wtsapi32.dll",SetLastError=true)] static extern bool WTSQueryUserToken(uint session,out KernelHandle token);
    [DllImport("wtsapi32.dll",EntryPoint="WTSQuerySessionInformationW",SetLastError=true)]static extern bool WTSQuerySessionInformation(nint server,uint session,int info,out nint buffer,out uint bytes);
    [DllImport("wtsapi32.dll")]static extern void WTSFreeMemory(nint buffer);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool DuplicateTokenEx(KernelHandle token,uint access,nint attributes,int level,int type,out KernelHandle duplicate);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool SetTokenInformation(KernelHandle token,int type,ref uint session,uint bytes);
    [DllImport("userenv.dll",SetLastError=true)] static extern bool CreateEnvironmentBlock(out nint environment,KernelHandle token,bool inherit);
    [DllImport("userenv.dll")] static extern bool DestroyEnvironmentBlock(nint environment);
    [DllImport("advapi32.dll",SetLastError=true,CharSet=CharSet.Unicode,EntryPoint="CreateProcessAsUserW")] static extern bool CreateProcessAsUser(KernelHandle token,string application,StringBuilder command,nint processAttr,nint threadAttr,bool inherit,uint flags,nint environment,string directory,ref StartupInfo startup,out ProcessInfo info);
    [DllImport("kernel32.dll",SetLastError=true)] static extern KernelHandle CreateJobObject(nint attributes,string? name);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool SetInformationJobObject(KernelHandle job,int type,nint info,uint bytes);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool AssignProcessToJobObject(KernelHandle job,KernelHandle process);
    [DllImport("kernel32.dll",SetLastError=true)] static extern uint ResumeThread(KernelHandle thread);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool TerminateProcess(KernelHandle process,uint code);
}
