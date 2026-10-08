using System.Collections.Concurrent;
using System.Text.Json;
using RemoteAssist.Protocol;

namespace RemoteAssist.Host.Core.Ipc;

public sealed class IpcFeatureRequests
{
    readonly ConcurrentDictionary<string,TaskCompletionSource<FeatureMessage>> pending=new(StringComparer.Ordinal);
    int waiting;
    public async Task<FeatureMessage> Request(IpcPeerSession peer,FeatureMessage body,CancellationToken ct)
    {
        if(Interlocked.Increment(ref waiting)>16){Interlocked.Decrement(ref waiting);throw new IOException("로컬 보조 작업 대기 상한이다.");}
        var reply=new TaskCompletionSource<FeatureMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var added=false;
        try
        {
            added=pending.TryAdd(body.Id,reply);if(!added)throw new InvalidDataException("중복 보조 요청이다.");
            peer.Post("clipboard-request",body,body.Id);return await reply.Task.WaitAsync(TimeSpan.FromSeconds(2),ct);
        }
        finally{if(added)pending.TryRemove(body.Id,out _);Interlocked.Decrement(ref waiting);}
    }
    public void Accept(LocalIpcMessage message)
    {
        var value=message.Body?.Deserialize<FeatureMessage>()??throw new InvalidDataException("로컬 클립보드 응답 누락이다.");
        _=FeatureMessage.Parse(ProtocolJson.Encode(value));
        if(value.Id!=message.Id||!pending.TryRemove(message.Id,out var reply))throw new InvalidDataException("다른 클립보드 응답이다.");reply.TrySetResult(value);
    }
}
