using System.Text;
using System.Threading.Channels;
using RemoteAssist.Host.Core.Runbooks;
using RemoteAssist.Host.Core.Security;
using RemoteAssist.Protocol;
using SIPSorcery.Net;
namespace RemoteAssist.Host.Core.Media;

internal sealed class RunbookDataSession(RTCDataChannel channel,PermissionGate permissions,PermissionSnapshot binding,int generation,Func<RunbookMessage,CancellationToken,Task<RunbookResult>> execute,Action<string> fail,CancellationToken ct)
{
    readonly object gate=new();readonly HashSet<string> seen=new();readonly Queue<string> history=new();
    readonly Channel<RunbookMessage> queue=Channel.CreateBounded<RunbookMessage>(1);
    long received,sent;RunbookMessage? current;CancellationTokenSource? operation;
    public Task Completion {get;private set;}=Task.CompletedTask;
    public void Attach()
    {
        channel.onopen+=()=>{try{Send(new(){T="runbook-catalog",Id=Guid.NewGuid().ToString("N"),Books=RunbookCatalog.Definitions()});}catch{fail("Runbook 목록 송신 실패다.");}};
        channel.onclose+=()=>fail("Runbook 채널 종료다.");channel.onerror+=_=>fail("Runbook 채널 오류다.");
        channel.onmessage+=(dataChannel,kind,bytes)=>
        {
            if(ct.IsCancellationRequested)return;
            try
            {
                if(kind!=DataChannelPayloadProtocols.WebRTC_String)throw new InvalidDataException("Runbook은 JSON 텍스트다.");var message=RunbookMessage.Parse(bytes);
                lock(gate)
                {
                    if(message.SessionId!=binding.SessionId||message.PermissionGeneration!=binding.Generation||message.Generation!=generation||message.Sequence!=received+1||!permissions.Allows(Capability.Runbook,binding.Generation))throw new UnauthorizedAccessException("Runbook 승인 바인딩 오류다.");received++;
                    if(message.T=="runbook-cancel"){if(current?.Id==message.Id)operation?.Cancel();return;}
                    if(message.T!="runbook-request"||current!=null||!seen.Add(message.Id))throw new InvalidDataException("중복/미지원 Runbook 실행이다.");
                    _=RunbookCatalog.Demand(message);history.Enqueue(message.Id);if(history.Count>256)seen.Remove(history.Dequeue());
                    current=message;operation=CancellationTokenSource.CreateLinkedTokenSource(ct);if(!queue.Writer.TryWrite(message))throw new IOException("Runbook 큐 상한이다.");
                }
            }
            catch{fail("Runbook 명령을 거부했다.");}
        };Completion=Run();
    }
    async Task Run()
    {
        try
        {
            await foreach(var request in queue.Reader.ReadAllAsync(ct))
            {
                RunbookResult result;
                try{result=await execute(request,operation!.Token).WaitAsync(TimeSpan.FromSeconds(55),ct);}
                catch(OperationCanceledException)when(!ct.IsCancellationRequested){result=new("cancelled",null,"","작업을 중단했다.",RunbookCatalog.Definition(request.BookId!).Impact);}
                lock(gate){Send(new(){T="runbook-result",Id=request.Id,Result=result});current=null;operation?.Dispose();operation=null;}
            }
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){}
        catch{fail("Runbook 실행 연결 실패다.");}
        finally{lock(gate){operation?.Cancel();operation?.Dispose();operation=null;current=null;}}
    }
    void Send(RunbookMessage message)
    {
        lock(gate)
        {
            ct.ThrowIfCancellationRequested();if(channel.readyState!=RTCDataChannelState.open||channel.bufferedAmount>65536)throw new IOException("Runbook 송신 상한이다.");
            var value=message with {SessionId=binding.SessionId,Generation=generation,PermissionGeneration=binding.Generation,Sequence=sent+1};var bytes=ProtocolJson.Encode(value);_=RunbookMessage.Parse(bytes);
            if(!permissions.Execute(Capability.Runbook,binding.Generation,()=>channel.send(Encoding.UTF8.GetString(bytes))))throw new UnauthorizedAccessException("Runbook 승인이 철회됐다.");sent++;
        }
    }
}
