using System.Security.Cryptography;
using System.Text.Json;
using RemoteAssist.Host.Core.Ipc;
using RemoteAssist.Host.Core.Runbooks;
using RemoteAssist.Host.Core.Security;
using RemoteAssist.Host.Core.Windows;
using RemoteAssist.Protocol;
namespace RemoteAssist.Host.Service;

internal sealed class RunbookScope:IDisposable
{
    readonly object gate=new();SessionProcess? child;bool disposed;
    public async Task<RunbookResult> Run(RunbookMessage request,uint session,string sid,CancellationToken ct)
    {
        var book=RunbookCatalog.Demand(request);var name=Guid.NewGuid().ToString("N");var nonce=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(25));
        using var cancel=timeout.Token.Register(Dispose);using var pipe=LocalIpcEndpoint.CreateServer(name,sid);
        lock(gate){if(disposed)throw new OperationCanceledException(ct);child=SessionProcess.Start(book.RequiresAdmin?HostChildRole.AdminRunbookWorker:HostChildRole.RunbookWorker,session,name,nonce);}
        await using var channel=await LocalIpcEndpoint.AcceptAsync(pipe,child,nonce,timeout.Token);
        var done=new TaskCompletionSource<RunbookResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var peer=new IpcPeerSession(channel,(message,_)=>
        {
            if(message.Type!="runbook-result"||message.Id!=request.Id)throw new InvalidDataException("다른 worker 결과다.");
            var result=message.Body?.Deserialize<RunbookResult>()??throw new InvalidDataException("worker 결과 누락이다.");result.Validate();if(!done.TrySetResult(result))throw new InvalidDataException("worker 결과 재생이다.");return Task.CompletedTask;
        },timeout.Token,onStopped:_=>done.TrySetException(new IOException("Runbook worker 연결 종료다.")));
        peer.Start();peer.Post("runbook-config",new RunbookConfiguration(request,DateTimeOffset.UtcNow.AddSeconds(20),session),request.Id);
        try{return await done.Task.WaitAsync(timeout.Token);}
        finally{Dispose();}
    }
    public void Fence()=>Volatile.Read(ref child)?.Dispose();
    public void Dispose(){Fence();lock(gate){if(disposed)return;disposed=true;child?.Dispose();}}
}
