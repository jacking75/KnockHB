using System.Runtime.InteropServices;
using System.Text.Json;
using RemoteAssist.Host.Core;
using RemoteAssist.Host.Core.Files;
using RemoteAssist.Host.Core.Ipc;
using RemoteAssist.Host.Core.Security;
using RemoteAssist.Host.Core.Input;
using RemoteAssist.Protocol;

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

/// <summary>
/// 대상 사용자 세션의 일반 권한 헬퍼. WebRTC·캡처·인코딩을 맡고, 관리자 입력·Runbook·클립보드는
/// 감독 서비스를 거쳐 처리한다.
/// </summary>
sealed class HelperRuntime : IAsyncDisposable
{
    public IpcPeerSession Peer {get;set;}=null!;
    ScreenShareSession? session;
    ScreenShareOptions? options;
    Task? completion;
    readonly IpcInputRequests adminInputs=new();
    readonly IpcFeatureRequests clipboard=new();
    readonly IpcLocalRequests requests=new();

    public async Task Handle(LocalIpcMessage message,CancellationToken ct)
    {
        switch(message.Type)
        {
            case "config":
                if(options!=null)throw new InvalidDataException("중복 헬퍼 설정이다.");
                options=message.Body?.Deserialize<ScreenShareOptions>()??throw new InvalidDataException("헬퍼 설정 누락이다.");break;
            case "start":
                if(options==null||session!=null)throw new InvalidDataException("헬퍼 시작 상태 오류다.");
                await Start(options,ct);break;
            case "stop":session?.EndSession("대상자가 즉시 종료했다.");break;
            case "admin-input-ack":adminInputs.Accept(message);break;
            case "clipboard-response":clipboard.Accept(message);break;
            case "file-audit-ack":case "runbook-result":requests.Accept(message);break;
            case "file-send":
                var path=message.Body?.GetProperty("path").GetString()??throw new InvalidDataException("보낼 파일 경로 누락이다.");
                if(session?.SendFile(path,message.Id)!=true)Peer.Post("file-state",new FileTransferState(message.Id,"from-host",0,"busy"),message.Id);
                break;
            case "file-cancel":session?.CancelFile(message.Id);break;
            case "chat":session?.SendChat(message.Body?.GetProperty("text").GetString()??throw new InvalidDataException("채팅 본문 누락이다."),message.Id);break;
            case "admin-state":
                var state=message.Body??throw new InvalidDataException("관리자 상태 누락이다.");
                if(state.GetProperty("state").GetString()=="active")
                {
                    var approval=state.GetProperty("approval").Deserialize<AdminApproval>()??throw new InvalidDataException("관리자 세대 누락이다.");
                    if(approval.RequestId!=message.Id)throw new InvalidDataException("관리자 응답 ID 불일치다.");
                    session!.ActivateAdmin(new(approval,state.GetProperty("mapping").GetString()!,batch=>adminInputs.Request(Peer,batch,Peer.Token).GetAwaiter().GetResult()));
                }
                else session!.DenyAdmin(message.Id);
                break;
            default:throw new InvalidDataException("일반 헬퍼에 허용되지 않은 IPC 요청이다.");
        }
    }

    async Task Start(ScreenShareOptions settings,CancellationToken ct)
    {
        var owned=new ScreenShareSession(settings);session=owned;
        // 관리자 모드가 시작되면 입력은 관리자 헬퍼로 간다. 그 전에는 이 프로세스가 직접 주입한다.
        owned.CreateInputBackend=_=>new AdminRoutingInputBackend(new WindowsInputBackend(monitorProvider:()=>owned.Monitor),()=>owned.AdminFrame);
        owned.SupporterJoined=supporter=>Peer.Post("supporter",new {sessionId=owned.Credentials!.SessionId,connectionId=supporter.ConnectionId,displayName=supporter.DisplayName,monitor=Descriptor(owned)});
        owned.AdminCommand=command=>Peer.Post(command.T,id:command.Id);
        owned.AdminMonitorChanged=monitor=>Peer.Post("admin-monitor",monitor);
        owned.ChatReceived=chat=>Peer.Post("chat",chat,chat.Id);
        owned.ClipboardOperation=(request,token)=>clipboard.Request(Peer,request,token);
        owned.IncomingFileFolder=(_,_)=>Task.FromResult<string?>(DownloadFolder.Resolve());
        owned.AuditFile=async (notice,token)=>{var id=Guid.NewGuid().ToString("N");await requests.Request(Peer,"file-audit",notice,id,TimeSpan.FromSeconds(2),token);};
        owned.FileState=state=>Peer.Post("file-state",state,state.Id);
        owned.RunbookOperation=async (request,token)=>
        {
            using var cancel=token.Register(()=>{try{Peer.Post("runbook-cancel",id:request.Id);}catch(InvalidOperationException){}});
            var reply=await requests.Request(Peer,"runbook-request",request,request.Id,TimeSpan.FromSeconds(55),token);
            var result=reply?.Deserialize<RunbookResult>()??throw new InvalidDataException("Runbook 결과 누락이다.");result.Validate();return result;
        };
        owned.StateChanged+=state=>
        {
            try{Peer.Post("state",new {status=state,generation=owned.ConnectionGeneration,active=state==ScreenShareSession.SharingState,monitor=Descriptor(owned)});}
            catch(InvalidOperationException){owned.EndSession("로컬 감독 연결 종료");}
        };
        var credentials=await owned.StartAsync(ct);
        Peer.Post("credentials",new {sessionId=credentials.SessionId,code=credentials.DisplayCode});
        completion=ObserveSession(owned);
    }

    static MonitorDescriptor Descriptor(ScreenShareSession owned)
        =>new(owned.Monitor.DeviceName,owned.TopologyGeneration,owned.Monitor.X,owned.Monitor.Y,owned.Monitor.Width,owned.Monitor.Height);

    async Task ObserveSession(ScreenShareSession owned)
    {
        var result=await owned.Completion;
        try{Peer.Post("state",new {status=result.Reason,ended=true,failed=result.Failed});}catch(InvalidOperationException){}
    }
    public void Stop()=>session?.EndSession("대상자가 즉시 종료했다.");
    public async ValueTask DisposeAsync()
    {
        session?.EndSession("로컬 감독 연결 종료");
        if(session!=null)await session.DisposeAsync();
        if(completion!=null)await completion;
    }
}
