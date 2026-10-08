using RemoteAssist.Host.Core.Input;

internal static class RecoveryRegression
{
    public static Task Run()
    {
        var sink=new Sink();var ledger=new RecoveryLedger("session",1,sink);
        var down=new RecoveryReservation("session",1,1,[65],["left"],[0xAC00]);
        ledger.Reserve(down);
        Reject(()=>ledger.Reserve(down));Reject(()=>ledger.Reserve(down with {Revision=2,SessionId="other"}));
        Reject(()=>ledger.Reserve(down with {Revision=2,Keys=[65,65]}));Reject(()=>ledger.Reserve(down with {Revision=2,Buttons=["extra"]}));
        sink.Prefix=1;Reject(()=>ledger.Release());Require(ledger.Count==2,"partial restoration lost pending ups");
        ledger.Release();Require(ledger.Count==0&&sink.Batches.SelectMany(x=>x).All(s=>s.Up),"restoration generated down or omitted up");
        ledger.Release();Require(sink.Batches.Count==2,"restored ups were replayed");Reject(()=>ledger.Reserve(down with {Revision=2}));

        var native=new Sink();var watchdog=new Sink();var reserved=new RecoveryLedger("session",1,watchdog);
        var before=new ReservedInputBackend("session",1,native,r=>{reserved.Reserve(r);Require(native.Batches.Count==0||r.Revision>1,"native down preceded ACK");});
        before.Send([new(StrokeKind.Key,Key:66)]);reserved.Release();Require(watchdog.Batches[0][0].Key==66,"crash after down lost recovery");
        var blocked=new Sink();var noAck=new ReservedInputBackend("session",1,blocked,_=>throw new IOException());Reject(()=>noAck.Send([new(StrokeKind.Key,Key:65)]));Require(blocked.Batches.Count==0,"failed ACK still injected");

        var unicodeSink=new Sink {Prefix=3};var unicodeRecovery=new Sink();var unicodeLedger=new RecoveryLedger("session",1,unicodeRecovery);
        var wrapper=new ReservedInputBackend("session",1,unicodeSink,unicodeLedger.Reserve);
        wrapper.Send([new(StrokeKind.Unicode,Character:'가'),new(StrokeKind.Unicode,Character:'가',Up:true),new(StrokeKind.Unicode,Character:'가'),new(StrokeKind.Unicode,Character:'가',Up:true)]);
        unicodeLedger.Release();Require(unicodeRecovery.Batches[0] is [{Kind:StrokeKind.Unicode,Up:true,Character:'가'}],"same Unicode repeated partial prefix erased a held down");
        Reject(()=>new WindowsRecoveryBackend(1,"invalid").Send([new(StrokeKind.Key,Key:65)]));
        return Task.CompletedTask;
    }
    static void Reject(Action action){try{action();}catch(Exception ex)when(ex is InvalidDataException or IOException or UnauthorizedAccessException){return;}throw new Exception("invalid recovery accepted");}
    static void Require(bool value,string reason){if(!value)throw new Exception(reason);}
    sealed class Sink:IInputBackend
    {
        public readonly List<InputStroke[]> Batches=[];public int? Prefix;
        public int Send(IReadOnlyList<InputStroke> strokes){Batches.Add(strokes.ToArray());var count=Prefix??strokes.Count;Prefix=null;return count;}
    }
}
