using System.Threading.Channels;
using System.Text.Json;
using RemoteAssist.Protocol;

namespace RemoteAssist.Host.Core.Ipc;

/// <summary>Heartbeat and receive lease stay live while a consent dialog waits for local input.</summary>
public sealed class IpcPeerSession : IAsyncDisposable
{
    readonly LocalIpcChannel channel;
    readonly CancellationTokenSource lifetime;
    readonly CancellationToken token;
    readonly Channel<LocalIpcMessage> incoming=Channel.CreateBounded<LocalIpcMessage>(128);
    readonly Channel<Outgoing> outgoing=Channel.CreateBounded<Outgoing>(128);
    readonly Func<LocalIpcMessage,CancellationToken,Task> handle;
    readonly Action<LocalIpcMessage>? urgent;
    readonly Action<string>? onStopped;
    readonly TaskCompletionSource completed=new(TaskCreationOptions.RunContinuationsAsynchronously);
    int started;
    int stopped;
    public Task Completion=>completed.Task;
    public string EndReason {get;private set;}="IPC 종료";
    public CancellationToken Token=>token;
    public IpcPeerSession(LocalIpcChannel channel,Func<LocalIpcMessage,CancellationToken,Task> handle,CancellationToken ct=default,Action<LocalIpcMessage>? urgent=null,Action<string>? onStopped=null)
    {
        this.channel=channel;this.handle=handle;this.urgent=urgent;this.onStopped=onStopped;lifetime=CancellationTokenSource.CreateLinkedTokenSource(ct);token=lifetime.Token;
    }
    public void Start()
    {
        if(Volatile.Read(ref stopped)!=0||Interlocked.Exchange(ref started,1)!=0)throw new InvalidOperationException("IPC 시작 상태 오류다.");
        _=Run();
    }
    async Task Run()
    {
        try {await Task.WhenAll(Observe(Read),Observe(Write),Observe(Dispatch),Observe(Heartbeat));completed.TrySetResult();}
        catch(Exception ex){completed.TrySetException(ex);}
    }
    public void Post(string type,object? body=null,string id="")
    {
        if(Volatile.Read(ref stopped)!=0)throw new InvalidOperationException("종료한 IPC다.");
        var snapshot=body is null?(JsonElement?)null:ProtocolJson.Element(body);
        LocalIpcMessage.Parse(ProtocolJson.Encode(new LocalIpcMessage {Type=type,Body=snapshot,Id=id,Sequence=1}));
        if(!outgoing.Writer.TryWrite(new(type,snapshot,id))){Stop("IPC 송신 큐 상한 초과");throw new InvalidOperationException("IPC 송신 큐 상한 초과");}
    }
    async Task Read()
    {
        while(!Token.IsCancellationRequested)
        {
            var message=await channel.ReceiveAsync(TimeSpan.FromSeconds(1),Token);
            if(message is null){Stop("IPC 상대 종료");return;}
            if(message.Type=="heartbeat")continue;
            if(message.Type=="hello")throw new UnauthorizedAccessException("IPC hello 재생");
            if(urgent!=null&&message.Type is "stop" or "admin-revoke"){urgent(message);continue;}
            if(!incoming.Writer.TryWrite(message))throw new InvalidDataException("IPC 수신 큐 상한 초과");
        }
    }
    async Task Write()
    {
        await foreach(var item in outgoing.Reader.ReadAllAsync(Token))
            await channel.SendAsync(item.Type,item.Body,item.Id,ct:Token);
    }
    async Task Dispatch()
    {await foreach(var item in incoming.Reader.ReadAllAsync(Token))await handle(item,Token);}
    async Task Heartbeat()
    {
        using var timer=new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        while(await timer.WaitForNextTickAsync(Token))await channel.SendAsync("heartbeat",ct:Token);
    }
    async Task Observe(Func<Task> operation)
    {
        try{await operation();}
        catch(OperationCanceledException)when(Token.IsCancellationRequested){}
        catch(Exception ex){Stop("IPC 실패: "+ex.GetType().Name);}
        finally{Stop(EndReason);}
    }
    void Stop(string reason)
    {
        if(Interlocked.Exchange(ref stopped,1)!=0)return;
        EndReason=reason;
        try{lifetime.Cancel();}catch(AggregateException){}
        incoming.Writer.TryComplete();outgoing.Writer.TryComplete();
        try{onStopped?.Invoke(reason);}catch{ /* Peer authority is already revoked. Never reopen on an observer error. */ }
    }
    public async ValueTask DisposeAsync()
    {
        Stop("로컬 IPC 종료");await channel.DisposeAsync();
        if(Volatile.Read(ref started)==0)completed.TrySetResult();
        try{await Completion.WaitAsync(TimeSpan.FromSeconds(5));}catch(TimeoutException){}
        if(Completion.IsCompleted)lifetime.Dispose();
    }
    sealed class Outgoing(string type,JsonElement? body,string id)
    {public string Type {get;}=type;public JsonElement? Body {get;}=body;public string Id {get;}=id;}
}
