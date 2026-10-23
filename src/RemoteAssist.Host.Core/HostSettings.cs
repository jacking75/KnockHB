using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteAssist.Host.Core;

public static class HostSettings
{
    public static ScreenShareOptions Parse(ReadOnlySpan<byte> bytes)
    {
        if(bytes.Length>32768)throw new InvalidDataException("Host 설정 크기 초과다.");
        if(bytes.StartsWith(new byte[]{0xEF,0xBB,0xBF}))bytes=bytes[3..];
        var text=new UTF8Encoding(false,true).GetString(bytes);
        using var document=JsonDocument.Parse(text,new JsonDocumentOptions {MaxDepth=8});
        if(document.RootElement.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("Host 설정은 객체다.");
        var fields=new HashSet<string>(StringComparer.Ordinal);
        foreach(var field in document.RootElement.EnumerateObject())if(!fields.Add(field.Name))throw new InvalidDataException("중복 Host 설정이다.");
        var result=JsonSerializer.Deserialize<ScreenShareOptions>(text,new JsonSerializerOptions {UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow})??throw new InvalidDataException("Host 설정 오류다.");
        result.Validate(256);return result;
    }
}
