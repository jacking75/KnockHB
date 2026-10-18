// Windows preparation harness primitive. No product consent or service bypass.
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

public sealed class E2eOwnedCommand : IDisposable
{
    private Handle job, process;
    private bool disposed;
    public int Pid { get; private set; }
    private E2eOwnedCommand(Handle j, Handle p, int pid) { job=j;process=p;Pid=pid; }
    public static long Tick { get { return Stopwatch.GetTimestamp(); } }
    public static long Deadline(int milliseconds) {
        if(milliseconds<1||milliseconds>900000)throw new ArgumentOutOfRangeException("milliseconds");
        return checked(Tick+(long)(milliseconds*(double)Stopwatch.Frequency/1000));
    }
    public static int Remaining(long deadline) {
        return (int)Math.Max(0,Math.Min(900000,Math.Ceiling((deadline-Tick)*1000.0/Stopwatch.Frequency)));
    }
    private static string Quote(string value) {
        if(value==null||value.IndexOf('\0')>=0)throw new ArgumentException("Invalid argument");
        var text=new StringBuilder("\"");int slash=0;
        foreach(char c in value){if(c=='\\'){slash++;continue;}text.Append('\\',c=='"'?slash*2+1:slash);text.Append(c);slash=0;}
        text.Append('\\',slash*2);return text.Append('"').ToString();
    }
    public static E2eOwnedCommand Start(string file,string[] args) {
        if(!System.IO.Path.IsPathRooted(file)||!System.IO.File.Exists(file))throw new ArgumentException("Existing absolute executable required");
        var command=new StringBuilder(Quote(file));foreach(var arg in args)command.Append(' ').Append(Quote(arg));
        Handle j=CreateJobObject(IntPtr.Zero,null);if(j.IsInvalid)throw new Win32Exception();
        Handle p=null;IntPtr memory=IntPtr.Zero;ProcessInfo info=new ProcessInfo();
        try {
            var limits=new JobLimits();limits.Basic.Flags=0x2000; // KILL_ON_JOB_CLOSE, no breakaway
            memory=Marshal.AllocHGlobal(Marshal.SizeOf(typeof(JobLimits)));Marshal.StructureToPtr(limits,memory,false);
            Check(SetInformationJobObject(j,9,memory,(uint)Marshal.SizeOf(typeof(JobLimits))));
            var startup=new StartupInfo();startup.Size=Marshal.SizeOf(typeof(StartupInfo));
            // Suspended: the child cannot create descendants before Job assignment.
            Check(CreateProcess(file,command,IntPtr.Zero,IntPtr.Zero,false,0x08000004,IntPtr.Zero,System.IO.Path.GetDirectoryName(file),ref startup,out info));
            p=new Handle(info.Process);
            Check(AssignProcessToJobObject(j,p));
            if(ResumeThread(info.Thread)==uint.MaxValue)throw new Win32Exception();
            return new E2eOwnedCommand(j,p,(int)info.Pid);
        } catch {if(p!=null){TerminateProcess(p,1);p.Dispose();}j.Dispose();throw;}
        finally {if(info.Thread!=IntPtr.Zero)CloseHandle(info.Thread);if(memory!=IntPtr.Zero)Marshal.FreeHGlobal(memory);}
    }
    public bool HasExited {get {if(disposed)return true;var result=WaitForSingleObject(process,0);if(result==0xffffffff)throw new Win32Exception();return result==0;}}
    public int Wait(long globalDeadline,int callMilliseconds) {
        if(callMilliseconds<1||callMilliseconds>10000)throw new ArgumentOutOfRangeException("callMilliseconds");
        int remaining=Math.Min(callMilliseconds,Remaining(globalDeadline));
        if(remaining==0){Dispose();throw new TimeoutException("Global deadline expired");}
        uint result=WaitForSingleObject(process,(uint)remaining);
        if(result==0x102){Dispose();throw new TimeoutException("Owned command timed out");}
        if(result!=0)throw new Win32Exception();
        uint code;Check(GetExitCodeProcess(process,out code));return unchecked((int)code);
    }
    public void Dispose(){if(disposed)return;disposed=true;job.Dispose();process.Dispose();}
    static void Check(bool ok){if(!ok)throw new Win32Exception(Marshal.GetLastWin32Error());}
    sealed class Handle:SafeHandleZeroOrMinusOneIsInvalid {
        public Handle():base(true){} public Handle(IntPtr value):base(true){SetHandle(value);}
        protected override bool ReleaseHandle(){return CloseHandle(handle);}
    }
    [StructLayout(LayoutKind.Sequential)] struct BasicLimits {public long ProcessTime,JobTime;public uint Flags;public UIntPtr MinWorkingSet,MaxWorkingSet;public uint ActiveProcesses;public UIntPtr Affinity;public uint Priority,Scheduling;}
    [StructLayout(LayoutKind.Sequential)] struct JobLimits {public BasicLimits Basic;public ulong ReadOps,WriteOps,OtherOps,ReadBytes,WriteBytes,OtherBytes;public UIntPtr ProcessMemory,JobMemory,PeakProcessMemory,PeakJobMemory;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct StartupInfo {public int Size;public string Reserved,Desktop,Title;public uint X,Y,Width,Height,XChars,YChars,Fill,Flags;public ushort Show,ReservedBytes;public IntPtr ReservedPtr,StdIn,StdOut,StdErr;}
    [StructLayout(LayoutKind.Sequential)] struct ProcessInfo {public IntPtr Process,Thread;public uint Pid,Tid;}
    [DllImport("kernel32.dll",SetLastError=true,CharSet=CharSet.Unicode,EntryPoint="CreateProcessW")] static extern bool CreateProcess(string app,StringBuilder command,IntPtr processAttributes,IntPtr threadAttributes,bool inherit,uint flags,IntPtr environment,string directory,ref StartupInfo startup,out ProcessInfo info);
    [DllImport("kernel32.dll",SetLastError=true)] static extern Handle CreateJobObject(IntPtr attributes,string name);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool SetInformationJobObject(Handle job,int type,IntPtr info,uint bytes);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool AssignProcessToJobObject(Handle job,Handle process);
    [DllImport("kernel32.dll",SetLastError=true)] static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll",SetLastError=true)] static extern uint WaitForSingleObject(Handle handle,uint milliseconds);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetExitCodeProcess(Handle process,out uint code);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool TerminateProcess(Handle process,uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
}
