using RemoteAssist.Host.Core.Capture;
using RemoteAssist.Host.Core.Security;
using RemoteAssist.Protocol;

namespace RemoteAssist.Host.Core.Input;

public enum StrokeKind {Move,Button,Wheel,Key,Unicode}
public readonly record struct InputStroke(StrokeKind Kind,int X=0,int Y=0,string Button="",ushort Key=0,char Character='\0',bool Up=false,int Delta=0);
public interface IInputBackend
{
    /// <returns>Count of the actually injected prefix, including partial failures.</returns>
    int Send(IReadOnlyList<InputStroke> strokes);
}

/// <summary>원격 입력을 순서대로 주입하고, 눌린 키·버튼을 추적해 정지·일시 중단 때 모두 뗀다.</summary>
public sealed class InputSession(PermissionGate permissions,MonitorInfo monitor,long topologyGeneration,IInputBackend backend,long connectionGeneration=1)
{
    readonly object gate=new();
    readonly HashSet<ushort> keys=[];
    readonly HashSet<string> buttons=new(StringComparer.Ordinal);
    readonly HashSet<char> unicode=[];
    InputSequenceGuard? sequences;
    bool stopped;
    bool paused;
    /// <summary>모니터·관리자 모드 전환 중에는 입력을 멈추고 눌린 키를 뗀다.</summary>
    public void Suspend(){lock(gate){paused=true;Release();}}
    public void RebindMonitor(MonitorInfo target,long generation)
    {lock(gate){if(!paused||stopped)throw new InvalidOperationException("모니터 전환 전 입력을 중단해야 한다.");monitor=target;topologyGeneration=generation;sequences?.RebindMonitor(target.DeviceName,generation);}}
    /// <summary>입력을 다시 받는다. 새 epoch를 돌려주며 Viewer는 이 epoch부터 보낸다.</summary>
    public long Resume()
    {
        lock(gate)
        {
            var grant=permissions.Current;
            if(stopped||!paused||grant==null||!permissions.Allows(Capability.Control,grant.Generation))throw new InvalidOperationException("입력 재개 상태 오류다.");
            sequences??=new(grant.SessionId,connectionGeneration,grant.Generation,monitor.DeviceName,topologyGeneration);
            var epoch=sequences.AdvanceLocalPhase();paused=false;
            return epoch;
        }
    }
    public bool Dispatch(string channel,ReadOnlyMemory<byte> bytes)
    {
        var message=ProtocolJson.ParseInput(bytes);
        lock(gate)
        {
            if(stopped||paused||!permissions.Allows(Capability.Control,message.PermissionGeneration))return false;
            var grant=permissions.Current;
            if(grant is null||message.SessionId!=grant.SessionId)return false;
            sequences??=new InputSequenceGuard(grant.SessionId,connectionGeneration,grant.Generation,monitor.DeviceName,topologyGeneration);
            if(!sequences.Accept(channel,message))return false;
            if(message.T=="reset"){Release();return true;}
            var strokes=new List<InputStroke>();
            if(message.T=="mouse")
            {
                var (x,y)=InputCoordinates.Pixel(monitor,message.X,message.Y);strokes.Add(new(StrokeKind.Move,X:x,Y:y));
                if(message.A=="wheel")strokes.Add(new(StrokeKind.Wheel,Delta:message.D!.Value));
                else if(message.A is "down" or "up")
                {
                    if(message.A=="down"||buttons.Contains(message.Btn!))strokes.Add(new(StrokeKind.Button,Button:message.Btn!,Up:message.A=="up"));
                }
            }
            else if(message.A=="text")
                foreach(var character in message.Text!){strokes.Add(new(StrokeKind.Unicode,Character:character));strokes.Add(new(StrokeKind.Unicode,Character:character,Up:true));}
            else if(message.A=="down"||keys.Contains((ushort)message.Vk!.Value))strokes.Add(new(StrokeKind.Key,Key:(ushort)message.Vk!.Value,Up:message.A=="up"));
            return permissions.Execute(Capability.Control,message.PermissionGeneration,()=>Inject(strokes));
        }
    }
    public void Stop()
    {lock(gate){stopped=true;Release();}}
    void Release()
    {
        var release=keys.Select(key=>new InputStroke(StrokeKind.Key,Key:key,Up:true)).Concat(buttons.Select(button=>new InputStroke(StrokeKind.Button,Button:button,Up:true)))
            .Concat(unicode.Select(character=>new InputStroke(StrokeKind.Unicode,Character:character,Up:true))).ToArray();
        Inject(release);
    }
    void Inject(IReadOnlyList<InputStroke> strokes)
    {
        if(strokes.Count==0)return;
        var count=backend.Send(strokes);
        if(count<0||count>strokes.Count)throw new InvalidOperationException("입력 backend 반환 개수 오류다.");
        for(var i=0;i<count;i++)
        {
            var stroke=strokes[i];
            if(stroke.Kind==StrokeKind.Key){if(stroke.Up)keys.Remove(stroke.Key);else keys.Add(stroke.Key);}
            if(stroke.Kind==StrokeKind.Button){if(stroke.Up)buttons.Remove(stroke.Button);else buttons.Add(stroke.Button);}
            if(stroke.Kind==StrokeKind.Unicode){if(stroke.Up)unicode.Remove(stroke.Character);else unicode.Add(stroke.Character);}
        }
        // 보안 데스크톱 전환 순간처럼 일부만 들어간 입력은 건너뛴다. 세션은 유지한다.
    }
}
