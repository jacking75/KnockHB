using System.Text.Json;
using RemoteAssist.Host.Core.Capture;
using RemoteAssist.Host.Core.Input;
using RemoteAssist.Host.Core.Security;
using RemoteAssist.Protocol;

internal static class InputRegression
{
    public static Task Run()
    {
        var permissions=new PermissionGate();var backend=new Sink();var monitor=new MonitorInfo(0,"DISPLAY",0,0,1920,1080,true);
        var input=new InputSession(permissions,monitor,1,backend);
        var message=new InputMsg {SessionId="sid",Id="request",Generation=1,PermissionGeneration=1,Sequence=1,TopologyGeneration=1,InputEpoch=1,MonitorIdentity="DISPLAY",T="key",A="down",Vk=65};
        bool Send(InputMsg m,string channel="input")=>input.Dispatch(channel,JsonSerializer.SerializeToUtf8Bytes(m));
        Require(!Send(message)&&backend.Batches.Count==0,"input before the supporter connected");
        permissions.Grant("sid","viewer");
        Require(Send(message),"key rejected");
        Require(!Send(message),"replayed key injected");
        Require(Send(message with {T="mouse",A="down",Btn="left",Sequence=2,X=1,Y=1}),"button rejected");
        var buttonBatch=backend.Batches[^1];Require(buttonBatch[0]==new InputStroke(StrokeKind.Move,X:1919,Y:1079),"transition lost its own final coordinate");
        Require(Send(message with {T="reset",A="all",Sequence=3,InputEpoch=2}),"reset rejected");
        var released=backend.Batches[^1];Require(released.Length==2&&released.All(s=>s.Up)&&released.Any(s=>s.Kind==StrokeKind.Key&&s.Key==65)&&released.Any(s=>s.Kind==StrokeKind.Button&&s.Button=="left"),"reset omitted remote held transitions");
        Require(!Send(message with {Sequence=4}),"old epoch resurrected input");

        // 관리자 모드·모니터 전환 중에는 입력을 멈춘다. 멈추기 전에 보낸 입력은 새 epoch에서 받지 않는다.
        Require(Send(message with {Sequence=4,InputEpoch=2}),"key before suspend failed");
        input.Suspend();var before=backend.Batches.Count;
        Require(backend.Batches[^1].All(s=>s.Up),"suspend did not release held key");
        Require(!Send(message with {Sequence=5,InputEpoch=2})&&backend.Batches.Count==before,"suspended input injected");
        var epoch=input.Resume();Require(epoch==3,"resume did not advance epoch");
        Require(!Send(message with {Sequence=5,InputEpoch=2}),"queued pre-suspend input injected after resume");
        Require(Send(message with {Sequence=5,InputEpoch=epoch}),"new epoch rejected");

        permissions.End();var count=backend.Batches.Count;
        Require(!Send(message with {Sequence=6,InputEpoch=epoch})&&backend.Batches.Count==count,"input injected after the session ended");

        var ordering=new InputSequenceGuard("sid",1,1,"DISPLAY",1);
        Require(ordering.Accept("input",message with {T="mouse",A="down",Btn="left",PointerBarrier=5}),"mouse barrier rejected");
        Require(!ordering.Accept("pointer",message with {T="mouse",A="move",Sequence=5})&&ordering.Accept("pointer",message with {T="mouse",A="move",Sequence=6}),"old move undid reliable final button coordinate");

        // 일부만 주입된 문자 입력도 눌린 상태로 추적해 정지 때 뗀다.
        var partialPermissions=new PermissionGate();partialPermissions.Grant("sid","viewer");
        var partial=new Sink {NextPrefix=1};var failed=new InputSession(partialPermissions,monitor,1,partial);
        failed.Dispatch("input",JsonSerializer.SerializeToUtf8Bytes(message with {A="text",Text="가"}));
        failed.Stop();Require(partial.Batches.Count==2&&partial.Batches[1].Length==1&&partial.Batches[1][0].Kind==StrokeKind.Unicode&&partial.Batches[1][0].Up,"partial Unicode down was never released");
        failed.Stop();Require(partial.Batches.Count==2,"released Unicode replayed");
        return Task.CompletedTask;
    }
    static void Require(bool value,string reason){if(!value)throw new Exception(reason);}
    sealed class Sink:IInputBackend
    {
        public readonly List<InputStroke[]> Batches=[];
        public int? NextPrefix;
        public int Send(IReadOnlyList<InputStroke> strokes){Batches.Add(strokes.ToArray());var result=NextPrefix??strokes.Count;NextPrefix=null;return result;}
    }
}
