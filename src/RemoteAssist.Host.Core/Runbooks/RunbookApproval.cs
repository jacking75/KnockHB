using RemoteAssist.Protocol;

namespace RemoteAssist.Host.Core.Runbooks;

/// <summary>Service actor state: a cancelled dialog must acknowledge dismissal before input resumes.</summary>
public sealed class RunbookApproval(TimeProvider? time=null)
{
    readonly TimeProvider clock=time??TimeProvider.System;
    readonly object gate=new();
    RunbookMessage? pending,dismissing,execution;
    bool executionCancelled;
    long began,dismissBegan;
    public bool IsBusy {get{lock(gate)return pending!=null||dismissing!=null||execution!=null;}}
    public bool Expired {get{lock(gate)return pending!=null&&clock.GetElapsedTime(began)>=TimeSpan.FromSeconds(30)||
        dismissing!=null&&clock.GetElapsedTime(dismissBegan)>=TimeSpan.FromSeconds(2);}}
    public void Begin(RunbookMessage request)
    {
        lock(gate){if(IsBusy)throw new InvalidOperationException("Runbook 승인 창이 이미 열려 있다.");
        _=RunbookCatalog.Demand(request);pending=request;began=clock.GetTimestamp();}
    }
    public RunbookMessage? Respond(string id,bool approved=false)
    {
        lock(gate){if(dismissing?.Id==id)return null;
        if(pending?.Id!=id||Expired)throw new UnauthorizedAccessException("현재 Runbook 승인 응답이 아니다.");
        var request=pending;pending=null;if(approved){execution=request;executionCancelled=false;}return request;}
    }
    public bool Cancel(string id)
    {
        lock(gate){if(execution?.Id==id)executionCancelled=true;if(pending?.Id!=id)return false;
        dismissing=pending;pending=null;dismissBegan=clock.GetTimestamp();return true;}
    }
    public RunbookMessage Dismissed(string id)
    {
        lock(gate){if(dismissing?.Id!=id||Expired)throw new UnauthorizedAccessException("현재 Runbook 창 닫기 응답이 아니다.");
        var request=dismissing;dismissing=null;return request;}
    }
    public bool ExecutionCancelled(string id){lock(gate)return execution?.Id==id&&executionCancelled;}
    public void CompleteExecution(string id){lock(gate){if(execution?.Id==id){execution=null;executionCancelled=false;}}}
}
