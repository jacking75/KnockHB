using System.Text;
using System.Text.Json;
using RemoteAssist.Host.Core.Diagnostics;
using RemoteAssist.Host.Core.Media;
using RemoteAssist.Host.Core.Security;
using RemoteAssist.Protocol;

static class FeatureRegression
{
    static void Check(bool condition,string reason){if(!condition)throw new Exception(reason);}
    static void Reject(Action action){try{action();}catch{return;}throw new Exception("expected rejection did not occur");}
    public static async Task Run()
    {
        foreach(var invalid in new[]{0,2,4,5,256})Reject(()=>FeatureContract.Permissions(invalid));
        foreach(var invalid in new[]{new[]{"chat","screen"},new[]{"diagnostics","diagnostics"},new[]{"unknown"}})Reject(()=>FeatureContract.ParsePermissions(invalid));
        Check(FeatureContract.Channels(32).SequenceEqual(new[]{"control","telemetry"}),"diagnostics only demanded input/video");
        Check(FeatureContract.Channels(144).SequenceEqual(new[]{"control","chat"}),"duplicate clipboard/chat channel");
        var ready=new ControlMessage {T="data-ready",Id="ready",SessionId="sid",Generation=1,PermissionGeneration=1,Sequence=1,Permissions=["diagnostics"],Channels=["control","telemetry"]};
        Check(ControlMessage.Parse(JsonSerializer.SerializeToUtf8Bytes(ready)).Monitor is null,"screen-free ready failed");
        Reject(()=>ControlMessage.Parse(JsonSerializer.SerializeToUtf8Bytes(ready with {Monitor=new("DISPLAY",1,0,0,1920,1080)})));
        var clip=new FeatureMessage {T="clipboard-set",Id=Guid.NewGuid().ToString("N"),SessionId="sid",Generation=1,PermissionGeneration=1,Sequence=1,Text="한글😀",Hash=FeatureMessage.TextHash("한글😀")};
        Check(FeatureMessage.Parse(JsonSerializer.SerializeToUtf8Bytes(clip)).Text==clip.Text,"clipboard Unicode lost");
        var get=clip with {T="clipboard-get",Text=null,Hash=null};
        Check(FeatureMessage.ClipboardTextReply(get,new string('한',5000)).T=="clipboard-text","allowed UTF8 Unicode grew beyond wire budget");
        Check(FeatureMessage.ClipboardTextReply(get,new string('\u0001',8192)).Code=="too-large","JSON escaping bypassed frame limit");
        Check(FeatureMessage.ClipboardTextReply(get,"\ud800").Code=="unavailable","invalid native clipboard text escaped");
        foreach(var invalid in new[]{clip with {Hash=new string('0',64)},clip with {Text=new string('한',8192),Hash=FeatureMessage.TextHash(new string('한',8192))},clip with {Id="other"},clip with {Text="\0",Hash=FeatureMessage.TextHash("\0")},clip with {PermissionGeneration=0}})Reject(()=>FeatureMessage.Parse(JsonSerializer.SerializeToUtf8Bytes(invalid)));
        var encoded=Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(clip));Reject(()=>FeatureMessage.Parse(Encoding.UTF8.GetBytes(encoded.Replace("\"v\":1","\"extra\":true,\"v\":1"))));
        var snapshot=new DiagnosticSnapshot(DateTimeOffset.UtcNow,null,new(100,20),[new("C:",100,20)],new(10,20),[new("Provider",70000,DateTimeOffset.UtcNow,"error")],["entry"],[new("Service","Running")]);
        var message=clip with {T="telemetry",Text=null,Hash=null,Snapshot=snapshot};FeatureMessage.Parse(JsonSerializer.SerializeToUtf8Bytes(message));
        var json=Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(message));Check(json.Contains("\"cpuPercent\":null")&&json.Contains("\"eventId\":70000")&&!json.Contains("TotalBytes"),"diagnostics wire casing or event ID drift");
        foreach(var invalid in new[]{snapshot with {CpuPercent=double.NaN},snapshot with {Services=[new("C:\\secret","Running")]},snapshot with {Memory=new(9007199254740992L,1)},snapshot with {Disks=[null!]}})Reject(invalid.Validate);
        var clock=new Clock();var quota=new FeatureQuota(2,clock);Check(quota.Allow()&&quota.Allow()&&!quota.Allow(),"clipboard quota overflow");clock.Ticks=TimeSpan.TicksPerSecond;Check(quota.Allow(),"quota failed to reopen");
        var permissions=new PermissionGate();var grant=permissions.Grant("sid","peer",Capability.Diagnostics,TimeSpan.FromMinutes(1));
        Check(!permissions.Allows(Capability.Screen,grant.Generation)&&!permissions.Allows(Capability.Clipboard,grant.Generation),"diagnostics implied privilege");
        using var started=new ManualResetEventSlim();using var release=new ManualResetEventSlim();int calls=0,sends=0;
        var provider=new Provider(()=>{Interlocked.Increment(ref calls);started.Set();release.Wait();return snapshot;});
        var collecting=DiagnosticsLoop.CollectOnce(provider,permissions,grant,_=>sends++,CancellationToken.None);
        try{Check(started.Wait(TimeSpan.FromSeconds(1)),"provider never started");permissions.Revoke();}finally{release.Set();}
        Check(!await collecting&&sends==0&&calls==1,"revoked diagnostics escaped");
        Check(!await DiagnosticsLoop.CollectOnce(provider,permissions,grant,_=>sends++,CancellationToken.None)&&calls==1,"revoked provider collected again");
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        try{await DiagnosticsLoop.CollectOnce(provider,permissions,grant,_=>sends++,cancel.Token);throw new Exception("cancel allowed provider");}catch(OperationCanceledException){}
    }
    sealed class Provider(Func<DiagnosticSnapshot> read):IDiagnosticsProvider {public DiagnosticSnapshot Read(CancellationToken ct)=>read();}
    sealed class Clock:TimeProvider {public long Ticks;public override long TimestampFrequency=>TimeSpan.TicksPerSecond;public override long GetTimestamp()=>Ticks;}
}
