using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using RemoteAssist.Host.Core;
using RemoteAssist.Host.Core.Capture;
using RemoteAssist.Host.Core.Input;
using RemoteAssist.Host.Core.Media;
using RemoteAssist.Host.Core.Tunnel;
using RemoteAssist.Protocol;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

// 사용: RemoteAssist.LoopbackCheck --tunnel http://127.0.0.1:25080 [--out <결과 JSON>]
// scripts/loopback-check.ps1이 터널링 서버를 띄우고 이 도구를 실행한다.
SetProcessDpiAwarenessContext(-4);
var tunnel = new Uri(Arg("--tunnel") ?? "http://127.0.0.1:25080");
var output = Arg("--out");
var checks = new List<object>();
var measurements = new Dictionary<string, double>();
var started = Stopwatch.StartNew();
var failed = false;
const string DeviceCode = "987654321";

void Record(string name, bool pass, string detail = "")
{
    checks.Add(new { name, status = pass ? "PASS" : "FAIL", detail });
    Console.WriteLine($"{(pass ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " · " + detail : "")}");
    if (!pass) failed = true;
}

try
{
    // 1. Host 세션: 실제 ScreenShareSession. 입력·관리자·Runbook은 기록만 하는 가짜 경로다.
    var host = new FakeHost(tunnel, DeviceCode);
    await host.Start();
    Record("host opens fixed support ID room", host.Credentials?.Code == DeviceCode, host.Credentials?.Code ?? "");

    // 2. Viewer 접속과 WebRTC 연결
    await using var viewer = new FakeViewer(tunnel, "loopback viewer");
    var joinClock = Stopwatch.StartNew();
    var joined = await viewer.Join(DeviceCode);
    Record("viewer joins with the support ID only (no PIN)", joined.Type == "joined" && joined.PeerPresent == true, joined.Type);
    var supporter = await host.Supporter.Task.WaitAsync(TimeSpan.FromSeconds(10));
    Record("host grants the supporter without local approval", supporter.DisplayName == "loopback viewer", supporter.DisplayName);
    await viewer.Connected.Task.WaitAsync(TimeSpan.FromSeconds(30));
    measurements["joinToConnectedMs"] = joinClock.Elapsed.TotalMilliseconds;
    Record("direct P2P transport connects without STUN/TURN", true, $"{joinClock.ElapsedMilliseconds}ms");

    // 3. 기능 채널과 data-ready
    var ready = await viewer.Next("control", m => m["t"]?.GetValue<string>() == "data-ready", TimeSpan.FromSeconds(15));
    var permissions = ready["permissions"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
    var channels = ready["channels"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
    Record("data-ready carries every permission", permissions.SequenceEqual(FeatureContract.Permissions), string.Join(",", permissions));
    Record("all seven feature channels opened", channels.SequenceEqual(FeatureContract.Channels) && FeatureContract.Channels.All(viewer.HasChannel), string.Join(",", viewer.ChannelLabels));
    measurements["joinToDataReadyMs"] = joinClock.Elapsed.TotalMilliseconds;
    viewer.SessionId = ready["sessionId"]!.GetValue<string>();
    var monitor = ready["monitor"]!;
    var monitorId = monitor["identity"]!.GetValue<string>();
    var topology = monitor["topologyGeneration"]!.GetValue<long>();

    viewer.SendControl("data-ping", ready["id"]!.GetValue<string>());
    var pong = await viewer.Next("control", m => m["t"]?.GetValue<string>() == "data-pong", TimeSpan.FromSeconds(5));
    Record("control liveness ping/pong", pong["id"]!.GetValue<string>() == ready["id"]!.GetValue<string>());

    // 4. 입력: 기록 backend에 도착해야 한다(실제 SendInput 없음).
    long epoch = 1;
    viewer.SendInput(new() { ["t"] = "key", ["a"] = "down", ["vk"] = 65 }, epoch, monitorId, topology);
    viewer.SendInput(new() { ["t"] = "key", ["a"] = "up", ["vk"] = 65 }, epoch, monitorId, topology);
    var typed = await host.Input.WaitFor(s => s.Kind == StrokeKind.Key && s.Key == 65 && s.Up, TimeSpan.FromSeconds(5));
    Record("remote key input reaches the host input path", typed);

    // 5. 관리자 모드 요청: 서비스 밖이라 관리자 헬퍼가 없으므로 denied와 새 epoch로 일반 모드를 계속한다.
    var adminId = Guid.NewGuid().ToString("N");
    viewer.SendControl("admin-request", adminId);
    var pending = await viewer.Next("control", m => m["t"]?.GetValue<string>() == "admin-state" && m["state"]?.GetValue<string>() == "pending", TimeSpan.FromSeconds(5));
    var denied = await viewer.Next("control", m => m["t"]?.GetValue<string>() == "admin-state" && m["state"]?.GetValue<string>() == "denied", TimeSpan.FromSeconds(10));
    epoch = denied["inputEpoch"]!.GetValue<long>();
    Record("admin request answers without a local approval window", pending["id"]!.GetValue<string>() == adminId && epoch == 2, $"inputEpoch={epoch}");
    viewer.SendInput(new() { ["t"] = "key", ["a"] = "down", ["vk"] = 66 }, epoch, monitorId, topology);
    viewer.SendInput(new() { ["t"] = "key", ["a"] = "up", ["vk"] = 66 }, epoch, monitorId, topology);
    Record("input resumes at the new epoch after the admin attempt", await host.Input.WaitFor(s => s.Kind == StrokeKind.Key && s.Key == 66 && s.Up, TimeSpan.FromSeconds(5)));

    // 6. 영상: 실제 캡처·H.264 인코딩·RTP가 도착해야 한다.
    var videoClock = Stopwatch.StartNew();
    while (viewer.VideoPackets == 0 && videoClock.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(50);
    Record("captured H.264 video arrives over RTP", viewer.VideoPackets > 0, $"{viewer.VideoPackets} packets, encoder {host.Session!.EncoderName}");
    var before = viewer.VideoPackets; await Task.Delay(5000);
    measurements["videoPacketsPerSecond"] = (viewer.VideoPackets - before) / 5.0;
    measurements["videoBytesPerSecond"] = viewer.TakeVideoBytes() / 5.0;

    // 7. 모니터 목록과 전환(모니터가 둘 이상이면 다른 모니터로 실제 전환)
    viewer.SendControl("monitor-list", Guid.NewGuid().ToString("N"));
    var list = await viewer.Next("control", m => m["t"]?.GetValue<string>() == "monitor-list", TimeSpan.FromSeconds(5));
    var monitors = list["monitors"]!.AsArray().Select(m => m!["identity"]!.GetValue<string>()).ToArray();
    Record("monitor list is available", monitors.Length >= 1, string.Join(",", monitors));
    var target = monitors.FirstOrDefault(m => m != monitorId) ?? monitorId;
    viewer.SendControl("monitor-select", Guid.NewGuid().ToString("N"), new() { ["monitorIdentity"] = target });
    var switched = await viewer.Next("control", m => m["t"]?.GetValue<string>() == "monitor-state", TimeSpan.FromSeconds(10));
    var newEpoch = switched["inputEpoch"]!.GetValue<long>();
    var newTopology = switched["monitor"]!["topologyGeneration"]!.GetValue<long>();
    Record("monitor switch announces the new input epoch", switched["monitor"]!["identity"]!.GetValue<string>() == target && newEpoch == epoch + 1,
        $"{target} topology={newTopology} inputEpoch={newEpoch}{(target == monitorId ? " (모니터 1개: 같은 모니터 재선택)" : "")}");
    epoch = newEpoch; monitorId = target; topology = newTopology;
    viewer.SendInput(new() { ["t"] = "key", ["a"] = "down", ["vk"] = 67 }, epoch, monitorId, topology);
    viewer.SendInput(new() { ["t"] = "key", ["a"] = "up", ["vk"] = 67 }, epoch, monitorId, topology);
    Record("input works on the switched monitor", await host.Input.WaitFor(s => s.Kind == StrokeKind.Key && s.Key == 67 && s.Up, TimeSpan.FromSeconds(5)));

    // 8. 원격 명령(Runbook command) 왕복
    await viewer.Next("runbook", m => m["t"]?.GetValue<string>() == "runbook-catalog", TimeSpan.FromSeconds(5));
    var script = "Get-Date";
    var bookId = Guid.NewGuid().ToString("N");
    viewer.Send("runbook", new JsonObject { ["t"] = "runbook-request", ["id"] = bookId, ["bookId"] = "command", ["bookVersion"] = 1, ["hash"] = RunbookMessage.ScriptHash(script),
        ["args"] = new JsonObject { ["script"] = script, ["admin"] = "false" } });
    var result = await viewer.Next("runbook", m => m["t"]?.GetValue<string>() == "runbook-result", TimeSpan.FromSeconds(10));
    Record("remote command request reaches the host and returns a result", result["id"]!.GetValue<string>() == bookId && result["result"]!["stdout"]!.GetValue<string>() == "loopback:" + script);

    // 9. 채팅
    viewer.Send("chat", new JsonObject { ["t"] = "chat", ["id"] = Guid.NewGuid().ToString("N"), ["text"] = "안녕하세요" });
    var chatClock = Stopwatch.StartNew();
    while (host.Chats.IsEmpty && chatClock.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
    Record("chat reaches the host", host.Chats.Contains("안녕하세요"));

    // 10. 교체 접속: 새 Viewer가 이어받고 기존 Viewer는 replaced를 받는다. Host 세션은 끝난다.
    await using (var second = new FakeViewer(tunnel, "second viewer"))
    {
        var refused = await second.Join(DeviceCode);
        Record("second viewer is told to retry while the room is handed over", refused.Type == "error" && refused.Code == "busy", refused.Code ?? refused.Type);
    }
    var kicked = await viewer.SignalError.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Record("replaced viewer is told not to retry", kicked == "replaced", kicked);
    var outcome = await host.Session!.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    Record("host session ends when the supporter leaves", true, outcome.Reason);
    var released = host.Input.ReleasedAllHeld();
    Record("no remote key is left held after the session ends", released);
    await host.Stop();

    // 11. 재대기와 재접속: 같은 지원 ID로 새 세션을 열고 다시 붙는다.
    var again = new FakeHost(tunnel, DeviceCode);
    var reopen = Stopwatch.StartNew();
    await again.Start();
    await using (var back = new FakeViewer(tunnel, "returning viewer"))
    {
        var rejoined = await back.Join(DeviceCode);
        await back.Connected.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await back.Next("control", m => m["t"]?.GetValue<string>() == "data-ready", TimeSpan.FromSeconds(15));
        measurements["reopenToDataReadyMs"] = reopen.Elapsed.TotalMilliseconds;
        Record("the same support ID accepts a reconnect after the host reopens", rejoined.Type == "joined", $"{reopen.ElapsedMilliseconds}ms");
    }
    await again.Session!.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    await again.Stop();
}
catch (Exception ex)
{
    Record("loopback run completed", false, ex.GetType().Name + ": " + ex.Message);
}

var summary = new { utc = DateTimeOffset.UtcNow, machine = Environment.MachineName, seconds = started.Elapsed.TotalSeconds, status = failed ? "FAIL" : "PASS", checks, measurements };
var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
if (output is not null) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!); File.WriteAllText(output, json); }
Console.WriteLine(json);
return failed ? 1 : 0;

string? Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
[DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(nint value);

/// <summary>실제 ScreenShareSession. 서비스·Tray 대신 기록용 callback을 연결한다.</summary>
sealed class FakeHost(Uri tunnel, string code)
{
    public ScreenShareSession? Session { get; private set; }
    public SessionCredentials? Credentials { get; private set; }
    public RecordingInput Input { get; } = new();
    public ConcurrentBag<string> Chats { get; } = new();
    public TaskCompletionSource<RemoteAssist.Host.Core.Security.SupporterIdentity> Supporter { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task Start()
    {
        var session = new ScreenShareSession(new ScreenShareOptions { TunnelUri = tunnel, AllowLoopbackHttp = true, DeviceCode = code, TargetFps = 15, BitrateKbps = 2000 });
        Session = session;
        session.CreateInputBackend = _ => Input;
        session.SupporterJoined = s => Supporter.TrySetResult(s);
        session.AdminCommand = m => { if (m.T == "admin-request") _ = Task.Run(() => session.DenyAdmin(m.Id)); };
        session.ChatReceived = m => Chats.Add(m.Text!);
        session.ClipboardOperation = (m, _) => Task.FromResult(m with { T = "clipboard-rejected", Text = null, Hash = null, Code = "unavailable" });
        session.IncomingFileFolder = (_, _) => Task.FromResult<string?>(Path.GetTempPath());
        session.AuditFile = (_, _) => Task.CompletedTask;
        session.RunbookOperation = (r, _) => Task.FromResult(new RunbookResult("completed", 0, "loopback:" + r.Args?["script"], "", ""));
        session.StateChanged += s => Console.WriteLine("  host: " + s);
        Credentials = await session.StartAsync();
    }
    public async Task Stop() { if (Session is not null) await Session.DisposeAsync(); }
}

/// <summary>입력을 주입하지 않고 기록만 한다.</summary>
sealed class RecordingInput : IInputBackend
{
    readonly ConcurrentQueue<InputStroke> strokes = new();
    public int Send(IReadOnlyList<InputStroke> batch) { foreach (var s in batch) strokes.Enqueue(s); return batch.Count; }
    public async Task<bool> WaitFor(Func<InputStroke, bool> match, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout) { if (strokes.Any(match)) return true; await Task.Delay(20); }
        return false;
    }
    /// <summary>눌렸다가 떼지 않은 키·버튼이 없는지.</summary>
    public bool ReleasedAllHeld()
    {
        var held = new HashSet<string>();
        foreach (var s in strokes)
        {
            if (s.Kind is not (StrokeKind.Key or StrokeKind.Button or StrokeKind.Unicode)) continue;
            var key = s.Kind + ":" + s.Key + s.Button + s.Character;
            if (s.Up) held.Remove(key); else held.Add(key);
        }
        return held.Count == 0;
    }
}

/// <summary>브라우저 Viewer의 와이어 동작을 따라 하는 SIPSorcery 기반 가짜 Viewer.</summary>
sealed class FakeViewer : IAsyncDisposable
{
    static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    readonly Uri tunnel;
    readonly string name;
    readonly ClientWebSocket socket = new();
    readonly CancellationTokenSource stop = new();
    readonly ConcurrentDictionary<string, RTCDataChannel> channels = new();
    readonly ConcurrentDictionary<string, long> sent = new();
    readonly ConcurrentQueue<(string Label, JsonNode Message)> received = new();
    readonly SemaphoreSlim sendGate = new(1, 1);
    readonly TaskCompletionSource<SignalMsg> joined = new(TaskCreationOptions.RunContinuationsAsynchronously);
    RTCPeerConnection? pc;
    Task? loop;
    int generation = 1;
    long videoPackets, videoBytes;
    public string SessionId { get; set; } = "";
    public TaskCompletionSource Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<string> SignalError { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public long VideoPackets => Interlocked.Read(ref videoPackets);
    public long TakeVideoBytes() => Interlocked.Exchange(ref videoBytes, 0);
    public bool HasChannel(string label) => channels.ContainsKey(label);
    public IEnumerable<string> ChannelLabels => channels.Keys.OrderBy(x => x);
    public FakeViewer(Uri tunnel, string name) { this.tunnel = tunnel; this.name = name; }

    public async Task<SignalMsg> Join(string code)
    {
        await socket.ConnectAsync(new Uri((tunnel.Scheme == "https" ? "wss://" : "ws://") + tunnel.Authority + "/ws"), stop.Token);
        loop = Receive();
        await Signal(new SignalMsg { Type = "join", Room = code, Role = "viewer", DisplayName = name });
        return await joined.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    async Task Signal(SignalMsg message)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        await sendGate.WaitAsync(stop.Token);
        try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, stop.Token); }
        finally { sendGate.Release(); }
    }

    async Task Receive()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var message = await TunnelFraming.ReceiveAsync(socket, stop.Token);
                if (message is null) return;
                switch (message.Type)
                {
                    case "joined": joined.TrySetResult(message); break;
                    case "error": joined.TrySetResult(message); SignalError.TrySetResult(message.Code ?? "error"); return;
                    case "ping": await Signal(new SignalMsg { Type = "pong" }); break;
                    case "offer": await Answer(message); break;
                    case "ice":
                        pc?.addIceCandidate(new RTCIceCandidateInit { candidate = message.Ice!.Candidate, sdpMid = message.Ice.SdpMid, sdpMLineIndex = (ushort)(message.Ice.SdpMLineIndex ?? 0) });
                        break;
                }
            }
        }
        catch (Exception) when (stop.IsCancellationRequested) { }
        catch (WebSocketException) { }
    }

    async Task Answer(SignalMsg offer)
    {
        generation = offer.Generation == 0 ? 1 : offer.Generation;
        pc = new RTCPeerConnection(new RTCConfiguration { iceServers = [] });
        pc.addTrack(new MediaStreamTrack(new VideoFormat(VideoCodecsEnum.H264, 102, 90000, H264Contract.Offer), MediaStreamStatusEnum.RecvOnly));
        pc.OnRtpPacketReceived += (_, media, packet) =>
        {
            if (media != SDPMediaTypesEnum.video) return;
            Interlocked.Increment(ref videoPackets); Interlocked.Add(ref videoBytes, packet.Payload.Length);
        };
        pc.onconnectionstatechange += state => { if (state == RTCPeerConnectionState.connected) Connected.TrySetResult(); };
        pc.ondatachannel += channel =>
        {
            channels[channel.label] = channel;
            channel.onmessage += (_, kind, data) =>
            {
                if (kind != DataChannelPayloadProtocols.WebRTC_String) return;
                received.Enqueue((channel.label, JsonNode.Parse(Encoding.UTF8.GetString(data))!));
            };
        };
        var gen = generation;
        pc.onicecandidate += candidate =>
        {
            if (candidate is null || string.IsNullOrEmpty(candidate.candidate)) return;
            _ = Signal(new SignalMsg { Type = "ice", Generation = gen, Ice = new IceCandidate { Candidate = IceCandidateWire.FromNative(candidate.candidate), SdpMid = candidate.sdpMid, SdpMLineIndex = candidate.sdpMLineIndex } });
        };
        if (pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = offer.Sdp }) != SetDescriptionResultEnum.OK) throw new InvalidDataException("offer 적용 실패");
        var answer = pc.createAnswer(null);
        await pc.setLocalDescription(answer);
        await Signal(new SignalMsg { Type = "answer", Generation = gen, Sdp = answer.sdp });
    }

    public async Task<JsonNode> Next(string label, Func<JsonNode, bool> match, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        var skipped = new List<(string, JsonNode)>();
        try
        {
            while (clock.Elapsed < timeout)
            {
                if (received.TryDequeue(out var item))
                {
                    if (item.Label == label && match(item.Message)) return item.Message;
                    skipped.Add(item);
                    continue;
                }
                await Task.Delay(10);
            }
            throw new TimeoutException($"{label} 채널 메시지를 기다리다 시간 초과");
        }
        finally { foreach (var item in skipped) received.Enqueue(item); }
    }

    public void Send(string label, JsonObject body)
    {
        body["v"] = 1; body["sessionId"] = SessionId; body["generation"] = generation; body["permissionGeneration"] = 1;
        body["seq"] = sent.AddOrUpdate(label, 1, (_, value) => value + 1);
        channels[label].send(body.ToJsonString());
    }
    public void SendControl(string type, string id, JsonObject? fields = null)
    {
        var body = fields ?? new JsonObject();
        body["t"] = type; body["id"] = id;
        Send("control", body);
    }
    long inputSequence;
    public void SendInput(JsonObject body, long epoch, string monitor, long topology)
    {
        var seq = ++inputSequence;
        body["id"] = $"input-{epoch}-{seq}"; body["inputEpoch"] = epoch; body["monitorIdentity"] = monitor; body["topologyGeneration"] = topology;
        body["v"] = 1; body["sessionId"] = SessionId; body["generation"] = generation; body["permissionGeneration"] = 1; body["seq"] = seq;
        channels["input"].send(body.ToJsonString());
    }

    public async ValueTask DisposeAsync()
    {
        try { if (socket.State == WebSocketState.Open) await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        stop.Cancel();
        if (loop is not null) try { await loop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        pc?.close(); pc?.Dispose();
        socket.Dispose();
    }
}
