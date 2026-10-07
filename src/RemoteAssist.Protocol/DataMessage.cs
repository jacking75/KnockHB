using System.Text.Json.Serialization;

namespace RemoteAssist.Protocol;

public record DataMessage
{
    [JsonPropertyName("v")] public int Version {get;init;}=1;
    [JsonPropertyName("t")] public string T {get;init;}="";
    [JsonPropertyName("sessionId")] public string SessionId {get;init;}="";
    [JsonPropertyName("generation")] public long Generation {get;init;}
    [JsonPropertyName("permissionGeneration")] public long PermissionGeneration {get;init;}
    [JsonPropertyName("id")] public string Id {get;init;}="";
    [JsonPropertyName("seq")] public long Sequence {get;init;}
}
