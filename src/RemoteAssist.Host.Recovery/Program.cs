using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using RemoteAssist.Host.Core;
using RemoteAssist.Host.Core.Input;
using RemoteAssist.Host.Core.Ipc;
using RemoteAssist.Host.Core.Windows;
using RemoteAssist.Host.Core.Diagnostics;
using RemoteAssist.Host.Core.Security;

RecoveryRuntime? runtime=null;
try
{
    var bootstrap=IpcBootstrap.Parse(args);
    using var identity=WindowsIdentity.GetCurrent();
    if(!identity.IsSystem||Process.GetCurrentProcess().SessionId==0)throw new UnauthorizedAccessException("복원 SYSTEM 사용자 세션이 필요하다.");
    using var stop=new CancellationTokenSource();
    await using var channel=await LocalIpcEndpoint.ConnectAsync(bootstrap.Pipe,bootstrap.Nonce,bootstrap.ServicePid,"recovery",stop.Token);
    runtime=new(bootstrap.ServicePid);
    await using var peer=new IpcPeerSession(channel,runtime.Handle,stop.Token,_=>stop.Cancel(),_=>stop.Cancel());
    runtime.Peer=peer;peer.Start();
    using var timer=new PeriodicTimer(TimeSpan.FromMilliseconds(100));
    try{while(await timer.WaitForNextTickAsync(stop.Token))if(runtime.MustStop)break;}
    finally{runtime.Release();}
    return 0;
}
catch(OperationCanceledException){return 0;}
catch(Exception ex){LocalDiagnostic.Failure("recovery",ex);return 1;}
finally{runtime?.Release();}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
sealed record RecoveryBinding(string SessionId,string ConnectionId,long PermissionGeneration,string UserSid,int HelperPid);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
sealed record RecoveryAdmin(int Pid);
sealed class RecoveryRuntime
{
    readonly Process service;
    string? connectionId;
    public RecoveryRuntime(int servicePid)
    {
        service=Process.GetProcessById(servicePid);_=service.Handle;
        if(service.SessionId!=0||!string.Equals(service.MainModule?.FileName,ProtectedInstallation.ResolveExecutable("RemoteAssist.Host.Service.exe"),StringComparison.OrdinalIgnoreCase))
        {service.Dispose();throw new UnauthorizedAccessException("복원 서비스 신원 오류다.");}
    }
    readonly object gate=new();
    readonly Stopwatch started=Stopwatch.StartNew();
    RecoveryLedger? ledger;
    Process? helper;
    readonly List<Process> adminHelpers=[];
    long lastLease;
    bool released;
    string? bindingId;
    long bindingGeneration;
    public IpcPeerSession Peer {get;set;}=null!;
    public bool MustStop
    {get{lock(gate)return released||(ledger is null?started.Elapsed>TimeSpan.FromSeconds(10):helper!.HasExited||(ledger.Count>0&&Stopwatch.GetElapsedTime(lastLease)>TimeSpan.FromSeconds(1)));}}
    public Task Handle(LocalIpcMessage message,CancellationToken ct)
    {
        lock(gate)
        {
            switch(message.Type)
            {
                case "recovery-bind":
                    if(ledger!=null)throw new InvalidDataException("복원 바인딩을 변경할 수 없다.");
                    var binding=message.Body?.Deserialize<RecoveryBinding>()??throw new InvalidDataException("복원 바인딩 누락");
                    if(string.IsNullOrEmpty(binding.ConnectionId)||binding.ConnectionId.Length>64||!binding.ConnectionId.All(c=>char.IsAsciiLetterOrDigit(c)||c is '-' or '_'))throw new InvalidDataException("복원 연결 오류다.");
                    var session=(uint)Process.GetCurrentProcess().SessionId;
                    if(binding.SessionId.Length is <1 or >64||binding.PermissionGeneration<1||binding.HelperPid<=0||SessionProcess.RecoveryConsoleUserSid(session)!=binding.UserSid)throw new UnauthorizedAccessException("복원 대상 바인딩 오류다.");
                    helper=Process.GetProcessById(binding.HelperPid);
                    // Access the handle now: later PID reuse cannot retarget the watchdog.
                    _=helper.Handle;
                    if(helper.SessionId!=session||!string.Equals(helper.MainModule?.FileName,ProtectedInstallation.ResolveExecutable("RemoteAssist.Host.Helper.exe"),StringComparison.OrdinalIgnoreCase))throw new UnauthorizedAccessException("복원 대상 Helper 불일치다.");
                    ledger=new(binding.SessionId,binding.PermissionGeneration,new WindowsRecoveryBackend(session,binding.UserSid));lastLease=Stopwatch.GetTimestamp();
                    bindingId=binding.SessionId;bindingGeneration=binding.PermissionGeneration;connectionId=binding.ConnectionId;
                    CaseTrace.Write("recovery-bound",bindingId,bindingGeneration,new {helperPid=binding.HelperPid,session,userSid=binding.UserSid});
                    Peer.Post("recovery-ack",id:message.Id);break;
                case "recovery-reserve":
                    if(ledger==null||MustStop)throw new InvalidDataException("복원 권한 또는 lease가 없다.");
                    ledger.Reserve(message.Body?.Deserialize<RecoveryReservation>()??throw new InvalidDataException("복원 예약 누락"));
                    lastLease=Stopwatch.GetTimestamp();Peer.Post("recovery-ack",id:message.Id);break;
                case "recovery-lease":
                    if(ledger==null||MustStop)throw new InvalidDataException("복원 lease가 만료됐다.");
                    lastLease=Stopwatch.GetTimestamp();break;
                case "recovery-admin":
                    if(ledger==null||MustStop||adminHelpers.Count>=8)throw new InvalidDataException("관리자 복원 바인딩 상태 오류다.");
                    var admin=message.Body?.Deserialize<RecoveryAdmin>()??throw new InvalidDataException("관리자 PID 누락이다.");
                    var process=Process.GetProcessById(admin.Pid);
                    try
                    {
                        _=process.Handle;
                        if(process.SessionId!=System.Diagnostics.Process.GetCurrentProcess().SessionId||!string.Equals(process.MainModule?.FileName,ProtectedInstallation.ResolveExecutable("RemoteAssist.Host.AdminHelper.exe"),StringComparison.OrdinalIgnoreCase))throw new UnauthorizedAccessException("관리자 복원 대상 불일치다.");
                        adminHelpers.Add(process);process=null!;Peer.Post("recovery-ack",id:message.Id);
                    }
                    finally{process?.Dispose();}
                    break;
                default:throw new InvalidDataException("복원 프로세스에 허용되지 않은 요청이다.");
            }
        }
        return Task.CompletedTask;
    }
    public void Release()
    {
        lock(gate)
        {
            if(released)return;released=true;
            // Fence all future downs before any up, including a helper stalled between ACK and SendInput.
            var children=adminHelpers.Concat(helper is null?[]:new[]{helper}).ToArray();
            foreach(var child in children)
                try{if(!child.HasExited)child.Kill();}catch(Exception ex){LocalDiagnostic.Failure("recovery-helper-stop",ex);}
            var fence=Stopwatch.StartNew();
            foreach(var child in children)
                try{if(!child.HasExited)child.WaitForExit((int)Math.Max(0,500-fence.ElapsedMilliseconds));}catch(Exception ex){LocalDiagnostic.Failure("recovery-helper-wait",ex);}
            if(children.Any(child=>!child.HasExited)){LocalDiagnostic.Failure("recovery-helper-alive",new IOException());return;}
            CaseTrace.Write("producers-fenced",bindingId,bindingGeneration,new {pids=children.Select(c=>c.Id).ToArray()});
            for(var attempt=0;ledger is {Count:>0}&&attempt<3;attempt++)
                try{ledger.Release();}catch(Exception ex){LocalDiagnostic.Failure("recovery-up",ex);}
            foreach(var child in children)child.Dispose();
            // Actual service handle death, not pipe disconnect, is the crash fact.
            // Audit I/O is strictly after fence/up and has its own process bound.
            try
            {
                using var limit=new Timer(_=>Environment.Exit(124),null,4000,Timeout.Infinite);
                if(CrashSessionAudit.Record(bindingId,connectionId,()=>service.HasExited||service.WaitForExit(500),AuditJournal.OpenProtected))
                {
                    CaseTrace.Write("recovery-session-ended",bindingId,bindingGeneration,new {servicePid=service.Id});
                }
            }
            catch(Exception ex){LocalDiagnostic.Failure("recovery-audit",ex);}
            finally{service.Dispose();}
        }
    }
}
