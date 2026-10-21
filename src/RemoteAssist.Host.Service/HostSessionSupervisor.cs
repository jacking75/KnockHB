using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using RemoteAssist.Host.Core;
using RemoteAssist.Host.Core.Ipc;
using RemoteAssist.Host.Core.Security;
using RemoteAssist.Host.Core.Windows;
using RemoteAssist.Host.Core.Input;
using RemoteAssist.Host.Core.Files;
using RemoteAssist.Protocol;
using RemoteAssist.Host.Core.Runbooks;
using RemoteAssist.Host.Core.Deployment;

namespace RemoteAssist.Host.Service;

/// <summary>Session 0 supervisor. Capture/encoding/WebRTC and input stay in the target user's helpers.</summary>
internal sealed class HostSessionSupervisor(ILogger log,IHostApplicationLifetime lifetime)
{
    readonly object gate=new();
    readonly object reportGate=new();
    readonly System.Collections.Concurrent.ConcurrentDictionary<string,Task> stopAudits=new();
    HelperScope? helper;
    IpcPeerSession? tray;
    ScreenShareOptions options=new();
    uint sessionId;
    string userSid="";
    string? consentId,sessionBinding,supporterBinding;
    string phase="configuration";
    AuditJournal? audit;
    FileStream? sessionLease;
    DateTime noticeTime;
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
            RemoteAssist.Host.Core.Diagnostics.CaseTrace.Write("service-settings",metadata:new {policy=options.ForceRelay?"relay":"all"});
            InstallationState.CheckCandidate();InstallationState.MarkReady();
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
            await using var uiPeer=new IpcPeerSession(channel,HandleTray,stoppingToken,message=>StopHelper("대상자가 즉시 종료했다.",stopMark:LocalStopMark.Read(message)),reason=>StopHelper(reason));
            tray=uiPeer;uiPeer.Start();uiPeer.Post("state",new {status="지원 코드를 만들면 대상자의 승인 후 연결한다.",ended=true});
            phase="supervision";
            Report("tray-ready");
            using var timer=new PeriodicTimer(TimeSpan.FromMilliseconds(200));
            while(await timer.WaitForNextTickAsync(stoppingToken))
            {
                if(ui.HasExited||uiPeer.Token.IsCancellationRequested||uiPeer.Completion.IsCompleted){StopHelper("대상자 UI 연결이 종료됐다.");break;}
                var notice=Path.Combine(InstallationState.Root,"update.notice.json");
                if(File.Exists(notice)&&File.GetLastWriteTimeUtc(notice)!=noticeTime){InstallationState.DemandProtected(notice);var bytes=await File.ReadAllBytesAsync(notice,stoppingToken);if(bytes.Length>1024)throw new InvalidDataException("업데이트 통지 상한이다.");using var doc=JsonDocument.Parse(bytes);uiPeer.Post("update-status",doc.RootElement.Clone());noticeTime=File.GetLastWriteTimeUtc(notice);}
                if(!SessionProcess.IsInteractiveConsole(sessionId)||SessionProcess.ConsoleUserSid(sessionId)!=userSid)
                {StopHelper("대상 Windows 사용자가 변경됐다.");break;}
                HelperScope? current;lock(gate)current=helper;
                if(current is not null&&(current.Child?.HasExited==true||current.Peer?.Token.IsCancellationRequested==true||current.Peer?.Completion.IsCompleted==true))StopHelper("사용자 헬퍼가 종료됐다.",current);
                if(current?.Recovery?.HasExited==true)StopHelper("입력 복원 감독이 종료됐다.",current);
                if(current?.Admin?.HasExited==true||current?.AdminApproval is {} adminApproval&&!current.AdminAuthorization!.Allows(adminApproval.AdminGeneration))StopHelper("관리자 승인 또는 헬퍼가 종료됐다.",current);
                if(current?.AdminAuthorization?.PendingExpired==true)StopHelper("관리자 승인 요청이 만료돼 지원 세션을 종료했다.",current);
                if(current?.BookApproval.Expired==true)StopHelper("Runbook 승인 또는 창 닫기 확인이 만료돼 지원 세션을 종료했다.",current);
                bool mediaExpired;lock(gate)mediaExpired=helper==current&&current?.MediaWatch==true&&(current.LastMediaFrame==0?Environment.TickCount64-current.MediaStarted>=15000:Environment.TickCount64-current.LastMediaFrame>=3000);
                if(mediaExpired)StopHelper("실제 영상 송신이 정지해 헬퍼와 지원 세션을 종료했다.",current);
            }
        }
        catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){}
        catch(Exception ex){log.LogError("Host 감독 실패: {Kind}",ex.GetType().Name);Report("supervisor-error",ex);}
        finally{StopHelper("Host 서비스 또는 Tray 종료");tray=null;try{await Task.WhenAll(stopAudits.Values).WaitAsync(TimeSpan.FromSeconds(5));}catch{log.LogWarning("종료 감사 기록의 5초 기한이 초과됐다. 기존 spool을 보존한다.");}lifetime.StopApplication();}
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
                    if(caps!=0){_=FeatureContract.Permissions(caps);if((caps&~255)!=0)throw new InvalidDataException("현재 구현하지 않은 권한이다.");}
                    approvedScope=helper;currentSession=sessionBinding!;currentConnection=supporterBinding!;consentId=null;
                    approvedScope.Capabilities=(Capability)caps;
                    if((caps&4)!=0)approvedScope.AdminAuthorization=new(new(currentSession,currentConnection,1,(Capability)caps,DateTimeOffset.UtcNow.AddHours(2)));
                }
                audit!.Append(caps==0?AuditKind.ConsentDenied:AuditKind.ConsentGranted,caps==0?AuditResult.Denied:AuditResult.Allowed,currentSession,currentConnection,caps==0?0:1);
                if((caps&2)!=0)
                {
                    var recovery=new RecoveryScope();approvedScope.Adopt(recovery);
                    await recovery.Start(sessionId,userSid,approvedScope.Child!.Pid,currentSession,approvedScope.Token,reason=>
                    {StopHelper(reason,approvedScope);});
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
                    reason=>{StopHelper(reason,adminOwner);});
                adminOwner.Token.ThrowIfCancellationRequested();
                adminOwner.Peer!.Post("admin-state",new {state="active",approval=approved,mapping=privileged.Mapping},message.Id);
                tray?.Post("state",new {status="관리자 지원 중 · 보안 화면은 Ctrl+Alt+Shift+F9 또는 현장 종료 버튼으로 종료한다.",active=true,capabilities=(int)adminOwner.Capabilities,monitor});
                break;
            case "chat":
                lock(gate)
                {
                    var owner=helper??throw new UnauthorizedAccessException("진행 중인 지원이 없다.");
                    if((owner.Capabilities&Capability.Chat)==0||!Guid.TryParseExact(message.Id,"N",out _))throw new UnauthorizedAccessException("로컬 채팅 승인이 없다.");
                    var text=message.Body?.GetProperty("text").GetString()??throw new InvalidDataException("채팅 본문 누락이다.");
                    if(text.Length is <1 or >4096||!FeatureMessage.ValidUnicode(text)||text.Contains('\0')||!owner.LocalChatQuota.Allow())throw new InvalidDataException("로컬 채팅 본문·빈도 오류다.");
                    owner.Peer!.Post("chat",new {text},message.Id);
                }
                break;
            case "runbook-response":
                HelperScope bookOwner;RunbookMessage? bookRequest;bool bookApproved;
                lock(gate)
                {
                    bookOwner=helper??throw new UnauthorizedAccessException("지원 세션이 없다.");
                    bookApproved=message.Body?.GetProperty("approved").GetBoolean()==true;
                    bookRequest=bookOwner.BookApproval.Respond(message.Id,bookApproved);
                    if(bookRequest==null)break; // Cancellation won; this local approval cannot start a worker.
                }
                if(bookApproved)_=ExecuteRunbook(bookOwner,bookRequest);
                else CompleteRunbook(bookOwner,bookRequest,new("denied",null,"","대상자가 이번 작업을 거부했다.",RunbookCatalog.Demand(bookRequest).Impact));break;
            case "runbook-dismissed":
                HelperScope dismissedOwner;RunbookMessage dismissedRequest;
                lock(gate)
                {
                    dismissedOwner=helper??throw new UnauthorizedAccessException("지원 세션이 없다.");
                    if(message.Body!=null)throw new UnauthorizedAccessException("Runbook 창 닫기 응답 본문을 허용하지 않는다.");
                    dismissedRequest=dismissedOwner.BookApproval.Dismissed(message.Id);
                    RemoteAssist.Host.Core.Diagnostics.CaseTrace.Write("dismiss-ack",dismissedRequest.SessionId,1,new {id=message.Id});
                }
                CompleteRunbook(dismissedOwner,dismissedRequest,new("cancelled",null,"","요청을 취소하고 승인 창을 닫았다.",RunbookCatalog.Demand(dismissedRequest).Impact));break;
            case "monitor-response":
                lock(gate){var owner=helper??throw new UnauthorizedAccessException("지원 세션이 없다.");if(!owner.Capabilities.HasFlag(Capability.Screen)||!owner.MonitorPending.Remove(message.Id))break;owner.Peer!.Post("monitor-response",message.Body,message.Id);}break;
            case "file-send":case "file-cancel":case "file-response":
                lock(gate)
                {
                    var owner=helper??throw new UnauthorizedAccessException("진행 중인 지원이 없다.");
                    if(!owner.Capabilities.HasFlag(Capability.File)||!Guid.TryParseExact(message.Id,"N",out _))throw new UnauthorizedAccessException("파일 전송 승인이 없다.");
                    if(message.Type=="file-response"&&!owner.FilePending.Remove(message.Id))break;
                    owner.Peer!.Post(message.Type,message.Body,message.Id);
                }break;
            case "clipboard-response":
                lock(gate)
                {
                    var owner=helper??throw new UnauthorizedAccessException("진행 중인 지원이 없다.");var response=BoundFeature(owner,message,Capability.Clipboard);
                    if(!owner.ClipboardPending.Remove(message.Id,out var request)||DateTimeOffset.UtcNow>request.Expires||response.T is not ("clipboard-text" or "clipboard-applied" or "clipboard-rejected")||
                        response.T!="clipboard-rejected"&&(request.Message.T=="clipboard-get"?response.T!="clipboard-text":response.T!="clipboard-applied"||response.Hash!=request.Message.Hash))throw new InvalidDataException("클립보드 응답 요청·방향 오류다.");
                    owner.Peer!.Post("clipboard-response",response,message.Id);
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
            if(stopAudits.Count>=32){scope.StartupFinished.TrySetResult();scope.Dispose();tray?.Post("state",new {status="필수 종료 감사 기록이 지연돼 새 지원을 시작하지 않는다."});return;}
            sessionLease=InstallationState.TrySessionLease();
            if(sessionLease==null){scope.StartupFinished.TrySetResult();scope.Dispose();tray?.Post("state",new {status="업데이트 준비 중이다. 완료 뒤 지원 코드를 만들 수 있다."});return;}
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
            {StopHelper(reason,scope);});scope.Adopt(peer);
            scope.Token.ThrowIfCancellationRequested();
            peer.Start();peer.Post("config",options);peer.Post("start");
        }
        catch(Exception ex){Report("helper-error",ex);StopHelper("사용자 헬퍼 시작 실패",scope);throw;}
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
        Action? durable=null;
        lock(gate)
        {
            if(helper!=scope)return;
            switch(message.Type)
            {
                case "credentials":
                    var credentials=message.Body??throw new InvalidDataException("참가 표시 누락");
                    sessionBinding=credentials.GetProperty("sessionId").GetString();
                    var createdSession=sessionBinding??"";
                    durable=()=>{audit!.Append(AuditKind.SessionCreated,AuditResult.Completed,createdSession,"",0);lock(gate)if(helper==scope)tray?.Post("credentials",credentials);};break;
                case "consent-request":
                    var body=message.Body??throw new InvalidDataException("동의 요청 누락");
                    if(consentId is not null||body.GetProperty("sessionId").GetString()!=sessionBinding||!Guid.TryParseExact(message.Id,"N",out _))throw new InvalidDataException("동의 요청 상태 오류");
                    supporterBinding=body.GetProperty("connectionId").GetString();scope.SupporterName=body.GetProperty("displayName").GetString()??"지원자";
                    scope.Monitor=body.GetProperty("monitorDescriptor").Deserialize<MonitorDescriptor>();consentId=message.Id;tray?.Post("consent-request",body,message.Id);break;
                case "state":
                    if(message.Body?.TryGetProperty("generation",out var connectionGen)==true){var value=connectionGen.GetInt32();if(value<scope.ConnectionGeneration||value>scope.ConnectionGeneration+1)throw new InvalidDataException("헬퍼 연결 세대 오류다.");scope.ConnectionGeneration=value;}
                    if(message.Body?.TryGetProperty("active",out var active)==true){var watching=active.GetBoolean()&&scope.Capabilities.HasFlag(Capability.Screen);if(watching&&!scope.MediaWatch){scope.MediaStarted=Environment.TickCount64;scope.LastMediaFrame=0;}scope.MediaWatch=watching;if(active.GetBoolean())scope.Monitor=message.Body.Value.GetProperty("monitor").Deserialize<MonitorDescriptor>();}
                    tray?.Post("state",message.Body);
                    if(message.Body?.TryGetProperty("ended",out var ended)==true&&ended.GetBoolean()){var endReason=message.Body.Value.GetProperty("status").GetString()??"지원 세션 종료";durable=()=>StopHelper(endReason,scope);}
                    break;
                case "media-lease":
                    var lease=message.Body??throw new InvalidDataException("영상 lease 누락이다.");
                    if(!scope.Capabilities.HasFlag(Capability.Screen)||lease.GetProperty("sessionId").GetString()!=sessionBinding)throw new UnauthorizedAccessException("영상 lease 권한·세션 오류다.");
                    var tick=lease.GetProperty("tick").GetInt64();var age=Environment.TickCount64-tick;
                    if(scope.MediaWatch&&lease.GetProperty("generation").GetInt32()==scope.ConnectionGeneration&&age>=0&&age<=1500&&tick>scope.LastMediaFrame)scope.LastMediaFrame=tick;
                    break;
                case "recovery-lease":
                    (scope.Recovery??throw new UnauthorizedAccessException("복원 승인이 없다.")).Lease();scope.Admin?.Lease();break;
                case "chat":
                    var chat=BoundFeature(scope,message,Capability.Chat);if(chat.T!="chat")throw new InvalidDataException("채팅 방향 오류다.");tray?.Post("chat",chat,message.Id);break;
                case "clipboard-request":
                    var clipboard=BoundFeature(scope,message,Capability.Clipboard);
                    if(clipboard.T is not ("clipboard-get" or "clipboard-set")||scope.ClipboardPending.Count>=16||!scope.ClipboardPending.TryAdd(message.Id,(clipboard,DateTimeOffset.UtcNow.AddSeconds(2))))throw new InvalidDataException("클립보드 요청·대기 상한 오류다.");
                    tray?.Post("clipboard-request",clipboard,message.Id);break;
                case "runbook-request":
                    var runbook=message.Body?.Deserialize<RunbookMessage>()??throw new InvalidDataException("Runbook 요청 누락이다.");var definition=RunbookCatalog.Demand(runbook);
                    if(!scope.Capabilities.HasFlag(Capability.Runbook)||definition.RequiresAdmin&&!scope.Capabilities.HasFlag(Capability.Admin)||runbook.SessionId!=sessionBinding||runbook.PermissionGeneration!=1||runbook.Generation!=scope.ConnectionGeneration||runbook.Id!=message.Id||scope.BookApproval.IsBusy||scope.BookCancel!=null||!scope.SeenBooks.Add(runbook.Id))throw new UnauthorizedAccessException("현재 등록 Runbook 승인이 아니다.");
                    scope.BookHistory.Enqueue(runbook.Id);if(scope.BookHistory.Count>256)scope.SeenBooks.Remove(scope.BookHistory.Dequeue());
                    scope.BookApproval.Begin(runbook);tray?.Post("runbook-request",runbook,message.Id);break;
                case "runbook-cancel":
                    if(scope.BookApproval.Cancel(message.Id)){
                        RemoteAssist.Host.Core.Diagnostics.CaseTrace.Write("dismiss-sent",sessionBinding,1,new {id=message.Id});
                        tray!.Post("runbook-dismiss",id:message.Id);
                    }
                    if(scope.ExecutingBook?.Id==message.Id){var cancelBook=scope.BookCancel;var cancelWorker=scope.BookWorker;durable=()=>{try{cancelBook?.Cancel();}catch(ObjectDisposedException){}cancelWorker?.Dispose();};}break;
                case "monitor-request":
                    if(!scope.Capabilities.HasFlag(Capability.Screen)||scope.AdminApproval!=null||message.Body?.GetProperty("sessionId").GetString()!=sessionBinding||!scope.MonitorPending.Add(message.Id)||scope.MonitorPending.Count>1)throw new UnauthorizedAccessException("현재 모니터 승인 요청이 아니다.");
                    tray?.Post("monitor-request",message.Body,message.Id);break;
                case "file-select":
                    if(!scope.Capabilities.HasFlag(Capability.File)||message.Body?.GetProperty("sessionId").GetString()!=sessionBinding||!scope.FilePending.Add(message.Id)||scope.FilePending.Count>1)throw new UnauthorizedAccessException("로컬 파일 선택 승인이 없다.");
                    tray?.Post("file-select",message.Body,message.Id);break;
                case "file-request":
                    var offer=message.Body?.Deserialize<FileMessage>()??throw new InvalidDataException("파일 제안 누락이다.");_=FileMessage.Parse(ProtocolJson.Encode(offer));
                    if(!scope.Capabilities.HasFlag(Capability.File)||offer.SessionId!=sessionBinding||offer.PermissionGeneration!=1||offer.Generation!=scope.ConnectionGeneration||offer.Id!=message.Id||offer.T!="file-offer"||!scope.FilePending.Add(message.Id)||scope.FilePending.Count>1)throw new UnauthorizedAccessException("현재 파일 수락 요청이 아니다.");
                    tray?.Post("file-request",offer,message.Id);break;
                case "file-state":
                    if(!scope.Capabilities.HasFlag(Capability.File))throw new UnauthorizedAccessException("파일 승인이 없다.");scope.FilePending.Remove(message.Id);tray?.Post("file-state",message.Body,message.Id);break;
                case "file-audit":
                    var notice=message.Body?.Deserialize<FileAuditNotice>()??throw new InvalidDataException("파일 감사 누락이다.");notice.Validate();
                    if(!scope.Capabilities.HasFlag(Capability.File)||notice.SessionId!=sessionBinding||notice.PermissionGeneration!=1)throw new UnauthorizedAccessException("파일 감사 바인딩 오류다.");
                    var fileConnection=supporterBinding!;
                    durable=()=>{scope.FileAudits.Record(notice,n=>PersistFileAudit(n,fileConnection));lock(gate)if(helper==scope)scope.Peer!.Post("file-audit-ack",id:message.Id);};break;
                case "admin-request":
                    if(scope.Monitor is null||scope.AdminAuthorization is null||message.Body!=null)throw new UnauthorizedAccessException("관리자 요청 승인 범위가 없다.");
                    scope.AdminAuthorization.Request(message.Id);
                    tray?.Post("admin-request",new {sessionId=sessionBinding,connectionId=supporterBinding,permissionGeneration=1,displayName=scope.SupporterName,monitor=scope.Monitor.Identity},message.Id);
                    break;
                case "admin-sas":
                    if(message.Body!=null||scope.AdminApproval is null||scope.AdminAuthorization?.Allows(scope.AdminApproval.AdminGeneration)!=true)throw new UnauthorizedAccessException("SAS 관리자 승인이 없다.");
                    var sasSession=sessionBinding??"";var sasConnection=supporterBinding??"";
                    durable=()=>{audit!.Append(AuditKind.SasRequested,AuditResult.Allowed,sasSession,sasConnection,1);lock(gate)if(helper==scope&&scope.AdminApproval is {} approval&&scope.AdminAuthorization?.Allows(approval.AdminGeneration)==true)SasBroker.Send();};break;
                default:throw new InvalidDataException("일반 헬퍼의 허용되지 않은 통지다.");
            }
        }
        durable?.Invoke();
    }
    FeatureMessage BoundFeature(HelperScope scope,LocalIpcMessage message,Capability capability)
    {
        var value=message.Body?.Deserialize<FeatureMessage>()??throw new InvalidDataException("보조 기능 본문 누락이다.");
        _=FeatureMessage.Parse(ProtocolJson.Encode(value));
        if(helper!=scope||(scope.Capabilities&capability)!=capability||value.Id!=message.Id||value.SessionId!=sessionBinding||value.PermissionGeneration!=1||value.Generation!=scope.ConnectionGeneration)throw new UnauthorizedAccessException("현재 보조 기능 승인과 일치하지 않는다.");return value;
    }
    async Task ExecuteRunbook(HelperScope owner,RunbookMessage request)
    {
        var book=RunbookCatalog.Demand(request);RunbookScope worker=new();CancellationTokenSource cancel;PermissionSnapshot? processGrant;
        lock(gate)
        {
            if(helper!=owner||owner.Token.IsCancellationRequested){worker.Dispose();return;}
            owner.Adopt(worker);cancel=CancellationTokenSource.CreateLinkedTokenSource(owner.Token);owner.BookCancel=cancel;owner.ExecutingBook=request;
            if(owner.BookApproval.ExecutionCancelled(request.Id))cancel.Cancel();
            processGrant=book.RequiresAdmin?new(request.SessionId,supporterBinding!,1,owner.Capabilities,DateTimeOffset.UtcNow.AddSeconds(30)):null;
        }
        RunbookResult result;
        try
        {
            cancel.Token.ThrowIfCancellationRequested();
            audit!.Append(AuditKind.RunbookStarted,AuditResult.Allowed,request.SessionId,supporterBinding??"",1,new(request.Id,book.BookId,book.Hash));
            result=await worker.Run(request,sessionId,userSid,processGrant,cancel.Token);
        }
        catch(OperationCanceledException){result=new("cancelled",null,"","작업을 중단했다.",book.Impact);}
        catch{result=new("failed",null,"","worker 시작 또는 실행 실패다.",book.Impact);}
        finally{worker.Dispose();lock(gate){owner.BookCancel=null;owner.ExecutingBook=null;}cancel.Dispose();}
        try{CompleteRunbook(owner,request,result);}catch(Exception ex){Report("runbook-completion-error",ex);StopHelper("Runbook 필수 감사 또는 응답 기록 실패다.",owner);}
    }
    void CompleteRunbook(HelperScope owner,RunbookMessage request,RunbookResult result)
    {
        string connection;lock(gate){if(helper!=owner)return;connection=supporterBinding??"";}
        var book=RunbookCatalog.Demand(request);
        audit!.Append(AuditKind.RunbookFinished,result.State switch {"completed"=>AuditResult.Completed,"denied"=>AuditResult.Denied,"cancelled"=>AuditResult.Stopped,_=>AuditResult.Failed},request.SessionId,connection,1,new(request.Id,book.BookId,book.Hash));
        lock(gate)if(helper==owner){owner.BookApproval.CompleteExecution(request.Id);owner.Peer!.Post("runbook-result",result,request.Id);}
    }
    void PersistFileAudit(FileAuditNotice notice,string connection)=>audit!.Append(notice.Phase=="started"?AuditKind.FileStarted:AuditKind.FileFinished,
        notice.Outcome switch {"allowed"=>AuditResult.Allowed,"denied"=>AuditResult.Denied,"completed"=>AuditResult.Completed,"stopped"=>AuditResult.Stopped,_=>AuditResult.Failed},
        notice.SessionId,connection,notice.PermissionGeneration,new(notice.Id,Direction:notice.Direction,Size:notice.Size));
    void StopHelper(string reason,HelperScope? expected=null,LocalStopMark? stopMark=null)
    {
        var receivedTick=System.Diagnostics.Stopwatch.GetTimestamp();
        HelperScope? scope;
        string endedSession,endedConnection;
        FileStream? releaseLease;
        lock(gate){if(expected!=null&&helper!=expected)return;scope=helper;endedSession=sessionBinding??"";endedConnection=supporterBinding??"";helper=null;consentId=sessionBinding=supporterBinding=null;releaseLease=sessionLease;sessionLease=null;}
        if(scope is null){releaseLease?.Dispose();return;}
        // Start the observer before cancellation callbacks; retain a kernel handle, never re-open a PID.
        // Scope this measure to the selected screen-only case. Input/admin/worker recovery needs its own evidence.
        SessionProcess.ExitProbe? exitProbe=null;bool completeInventory=false;string? probeFailure=null;
        try
        {
            completeInventory=scope.Capabilities==Capability.Screen&&scope.StartupFinished.Task.IsCompletedSuccessfully&&scope.Admin is null&&scope.BookWorker is null;
            exitProbe=scope.Child?.CaptureExitProbe();completeInventory&=exitProbe is not null;
        }
        catch(Exception ex){probeFailure=ex.GetType().Name;}
        if(stopMark is not null||exitProbe is not null)
        {
            var probe=exitProbe;var inventory=completeInventory;
            var sampling=Task.Run(()=>
            {
                using(probe)
                {
                    var requested=stopMark?.RequestedTick??receivedTick;
                    var end=requested+System.Diagnostics.Stopwatch.Frequency;
                    bool exited=false;string? failure=probeFailure;
                    try{exited=probe?.WaitUntil(end)==true;}catch(Exception ex){failure=ex.GetType().Name;}
                    var observed=System.Diagnostics.Stopwatch.GetTimestamp();
                    Report("local-stop-observation",observation:new {requestId=stopMark?.RequestId,requestedTick=stopMark?.RequestedTick,receivedTick,observedTick=observed,
                        frequency=System.Diagnostics.Stopwatch.Frequency,childPid=probe?.Pid,processExited=exited,completeInventory=inventory,
                        scope="screen-only-producer-exit",result=stopMark is null||!inventory||failure is not null?"UNVERIFIED":exited&&observed<=end?"PASS":"FAIL",failure});
                }
            });
            TrackStopTask(sampling);
        }
        // Owned job termination bypasses a hung native call or a blocked consent/IPC actor.
        scope.Dispose();releaseLease?.Dispose();
        try{tray?.Post("state",new {status=reason,ended=true});}catch(InvalidOperationException){}
        var recording=Task.Run(()=>{Report("helper-stopped");try{scope.FileAudits.End(n=>PersistFileAudit(n,endedConnection));}catch(Exception ex){Report("audit-file-stop-error",ex);}if(scope.AdminApproval!=null)try{audit?.Append(AuditKind.AdminRevoked,AuditResult.Stopped,endedSession,endedConnection,1);}catch(Exception ex){Report("audit-admin-stop-error",ex);}try{audit?.Append(AuditKind.SessionEnded,AuditResult.Stopped,endedSession,endedConnection,0);}catch(Exception ex){Report("audit-stop-error",ex);}});
        TrackStopTask(recording);
    }
    void TrackStopTask(Task task)
    {
        var id=Guid.NewGuid().ToString("N");stopAudits.TryAdd(id,task);
        _=task.ContinueWith(completed=>{_=completed.Exception;stopAudits.TryRemove(id,out _);},CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
    }
    void Report(string kind,Exception? error=null,int? childPid=null,object? observation=null)
    {
        // Metadata only. Never serialize an exception message, IPC body, credentials or bootstrap.
        var path=Path.Combine(AppContext.BaseDirectory,"service-status.ndjson");
        try
        {
            lock(reportGate)
            {
                if(File.Exists(path)&&new FileInfo(path).Length>4*1024*1024)throw new IOException("감독 기록 상한 초과");
                File.AppendAllText(path,JsonSerializer.Serialize(new {utc=DateTimeOffset.UtcNow,monotonicTick=System.Diagnostics.Stopwatch.GetTimestamp(),kind,phase,sessionId,childPid,observation,error=error?.GetType().Name,
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
        public Capability Capabilities {get;set;}
        public int ConnectionGeneration {get;set;}=1;
        public bool MediaWatch {get;set;}
        public long MediaStarted {get;set;}
        public long LastMediaFrame {get;set;}
        public RunbookApproval BookApproval {get;}=new();
        public FileAuditLedger FileAudits {get;}=new();
        public Queue<string> BookHistory {get;}=new();
        public RunbookMessage? ExecutingBook {get;set;}
        public HashSet<string> SeenBooks {get;}=new();
        public CancellationTokenSource? BookCancel {get;set;}
        RunbookScope? bookWorker;
        public RunbookScope? BookWorker {get{lock(ownership)return bookWorker;}}
        public RemoteAssist.Host.Core.Media.FeatureQuota LocalChatQuota {get;}=new(5);
        public Dictionary<string,(FeatureMessage Message,DateTimeOffset Expires)> ClipboardPending {get;}=new(StringComparer.Ordinal);
        public HashSet<string> FilePending {get;}=new(StringComparer.Ordinal);
        public HashSet<string> MonitorPending {get;}=new(StringComparer.Ordinal);
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
        public void Adopt(RunbookScope value){lock(ownership){if(!disposed){bookWorker?.Dispose();bookWorker=value;return;}}value.Dispose();throw new OperationCanceledException(Token);}
        public void Dispose()
        {
            SessionProcess? ownedChild;NamedPipeServerStream? ownedServer;IpcPeerSession? ownedPeer;
            lock(ownership){if(disposed)return;disposed=true;ownedChild=child;ownedServer=server;ownedPeer=peer;}
            // Cancellation callbacks can wait on native startup/worker locks. Fence producers first.
            ownedChild?.Dispose();admin?.Fence();bookWorker?.Fence();
            try{lifetime.Cancel();}catch(AggregateException){}
            AdminAuthorization?.End();admin?.Dispose();
            bookWorker?.Dispose();
            ownedServer?.Dispose();
            recovery?.Dispose();
            _=CompleteCleanup(ownedPeer);
        }
        async Task CompleteCleanup(IpcPeerSession? ownedPeer)
        {await StartupFinished.Task;if(ownedPeer!=null)await ObserveDispose(ownedPeer);lifetime.Dispose();}
        static async Task ObserveDispose(IpcPeerSession peer){try{await peer.DisposeAsync();}catch{}}
    }
}
