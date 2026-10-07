using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteAssist.Protocol;

namespace RemoteAssist.Host.Core.Signaling;

/// <summary>
/// Host 쪽 시그널링 클라이언트. Go 시그널링 서버와 말한다.
///
/// 와이어 포맷의 기준은 <c>docs/protocol.md</c> 다. 이 코드가 아니다.
/// </summary>
public sealed class SignalingClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,

        // 서버(Go)는 camelCase로 내보낸다. SignalMsg는 [JsonPropertyName]으로 이름을
        // 못 박아 두었으므로 영향받지 않고, SessionCredentials 같은 단순 DTO만 이 정책을 탄다.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly Uri _httpBase;
    private readonly ILogger _log;
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _heartbeat;
    private int _disposed;
    public SignalMsg? JoinedAck {get;private set;}

    public SignalingClient(Uri httpBase, ILogger<SignalingClient>? log = null,bool allowLoopbackHttp=false)
    {
        ScreenShareOptions.ValidateEndpoint(httpBase,allowLoopbackHttp);
        _httpBase = httpBase;
        _log = (ILogger?)log ?? NullLogger.Instance;
    }

    /// <summary>서버에서 세션 코드·PIN·host 토큰을 발급받는다.</summary>
    public static async Task<SessionCredentials> CreateSessionAsync(Uri httpBase, CancellationToken ct = default,bool allowLoopbackHttp=false)
    {
        ScreenShareOptions.ValidateEndpoint(httpBase,allowLoopbackHttp);
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(10));ct=deadline.Token;
        using var http = new HttpClient();
        using var request=new HttpRequestMessage(HttpMethod.Post,new Uri(httpBase,"/session"));
        using var response = await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(SignalingFraming.MaxBytes,ct);
        var credentials=await response.Content.ReadFromJsonAsync<SessionCredentials>(Json, ct)
            ?? throw new InvalidOperationException("세션 발급 응답을 해석할 수 없습니다.");
        if(credentials.Code.Length!=9||credentials.Code.Any(c=>c is <'0' or >'9')||credentials.Pin.Length!=4||credentials.Pin.Any(c=>c is <'0' or >'9')||
           credentials.HostToken.Length!=32||credentials.SessionId.Length!=32||credentials.ExpiresIn is <30 or >1800)
            throw new InvalidDataException("세션 발급 계약과 다른 응답이다.");
        return credentials;
    }

    /// <summary>WebSocket에 붙어 host 역할로 참가한다. 반대편이 이미 있으면 true.</summary>
    public async Task<bool> ConnectAsync(SessionCredentials session, CancellationToken ct = default)
    {
        var wsUri = new UriBuilder(_httpBase)
        {
            Scheme = _httpBase.Scheme == "https" ? "wss" : "ws",
            Path = "/ws",
        }.Uri;

        await _socket.ConnectAsync(wsUri, ct);
        _log.LogInformation("시그널링 연결: {Uri}", wsUri);

        await SendAsync(new SignalMsg
        {
            Type = "join",
            Room = session.Code,
            Role = "host",
            Token = session.HostToken,
        }, ct);

        var ack = await ReceiveAsync(ct) ?? throw new InvalidOperationException("참가 응답이 없습니다.");
        if (ack.Type == "error")
        {
            throw new InvalidOperationException($"참가 거부됨: {ack.Code} - {ack.Reason}");
        }
        if (ack.Type != "joined")
        {
            throw new InvalidOperationException($"예상치 못한 응답: {ack.Type}");
        }

        if (ack.SessionId != session.SessionId || ack.Generation != 1) throw new InvalidDataException("다른 세션의 ACK다.");
        JoinedAck=ack;
        _heartbeat = HeartbeatAsync(_lifetime.Token);
        return ack.PeerPresent ?? false;
    }

    public async Task SendAsync(SignalMsg msg, CancellationToken ct = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(msg, Json);
        if (bytes.Length > SignalingFraming.MaxBytes) throw new InvalidDataException("메시지 크기 상한 초과다.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        ct = timeout.Token;

        await _sendLock.WaitAsync(ct);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>메시지 하나를 받는다. 연결이 닫히면 null.</summary>
    public async Task<SignalMsg?> ReceiveAsync(CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        return await SignalingFraming.ReceiveAsync(_socket, deadline.Token);
    }

    private async Task HeartbeatAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(ct)) await SendAsync(new SignalMsg { Type = "ping" }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception) { _socket.Abort(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _sendLock.WaitAsync(deadline.Token);
                try { await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", deadline.Token); }
                finally { _sendLock.Release(); }
            }
        }
        catch
        {
            // 이미 끊긴 연결을 닫으려다 나는 예외는 무시한다.
        }

        _socket.Abort();
        if (_heartbeat is not null) await _heartbeat;
        _socket.Dispose();
        JoinedAck=null;
        // Concurrent sends may still own the semaphore; managed wait handles are GC-owned.
        _lifetime.Dispose();
    }
}

/// <summary>POST /session 응답.</summary>
public sealed record SessionCredentials
{
    public string SessionId { get; init; } = "";
    public string Code { get; init; } = "";
    public string Pin { get; init; } = "";
    public string HostToken { get; init; } = "";
    public int ExpiresIn { get; init; }

    /// <summary>대상자에게 읽어 주기 좋은 형태. 418207935 → 418-207-935</summary>
    public string DisplayCode =>
        Code.Length == 9 ? $"{Code[..3]}-{Code[3..6]}-{Code[6..]}" : Code;
}
