using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Channels;
using RemoteAssist.Host.Core.Capture;
using RemoteAssist.Host.Core.Input;
using RemoteAssist.Host.Core.Ipc;
using RemoteAssist.Host.Core.Media;
using RemoteAssist.Host.Core.Windows;
using RemoteAssist.Protocol;

namespace RemoteAssist.Host.Core.Security;

/// <summary>
/// 관리자 헬퍼(SYSTEM, 대상 사용자 세션)의 캡처·입력 런타임이다. 현재 입력 데스크톱(기본·UAC 보안 데스크톱·잠금 화면)을
/// 따라가며 캡처하고 입력을 주입한다. 네트워크·파일·명령 실행 API는 없다. 수명은 서비스의 지원 세션이 정한다.
/// </summary>
public sealed class AdminDesktopRuntime:IDisposable
{
    readonly CancellationTokenSource stop=new();
    readonly Channel<InputWork> input=Channel.CreateBounded<InputWork>(64);
    readonly TaskCompletionSource completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Stopwatch bootstrap=Stopwatch.StartNew();
    readonly object gate=new();
    AdminConfiguration? config;
    MonitorDescriptor? monitor;
    SharedFrameMap? mapping;
    Task? worker;
    int stopped;
    int desktopRunning;
    public IpcPeerSession Peer {get;set;}=null!;
    public Task Completion=>completion.Task;
    /// <summary>설정을 10초 안에 받지 못하면 끝낸다.</summary>
    public bool Expired=>config is null&&bootstrap.Elapsed.TotalSeconds>=10;
    MonitorDescriptor CurrentMonitor {get{lock(gate)return monitor!;}}
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
                    if(!identity.IsSystem||Process.GetCurrentProcess().SessionId!=value.WindowsSessionId||value.Approval.AdminGeneration<1||value.TargetFps is <1 or >60||value.MaxShortEdge is <2 or >2160||value.MaxLongEdge<value.MaxShortEdge||value.MaxLongEdge>3840)
                        throw new InvalidOperationException("관리자 헬퍼 설정이 현재 세션과 맞지 않는다.");
                    monitor=value.Monitor;_=Resolve(value.Monitor);mapping=SharedFrameMap.Open(value.Mapping,true);config=value;
                    worker=Run();
                }
                break;
            case "admin-monitor":
                // 지원자가 관리자 모드에서 다른 모니터를 골랐다. 캡처 루프가 다음 반복에서 새 모니터로 다시 연다.
                var next=message.Body?.Deserialize<MonitorDescriptor>()??throw new InvalidDataException("모니터 누락이다.");
                _=Resolve(next);lock(gate)monitor=next;break;
            case "admin-input":
                var batch=message.Body?.Deserialize<AdminInputBatch>()??throw new InvalidDataException("관리자 입력 누락이다.");AdminInputValidation.Validate(batch);
                if(config==null||batch.AdminGeneration!=config.Approval.AdminGeneration)throw new InvalidOperationException("이전 관리자 세대의 입력이다.");
                var reply=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                if(!input.Writer.TryWrite(new(batch,reply)))throw new IOException("관리자 입력 큐 상한이다.");
                var count=await reply.Task.WaitAsync(TimeSpan.FromMilliseconds(400),ct);
                Peer.Post("admin-input-ack",new AdminInputResult(count),message.Id);break;
            default:throw new InvalidDataException("관리자 헬퍼에 허용되지 않은 요청이다.");
        }
    }
    async Task Run()
    {
        try
        {
            var value=config!;var first=true;
            while(!stop.IsCancellationRequested)
            {
                var finished=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Interlocked.Increment(ref desktopRunning);
                var thread=new Thread(()=>
                {
                    try{CaptureDesktop(value,()=>{if(first){first=false;Peer.Post("admin-ready",id:value.Approval.RequestId);}});finished.TrySetResult();}
                    catch(Exception ex){finished.TrySetException(ex);}
                    finally{Interlocked.Decrement(ref desktopRunning);}
                }) {Name="RemoteAssist admin desktop",IsBackground=true};
                thread.Start();await finished.Task.WaitAsync(stop.Token);
            }
        }
        catch(OperationCanceledException)when(stop.IsCancellationRequested){}
        catch(Exception ex){LocalDiagnostic.Failure("admin-desktop",ex);}
        finally{Stop();completion.TrySetResult();}
    }
    /// <summary>현재 입력 데스크톱 하나를 캡처한다. 데스크톱이나 모니터가 바뀌면 돌아가고, 바깥 루프가 새로 연다.</summary>
    void CaptureDesktop(AdminConfiguration value,Action ready)
    {
        using var desktop=new DesktopContext();
        var secure=desktop.Name=="Winlogon";
        var selected=CurrentMonitor;
        using var indicator=secure?new SecureStopWindow(selected,Stop):null;
        var target=Resolve(selected);
        using IScreenCapture capture=secure?new DxgiScreenCapture(target):ScreenCaptureFactory.Create(target);
        using var scaler=new BgraFrameScaler(Math.Min(value.MaxLongEdge,1920),Math.Min(value.MaxShortEdge,1080));
        ready();var next=TimeSpan.Zero;var clock=Stopwatch.StartNew();
        ReadOnlyMemory<byte> previous=default;
        var remoteKeys=new HashSet<ushort>();var remoteButtons=new HashSet<string>();var remoteUnicode=new HashSet<char>();
        try
        {
            while(!stop.IsCancellationRequested&&desktop.IsReceivingInput&&ReferenceEquals(selected,CurrentMonitor))
            {
                DemandTarget(value);
                indicator?.Pump();
                while(input.Reader.TryRead(out var work))
                {
                    try
                    {
                        if(work.Batch.AdminGeneration!=value.Approval.AdminGeneration||!desktop.IsReceivingInput||stop.IsCancellationRequested){work.Reply.TrySetResult(0);continue;}
                        // 모니터 전환 직후 이전 좌표로 들어온 입력은 버린다. 세션은 유지한다.
                        if(work.Batch.Strokes.Any(s=>s.Kind==StrokeKind.Move&&(s.X<target.X||s.X>=target.X+(long)target.Width||s.Y<target.Y||s.Y>=target.Y+(long)target.Height))){work.Reply.TrySetResult(0);continue;}
                        var sent=WindowsInputBackend.SendAsAdmin(work.Batch.Strokes);
                        foreach(var s in work.Batch.Strokes.Take(sent))
                        {
                            if(s.Kind==StrokeKind.Key){if(s.Up)remoteKeys.Remove(s.Key);else remoteKeys.Add(s.Key);}
                            if(s.Kind==StrokeKind.Button){if(s.Up)remoteButtons.Remove(s.Button);else remoteButtons.Add(s.Button);}
                            if(s.Kind==StrokeKind.Unicode){if(s.Up)remoteUnicode.Remove(s.Character);else remoteUnicode.Add(s.Character);}
                        }
                        work.Reply.TrySetResult(sent);
                    }
                    catch(Exception ex){work.Reply.TrySetException(ex);}
                }
                if(clock.Elapsed>=next)
                {
                    next=clock.Elapsed+TimeSpan.FromSeconds(1d/value.TargetFps);
                    if(capture.TryGetFrame(TimeSpan.FromMilliseconds(5),out var frame))
                        previous=scaler.Scale(frame.Bgra,frame.Width,frame.Height,frame.Stride);
                    else if(previous.IsEmpty&&clock.Elapsed>TimeSpan.FromSeconds(3))throw new TimeoutException("관리자 캡처 첫 프레임을 얻지 못했다.");
                    if(!desktop.IsReceivingInput||stop.IsCancellationRequested)return;
                    if(!previous.IsEmpty)mapping!.Publish(previous.Span,scaler.Width,scaler.Height,scaler.Width*4,value.Approval.PermissionGeneration,AdminFrameSource.MappingTopology,value.Approval.AdminGeneration,secure,Environment.TickCount64);
                }
                stop.Token.WaitHandle.WaitOne(5);
            }
        }
        finally
        {
            // 데스크톱을 떠날 때 이 데스크톱에서 눌러 둔 키·버튼을 뗀다.
            var release=remoteKeys.Select(k=>new InputStroke(StrokeKind.Key,Key:k,Up:true)).Concat(remoteButtons.Select(b=>new InputStroke(StrokeKind.Button,Button:b,Up:true))).Concat(remoteUnicode.Select(c=>new InputStroke(StrokeKind.Unicode,Character:c,Up:true))).ToArray();
            if(release.Length>0)try{WindowsInputBackend.SendAsAdmin(release);}catch(Exception ex){LocalDiagnostic.Failure("admin-release",ex);}
            mapping!.Clear();
        }
    }
    static MonitorInfo Resolve(MonitorDescriptor m)
    {
        var monitors=Monitors.Enumerate();
        return monitors.FirstOrDefault(x=>x.DeviceName==m.Identity&&x.X==m.X&&x.Y==m.Y&&x.Width==m.Width&&x.Height==m.Height) is {Handle:not 0} exact?exact
            :monitors.FirstOrDefault(x=>x.DeviceName==m.Identity) is {Handle:not 0} named?named
            :throw new InvalidDataException("관리자 캡처 모니터가 없다.");
    }
    static void DemandTarget(AdminConfiguration value)
    {if(!SessionProcess.IsInteractiveConsole(value.WindowsSessionId)||SessionProcess.CurrentConsoleUserSid(value.WindowsSessionId)!=value.UserSid)throw new InvalidOperationException("대상 Windows 사용자가 바뀌었다.");}
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
