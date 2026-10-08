using System.Security.Cryptography;
using System.Diagnostics;
using System.Threading.Channels;
using RemoteAssist.Host.Core.Security;
using RemoteAssist.Protocol;

namespace RemoteAssist.Host.Core.Files;

public interface IFileTransferTransport
{
    bool IsOpen {get;}
    ulong BufferedAmount {get;}
    void Send(FileMessage message);
    void Send(byte[] packet);
}
public sealed record FileTransferState(string Id,string Direction,long Size,string State);

/// <summary>One authenticated file exchange. Approval, durable audit and checksum precede payload and commit.</summary>
public sealed class FileTransferSession:IAsyncDisposable
{
    readonly object gate=new(),sendGate=new();
    readonly IFileTransferTransport transport;
    readonly PermissionGate permissions;readonly PermissionSnapshot binding;
    readonly Func<FileMessage,CancellationToken,Task<string?>> approve;
    readonly Func<FileAuditNotice,CancellationToken,Task> audit;
    readonly Action<string> fail;readonly Action<FileTransferState> state;
    readonly CancellationTokenSource lifetime;
    readonly Channel<Entry> queue=Channel.CreateBounded<Entry>(32);
    readonly HashSet<string> seen=new(StringComparer.Ordinal);readonly Queue<string> seenOrder=new();
    readonly Dictionary<string,FileMessage> closed=new(StringComparer.Ordinal);
    Transfer? current;Task? sender;long received,sent;int started,disposed;readonly long generation;
    public Task Completion {get;private set;}=Task.CompletedTask;
    public bool IsOpen=>transport.IsOpen;
    public FileTransferSession(IFileTransferTransport transport,PermissionGate permissions,PermissionSnapshot binding,
        Func<FileMessage,CancellationToken,Task<string?>> approve,Func<FileAuditNotice,CancellationToken,Task> audit,Action<string> fail,Action<FileTransferState> state,CancellationToken ct,long generation=1)
    {this.generation=generation;this.transport=transport;this.permissions=permissions;this.binding=binding;this.approve=approve;this.audit=audit;this.fail=fail;this.state=state;lifetime=CancellationTokenSource.CreateLinkedTokenSource(ct);}
    public void Start(){if(Interlocked.Exchange(ref started,1)!=0)throw new InvalidOperationException("파일 수신은 한 번만 시작한다.");Completion=Pump();}
    public void Receive(FileMessage message)
    {
        if(Volatile.Read(ref disposed)!=0||lifetime.IsCancellationRequested)return;
        try
        {
            FileMessage? busy=null;
            _=FileMessage.Parse(ProtocolJson.Encode(message));
            lock(gate)
            {
                Demand();if(message.SessionId!=binding.SessionId||message.Generation!=generation||message.PermissionGeneration!=binding.Generation||message.Sequence!=received+1)throw new UnauthorizedAccessException("파일 바인딩·순번 오류다.");received++;
                if(message.T=="file-offer")
                {
                    Remember(message.Id);
                    if(current!=null)busy=message with {T="file-reject",Name=null,Size=null,Hash=null,Code="busy"};
                    else {current=new(message,false,lifetime.Token);Enqueue(new(current,message,null));return;}
                }
                else
                {
                if(current?.Offer.Id!=message.Id)
                {
                    if(closed.TryGetValue(message.Id,out var old)&&(message.T=="file-cancel"||message.T=="file-received"&&old.T=="file-complete"&&message.Size==old.Size&&message.Hash==old.Hash))return;
                    throw new InvalidDataException("다른 파일 응답이다.");
                }
                var owner=current;
                if(message.T=="file-progress")
                {
                    if(!owner.Outgoing||owner.Phase is not ("sending" or "receipt")||message.NextChunk<=owner.Acknowledged||message.NextChunk>Volatile.Read(ref owner.SentChunks)||message.Size!=Math.Min(owner.Offer.Size!.Value,(long)message.NextChunk!.Value*FileChunkHeader.MaxPayload))throw new InvalidDataException("파일 credit 오류다.");
                    owner.Acknowledged=message.NextChunk.Value;var wake=owner.Progress;owner.Progress=new(TaskCreationOptions.RunContinuationsAsynchronously);wake.TrySetResult();return;
                }
                if(message.T is "file-cancel" or "file-reject")
                {
                    if(message.T=="file-reject"&&(!owner.Outgoing||owner.Phase!="offered"))throw new InvalidDataException("파일 거부 방향 오류다.");
                    owner.PeerEnded=true;owner.Result=message.T=="file-reject"?"denied":message.Code is "io-error" or "hash-mismatch" or "timeout" or "too-large"?"failed":"stopped";owner.Cancel.Cancel();Enqueue(new(owner,message,null));return;
                }
                if(message.T=="file-accept")
                {if(!owner.Outgoing||owner.Phase!="offered")throw new InvalidDataException("파일 수락 순서 오류다.");owner.Phase="sending";owner.Accepted.TrySetResult();return;}
                if(message.T=="file-received")
                {if(!owner.Outgoing||owner.Phase!="receipt"||message.Size!=owner.Offer.Size||message.Hash!=owner.Offer.Hash)throw new InvalidDataException("파일 receipt 오류다.");owner.Receipt.TrySetResult();return;}
                if(message.T!="file-complete"||owner.Outgoing||owner.Phase!="receiving")throw new InvalidDataException("파일 JSON 단계 오류다.");
                owner.Phase="completing";Enqueue(new(owner,message,null));
                }
            }
            if(busy!=null)Send(busy);
        }
        catch{Fatal("파일 채널 계약을 거부했다.");}
    }
    public void Receive(byte[] packet)
    {
        if(Volatile.Read(ref disposed)!=0||lifetime.IsCancellationRequested)return;
        try
        {
            var header=FileChunkHeader.Decode(packet);
            lock(gate)
            {
                if(closed.TryGetValue(header.TransferId.ToString("N"),out var old)&&old.T=="file-offer")return;
                Demand();var owner=current??throw new InvalidDataException("수락하지 않은 파일 본문이다.");
                if(owner.Outgoing||owner.Phase!="receiving"||owner.Token.IsCancellationRequested||header.TransferId!=Guid.ParseExact(owner.Offer.Id,"N")||header.Length==0)throw new InvalidDataException("파일 본문 방향·UUID 오류다.");
                Enqueue(new(owner,null,packet.ToArray()));
            }
        }
        catch{Fatal("파일 청크를 거부했다.");}
    }
    public bool SendFile(string selectedPath,string id)
    {
        lock(gate)
        {
            Demand();if(current!=null||sender is {IsCompleted:false})return false;Remember(id);
            var owner=new Transfer(new(){Id=id,SessionId=binding.SessionId,Generation=1,PermissionGeneration=binding.Generation},true,lifetime.Token);current=owner;
            sender=Task.Run(()=>SendFile(owner,selectedPath));return true;
        }
    }
    public void CancelFile(string id)
    {lock(gate){if(current?.Offer.Id!=id)return;current.Result="stopped";current.Cancel.Cancel();Enqueue(new(current,null,null));}}
    async Task SendFile(Transfer owner,string path)
    {
        try
        {
            owner.Token.ThrowIfCancellationRequested();
            WindowsFileSandbox.LocalPath(path);var name=Path.GetFileName(path);
            using var folder=new WindowsFileSandbox(Path.GetDirectoryName(path)!);using var source=folder.Read(name);
            if(source.Length>FileMessage.MaxSize)throw new IOException("전송 파일 크기 상한이다.");
            string digest;
            try{digest=Convert.ToHexStringLower(await SHA256.HashDataAsync(source,owner.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(30),owner.Token));}
            catch(TimeoutException){Fatal("파일 hash 읽기의 기한을 넘었다.");throw;}
            source.Position=0;
            owner.Offer=Bound(new(){T="file-offer",Id=owner.Offer.Id,Name=name,Size=source.Length,Hash=digest},1);
            await Audit(owner,"started","allowed");owner.Audited=true;
            lock(gate){owner.Token.ThrowIfCancellationRequested();owner.Phase="offered";owner.Published=true;}
            Send(owner.Offer);
            await owner.Accepted.Task.WaitAsync(TimeSpan.FromSeconds(30),owner.Token);
            using var total=CancellationTokenSource.CreateLinkedTokenSource(owner.Token);total.CancelAfter(TimeSpan.FromMinutes(10));
            var buffer=new byte[FileChunkHeader.MaxPayload];uint sequence=0;long count=0;
            while(count<source.Length)
            {
                await Capacity(total.Token);
                await Credit(owner,total.Token);
                var read=(int)Math.Min(buffer.Length,source.Length-count);
                try{await source.ReadExactlyAsync(buffer.AsMemory(0,read),total.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(10),total.Token);}
                catch(TimeoutException){Fatal("파일 청크 읽기의 기한을 넘었다.");throw;}
                if(read==0)throw new EndOfStreamException();
                var packet=new FileChunkHeader(Guid.ParseExact(owner.Offer.Id,"N"),sequence++,read).Encode(buffer.AsSpan(0,read));
                lock(sendGate){owner.Token.ThrowIfCancellationRequested();Demand();Volatile.Write(ref owner.SentChunks,(int)sequence);if(!permissions.Execute(Capability.File,binding.Generation,()=>transport.Send(packet)))throw new UnauthorizedAccessException("파일 송신 동의가 철회됐다.");}
                count+=read;
            }
            await Capacity(total.Token);
            lock(gate){owner.Phase="receipt";}
            Send(owner.Offer with {T="file-complete",Name=null});
            await owner.Receipt.Task.WaitAsync(TimeSpan.FromSeconds(10),total.Token);owner.Result="completed";
            await Audit(owner,"finished",owner.Result);owner.FinishedAudit=true;
            state(new(owner.Offer.Id,"from-host",owner.Offer.Size!.Value,"completed"));
        }
        catch(OperationCanceledException){await FinishFailure(owner,owner.Result??(owner.Token.IsCancellationRequested?"stopped":"failed"),owner.Token.IsCancellationRequested?"cancelled":"timeout");}
        catch(TimeoutException){await FinishFailure(owner,"failed","timeout");}
        catch{await FinishFailure(owner,"failed","io-error");}
        finally{Clear(owner);}
    }
    async Task Pump()
    {
        try
        {
            await foreach(var entry in queue.Reader.ReadAllAsync(lifetime.Token))
            {
                var owner=entry.Owner;lock(gate){if(current!=owner)continue;}
                lifetime.Token.ThrowIfCancellationRequested();
                if(entry.Message?.T=="file-offer"){await Accept(owner);continue;}
                if(owner.Outgoing)continue;
                if(owner.Token.IsCancellationRequested){await FinishFailure(owner,owner.Result??"stopped","cancelled");Clear(owner);continue;}
                try
                {
                    if(entry.Packet!=null)
                    {
                        owner.Incoming!.Chunk(entry.Packet);owner.ReceivedChunks++;
                        if(owner.ReceivedChunks%4==0||owner.Incoming.Received==owner.Offer.Size)Send(owner.Offer with {T="file-progress",Name=null,Hash=null,Size=owner.Incoming.Received,NextChunk=owner.ReceivedChunks});
                        continue;
                    }
                    owner.Token.ThrowIfCancellationRequested();owner.Incoming!.Complete(entry.Message!);owner.Result="completed";
                    await Audit(owner,"finished","completed");owner.FinishedAudit=true;
                    Clear(owner);
                    Send(owner.Offer with {T="file-received",Name=null});
                    state(new(owner.Offer.Id,"to-host",owner.Offer.Size!.Value,"completed"));
                }
                catch(OperationCanceledException){await FinishFailure(owner,"stopped","cancelled");Clear(owner);}
                catch(InvalidDataException){await FinishFailure(owner,"failed","hash-mismatch");Clear(owner);}
                catch{await FinishFailure(owner,"failed","io-error");Clear(owner);}
            }
        }
        catch(OperationCanceledException)when(lifetime.IsCancellationRequested){}
        catch{Fatal("파일 수신 처리에 실패했다.");}
        finally
        {
            Transfer? owned;lock(gate){owned=current;owned?.Cancel.Cancel();}
            if(owned is {Outgoing:false})Clear(owned);while(queue.Reader.TryRead(out _)){}
        }
    }
    async Task Accept(Transfer owner)
    {
        try
        {
            await Audit(owner,"started","allowed");owner.Audited=true;
            var selected=await approve(owner.Offer,owner.Token).WaitAsync(TimeSpan.FromSeconds(30),owner.Token);
            if(selected is null){owner.Result="denied";await FinishFailure(owner,"denied","declined");Clear(owner);return;}
            owner.Token.ThrowIfCancellationRequested();
            owner.Folder=new(selected);var candidate=owner.Folder.Create(owner.Offer.Name!);owner.Candidate=candidate;
            owner.Incoming=new(owner.Offer,permissions,binding.Generation,candidate.Stream,candidate.Commit,candidate.Dispose);
            lock(gate){owner.Token.ThrowIfCancellationRequested();owner.Phase="receiving";Interlocked.Exchange(ref owner.LastActivity,Stopwatch.GetTimestamp());}
            Send(owner.Offer with {T="file-accept",Name=null,Size=null,Hash=null});
            owner.Deadline=WatchIncoming(owner);
        }
        catch(OperationCanceledException){await FinishFailure(owner,owner.Result??(owner.Token.IsCancellationRequested?"stopped":"failed"),owner.Token.IsCancellationRequested?"cancelled":"timeout");Clear(owner);}
        catch(TimeoutException){await FinishFailure(owner,"failed","timeout");Clear(owner);}
        catch{await FinishFailure(owner,"failed","io-error");Clear(owner);}
    }
    async Task WatchIncoming(Transfer owner)
    {
        try
        {
            using var timer=new PeriodicTimer(TimeSpan.FromSeconds(1));
            while(await timer.WaitForNextTickAsync(owner.Token))
                if(Stopwatch.GetElapsedTime(Interlocked.Read(ref owner.LastActivity))>=TimeSpan.FromSeconds(10)||Stopwatch.GetElapsedTime(owner.Started)>=TimeSpan.FromMinutes(10))
                {
                    lock(gate){if(current!=owner)return;if(owner.Phase=="completing"){Fatal("파일 저장 또는 완료 응답의 기한을 넘었다.");return;}owner.Result="failed";owner.Cancel.Cancel();Enqueue(new(owner,null,null));}return;
                }
        }
        catch(OperationCanceledException){}
        catch{Fatal("파일 수신 기한 감독이 실패했다.");}
    }
    async Task Capacity(CancellationToken ct)
    {
        using var limit=CancellationTokenSource.CreateLinkedTokenSource(ct);limit.CancelAfter(TimeSpan.FromSeconds(10));
        while(transport.BufferedAmount>=65536)
        {Demand();if(transport.BufferedAmount>262144)throw new IOException("파일 송신 버퍼 상한이다.");await Task.Delay(10,limit.Token);}
    }
    async Task Credit(Transfer owner,CancellationToken ct)
    {
        while(true)
        {
            Task waiting;lock(gate){if(owner.SentChunks-owner.Acknowledged<16)return;waiting=owner.Progress.Task;}
            await waiting.WaitAsync(TimeSpan.FromSeconds(10),ct);
        }
    }
    async Task FinishFailure(Transfer owner,string outcome,string code)
    {
        if(Interlocked.Exchange(ref owner.Failing,1)!=0)return;
        try
        {
            if(owner.Audited&&!owner.FinishedAudit){await Audit(owner,"finished",outcome);owner.FinishedAudit=true;}
            if(owner.Published&&!owner.PeerEnded&&!lifetime.IsCancellationRequested&&transport.IsOpen)Send(owner.Offer with {T=owner.Outgoing||owner.Phase!="offered"?"file-cancel":"file-reject",Name=null,Size=null,Hash=null,Code=code});
            state(new(owner.Offer.Id,owner.Outgoing?"from-host":"to-host",owner.Offer.Size??0,outcome));
        }
        catch{Fatal("파일 실패 감사 또는 종료 통지가 실패했다.");}
    }
    async Task Audit(Transfer owner,string phase,string outcome)
    {
        var notice=new FileAuditNotice(owner.Offer.Id,binding.SessionId,binding.Generation,phase,owner.Outgoing?"from-host":"to-host",owner.Offer.Size??0,outcome);notice.Validate();
        try{await audit(notice,lifetime.Token).WaitAsync(TimeSpan.FromSeconds(2),lifetime.Token);}catch{Fatal("파일 감사 기록에 실패했다.");throw;}
    }
    void Send(FileMessage message)
    {
        lock(sendGate)
        {
            Demand();if(transport.BufferedAmount>262144)throw new IOException("파일 JSON 송신 상한이다.");
            var value=Bound(message,sent+1);_=FileMessage.Parse(ProtocolJson.Encode(value));
            if(!permissions.Execute(Capability.File,binding.Generation,()=>transport.Send(value)))throw new UnauthorizedAccessException("파일 송신 승인이 종료됐다.");sent++;
        }
    }
    FileMessage Bound(FileMessage message,long seq)=>message with {Version=1,SessionId=binding.SessionId,Generation=generation,PermissionGeneration=binding.Generation,Sequence=seq};
    void Demand(){lifetime.Token.ThrowIfCancellationRequested();if(!transport.IsOpen||!permissions.Allows(Capability.File,binding.Generation))throw new UnauthorizedAccessException("파일 채널의 현재 동의가 없다.");}
    void Remember(string id)
    {if(!Guid.TryParseExact(id,"N",out var parsed)||parsed==Guid.Empty||parsed.ToString("N")!=id||!seen.Add(id))throw new InvalidDataException("파일 전송 ID 재생이다.");seenOrder.Enqueue(id);if(seenOrder.Count>256)seen.Remove(seenOrder.Dequeue());}
    void Enqueue(Entry entry)
    {Interlocked.Exchange(ref entry.Owner.LastActivity,Stopwatch.GetTimestamp());if(!queue.Writer.TryWrite(entry))throw new IOException("파일 수신 큐 상한이다.");}
    void Clear(Transfer owner)
    {
        lock(gate){if(current!=owner)return;current=null;closed[owner.Offer.Id]=owner.Offer with {T=owner.Outgoing?"file-complete":"file-offer"};if(closed.Count>256)closed.Remove(closed.Keys.First());owner.Cancel.Cancel();owner.Incoming?.Dispose();owner.Candidate?.Dispose();owner.Folder?.Dispose();owner.Cancel.Dispose();}
    }
    void Fatal(string reason){if(Volatile.Read(ref disposed)!=0)return;try{lifetime.Cancel();}catch(AggregateException){}catch(ObjectDisposedException){return;}fail(reason);}
    public async ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;lifetime.Cancel();queue.Writer.TryComplete();
        await Task.WhenAll(new[]{Completion,sender}.OfType<Task>()).WaitAsync(TimeSpan.FromSeconds(3));lifetime.Dispose();
    }
    sealed record Entry(Transfer Owner,FileMessage? Message,byte[]? Packet);
    sealed class Transfer
    {
        public FileMessage Offer;public readonly bool Outgoing;
        public readonly CancellationTokenSource Cancel;
        public CancellationToken Token {get;}
        public readonly TaskCompletionSource Accepted=new(TaskCreationOptions.RunContinuationsAsynchronously),Receipt=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Progress=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SentChunks,Acknowledged,ReceivedChunks;
        public string Phase="offered";public string? Result;public bool Audited,FinishedAudit,Published,PeerEnded;public int Failing;
        public readonly long Started=Stopwatch.GetTimestamp();public long LastActivity=Stopwatch.GetTimestamp();
        public WindowsFileSandbox? Folder;public WindowsFileSandbox.Candidate? Candidate;public IncomingFile? Incoming;public Task? Deadline;
        public Transfer(FileMessage offer,bool outgoing,CancellationToken ct)
        {Offer=offer;Outgoing=outgoing;Published=!outgoing;Cancel=CancellationTokenSource.CreateLinkedTokenSource(ct);Token=Cancel.Token;}
    }
}
