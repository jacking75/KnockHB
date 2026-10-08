using RemoteAssist.Host.Core.Input;
using RemoteAssist.Host.Core.Media;

namespace RemoteAssist.Host.Core.Security;

/// <summary>General helper reads privileged BGRA and forwards only bounded native input to the authenticated supervisor.</summary>
public sealed class AdminFrameSource:IDisposable
{
    readonly SharedFrameMap mapping;
    readonly Func<AdminInputBatch,int> send;
    long frameSequence;
    public AdminApproval Approval {get;}
    public bool IsCurrent=>DateTimeOffset.UtcNow<Approval.ExpiresAt;
    public AdminFrameSource(AdminApproval approval,string mapping,Func<AdminInputBatch,int> send)
    {Approval=approval;this.send=send;this.mapping=SharedFrameMap.Open(mapping,false);}
    public bool TryRead(long topology,out SharedFrame? frame)
    {
        if(!IsCurrent)throw new UnauthorizedAccessException("관리자 화면 승인이 만료됐다.");
        if(!mapping.TryRead(frameSequence,Approval.PermissionGeneration,topology,Approval.AdminGeneration,out frame))return false;
        if(Environment.TickCount64-frame!.TimestampMs is <0 or >1000){Array.Clear(frame.Pixels);throw new InvalidDataException("이전 관리자 프레임이다.");}
        frameSequence=frame.Sequence;return true;
    }
    public int Send(IReadOnlyList<InputStroke> strokes)
    {
        var count=0;
        foreach(var part in strokes.Chunk(64))
        {
            if(!IsCurrent)throw new UnauthorizedAccessException("관리자 입력 승인이 만료됐다.");
            var actual=send(new(Approval.AdminGeneration,part));if(actual<0||actual>part.Length)throw new InvalidDataException("관리자 주입 개수 오류다.");
            count+=actual;if(actual!=part.Length)break;
        }
        return count;
    }
    public void Dispose()=>mapping.Dispose();
}

public sealed class AdminRoutingInputBackend(IInputBackend ordinary,Func<AdminFrameSource?> current):IInputBackend,IInputPreflight
{
    public bool CanPress(InputStroke stroke)=>current() is not null||ordinary is not IInputPreflight preflight||preflight.CanPress(stroke);
    public int Send(IReadOnlyList<InputStroke> strokes)=>current() is {} privileged?privileged.Send(strokes):ordinary.Send(strokes);
}
