using RemoteAssist.Host.Core.Security;
using RemoteAssist.Host.Core.Media;
using RemoteAssist.Host.Core.Input;
using RemoteAssist.Protocol;
using System.Text.Json;

internal static class AdminRegression
{
    public static Task Run()
    {
        // 관리자 세대: 끝난 세션이나 이전 세대의 관리자 입력은 받지 않는다.
        var binding=new PermissionSnapshot("session","viewer",1,Capability.All);
        var admin=new AdminAuthorization(binding);var id=Guid.NewGuid().ToString("N");
        if(admin.Allows(1))throw new Exception("admin generation before request");
        var approval=admin.Authorize(id);
        if(!admin.Allows(approval.AdminGeneration)||admin.Allows(approval.AdminGeneration+1)||approval.SessionId!="session"||approval.ConnectionId!="viewer")throw new Exception("admin generation not bound");
        Reject(()=>admin.Authorize(Guid.NewGuid().ToString("N")));
        admin.End();if(admin.Allows(approval.AdminGeneration))throw new Exception("ended session kept admin input");
        Reject(()=>admin.Authorize(Guid.NewGuid().ToString("N")));
        var control=new ControlMessage {T="admin-state",SessionId="session",Generation=1,PermissionGeneration=1,Sequence=1,Id=id,State="active",AdminGeneration=1,InputEpoch=2};
        _=ControlMessage.Parse(JsonSerializer.SerializeToUtf8Bytes(control));
        Reject(()=>ControlMessage.Parse(JsonSerializer.SerializeToUtf8Bytes(control with {InputEpoch=null})));
        Reject(()=>ControlMessage.Parse(JsonSerializer.SerializeToUtf8Bytes(control with {State="pending"})));
        Reject(()=>AdminInputValidation.Validate(new(1,[new(StrokeKind.Key,Key:0)])));
        Reject(()=>AdminInputValidation.Validate(new(1,Enumerable.Repeat(new InputStroke(StrokeKind.Key,Key:65),65).ToArray())));

        using var map=SharedFrameMap.CreateAnonymousForTests();
        var pixels=Enumerable.Range(0,64).Select(x=>(byte)x).ToArray();
        if(map.TryRead(0,1,1,1,out _))throw new Exception("empty frame exposed");
        map.Publish(pixels,4,4,16,1,1,1,false,5);
        if(!map.TryRead(0,1,1,1,out var frame)||!frame!.Pixels.SequenceEqual(pixels)||frame.Sequence!=1)throw new Exception("mapping changed pixels or generation");
        if(map.TryRead(1,1,1,1,out _)||map.TryRead(0,1,1,2,out _))throw new Exception("stale frame exposed");
        Reject(()=>map.Publish(pixels,4,4,15,1,1,1,true,6));
        map.Publish(pixels,4,4,16,1,1,2,true,6);
        if(!map.TryRead(1,1,1,2,out frame)||!frame!.SecureDesktop||frame.Sequence!=2)throw new Exception("latest secure frame absent");
        map.Clear();if(map.TryRead(0,1,1,2,out _))throw new Exception("cleared secure frame exposed");
        return Task.CompletedTask;
    }
    static void Reject(Action action)
    {try{action();}catch(Exception ex)when(ex is UnauthorizedAccessException or InvalidOperationException or InvalidDataException){return;}throw new Exception("invalid admin/mapping accepted");}
}
