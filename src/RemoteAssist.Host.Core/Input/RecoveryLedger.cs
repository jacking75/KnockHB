using System.Text.Json.Serialization;

namespace RemoteAssist.Host.Core.Input;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RecoveryReservation(string SessionId,long PermissionGeneration,long Revision,ushort[] Keys,string[] Buttons,ushort[] Unicode);

/// <summary>Potential down transitions must be acknowledged before native injection. Never records text in diagnostics.</summary>
public sealed class RecoveryLedger(string sessionId,long permissionGeneration,IInputBackend backend)
{
    readonly object gate=new();
    readonly HashSet<ushort> keys=[];
    readonly HashSet<string> buttons=new(StringComparer.Ordinal);
    readonly HashSet<ushort> unicode=[];
    long revision;
    bool ended;
    public int Count {get{lock(gate)return keys.Count+buttons.Count+unicode.Count;}}
    public void Reserve(RecoveryReservation reservation)
    {
        lock(gate)
        {
            if(ended||reservation.SessionId!=sessionId||reservation.PermissionGeneration!=permissionGeneration||reservation.Revision!=revision+1||
                reservation.Keys is null||reservation.Buttons is null||reservation.Unicode is null||reservation.Keys.Length>254||reservation.Buttons.Length>3||reservation.Unicode.Length>4096||
                reservation.Keys.Any(k=>k is <1 or >254)||reservation.Buttons.Any(b=>b is not ("left" or "right" or "middle"))||reservation.Unicode.Any(c=>c==0)||
                reservation.Keys.Distinct().Count()!=reservation.Keys.Length||reservation.Buttons.Distinct().Count()!=reservation.Buttons.Length||reservation.Unicode.Distinct().Count()!=reservation.Unicode.Length)
                throw new InvalidDataException("복원 예약 바인딩/순번/상한 오류다.");
            keys.Clear();keys.UnionWith(reservation.Keys);buttons.Clear();buttons.UnionWith(reservation.Buttons);unicode.Clear();unicode.UnionWith(reservation.Unicode);revision++;
        }
    }
    public void Release()
    {
        lock(gate)
        {
            ended=true;
            var strokes=keys.Select(k=>new InputStroke(StrokeKind.Key,Key:k,Up:true)).Concat(buttons.Select(b=>new InputStroke(StrokeKind.Button,Button:b,Up:true)))
                .Concat(unicode.Select(c=>new InputStroke(StrokeKind.Unicode,Character:(char)c,Up:true))).ToArray();
            if(strokes.Length==0)return;
            var count=backend.Send(strokes);
            if(count<0||count>strokes.Length)throw new InvalidOperationException("복원 주입 개수 오류다.");
            foreach(var stroke in strokes.Take(count))
                if(stroke.Kind==StrokeKind.Key)keys.Remove(stroke.Key);else if(stroke.Kind==StrokeKind.Button)buttons.Remove(stroke.Button);else unicode.Remove(stroke.Character);
            if(count!=strokes.Length)throw new IOException("원격 키 복원 일부 실패다.");
        }
    }
}

/// <summary>Serial native backend wrapper. No native down is sent until the independent ledger acknowledges its reservation.</summary>
public sealed class ReservedInputBackend(string sessionId,long permissionGeneration,IInputBackend backend,Action<RecoveryReservation> acknowledge):IInputBackend,IInputPreflight
{
    public bool CanPress(InputStroke stroke)=>backend is not IInputPreflight preflight||preflight.CanPress(stroke);
    readonly HashSet<ushort> keys=[];readonly HashSet<string> buttons=new(StringComparer.Ordinal);readonly HashSet<ushort> unicode=[];
    long revision;
    public int Send(IReadOnlyList<InputStroke> strokes)
    {
        if(strokes.All(s=>s.Kind is StrokeKind.Move or StrokeKind.Wheel))return backend.Send(strokes);
        foreach(var stroke in strokes.Where(s=>!s.Up))Record(stroke,false);
        Publish(); // If ACK fails, no native write is allowed. Potential downs remain recoverable.
        var count=backend.Send(strokes);
        if(count<0||count>strokes.Count)throw new InvalidOperationException("주입 개수 오류다.");
        foreach(var stroke in strokes.Take(count))Record(stroke,stroke.Up);
        Publish();return count;
    }
    void Record(InputStroke s,bool remove)
    {
        if(s.Kind==StrokeKind.Key){if(remove)keys.Remove(s.Key);else keys.Add(s.Key);}
        if(s.Kind==StrokeKind.Button){if(remove)buttons.Remove(s.Button);else buttons.Add(s.Button);}
        if(s.Kind==StrokeKind.Unicode){if(remove)unicode.Remove(s.Character);else unicode.Add(s.Character);}
    }
    void Publish()=>acknowledge(new(sessionId,permissionGeneration,++revision,keys.ToArray(),buttons.ToArray(),unicode.ToArray()));
}
