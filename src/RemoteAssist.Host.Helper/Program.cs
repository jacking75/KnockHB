using System.Runtime.InteropServices;
using System.Text.Json;
using RemoteAssist.Host.Core;
using RemoteAssist.Host.Core.Ipc;
using RemoteAssist.Host.Core.Security;
using RemoteAssist.Host.Core.Input;

SetProcessDpiAwarenessContext(-4);
try
{
    var bootstrap=IpcBootstrap.Parse(args);
    using var stop=new CancellationTokenSource();
    await using var channel=await LocalIpcEndpoint.ConnectAsync(bootstrap.Pipe,bootstrap.Nonce,bootstrap.ServicePid,"helper",stop.Token);
    await using var runtime=new HelperRuntime();
    await using var peer=new IpcPeerSession(channel,runtime.Handle,stop.Token,_=>runtime.Stop(),_=>runtime.Stop());
    runtime.Peer=peer;
    peer.Start();
    await peer.Completion;return 0;
}
catch(Exception ex){LocalDiagnostic.Failure("helper",ex);Console.Error.WriteLine("사용자 헬퍼 실패: "+ex.GetType().Name);return 1;}
[DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(nint value);

sealed class HelperRuntime : IAsyncDisposable
{
    public IpcPeerSession Peer {get;set;}=null!;
    ScreenShareSession? session;
    ScreenShareOptions? options;
    Task? completion;
    string? pending;
    TaskCompletionSource<Capability>? consent;
    readonly IpcAcknowledgements acknowledgements=new();
    readonly IpcInputRequests adminInputs=new();
    public async Task Handle(LocalIpcMessage message,CancellationToken ct)
    {
        switch(message.Type)
        {
            case "config":
                if(options!=null)throw new InvalidDataException("중복 헬퍼 설정이다.");
                options=message.Body?.Deserialize<ScreenShareOptions>()??throw new InvalidDataException("헬퍼 설정 누락이다.");break;
            case "start":
                if(options==null||session!=null)throw new InvalidDataException("헬퍼 시작 상태 오류다.");
                var owned=new ScreenShareSession(options);session=owned;
                owned.CreateInputBackend=grant=>new ReservedInputBackend(grant.SessionId,grant.Generation,new AdminRoutingInputBackend(new WindowsInputBackend(owned.Monitor),()=>owned.AdminFrame),reservation=>acknowledgements.Request(Peer,"recovery-reserve",reservation,Peer.Token).GetAwaiter().GetResult());
                owned.InputLeaseObserved=()=>Peer.Post("recovery-lease");
                owned.AdminCommand=message=>Peer.Post(message.T=="admin-request"?"admin-request":"admin-sas",id:message.Id);
                owned.StateChanged+=state=> {try{Peer.Post("state",new {status=state,active=state=="승인한 화면 공유 중",capabilities=(int)owned.GrantedCapabilities,
                    monitor=new RemoteAssist.Protocol.MonitorDescriptor(owned.Monitor.DeviceName,1,owned.Monitor.X,owned.Monitor.Y,owned.Monitor.Width,owned.Monitor.Height)});}catch(InvalidOperationException){owned.EndSession("로컬 감독 연결 종료");}};
                owned.RequestLocalConsent=async (identity,token)=>
                {
                    consent=new(TaskCreationOptions.RunContinuationsAsynchronously);pending=Guid.NewGuid().ToString("N");
                    var pairing=owned.Credentials??throw new InvalidOperationException("참가 세션이 없다.");
                    Peer.Post("consent-request",new {sessionId=pairing.SessionId,connectionId=identity.ConnectionId,displayName=identity.DisplayName,
                        monitor=owned.Monitor.ToString(),monitorDescriptor=new RemoteAssist.Protocol.MonitorDescriptor(owned.Monitor.DeviceName,1,owned.Monitor.X,owned.Monitor.Y,owned.Monitor.Width,owned.Monitor.Height),available=new[]{"screen","control","admin"}},pending);
                    try{return await consent.Task.WaitAsync(token);}finally{pending=null;consent=null;}
                };
                var credentials=await owned.StartAsync(ct);
                Peer.Post("credentials",new {sessionId=credentials.SessionId,code=credentials.DisplayCode,pin=credentials.Pin});
                completion=ObserveSession(owned);break;
            case "consent":
                if(pending!=message.Id||consent==null)throw new InvalidDataException("오래되거나 다른 동의 응답이다.");
                var caps=message.Body?.GetProperty("capabilities").GetInt32()??0;
                if(caps is not (0 or 1 or 3 or 7))throw new InvalidDataException("미구현 권한은 부여할 수 없다.");
                consent.TrySetResult((Capability)caps);break;
            case "stop":session?.EndSession("대상자가 즉시 종료했다.");consent?.TrySetCanceled(ct);break;
            case "recovery-ack":acknowledgements.Accept(message);break;
            case "admin-input-ack":adminInputs.Accept(message);break;
            case "admin-state":
                var state=message.Body??throw new InvalidDataException("관리자 상태 누락이다.");
                if(state.GetProperty("state").GetString()=="denied")session!.DenyAdmin(message.Id);
                else if(state.GetProperty("state").GetString()=="active")
                {
                    var approval=state.GetProperty("approval").Deserialize<AdminApproval>()??throw new InvalidDataException("관리자 승인 누락이다.");
                    if(approval.RequestId!=message.Id)throw new InvalidDataException("관리자 응답 ID 불일치다.");
                    session!.ActivateAdmin(new(approval,state.GetProperty("mapping").GetString()!,batch=>adminInputs.Request(Peer,batch,Peer.Token).GetAwaiter().GetResult()));
                }
                else throw new InvalidDataException("관리자 상태 오류다.");break;
            default:throw new InvalidDataException("일반 헬퍼에 허용되지 않은 IPC 요청이다.");
        }
    }
    public void Stop(){session?.EndSession("대상자가 즉시 종료했다.");consent?.TrySetCanceled();}
    async Task ObserveSession(ScreenShareSession owned)
    {
        var result=await owned.Completion;
        try{Peer.Post("state",new {status=result.Reason,ended=true,failed=result.Failed});}catch(InvalidOperationException){}
    }
    public async ValueTask DisposeAsync()
    {
        session?.EndSession("로컬 감독 연결 종료");
        if(session!=null)await session.DisposeAsync();
        if(completion!=null)await completion;
    }
}
