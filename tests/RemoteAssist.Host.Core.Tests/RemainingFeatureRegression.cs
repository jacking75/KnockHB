using System.Runtime.InteropServices;
using System.Text;
using RemoteAssist.Host.Core.Capture;
using RemoteAssist.Host.Core.Files;
using RemoteAssist.Host.Core.Runbooks;
using RemoteAssist.Host.Core.Tunnel;
using RemoteAssist.Protocol;

static class RemainingFeatureRegression
{
    static void Check(bool value,string reason){if(!value)throw new Exception(reason);}
    static void Reject(Action action){try{action();}catch(Exception e)when(e is InvalidDataException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException){return;}throw new Exception("invalid operation accepted");}
    public static Task CaptureSelection()
    {
        var target=new MonitorInfo(0,"DISPLAY",0,0,1280,720,true,1);var secondary=0;
        using(var capture=ScreenCaptureFactory.Create(target,()=>true,_=>throw new COMException("injected WGC init failure"),m=>{secondary++;return new FakeCapture(m);},_=>true))
            Check(capture.Monitor==target&&secondary==1,"WGC initialization did not select the same DXGI output");
        var attempts=0;
        Reject(()=>ScreenCaptureFactory.Create(target,()=>true,_=>{attempts++;throw new COMException();},_=>{secondary++;return new FakeCapture(target);},_=>attempts==0));
        Check(secondary==1,"changed monitor fell back without approval");
        Reject(()=>ScreenCaptureFactory.Create(target,()=>true,_=>throw new UnauthorizedAccessException(),_=>{secondary++;return new FakeCapture(target);},_=>true));
        Check(secondary==1,"permission failure was retried with another backend");
        var wrong=new FakeCapture(target with {DeviceName="OTHER"});
        Reject(()=>ScreenCaptureFactory.Create(target,()=>true,_=>wrong,_=>throw new Exception("unexpected fallback"),_=>true));
        Check(wrong.Disposed,"unapproved backend output leaked");
        try{ScreenCaptureFactory.Create(target,()=>true,_=>throw new COMException(),_=>throw new COMException(),_=>true);throw new Exception("all-fail succeeded");}
        catch(AggregateException ex){Check(ex.InnerExceptions.Count==2,"capture failures lost their causes");}
        return Task.CompletedTask;
    }
    public static async Task FileAuditTermination()
    {
        var ledger=new FileAuditLedger();var entries=new List<FileAuditNotice>();
        var first=new FileAuditNotice(Guid.NewGuid().ToString("N"),"sid",1,"started","to-host",10,"allowed");
        var second=first with {Id=Guid.NewGuid().ToString("N"),Direction="from-host",Size=0};
        ledger.Record(first,entries.Add);ledger.Record(second,entries.Add);
        Reject(()=>ledger.Record(first,entries.Add));
        Reject(()=>ledger.Record(first with {Phase="finished",Outcome="completed",Size=9},entries.Add));
        ledger.Record(first with {Phase="finished",Outcome="completed"},entries.Add);
        ledger.End(entries.Add);ledger.End(entries.Add);
        Check(entries.Count==4&&entries[^1]==second with {Phase="finished",Outcome="stopped"},"helper death lost or duplicated unfinished audit");
        Reject(()=>ledger.Record(second,entries.Add));
        // Stop can run concurrently with a durable start; it must follow that start and close it exactly once.
        var raced=new FileAuditLedger();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release=new ManualResetEventSlim();var ordered=new List<string>();
        var writing=Task.Run(()=>raced.Record(first,n=>{entered.TrySetResult();if(!release.Wait(TimeSpan.FromSeconds(3)))throw new TimeoutException();ordered.Add(n.Phase);}));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var ending=Task.Run(()=>raced.End(n=>ordered.Add(n.Phase)));release.Set();
        await Task.WhenAll(writing,ending).WaitAsync(TimeSpan.FromSeconds(3));
        Check(ordered.SequenceEqual(new[]{"started","finished"}),"stop overtook durable start");
    }
    public static Task RemoteCommand()
    {
        var script="Get-Service | Select-Object -First 3";
        var request=new RunbookMessage {T="runbook-request",Id=Guid.NewGuid().ToString("N"),SessionId="sid",Generation=1,PermissionGeneration=1,Sequence=1,BookId=RunbookMessage.CommandBook,BookVersion=1,Hash=RunbookMessage.ScriptHash(script),Args=new(){["script"]=script,["admin"]="true"}};
        var parsed=RunbookMessage.Parse(ProtocolJson.Encode(request));
        Check(parsed.IsCommand&&parsed.CommandAsAdmin,"remote command lost its target account");
        var book=RunbookCatalog.Demand(parsed);Check(book.Source==script&&book.RequiresAdmin,"remote command definition drift");
        Check(!Encoding.UTF8.GetString(ProtocolJson.Encode(request)).Contains("IsCommand"),"computed property leaked to the wire");
        foreach(var invalid in new[]{request with {Hash=new string('0',64)},request with {Args=new(){["script"]=script}},request with {Args=new(){["script"]="",["admin"]="false"},Hash=RunbookMessage.ScriptHash("")},request with {Args=new(){["script"]=script,["admin"]="yes"}}})
            Reject(()=>RunbookMessage.Parse(ProtocolJson.Encode(invalid)));
        return Task.CompletedTask;
    }
    public static Task StrictCredentials()
    {
        var json="{\"sessionId\":\""+new string('a',32)+"\",\"hostToken\":\""+new string('b',32)+"\",\"code\":\"012345678\",\"expiresIn\":600}";
        var credentials=SessionCredentials.Parse(Encoding.UTF8.GetBytes(json));Check(credentials.Code=="012345678","leading zero lost");
        foreach(var value in new[]{json.Replace("600}","600,\"expiresIn\":700}"),json.Replace("600}","600,\"unknown\":0}"),json.Replace("\"012345678\"","null"),json.Replace("600}","600,\"pin\":\"0123\"}"),json.Replace("expiresIn","ExpiresIn"),json.Replace(new string('a',32),new string('!',32)),"{}"})
            Reject(()=>SessionCredentials.Parse(Encoding.UTF8.GetBytes(value)));
        foreach(var value in new[]{"{\"v\":1,\"type\":\"ice\",\"ice\":{\"candidate\":\"candidate:1\",\"extra\":1}}","{\"v\":1,\"type\":\"joined\",\"iceServers\":[{\"urls\":[\"turn:host\"],\"extra\":1}]}"})
            Reject(()=>TunnelFraming.Parse(Encoding.UTF8.GetBytes(value)));
        Check(RemoteAssist.Host.Core.Deployment.DeviceId.Valid("012345678")&&!RemoteAssist.Host.Core.Deployment.DeviceId.Valid("12345678")&&!RemoteAssist.Host.Core.Deployment.DeviceId.Valid("12345678a"),"device id validation");
        return Task.CompletedTask;
    }
    sealed class FakeCapture(MonitorInfo monitor):IScreenCapture
    {public bool Disposed;public MonitorInfo Monitor=>monitor;public bool TryGetFrame(TimeSpan timeout,out CapturedFrame frame){frame=default;return false;}public void Dispose()=>Disposed=true;}
}
