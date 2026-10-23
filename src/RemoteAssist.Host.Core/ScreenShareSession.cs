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

/// <summary>
/// 지원 ID 방 하나에서 지원자 한 명을 받는 세션이다. 지원자와는 상호 신뢰 관계이므로
/// 지원자가 들어오면 바로 전 권한으로 연결한다. 협상 이벤트는 모두 한 큐에서 직렬로 처리한다.
/// </summary>
public sealed class ScreenShareSession : IAsyncDisposable
{
    public const string SharingState = "화면 공유·원격 제어 중";

    private readonly ScreenShareOptions _options;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly PermissionGate _permissions = new();
    private readonly Channel<Func<Task>> _events = Channel.CreateBounded<Func<Task>>(128);
    private readonly TaskCompletionSource<SessionOutcome> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _endGate = new();
    private readonly KeyframeRequests _keyframes = new();
    private readonly Queue<IceCandidate> _remoteIce = new();
    private SignalingClient? _signaling;
    private RTCPeerConnection? _pc;
    private Thread? _captureThread;
    private Task? _receiveTask, _eventTask, _pairingTask, _negotiationTask, _diagnosticsTask;
    private CancellationTokenRegistration _callerCancellation;
    private int _started, _ended, _disposed;
    private string _encoderName = "(미선택)";
    private sealed record MonitorBinding(MonitorInfo Value);
    private MonitorBinding _monitorBinding = new(default);
    private MonitorInfo _monitor { get => Volatile.Read(ref _monitorBinding).Value; set => Volatile.Write(ref _monitorBinding, new(value)); }
    private bool _answerApplied;
    private InputDataSession? _inputData;
    private ControlDataSession? _controlData;
    private AdminFrameSource? _adminFrame;
    private FeatureDataChannel? _chat, _telemetry;
    private FileTransferSession? _files;
    private RunbookDataSession? _runbooks;
    private AdaptiveQuality? _quality;
    private H264Contract? _h264;
    private long _topologyGeneration = 1;
    private MonitorSwitch? _switch;
    private CancellationTokenSource? _transportLife;
    private int _connectionGeneration = 1, _recoveryAttempts, _formatPaused;
    private long _lastRebuildAt;
    private volatile bool _mediaPaused;

    public ScreenShareSession(ScreenShareOptions options, ILoggerFactory? loggerFactory = null)
    {
        _options = options; _loggerFactory = loggerFactory;
        _log = (ILogger?)loggerFactory?.CreateLogger<ScreenShareSession>() ?? NullLogger.Instance;
    }

    public SessionCredentials? Credentials { get; private set; }
    public Task<SessionOutcome> Completion => _completion.Task;
    public string EncoderName => Volatile.Read(ref _encoderName);
    public MonitorInfo Monitor => _monitor;
    public int ConnectionGeneration => Volatile.Read(ref _connectionGeneration);
    public long TopologyGeneration => Interlocked.Read(ref _topologyGeneration);
    public Capability GrantedCapabilities => _permissions.Current?.Capabilities ?? Capability.None;
    public AdminFrameSource? AdminFrame => Volatile.Read(ref _adminFrame);
    public event Action<int, int>? FrameSent;
    public event Action<string>? StateChanged;

    // 헬퍼가 연결하는 기능 경로. 모두 연결돼 있어야 세션을 시작한다.
    public Func<PermissionSnapshot, IInputBackend>? CreateInputBackend { get; set; }
    public Action<SupporterIdentity>? SupporterJoined { get; set; }
    public Action<ControlMessage>? AdminCommand { get; set; }
    public Action<MonitorDescriptor>? AdminMonitorChanged { get; set; }
    public Action<FeatureMessage>? ChatReceived { get; set; }
    public Func<FeatureMessage, CancellationToken, Task<FeatureMessage>>? ClipboardOperation { get; set; }
    public IDiagnosticsProvider DiagnosticsProvider { get; set; } = new DiagnosticsCollector();
    public Func<FileMessage, CancellationToken, Task<string?>>? IncomingFileFolder { get; set; }
    public Func<FileAuditNotice, CancellationToken, Task>? AuditFile { get; set; }
    public Action<FileTransferState>? FileState { get; set; }
    public Func<RunbookMessage, CancellationToken, Task<RunbookResult>>? RunbookOperation { get; set; }

