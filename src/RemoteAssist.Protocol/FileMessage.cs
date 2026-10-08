using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteAssist.Protocol;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FileMessage:DataMessage
{
    public const long MaxSize=256L*1024*1024;
    [JsonPropertyName("name"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]public string? Name {get;init;}
    [JsonPropertyName("size"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]public long? Size {get;init;}
    [JsonPropertyName("hash"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]public string? Hash {get;init;}
    [JsonPropertyName("code"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]public string? Code {get;init;}
    public static FileMessage Parse(ReadOnlyMemory<byte> bytes)
    {
        using var doc=ProtocolJson.Parse(bytes,32768);var value=JsonSerializer.Deserialize<FileMessage>(bytes.Span)??throw new InvalidDataException("파일 메시지 누락이다.");
        if(value.SessionId is not {Length:>0 and <=64}||value.Generation is <1 or >9007199254740991L||value.PermissionGeneration is <1 or >9007199254740991L||value.Sequence is <1 or >9007199254740991L||!Guid.TryParseExact(value.Id,"N",out var id)||id==Guid.Empty||value.Id!=id.ToString("N"))throw new InvalidDataException("파일 바인딩 오류다.");
        switch(value.T)
        {
            case "file-offer":if(!SafeName(value.Name)||!Sized(value.Size)||!Hashed(value.Hash)||value.Code!=null)throw new InvalidDataException("파일 제안 오류다.");break;
            case "file-complete":case "file-received":if(value.Name!=null||!Sized(value.Size)||!Hashed(value.Hash)||value.Code!=null)throw new InvalidDataException("파일 완료 오류다.");break;
            case "file-accept":if(value.Name!=null||value.Size!=null||value.Hash!=null||value.Code!=null)throw new InvalidDataException("파일 수락 오류다.");break;
            case "file-reject":case "file-cancel":if(value.Name!=null||value.Size!=null||value.Hash!=null||value.Code is not ("declined" or "busy" or "timeout" or "io-error" or "hash-mismatch" or "cancelled" or "too-large"))throw new InvalidDataException("파일 거부 오류다.");break;
            default:throw new InvalidDataException("파일 명령 오류다.");
        }
        return value;
    }
    static bool Sized(long? value)=>value is >=0 and <=MaxSize;
    static bool Hashed(string? value)=>value is {Length:64}&&value.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static bool SafeName(string? name)
    {
        if(name is null||name.Length is <1 or >128||name.EndsWith('.')||name.EndsWith(' ')||name.Any(c=>char.IsControl(c)||"<>:\"/\\|?*".Contains(c))||!FeatureMessage.ValidUnicode(name))return false;
        var stem=name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return stem is not ("CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$")&&!(stem.Length==4&&(stem.StartsWith("COM",StringComparison.Ordinal)||stem.StartsWith("LPT",StringComparison.Ordinal))&&"123456789¹²³".Contains(stem[3]));
    }
}
