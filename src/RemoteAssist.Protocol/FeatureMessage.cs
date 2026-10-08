using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteAssist.Protocol;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FeatureMessage:DataMessage
{
    [JsonPropertyName("text"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]public string? Text {get;init;}
    [JsonPropertyName("hash"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]public string? Hash {get;init;}
    [JsonPropertyName("code"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]public string? Code {get;init;}
    [JsonPropertyName("snapshot"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]public DiagnosticSnapshot? Snapshot {get;init;}
    public static FeatureMessage Parse(ReadOnlyMemory<byte> bytes)
    {
        using var document=ProtocolJson.Parse(bytes,32768);var value=JsonSerializer.Deserialize<FeatureMessage>(bytes.Span)??throw new InvalidDataException("보조 기능 메시지 누락이다.");
        if(value.Generation is <1 or >9007199254740991L||value.PermissionGeneration is <1 or >9007199254740991L||value.Sequence is <1 or >9007199254740991L||value.SessionId is null||value.SessionId.Length is <1 or >64||!Guid.TryParseExact(value.Id,"N",out var id)||value.Id!=id.ToString("N"))throw new InvalidDataException("보조 기능 바인딩 오류다.");
        switch(value.T)
        {
            case "chat":if(value.Text is null||value.Text.Length is <1 or >4096||value.Hash!=null||value.Code!=null||value.Snapshot!=null)throw new InvalidDataException("채팅 본문 오류다.");break;
            case "clipboard-get":if(value.Text!=null||value.Hash!=null||value.Code!=null||value.Snapshot!=null)throw new InvalidDataException("클립보드 읽기 본문 오류다.");break;
            case "clipboard-set":case "clipboard-text":
                if(value.Text is null||value.Text.Length>8192||Encoding.UTF8.GetByteCount(value.Text)>16384||value.Hash!=TextHash(value.Text)||value.Code!=null||value.Snapshot!=null)throw new InvalidDataException("클립보드 본문·hash 오류다.");break;
            case "clipboard-applied":if(value.Text!=null||!ValidHash(value.Hash)||value.Code!=null||value.Snapshot!=null)throw new InvalidDataException("클립보드 적용 응답 오류다.");break;
            case "clipboard-rejected":if(value.Text!=null||value.Hash!=null||value.Snapshot!=null||value.Code is not ("clipboard-busy" or "too-large" or "unavailable"))throw new InvalidDataException("클립보드 거부 응답 오류다.");break;
            case "telemetry":if(value.Text!=null||value.Hash!=null||value.Code!=null||value.Snapshot is null)throw new InvalidDataException("진단 본문 오류다.");value.Snapshot.Validate();break;
            default:throw new InvalidDataException("미지원 보조 기능 메시지다.");
        }
        if(value.Text is {} text&&(!ValidUnicode(text)||text.Contains('\0')))throw new InvalidDataException("텍스트 Unicode 오류다.");return value;
    }
    public static string TextHash(string text)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static FeatureMessage ClipboardTextReply(FeatureMessage request,string text)
    {
        if(request.T!="clipboard-get")throw new InvalidDataException("클립보드 읽기 요청이 아니다.");
        if(!ValidUnicode(text)||text.Contains('\0'))return request with {T="clipboard-rejected",Code="unavailable"};
        if(text.Length>8192||Encoding.UTF8.GetByteCount(text)>16384)return request with {T="clipboard-rejected",Code="too-large"};
        var response=request with {T="clipboard-text",Text=text,Hash=TextHash(text)};
        return ProtocolJson.Encode(response).Length>30720?request with {T="clipboard-rejected",Code="too-large"}:response;
    }
    static bool ValidHash(string? text)=>text is {Length:64}&&text.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static bool ValidUnicode(string text)
    {
        for(var i=0;i<text.Length;i++)if(char.IsSurrogate(text[i])){if(!char.IsHighSurrogate(text[i])||i+1==text.Length||!char.IsLowSurrogate(text[++i]))return false;}
        return true;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DiagnosticSnapshot([property:JsonRequired,JsonPropertyName("utc")]DateTimeOffset Utc,[property:JsonRequired,JsonPropertyName("cpuPercent")]double? CpuPercent,[property:JsonRequired,JsonPropertyName("memory")]MemorySnapshot? Memory,[property:JsonRequired,JsonPropertyName("disks")]DiskSnapshot[]? Disks,[property:JsonRequired,JsonPropertyName("network")]NetworkSnapshot? Network,[property:JsonRequired,JsonPropertyName("errors")]ErrorSnapshot[]? Errors,[property:JsonRequired,JsonPropertyName("startup")]string[]? Startup,[property:JsonRequired,JsonPropertyName("services")]ServiceSnapshot[]? Services)
{
    static bool Count(long value)=>value is >=0 and <=9007199254740991L;
    public void Validate()
    {
        if(Utc.Offset!=TimeSpan.Zero||CpuPercent is {} cpu&&(!double.IsFinite(cpu)||cpu is <0 or >100)||Memory is {} m&&(!Count(m.TotalBytes)||!Count(m.AvailableBytes)||m.AvailableBytes>m.TotalBytes)||Network is {} n&&(!Count(n.ReceivedBytes)||!Count(n.SentBytes))||
            Disks?.Length>128||Errors?.Length>20||Startup?.Length>128||Services?.Length>128)throw new InvalidDataException("진단 수치·목록 상한 오류다.");
        foreach(var d in Disks??[])if(d is null||d.Name is not {Length:2}||!char.IsAsciiLetter(d.Name[0])||d.Name[1]!=':'||!Count(d.TotalBytes)||!Count(d.FreeBytes)||d.FreeBytes>d.TotalBytes)throw new InvalidDataException("디스크 진단 오류다.");
        foreach(var e in Errors??[])if(e is null||!Name(e.Provider)||e.EventId is <0 or >0x3fffffff||e.Utc.Offset!=TimeSpan.Zero||e.Level is not ("error" or "warning"))throw new InvalidDataException("이벤트 진단 오류다.");
        foreach(var s in Services??[])if(s is null||!Name(s.Name)||s.State is not ("Running" or "Stopped" or "Paused" or "StartPending" or "StopPending" or "ContinuePending" or "PausePending"))throw new InvalidDataException("서비스 진단 오류다.");
        if((Startup??[]).Any(s=>!Name(s)))throw new InvalidDataException("시작 항목 진단 오류다.");
    }
    static bool Name(string? value)=>value is not null&&value.Length is >0 and <=128&&!value.Any(char.IsControl)&&!value.Contains('\\')&&!value.Contains('/');
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]public sealed record MemorySnapshot([property:JsonRequired,JsonPropertyName("totalBytes")]long TotalBytes,[property:JsonRequired,JsonPropertyName("availableBytes")]long AvailableBytes);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]public sealed record DiskSnapshot([property:JsonRequired,JsonPropertyName("name")]string Name,[property:JsonRequired,JsonPropertyName("totalBytes")]long TotalBytes,[property:JsonRequired,JsonPropertyName("freeBytes")]long FreeBytes);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]public sealed record NetworkSnapshot([property:JsonRequired,JsonPropertyName("receivedBytes")]long ReceivedBytes,[property:JsonRequired,JsonPropertyName("sentBytes")]long SentBytes);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]public sealed record ErrorSnapshot([property:JsonRequired,JsonPropertyName("provider")]string Provider,[property:JsonRequired,JsonPropertyName("eventId")]int EventId,[property:JsonRequired,JsonPropertyName("utc")]DateTimeOffset Utc,[property:JsonRequired,JsonPropertyName("level")]string Level);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]public sealed record ServiceSnapshot([property:JsonRequired,JsonPropertyName("name")]string Name,[property:JsonRequired,JsonPropertyName("state")]string State);
