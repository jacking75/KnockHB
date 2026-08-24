using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteAssist.Host.Core.Capture;
using RemoteAssist.Host.Core.Media;
using RemoteAssist.Host.Core.Signaling;
using RemoteAssist.Protocol;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace RemoteAssist.Host.Core;

/// <summary>화면 공유 세션 설정.</summary>
public sealed record ScreenShareOptions
{
    public Uri SignalingUri { get; init; } = new("http://localhost:25001");
    public int MonitorIndex { get; init; }
    public int TargetFps { get; init; } = 30;
    public int BitrateKbps { get; init; } = 4000;

    /// <summary>특정 H.264 인코더 강제. null이면 자동 선택.</summary>
    public string? PreferredEncoder { get; init; }

    /// <summary>STUN 서버. Phase 4에서 자체 coturn으로 바꾼다.</summary>
    public string[] StunServers { get; init; } = ["stun:stun.l.google.com:19302"];
}

/// <summary>
/// 캡처 → 인코딩 → WebRTC 송신을 잇는 Host 쪽 세션 (T1.4).
///
/// <para>
/// 아직 <b>동의 게이트가 없다.</b> Phase 3(T3.2)에서 붙기 전까지 이 클래스는
/// 시작하자마자 화면을 내보낸다. 개발·검증 용도로만 쓴다.
/// </para>
/// </summary>
public sealed class ScreenShareSession : IAsyncDisposable
{
    private readonly ScreenShareOptions _options;
    private readonly ILogger _log;
    private readonly ILoggerFactory? _loggerFactory;

    private SignalingClient? _signaling;
    private RTCPeerConnection? _pc;
    private IScreenCapture? _capture;
    private FFmpegH264Encoder? _encoder;

    private CancellationTokenSource? _cts;
    private Task? _signalingLoop;
    private Thread? _captureThread;

    public SessionCredentials? Credentials { get; private set; }
    public string EncoderName => _encoder?.EncoderName ?? "(미선택)";
    public MonitorInfo Monitor => _capture?.Monitor ?? default;

    /// <summary>프레임을 하나 보낼 때마다 호출된다. 상태 표시·통계용.</summary>
    public event Action<int, int>? FrameSent; // (전송 프레임 수, 마지막 프레임 바이트)

    public ScreenShareSession(ScreenShareOptions options, ILoggerFactory? loggerFactory = null)
    {
        _options = options;
        _loggerFactory = loggerFactory;
        _log = (ILogger?)loggerFactory?.CreateLogger<ScreenShareSession>() ?? NullLogger.Instance;
    }

    /// <summary>세션 코드를 발급받고 시그널링에 붙는다. 아직 화면은 나가지 않는다.</summary>
    public async Task<SessionCredentials> StartAsync(CancellationToken ct = default)
    {
        Credentials = await SignalingClient.CreateSessionAsync(_options.SignalingUri, ct);
        _log.LogInformation("세션 코드 {Code} / PIN {Pin}", Credentials.DisplayCode, Credentials.Pin);

        _signaling = new SignalingClient(_options.SignalingUri, _loggerFactory?.CreateLogger<SignalingClient>());
        var peerPresent = await _signaling.ConnectAsync(Credentials, ct);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _signalingLoop = Task.Run(() => RunSignalingLoopAsync(_cts.Token), _cts.Token);

        if (peerPresent)
        {
            // 이미 붙어 있었다면 바로 협상을 건다.
            await NegotiateAsync(_cts.Token);
        }

        return Credentials;
    }

    /// <summary>시그널링 메시지를 계속 처리한다.</summary>
    private async Task RunSignalingLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var msg = await _signaling!.ReceiveAsync(ct);
                if (msg is null) break;