    public bool SendFile(string path, string id) => _files?.SendFile(path, id) == true;
    public void CancelFile(string id) => _files?.CancelFile(id);
    public void SendChat(string text, string id) => _chat?.Send(new() { T = "chat", Id = id, Text = text }, Capability.Chat);

    public async Task<SessionCredentials> StartAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _ended) != 0) throw new InvalidOperationException("종료한 세션은 시작할 수 없다.");
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("세션은 한 번만 시작할 수 있다.");
        if (CreateInputBackend is null || AdminCommand is null || ChatReceived is null || ClipboardOperation is null || IncomingFileFolder is null || AuditFile is null || RunbookOperation is null)
            throw new InvalidOperationException("헬퍼가 연결하지 않은 기능 경로가 있다.");
        try
        {
            var monitors = Monitors.Enumerate();
            _options.Validate(monitors.Count);
            _monitor = monitors[_options.MonitorIndex];
            _callerCancellation = ct.Register(() => EndSession("호출자 취소", false));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            Credentials = await SignalingClient.CreateSessionAsync(_options.SignalingUri, deadline.Token, _options.AllowLoopbackHttp, _options.SignalingCertificateBase64, _options.DeviceCode);
            _log.LogInformation("세션 생성: {SessionId}", Credentials.SessionId);
            _signaling = new SignalingClient(_options.SignalingUri, _loggerFactory?.CreateLogger<SignalingClient>(), _options.AllowLoopbackHttp, _options.SignalingCertificateBase64);
            await _signaling.ConnectAsync(Credentials, deadline.Token);
            _lifetime.Token.ThrowIfCancellationRequested();
            _eventTask = ProcessEventsAsync();
            _receiveTask = ReceiveLoopAsync();
            // 고정 지원 ID는 지원자를 무기한 기다린다. 임의 코드만 유효기간이 있다.
            if (_options.DeviceCode is null) _pairingTask = WatchPairingAsync(Credentials.ExpiresIn);
            NotifyState("지원자 접속 대기");
            return Credentials;
        }
        catch { EndSession("세션 시작 실패", true); throw; }
    }

    // ---- 시그널링 ---------------------------------------------------------

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
        catch (Exception ex)
        {
            LocalDiagnostic.Failure("session-transition", ex);
            _log.LogError("세션 상태 전이 실패: {Kind}", ex.GetType().Name);
            EndSession("협상 또는 상태 처리 실패", true);
        }
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
                if (msg.Generation != 0 && msg.Generation != ConnectionGeneration)
                {
                    if (msg.Generation < ConnectionGeneration) continue;
                    throw new InvalidDataException("잘못된 연결 세대다.");
                }
                if (msg.Type == "ping") { await _signaling.SendAsync(new SignalMsg { Type = "pong" }, _lifetime.Token); continue; }
                if (msg.Type == "pong") continue;
                // 지원자가 나가면 세션을 끝낸다. 서비스가 같은 지원 ID로 새 대기를 바로 연다.
                if (msg.Type is "peer-left" or "bye" or "error") { EndSession(msg.Code ?? "지원자 연결 종료", msg.Type == "error"); return; }
                Enqueue(() => HandleMessageAsync(msg));
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            LocalDiagnostic.Failure("signaling-receive", ex);
            _log.LogWarning("시그널링 수신 실패: {Kind}", ex.GetType().Name);
            EndSession("시그널링 수신 실패", true);
        }
    }

    private async Task HandleMessageAsync(SignalMsg msg)
    {
        if (msg.Generation != 0 && msg.Generation < ConnectionGeneration) return;
        switch (msg.Type)
        {
            case "peer-joined": await ConnectSupporterAsync(msg); break;
            case "answer":
                if (_pc is null || _answerApplied || string.IsNullOrEmpty(msg.Sdp)) throw new InvalidDataException("예상하지 않은 answer다.");
                var limits = H264Contract.FromAnswer(msg.Sdp); Volatile.Write(ref _h264, limits);
                var mode = _quality?.Current.Mode ?? "auto";
                _quality = new(Math.Min(_options.TargetFps, limits.Fps), Math.Min(_options.BitrateKbps, limits.Bitrate), Math.Min(_options.MaxOutputLongEdge, limits.LongEdge), Math.Min(_options.MaxOutputShortEdge, limits.ShortEdge));
                _quality.Mode(mode);
                if (_pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = msg.Sdp }) != SetDescriptionResultEnum.OK)
                    throw new InvalidDataException("answer 적용 실패다.");
                _answerApplied = true;
                while (_remoteIce.TryDequeue(out var candidate)) ApplyIce(candidate);
                break;
            case "ice" when msg.Ice is not null:
                if (msg.Ice.Candidate.Length > 8192 || msg.Ice.SdpMLineIndex is < 0 or > 65535) throw new InvalidDataException("잘못된 ICE다.");
                if (!_answerApplied)
                {
                    if (_remoteIce.Count >= 256) throw new InvalidDataException("ICE 대기 상한 초과다.");
                    _remoteIce.Enqueue(msg.Ice);
                }
                else ApplyIce(msg.Ice);
                break;
            default: throw new InvalidDataException("예상하지 않은 시그널링 메시지다.");
        }
    }

    private void ApplyIce(IceCandidate candidate) => _pc!.addIceCandidate(new RTCIceCandidateInit
    { candidate = candidate.Candidate, sdpMid = candidate.SdpMid, sdpMLineIndex = (ushort)(candidate.SdpMLineIndex ?? 0) });

    /// <summary>지원자가 들어오면 확인 절차 없이 전 권한으로 연결한다.</summary>
    private async Task ConnectSupporterAsync(SignalMsg peer)
    {
        if (_pc is not null) return;
        if (string.IsNullOrEmpty(peer.ConnectionId)) throw new InvalidDataException("지원자 연결 ID가 없다.");
        var grant = _permissions.Grant(Credentials!.SessionId, peer.ConnectionId);
        SupporterJoined?.Invoke(new SupporterIdentity(peer.ConnectionId, peer.DisplayName ?? "지원자"));
        await CreateTransport(grant);
    }

    private async Task WatchPairingAsync(int expiresIn)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(expiresIn, 1, 1800)), _lifetime.Token);
            if (_pc is null) EndSession("참가 코드 유효기간 종료", false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    // ---- WebRTC 전송 -------------------------------------------------------

    private async Task CreateTransport(PermissionSnapshot grant)
    {
        var generation = ConnectionGeneration;
        _transportLife = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _transportLife.Token;
        Action<string> fail = reason => { if (ConnectionGeneration == generation && !_lifetime.IsCancellationRequested) EndSession(reason, true); };
        // 두 PC는 지정 IP로 직접 연결한다. STUN/TURN 없이 host 후보만 쓴다.
        _pc = new RTCPeerConnection(new RTCConfiguration { iceServers = [], iceTransportPolicy = RTCIceTransportPolicy.all }, 0, new PortRange(27002, 27099));
        var pc = _pc;

        var control = new ControlDataSession(await pc.createDataChannel("control", new RTCDataChannelInit { ordered = true }), _permissions, grant, _monitor, fail,
            AdminControl, message => Enqueue(() => ConnectionGeneration == generation ? ScreenCommand(message) : Task.CompletedTask),
            generation, TopologyGeneration, () => ConnectionGeneration == generation && !token.IsCancellationRequested);
        _controlData = control; control.Attach();
        _quality ??= new(_options.TargetFps, _options.BitrateKbps, _options.MaxOutputLongEdge, _options.MaxOutputShortEdge);

        _inputData = new(new InputSession(_permissions, _monitor, TopologyGeneration, CreateInputBackend!(grant), generation), token, fail, control.ResetAcknowledged);
        _inputData.Attach(await pc.createDataChannel("input", new RTCDataChannelInit { ordered = true }));
        _inputData.Attach(await pc.createDataChannel("pointer", new RTCDataChannelInit { ordered = true }));
        _inputData.Start();

        var files = new FileDataTransport(await pc.createDataChannel("file", new RTCDataChannelInit { ordered = true }));
        _files = new(files, _permissions, grant, (offer, t) => IncomingFileFolder!(offer, t), AuditFile!, fail, value => FileState?.Invoke(value), token, generation);
        files.Attach(_files, fail); _files.Start();

        _chat = new(await pc.createDataChannel("chat", new RTCDataChannelInit { ordered = true }), _permissions, grant, token, HandleChatOrClipboard, fail, generation);
        _chat.Attach();
        _telemetry = new(await pc.createDataChannel("telemetry", new RTCDataChannelInit { ordered = true }), _permissions, grant, token, (_, _) => throw new InvalidDataException("Viewer가 telemetry를 보낼 수 없다."), fail, generation);
        _telemetry.Attach();
        _diagnosticsTask = ObserveDiagnostics(grant, token, generation);

        _runbooks = new(await pc.createDataChannel("runbook", new RTCDataChannelInit { ordered = true }), _permissions, grant, generation,
            async (request, t) =>
            {
                using var operation = CancellationTokenSource.CreateLinkedTokenSource(t, _lifetime.Token);
                operation.CancelAfter(TimeSpan.FromSeconds(55));
                return await RunbookOperation!(request, operation.Token).WaitAsync(operation.Token);
            }, fail, token);
        _runbooks.Attach();

        pc.ondatachannel += _ => fail("미지원 또는 중복 기능 채널이다.");
        var feedbackBinding = new RtcpFeedbackBinding();
        pc.OnReceiveReport += (_, media, report) =>
        {
            if (Volatile.Read(ref _ended) == 0 && ConnectionGeneration == generation)
            {
                feedbackBinding.Observe(media, report, pc.VideoLocalTrack, pc.VideoRemoteTrack);
                _keyframes.Observe(media, report);
            }
        };
        pc.addTrack(new MediaStreamTrack(new VideoFormat(VideoCodecsEnum.H264, 102, 90000, H264Contract.Offer), MediaStreamStatusEnum.SendOnly));
        pc.onicecandidate += candidate =>
        {
            if (candidate is null || ConnectionGeneration != generation) return;
            Enqueue(() => ConnectionGeneration != generation ? Task.CompletedTask : _signaling!.SendAsync(new SignalMsg
            {
                Type = "ice", Generation = generation, Room = Credentials!.Code,
                Ice = new IceCandidate { Candidate = IceCandidateWire.FromNative(candidate.candidate), SdpMid = candidate.sdpMid, SdpMLineIndex = candidate.sdpMLineIndex }
            }, _lifetime.Token));
        };
        var recovering = 0;
        pc.onconnectionstatechange += state =>
        {
            if (ConnectionGeneration != generation || _lifetime.IsCancellationRequested) return;
            if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.disconnected)
            {
                lock (_endGate) _mediaPaused = true;
                _inputData?.Suspend();
                if (Interlocked.Exchange(ref recovering, 1) == 0) _ = RecoverDisconnected(pc, generation);
            }
            else if (state == RTCPeerConnectionState.closed) fail("WebRTC 연결 종료다.");
            else if (state == RTCPeerConnectionState.connected)
                Enqueue(() =>
                {
                    if (ConnectionGeneration != generation || recovering != 0) return Task.CompletedTask;
                    _mediaPaused = false; _recoveryAttempts = 0; _keyframes.Request();
                    if (_captureThread is null) StartStreaming(grant.Generation);
                    else NotifyState(SharingState);
                    return Task.CompletedTask;
                });
        };
        // ICE 콜백은 이 작업 뒤에 큐에 들어가므로 offer가 후보보다 먼저 나간다.
        var offer = pc.createOffer(null);
        await pc.setLocalDescription(offer);
        _lifetime.Token.ThrowIfCancellationRequested();
        await _signaling!.SendAsync(new SignalMsg { Type = "offer", Generation = generation, Room = Credentials!.Code, Sdp = offer.sdp }, token);
        NotifyState("WebRTC 협상 중");
        _negotiationTask = WatchNegotiationAsync(pc, token, generation);
    }

    private async Task HandleChatOrClipboard(FeatureMessage message, CancellationToken token)
    {
        if (message.T == "chat") { ChatReceived!(message); return; }
        var reply = await ClipboardOperation!(message, token);
        if (reply.Id != message.Id || reply.T is not ("clipboard-text" or "clipboard-applied" or "clipboard-rejected")) throw new InvalidDataException("클립보드 응답 방향 오류다.");
        token.ThrowIfCancellationRequested();
        _chat!.Send(reply, Capability.Clipboard);
    }

    private async Task WatchNegotiationAsync(RTCPeerConnection pc, CancellationToken token, int generation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            if (ConnectionGeneration == generation && pc.connectionState != RTCPeerConnectionState.connected) EndSession("협상 시간 초과", true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task ObserveDiagnostics(PermissionSnapshot grant, CancellationToken token, int generation)
    {
        var telemetry = _telemetry!;
        try { await DiagnosticsLoop.Run(DiagnosticsProvider, _permissions, grant, () => telemetry.IsOpen, message => telemetry.Send(message, Capability.Diagnostics), token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { if (ConnectionGeneration == generation) EndSession("진단 수집이 실패하거나 기한을 넘었다.", true); }
    }

    /// <summary>2초 넘게 끊긴 전송은 같은 시그널링 위에서 새 offer로 다시 만든다(ICE 재시작).</summary>
    private async Task RecoverDisconnected(RTCPeerConnection pc, int generation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), _lifetime.Token);
            if (ConnectionGeneration == generation)
                Enqueue(async () => { if (_pc == pc && pc.connectionState != RTCPeerConnectionState.connected) await RestartTransport(); });
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private async Task RestartTransport()
    {
        if (_lastRebuildAt != 0)
        {
            var remaining = TimeSpan.FromSeconds(5) - Stopwatch.GetElapsedTime(_lastRebuildAt);
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, _lifetime.Token);
        }
        if (++_recoveryAttempts > 3) throw new InvalidOperationException("연결 복구 횟수를 넘었다.");
        lock (_endGate) _mediaPaused = true;
        _inputData?.Suspend();
        NotifyState("연결 복구 중 · 입력과 영상 송신 중단");
        _lastRebuildAt = Stopwatch.GetTimestamp();
        var grant = _permissions.Current ?? throw new InvalidOperationException("복구할 세션이 없다.");
        lock (_endGate) Interlocked.Increment(ref _connectionGeneration);
        if (ConnectionGeneration > 256) throw new InvalidOperationException("연결 세대 상한이다.");
        _transportLife!.Cancel(); _pc!.close();
        if (_files != null) { await _files.DisposeAsync(); _files = null; }
        await Task.WhenAll(new[] { _inputData?.Completion, _chat?.Completion, _telemetry?.Completion, _diagnosticsTask, _negotiationTask, _runbooks?.Completion }.OfType<Task>()).WaitAsync(TimeSpan.FromSeconds(3));
        _pc.Dispose(); _transportLife.Dispose();
        _pc = null; _inputData = null; _chat = _telemetry = null; _runbooks = null; _answerApplied = false; _remoteIce.Clear();
        await CreateTransport(grant);
    }

    // ---- 관리자 모드 -------------------------------------------------------

    private void AdminControl(ControlMessage message)
    {
        switch (message.T)
        {
            case "admin-request":
                if (AdminFrame is not null) throw new InvalidOperationException("이미 관리자 모드다.");
                _inputData!.Suspend();
                AdminCommand!(message);
                break;
            case "admin-sas":
                if (AdminFrame?.Approval.AdminGeneration != message.AdminGeneration) throw new InvalidOperationException("현재 관리자 세대가 아니다.");
                AdminCommand!(message);
                break;
            case "admin-revoke":
                if (AdminFrame?.Approval.AdminGeneration != message.AdminGeneration) throw new InvalidOperationException("현재 관리자 세대가 아니다.");
                EndSession("지원자가 지원 세션을 끝냈다.");
                break;
        }
    }

    /// <summary>관리자 헬퍼가 준비되면 화면·입력 경로를 관리자 헬퍼로 바꾼다. 일반·상승 창과 UAC 화면이 모두 보이고 조작된다.</summary>
    public void ActivateAdmin(AdminFrameSource source)
    {
        lock (_endGate)
        {
            var grant = _permissions.Current;
            if (Volatile.Read(ref _ended) != 0 || _adminFrame != null || grant is null || grant.SessionId != source.Approval.SessionId || grant.ConnectionId != source.Approval.ConnectionId || grant.Generation != source.Approval.PermissionGeneration)
            { source.Dispose(); throw new InvalidOperationException("현재 세션의 관리자 헬퍼가 아니다."); }
            Volatile.Write(ref _adminFrame, source); _keyframes.Request();
        }
        var epoch = _inputData!.Resume();
        _controlData!.AdminState(source.Approval.RequestId, "active", source.Approval.AdminGeneration, epoch);
        NotifyState(SharingState);
    }

    public void DenyAdmin(string requestId)
    {
        var epoch = _inputData!.Resume();
        _controlData!.AdminState(requestId, "denied", inputEpoch: epoch);
    }

    // ---- 화면 명령: 모니터·품질 ---------------------------------------------

    MonitorDescriptor Descriptor(MonitorInfo monitor) => new(monitor.DeviceName, TopologyGeneration, monitor.X, monitor.Y, monitor.Width, monitor.Height);

    async Task ScreenCommand(ControlMessage message)
    {
        switch (message.T)
        {
            case "monitor-list": _controlData!.Monitors(message.Id, Monitors.Enumerate().Take(16).Select(Descriptor).ToArray()); break;
            case "quality-mode": _quality!.Mode(message.Mode!); _controlData!.QualityChanged(message.Id, _quality.Current); break;
            case "quality-report": if (_quality!.Observe(message.Sample!)) _controlData!.QualityChanged(message.Id, _quality.Current); break;
            default:
                var target = Monitors.Enumerate().SingleOrDefault(m => m.DeviceName == message.MonitorIdentity);
                if (target.Handle == 0) throw new InvalidDataException("선택한 모니터가 없다.");
                await SwitchMonitor(target, message.Id);
                break;
        }
    }

    /// <summary>
    /// 공유 모니터를 바꾼다. 입력을 잠시 멈추고 좌표 기준을 새 모니터로 옮긴 뒤 새 inputEpoch와 함께 알린다.
    /// 관리자 모드에서는 관리자 헬퍼가 새 모니터를 캡처한다.
    /// </summary>
    async Task SwitchMonitor(MonitorInfo target, string id)
    {
        if (_captureThread?.IsAlive != true) return;
        _inputData!.Suspend();
        if (target != _monitor || Volatile.Read(ref _formatPaused) != 0)
        {
            if (AdminFrame is not null)
            {
                _monitor = target; Interlocked.Increment(ref _topologyGeneration);
                _inputData.RebindMonitor(_monitor, TopologyGeneration);
                AdminMonitorChanged?.Invoke(Descriptor(_monitor));
                Interlocked.Exchange(ref _formatPaused, 0); _keyframes.Request();
            }
            else
            {
                var request = new MonitorSwitch(target);
                if (Interlocked.CompareExchange(ref _switch, request, null) != null) throw new InvalidOperationException("중복 모니터 전환이다.");
                await request.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5), _lifetime.Token);
            }
        }
        _controlData!.MonitorChanged(id, Descriptor(_monitor), _inputData.Resume());
        NotifyState(SharingState);
    }

    sealed record MonitorSwitch(MonitorInfo Target)
    { public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); }

    /// <summary>해상도 변경·모니터 분리 때 같은 모니터(없으면 주 모니터)로 다시 맞춘다. 대상자 승인은 필요 없다.</summary>
    void FollowFormatChange()
    {
        if (Interlocked.CompareExchange(ref _formatPaused, 1, 0) != 0) return;
        _inputData?.Suspend();
        NotifyState("화면 구성 변경 · 다시 맞추는 중");
        Enqueue(async () =>
        {
            var monitors = Monitors.Enumerate();
            var target = monitors.FirstOrDefault(m => m.DeviceName == _monitor.DeviceName);
            if (target.Handle == 0) target = monitors.FirstOrDefault(m => m.IsPrimary);
            if (target.Handle == 0 && monitors.Count > 0) target = monitors[0];
            if (target.Handle == 0) { EndSession("공유할 모니터가 없다.", true); return; }
            await SwitchMonitor(target, Guid.NewGuid().ToString("N"));
        });
    }

    // ---- 캡처·인코딩·송신 ---------------------------------------------------

    private void StartStreaming(long generation)
    {
        _captureThread = new Thread(() => CaptureLoop(generation)) { Name = "RemoteAssist capture", IsBackground = true };
        _captureThread.Start();
    }

    private void CaptureLoop(long permissionGeneration)
    {
        IScreenCapture? capture = null;
        ReadOnlyMemory<byte> lastFrame = default;
        try
        {
            capture = ScreenCaptureFactory.Create(_monitor);
            _monitor = capture.Monitor;
            using var encoder = new FFmpegH264Encoder(_options.PreferredEncoder, _loggerFactory?.CreateLogger<FFmpegH264Encoder>()) { MaximumLevel = Volatile.Read(ref _h264)?.Level ?? throw new InvalidDataException("영상 협상 계약 누락이다.") };
            var quality = _quality!.Current;
            using var scaler = new BgraFrameScaler(quality.LongEdge, quality.ShortEdge);
            var width = 0; var height = 0; var sent = 0;
            var clock = Stopwatch.StartNew(); RTCPeerConnection? timingPeer = null; FrameRtpClock? rtpClock = null;
            var nextTick = TimeSpan.Zero; var interval = TimeSpan.FromSeconds(1d / quality.Fps); var topologyCheck = TimeSpan.Zero;
            var nextRecoveryKeyframe = TimeSpan.Zero;
            MonitorSwitch? changing = null;
            var waitingForDesktop = false;
            NotifyState(SharingState);
            while (!_lifetime.IsCancellationRequested && _permissions.Allows(Capability.Screen, permissionGeneration))
            {
                if (Interlocked.Exchange(ref _switch, null) is { } requested)
                {
                    capture?.Dispose(); capture = null; ClearCopiedFrame(ref lastFrame);
                    capture = ScreenCaptureFactory.Create(requested.Target);
                    _monitor = capture.Monitor; Interlocked.Increment(ref _topologyGeneration);
                    _inputData?.RebindMonitor(_monitor, TopologyGeneration);
                    width = height = 0; changing = requested; _keyframes.Request(); Interlocked.Exchange(ref _formatPaused, 0);
                }
                if (_mediaPaused || Volatile.Read(ref _formatPaused) != 0) { ClearCopiedFrame(ref lastFrame); _lifetime.Token.WaitHandle.WaitOne(20); continue; }
                var admin = AdminFrame;
                if (clock.Elapsed >= topologyCheck)
                {
                    topologyCheck = clock.Elapsed + TimeSpan.FromSeconds(1);
                    if (!Monitors.Enumerate().Contains(_monitor)) { FollowFormatChange(); ClearCopiedFrame(ref lastFrame); continue; }
                }
                var updated = _quality.Current;
                if (updated != quality || encoder.MaximumLevel != Volatile.Read(ref _h264)!.Level)
                {
                    quality = updated; encoder.MaximumLevel = Volatile.Read(ref _h264)!.Level; interval = TimeSpan.FromSeconds(1d / quality.Fps);
                    scaler.SetLimits(quality.LongEdge, quality.ShortEdge); ClearCopiedFrame(ref lastFrame); width = height = 0; _keyframes.Request();
                }
                if (admin != null)
                {
                    // 관리자 헬퍼가 기본·보안 데스크톱을 모두 캡처한다. 일반 캡처는 닫는다.
                    if (capture != null) { capture.Dispose(); capture = null; }
                    if (admin.TryRead(out var privileged))
                    {
                        ClearCopiedFrame(ref lastFrame);
                        var source = privileged!.Pixels;
                        try { lastFrame = scaler.Scale(source, privileged.Width, privileged.Height, privileged.Width * 4); }
                        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(source); }
                        if (width != scaler.Width || height != scaler.Height) Reconfigure(encoder, scaler, quality, ref width, ref height);
                        if (changing != null) { changing.Ready.TrySetResult(); changing = null; }
                    }
                }
                else if (!InputDesktop.IsDefault())
                {
                    // 관리자 모드가 아닌 동안의 UAC·잠금 화면은 일반 헬퍼가 볼 수 없다. 끊지 않고 마지막 화면을 유지한다.
                    if (!waitingForDesktop) { waitingForDesktop = true; NotifyState("보안 데스크톱 표시 중 · 관리자 모드에서 화면이 보인다"); }
                }
                else
                {
                    if (waitingForDesktop) { waitingForDesktop = false; NotifyState(SharingState); }
                    capture ??= ScreenCaptureFactory.Create(_monitor);
                    var available = capture.TryGetFrame(TimeSpan.FromMilliseconds(5), out var frame);
                    if (capture.Monitor.DeviceName != _monitor.DeviceName || capture.Monitor.Width != _monitor.Width || capture.Monitor.Height != _monitor.Height) { FollowFormatChange(); ClearCopiedFrame(ref lastFrame); continue; }
                    if (available)
                    {
                        if (frame.MonitorIdentity != _monitor.DeviceName || frame.Width != _monitor.Width || frame.Height != _monitor.Height) { FollowFormatChange(); continue; }
                        FrameLayout.Validate(frame.Bgra, frame.Width, frame.Height, frame.Stride);
                        ClearCopiedFrame(ref lastFrame);
                        lastFrame = scaler.Scale(frame.Bgra, frame.Width, frame.Height, frame.Stride);
                        if (scaler.Width != width || scaler.Height != height) Reconfigure(encoder, scaler, quality, ref width, ref height);
                    }
                }
                if (!lastFrame.IsEmpty)
                {
                    var periodic = clock.Elapsed >= nextRecoveryKeyframe;
                    if (periodic) nextRecoveryKeyframe = clock.Elapsed + TimeSpan.FromSeconds(2);
                    var encoded = encoder.Encode(lastFrame, _keyframes.Consume() || periodic);
                    if (!encoded.IsEmpty)
                    {
                        // 종료와 마지막 송신을 직렬화한다. 다른 스레드에서 살아 있는 인코더를 해제하지 않는다.
                        lock (_endGate)
                        {
                            if (Volatile.Read(ref _ended) != 0 || !_permissions.Allows(Capability.Screen, permissionGeneration)) break;
                            if (_mediaPaused) continue;
                            if (timingPeer != _pc) { timingPeer = _pc; rtpClock = new FrameRtpClock(_pc!.VideoLocalTrack.Timestamp); }
                            var timestamp = rtpClock!.Timestamp;
                            // SendVideo는 현재 프레임 뒤에 내부 timestamp를 올린다. raw RTP API로 실제 샘플 시간을 쓴다.
                            foreach (var packet in H264RtpPayloads.Packetize(encoded.ToArray())) _pc!.SendRtpRaw(SDPMediaTypesEnum.video, packet.Bytes, timestamp, packet.Marker, 102);
                        }
                        FrameSent?.Invoke(++sent, encoded.Length);
                        if (changing != null) { changing.Ready.TrySetResult(); changing = null; }
                    }
                }
                nextTick += interval;
                if (clock.Elapsed - nextTick > interval) nextTick = clock.Elapsed;
                var wait = nextTick - clock.Elapsed;
                if (wait > TimeSpan.Zero) _lifetime.Token.WaitHandle.WaitOne(wait);
            }
            if (!_lifetime.IsCancellationRequested) EndSession("세션 종료", false);
        }
        catch (Exception ex) { LocalDiagnostic.Failure("media-pipeline", ex); _log.LogError("미디어 파이프라인 실패: {Kind}", ex.GetType().Name); EndSession("미디어 파이프라인 실패", true); }
        finally { capture?.Dispose(); ClearCopiedFrame(ref lastFrame); }
    }

    void Reconfigure(FFmpegH264Encoder encoder, BgraFrameScaler scaler, QualitySetting quality, ref int width, ref int height)
    {
        width = scaler.Width; height = scaler.Height;
        encoder.Configure(width, height, quality.Fps, quality.BitrateKbps);
        Volatile.Write(ref _encoderName, encoder.EncoderName);
        _keyframes.Request();
    }

    static void ClearCopiedFrame(ref ReadOnlyMemory<byte> frame)
    {
        if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(frame, out var bytes) && bytes.Array != null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes.AsSpan());
        frame = default;
    }

    // ---- 종료 --------------------------------------------------------------

    public void EndSession(string reason = "로컬 종료", bool failed = false)
    {
        lock (_endGate)
        {
            if (Interlocked.Exchange(ref _ended, 1) != 0) return;
            _permissions.End();
            try { _lifetime.Cancel(); } catch (AggregateException) { _log.LogWarning("취소 관찰자 오류다. 기능 권한은 이미 폐기했다."); }
            _events.Writer.TryComplete();
            _completion.TrySetResult(new SessionOutcome(reason, failed));
        }
        NotifyState(reason);
    }

    private void NotifyState(string state)
    {
        foreach (var observer in StateChanged?.GetInvocationList() ?? [])
            try { ((Action<string>)observer)(state); } catch (Exception) { _log.LogWarning("상태 관찰자 오류다."); }
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
        if (_files != null) await _files.DisposeAsync();
        var pending = new[] { _eventTask, _receiveTask, _pairingTask, _negotiationTask, _inputData?.Completion, _chat?.Completion, _telemetry?.Completion, _diagnosticsTask, _runbooks?.Completion }.OfType<Task>();
        await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(Math.Max(.1, 5 - clock.Elapsed.TotalSeconds)));
        _pc?.close(); _pc?.Dispose();
        _adminFrame?.Dispose();
        _callerCancellation.Dispose();
        _transportLife?.Dispose();
        _lifetime.Dispose();
    }
}
