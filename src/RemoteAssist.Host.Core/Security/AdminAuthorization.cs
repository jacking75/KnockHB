namespace RemoteAssist.Host.Core.Security;

public sealed record AdminApproval(string RequestId,string SessionId,string ConnectionId,long PermissionGeneration,long AdminGeneration);

/// <summary>
/// 관리자 모드 세대를 발급한다. 지원자는 상호 신뢰 관계이므로 요청하면 바로 허가한다.
/// 세대 번호는 이전 관리자 헬퍼의 늦은 입력이 새 헬퍼로 섞이지 않게 하는 용도다.
/// </summary>
public sealed class AdminAuthorization(PermissionSnapshot binding)
{
    readonly object gate=new();
    AdminApproval? current;
    long generation;
    bool ended;
    public AdminApproval Authorize(string requestId)
    {
        lock(gate)
        {
            if(ended)throw new InvalidOperationException("종료한 지원 세션이다.");
            if(!Guid.TryParseExact(requestId,"N",out _)||current!=null)throw new InvalidOperationException("관리자 요청 상태 오류다.");
            return current=new(requestId,binding.SessionId,binding.ConnectionId,binding.Generation,++generation);
        }
    }
    public bool Allows(long adminGeneration){lock(gate)return !ended&&current is {} grant&&grant.AdminGeneration==adminGeneration;}
    public void End(){lock(gate){ended=true;current=null;generation++;}}
}
