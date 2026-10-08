using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using RemoteAssist.Host.Core;
using RemoteAssist.Host.Core.Ipc;
using RemoteAssist.Host.Core.Security;
using RemoteAssist.Host.Core.Windows;

namespace RemoteAssist.Host.Service;

/// <summary>Session 0 supervisor. Capture/encoding/WebRTC and input stay in the target user's helpers.</summary>
internal sealed class HostSessionSupervisor(ILogger log,IHostApplicationLifetime lifetime)
{
    readonly object gate=new();
    HelperScope? helper;
    IpcPeerSession? tray;
    ScreenShareOptions options=new();
    uint sessionId;
    string userSid="";
    string? consentId,sessionBinding,supporterBinding;
    string phase="configuration";
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        try
        {
            var config=Path.Combine(AppContext.BaseDirectory,"host.settings.json");
            if(File.Exists(config))
            {
                var bytes=await File.ReadAllBytesAsync(config,stoppingToken);
                options=HostSettings.Parse(bytes);
            }
            options.Validate(256);
            while(!stoppingToken.IsCancellationRequested)
            {
                sessionId=SessionProcess.ActiveConsoleSession;
                if(!SessionProcess.IsInteractiveConsole(sessionId)){await Task.Delay(250,stoppingToken);continue;}
                try{userSid=SessionProcess.ConsoleUserSid(sessionId);break;}
                catch(System.ComponentModel.Win32Exception ex)when(ex.NativeErrorCode==1008){await Task.Delay(250,stoppingToken);}
            }
            if(stoppingToken.IsCancellationRequested)return;
            phase="tray-launch";
            var name=Guid.NewGuid().ToString("N");var nonce=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            using var server=LocalIpcEndpoint.CreateServer(name,userSid);
            using var ui=SessionProcess.Start(HostChildRole.Tray,sessionId,name,nonce);
            Report("tray-launched",childPid:ui.Pid);
            phase="tray-authentication";
            var channel=await LocalIpcEndpoint.AcceptAsync(server,ui,nonce,stoppingToken);
            await using var uiPeer=new IpcPeerSession(channel,HandleTray,stoppingToken,_=>StopHelper("대상자가 즉시 종료했다."),reason=>StopHelper(reason));
            tray=uiPeer;uiPeer.Start();uiPeer.Post("state",new {status="지원 코드를 만들면 대상자의 승인 후 연결한다.",ended=true});
            phase="supervision";
            Report("tray-ready");
            using var timer=new PeriodicTimer(TimeSpan.FromMilliseconds(200));
            while(await timer.WaitForNextTickAsync(stoppingToken))
            {
                if(ui.HasExited||uiPeer.Token.IsCancellationRequested||uiPeer.Completion.IsCompleted){StopHelper("대상자 UI 연결이 종료됐다.");break;}
                if(!SessionProcess.IsInteractiveConsole(sessionId)||SessionProcess.ConsoleUserSid(sessionId)!=userSid)
                {StopHelper("대상 Windows 사용자가 변경됐다.");break;}
                HelperScope? current;lock(gate)current=helper;
                if(current is not null&&(current.Child?.HasExited==true||current.Peer?.Token.IsCancellationRequested==true||current.Peer?.Completion.IsCompleted==true))StopHelper("사용자 헬퍼가 종료됐다.");
            }
        }
        catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){}
        catch(Exception ex){log.LogError("Host 감독 실패: {Kind}",ex.GetType().Name);Report("supervisor-error",ex);}
        finally{StopHelper("Host 서비스 또는 Tray 종료");tray=null;lifetime.StopApplication();}
    }
    async Task HandleTray(LocalIpcMessage message,CancellationToken ct)
    {
        switch(message.Type)
        {
            case "start":await StartHelper(ct);break;
            case "consent":
                lock(gate)
                {
                    if(helper?.Peer is null||consentId!=message.Id||string.IsNullOrEmpty(consentId))throw new InvalidDataException("현재 로컬 동의 요청이 아니다.");
                    var body=message.Body??throw new InvalidDataException("동의 본문 누락");
                    if(body.GetProperty("sessionId").GetString()!=sessionBinding||body.GetProperty("connectionId").GetString()!=supporterBinding)throw new InvalidDataException("동의 대상 바인딩 불일치");
                    var caps=body.GetProperty("capabilities").GetInt32();
                    if(caps is not (0 or (int)Capability.Screen))throw new InvalidDataException("현재 구현하지 않은 권한이다.");
                    helper.Peer.Post("consent",new {capabilities=caps},message.Id);consentId=null;
                }
                break;
            default:throw new InvalidDataException("Tray에 허용되지 않은 IPC다.");
        }
    }
    async Task StartHelper(CancellationToken ct)
    {
        var scope=new HelperScope(ct);
        lock(gate)
        {
            if(helper!=null){scope.StartupFinished.TrySetResult();scope.Dispose();tray?.Post("state",new {status="지원 세션이 이미 진행 중이다."});return;}
            helper=scope;consentId=sessionBinding=supporterBinding=null;
        }
        try
        {
            var name=Guid.NewGuid().ToString("N");var nonce=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            scope.Token.ThrowIfCancellationRequested();
            var server=LocalIpcEndpoint.CreateServer(name,userSid);scope.Adopt(server);
            scope.Token.ThrowIfCancellationRequested();
            var child=SessionProcess.Start(HostChildRole.Helper,sessionId,name,nonce);scope.Adopt(child);Report("helper-launched");
            var channel=await LocalIpcEndpoint.AcceptAsync(server,child,nonce,scope.Token);
            var peer=new IpcPeerSession(channel,(message,token)=>HandleHelper(scope,message,token),scope.Token,onStopped:reason=>
            {lock(gate){if(helper==scope)StopHelper(reason);}});scope.Adopt(peer);
            scope.Token.ThrowIfCancellationRequested();
            peer.Start();peer.Post("config",options);peer.Post("start");
        }
        catch(Exception ex){Report("helper-error",ex);StopHelper("사용자 헬퍼 시작 실패");throw;}
        finally{scope.StartupFinished.TrySetResult();}
    }
    Task HandleHelper(HelperScope scope,LocalIpcMessage message,CancellationToken ct)
    {
        lock(gate)
        {
            if(helper!=scope)return Task.CompletedTask;
            switch(message.Type)
            {
                case "credentials":
                    var credentials=message.Body??throw new InvalidDataException("참가 표시 누락");
                    sessionBinding=credentials.GetProperty("sessionId").GetString();tray?.Post("credentials",credentials);break;
                case "consent-request":
                    var body=message.Body??throw new InvalidDataException("동의 요청 누락");
                    if(consentId is not null||body.GetProperty("sessionId").GetString()!=sessionBinding||!Guid.TryParseExact(message.Id,"N",out _))throw new InvalidDataException("동의 요청 상태 오류");
                    supporterBinding=body.GetProperty("connectionId").GetString();consentId=message.Id;tray?.Post("consent-request",body,message.Id);break;
                case "state":
                    tray?.Post("state",message.Body);
                    if(message.Body?.TryGetProperty("ended",out var ended)==true&&ended.GetBoolean())StopHelper(message.Body.Value.GetProperty("status").GetString()??"지원 세션 종료");
                    break;
                default:throw new InvalidDataException("일반 헬퍼의 허용되지 않은 통지다.");
            }
        }
        return Task.CompletedTask;
    }
    void StopHelper(string reason)
    {
        HelperScope? scope;
        lock(gate){scope=helper;helper=null;consentId=sessionBinding=supporterBinding=null;}
        if(scope is null)return;
        // Owned job termination bypasses a hung native call or a blocked consent/IPC actor.
        scope.Dispose();Report("helper-stopped");
        try{tray?.Post("state",new {status=reason,ended=true});}catch(InvalidOperationException){}
    }
    void Report(string kind,Exception? error=null,int? childPid=null)
    {
        // Metadata only. Never serialize an exception message, IPC body, credentials or bootstrap.
        var path=Path.Combine(AppContext.BaseDirectory,"service-status.ndjson");
        try
        {
            lock(gate)
            {
                if(File.Exists(path)&&new FileInfo(path).Length>4*1024*1024)throw new IOException("감독 기록 상한 초과");
                File.AppendAllText(path,JsonSerializer.Serialize(new {utc=DateTimeOffset.UtcNow,kind,phase,sessionId,childPid,error=error?.GetType().Name,
                    nativeError=(error as System.ComponentModel.Win32Exception)?.NativeErrorCode,stack=error?.StackTrace})+Environment.NewLine);
            }
        }
        catch(Exception ex){log.LogError("감독 메타데이터 기록 실패: {Kind}",ex.GetType().Name);}
    }
    sealed class HelperScope : IDisposable
    {
        readonly object ownership=new();
        readonly CancellationTokenSource lifetime;
        public CancellationToken Token {get;}
        public TaskCompletionSource StartupFinished {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        NamedPipeServerStream? server;
        SessionProcess? child;
        IpcPeerSession? peer;
        public SessionProcess? Child {get{lock(ownership)return child;}}
        public IpcPeerSession? Peer {get{lock(ownership)return peer;}}
        bool disposed;
        public HelperScope(CancellationToken ct){lifetime=CancellationTokenSource.CreateLinkedTokenSource(ct);Token=lifetime.Token;}
        public void Adopt(NamedPipeServerStream value)
        {lock(ownership){if(!disposed){server=value;return;}}value.Dispose();throw new OperationCanceledException(Token);}
        public void Adopt(SessionProcess value)
        {lock(ownership){if(!disposed){child=value;return;}}value.Dispose();throw new OperationCanceledException(Token);}
        public void Adopt(IpcPeerSession value)
        {lock(ownership){if(!disposed){peer=value;return;}}_=ObserveDispose(value);throw new OperationCanceledException(Token);}
        public void Dispose()
        {
            SessionProcess? ownedChild;NamedPipeServerStream? ownedServer;IpcPeerSession? ownedPeer;
            lock(ownership){if(disposed)return;disposed=true;ownedChild=child;ownedServer=server;ownedPeer=peer;}
            try{lifetime.Cancel();}catch(AggregateException){}
            ownedChild?.Dispose();ownedServer?.Dispose();
            _=CompleteCleanup(ownedPeer);
        }
        async Task CompleteCleanup(IpcPeerSession? ownedPeer)
        {await StartupFinished.Task;if(ownedPeer!=null)await ObserveDispose(ownedPeer);lifetime.Dispose();}
        static async Task ObserveDispose(IpcPeerSession peer){try{await peer.DisposeAsync();}catch{}}
    }
}
