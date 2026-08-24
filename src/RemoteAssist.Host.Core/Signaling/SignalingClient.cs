using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
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

    public SignalingClient(Uri httpBase, ILogger<SignalingClient>? log = null)
    {
        _httpBase = httpBase;
        _log = (ILogger?)log ?? NullLogger.Instance;
    }

    /// <summary>서버에서 세션 코드·PIN·host 토큰을 발급받는다.</summary>
    public static async Task<SessionCredentials> CreateSessionAsync(Uri httpBase, CancellationToken ct = default)
    {
        using var http = new HttpClient();
        using var response = await http.PostAsync(new Uri(httpBase, "/session"), null, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<SessionCredentials>(Json, ct)
            ?? throw new InvalidOperationException("세션 발급 응답을 해석할 수 없습니다.");
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

        return ack.PeerPresent ?? false;
    }

    public async Task SendAsync(SignalMsg msg, CancellationToken ct = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(msg, Json);

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
        var buffer = new ArraySegment<byte>(new byte[64 * 1024]);
        var sb = new StringBuilder();

        while (true)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await _socket.ReceiveAsync(buffer, ct);
            }
            catch (WebSocketException ex)
            {
                _log.LogInformation("시그널링 연결 종료: {Message}", ex.Message);
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            sb.Append(Encoding.UTF8.GetString(buffer.Array!, 0, result.Count));
            if (result.EndOfMessage) break;
        }

        return JsonSerializer.Deserialize<SignalMsg>(sb.ToString(), Json);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
        }
        catch
        {
            // 이미 끊긴 연결을 닫으려다 나는 예외는 무시한다.
        }

        _socket.Dispose();
        _sendLock.Dispose();
    }
}

/// <summary>POST /session 응답.</summary>
public sealed record SessionCredentials
{
    public string Code { get; init; } = "";
    public string Pin { get; init; } = "";
    public string HostToken { get; init; } = "";
    public int ExpiresIn { get; init; }

    /// <summary>대상자에게 읽어 주기 좋은 형태. 418207935 → 418-207-935</summary>
    public string DisplayCode =>
        Code.Length == 9 ? $"{Code[..3]}-{Code[3..6]}-{Code[6..]}" : Code;
}
