using System.Collections.Concurrent;
using System.Text.Json;

namespace RemoteAssist.Host.Core.Ipc;

/// <summary>Authenticated, bounded, one-shot local replies. Late replies after cancellation are discarded.</summary>
public sealed class IpcLocalRequests
{
    readonly ConcurrentDictionary<string,TaskCompletionSource<JsonElement?>> pending=new();
    public async Task<JsonElement?> Request(IpcPeerSession peer,string type,object body,string id,TimeSpan timeout,CancellationToken ct)
    {
        if(pending.Count>=16)throw new IOException("로컬 요청 대기 상한이다.");
        var completion=new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if(!pending.TryAdd(id,completion))throw new InvalidDataException("로컬 요청 ID 중복이다.");
        try{peer.Post(type,body,id);return await completion.Task.WaitAsync(timeout,ct);}
        finally{pending.TryRemove(id,out _);}
    }
    public void Accept(LocalIpcMessage message)
    {if(pending.TryRemove(message.Id,out var completion))completion.TrySetResult(message.Body);}
}
