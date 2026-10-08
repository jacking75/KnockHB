using System.Security.Principal;
using System.Text.Json;
using RemoteAssist.Host.Core;
using RemoteAssist.Host.Core.Ipc;
using RemoteAssist.Host.Core.Runbooks;
try
{
    var bootstrap=IpcBootstrap.Parse(args);using var lifetime=new CancellationTokenSource();using var identity=WindowsIdentity.GetCurrent();
    await using var channel=await LocalIpcEndpoint.ConnectAsync(bootstrap.Pipe,bootstrap.Nonce,bootstrap.ServicePid,identity.IsSystem?"adminrunbookworker":"runbookworker",lifetime.Token);
    var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);int configured=0;IpcPeerSession? peer=null;
    await using(peer=new(channel,(message,ct)=>
    {
        if(message.Type!="runbook-config"||Interlocked.Exchange(ref configured,1)!=0)throw new InvalidDataException("Runbook worker 명령 오류다.");
        var config=message.Body?.Deserialize<RunbookConfiguration>()??throw new InvalidDataException("Runbook 설정 누락이다.");
        _=Task.Run(async()=>{try{var result=await RunbookExecution.Execute(config,ct);ct.ThrowIfCancellationRequested();peer!.Post("runbook-result",result,message.Id);}catch{lifetime.Cancel();}finally{done.TrySetResult();}});return Task.CompletedTask;
    },lifetime.Token,onStopped:_=>lifetime.Cancel()))
    {peer.Start();await Task.WhenAny(peer.Completion,Task.Delay(TimeSpan.FromSeconds(25),lifetime.Token));}
    return configured==1&&done.Task.IsCompleted?0:1;
}
catch(Exception ex){LocalDiagnostic.Failure("runbook-worker",ex);return 1;}
