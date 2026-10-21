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
using RemoteAssist.Host.Core.Files;
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
    private readonly LocalApprovalGate _localApproval=new();
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
    private sealed record MonitorBinding(MonitorInfo Value);
    private MonitorBinding _monitorBinding=new(default);
    private MonitorInfo _monitor {get=>Volatile.Read(ref _monitorBinding).Value;set=>Volatile.Write(ref _monitorBinding,new(value));}
    private bool _answerApplied;
    private InputDataSession? _inputData;
    private ControlDataSession? _controlData;
    private AdminFrameSource? _adminFrame;
    private FeatureDataChannel? _chat,_telemetry;
    private Task? _diagnosticsTask;
    private FileTransferSession? _files;
    private RunbookDataSession? _runbooks;
    public Func<RunbookMessage,CancellationToken,Task<RunbookResult>>? RunbookOperation {get;set;}
    private AdaptiveQuality? _quality;
    private H264Contract? _h264;
    private long _topologyGeneration=1;
    private MonitorSwitch? _switch;
    private CancellationTokenSource? _transportLife;
    private int _connectionGeneration=1,_recoveryAttempts;
    private long _lastRefreshAt;
    private volatile bool _mediaPaused;
    private int _formatPaused;
    private bool _refreshPending;
    private Task? _refreshTask;
    private IceServerInfo[]? _turnServers;
    public int ConnectionGeneration=>Volatile.Read(ref _connectionGeneration);
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
    public Func<FileMessage,CancellationToken,Task<string?>>? RequestFileApproval {get;set;}
    public Func<FileAuditNotice,CancellationToken,Task>? AuditFile {get;set;}
    public Action<FileTransferState>? FileState {get;set;}
    public Func<MonitorDescriptor,CancellationToken,Task<bool>>? RequestMonitorApproval {get;set;}
    public long TopologyGeneration=>Interlocked.Read(ref _topologyGeneration);
    public bool SendFile(string path,string id)=>_files?.SendFile(path,id)==true;
    public void CancelFile(string id)=>_files?.CancelFile(id);
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
        var epoch=_inputData!.ResumeLocalPhase();
        _localApproval.Complete(source.Approval.RequestId,"admin");
        _controlData!.AdminState(source.Approval.RequestId,"active",source.Approval.AdminGeneration,epoch);
    }
    public void DenyAdmin(string requestId){var epoch=_inputData!.ResumeLocalPhase();_localApproval.Complete(requestId,"admin");_controlData!.AdminState(requestId,"denied",inputEpoch:epoch);}
    public async Task<T?> WithLocalApproval<T>(string kind,string id,Func<CancellationToken,Task<T?>> request,CancellationToken ct)
    {
        var capability=kind switch {"file"=>Capability.File,"runbook"=>Capability.Runbook,"monitor"=>Capability.Screen,_=>throw new ArgumentException("지원하는 로컬 승인 종류가 아니다.")};
        var grant=_permissions.Current;if(grant is null||!_permissions.Allows(capability,grant.Generation))throw new UnauthorizedAccessException("로컬 승인 기능의 현재 동의가 없다.");
        if(AdminFrame!=null||!_localApproval.TryBegin(id,kind))return default;
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct,_lifetime.Token);timeout.CancelAfter(TimeSpan.FromSeconds(kind=="runbook"?55:30));
        try
        {
            _inputData?.SuspendForApproval();_controlData!.LocalApproval(id,kind,"pending");
            return await request(timeout.Token).WaitAsync(timeout.Token);
        }
        catch(OperationCanceledException)
        {
            // A timed-out local dialog may still own focus. Never resume remote input beneath it.
            EndSession("건별 승인 또는 실행을 취소하거나 제한 시간을 초과했다.",false);throw;
        }
        finally
        {
            if(!_lifetime.IsCancellationRequested){var epoch=_inputData?.ResumeLocalPhase();if(_localApproval.Complete(id,kind))_controlData!.LocalApproval(id,kind,"resumed",epoch);}
            else _localApproval.Complete(id,kind);
        }
    }

    public ScreenShareSession(ScreenShareOptions options, ILoggerFactory? loggerFactory = null)
    {
        _options = options; _loggerFactory = loggerFactory;
        _log = (ILogger?)loggerFactory?.CreateLogger<ScreenShareSession>() ?? NullLogger.Instance;
    }
    public async Task<SessionCredentials> StartAsync(CancellationToken ct = default)
    {
        CaseTrace.Write("session-runtime-start");
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
        catch (Exception ex) { LocalDiagnostic.Failure("session-transition",ex); _log.LogError("세션 상태 전이 실패: {Kind}",ex.GetType().Name); EndSession("협상 또는 상태 처리 실패", true); }
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
                if(msg.Generation!=0&&msg.Generation!=ConnectionGeneration){if(msg.Generation<ConnectionGeneration)continue;throw new InvalidDataException("잘못된 연결 세대다.");}
                if (msg.Type == "ping") { await _signaling.SendAsync(new SignalMsg { Type = "pong" }, _lifetime.Token); continue; }
                if (msg.Type == "pong") continue;
                // Termination bypasses a pending local consent or SDP operation.
                if (msg.Type is "peer-left" or "bye" or "error") { EndSession(msg.Code ?? "지원자 연결 종료", msg.Type == "error"); return; }
                Enqueue(() => HandleMessageAsync(msg));
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { LocalDiagnostic.Failure("signaling-receive",ex); _log.LogWarning("시그널링 수신 실패: {Kind}",ex.GetType().Name); EndSession("시그널링 수신 실패", true); }
    }
    private async Task HandleMessageAsync(SignalMsg msg)
    {
        if(msg.Generation!=0&&msg.Generation<ConnectionGeneration)return;
        switch (msg.Type)
        {
            case "peer-joined": await NegotiateAsync(msg); break;
            case "turn-credentials":
                if(!_refreshPending)throw new InvalidDataException("예상하지 않은 TURN 갱신이다.");
                _turnServers=msg.IceServers??[];_refreshPending=false;await RebuildTransport();break;
            case "answer":
                if (_pc is null || _answerApplied || string.IsNullOrEmpty(msg.Sdp)) throw new InvalidDataException("예상하지 않은 answer다.");
                if(GrantedCapabilities.HasFlag(Capability.Screen))
                {
                    var limits=H264Contract.FromAnswer(msg.Sdp);Volatile.Write(ref _h264,limits);var mode=_quality?.Current.Mode??"auto";
                    _quality=new(Math.Min(_options.TargetFps,limits.Fps),Math.Min(_options.BitrateKbps,limits.Bitrate),Math.Min(_options.MaxOutputLongEdge,limits.LongEdge),Math.Min(_options.MaxOutputShortEdge,limits.ShortEdge));_quality.Mode(mode);
                }
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
        consentDeadline.CancelAfter(TimeSpan.FromSeconds(ScreenShareOptions.ConsentDecisionSeconds));
        var capability = RequestLocalConsent is null ? Capability.None :
            await RequestLocalConsent(new SupporterIdentity(peer.ConnectionId, peer.DisplayName ?? "이름 미제공"), consentDeadline.Token).WaitAsync(consentDeadline.Token);
        consentDeadline.Token.ThrowIfCancellationRequested();
        if (_lifetime.IsCancellationRequested || capability == Capability.None) { EndSession("대상자가 승인을 거절했다", false); return; }
        // Only implemented capabilities can be granted. Future features stay denied.
        if (capability.HasFlag(Capability.Runbook)&&RunbookOperation==null||capability.HasFlag(Capability.File)&&(RequestFileApproval==null||AuditFile==null)||capability.HasFlag(Capability.Control)&&CreateInputBackend==null||capability.HasFlag(Capability.Admin)&&(!capability.HasFlag(Capability.Control)||AdminCommand==null)||capability.HasFlag(Capability.Clipboard)&&ClipboardOperation==null||capability.HasFlag(Capability.Chat)&&ChatReceived==null) throw new InvalidOperationException("아직 연결하지 않은 기능 권한이다.");
        var grant = _permissions.Grant(Credentials!.SessionId, peer.ConnectionId, capability, TimeSpan.FromHours(2));
        CaseTrace.Write("consent-granted",grant.SessionId,grant.Generation,new {mask=(int)capability,connectionId=grant.ConnectionId});
        await _signaling.SendAsync(new SignalMsg { Type = "consent", Permissions = FeatureContract.Permissions((int)capability), PermissionGeneration = (int)grant.Generation }, _lifetime.Token);
        await CreateTransport(grant);
        _refreshTask=RefreshLoop();
    }
    private async Task CreateTransport(PermissionSnapshot grant)
    {
        var capability=grant.Capabilities;var generation=ConnectionGeneration;
        _transportLife=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);var token=_transportLife.Token;
        Action<string> fail=reason=>{if(ConnectionGeneration==generation&&!_lifetime.IsCancellationRequested)EndSession(reason,true);};
        var iceServers = _options.StunServers.Select(url => new RTCIceServer {urls=url}).ToList();
        var supplied=_turnServers??_signaling!.JoinedAck?.IceServers??[];
        if(supplied.Length>8||supplied.Any(s=>s is null||s.Urls is not {Length:>0 and <=8}||s.Urls.Any(u=>u is null||u.Length>1024||!System.Text.RegularExpressions.Regex.IsMatch(u,@"^(stun|stuns|turn|turns):[^\s]+$"))||s.Username?.Length>256||s.Credential?.Length>256))throw new InvalidDataException("ICE server 계약 오류다.");
        foreach(var server in supplied)
            foreach(var url in server.Urls)iceServers.Add(new RTCIceServer{urls=url,username=server.Username,credential=server.Credential});
        if(_options.ForceRelay&&!iceServers.Any(server=>server.urls.StartsWith("turn:",StringComparison.Ordinal)||server.urls.StartsWith("turns:",StringComparison.Ordinal)))throw new InvalidOperationException("relay 강제 모드에 TURN 설정이 없다.");
        _pc = new RTCPeerConnection(new RTCConfiguration
        { iceServers = iceServers, iceTransportPolicy = _options.ForceRelay ? RTCIceTransportPolicy.relay : RTCIceTransportPolicy.all }, 0, new PortRange(27002, 27099));
        var pc = _pc;
        CaseTrace.Write("transport-created",grant.SessionId,grant.Generation,new {connectionGeneration=generation,policy=_options.ForceRelay?"relay":"all"});
        var controlChannel=await pc.createDataChannel("control",new RTCDataChannelInit {ordered=true});
        var control=new ControlDataSession(controlChannel,_permissions,grant,_monitor,fail,InputLeaseObserved,message=>
        {
            if(message.T is "admin-sas" or "admin-revoke"&&AdminFrame?.Approval.AdminGeneration!=message.AdminGeneration)throw new UnauthorizedAccessException("현재 관리자 세대가 아니다.");
            if(message.T=="admin-request")
            {if(!_localApproval.TryBegin(message.Id,"admin"))throw new UnauthorizedAccessException("다른 로컬 승인 창이 열려 있다.");_inputData!.SuspendForApproval();}
            if(message.T=="admin-revoke")EndSession("지원자가 관리자 모드와 지원 세션을 해제했다.");else AdminCommand!(message);
        },message=>Enqueue(()=>ConnectionGeneration==generation?ScreenCommand(message):Task.CompletedTask),generation,TopologyGeneration,()=>ConnectionGeneration==generation&&!token.IsCancellationRequested);_controlData=control;control.Attach();
        _quality??=new(_options.TargetFps,_options.BitrateKbps,_options.MaxOutputLongEdge,_options.MaxOutputShortEdge);
        if(capability.HasFlag(Capability.Control))
        {
            _inputData=new(new InputSession(_permissions,_monitor,TopologyGeneration,CreateInputBackend!(grant),generation),token,fail,control.ResetAcknowledged);
            _inputData.Attach(await pc.createDataChannel("input",new RTCDataChannelInit {ordered=true}));
            _inputData.Attach(await pc.createDataChannel("pointer",new RTCDataChannelInit {ordered=true}));_inputData.Start();
        }
        if((capability&(Capability.Chat|Capability.Clipboard))!=0)
        {
            _chat=new(await pc.createDataChannel("chat",new RTCDataChannelInit {ordered=true}),_permissions,grant,token,async (message,token)=>
            {
                if(message.T=="chat")
                {if(!_permissions.Execute(Capability.Chat,grant.Generation,()=>ChatReceived!(message)))throw new UnauthorizedAccessException("채팅 승인이 종료됐다.");}
                else
                {
                    if(!_permissions.Allows(Capability.Clipboard,grant.Generation))throw new UnauthorizedAccessException("클립보드 승인이 없다.");
                    var reply=await ClipboardOperation!(message,token);if(reply.Id!=message.Id||reply.T is not ("clipboard-text" or "clipboard-applied" or "clipboard-rejected"))throw new InvalidDataException("클립보드 응답 방향 오류다.");
                    token.ThrowIfCancellationRequested();_chat!.Send(reply,Capability.Clipboard);
                }
            },fail,generation);_chat.Attach();
        }
        if(capability.HasFlag(Capability.Diagnostics))
        {
            _telemetry=new(await pc.createDataChannel("telemetry",new RTCDataChannelInit {ordered=true}),_permissions,grant,token,(_,_)=>throw new InvalidDataException("Viewer가 telemetry를 보낼 수 없다."),fail,generation);_telemetry.Attach();
            _diagnosticsTask=ObserveDiagnostics(grant,token,generation);
        }
        if(capability.HasFlag(Capability.File))
        {
            var transport=new FileDataTransport(await pc.createDataChannel("file",new RTCDataChannelInit {ordered=true}));
            _files=new(transport,_permissions,grant,(offer,token)=>WithLocalApproval("file",offer.Id,t=>RequestFileApproval!(offer,t),token),AuditFile!,fail,value=>FileState?.Invoke(value),token,generation);
            transport.Attach(_files,fail);_files.Start();
        }
        if(capability.HasFlag(Capability.Runbook))
        {
            _runbooks=new(await pc.createDataChannel("runbook",new RTCDataChannelInit{ordered=true}),_permissions,grant,generation,async(request,token)=>
            {
                return await WithLocalApproval<RunbookResult>("runbook",request.Id,async t=>(RunbookResult?)await RunbookOperation!(request,t),token)??new RunbookResult("denied",null,"","다른 로컬 승인 또는 관리자 모드가 진행 중이다.","");
            },fail,token);_runbooks.Attach();
        }
        pc.ondatachannel+=_=>fail("미지원 또는 중복 기능 채널이다.");
        var feedbackBinding=new RtcpFeedbackBinding();
        pc.OnReceiveReport += (_, media, report) =>
        {
            if (Volatile.Read(ref _ended) == 0&&ConnectionGeneration==generation)
            {
                feedbackBinding.Observe(media,report,pc.VideoLocalTrack,pc.VideoRemoteTrack);
                _keyframes.Observe(media, report);
            }
        };
        if(capability.HasFlag(Capability.Screen))pc.addTrack(new MediaStreamTrack(new VideoFormat(VideoCodecsEnum.H264, 102, 90000, H264Contract.Offer), MediaStreamStatusEnum.SendOnly));
        pc.onicecandidate += candidate =>
        {
            if (candidate is null||ConnectionGeneration!=generation) return;
            Enqueue(() => ConnectionGeneration!=generation?Task.CompletedTask:_signaling!.SendAsync(new SignalMsg { Type = "ice", Generation=generation,Room = Credentials!.Code,
                Ice = new IceCandidate { Candidate = IceCandidateWire.FromNative(candidate.candidate), SdpMid = candidate.sdpMid, SdpMLineIndex = candidate.sdpMLineIndex } }, _lifetime.Token));
        };
        var recovering=0;
        pc.onconnectionstatechange += state =>
        {
            if(state==RTCPeerConnectionState.connected&&pc.GetRtpChannel() is RtpIceChannel ice&&ice.NominatedEntry is {} pair)
                CaseTrace.Write("selected-pair",grant.SessionId,grant.Generation,new {connectionGeneration=generation,
                    policy=_options.ForceRelay?"relay":"all",selected=pair.Nominated,
                    localType=pair.LocalCandidate.type.ToString(),remoteType=pair.RemoteCandidate.type.ToString(),
                    localAddress=pair.LocalCandidate.address,localPort=pair.LocalCandidate.port,
                    remoteAddress=pair.RemoteCandidate.address,remotePort=pair.RemoteCandidate.port});
            if(ConnectionGeneration!=generation||_lifetime.IsCancellationRequested)return;
            if(state is RTCPeerConnectionState.failed or RTCPeerConnectionState.disconnected){lock(_endGate)_mediaPaused=true;_inputData?.SuspendForApproval();if(Interlocked.Exchange(ref recovering,1)==0)_ = RecoverDisconnected(pc,generation);}
            else if(state==RTCPeerConnectionState.closed)fail("WebRTC 연결 종료다.");
            else if (state == RTCPeerConnectionState.connected)
                Enqueue(() => {if(ConnectionGeneration!=generation||recovering!=0)return Task.CompletedTask;_mediaPaused=false;_recoveryAttempts=0;_keyframes.Request();if (_captureThread is null && _permissions.Allows(Capability.Screen, grant.Generation)) StartStreaming(grant.Generation);else NotifyState(grant.Capabilities.HasFlag(Capability.Screen)?"승인한 화면 공유 중":"승인한 보조 기능 연결 중");return Task.CompletedTask; });
        };
        // ICE callbacks enqueue behind this operation; offer therefore precedes candidates.
        var offer = pc.createOffer(null);
        await pc.setLocalDescription(offer);
        _lifetime.Token.ThrowIfCancellationRequested();
        await _signaling!.SendAsync(new SignalMsg { Type = "offer", Generation=generation,Room = Credentials!.Code, Sdp = offer.sdp }, token);
        NotifyState("WebRTC 협상 중");
        _negotiationTask = WatchNegotiationAsync(pc,token,generation);
    }
    private async Task WatchNegotiationAsync(RTCPeerConnection pc,CancellationToken token,int generation)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), token); if (ConnectionGeneration==generation&&pc.connectionState != RTCPeerConnectionState.connected) EndSession("협상 시간 초과", true); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    private async Task ObserveDiagnostics(PermissionSnapshot grant,CancellationToken token,int generation)
    {
        var telemetry=_telemetry!;
        try{await DiagnosticsLoop.Run(DiagnosticsProvider,_permissions,grant,()=>telemetry.IsOpen,message=>telemetry.Send(message,Capability.Diagnostics),token);}
        catch(OperationCanceledException)when(token.IsCancellationRequested){}
        catch(Exception){if(ConnectionGeneration==generation)EndSession("진단 수집이 실패하거나 기한을 넘었다.",true);}
    }
    private async Task RefreshLoop()
    {
        try{using var timer=new PeriodicTimer(TimeSpan.FromMinutes(8));while(await timer.WaitForNextTickAsync(_lifetime.Token))Enqueue(RequestRefresh);}
        catch(OperationCanceledException)when(_lifetime.IsCancellationRequested){}
    }
    async Task RecoverDisconnected(RTCPeerConnection pc,int generation)
    {
        try{await Task.Delay(TimeSpan.FromSeconds(2),_lifetime.Token);if(ConnectionGeneration==generation)Enqueue(async ()=>{if(_pc==pc)await RequestRefresh();});}
        catch(OperationCanceledException)when(_lifetime.IsCancellationRequested){}
    }
    async Task RequestRefresh()
    {
        if(_refreshPending)return;
        if(_lastRefreshAt!=0){var remaining=TimeSpan.FromSeconds(5)-Stopwatch.GetElapsedTime(_lastRefreshAt);if(remaining>TimeSpan.Zero)await Task.Delay(remaining,_lifetime.Token);}
        if(_localApproval.IsPending||AdminFrame!=null||++_recoveryAttempts>3)throw new InvalidOperationException("현재 승인 단계 또는 복구 횟수 때문에 종료한다.");
        _refreshPending=true;lock(_endGate)_mediaPaused=true;_inputData?.SuspendForApproval();NotifyState("연결 복구 중 · 입력과 영상 송신 중단");
        await _signaling!.SendAsync(new SignalMsg {Type="turn-refresh",Generation=ConnectionGeneration},_lifetime.Token);
        _lastRefreshAt=Stopwatch.GetTimestamp();
        _=WatchRefresh(ConnectionGeneration);
    }
    async Task WatchRefresh(int generation){try{await Task.Delay(TimeSpan.FromSeconds(10),_lifetime.Token);if(_refreshPending&&ConnectionGeneration==generation)EndSession("TURN 갱신 응답 시간 초과다.",true);}catch(OperationCanceledException)when(_lifetime.IsCancellationRequested){}}
    async Task RebuildTransport()
    {
        if(_localApproval.IsPending||AdminFrame!=null||_permissions.Current is not {} grant||!_permissions.Allows(grant.Capabilities,grant.Generation))throw new UnauthorizedAccessException("복구할 현재 승인이 없다.");
        lock(_endGate){_mediaPaused=true;Interlocked.Increment(ref _connectionGeneration);}if(ConnectionGeneration>256)throw new InvalidOperationException("연결 세대 상한이다.");
        _transportLife!.Cancel();_pc!.close();
        if(_files!=null){await _files.DisposeAsync();_files=null;}
        await Task.WhenAll(new[]{_inputData?.Completion,_chat?.Completion,_telemetry?.Completion,_diagnosticsTask,_negotiationTask,_runbooks?.Completion}.OfType<Task>()).WaitAsync(TimeSpan.FromSeconds(3));
        _pc.Dispose();_transportLife.Dispose();_pc=null;_inputData=null;_chat=_telemetry=null;_runbooks=null;_answerApplied=false;_remoteIce.Clear();
        await CreateTransport(grant);
    }
    private async Task WatchPairingAsync(int expiresIn)
    {
        try {await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(expiresIn,1,1800)),_lifetime.Token);if(_pc is null)EndSession("참가 코드 유효기간 종료",false);}
        catch(OperationCanceledException)when(_lifetime.IsCancellationRequested){}
    }
    private void StartStreaming(long generation)
    {
        _captureThread = new Thread(() => CaptureLoop(generation)) { Name = "RemoteAssist capture", IsBackground = true };
        CaseTrace.Write("capture-start",Credentials?.SessionId,generation,new {monitor=_monitor.DeviceName,x=_monitor.X,y=_monitor.Y,width=_monitor.Width,height=_monitor.Height});
        _captureThread.Start();
    }
    MonitorDescriptor Descriptor(MonitorInfo monitor)=>new(monitor.DeviceName,TopologyGeneration,monitor.X,monitor.Y,monitor.Width,monitor.Height);
    async Task ScreenCommand(ControlMessage message)
    {
        var grant=_permissions.Current??throw new UnauthorizedAccessException("현재 화면 승인이 없다.");
        if(!_permissions.Allows(Capability.Screen,grant.Generation))throw new UnauthorizedAccessException("화면 승인이 철회됐다.");
        if(message.T=="monitor-list")_controlData!.Monitors(message.Id,Monitors.Enumerate().Take(16).Select(Descriptor).ToArray());
        else if(message.T=="quality-mode"){_quality!.Mode(message.Mode!);_controlData!.QualityChanged(message.Id,_quality.Current);}
        else if(message.T=="quality-report"){if(_quality!.Observe(message.Sample!))_controlData!.QualityChanged(message.Id,_quality.Current);}
        else
        {
            if(AdminFrame!=null||RequestMonitorApproval==null||_captureThread?.IsAlive!=true)return;
            var target=Monitors.Enumerate().SingleOrDefault(m=>m.DeviceName==message.MonitorIdentity);
            if(target.Handle==0)throw new InvalidDataException("선택한 모니터가 없다.");
            if(target==_monitor){_controlData!.MonitorChanged(message.Id,Descriptor(target));return;}
            await WithLocalApproval<object>("monitor",message.Id,async token=>
            {
                if(!await RequestMonitorApproval(Descriptor(target),token))return null;
                var request=new MonitorSwitch(target,message.Id);
                if(Interlocked.CompareExchange(ref _switch,request,null)!=null)throw new InvalidOperationException("중복 모니터 전환이다.");
                await request.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5),token);return new object();
            },_lifetime.Token);
        }
    }
    sealed record MonitorSwitch(MonitorInfo Target,string Id)
    {public TaskCompletionSource Ready {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);}
    private void CaptureLoop(long permissionGeneration)
    {
        IScreenCapture? capture=null;
        ReadOnlyMemory<byte> lastFrame=default;
        try
        {
            capture = ScreenCaptureFactory.Create(_monitor);
            _monitor = capture.Monitor;
            using var encoder = new FFmpegH264Encoder(_options.PreferredEncoder, _loggerFactory?.CreateLogger<FFmpegH264Encoder>()){MaximumLevel=Volatile.Read(ref _h264)?.Level??throw new InvalidDataException("영상 협상 계약 누락이다.")};
            var quality=_quality!.Current;
            using var scaler = new BgraFrameScaler(quality.LongEdge,quality.ShortEdge);
            bool privilegedSource=false;
            var width = 0; var height = 0; var sent = 0;
            var clock = Stopwatch.StartNew();RTCPeerConnection? timingPeer=null;FrameRtpClock? rtpClock=null;
            var nextTick=TimeSpan.Zero;var interval=TimeSpan.FromSeconds(1d/quality.Fps);var topologyCheck=TimeSpan.Zero;
            MonitorSwitch? changing=null;
            var nextRecoveryKeyframe=TimeSpan.Zero;
            NotifyState("승인한 화면 공유 중");
            while (!_lifetime.IsCancellationRequested && _permissions.Allows(Capability.Screen, permissionGeneration))
            {
                if(Interlocked.Exchange(ref _switch,null) is {} requested)
                {
                    if(AdminFrame!=null||!Monitors.Enumerate().Contains(requested.Target))throw new InvalidOperationException("전환 모니터 상태가 달라졌다.");
                    capture?.Dispose();capture=null;ClearCopiedFrame(ref lastFrame);
                    capture=ScreenCaptureFactory.Create(requested.Target);
                    _monitor=capture.Monitor;Interlocked.Increment(ref _topologyGeneration);
                    var grant=_permissions.Current!;_inputData?.RebindMonitor(_monitor,TopologyGeneration,CreateInputBackend!(grant));
                    width=height=0;changing=requested;_keyframes.Request();Interlocked.Exchange(ref _formatPaused,0);
                }
                if(_mediaPaused||Volatile.Read(ref _formatPaused)!=0){ClearCopiedFrame(ref lastFrame);_lifetime.Token.WaitHandle.WaitOne(20);continue;}
                if(clock.Elapsed>=topologyCheck&&AdminFrame==null)
                {topologyCheck=clock.Elapsed+TimeSpan.FromSeconds(1);if(!Monitors.Enumerate().Contains(_monitor)){PauseForFormatChange();ClearCopiedFrame(ref lastFrame);continue;}}
                var updated=_quality.Current;
                if(updated!=quality||encoder.MaximumLevel!=Volatile.Read(ref _h264)!.Level){quality=updated;encoder.MaximumLevel=Volatile.Read(ref _h264)!.Level;interval=TimeSpan.FromSeconds(1d/quality.Fps);scaler.SetLimits(quality.LongEdge,quality.ShortEdge);ClearCopiedFrame(ref lastFrame);width=height=0;_keyframes.Request();}
                var admin=AdminFrame;
                if(admin!=null)
                {
                    if(!privilegedSource){capture?.Dispose();capture=null;ClearCopiedFrame(ref lastFrame);privilegedSource=true;}
                    ClearCopiedFrame(ref lastFrame);
                    if(admin.TryRead(1,out var privileged))
                    {
                        var source=privileged!.Pixels;
                        try{lastFrame=scaler.Scale(source,privileged.Width,privileged.Height,privileged.Width*4);}finally{System.Security.Cryptography.CryptographicOperations.ZeroMemory(source);}
                        if(width!=scaler.Width||height!=scaler.Height){width=scaler.Width;height=scaler.Height;encoder.Configure(width,height,quality.Fps,quality.BitrateKbps);Volatile.Write(ref _encoderName,encoder.EncoderName);_keyframes.Request();}
                    }
                }
                else if(!InputDesktop.IsDefault()){EndSession("관리자 승인이 없는 데스크톱 전환으로 화면 공유를 종료했다.");return;}
                else
                {
                    var available=capture!.TryGetFrame(TimeSpan.FromMilliseconds(5),out var frame);
                    if(capture.Monitor.DeviceName!=_monitor.DeviceName||capture.Monitor.Width!=_monitor.Width||capture.Monitor.Height!=_monitor.Height){PauseForFormatChange();ClearCopiedFrame(ref lastFrame);continue;}
                    if(available)
                    {
                    if(frame.MonitorIdentity!=_monitor.DeviceName||frame.Width!=_monitor.Width||frame.Height!=_monitor.Height)throw new InvalidDataException("다른 화면 포맷을 거부한다.");
                    FrameLayout.Validate(frame.Bgra, frame.Width, frame.Height, frame.Stride);
                    lastFrame=scaler.Scale(frame.Bgra,frame.Width,frame.Height,frame.Stride);
                    if (scaler.Width != width || scaler.Height != height)
                    {
                        width = scaler.Width; height = scaler.Height;
                        encoder.Configure(width, height, quality.Fps, quality.BitrateKbps);
                        Volatile.Write(ref _encoderName, encoder.EncoderName);
                        _keyframes.Request();
                    }
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
                            if(_mediaPaused)continue;
                            if(admin is null?!InputDesktop.IsDefault():!admin.IsCurrent){EndSession("데스크톱 송신 승인이 종료됐다.");return;}
                            if(timingPeer!=_pc){timingPeer=_pc;rtpClock=new FrameRtpClock(_pc!.VideoLocalTrack.Timestamp);}
                            var timestamp=rtpClock!.Timestamp;
                            // SendVideo increments an inaccessible track timestamp AFTER the current frame.
                            // The public raw RTP API preserves SRTP, SSRC and sequence ownership while accepting the actual sample time.
                            foreach(var packet in H264RtpPayloads.Packetize(encoded.ToArray()))_pc!.SendRtpRaw(SDPMediaTypesEnum.video,packet.Bytes,timestamp,packet.Marker,102);
                        }
                        FrameSent?.Invoke(++sent, encoded.Length);
                        if(changing!=null){_controlData!.MonitorChanged(changing.Id,Descriptor(_monitor));changing.Ready.TrySetResult();changing=null;NotifyState("승인한 화면 공유 중");}
                    }
                }
                nextTick+=interval;
                if(clock.Elapsed-nextTick>interval)nextTick=clock.Elapsed;
                var wait=nextTick-clock.Elapsed;
                if(wait>TimeSpan.Zero)_lifetime.Token.WaitHandle.WaitOne(wait);
            }
            if (!_lifetime.IsCancellationRequested) EndSession("권한 유효기간 종료", false);
        }
        catch (Exception ex) { LocalDiagnostic.Failure("media-pipeline",ex); _log.LogError("미디어 파이프라인 실패: {Kind}",ex.GetType().Name); EndSession("미디어 파이프라인 실패", true); }
        finally{capture?.Dispose();ClearCopiedFrame(ref lastFrame);}
    }
    void PauseForFormatChange()
    {
        if(Interlocked.CompareExchange(ref _formatPaused,1,0)!=0)return;
        _inputData?.SuspendForApproval();NotifyState("화면 포맷 변경 · 대상자의 새 승인 대기");
        Enqueue(async ()=>
        {
            var identity=_monitor.DeviceName;var target=Monitors.Enumerate().SingleOrDefault(m=>m.DeviceName==identity);
            if(target.Handle==0||target==_monitor||RequestMonitorApproval==null||_localApproval.IsPending){EndSession("캡처 포맷 상실 또는 승인 준비 실패다.",true);return;}
            await ScreenCommand(new ControlMessage {T="monitor-select",MonitorIdentity=identity,Id=Guid.NewGuid().ToString("N")});
            if(Volatile.Read(ref _formatPaused)!=0)EndSession("바뀐 화면의 새 공유 승인을 받지 못했다.");
        });
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
            _localApproval.End();
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
        if(_files!=null)await _files.DisposeAsync();
        var pending = new[] { _eventTask, _receiveTask, _pairingTask, _negotiationTask,_inputData?.Completion,_chat?.Completion,_telemetry?.Completion,_diagnosticsTask,_refreshTask,_runbooks?.Completion }.OfType<Task>();
        await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(Math.Max(.1, 5 - clock.Elapsed.TotalSeconds)));
        _pc?.close(); _pc?.Dispose();
        _adminFrame?.Dispose();
        _callerCancellation.Dispose();
        _transportLife?.Dispose();
        _lifetime.Dispose();
    }
}
