using System.Collections.Concurrent;
using System.Text.Json;
using RemoteAssist.Host.Core.Security;

namespace RemoteAssist.Host.Core.Ipc;

public sealed class IpcInputRequests
{
    readonly ConcurrentDictionary<string,TaskCompletionSource<int>> pending=new(StringComparer.Ordinal);
    int waiting;
    public async Task<int> Request(IpcPeerSession peer,AdminInputBatch body,CancellationToken ct)
    {
        AdminInputValidation.Validate(body);
        if(Interlocked.Increment(ref waiting)>32){Interlocked.Decrement(ref waiting);throw new IOException("관리자 입력 ACK 대기 상한이다.");}
        var id=Guid.NewGuid().ToString("N");var reply=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            if(!pending.TryAdd(id,reply))throw new IOException("관리자 입력 요청 충돌이다.");
            peer.Post("admin-input",body,id);var count=await reply.Task.WaitAsync(TimeSpan.FromMilliseconds(500),ct);
            if(count<0||count>body.Strokes.Length)throw new InvalidDataException("관리자 입력 반환 개수 오류다.");return count;
        }
        finally{pending.TryRemove(id,out _);Interlocked.Decrement(ref waiting);}
    }
    public void Accept(LocalIpcMessage message)
    {
        if(message.Type!="admin-input-ack"||message.Body is null||!pending.TryRemove(message.Id,out var reply))throw new InvalidDataException("예상하지 않은 관리자 입력 ACK다.");
        var result=message.Body.Value.Deserialize<AdminInputResult>()??throw new InvalidDataException("입력 결과 누락이다.");
        if(result.Count is <0 or >64)throw new InvalidDataException("입력 결과 상한 오류다.");reply.TrySetResult(result.Count);
    }
}