                switch (msg.Type)
                {
                    case "peer-joined":
                        _log.LogInformation("지원자 접속 — 협상 시작");
                        await NegotiateAsync(ct);
                        break;

                    case "answer" when msg.Sdp is not null:
                        HandleAnswer(msg.Sdp);
                        break;

                    case "ice" when msg.Ice is not null:
                        _pc?.addIceCandidate(new RTCIceCandidateInit
                        {
                            candidate = msg.Ice.Candidate,
                            sdpMid = msg.Ice.SdpMid,
                            sdpMLineIndex = (ushort)(msg.Ice.SdpMLineIndex ?? 0),
                        });
                        break;

                    case "peer-left":
                    case "bye":
                        _log.LogInformation("지원자 연결 종료");
                        StopStreaming();
                        break;

                    case "error":
                        _log.LogError("시그널링 오류: {Code} - {Reason}", msg.Code, msg.Reason);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 정상 종료
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "시그널링 루프 오류");
        }
    }

    private async Task NegotiateAsync(CancellationToken ct)
    {
        if (_pc is not null)
        {
            _log.LogWarning("이미 협상된 연결이 있다. 무시한다.");
            return;
        }

        _pc = CreatePeerConnection();

        var offer = _pc.createOffer(null);
        await _pc.setLocalDescription(offer);

        await _signaling!.SendAsync(new SignalMsg
        {
            Type = "offer",
            Room = Credentials!.Code,
            Sdp = offer.sdp,
        }, ct);

        _log.LogInformation("offer 전송");
    }

    private RTCPeerConnection CreatePeerConnection()
    {
        var config = new RTCConfiguration
        {
            iceServers = [.. _options.StunServers.Select(u => new RTCIceServer { urls = u })],
        };

        var pc = new RTCPeerConnection(config);

        // H.264 단일 포맷. packetization-mode=1 은 브라우저 호환에 필요하다.
        var format = new VideoFormat(VideoCodecsEnum.H264, 102, 90000, "packetization-mode=1");
        pc.addTrack(new MediaStreamTrack(format, MediaStreamStatusEnum.SendOnly));

        pc.onicecandidate += candidate =>
        {
            if (candidate is null || _cts is null || _cts.IsCancellationRequested) return;

            _ = _signaling!.SendAsync(new SignalMsg
            {
                Type = "ice",
                Room = Credentials!.Code,
                Ice = new Protocol.IceCandidate
                {
                    Candidate = candidate.candidate,
                    SdpMid = candidate.sdpMid,
                    SdpMLineIndex = candidate.sdpMLineIndex,
                },
            }, _cts.Token);
        };

        pc.onconnectionstatechange += state =>
        {
            _log.LogInformation("연결 상태: {State}", state);

            if (state == RTCPeerConnectionState.connected)
            {
                StartStreaming();
            }
            else if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed
                     or RTCPeerConnectionState.disconnected)
            {
                StopStreaming();
            }
        };

        return pc;
    }

    private void HandleAnswer(string sdp)
    {
        var result = _pc!.setRemoteDescription(new RTCSessionDescriptionInit
        {
            type = RTCSdpType.answer,
            sdp = sdp,
        });

        if (result != SetDescriptionResultEnum.OK)
        {
            _log.LogError("answer 적용 실패: {Result}", result);
            return;
        }

        _log.LogInformation("answer 적용 완료");
    }

    private void StartStreaming()
    {
        if (_captureThread is not null) return;

        _captureThread = new Thread(CaptureLoop)
        {
            Name = "RemoteAssist 캡처",
            IsBackground = true,
        };
        _captureThread.Start();
    }

    private void StopStreaming()
    {
        _cts?.Cancel();
    }

    /// <summary>
    /// 캡처 → 인코딩 → 송신 루프. 캡처와 인코더는 스레드 안전하지 않으므로
    /// 반드시 이 스레드 하나에서만 만들고 쓴다.
    /// </summary>
    private void CaptureLoop()
    {
        var ct = _cts!.Token;
        var interval = TimeSpan.FromMilliseconds(1000.0 / _options.TargetFps);
        var durationRtpUnits = (uint)(90000 / _options.TargetFps);

        try
        {
            _capture = WgcScreenCapture.IsSupported()
                ? new WgcScreenCapture(_options.MonitorIndex, _loggerFactory?.CreateLogger<WgcScreenCapture>())
                : new DxgiScreenCapture(_options.MonitorIndex, _loggerFactory?.CreateLogger<DxgiScreenCapture>());

            _encoder = new FFmpegH264Encoder(_options.PreferredEncoder, _loggerFactory?.CreateLogger<FFmpegH264Encoder>());
            _encoder.Configure(_capture.Monitor.Width, _capture.Monitor.Height, _options.TargetFps, _options.BitrateKbps);

            _log.LogInformation("스트리밍 시작: {W}x{H} @{Fps}fps, 인코더 {Encoder}",
                _capture.Monitor.Width, _capture.Monitor.Height, _options.TargetFps, _encoder.EncoderName);

            var sent = 0;
            ReadOnlyMemory<byte> lastFrame = default;

            while (!ct.IsCancellationRequested)
            {
                var tick = Environment.TickCount64;

                // 새 프레임이 없으면(정적 화면) 직전 프레임을 그대로 다시 인코딩한다.
                // 캡처 버퍼는 재사용되지만 새 프레임이 없으면 내용이 그대로라 안전하다.
                if (_capture.TryGetFrame(TimeSpan.FromMilliseconds(5), out var frame))
                {
                    lastFrame = frame.Bgra;
                }

                if (!lastFrame.IsEmpty)
                {
                    var encoded = _encoder.Encode(lastFrame, forceKeyframe: false);
                    if (!encoded.IsEmpty)
                    {
                        _pc?.SendVideo(durationRtpUnits, encoded.ToArray());
                        sent++;
                        FrameSent?.Invoke(sent, encoded.Length);
                    }
                }

                var elapsed = Environment.TickCount64 - tick;
                var wait = (int)(interval.TotalMilliseconds - elapsed);
                if (wait > 0) Thread.Sleep(wait);
            }

            _log.LogInformation("스트리밍 종료: {Sent}프레임 전송", sent);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "캡처 루프 오류");
        }
        finally
        {
            _encoder?.Dispose();
            _capture?.Dispose();
            _encoder = null;
            _capture = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();

        if (_captureThread is not null && _captureThread.IsAlive)
        {
            _captureThread.Join(TimeSpan.FromSeconds(3));
        }

        if (_signalingLoop is not null)
        {
            try { await _signalingLoop.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch { /* 종료 중 예외는 무시 */ }
        }

        _pc?.close();
        if (_signaling is not null) await _signaling.DisposeAsync();
        _cts?.Dispose();
    }
}
