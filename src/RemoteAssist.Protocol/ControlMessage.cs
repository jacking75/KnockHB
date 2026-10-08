using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteAssist.Protocol;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ControlMessage:DataMessage
{
    [JsonPropertyName("channels"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]public string[]? Channels {get;init;}
    [JsonPropertyName("permissions"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]public string[]? Permissions {get;init;}
    [JsonPropertyName("monitor"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]public MonitorDescriptor? Monitor {get;init;}
    [JsonPropertyName("inputEpoch"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]public long? InputEpoch {get;init;}
    [JsonPropertyName("state"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]public string? State {get;init;}
    [JsonPropertyName("adminGeneration"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]public long? AdminGeneration {get;init;}
    public static ControlMessage Parse(ReadOnlyMemory<byte> bytes)
    {
        using var document=ProtocolJson.Parse(bytes,32768);
        var message=JsonSerializer.Deserialize<ControlMessage>(bytes.Span)??throw new InvalidDataException("제어 메시지 누락이다.");
        if(message.T is not ("data-ready" or "data-ping" or "data-pong" or "input-reset-ack" or "admin-request" or "admin-state" or "admin-sas" or "admin-revoke")||message.SessionId is null||message.SessionId.Length is <1 or >64||
            message.Id is null||message.Id.Length is <1 or >64||message.Generation<1||message.PermissionGeneration<1||message.Sequence is <1 or >9007199254740991L)
            throw new InvalidDataException("제어 메시지 바인딩 오류다.");
        if(message.Generation>9007199254740991L||message.PermissionGeneration>9007199254740991L)throw new InvalidDataException("제어 세대 상한 오류다.");
        if(message.T=="data-ready")
        {
            var m=message.Monitor;
            var mask=FeatureContract.ParsePermissions(message.Permissions);
            if(message.Channels is null||!message.Channels.SequenceEqual(FeatureContract.Channels(mask)))throw new InvalidDataException("기능 채널 준비 계약 오류다.");
            if((mask&1)!=0)
            {if(m is null||m.Identity is null||m.Identity.Length is <1 or >128||m.TopologyGeneration!=1||m.Width is <2 or >16384||m.Height is <2 or >16384)throw new InvalidDataException("모니터 준비 계약 오류다.");}
            else if(m!=null)throw new InvalidDataException("화면 승인 없는 모니터 전달이다.");
        }
        else if(message.Channels!=null||message.Permissions!=null||message.Monitor!=null)throw new InvalidDataException("생존 메시지의 추가 필드를 거부한다.");
        var phase=message.T=="admin-state"&&message.State is "active" or "denied";
        if(message.T=="input-reset-ack"||phase?message.InputEpoch is null or <2 or >9007199254740991L:message.InputEpoch!=null)throw new InvalidDataException("입력 phase ACK 오류다.");
        var active=message.T=="admin-state"&&message.State=="active";
        if(message.T=="admin-state"?message.State is not ("pending" or "denied" or "active"):message.State!=null)throw new InvalidDataException("관리자 상태 오류다.");
        if(active||message.T is "admin-sas" or "admin-revoke")
        {if(message.AdminGeneration is null or <1 or >9007199254740991L)throw new InvalidDataException("관리자 세대 오류다.");}
        else if(message.AdminGeneration!=null)throw new InvalidDataException("미지원 관리자 세대 필드다.");
        if(message.T=="admin-request"&&!Guid.TryParseExact(message.Id,"N",out _))throw new InvalidDataException("관리자 요청 ID 오류다.");
        return message;
    }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MonitorDescriptor(
    [property:JsonPropertyName("identity")]string Identity,
    [property:JsonPropertyName("topologyGeneration")]long TopologyGeneration,
    [property:JsonPropertyName("x")]int X,[property:JsonPropertyName("y")]int Y,
    [property:JsonPropertyName("width")]int Width,[property:JsonPropertyName("height")]int Height);
