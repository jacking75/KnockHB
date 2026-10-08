using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Channels;
using RemoteAssist.Host.Core.Capture;
using RemoteAssist.Host.Core.Input;
using RemoteAssist.Host.Core.Ipc;
using RemoteAssist.Host.Core.Media;
using RemoteAssist.Host.Core.Windows;

namespace RemoteAssist.Host.Core.Security;

/// <summary>Privileged capture/input only. No network, arbitrary command, clipboard or file access API.</summary>
public sealed class AdminDesktopRuntime:IDisposable
{
    readonly CancellationTokenSource stop=new();
    readonly Channel<InputWork> input=Channel.CreateBounded<InputWork>(64);
    readonly TaskCompletionSource completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Stopwatch bootstrap=Stopwatch.StartNew();
    readonly object gate=new();
    AdminConfiguration? config;
    SharedFrameMap? mapping;
    Task? worker;
    long lease=Stopwatch.GetTimestamp();
    int stopped;
    int desktopRunning;
    public IpcPeerSession Peer {get;set;}=null!;
    public Task Completion=>completion.Task;
    public bool Expired=>config is null?bootstrap.Elapsed.TotalSeconds>=10:DateTimeOffset.UtcNow>=config.Approval.ExpiresAt||Stopwatch.GetElapsedTime(Volatile.Read(ref lease)).TotalSeconds>=1;
    public async Task Handle(LocalIpcMessage message,CancellationToken ct)
    {
        switch(message.Type)
        {
            case "admin-config":
                lock(gate)
                {
                    if(config!=null||stopped!=0)throw new InvalidDataException("중복 관리자 설정이다.");
                    var value=message.Body?.Deserialize<AdminConfiguration>()??throw new InvalidDataException("관리자 설정 누락이다.");
                    using var identity=WindowsIdentity.GetCurrent();
                    if(!identity.IsSystem||Process.GetCurrentProcess().SessionId!=value.WindowsSessionId||value.Approval.ExpiresAt<=DateTimeOffset.UtcNow||value.Approval.ExpiresAt>DateTimeOffset.UtcNow.AddMinutes(2)||value.Approval.AdminGeneration<1||value.Approval.PermissionGeneration<1||value.TargetFps is <1 or >60||value.MaxShortEdge is <2 or >2160||value.MaxLongEdge<value.MaxShortEdge||value.MaxLongEdge>3840||SessionProcess.RecoveryConsoleUserSid(value.WindowsSessionId)!=value.UserSid)
                        throw new UnauthorizedAccessException("현재 독립 관리자 승인이 아니다.");
                    _=ApprovedMonitor(value);mapping=SharedFrameMap.Open(value.Mapping,true);config=value;Volatile.Write(ref lease,Stopwatch.GetTimestamp());
                    worker=Run();
                }
                break;
            case "admin-lease":
                if(message.Body!=null||Expired||config==null||stopped!=0)throw new UnauthorizedAccessException("관리자 lease가 만료됐다.");
                Volatile.Write(ref lease,Stopwatch.GetTimestamp());break;
            case "admin-input":
                var batch=message.Body?.Deserialize<AdminInputBatch>()??throw new InvalidDataException("관리자 입력 누락이다.");AdminInputValidation.Validate(batch);
                if(config==null||Expired||batch.AdminGeneration!=config.Approval.AdminGeneration)throw new UnauthorizedAccessException("이전 관리자 입력이다.");
                var reply=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                if(!input.Writer.TryWrite(new(batch,reply)))throw new IOException("관리자 입력 큐 상한이다.");
                var count=await reply.Task.WaitAsync(TimeSpan.FromMilliseconds(400),ct);
                Peer.Post("admin-input-ack",new AdminInputResult(count),message.Id);break;
            default:throw new InvalidDataException("특권 Helper에 허용되지 않은 요청이다.");
        }
    }
    async Task Run()
    {
        try
        {
            var value=config!;var first=true;
            while(!stop.IsCancellationRequested&&!Expired)
            {
                var finished=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Interlocked.Increment(ref desktopRunning);
                var thread=new Thread(()=>
                {
                    try{CaptureDesktop(value,()=>{if(first){first=false;Peer.Post("admin-ready",id:value.Approval.RequestId);}});finished.TrySetResult();}
                    catch(Exception ex){finished.TrySetException(ex);}
                    finally{Interlocked.Decrement(ref desktopRunning);}
                }) {Name="RemoteAssist approved desktop",IsBackground=true};
                thread.Start();await finished.Task.WaitAsync(stop.Token);
            }
        }
        catch(OperationCanceledException)when(stop.IsCancellationRequested){}
        catch(Exception ex){LocalDiagnostic.Failure("admin-desktop",ex);}
        finally{Stop();completion.TrySetResult();}
    }
    void CaptureDesktop(AdminConfiguration value,Action ready)
    {
        using var desktop=new DesktopContext();
        var secure=desktop.Name=="Winlogon";var entered=Stopwatch.StartNew();
        using var indicator=secure?new SecureStopWindow(value.Monitor,Stop):null;
        var monitor=ApprovedMonitor(value);
        using var capture=new DxgiScreenCapture(monitor);
        using var scaler=new BgraFrameScaler(Math.Min(value.MaxLongEdge,1920),Math.Min(value.MaxShortEdge,1080));
        ready();var next=TimeSpan.Zero;var clock=Stopwatch.StartNew();
        ReadOnlyMemory<byte> previous=default;
        var remoteKeys=new HashSet<ushort>();var remoteButtons=new HashSet<string>();var remoteUnicode=new HashSet<char>();
        try
        {
            while(!stop.IsCancellationRequested&&!Expired&&desktop.IsReceivingInput)
            {
                DemandTarget(value);
                if(secure&&entered.Elapsed.TotalSeconds>=30){Stop();break;}
                indicator?.Pump();
                while(input.Reader.TryRead(out var work))
                {
                    try
                    {
                        if(work.Batch.AdminGeneration!=value.Approval.AdminGeneration||!desktop.IsReceivingInput||Expired||stop.IsCancellationRequested)throw new UnauthorizedAccessException("관리자 입력 실행 권한 상실이다.");
                        var approved=ApprovedMonitor(value);
                        foreach(var stroke in work.Batch.Strokes.Where(s=>s.Kind==StrokeKind.Move))
                            if(stroke.X<approved.X||stroke.X>=approved.X+(long)approved.Width||stroke.Y<approved.Y||stroke.Y>=approved.Y+(long)approved.Height)throw new InvalidDataException("승인 모니터 밖 관리자 입력이다.");
                        DemandTarget(value);
                        if(!desktop.IsReceivingInput||Expired||stop.IsCancellationRequested)throw new UnauthorizedAccessException("관리자 입력 최종 게이트가 닫혔다.");
                        var preflight=new WindowsInputBackend();
                        foreach(var s in work.Batch.Strokes.Where(s=>!s.Up&&s.Kind is StrokeKind.Key or StrokeKind.Button or StrokeKind.Unicode))
                        {
                            var owned=s.Kind switch {StrokeKind.Key=>remoteKeys.Contains(s.Key),StrokeKind.Button=>remoteButtons.Contains(s.Button),_=>remoteUnicode.Contains(s.Character)};
                            if(!owned&&!preflight.CanPress(s))throw new IOException("관리자 화면의 로컬 입력과 충돌했다.");
                        }
                        var sent=WindowsInputBackend.SendApprovedAdmin(work.Batch.Strokes);
                        foreach(var s in work.Batch.Strokes.Take(sent))
                        {
                            if(s.Kind==StrokeKind.Key){if(s.Up)remoteKeys.Remove(s.Key);else remoteKeys.Add(s.Key);}
                            if(s.Kind==StrokeKind.Button){if(s.Up)remoteButtons.Remove(s.Button);else remoteButtons.Add(s.Button);}
                            if(s.Kind==StrokeKind.Unicode){if(s.Up)remoteUnicode.Remove(s.Character);else remoteUnicode.Add(s.Character);}
                        }
                        work.Reply.TrySetResult(sent);
                    }
                    catch(Exception ex){work.Reply.TrySetException(ex);Stop();return;}
                }
                if(clock.Elapsed>=next)
                {
                    next=clock.Elapsed+TimeSpan.FromSeconds(1d/value.TargetFps);
                    if(capture.TryGetFrame(TimeSpan.FromMilliseconds(5),out var frame))
                    {
                        previous=scaler.Scale(frame.Bgra,frame.Width,frame.Height,frame.Stride);
                    }
                    else if(previous.IsEmpty&&clock.Elapsed>TimeSpan.FromSeconds(3))throw new TimeoutException("관리자 캡처 첫 프레임을 얻지 못했다.");
                    DemandTarget(value);
                    if(!desktop.IsReceivingInput||Expired||stop.IsCancellationRequested)return;
                    if(!previous.IsEmpty)mapping!.Publish(previous.Span,scaler.Width,scaler.Height,scaler.Width*4,value.Approval.PermissionGeneration,value.Monitor.TopologyGeneration,value.Approval.AdminGeneration,secure,Environment.TickCount64);
                }
                stop.Token.WaitHandle.WaitOne(5);
            }
        }
        finally{mapping!.Clear();}
    }
    static MonitorInfo ApprovedMonitor(AdminConfiguration value)
    {
        var m=value.Monitor;
        return Monitors.Enumerate().Single(x=>x.DeviceName==m.Identity&&x.X==m.X&&x.Y==m.Y&&x.Width==m.Width&&x.Height==m.Height);
    }
    static void DemandTarget(AdminConfiguration value)
    {if(!SessionProcess.IsInteractiveConsole(value.WindowsSessionId)||SessionProcess.RecoveryConsoleUserSid(value.WindowsSessionId)!=value.UserSid)throw new UnauthorizedAccessException("관리자 대상 사용자가 변경됐다.");}
    public void Stop()
    {
        if(Interlocked.Exchange(ref stopped,1)!=0)return;stop.Cancel();input.Writer.TryComplete();
        while(input.Reader.TryRead(out var item))item.Reply.TrySetCanceled();
        try{Peer?.Post("stop");}catch{}
        if(worker==null)completion.TrySetResult();
    }
    public void Dispose(){Stop();if((worker is null||worker.IsCompleted)&&Volatile.Read(ref desktopRunning)==0){mapping?.Dispose();stop.Dispose();}}
    sealed record InputWork(AdminInputBatch Batch,TaskCompletionSource<int> Reply);
}
