using System.Text.Json.Serialization;

namespace RemoteAssist.Protocol;

/// <summary>
/// input 데이터채널 메시지. 좌표는 항상 0~1 정규화이며 Host가 실제 픽셀로 환산한다.
/// 뷰어 창 크기·DPI에 의존하지 않게 하기 위한 것이다. 기준: docs/protocol.md.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InputMsg : DataMessage
{
    /// <summary>mouse: move|down|up|wheel / key: down|up|text</summary>
    [JsonPropertyName("a")] public string A { get; init; } = "";

    [JsonPropertyName("x")] public double X { get; init; }
    [JsonPropertyName("y")] public double Y { get; init; }

    [JsonPropertyName("monitorIdentity")] public string MonitorIdentity { get; init; } = "";
    [JsonPropertyName("topologyGeneration")] public long TopologyGeneration { get; init; }
    [JsonPropertyName("inputEpoch")] public long InputEpoch {get;init;}
    [JsonPropertyName("pointerBarrier")] public long PointerBarrier {get;init;}

    /// <summary>마우스 버튼: left|right|middle</summary>
    [JsonPropertyName("btn")] public string? Btn { get; init; }

    /// <summary>휠 델타</summary>
    [JsonPropertyName("d")] public int? D { get; init; }

    /// <summary>가상 키코드</summary>
    [JsonPropertyName("vk")] public int? Vk { get; init; }

    /// <summary>유니코드 문자 입력</summary>
    [JsonPropertyName("text")] public string? Text { get; init; }

    /// <summary>"ctrl" | "alt" | "shift" | "win"</summary>
    [JsonPropertyName("mods")] public string[]? Mods { get; init; }
}
