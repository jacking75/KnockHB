using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using Microsoft.Win32;
using RemoteAssist.Protocol;

namespace RemoteAssist.Host.Core.Diagnostics;

public interface IDiagnosticsProvider {DiagnosticSnapshot Read(CancellationToken ct);}

/// <summary>Ordinary-user read-only metadata. Never collects paths, commands, IP/MAC, event text or account/host names.</summary>
public sealed class DiagnosticsCollector:IDiagnosticsProvider
{
    ulong? previousIdle,previousTotal;
    public DiagnosticSnapshot Read(CancellationToken ct)
    {
        var result=new DiagnosticSnapshot(DateTimeOffset.UtcNow,Attempt(Cpu,ct),Attempt(Memory,ct),Attempt(Disks,ct),Attempt(Network,ct),Attempt(Errors,ct),Attempt(Startup,ct),Attempt(Services,ct));result.Validate();return result;
    }
    static T? Attempt<T>(Func<T> read,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();try{return read();}catch(OperationCanceledException){throw;}catch{return default;}
    }
    double? Cpu()
    {
        if(!GetSystemTimes(out var idle,out var kernel,out var user))return null;
        var total=checked(kernel+user);double? percent=null;
        if(previousIdle is {} lastIdle&&previousTotal is {} lastTotal&&idle>=lastIdle&&total>lastTotal)percent=Math.Clamp(100*(1d-(double)(idle-lastIdle)/(total-lastTotal)),0,100);
        previousIdle=idle;previousTotal=total;return percent;
    }
    static MemorySnapshot Memory()
    {
        var state=new MemoryStatus {Length=(uint)Marshal.SizeOf<MemoryStatus>()};if(!GlobalMemoryStatusEx(ref state))throw new IOException("메모리 진단 조회 실패다.");
        return new(checked((long)state.TotalPhysical),checked((long)state.AvailablePhysical));
    }
    static DiskSnapshot[] Disks()=>DriveInfo.GetDrives().Where(d=>d.DriveType==DriveType.Fixed&&d.IsReady).Take(32).Select(d=>new DiskSnapshot(d.Name[..2],d.TotalSize,d.AvailableFreeSpace)).ToArray();
    static NetworkSnapshot Network()
    {
        long received=0,sent=0;
        foreach(var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(a=>a.OperationalStatus==OperationalStatus.Up).Take(64))
        {var stats=adapter.GetIPStatistics();received=checked(received+stats.BytesReceived);sent=checked(sent+stats.BytesSent);}
        return new(received,sent);
    }
    static ErrorSnapshot[] Errors()
    {
        using var events=new EventLog("System");var result=new List<ErrorSnapshot>();var count=events.Entries.Count;
        for(var i=count-1;i>=Math.Max(0,count-200)&&result.Count<20;i--)
        {
            var e=events.Entries[i];var utc=new DateTimeOffset(e.TimeGenerated).ToUniversalTime();if(utc<DateTimeOffset.UtcNow.AddHours(-1))continue;
            if(e.EntryType is not (EventLogEntryType.Error or EventLogEntryType.Warning)||!Name(e.Source))continue;
            result.Add(new(e.Source,(int)(e.InstanceId&0x3fffffff),utc,e.EntryType==EventLogEntryType.Error?"error":"warning"));
        }
        return result.ToArray();
    }
    static string[] Startup()
    {
        var names=new HashSet<string>(StringComparer.Ordinal);
        foreach(var root in new[]{Registry.CurrentUser,Registry.LocalMachine})
        {
            using var key=root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run");
            foreach(var name in key?.GetValueNames()??[])if(Name(name)&&names.Count<32)names.Add(name);
        }
        return names.Order(StringComparer.Ordinal).ToArray();
    }
    static ServiceSnapshot[] Services()
    {
        var result=new List<ServiceSnapshot>();
        var all=ServiceController.GetServices();
        try
        {
            foreach(var service in all.OrderBy(s=>s.ServiceName,StringComparer.Ordinal))
                if(result.Count<64&&Name(service.ServiceName))try{result.Add(new(service.ServiceName,service.Status.ToString()));}catch{}
        }
        finally{foreach(var service in all)service.Dispose();}
        return result.ToArray();
    }
    static bool Name(string value)=>value.Length is >0 and <=64&&!value.Any(char.IsControl)&&!value.Contains('\\')&&!value.Contains('/');
    [StructLayout(LayoutKind.Sequential)]struct MemoryStatus {public uint Length,MemoryLoad;public ulong TotalPhysical,AvailablePhysical,TotalPageFile,AvailablePageFile,TotalVirtual,AvailableVirtual,AvailableExtendedVirtual;}
    [DllImport("kernel32.dll")]static extern bool GetSystemTimes(out ulong idle,out ulong kernel,out ulong user);
    [DllImport("kernel32.dll")]static extern bool GlobalMemoryStatusEx(ref MemoryStatus state);
}
