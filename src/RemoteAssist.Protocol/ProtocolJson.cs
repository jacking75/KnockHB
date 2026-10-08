using System.Text.Json;
using System.Text.Encodings.Web;

namespace RemoteAssist.Protocol;

public static class ProtocolJson
{
    // Transport JSON is never embedded in HTML. Keep Unicode within the documented UTF-8 limits.
    static readonly JsonSerializerOptions wire=CreateWire();
    static JsonSerializerOptions CreateWire(){var value=new JsonSerializerOptions {Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping};value.MakeReadOnly(populateMissingResolver:true);return value;}
    public static byte[] Encode<T>(T value)=>JsonSerializer.SerializeToUtf8Bytes(value,wire);
    public static JsonElement Element<T>(T value)=>JsonSerializer.SerializeToElement(value,wire);
    public static JsonDocument Parse(ReadOnlyMemory<byte> bytes,int maxBytes)
    {
        var document=ParseObject(bytes,maxBytes);
        try
        {
            if(!document.RootElement.TryGetProperty("v",out var version)||!version.TryGetInt32(out var value)||value!=1)
                throw new InvalidDataException("프로토콜 버전이 필요하다.");
            return document;
        }
        catch {document.Dispose();throw;}
    }
    public static JsonDocument ParseObject(ReadOnlyMemory<byte> bytes,int maxBytes)
    {
        if(bytes.Length>maxBytes)throw new InvalidDataException("프로토콜 크기 상한 초과다.");
        var document=JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=32});
        try
        {
            ValidateDuplicates(document.RootElement);
            if(document.RootElement.ValueKind!=JsonValueKind.Object)
                throw new InvalidDataException("JSON 객체가 필요하다.");
            return document;
        }
        catch {document.Dispose();throw;}
    }
    private static void ValidateDuplicates(JsonElement value)
    {
        if(value.ValueKind==JsonValueKind.Object)
        {
            var fields=new HashSet<string>(StringComparer.Ordinal);
            foreach(var property in value.EnumerateObject()){if(!fields.Add(property.Name))throw new InvalidDataException("중복 JSON 필드다.");ValidateDuplicates(property.Value);}
        }
        else if(value.ValueKind==JsonValueKind.Array)foreach(var child in value.EnumerateArray())ValidateDuplicates(child);
    }
    public static InputMsg ParseInput(ReadOnlyMemory<byte> bytes)
    {
        using var document=Parse(bytes,32*1024);
        var message=JsonSerializer.Deserialize<InputMsg>(bytes.Span)??throw new InvalidDataException("빈 입력이다.");
        if(message.Generation<1||message.PermissionGeneration<1||message.Sequence<1||message.Sequence>9007199254740991L||
           message.SessionId is null||message.SessionId.Length is <1 or >64||message.Id is null||message.Id.Length is <1 or >64||message.TopologyGeneration<1||
           message.InputEpoch is <1 or >9007199254740991L||message.MonitorIdentity is null||message.MonitorIdentity.Length is <1 or >128)
            throw new InvalidDataException("입력 바인딩/세대/순번 오류다.");
        switch(message.T)
        {
            case "mouse":
                if(message.A!="move"&&(!document.RootElement.TryGetProperty("pointerBarrier",out _)||message.PointerBarrier is <0 or >9007199254740991L))throw new InvalidDataException("포인터 barrier 오류다.");
                if(!document.RootElement.TryGetProperty("x",out _)||!document.RootElement.TryGetProperty("y",out _)||
                   !double.IsFinite(message.X)||!double.IsFinite(message.Y)||message.X is <0 or >1||message.Y is <0 or >1)
                    throw new InvalidDataException("잘못된 좌표다.");
                if(message.A is "down" or "up") {if(message.Btn is not ("left" or "right" or "middle"))throw new InvalidDataException("잘못된 버튼이다.");}
                else if(message.A=="wheel") {if(message.D is null or < -12000 or >12000)throw new InvalidDataException("잘못된 휠이다.");}
                else if(message.A!="move")throw new InvalidDataException("잘못된 마우스 동작이다.");
                break;
            case "key":
                if(message.A=="text") {if(message.Text is null||message.Text.Length is <1 or >4096||message.Text.Contains('\0'))throw new InvalidDataException("잘못된 입력 문자열이다.");}
                else if(message.A is "down" or "up") {if(message.Vk is null or <1 or >254)throw new InvalidDataException("잘못된 키다.");}
                else throw new InvalidDataException("잘못된 키 동작이다.");
                if(message.Mods is not null && (message.Mods.Length>4 ||message.Mods.Distinct().Count()!=message.Mods.Length||message.Mods.Any(mod=>mod is not ("ctrl" or "alt" or "shift" or "win"))))throw new InvalidDataException("잘못된 modifier다.");
                break;
            case "reset":if(message.A!="all")throw new InvalidDataException("잘못된 reset이다.");break;
            default:throw new InvalidDataException("알 수 없는 입력 명령이다.");
        }
        return message;
    }
}
