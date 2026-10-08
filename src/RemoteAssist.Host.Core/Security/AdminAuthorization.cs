namespace RemoteAssist.Host.Core.Security;

public sealed record AdminApproval(string RequestId,string SessionId,string ConnectionId,long PermissionGeneration,long AdminGeneration,DateTimeOffset ExpiresAt);

/// <summary>The initial capability permits only a request. A current local second approval is mandatory for privileged work.</summary>
public sealed class AdminAuthorization(PermissionSnapshot binding,TimeProvider? time=null)
{
    readonly object gate=new();
    readonly TimeProvider clock=time??TimeProvider.System;
    string? pending;
    DateTimeOffset deadline;
    AdminApproval? current;
    long generation;
    bool ended;
    public bool PendingExpired {get{lock(gate)return !ended&&pending!=null&&clock.GetUtcNow()>=deadline;}}
    public string Request(string requestId)
    {
        lock(gate)
        {
            DemandBinding();
            if(!Guid.TryParseExact(requestId,"N",out _)||pending!=null||current!=null)throw new InvalidOperationException("관리자 요청 상태 오류다.");
            pending=requestId;deadline=clock.GetUtcNow().AddSeconds(30);return requestId;
        }
    }
    public AdminApproval? RespondLocally(string requestId,bool approved)
    {
        lock(gate)
        {
            DemandBinding();
            if(pending!=requestId||clock.GetUtcNow()>=deadline)throw new UnauthorizedAccessException("만료되거나 다른 관리자 승인이다.");
            pending=null;
            if(!approved)return null;
            return current=new(requestId,binding.SessionId,binding.ConnectionId,binding.Generation,++generation,
                Min(clock.GetUtcNow().AddMinutes(2),binding.ExpiresAt));
        }
    }
    public bool Allows(long adminGeneration)
    {lock(gate)return !ended&&current is {} grant&&grant.AdminGeneration==adminGeneration&&clock.GetUtcNow()<grant.ExpiresAt&&clock.GetUtcNow()<binding.ExpiresAt;}
    public void Revoke(){lock(gate){pending=null;current=null;generation++;}}
    public void End(){lock(gate){ended=true;pending=null;current=null;generation++;}}
    public PermissionSnapshot ProcessGrant(AdminApproval approval)
    {
        lock(gate)
        {
            if(current!=approval||!Allows(approval.AdminGeneration))throw new UnauthorizedAccessException("현재 관리자 승인이 아니다.");
            return binding with {Capabilities=Capability.Screen|Capability.Control|Capability.Admin,ExpiresAt=approval.ExpiresAt};
        }
    }
    void DemandBinding()
    {if(ended||clock.GetUtcNow()>=binding.ExpiresAt||!binding.Capabilities.HasFlag(Capability.Admin)||!binding.Capabilities.HasFlag(Capability.Screen)||!binding.Capabilities.HasFlag(Capability.Control))throw new UnauthorizedAccessException("관리자 요청 범위가 승인되지 않았다.");}
    static DateTimeOffset Min(DateTimeOffset a,DateTimeOffset b)=>a<b?a:b;
}
