using System.Text.Json;
using System.Text.Json.Serialization;
using RemoteAssist.Protocol;

namespace RemoteAssist.Host.Core.Ipc;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class LocalIpcMessage
{
    [JsonPropertyName("v")] public int Version {get;init;}=1;
    [JsonPropertyName("t")] public string Type {get;init;}="";
    [JsonPropertyName("seq")] public long Sequence {get;init;}
    [JsonPropertyName("id")] public string Id {get;init;}="";
    [JsonPropertyName("nonce"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? Nonce {get;init;}
    [JsonPropertyName("body"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public JsonElement? Body {get;init;}
    public const int MaxBytes=32768;
    static readonly HashSet<string> Types=new(StringComparer.Ordinal) {"hello","heartbeat","config","start","stop","credentials","state","consent-request","consent","admin-request","admin-state","admin-revoke","input","monitor","file-request","file-response","clipboard-request","clipboard-response","runbook-request","runbook-response","error"};
    public static LocalIpcMessage Parse(ReadOnlyMemory<byte> bytes)
    {
        using var doc=ProtocolJson.Parse(bytes,MaxBytes);
        var msg=JsonSerializer.Deserialize<LocalIpcMessage>(bytes.Span)??throw new InvalidDataException("빈 IPC다.");
        if(!Types.Contains(msg.Type)||msg.Sequence<1||msg.Id is null||msg.Id.Length>64||msg.Nonce is not null&&(msg.Type!="hello"||msg.Nonce.Length!=64||!msg.Nonce.All(Uri.IsHexDigit)))throw new InvalidDataException("IPC envelope 오류다.");
        return msg;
    }
    // Deliberately a class: generated record ToString must never expose nonce/PIN/body.
}
