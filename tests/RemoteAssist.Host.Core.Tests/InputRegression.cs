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
        Require(!Send(message)&&backend.Batches.Count==0,"input before consent");
        permissions.Grant("sid","viewer",Capability.Screen|Capability.Control,TimeSpan.FromMinutes(1));
        Require(Send(message),"approved key rejected");
        Require(!Send(message),"replayed key injected");
        Require(Send(message with {T="mouse",A="down",Btn="left",Sequence=2,X=1,Y=1}),"button rejected");
        var buttonBatch=backend.Batches[^1];Require(buttonBatch[0]==new InputStroke(StrokeKind.Move,X:1919,Y:1079),"transition lost its own final coordinate");
        Require(Send(message with {T="reset",A="all",Sequence=3,InputEpoch=2}),"reset rejected");
        var released=backend.Batches[^1];Require(released.Length==2&&released.All(s=>s.Up)&&released.Any(s=>s.Kind==StrokeKind.Key&&s.Key==65)&&released.Any(s=>s.Kind==StrokeKind.Button&&s.Button=="left"),"reset omitted remote held transitions");
        Require(!Send(message with {Sequence=4}),"old epoch resurrected input");
        permissions.Revoke();var count=backend.Batches.Count;Require(!Send(message with {Sequence=4,InputEpoch=2})&&backend.Batches.Count==count,"revoked input injected");
        input.Stop();Require(backend.Batches.Count==count,"stop released unowned physical keys");

        var ordering=new InputSequenceGuard("sid",1,1,"DISPLAY",1);
        Require(ordering.Accept("input",message with {T="mouse",A="down",Btn="left",PointerBarrier=5}),"mouse barrier rejected");
        Require(!ordering.Accept("pointer",message with {T="mouse",A="move",Sequence=5})&&ordering.Accept("pointer",message with {T="mouse",A="move",Sequence=6}),"old move undid reliable final button coordinate");

        var partialPermissions=new PermissionGate();partialPermissions.Grant("sid","viewer",Capability.Screen|Capability.Control,TimeSpan.FromMinutes(1));
        var partial=new Sink {NextPrefix=1};var failed=new InputSession(partialPermissions,monitor,1,partial);
        try{failed.Dispatch("input",JsonSerializer.SerializeToUtf8Bytes(message with {A="text",Text="가"}));throw new Exception("partial success accepted");}catch(IOException){}
        failed.Stop();Require(partial.Batches.Count==2&&partial.Batches[1].Length==1&&partial.Batches[1][0].Kind==StrokeKind.Unicode&&partial.Batches[1][0].Up,"partial Unicode down was never released");
        failed.Stop();Require(partial.Batches.Count==2,"released Unicode replayed");
        var localPermissions=new PermissionGate();localPermissions.Grant("sid","viewer",Capability.Screen|Capability.Control,TimeSpan.FromMinutes(1));
        var local=new LocalHeld();var conflict=new InputSession(localPermissions,monitor,1,local);
        try{conflict.Dispatch("input",JsonSerializer.SerializeToUtf8Bytes(message));throw new Exception("local held key claimed remotely");}catch(IOException){}
        conflict.Stop();Require(local.Writes==0,"local physical key was injected or released");
        var approvalPermissions=new PermissionGate();approvalPermissions.Grant("sid","viewer",Capability.Screen|Capability.Control|Capability.Admin,TimeSpan.FromMinutes(1));
        var approvalSink=new Sink();var approvalInput=new InputSession(approvalPermissions,monitor,1,approvalSink);
        Require(approvalInput.Dispatch("input",JsonSerializer.SerializeToUtf8Bytes(message)),"key before admin request failed");
        approvalInput.SuspendForApproval();var before=approvalSink.Batches.Count;
        Require(!approvalInput.Dispatch("input",JsonSerializer.SerializeToUtf8Bytes(message with {Sequence=2}))&&approvalSink.Batches.Count==before,"remote input manipulated local approval dialog");
        var phase=approvalInput.ResumeLocalPhase();Require(phase==2,"local approval did not advance epoch");
        Require(!approvalInput.Dispatch("input",JsonSerializer.SerializeToUtf8Bytes(message with {Sequence=2})),"queued pre-approval input gained admin authority");
        Require(approvalInput.Dispatch("input",JsonSerializer.SerializeToUtf8Bytes(message with {Sequence=2,InputEpoch=phase})),"new approval epoch rejected");approvalInput.Stop();
        return Task.CompletedTask;
    }
    static void Require(bool value,string reason){if(!value)throw new Exception(reason);}
    sealed class Sink:IInputBackend
    {
        public readonly List<InputStroke[]> Batches=[];
        public int? NextPrefix;
        public int Send(IReadOnlyList<InputStroke> strokes){Batches.Add(strokes.ToArray());var result=NextPrefix??strokes.Count;NextPrefix=null;return result;}
    }
    sealed class LocalHeld:IInputBackend,IInputPreflight
    {
        public int Writes;public bool CanPress(InputStroke stroke)=>false;
        public int Send(IReadOnlyList<InputStroke> strokes){Writes++;return strokes.Count;}
    }
}
