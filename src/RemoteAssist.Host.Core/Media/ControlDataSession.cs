using System.Text.Json;
using RemoteAssist.Host.Core.Capture;
using RemoteAssist.Host.Core.Security;
using RemoteAssist.Protocol;
using SIPSorcery.Net;

namespace RemoteAssist.Host.Core.Media;

internal sealed class ControlDataSession(RTCDataChannel channel,PermissionGate permissions,PermissionSnapshot binding,MonitorInfo monitor,Action<string> fail,Action? inputLease=null,Action<ControlMessage>? adminCommand=null,Action<ControlMessage>? screenCommand=null,int generation=1,long topology=1,Func<bool>? ownsTransport=null)
{
    readonly object gate=new();
    long sent,received;
    bool ready;
    public void Attach()
    {
        channel.onopen+=()=>
        {
            if(ownsTransport?.Invoke()==false)return;
            try{lock(gate){ready=true;Send("data-ready",Guid.NewGuid().ToString("N"),FeatureContract.Channels((int)binding.Capabilities),FeatureContract.Permissions((int)binding.Capabilities),binding.Capabilities.HasFlag(Capability.Screen)?new(monitor.DeviceName,topology,monitor.X,monitor.Y,monitor.Width,monitor.Height):null);}}
            catch {fail("기능 채널 준비 실패다.");}
        };
        channel.onclose+=()=>fail("기능 채널이 종료됐다.");
        channel.onerror+=_=>fail("기능 채널 전송 실패다.");
        channel.onmessage+=(_,kind,bytes)=>
        {
            if(ownsTransport?.Invoke()==false)return;
            try
            {
                if(kind!=DataChannelPayloadProtocols.WebRTC_String)throw new InvalidDataException("제어 채널은 JSON 텍스트만 허용한다.");
                var message=ControlMessage.Parse(bytes);
                lock(gate)
                {
                    if(!ready||message.SessionId!=binding.SessionId||message.Generation!=generation||message.PermissionGeneration!=binding.Generation||message.Sequence!=received+1||
                        !permissions.Allows(binding.Capabilities,binding.Generation))throw new InvalidDataException("오래되거나 다른 제어 채널이다.");
                    received=message.Sequence;
                    if(message.T=="data-ping")
                    {if(binding.Capabilities.HasFlag(Capability.Control))inputLease?.Invoke();Send("data-pong",message.Id);}
                    else if(message.T is "monitor-list" or "monitor-select" or "quality-report" or "quality-mode"&&binding.Capabilities.HasFlag(Capability.Screen)&&screenCommand!=null&&message.Monitors==null)screenCommand(message);
                    else if(message.T is "admin-request" or "admin-sas" or "admin-revoke"&&binding.Capabilities.HasFlag(Capability.Admin)&&adminCommand!=null)
                    {if(message.T=="admin-request")AdminState(message.Id,"pending");adminCommand(message);}
                    else throw new InvalidDataException("현재 Viewer 제어 명령을 허용하지 않는다.");
                }
            }
            catch {fail("잘못된 기능 채널 메시지다.");}
        };
    }
    public void ResetAcknowledged(InputMsg message)=>Send("input-reset-ack",message.Id,inputEpoch:message.InputEpoch);
    public void AdminState(string id,string state,long? adminGeneration=null,long? inputEpoch=null)=>Send("admin-state",id,state:state,adminGeneration:adminGeneration,inputEpoch:inputEpoch);
    public void LocalApproval(string id,string kind,string state,long? inputEpoch=null)=>Send("local-approval",id,inputEpoch:inputEpoch,state:state,kind:kind);
    public void Monitors(string id,MonitorDescriptor[] monitors)=>Send("monitor-list",id,monitors:monitors);
    public void MonitorChanged(string id,MonitorDescriptor monitor)=>Send("monitor-state",id,descriptor:monitor);
    public void QualityChanged(string id,QualitySetting quality)=>Send("quality-state",id,quality:quality);
    void Send(string type,string id,string[]? channels=null,string[]? capabilities=null,MonitorDescriptor? descriptor=null,long? inputEpoch=null,string? state=null,long? adminGeneration=null,string? kind=null,MonitorDescriptor[]? monitors=null,QualitySetting? quality=null)
    {
        lock(gate)
        {
            if(ownsTransport?.Invoke()==false)throw new OperationCanceledException("협상 세대가 교체됐다.");
            if(channel.readyState!=RTCDataChannelState.open||channel.bufferedAmount>256*1024)throw new IOException("기능 채널 송신 상한/상태 오류다.");
            var message=new ControlMessage {T=type,SessionId=binding.SessionId,Generation=generation,PermissionGeneration=binding.Generation,Sequence=sent+1,Id=id,
                Channels=channels,Permissions=capabilities,Monitor=descriptor,InputEpoch=inputEpoch,State=state,AdminGeneration=adminGeneration,Kind=kind,Monitors=monitors,Quality=quality};
            var bytes=JsonSerializer.SerializeToUtf8Bytes(message);ControlMessage.Parse(bytes);
            if(!permissions.Execute(binding.Capabilities,binding.Generation,()=>channel.send(System.Text.Encoding.UTF8.GetString(bytes))))throw new UnauthorizedAccessException("기능 권한이 폐기됐다.");
            sent++;
        }
    }
}
