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
    Task? mediaLease;
    long lastMediaFrame;
    string? pending;
    TaskCompletionSource<Capability>? consent;
    readonly IpcAcknowledgements acknowledgements=new();
    readonly IpcInputRequests adminInputs=new();
    readonly IpcFeatureRequests clipboard=new();
    readonly IpcLocalRequests files=new();
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
                IInputBackend? reservedBackend=null;
                owned.CreateInputBackend=grant=>{
                    if(reservedBackend is not null)return reservedBackend;
                    reservedBackend=new ReservedInputBackend(grant.SessionId,grant.Generation,new AdminRoutingInputBackend(new WindowsInputBackend(monitorProvider:()=>owned.Monitor),()=>owned.AdminFrame),reservation=>acknowledgements.Request(Peer,"recovery-reserve",reservation,Peer.Token).GetAwaiter().GetResult());
                    RemoteAssist.Host.Core.Diagnostics.CaseTrace.Write("input-backend-ready",grant.SessionId,grant.Generation,new {heldCount=0,epoch=1});
                    return reservedBackend;
                };
                owned.InputLeaseObserved=()=>Peer.Post("recovery-lease");
                owned.AdminCommand=message=>Peer.Post(message.T=="admin-request"?"admin-request":"admin-sas",id:message.Id);
                owned.ChatReceived=message=>Peer.Post("chat",message,message.Id);
                owned.ClipboardOperation=(message,token)=>clipboard.Request(Peer,message,token);
                owned.RequestFileApproval=async (offer,token)=>
                {
                    var result=await files.Request(Peer,"file-request",offer,offer.Id,TimeSpan.FromSeconds(30),token);
                    return result?.GetProperty("folder").GetString();
                };
                owned.AuditFile=async (notice,token)=>
                {var id=Guid.NewGuid().ToString("N");await files.Request(Peer,"file-audit",notice,id,TimeSpan.FromSeconds(2),token);};
                owned.FileState=state=>Peer.Post("file-state",state,state.Id);
                owned.RequestMonitorApproval=async (monitor,token)=>
                {var id=Guid.NewGuid().ToString("N");var reply=await files.Request(Peer,"monitor-request",new {sessionId=owned.Credentials!.SessionId,monitor},id,TimeSpan.FromSeconds(30),token);return reply?.GetProperty("approved").GetBoolean()==true;};
                owned.RunbookOperation=async(request,token)=>
                {
                    using var cancel=token.Register(()=>{try{Peer.Post("runbook-cancel",id:request.Id);}catch(InvalidOperationException){}});
                    var reply=await files.Request(Peer,"runbook-request",request,request.Id,TimeSpan.FromSeconds(55),token);
                    var result=reply?.Deserialize<RemoteAssist.Protocol.RunbookResult>()??throw new InvalidDataException("Runbook 로컬 결과 누락이다.");result.Validate();return result;
                };
                owned.StateChanged+=state=> {try{Peer.Post("state",new {status=state,generation=owned.ConnectionGeneration,active=state is "승인한 화면 공유 중" or "승인한 보조 기능 연결 중",capabilities=(int)owned.GrantedCapabilities,
                    monitor=new RemoteAssist.Protocol.MonitorDescriptor(owned.Monitor.DeviceName,owned.TopologyGeneration,owned.Monitor.X,owned.Monitor.Y,owned.Monitor.Width,owned.Monitor.Height)});}catch(InvalidOperationException){owned.EndSession("로컬 감독 연결 종료");}};
                owned.RequestLocalConsent=async (identity,token)=>
                {
                    consent=new(TaskCreationOptions.RunContinuationsAsynchronously);pending=Guid.NewGuid().ToString("N");
                    var pairing=owned.Credentials??throw new InvalidOperationException("참가 세션이 없다.");
                    Peer.Post("consent-request",new {sessionId=pairing.SessionId,connectionId=identity.ConnectionId,displayName=identity.DisplayName,
                        monitor=owned.Monitor.ToString(),monitorDescriptor=new RemoteAssist.Protocol.MonitorDescriptor(owned.Monitor.DeviceName,1,owned.Monitor.X,owned.Monitor.Y,owned.Monitor.Width,owned.Monitor.Height),available=new[]{"screen","control","admin","file","clipboard","diagnostics","runbook","chat"}},pending);
                    try{return await consent.Task.WaitAsync(token);}finally{pending=null;consent=null;}
                };
                var credentials=await owned.StartAsync(ct);
                Peer.Post("credentials",new {sessionId=credentials.SessionId,code=credentials.DisplayCode});
                owned.FrameSent+=(_,_)=>Interlocked.Exchange(ref lastMediaFrame,Environment.TickCount64);
                mediaLease=ReportMedia(owned);completion=ObserveSession(owned);break;
            case "consent":
                if(pending!=message.Id||consent==null)throw new InvalidDataException("오래되거나 다른 동의 응답이다.");
                var caps=message.Body?.GetProperty("capabilities").GetInt32()??0;
                if(caps!=0){_=RemoteAssist.Protocol.FeatureContract.Permissions(caps);if((caps&~255)!=0)throw new InvalidDataException("미구현 권한은 부여할 수 없다.");}
                session!.InitialSupport=message.Body?.TryGetProperty("initialSupport",out var initial)==true&&initial.GetBoolean();
                consent.TrySetResult((Capability)caps);break;
            case "stop":session?.EndSession("대상자가 즉시 종료했다.");consent?.TrySetCanceled(ct);break;
            case "recovery-ack":acknowledgements.Accept(message);break;
            case "admin-input-ack":adminInputs.Accept(message);break;
            case "clipboard-response":clipboard.Accept(message);break;
            case "file-response":case "file-audit-ack":case "monitor-response":case "runbook-result":files.Accept(message);break;
            case "file-send":
                _=SelectFile(session??throw new InvalidOperationException("지원 세션이 없다."),message.Id,ct);break;
            case "file-cancel":session!.CancelFile(message.Id);break;
            case "chat":
                var local=message.Body??throw new InvalidDataException("로컬 채팅 본문 누락이다.");session!.SendChat(local.GetProperty("text").GetString()!,message.Id);break;
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
    async Task SelectFile(ScreenShareSession owner,string id,CancellationToken ct)
    {
        try
        {
            var path=await owner.WithLocalApproval<string>("file",id,async token=>
            {var reply=await files.Request(Peer,"file-select",new {sessionId=owner.Credentials!.SessionId},id,TimeSpan.FromSeconds(30),token);return reply?.GetProperty("folder").GetString();},ct);
            if(path==null||!owner.SendFile(path,id))Peer.Post("file-state",new RemoteAssist.Host.Core.Files.FileTransferState(id,"from-host",0,path==null?"denied":"busy"),id);
        }
        catch{owner.EndSession("로컬 파일 선택이 실패했다.",true);}
    }
    async Task ObserveSession(ScreenShareSession owned)
    {
        var result=await owned.Completion;
        try{Peer.Post("state",new {status=result.Reason,ended=true,failed=result.Failed});}catch(InvalidOperationException){}
    }
    async Task ReportMedia(ScreenShareSession owned)
    {
        try
        {
            using var timer=new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            while(await timer.WaitForNextTickAsync(Peer.Token)&&!owned.Completion.IsCompleted)
            {var tick=Interlocked.Read(ref lastMediaFrame);if(tick!=0&&Environment.TickCount64-tick<=1000)Peer.Post("media-lease",new {sessionId=owned.Credentials!.SessionId,generation=owned.ConnectionGeneration,tick});}
        }
        catch(OperationCanceledException)when(Peer.Token.IsCancellationRequested){}
        catch{owned.EndSession("미디어 진행 감독 연결 실패다.",true);}
    }
    public async ValueTask DisposeAsync()
    {
        session?.EndSession("로컬 감독 연결 종료");
        if(session!=null)await session.DisposeAsync();
        if(completion!=null)await completion;
        if(mediaLease!=null)await mediaLease;
    }
}
