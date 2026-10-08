using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RemoteAssist.Host.Core.Ipc;

/// <summary>Bounded framing after native pipe peer authentication. Owns the supplied stream.</summary>
public sealed class LocalIpcChannel(Stream stream) : IAsyncDisposable
{
    readonly SemaphoreSlim writeGate=new(1,1);
    readonly SemaphoreSlim readGate=new(1,1);
    long sent,received;int pending,disposed;
    public async Task SendAsync(string type,object? body=null,string id="",string? nonce=null,CancellationToken ct=default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed)!=0,this);
        if(Interlocked.Increment(ref pending)>128){Interlocked.Decrement(ref pending);await DisposeAsync();throw new InvalidOperationException("IPC 쓰기 대기 상한 초과다.");}
        try
        {
            await writeGate.WaitAsync(ct);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed)!=0,this);
                var message=new LocalIpcMessage {Type=type,Sequence=sent+1,Id=id,Nonce=nonce,Body=body is null?null:JsonSerializer.SerializeToElement(body)};
                var bytes=JsonSerializer.SerializeToUtf8Bytes(message);LocalIpcMessage.Parse(bytes);
                var prefix=new byte[4];BinaryPrimitives.WriteUInt32LittleEndian(prefix,(uint)bytes.Length);
                await stream.WriteAsync(prefix,ct);await stream.WriteAsync(bytes,ct);await stream.FlushAsync(ct);sent++;
            }
            finally{writeGate.Release();}
        }
        catch {await DisposeAsync();throw;}
        finally{Interlocked.Decrement(ref pending);}
    }
    public async Task<LocalIpcMessage?> ReceiveAsync(TimeSpan timeout,CancellationToken ct=default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed)!=0,this);
        if(timeout<=TimeSpan.Zero||timeout>TimeSpan.FromSeconds(10))throw new ArgumentOutOfRangeException(nameof(timeout));
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(timeout);
        await readGate.WaitAsync(deadline.Token);
        try
        {
            var prefix=new byte[4];var first=await stream.ReadAsync(prefix.AsMemory(0,1),deadline.Token);if(first==0){await DisposeAsync();return null;}
            await stream.ReadExactlyAsync(prefix.AsMemory(1),deadline.Token);
            var length=BinaryPrimitives.ReadUInt32LittleEndian(prefix);
            if(length is 0 or >LocalIpcMessage.MaxBytes)throw new InvalidDataException("IPC 길이 상한 오류다.");
            var bytes=new byte[length];await stream.ReadExactlyAsync(bytes,deadline.Token);
            var msg=LocalIpcMessage.Parse(bytes);
            if(msg.Sequence!=received+1)throw new InvalidDataException("IPC 순번 재생/누락이다.");
            received=msg.Sequence;return msg;
        }
        catch{await DisposeAsync();throw;}
        finally{readGate.Release();}
    }
    public async Task AuthenticateAsync(string ownRole,string expectedRole,string nonce,CancellationToken ct=default)
    {
        if(nonce.Length!=64||!nonce.All(Uri.IsHexDigit))throw new ArgumentException("32-byte nonce가 필요하다.");
        // Windows pipes may have no outbound buffering. Both sides must read while writing hello.
        var incoming=ReceiveAsync(TimeSpan.FromSeconds(10),ct);
        try
        {
            await SendAsync("hello",new {role=ownRole},nonce:nonce,ct:ct);
            var hello=await incoming;
            var actual=hello?.Nonce;
            if(hello?.Type!="hello"||actual is null||actual.Length!=nonce.Length||
                !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual),Encoding.ASCII.GetBytes(nonce))||
                hello.Body is not {} body||body.ValueKind!=JsonValueKind.Object||body.EnumerateObject().Count()!=1||
                !body.TryGetProperty("role",out var role)||role.ValueKind!=JsonValueKind.String||role.GetString()!=expectedRole)
                throw new UnauthorizedAccessException("IPC 상대 인증 실패다.");
        }
        catch(Exception failure)
        {
            await DisposeAsync();
            try{await incoming;}catch(Exception readFailure)
            {
                if(failure is ObjectDisposedException)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(readFailure).Throw();
            }
            throw;
        }
    }
    public async ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        await stream.DisposeAsync();
        // Pending writers/readers still release gates; do not dispose semaphores under them.
    }
}
