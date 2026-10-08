using System.Collections.Concurrent;

namespace RemoteAssist.Host.Core.Ipc;

/// <summary>Bounded correlated ACKs. The caller cancels its authority on timeout; late ACKs cannot reopen it.</summary>
public sealed class IpcAcknowledgements
{
    readonly ConcurrentDictionary<string,TaskCompletionSource> pending=new(StringComparer.Ordinal);
    int waiting;
    public async Task Request(IpcPeerSession peer,string type,object body,CancellationToken ct)
    {
        if(Interlocked.Increment(ref waiting)>32){Interlocked.Decrement(ref waiting);throw new IOException("IPC ACK 대기 상한이다.");}
        var id=Guid.NewGuid().ToString("N");var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            if(!pending.TryAdd(id,completion))throw new IOException("IPC 요청 ID 충돌이다.");
            peer.Post(type,body,id);await completion.Task.WaitAsync(TimeSpan.FromMilliseconds(500),ct);
        }
        finally{pending.TryRemove(id,out _);Interlocked.Decrement(ref waiting);}
    }
    public void Accept(LocalIpcMessage message)
    {
        if(message.Type!="recovery-ack"||message.Body!=null||!pending.TryRemove(message.Id,out var completion))throw new InvalidDataException("예상하지 않은 IPC ACK다.");
        completion.TrySetResult();
    }
}
