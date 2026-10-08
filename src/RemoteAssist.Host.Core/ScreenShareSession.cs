using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteAssist.Host.Core.Capture;
using RemoteAssist.Host.Core.Media;
using RemoteAssist.Host.Core.Security;
using RemoteAssist.Host.Core.Signaling;
using RemoteAssist.Host.Core.Windows;
using RemoteAssist.Host.Core.Input;
using RemoteAssist.Host.Core.Diagnostics;
using RemoteAssist.Protocol;
using SIPSorcery.Net;
using SIPSorcery.Sys;
using SIPSorceryMedia.Abstractions;

namespace RemoteAssist.Host.Core;

public sealed record SessionOutcome(string Reason, bool Failed);

/// <summary>로컬 동의 전에는 캡처·PeerConnection을 만들지 않는다. 모든 협상 이벤트는 직렬화한다.</summary>
public sealed class ScreenShareSession : IAsyncDisposable
{
    private readonly ScreenShareOptions _options;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly PermissionGate _permissions = new();
    private readonly Channel<Func<Task>> _events = Channel.CreateBounded<Func<Task>>(128);
    private readonly TaskCompletionSource<SessionOutcome> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _endGate = new();
    private SignalingClient? _signaling;
    private RTCPeerConnection? _pc;
    private Thread? _captureThread;
    private Task? _receiveTask, _eventTask;
    private Task? _pairingTask, _negotiationTask;
    private CancellationTokenRegistration _callerCancellation;
    private int _started, _ended, _disposed;
    private readonly KeyframeRequests _keyframes = new();
    private string _encoderName = "(미선택)";
    private MonitorInfo _monitor;
    private bool _answerApplied;
    private InputDataSession? _inputData;
    private ControlDataSession? _controlData;
    private AdminFrameSource? _adminFrame;
    private FeatureDataChannel? _chat,_telemetry;
    private Task? _diagnosticsTask;
    private readonly Queue<IceCandidate> _remoteIce = new();
    public SessionCredentials? Credentials { get; private set; }
    public Task<SessionOutcome> Completion => _completion.Task;
    public string EncoderName => Volatile.Read(ref _encoderName);
    public MonitorInfo Monitor => _monitor;
    public Capability GrantedCapabilities=>_permissions.Current?.Capabilities??Capability.None;
    public event Action<int, int>? FrameSent;
    public event Action<string>? StateChanged;
    public Func<SupporterIdentity, CancellationToken, Task<Capability>>? RequestLocalConsent { get; set; }
    public Func<PermissionSnapshot,IInputBackend>? CreateInputBackend {get;set;}
    public Action? InputLeaseObserved {get;set;}
    public Action<ControlMessage>? AdminCommand {get;set;}
    public Action<FeatureMessage>? ChatReceived {get;set;}
    public Func<FeatureMessage,CancellationToken,Task<FeatureMessage>>? ClipboardOperation {get;set;}
    public IDiagnosticsProvider DiagnosticsProvider {get;set;}=new DiagnosticsCollector();
    public void SendChat(string text,string id)=>_chat!.Send(new(){T="chat",Id=id,Text=text},Capability.Chat);
    public AdminFrameSource? AdminFrame=>Volatile.Read(ref _adminFrame);
    public void ActivateAdmin(AdminFrameSource source)
    {
        lock(_endGate)
        {
            var grant=_permissions.Current;
            if(Volatile.Read(ref _ended)!=0||_adminFrame!=null||grant is null||!grant.Capabilities.HasFlag(Capability.Admin)||grant.SessionId!=source.Approval.SessionId||grant.ConnectionId!=source.Approval.ConnectionId||grant.Generation!=source.Approval.PermissionGeneration||!source.IsCurrent)
            {source.Dispose();throw new UnauthorizedAccessException("현재 관리자 프레임 승인이 아니다.");}
            Volatile.Write(ref _adminFrame,source);_keyframes.Request();
        }
        _controlData!.AdminState(source.Approval.RequestId,"active",source.Approval.AdminGeneration,_inputData!.ResumeLocalPhase());
    }
    public void DenyAdmin(string requestId)=>_controlData!.AdminState(requestId,"denied",inputEpoch:_inputData!.ResumeLocalPhase());

