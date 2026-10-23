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
    [JsonPropertyName("monitors"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]public MonitorDescriptor[]? Monitors {get;init;}
    [JsonPropertyName("monitorIdentity"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]public string? MonitorIdentity {get;init;}
    [JsonPropertyName("sample"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]public QualitySample? Sample {get;init;}
    [JsonPropertyName("quality"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]public QualitySetting? Quality {get;init;}
    [JsonPropertyName("mode"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]public string? Mode {get;init;}
    public static ControlMessage Parse(ReadOnlyMemory<byte> bytes)
    {
        using var document=ProtocolJson.Parse(bytes,32768);
        var message=JsonSerializer.Deserialize<ControlMessage>(bytes.Span)??throw new InvalidDataException("제어 메시지 누락이다.");
        if(message.T is not ("monitor-list" or "monitor-select" or "monitor-state" or "quality-report" or "quality-mode" or "quality-state" or "data-ready" or "data-ping" or "data-pong" or "input-reset-ack" or "admin-request" or "admin-state" or "admin-sas" or "admin-revoke")||message.SessionId is null||message.SessionId.Length is <1 or >64||
            message.Id is null||message.Id.Length is <1 or >64||message.Generation<1||message.PermissionGeneration<1||message.Sequence is <1 or >9007199254740991L)
            throw new InvalidDataException("제어 메시지 바인딩 오류다.");
        if(message.Generation>9007199254740991L||message.PermissionGeneration>9007199254740991L)throw new InvalidDataException("제어 세대 상한 오류다.");
        if(message.T=="data-ready")
        {
            var m=message.Monitor;
            if(message.Permissions is null||!message.Permissions.SequenceEqual(FeatureContract.Permissions)||message.Channels is null||!message.Channels.SequenceEqual(FeatureContract.Channels))throw new InvalidDataException("기능 채널 준비 계약 오류다.");
            if(m is null||m.Identity is null||m.Identity.Length is <1 or >128||m.TopologyGeneration is <1 or >9007199254740991L||m.Width is <2 or >16384||m.Height is <2 or >16384)throw new InvalidDataException("모니터 준비 계약 오류다.");
        }
        else if(message.Channels!=null||message.Permissions!=null||message.Monitor!=null&&message.T!="monitor-state")throw new InvalidDataException("생존 메시지의 추가 필드를 거부한다.");
        if(message.T=="monitor-state"){ValidateMonitor(message.Monitor??throw new InvalidDataException("모니터 상태 누락이다."));}
        if(message.Monitors!=null){if(message.T!="monitor-list"||message.Monitors.Length is <1 or >16||message.Monitors.Select(m=>m.Identity).Distinct().Count()!=message.Monitors.Length)throw new InvalidDataException("모니터 목록 오류다.");foreach(var item in message.Monitors)ValidateMonitor(item);}
        if(message.T=="monitor-select"?message.MonitorIdentity is not {Length:>0 and <=128}:message.MonitorIdentity!=null)throw new InvalidDataException("모니터 선택 오류다.");
        if(message.T=="quality-report"){(message.Sample??throw new InvalidDataException("품질 통계 누락이다.")).Validate();}else if(message.Sample!=null)throw new InvalidDataException("추가 통계 필드다.");
        if(message.T=="quality-state"){(message.Quality??throw new InvalidDataException("품질 설정 누락이다.")).Validate();}else if(message.Quality!=null)throw new InvalidDataException("추가 품질 필드다.");
        if(message.T=="quality-mode"?message.Mode is not ("auto" or "detail" or "motion"):message.Mode!=null)throw new InvalidDataException("품질 모드 오류다.");
        // 입력을 멈췄다 재개하는 전이(관리자 모드, 모니터 전환)는 새 inputEpoch를 알린다.
        var phase=message.T=="admin-state"&&message.State is "active" or "denied"||message.T=="monitor-state";
        if(message.T=="input-reset-ack"||phase?message.InputEpoch is null or <2 or >9007199254740991L:message.InputEpoch!=null)throw new InvalidDataException("입력 phase ACK 오류다.");
        var active=message.T=="admin-state"&&message.State=="active";
        if(message.T=="admin-state"?message.State is not ("pending" or "denied" or "active"):message.State!=null)throw new InvalidDataException("관리자 상태 오류다.");
        if(active||message.T is "admin-sas" or "admin-revoke")
        {if(message.AdminGeneration is null or <1 or >9007199254740991L)throw new InvalidDataException("관리자 세대 오류다.");}
        else if(message.AdminGeneration!=null)throw new InvalidDataException("미지원 관리자 세대 필드다.");
        if(message.T=="admin-request"&&!Guid.TryParseExact(message.Id,"N",out _))throw new InvalidDataException("관리자 요청 ID 오류다.");
        return message;
    }
    static void ValidateMonitor(MonitorDescriptor m){if(m.Identity is not {Length:>0 and <=128}||m.TopologyGeneration is <1 or >9007199254740991L||m.Width is <2 or >16384||m.Height is <2 or >16384)throw new InvalidDataException("모니터 geometry 오류다.");}
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record QualitySample([property:JsonPropertyName("rttMs")]double RttMs,[property:JsonPropertyName("lossPercent")]double LossPercent,[property:JsonPropertyName("decodeMs")]double DecodeMs,[property:JsonPropertyName("receivedKbps")]double ReceivedKbps)
{
    public void Validate(){if(!double.IsFinite(RttMs)||RttMs is <0 or >10000||!double.IsFinite(LossPercent)||LossPercent is <0 or >100||!double.IsFinite(DecodeMs)||DecodeMs is <0 or >10000||!double.IsFinite(ReceivedKbps)||ReceivedKbps is <0 or >100000)throw new InvalidDataException("품질 통계 범위 오류다.");}
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record QualitySetting([property:JsonPropertyName("fps")]int Fps,[property:JsonPropertyName("bitrateKbps")]int BitrateKbps,[property:JsonPropertyName("longEdge")]int LongEdge,[property:JsonPropertyName("shortEdge")]int ShortEdge,[property:JsonPropertyName("mode")]string Mode)
{
    public void Validate(){if(Fps is <1 or >60||BitrateKbps is <100 or >50000||LongEdge is <2 or >3840||ShortEdge is <2 or >2160||ShortEdge>LongEdge||Mode is not ("auto" or "detail" or "motion"))throw new InvalidDataException("품질 설정 범위 오류다.");}
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MonitorDescriptor(
    [property:JsonPropertyName("identity")]string Identity,
    [property:JsonPropertyName("topologyGeneration")]long TopologyGeneration,
    [property:JsonPropertyName("x")]int X,[property:JsonPropertyName("y")]int Y,
    [property:JsonPropertyName("width")]int Width,[property:JsonPropertyName("height")]int Height);
