using System.Text.Json.Serialization;

namespace RemoteAssist.Protocol;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FileAuditNotice(
    [property:JsonPropertyName("id")]string Id,
    [property:JsonPropertyName("sessionId")]string SessionId,
    [property:JsonPropertyName("permissionGeneration")]long PermissionGeneration,
    [property:JsonPropertyName("phase")]string Phase,
    [property:JsonPropertyName("direction")]string Direction,
    [property:JsonPropertyName("size")]long Size,
    [property:JsonPropertyName("outcome")]string Outcome)
{
    public void Validate()
    {
        if(!Guid.TryParseExact(Id,"N",out var id)||id==Guid.Empty||id.ToString("N")!=Id||SessionId is not {Length:>0 and <=64}||PermissionGeneration is <1 or >9007199254740991L||Direction is not ("to-host" or "from-host")||Size is <0 or >FileMessage.MaxSize||
            (Phase=="started"?Outcome!="allowed":Phase!="finished"||Outcome is not ("denied" or "completed" or "failed" or "stopped")))throw new InvalidDataException("파일 감사 메타데이터 오류다.");
    }
}
