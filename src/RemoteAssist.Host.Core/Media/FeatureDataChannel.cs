using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using RemoteAssist.Host.Core.Security;
using RemoteAssist.Protocol;
using SIPSorcery.Net;

namespace RemoteAssist.Host.Core.Media;

internal sealed class FeatureDataChannel(RTCDataChannel channel,PermissionGate permissions,PermissionSnapshot binding,CancellationToken ct,Func<FeatureMessage,CancellationToken,Task> handle,Action<string> fail,long generation=1)
{
    readonly object gate=new();
    readonly Channel<FeatureMessage> queue=Channel.CreateBounded<FeatureMessage>(64);
    readonly HashSet<string> seen=new(StringComparer.Ordinal);
    readonly Queue<string> ids=new();
    readonly FeatureQuota chatQuota=new(5),clipboardQuota=new(2);
    long received,sent;
    public Task Completion {get;private set;}=Task.CompletedTask;
    public bool IsOpen=>channel.readyState==RTCDataChannelState.open;
    public void Attach()
    {
        channel.onclose+=()=>fail("보조 기능 채널 종료다.");channel.onerror+=_=>fail("보조 기능 채널 실패다.");
        channel.onmessage+=(_,kind,bytes)=>
        {
            if(ct.IsCancellationRequested)return;
            try
            {
                if(kind!=DataChannelPayloadProtocols.WebRTC_String||channel.label!="chat")throw new InvalidDataException("보조 기능 수신 방향 오류다.");
                var message=FeatureMessage.Parse(bytes);
                var cap=message.T switch {"chat"=>Capability.Chat,"clipboard-get" or "clipboard-set"=>Capability.Clipboard,_=>throw new InvalidDataException("Viewer의 허용되지 않은 보조 명령이다.")};
                lock(gate)
                {
                    if(message.SessionId!=binding.SessionId||message.Generation!=generation||message.PermissionGeneration!=binding.Generation||message.Sequence!=received+1||!permissions.Allows(cap,binding.Generation)||!seen.Add(message.Id))throw new UnauthorizedAccessException("보조 기능 권한·순번·재생 오류다.");
                    ids.Enqueue(message.Id);if(ids.Count>256)seen.Remove(ids.Dequeue());received=message.Sequence;
                    if(!(cap==Capability.Chat?chatQuota:clipboardQuota).Allow())throw new IOException("보조 기능 요청 빈도 상한이다.");
                    if(!queue.Writer.TryWrite(message))throw new IOException("보조 기능 수신 큐 상한이다.");
                }
            }
            catch{fail("보조 기능 요청을 거부했다.");}
        };
        Completion=Run();
    }
    async Task Run()
    {
        try{await foreach(var message in queue.Reader.ReadAllAsync(ct))await handle(message,ct);}
        catch(OperationCanceledException)when(ct.IsCancellationRequested){}
        catch{fail("보조 기능 처리 실패다.");}
        finally{lock(gate){while(queue.Reader.TryRead(out _)){}seen.Clear();ids.Clear();}}
    }
    public void Send(FeatureMessage message,Capability cap)
    {
        lock(gate)
        {
            if(ct.IsCancellationRequested||!IsOpen||channel.bufferedAmount>64*1024)throw new IOException("보조 기능 송신 상태·배압 오류다.");
            var value=message with {Version=1,SessionId=binding.SessionId,Generation=generation,PermissionGeneration=binding.Generation,Sequence=sent+1};
            var bytes=ProtocolJson.Encode(value);FeatureMessage.Parse(bytes);
            if(!permissions.Execute(cap,binding.Generation,()=>channel.send(Encoding.UTF8.GetString(bytes))))throw new UnauthorizedAccessException("보조 기능 송신 권한 종료다.");sent++;
        }
    }
}

public sealed class FeatureQuota(int perSecond,TimeProvider? time=null)
{
    readonly object gate=new();readonly TimeProvider clock=time??TimeProvider.System;
    readonly int limit=perSecond is >=1 and <=100?perSecond:throw new ArgumentOutOfRangeException(nameof(perSecond));
    long start;int count;
    public bool Allow()
    {
        lock(gate)
        {
            var now=clock.GetTimestamp();if(count==0||clock.GetElapsedTime(start,now)>=TimeSpan.FromSeconds(1)){start=now;count=0;}
            if(count>=limit)return false;count++;return true;
        }
    }
}
