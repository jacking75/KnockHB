using System.Net.WebSockets;
using System.Text.Json;
using RemoteAssist.Protocol;

namespace RemoteAssist.Host.Core.Signaling;

public static class SignalingFraming
{
    public const int MaxBytes = 128 * 1024;
    public static async Task<SignalMsg?> ReceiveAsync(WebSocket socket, CancellationToken ct)
    {
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("텍스트 시그널링만 허용한다.");
            if (bytes.Length + result.Count > MaxBytes) throw new InvalidDataException("시그널링 크기 상한 초과다.");
            bytes.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }
        return Parse(bytes.ToArray());
    }
    public static SignalMsg Parse(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length > MaxBytes) throw new InvalidDataException("메시지 크기 상한 초과다.");
        using var doc = ProtocolJson.Parse(bytes,MaxBytes);
        var msg = JsonSerializer.Deserialize<SignalMsg>(bytes.Span) ?? throw new InvalidDataException("빈 시그널링이다.");
        // A property initializer is not evidence that the sender included a version.
        if (!doc.RootElement.TryGetProperty("v", out var version) || version.GetInt32() != 1 || msg.Version != 1)
            throw new InvalidDataException("지원하지 않는 프로토콜 버전이다.");
        if (string.IsNullOrEmpty(msg.Type)) throw new InvalidDataException("type이 필요하다.");
        return msg;
    }
}
