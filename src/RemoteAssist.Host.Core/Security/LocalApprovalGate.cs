namespace RemoteAssist.Host.Core.Security;

/// <summary>One local approval UI at a time. A remote input grant never authorizes clicking its own consent.</summary>
public sealed class LocalApprovalGate
{
    readonly object gate=new();string? current,kind;bool ended;
    public bool TryBegin(string requestId,string approvalKind)
    {
        if(!Guid.TryParseExact(requestId,"N",out var id)||id.ToString("N")!=requestId||approvalKind is not ("admin" or "file" or "runbook"))throw new InvalidDataException("로컬 승인 요청 바인딩 오류다.");
        lock(gate){if(ended||current!=null)return false;current=requestId;kind=approvalKind;return true;}
    }
    public bool Complete(string requestId,string approvalKind)
    {lock(gate){if(ended)return false;if(current!=requestId||kind!=approvalKind)throw new UnauthorizedAccessException("다른 로컬 승인 응답이다.");current=kind=null;return true;}}
    public void End(){lock(gate){ended=true;current=kind=null;}}
}
