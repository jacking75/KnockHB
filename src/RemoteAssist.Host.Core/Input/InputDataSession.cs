using System.Threading.Channels;
using RemoteAssist.Protocol;
using SIPSorcery.Net;

namespace RemoteAssist.Host.Core.Input;

internal sealed class InputDataSession(InputSession input,CancellationToken ct,Action<string> fail,Action<InputMsg> resetAcknowledged)
{
    readonly Channel<byte[]> reliable=Channel.CreateBounded<byte[]>(128);
    readonly Channel<bool> wake=Channel.CreateBounded<bool>(new BoundedChannelOptions(1){FullMode=BoundedChannelFullMode.DropWrite});
    readonly object gate=new();
    byte[]? pointer;
    public Task Completion {get;private set;}=Task.CompletedTask;
    public void Suspend(){input.Suspend();lock(gate)pointer=null;}
    public long Resume()=>input.Resume();
    public void RebindMonitor(RemoteAssist.Host.Core.Capture.MonitorInfo monitor,long generation)=>input.RebindMonitor(monitor,generation);
    public void Start()=>Completion=Run();
    public void Attach(RTCDataChannel channel)
    {
        channel.onclose+=()=>fail("입력 채널이 종료됐다.");channel.onerror+=_=>fail("입력 채널 실패다.");
        channel.onmessage+=(_,kind,bytes)=>
        {
            if(ct.IsCancellationRequested)return;
            try
            {
                if(kind!=DataChannelPayloadProtocols.WebRTC_String)throw new InvalidDataException("입력은 JSON 텍스트다.");
                var message=ProtocolJson.ParseInput(bytes);
                if(channel.label=="pointer")
                {
                    if(message.T!="mouse"||message.A!="move")throw new InvalidDataException("pointer에는 move만 허용한다.");
                    lock(gate)pointer=bytes.ToArray();
                }
                else if(!reliable.Writer.TryWrite(bytes.ToArray()))throw new IOException("입력 수신 큐 상한이다.");
                wake.Writer.TryWrite(true);
            }
            catch{fail("잘못된 입력 메시지다.");}
        };
    }
    async Task Run()
    {
        try
        {
            await foreach(var _ in wake.Reader.ReadAllAsync(ct))
            {
                while(reliable.Reader.TryRead(out var bytes))
                {
                    ct.ThrowIfCancellationRequested();
                    if(input.Dispatch("input",bytes)) {var message=ProtocolJson.ParseInput(bytes);if(message.T=="reset")resetAcknowledged(message);}
                }
                byte[]? latest;lock(gate){latest=pointer;pointer=null;}
                if(latest!=null){ct.ThrowIfCancellationRequested();input.Dispatch("pointer",latest);}
            }
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){}
        catch{fail("원격 입력 처리 실패다.");}
        finally
        {
            try{input.Stop();}catch{fail("원격 입력 해제 실패다.");}
            lock(gate)pointer=null;while(reliable.Reader.TryRead(out _)){}
        }
    }
}
