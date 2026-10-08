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
public interface IInputPreflight {bool CanPress(InputStroke stroke);}

/// <summary>All validation and native writes are serialized with revocation. Tracks only its own successfully injected transitions.</summary>
public sealed class InputSession(PermissionGate permissions,MonitorInfo monitor,long topologyGeneration,IInputBackend backend)
{
    readonly object gate=new();
    readonly HashSet<ushort> keys=[];
    readonly HashSet<string> buttons=new(StringComparer.Ordinal);
    readonly HashSet<char> unicode=[];
    InputSequenceGuard? sequences;
    bool stopped;
    bool paused;
    public void SuspendForApproval(){lock(gate){paused=true;Release();}}
    public long ResumeLocalPhase()
    {
        lock(gate)
        {
            var grant=permissions.Current;
            if(stopped||!paused||grant==null||!permissions.Allows(Capability.Control,grant.Generation))throw new UnauthorizedAccessException("입력 승인 전환 상태 오류다.");
            sequences??=new(grant.SessionId,1,grant.Generation,monitor.DeviceName,topologyGeneration);
            var epoch=sequences.AdvanceLocalPhase();paused=false;return epoch;
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
            sequences??=new InputSequenceGuard(grant.SessionId,1,grant.Generation,monitor.DeviceName,topologyGeneration);
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
            return permissions.Execute(Capability.Control,message.PermissionGeneration,()=>
            {
                if(backend is IInputPreflight preflight)
                    foreach(var stroke in strokes.Where(s=>!s.Up&&s.Kind is StrokeKind.Key or StrokeKind.Button or StrokeKind.Unicode))
                    {
                        var owned=stroke.Kind switch {StrokeKind.Key=>keys.Contains(stroke.Key),StrokeKind.Button=>buttons.Contains(stroke.Button),_=>unicode.Contains(stroke.Character)};
                        if(!owned&&!preflight.CanPress(stroke)){stopped=true;throw new IOException("로컬 사용자가 이미 누른 키·버튼과 충돌했다.");}
                    }
                Inject(strokes);
            });
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
        if(count!=strokes.Count){stopped=true;throw new IOException("SendInput 일부 또는 전체 실패다. 보안 경계와 키 복원을 확인해야 한다.");}
    }
}
