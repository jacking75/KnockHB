using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using RemoteAssist.Host.Core;
using RemoteAssist.Host.Core.Ipc;
using RemoteAssist.Host.Core.Security;
using RemoteAssist.Host.Core.Windows;
using RemoteAssist.Host.Core.Input;
using RemoteAssist.Protocol;

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
    AuditJournal? audit;
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        try
        {
            audit=AuditJournal.OpenProtected();
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
                if(current?.Recovery?.HasExited==true)StopHelper("입력 복원 감독이 종료됐다.");
                if(current?.Admin?.HasExited==true||current?.AdminApproval is {} adminApproval&&!current.AdminAuthorization!.Allows(adminApproval.AdminGeneration))StopHelper("관리자 승인 또는 헬퍼가 종료됐다.");
                if(current?.AdminAuthorization?.PendingExpired==true)StopHelper("관리자 승인 요청이 만료돼 지원 세션을 종료했다.");
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
                HelperScope approvedScope;int caps;string currentSession,currentConnection;
                lock(gate)
                {
                    if(helper?.Peer is null||consentId!=message.Id||string.IsNullOrEmpty(consentId))throw new InvalidDataException("현재 로컬 동의 요청이 아니다.");
                    var body=message.Body??throw new InvalidDataException("동의 본문 누락");
                    if(body.GetProperty("sessionId").GetString()!=sessionBinding||body.GetProperty("connectionId").GetString()!=supporterBinding)throw new InvalidDataException("동의 대상 바인딩 불일치");
                    caps=body.GetProperty("capabilities").GetInt32();
                    if(caps is not (0 or 1 or 3 or 7))throw new InvalidDataException("현재 구현하지 않은 권한이다.");
                    approvedScope=helper;currentSession=sessionBinding!;currentConnection=supporterBinding!;consentId=null;
                    if((caps&4)!=0)approvedScope.AdminAuthorization=new(new(currentSession,currentConnection,1,(Capability)caps,DateTimeOffset.UtcNow.AddHours(2)));
                }
                audit!.Append(caps==0?AuditKind.ConsentDenied:AuditKind.ConsentGranted,caps==0?AuditResult.Denied:AuditResult.Allowed,currentSession,currentConnection,caps==0?0:1);
                if((caps&2)!=0)
                {
                    var recovery=new RecoveryScope();approvedScope.Adopt(recovery);
                    await recovery.Start(sessionId,userSid,approvedScope.Child!.Pid,currentSession,approvedScope.Token,reason=>
                    {lock(gate){if(helper==approvedScope)StopHelper(reason);}});
                }
                approvedScope.Token.ThrowIfCancellationRequested();
                approvedScope.Peer!.Post("consent",new {capabilities=caps},message.Id);
                break;
            case "admin-response":
                HelperScope adminOwner;AdminApproval? approved;MonitorDescriptor monitor;string adminSession,adminConnection;
                lock(gate)
                {
                    adminOwner=helper??throw new UnauthorizedAccessException("진행 중인 지원이 없다.");
                    var response=message.Body??throw new InvalidDataException("관리자 응답 누락이다.");
                    if(response.GetProperty("sessionId").GetString()!=sessionBinding||response.GetProperty("connectionId").GetString()!=supporterBinding||response.GetProperty("permissionGeneration").GetInt64()!=1)throw new UnauthorizedAccessException("관리자 로컬 응답 대상 불일치다.");
                    approved=(adminOwner.AdminAuthorization??throw new UnauthorizedAccessException("관리자 요청 승인 범위가 없다.")).RespondLocally(message.Id,response.GetProperty("approved").GetBoolean());
                    adminSession=sessionBinding!;adminConnection=supporterBinding!;
                    monitor=adminOwner.Monitor??throw new InvalidOperationException("활성 모니터가 없다.");adminOwner.AdminApproval=approved;
                }
                audit!.Append(approved is null?AuditKind.AdminDenied:AuditKind.AdminGranted,approved is null?AuditResult.Denied:AuditResult.Allowed,adminSession,adminConnection,1);
                if(approved is null){adminOwner.Peer!.Post("admin-state",new {state="denied"},message.Id);break;}
                var privileged=new AdminScope();adminOwner.Adopt(privileged);
                await privileged.Start(new(approved,sessionId,userSid,monitor,"",options.TargetFps,options.MaxOutputLongEdge,options.MaxOutputShortEdge),adminOwner.AdminAuthorization!.ProcessGrant(approved),adminOwner.Recovery!,adminOwner.Token,
                    reason=>{lock(gate){if(helper==adminOwner)StopHelper(reason);}});
                adminOwner.Token.ThrowIfCancellationRequested();
                adminOwner.Peer!.Post("admin-state",new {state="active",approval=approved,mapping=privileged.Mapping},message.Id);
                tray?.Post("state",new {status="관리자 지원 중 · 보안 화면은 Ctrl+Alt+Shift+F9 또는 현장 종료 버튼으로 종료한다.",active=true,capabilities=7,monitor});
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
    async Task HandleHelper(HelperScope scope,LocalIpcMessage message,CancellationToken ct)
    {
        if(message.Type=="admin-input")
        {
            AdminScope admin;AdminInputBatch batch;
            lock(gate)
            {
                batch=message.Body?.Deserialize<AdminInputBatch>()??throw new InvalidDataException("관리자 입력 본문 누락이다.");AdminInputValidation.Validate(batch);
                if(helper!=scope||scope.Admin is null||scope.AdminAuthorization?.Allows(batch.AdminGeneration)!=true)throw new UnauthorizedAccessException("현재 관리자 승인과 일치하지 않는다.");admin=scope.Admin;
            }
            var count=await admin.Input(batch,ct);ct.ThrowIfCancellationRequested();scope.Peer!.Post("admin-input-ack",new AdminInputResult(count),message.Id);return;
        }
        if(message.Type=="recovery-reserve")
        {
            RecoveryScope recovery;RecoveryReservation reservation;
            lock(gate)
            {
                if(helper!=scope||scope.Recovery is null)throw new UnauthorizedAccessException("일반 제어 복원 승인이 없다.");
                recovery=scope.Recovery;reservation=message.Body?.Deserialize<RecoveryReservation>()??throw new InvalidDataException("입력 예약 누락");
                if(reservation.SessionId!=sessionBinding||reservation.PermissionGeneration!=1)throw new UnauthorizedAccessException("현재 입력 세션이 아니다.");
            }
            await recovery.Reserve(reservation,ct);ct.ThrowIfCancellationRequested();scope.Peer!.Post("recovery-ack",id:message.Id);return;
        }
        lock(gate)
        {
            if(helper!=scope)return;
            switch(message.Type)
            {
                case "credentials":
                    var credentials=message.Body??throw new InvalidDataException("참가 표시 누락");
                    sessionBinding=credentials.GetProperty("sessionId").GetString();
                    audit!.Append(AuditKind.SessionCreated,AuditResult.Completed,sessionBinding??"","",0);
                    tray?.Post("credentials",credentials);break;
                case "consent-request":
                    var body=message.Body??throw new InvalidDataException("동의 요청 누락");
                    if(consentId is not null||body.GetProperty("sessionId").GetString()!=sessionBinding||!Guid.TryParseExact(message.Id,"N",out _))throw new InvalidDataException("동의 요청 상태 오류");
                    supporterBinding=body.GetProperty("connectionId").GetString();scope.SupporterName=body.GetProperty("displayName").GetString()??"지원자";
                    scope.Monitor=body.GetProperty("monitorDescriptor").Deserialize<MonitorDescriptor>();consentId=message.Id;tray?.Post("consent-request",body,message.Id);break;
                case "state":
                    if(message.Body?.TryGetProperty("active",out var active)==true&&active.GetBoolean())scope.Monitor=message.Body.Value.GetProperty("monitor").Deserialize<MonitorDescriptor>();
                    tray?.Post("state",message.Body);
                    if(message.Body?.TryGetProperty("ended",out var ended)==true&&ended.GetBoolean())StopHelper(message.Body.Value.GetProperty("status").GetString()??"지원 세션 종료");
                    break;
                case "recovery-lease":
                    (scope.Recovery??throw new UnauthorizedAccessException("복원 승인이 없다.")).Lease();scope.Admin?.Lease();break;
                case "admin-request":
                    if(scope.Monitor is null||scope.AdminAuthorization is null||message.Body!=null)throw new UnauthorizedAccessException("관리자 요청 승인 범위가 없다.");
                    scope.AdminAuthorization.Request(message.Id);
                    tray?.Post("admin-request",new {sessionId=sessionBinding,connectionId=supporterBinding,permissionGeneration=1,displayName=scope.SupporterName,monitor=scope.Monitor.Identity},message.Id);
                    break;
                case "admin-sas":
                    if(message.Body!=null||scope.AdminApproval is null||scope.AdminAuthorization?.Allows(scope.AdminApproval.AdminGeneration)!=true)throw new UnauthorizedAccessException("SAS 관리자 승인이 없다.");
                    audit!.Append(AuditKind.SasRequested,AuditResult.Allowed,sessionBinding??"",supporterBinding??"",1);
                    SasBroker.Send();break;
                default:throw new InvalidDataException("일반 헬퍼의 허용되지 않은 통지다.");
            }
        }
    }
    void StopHelper(string reason)
    {
        HelperScope? scope;
        string endedSession,endedConnection;
        lock(gate){scope=helper;endedSession=sessionBinding??"";endedConnection=supporterBinding??"";helper=null;consentId=sessionBinding=supporterBinding=null;}
        if(scope is null)return;
        // Owned job termination bypasses a hung native call or a blocked consent/IPC actor.
        scope.Dispose();Report("helper-stopped");
        if(scope.AdminApproval!=null)try{audit?.Append(AuditKind.AdminRevoked,AuditResult.Stopped,endedSession,endedConnection,1);}catch(Exception ex){Report("audit-admin-stop-error",ex);}
        try{audit?.Append(AuditKind.SessionEnded,AuditResult.Stopped,endedSession,endedConnection,0);}catch(Exception ex){Report("audit-stop-error",ex);}
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
        RecoveryScope? recovery;
        AdminScope? admin;
        public AdminScope? Admin {get{lock(ownership)return admin;}}
        public AdminAuthorization? AdminAuthorization {get;set;}
        public AdminApproval? AdminApproval {get;set;}
        public MonitorDescriptor? Monitor {get;set;}
        public string SupporterName {get;set;}="지원자";
        public RecoveryScope? Recovery {get{lock(ownership)return recovery;}}
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
        public void Adopt(RecoveryScope value)
        {lock(ownership){if(!disposed){recovery=value;return;}}value.Dispose();throw new OperationCanceledException(Token);}
        public void Adopt(AdminScope value)
        {lock(ownership){if(!disposed&&admin==null){admin=value;return;}}value.Dispose();throw new OperationCanceledException(Token);}
        public void Dispose()
        {
            SessionProcess? ownedChild;NamedPipeServerStream? ownedServer;IpcPeerSession? ownedPeer;
            lock(ownership){if(disposed)return;disposed=true;ownedChild=child;ownedServer=server;ownedPeer=peer;}
            try{lifetime.Cancel();}catch(AggregateException){}
            AdminAuthorization?.End();admin?.Dispose();
            ownedChild?.Dispose();ownedServer?.Dispose();
            recovery?.Dispose();
            _=CompleteCleanup(ownedPeer);
        }
        async Task CompleteCleanup(IpcPeerSession? ownedPeer)
        {await StartupFinished.Task;if(ownedPeer!=null)await ObserveDispose(ownedPeer);lifetime.Dispose();}
        static async Task ObserveDispose(IpcPeerSession peer){try{await peer.DisposeAsync();}catch{}}
    }
}
