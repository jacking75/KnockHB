using System.Text.Json.Serialization;
using RemoteAssist.Host.Core.Input;
using RemoteAssist.Protocol;

namespace RemoteAssist.Host.Core.Security;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AdminConfiguration(AdminApproval Approval,uint WindowsSessionId,string UserSid,MonitorDescriptor Monitor,string Mapping,int TargetFps,int MaxLongEdge,int MaxShortEdge,bool InitialSupport=false)
{
    public void ValidateLifetime(DateTimeOffset now)
    {
        var maximum=InitialSupport?TimeSpan.FromHours(8):TimeSpan.FromMinutes(2);
        if(Approval is null||Approval.ExpiresAt<=now||Approval.ExpiresAt-now>maximum)
            throw new UnauthorizedAccessException("관리자 허가가 만료됐거나 허용된 실행 시간을 초과했다.");
    }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AdminInputBatch(long AdminGeneration,InputStroke[] Strokes);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AdminInputResult(int Count);

public static class AdminInputValidation
{
    public static void Validate(AdminInputBatch batch)
    {
        if(batch.AdminGeneration<1||batch.Strokes is null||batch.Strokes.Length is <1 or >64)throw new InvalidDataException("관리자 입력 상한/세대 오류다.");
        foreach(var s in batch.Strokes)
        {
            if(!Enum.IsDefined(s.Kind))throw new InvalidDataException("관리자 입력 종류 오류다.");
            if(s.Kind==StrokeKind.Key&&s.Key is <1 or >254||s.Kind==StrokeKind.Unicode&&s.Character=='\0'||s.Kind==StrokeKind.Button&&s.Button is not ("left" or "right" or "middle")||s.Kind==StrokeKind.Wheel&&s.Delta is < -12000 or >12000)
                throw new InvalidDataException("관리자 입력 값 오류다.");
        }
    }
}
