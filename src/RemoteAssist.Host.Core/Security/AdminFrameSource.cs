using RemoteAssist.Host.Core.Input;
using RemoteAssist.Host.Core.Media;

namespace RemoteAssist.Host.Core.Security;

/// <summary>일반 Helper가 관리자 헬퍼의 BGRA 프레임을 읽고, 입력을 감독 서비스를 거쳐 관리자 헬퍼로 보낸다.</summary>
public sealed class AdminFrameSource:IDisposable
{
    /// <summary>공유 mapping의 프레임 키. 모니터를 바꿔도 같은 mapping을 쓰므로 고정이다.</summary>
    public const long MappingTopology=1;
    readonly SharedFrameMap mapping;
    readonly Func<AdminInputBatch,int> send;
    long frameSequence;
    int disposed;
    public AdminApproval Approval {get;}
    public bool IsCurrent=>Volatile.Read(ref disposed)==0;
    public AdminFrameSource(AdminApproval approval,string mapping,Func<AdminInputBatch,int> send)
    {Approval=approval;this.send=send;this.mapping=SharedFrameMap.Open(mapping,false);}
    public bool TryRead(out SharedFrame? frame)
    {
        frame=null;
        if(!IsCurrent||!mapping.TryRead(frameSequence,Approval.PermissionGeneration,MappingTopology,Approval.AdminGeneration,out frame))return false;
        if(Environment.TickCount64-frame!.TimestampMs is <0 or >1000){Array.Clear(frame.Pixels);frame=null;return false;}
        frameSequence=frame.Sequence;return true;
    }
    public int Send(IReadOnlyList<InputStroke> strokes)
    {
        var count=0;
        foreach(var part in strokes.Chunk(64))
        {
            var actual=send(new(Approval.AdminGeneration,part));if(actual<0||actual>part.Length)throw new InvalidDataException("관리자 주입 개수 오류다.");
            count+=actual;if(actual!=part.Length)break;
        }
        return count;
    }
    public void Dispose(){if(Interlocked.Exchange(ref disposed,1)==0)mapping.Dispose();}
}

/// <summary>관리자 모드에서는 관리자 헬퍼(SYSTEM, 사용자 세션)가 입력을 주입한다. 상승 창·UAC 화면에도 들어간다.</summary>
public sealed class AdminRoutingInputBackend(IInputBackend ordinary,Func<AdminFrameSource?> current):IInputBackend
{
    public int Send(IReadOnlyList<InputStroke> strokes)=>current() is {} privileged?privileged.Send(strokes):ordinary.Send(strokes);
}
