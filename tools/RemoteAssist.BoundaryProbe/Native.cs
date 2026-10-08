using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RemoteAssist.BoundaryProbe;

internal static class Native
{
    internal sealed class KernelHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public KernelHandle() : base(true) { }
        internal void Initialize(nint value) => SetHandle(value);
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
    internal sealed class Child : IDisposable
    {
        public int Pid { get; }
        readonly KernelHandle process, job;
        internal Child(int pid, KernelHandle process, KernelHandle job) { Pid=pid;this.process=process;this.job=job; }
        public void Dispose() { job.Dispose(); process.Dispose(); }
    }
    internal static void Check(bool result) { if(!result)throw new Win32Exception(Marshal.GetLastWin32Error()); }
    internal static void Enable(string name)
    {
        Check(OpenProcessToken(GetCurrentProcess(),0x28,out var token));
        using(token)
        {
            Check(LookupPrivilegeValue(null,name,out var luid));
            var privilege=new TokenPrivileges {Count=1,Luid=luid,Attributes=2};
            Check(AdjustTokenPrivileges(token,false,ref privilege,0,0,0));
            if(Marshal.GetLastWin32Error()!=0)throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }
    internal static Child Launch(string mode,string pipe,uint session,bool system)
    {
        if(!Path.GetFullPath(AppContext.BaseDirectory).StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("SYSTEM 실행 파일은 보호된 Program Files 배치에서만 실행한다.");
        Enable("SeTcbPrivilege"); Enable("SeIncreaseQuotaPrivilege"); Enable("SeAssignPrimaryTokenPrivilege");
        KernelHandle source;
        if(system)Check(OpenProcessToken(GetCurrentProcess(),0xB,out source));
        else Check(WTSQueryUserToken(session,out source));
        using(source)
        {
            Check(DuplicateTokenEx(source,0xF01FF,0,2,1,out var primary));
            using(primary)
            {
                if(system)
                {
                    Check(SetTokenInformation(primary,12,ref session,4));
                }
                Check(CreateEnvironmentBlock(out var environment,primary,false));
                try
                {
                    using var candidateJob=CreateJobObject(0,null);
                    if(candidateJob.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
                    var limit=new JobLimits {Basic=new BasicJobLimits {LimitFlags=0x2000}};
                    var bytes=Marshal.SizeOf<JobLimits>(); var ptr=Marshal.AllocHGlobal(bytes);
                    try { Marshal.StructureToPtr(limit,ptr,false);Check(SetInformationJobObject(candidateJob,9,ptr,(uint)bytes)); }
                    finally {Marshal.FreeHGlobal(ptr);}
                    var exe=Path.Combine(AppContext.BaseDirectory,"RemoteAssist.BoundaryProbe.exe");
                    var startup=new StartupInfo {Size=Marshal.SizeOf<StartupInfo>(),Desktop="winsta0\\default"};
                    Check(CreateProcessAsUser(primary,exe,new StringBuilder($"\"{exe}\" {mode} {pipe}"),0,0,false,0x404,environment,AppContext.BaseDirectory,ref startup,out var info));
                    using var thread=new KernelHandle(); thread.SetHandleForProbe(info.Thread);
                    var process=new KernelHandle();process.SetHandleForProbe(info.Process);
                    try
                    {
                        Check(AssignProcessToJobObject(candidateJob,process));
                        if(ResumeThread(thread)==uint.MaxValue)throw new Win32Exception(Marshal.GetLastWin32Error());
                        var ownedJob=new KernelHandle();ownedJob.SetHandleForProbe(candidateJob.DangerousGetHandle());candidateJob.SetHandleAsInvalid();
                        return new Child((int)info.Pid,process,ownedJob);
                    }
                    catch {TerminateProcess(process,1);process.Dispose();throw;}
                }
                finally {DestroyEnvironmentBlock(environment);}
            }
        }
    }
    // Only used for handles returned by successful native calls in this tool.
    internal static void SetHandleForProbe(this KernelHandle handle,nint value) => HandleSetter(handle,value);
    static void HandleSetter(KernelHandle handle,nint value) => handle.Initialize(value);
    [StructLayout(LayoutKind.Sequential)] internal struct Luid {public uint Low; public int High;}
    [StructLayout(LayoutKind.Sequential)] internal struct TokenPrivileges {public uint Count;public Luid Luid;public uint Attributes;}
    [StructLayout(LayoutKind.Sequential)] internal struct BasicJobLimits {public long ProcessTime,JobTime;public uint LimitFlags;public nuint MinWorkingSet,MaxWorkingSet;public uint ActiveProcesses;public nuint Affinity;public uint Priority,Scheduling;}
    [StructLayout(LayoutKind.Sequential)] internal struct JobLimits {public BasicJobLimits Basic;public ulong ReadOps,WriteOps,OtherOps,ReadBytes,WriteBytes,OtherBytes;public nuint ProcessMemory,JobMemory,PeakProcessMemory,PeakJobMemory;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] internal struct StartupInfo {public int Size;public string? Reserved,Desktop,Title;public uint X,Y,Width,Height,XChars,YChars,Fill,Flags;public ushort Show,ReservedBytes;public nint ReservedPtr,StdIn,StdOut,StdErr;}
    [StructLayout(LayoutKind.Sequential)] internal struct ProcessInfo {public nint Process,Thread;public uint Pid,Tid;}
    [DllImport("kernel32.dll")] internal static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("kernel32.dll")] static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint handle);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool OpenProcessToken(nint process,uint access,out KernelHandle token);
    [DllImport("advapi32.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern bool LookupPrivilegeValue(string? system,string name,out Luid luid);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool AdjustTokenPrivileges(KernelHandle token,bool disable,ref TokenPrivileges privileges,uint length,nint previous,nint needed);
    [DllImport("wtsapi32.dll",SetLastError=true)] static extern bool WTSQueryUserToken(uint session,out KernelHandle token);
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
    [DllImport("kernel32.dll",SetLastError=true)] internal static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe,out uint pid);
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] internal static extern nint GetThreadDesktop(uint thread);
    [DllImport("user32.dll",SetLastError=true)] internal static extern nint OpenInputDesktop(uint flags,bool inherit,uint access);
    [DllImport("user32.dll",SetLastError=true)] internal static extern bool SetThreadDesktop(nint desktop);
    [DllImport("user32.dll")] internal static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll",EntryPoint="GetUserObjectInformationW",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool GetUserObjectInformation(nint handle,int index,StringBuilder? text,uint bytes,out uint needed);
    internal static string? Name(nint handle)
    {
        GetUserObjectInformation(handle,2,null,0,out var bytes);if(bytes==0)return null;
        var text=new StringBuilder((int)bytes/2);return GetUserObjectInformation(handle,2,text,bytes,out _)?text.ToString():null;
    }
    [DllImport("user32.dll",EntryPoint="GetUserObjectInformationW",SetLastError=true)] static extern bool DesktopInputInfo(nint handle,int index,out int value,uint size,out uint needed);
    internal static bool ReceivingInput(nint desktop)=>DesktopInputInfo(desktop,6,out var flag,4,out _)&&flag!=0;
}
