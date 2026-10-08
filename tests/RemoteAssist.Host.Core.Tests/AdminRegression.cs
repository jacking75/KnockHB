using RemoteAssist.Host.Core.Security;
using RemoteAssist.Host.Core.Media;
using RemoteAssist.Host.Core.Input;
using RemoteAssist.Protocol;
using System.Text.Json;

internal static class AdminRegression
{
    public static Task Run()
    {
        var time=new Clock();var binding=new PermissionSnapshot("session","viewer",1,Capability.Screen|Capability.Control|Capability.Admin,time.GetUtcNow().AddHours(1));
        var admin=new AdminAuthorization(binding,time);var id=Guid.NewGuid().ToString("N");
        if(admin.Allows(1))throw new Exception("initial toggle granted privilege");
        admin.Request(id);Reject(()=>admin.RespondLocally(Guid.NewGuid().ToString("N"),true));
        var approval=admin.RespondLocally(id,true)!;
        if(!admin.Allows(approval.AdminGeneration)||admin.ProcessGrant(approval).ExpiresAt>time.GetUtcNow().AddMinutes(2))throw new Exception("local lease not bound");
        Reject(()=>admin.RespondLocally(id,true));admin.Revoke();Reject(()=>admin.ProcessGrant(approval));
        admin.Request(Guid.NewGuid().ToString("N"));admin.End();Reject(()=>admin.Request(Guid.NewGuid().ToString("N")));
        var expired=new AdminAuthorization(binding,time);expired.Request(id);time.Now=time.Now.AddSeconds(30);Reject(()=>expired.RespondLocally(id,true));
        var denied=new AdminAuthorization(binding with {Capabilities=Capability.Screen|Capability.Control},time);Reject(()=>denied.Request(id));
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
    sealed class Clock:TimeProvider
    {public DateTimeOffset Now=new(2026,10,8,0,0,0,TimeSpan.Zero);public override DateTimeOffset GetUtcNow()=>Now;}
}
