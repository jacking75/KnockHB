using System.Text.Json.Serialization;

namespace RemoteAssist.Protocol;

/// <summary>
/// 시그널링 서버(Go)와 주고받는 메시지.
///
/// <b>필드명 기준은 <c>docs/protocol.md</c> 다.</b> 이 코드가 아니다.
/// Go 서버·JS 뷰어가 같은 문서를 각자 구현하므로, 스키마를 바꿀 때는 문서를 먼저 고친다.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SignalMsg
{
    [JsonPropertyName("v")] public int Version { get; init; } = 1;
    [JsonPropertyName("sessionId")] public string? SessionId { get; init; }
    [JsonPropertyName("connectionId")] public string? ConnectionId { get; init; }
    [JsonPropertyName("generation")] public int Generation { get; init; }
    [JsonPropertyName("permissionGeneration")] public int PermissionGeneration { get; init; }
    [JsonPropertyName("permissions")] public string[]? Permissions { get; init; }
    [JsonPropertyName("displayName")] public string? DisplayName { get; init; }
    [JsonPropertyName("iceServers")] public IceServerInfo[]? IceServers { get; init; }
    /// <summary>join | joined | peer-joined | peer-left | offer | answer | ice | bye | error</summary>
    [JsonPropertyName("type")] public string Type { get; init; } = "";

    /// <summary>세션 코드(룸 식별자).</summary>
    [JsonPropertyName("room")] public string? Room { get; init; }

    /// <summary>host | viewer</summary>
    [JsonPropertyName("role")] public string? Role { get; init; }

    /// <summary>Host 슬롯 점유용 토큰. 서버는 이 필드를 반대편으로 릴레이하지 않는다.</summary>
    [JsonPropertyName("token")] public string? Token { get; init; }

    [JsonPropertyName("sdp")] public string? Sdp { get; init; }

    [JsonPropertyName("ice")] public IceCandidate? Ice { get; init; }

    /// <summary>joined 응답에서, 반대편이 이미 붙어 있는지.</summary>
    [JsonPropertyName("peerPresent")] public bool? PeerPresent { get; init; }

    /// <summary>error 전용. bad-request | not-found | expired | bad-token | bad-role | slot-taken</summary>
    [JsonPropertyName("code")] public string? Code { get; init; }

    /// <summary>error 전용. 사람이 읽는 설명.</summary>
    [JsonPropertyName("reason")] public string? Reason { get; init; }
}

/// <summary>ICE 후보. 브라우저 RTCIceCandidate의 필드명을 그대로 따른다.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record IceCandidate
{
    [JsonPropertyName("candidate")] public string Candidate { get; init; } = "";
    [JsonPropertyName("sdpMid")] public string? SdpMid { get; init; }
    [JsonPropertyName("sdpMLineIndex")] public int? SdpMLineIndex { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record IceServerInfo
{
    [JsonPropertyName("urls")] public string[] Urls {get;init;}=[];
    [JsonPropertyName("username")] public string? Username {get;init;}
    [JsonPropertyName("credential")] public string? Credential {get;init;}
}