    public ScreenShareSession(ScreenShareOptions options, ILoggerFactory? loggerFactory = null)
    {
        _options = options; _loggerFactory = loggerFactory;
        _log = (ILogger?)loggerFactory?.CreateLogger<ScreenShareSession>() ?? NullLogger.Instance;
    }
    public async Task<SessionCredentials> StartAsync(CancellationToken ct = default)
    {
        if(Volatile.Read(ref _ended)!=0)throw new InvalidOperationException("종료한 세션은 시작할 수 없다.");
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("세션은 한 번만 시작할 수 있다.");
        try
        {
            var monitors=Monitors.Enumerate();
            _options.Validate(monitors.Count);
            _monitor=monitors[_options.MonitorIndex];
            _callerCancellation=ct.Register(() => EndSession("호출자 취소", false));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            Credentials = await SignalingClient.CreateSessionAsync(_options.SignalingUri, deadline.Token,_options.AllowLoopbackHttp);
            _log.LogInformation("세션 생성: {SessionId}", Credentials.SessionId);
            _signaling = new SignalingClient(_options.SignalingUri, _loggerFactory?.CreateLogger<SignalingClient>(),_options.AllowLoopbackHttp);
            await _signaling.ConnectAsync(Credentials, deadline.Token);
            _lifetime.Token.ThrowIfCancellationRequested();
            _eventTask = ProcessEventsAsync();
            _receiveTask = ReceiveLoopAsync();
            _pairingTask=WatchPairingAsync(Credentials.ExpiresIn);
            NotifyState("지원자 참가 대기");
            return Credentials;
        }
        catch { EndSession("세션 시작 실패", true); throw; }
    }
    private async Task ProcessEventsAsync()
    {
        try
        {
            await foreach (var action in _events.Reader.ReadAllAsync(_lifetime.Token))
            {
                if (Volatile.Read(ref _ended) != 0) return;
                await action();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { _log.LogError(ex, "세션 상태 전이 실패"); EndSession("협상 또는 상태 처리 실패", true); }
    }
    private void Enqueue(Func<Task> action)
    {
        if (Volatile.Read(ref _ended) == 0 && !_events.Writer.TryWrite(action)) EndSession("이벤트 큐 상한 초과", true);
    }
    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var msg = await _signaling!.ReceiveAsync(_lifetime.Token);
                if (msg is null) { EndSession("시그널링 연결 종료", true); return; }
                if (msg.SessionId is not null && msg.SessionId != Credentials!.SessionId) throw new InvalidDataException("다른 세션 메시지다.");
                if (msg.Generation is not (0 or 1)) throw new InvalidDataException("잘못된 연결 세대다.");
                if (msg.Type == "ping") { await _signaling.SendAsync(new SignalMsg { Type = "pong" }, _lifetime.Token); continue; }
                if (msg.Type == "pong") continue;
                // Termination bypasses a pending local consent or SDP operation.
                if (msg.Type is "peer-left" or "bye" or "error") { EndSession(msg.Code ?? "지원자 연결 종료", msg.Type == "error"); return; }
                Enqueue(() => HandleMessageAsync(msg));
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { _log.LogWarning(ex, "시그널링 수신 실패"); EndSession("시그널링 수신 실패", true); }
    }
    private async Task HandleMessageAsync(SignalMsg msg)
    {
        switch (msg.Type)
        {
            case "peer-joined": await NegotiateAsync(msg); break;
            case "answer":
                if (_pc is null || _answerApplied || string.IsNullOrEmpty(msg.Sdp)) throw new InvalidDataException("예상하지 않은 answer다.");
                if (_pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = msg.Sdp }) != SetDescriptionResultEnum.OK)
                    throw new InvalidDataException("answer 적용 실패다.");
                _answerApplied = true;
                while (_remoteIce.TryDequeue(out var candidate)) ApplyIce(candidate);
                break;
            case "ice" when msg.Ice is not null:
                if (msg.Ice.Candidate.Length > 8192 || msg.Ice.SdpMLineIndex is < 0 or > 65535) throw new InvalidDataException("잘못된 ICE다.");
                if (!_answerApplied) { if (_remoteIce.Count >= 256) throw new InvalidDataException("ICE 대기 상한 초과다."); _remoteIce.Enqueue(msg.Ice); }
                else ApplyIce(msg.Ice);
                break;
            default: throw new InvalidDataException("예상하지 않은 시그널링 메시지다.");
        }
    }
    private void ApplyIce(IceCandidate candidate) => _pc!.addIceCandidate(new RTCIceCandidateInit
    { candidate = candidate.Candidate, sdpMid = candidate.SdpMid, sdpMLineIndex = (ushort)(candidate.SdpMLineIndex ?? 0) });

    private async Task NegotiateAsync(SignalMsg peer)
    {
        if (_pc is not null) return;
        if (string.IsNullOrEmpty(peer.ConnectionId)) throw new InvalidDataException("지원자 바인딩이 없다.");
        NotifyState("로컬 동의 대기");
        await _signaling!.SendAsync(new SignalMsg { Type = "consent-pending" }, _lifetime.Token);
        using var consentDeadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        consentDeadline.CancelAfter(TimeSpan.FromSeconds(30));
        var capability = RequestLocalConsent is null ? Capability.None :
            await RequestLocalConsent(new SupporterIdentity(peer.ConnectionId, peer.DisplayName ?? "이름 미제공"), consentDeadline.Token).WaitAsync(consentDeadline.Token);
        consentDeadline.Token.ThrowIfCancellationRequested();
        if (_lifetime.IsCancellationRequested || capability == Capability.None) { EndSession("대상자가 승인을 거절했다", false); return; }
        // Only implemented capabilities can be granted. Future features stay denied.
        if ((capability & ~(Capability.Screen|Capability.Control|Capability.Admin|Capability.Chat|Capability.Clipboard|Capability.Diagnostics)) != 0||capability.HasFlag(Capability.Control)&&CreateInputBackend==null||capability.HasFlag(Capability.Admin)&&(!capability.HasFlag(Capability.Control)||AdminCommand==null)||capability.HasFlag(Capability.Clipboard)&&ClipboardOperation==null||capability.HasFlag(Capability.Chat)&&ChatReceived==null) throw new InvalidOperationException("아직 연결하지 않은 기능 권한이다.");
        var grant = _permissions.Grant(Credentials!.SessionId, peer.ConnectionId, capability, TimeSpan.FromHours(2));
        await _signaling.SendAsync(new SignalMsg { Type = "consent", Permissions = FeatureContract.Permissions((int)capability), PermissionGeneration = (int)grant.Generation }, _lifetime.Token);
        var iceServers = _options.StunServers.Select(url => new RTCIceServer {urls=url}).ToList();
        foreach(var server in _signaling.JoinedAck?.IceServers ?? [])
            foreach(var url in server.Urls)iceServers.Add(new RTCIceServer{urls=url,username=server.Username,credential=server.Credential});
        if(_options.ForceRelay&&!iceServers.Any(server=>server.urls.StartsWith("turn:",StringComparison.Ordinal)||server.urls.StartsWith("turns:",StringComparison.Ordinal)))throw new InvalidOperationException("relay 강제 모드에 TURN 설정이 없다.");
        _pc = new RTCPeerConnection(new RTCConfiguration
        { iceServers = iceServers, iceTransportPolicy = _options.ForceRelay ? RTCIceTransportPolicy.relay : RTCIceTransportPolicy.all }, 0, new PortRange(27002, 27099));
        var pc = _pc;
        var controlChannel=await pc.createDataChannel("control",new RTCDataChannelInit {ordered=true});
        var control=new ControlDataSession(controlChannel,_permissions,grant,_monitor,reason=>EndSession(reason,true),InputLeaseObserved,message=>
        {
            if(message.T is "admin-sas" or "admin-revoke"&&AdminFrame?.Approval.AdminGeneration!=message.AdminGeneration)throw new UnauthorizedAccessException("현재 관리자 세대가 아니다.");
            if(message.T=="admin-request")_inputData!.SuspendForApproval();
            if(message.T=="admin-revoke")EndSession("지원자가 관리자 모드와 지원 세션을 해제했다.");else AdminCommand!(message);
        });_controlData=control;control.Attach();
        if(capability.HasFlag(Capability.Control))
        {
            _inputData=new(new InputSession(_permissions,_monitor,1,CreateInputBackend!(grant)),_lifetime.Token,reason=>EndSession(reason,true),control.ResetAcknowledged);
            _inputData.Attach(await pc.createDataChannel("input",new RTCDataChannelInit {ordered=true}));
            _inputData.Attach(await pc.createDataChannel("pointer",new RTCDataChannelInit {ordered=true}));_inputData.Start();
        }
        if((capability&(Capability.Chat|Capability.Clipboard))!=0)
        {
            _chat=new(await pc.createDataChannel("chat",new RTCDataChannelInit {ordered=true}),_permissions,grant,_lifetime.Token,async (message,token)=>
            {
                if(message.T=="chat")
                {if(!_permissions.Execute(Capability.Chat,grant.Generation,()=>ChatReceived!(message)))throw new UnauthorizedAccessException("채팅 승인이 종료됐다.");}
                else
                {
                    if(!_permissions.Allows(Capability.Clipboard,grant.Generation))throw new UnauthorizedAccessException("클립보드 승인이 없다.");
                    var reply=await ClipboardOperation!(message,token);if(reply.Id!=message.Id||reply.T is not ("clipboard-text" or "clipboard-applied" or "clipboard-rejected"))throw new InvalidDataException("클립보드 응답 방향 오류다.");
                    _chat!.Send(reply,Capability.Clipboard);
                }
            },reason=>EndSession(reason,true));_chat.Attach();
        }
        if(capability.HasFlag(Capability.Diagnostics))
        {
            _telemetry=new(await pc.createDataChannel("telemetry",new RTCDataChannelInit {ordered=true}),_permissions,grant,_lifetime.Token,(_,_)=>throw new InvalidDataException("Viewer가 telemetry를 보낼 수 없다."),reason=>EndSession(reason,true));_telemetry.Attach();
            _diagnosticsTask=ObserveDiagnostics(grant);
        }
        pc.ondatachannel+=_=>EndSession("미지원 또는 중복 기능 채널이다.",true);
        var feedbackBinding=new RtcpFeedbackBinding();
        pc.OnReceiveReport += (_, media, report) =>
        {
            if (Volatile.Read(ref _ended) == 0)
            {
                feedbackBinding.Observe(media,report,pc.VideoLocalTrack,pc.VideoRemoteTrack);
                _keyframes.Observe(media, report);
            }
        };
        if(capability.HasFlag(Capability.Screen))pc.addTrack(new MediaStreamTrack(new VideoFormat(VideoCodecsEnum.H264, 102, 90000, "packetization-mode=1"), MediaStreamStatusEnum.SendOnly));
        pc.onicecandidate += candidate =>
        {
            if (candidate is null) return;
            Enqueue(() => _signaling.SendAsync(new SignalMsg { Type = "ice", Room = Credentials.Code,
                Ice = new IceCandidate { Candidate = IceCandidateWire.FromNative(candidate.candidate), SdpMid = candidate.sdpMid, SdpMLineIndex = candidate.sdpMLineIndex } }, _lifetime.Token));
        };
        pc.onconnectionstatechange += state =>
        {
            if (state is RTCPeerConnectionState.closed or RTCPeerConnectionState.failed or RTCPeerConnectionState.disconnected)
                EndSession("WebRTC 연결 종료: " + state, state != RTCPeerConnectionState.closed);
            else if (state == RTCPeerConnectionState.connected)
                Enqueue(() => { if (_captureThread is null && _permissions.Allows(Capability.Screen, grant.Generation)) StartStreaming(grant.Generation);else if(!grant.Capabilities.HasFlag(Capability.Screen))NotifyState("승인한 보조 기능 연결 중");return Task.CompletedTask; });
        };
        // ICE callbacks enqueue behind this operation; offer therefore precedes candidates.
        var offer = pc.createOffer(null);
        await pc.setLocalDescription(offer);
        _lifetime.Token.ThrowIfCancellationRequested();
        await _signaling.SendAsync(new SignalMsg { Type = "offer", Room = Credentials.Code, Sdp = offer.sdp }, _lifetime.Token);
        NotifyState("WebRTC 협상 중");
        _negotiationTask = WatchNegotiationAsync(pc);
    }
    private async Task WatchNegotiationAsync(RTCPeerConnection pc)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), _lifetime.Token); if (pc.connectionState != RTCPeerConnectionState.connected) EndSession("협상 시간 초과", true); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }
    private async Task ObserveDiagnostics(PermissionSnapshot grant)
    {
        try{await DiagnosticsLoop.Run(DiagnosticsProvider,_permissions,grant,()=>_telemetry!.IsOpen,message=>_telemetry!.Send(message,Capability.Diagnostics),_lifetime.Token);}
        catch(OperationCanceledException)when(_lifetime.IsCancellationRequested){}
        catch(Exception){EndSession("진단 수집이 실패하거나 기한을 넘었다.",true);}
    }
    private async Task WatchPairingAsync(int expiresIn)
    {
        try {await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(expiresIn,1,1800)),_lifetime.Token);if(_pc is null)EndSession("참가 코드 유효기간 종료",false);}
        catch(OperationCanceledException)when(_lifetime.IsCancellationRequested){}
    }
    private void StartStreaming(long generation)
    {
        _captureThread = new Thread(() => CaptureLoop(generation)) { Name = "RemoteAssist capture", IsBackground = true };
        _captureThread.Start();
    }
    private void CaptureLoop(long permissionGeneration)
    {
        IScreenCapture? capture=null;
        ReadOnlyMemory<byte> lastFrame=default;
        try
        {
            capture = WgcScreenCapture.IsSupported() ? (IScreenCapture)new WgcScreenCapture(_monitor) : new DxgiScreenCapture(_monitor);
            _monitor = capture.Monitor;
            using var encoder = new FFmpegH264Encoder(_options.PreferredEncoder, _loggerFactory?.CreateLogger<FFmpegH264Encoder>());
            using var scaler = new BgraFrameScaler(_options.MaxOutputLongEdge,_options.MaxOutputShortEdge);
            bool privilegedSource=false;
            var width = 0; var height = 0; var sent = 0;
            var clock = Stopwatch.StartNew(); var previousSend = TimeSpan.Zero;
            var nextTick=TimeSpan.Zero;var interval=TimeSpan.FromSeconds(1d/_options.TargetFps);
            var nextRecoveryKeyframe=TimeSpan.Zero;
            NotifyState("승인한 화면 공유 중");
            while (!_lifetime.IsCancellationRequested && _permissions.Allows(Capability.Screen, permissionGeneration))
            {
                var admin=AdminFrame;
                if(admin!=null)
                {
                    if(!privilegedSource){capture?.Dispose();capture=null;ClearCopiedFrame(ref lastFrame);privilegedSource=true;}
                    ClearCopiedFrame(ref lastFrame);
                    if(admin.TryRead(1,out var privileged))
                    {
                        lastFrame=privileged!.Pixels;
                        if(width!=privileged.Width||height!=privileged.Height){width=privileged.Width;height=privileged.Height;encoder.Configure(width,height,_options.TargetFps,_options.BitrateKbps);Volatile.Write(ref _encoderName,encoder.EncoderName);_keyframes.Request();}
                    }
                }
                else if(!InputDesktop.IsDefault()){EndSession("관리자 승인이 없는 데스크톱 전환으로 화면 공유를 종료했다.");return;}
                else if (capture!.TryGetFrame(TimeSpan.FromMilliseconds(5), out var frame))
                {
                    FrameLayout.Validate(frame.Bgra, frame.Width, frame.Height, frame.Stride);
                    lastFrame=scaler.Scale(frame.Bgra,frame.Width,frame.Height,frame.Stride);
                    if (scaler.Width != width || scaler.Height != height)
                    {
                        width = scaler.Width; height = scaler.Height;
                        encoder.Configure(width, height, _options.TargetFps, _options.BitrateKbps);
                        Volatile.Write(ref _encoderName, encoder.EncoderName);
                        _keyframes.Request();
                    }
                }
                if (!lastFrame.IsEmpty)
                {
                    var periodic=clock.Elapsed>=nextRecoveryKeyframe;
                    if(periodic)nextRecoveryKeyframe=clock.Elapsed+TimeSpan.FromSeconds(2);
                    var encoded = encoder.Encode(lastFrame, _keyframes.Consume()||periodic);
                    if (!encoded.IsEmpty)
                    {
                        // End and the final send are serialized, without disposing a live encoder from another thread.
                        lock (_endGate)
                        {
                            if (Volatile.Read(ref _ended) != 0 || !_permissions.Allows(Capability.Screen, permissionGeneration)) break;
                            if(admin is null?!InputDesktop.IsDefault():!admin.IsCurrent){EndSession("데스크톱 송신 승인이 종료됐다.");return;}
                            var now = clock.Elapsed;
                            var delta = previousSend == TimeSpan.Zero ? 1.0 / _options.TargetFps : (now - previousSend).TotalSeconds;
                            _pc!.SendVideo((uint)Math.Max(1, Math.Round(delta * 90000)), encoded.ToArray());
                            previousSend = now;
                        }
                        FrameSent?.Invoke(++sent, encoded.Length);
                    }
                }
                nextTick+=interval;
                if(clock.Elapsed-nextTick>interval)nextTick=clock.Elapsed;
                var wait=nextTick-clock.Elapsed;
                if(wait>TimeSpan.Zero)_lifetime.Token.WaitHandle.WaitOne(wait);
            }
            if (!_lifetime.IsCancellationRequested) EndSession("권한 유효기간 종료", false);
        }
        catch (Exception ex) { _log.LogError(ex, "미디어 파이프라인 실패"); EndSession("미디어 파이프라인 실패", true); }
        finally{capture?.Dispose();ClearCopiedFrame(ref lastFrame);}
    }
    static void ClearCopiedFrame(ref ReadOnlyMemory<byte> frame)
    {
        if(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(frame,out var bytes)&&bytes.Array!=null)System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes.AsSpan());
        frame=default;
    }
    public void EndSession(string reason = "로컬 종료", bool failed = false)
    {
        lock (_endGate)
        {
            if (Interlocked.Exchange(ref _ended, 1) != 0) return;
            _permissions.End();
            try {_lifetime.Cancel();}catch(AggregateException){_log.LogWarning("취소 관찰자 오류다. 기능 권한은 이미 폐기했다.");}
            _events.Writer.TryComplete();
            _completion.TrySetResult(new SessionOutcome(reason, failed));
        }
        NotifyState(reason);
    }
    private void NotifyState(string state)
    {
        foreach(var observer in StateChanged?.GetInvocationList()??[])
            try{((Action<string>)observer)(state);}catch(Exception){_log.LogWarning("상태 관찰자 오류다.");}
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        EndSession();
        var clock = Stopwatch.StartNew();
        if (_captureThread is { IsAlive: true } thread)
        {
            var stopped = await Task.Run(() => thread.Join(TimeSpan.FromSeconds(3)));
            if (!stopped) throw new TimeoutException("네이티브 캡처가 정지하지 않았다. 살아 있는 스레드의 자원은 해제하지 않는다. 헬퍼 감독 종료가 필요하다.");
        }
        if (_signaling is not null) await _signaling.DisposeAsync();
        var pending = new[] { _eventTask, _receiveTask, _pairingTask, _negotiationTask,_inputData?.Completion,_chat?.Completion,_telemetry?.Completion,_diagnosticsTask }.OfType<Task>();
        await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(Math.Max(.1, 5 - clock.Elapsed.TotalSeconds)));
        _pc?.close(); _pc?.Dispose();
        _adminFrame?.Dispose();
        _callerCancellation.Dispose();
        _lifetime.Dispose();
    }
}
