using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using RemoteAssist.Host.Core;
using RemoteAssist.Host.Core.Deployment;
using RemoteAssist.Host.Core.Files;
using RemoteAssist.Host.Core.Ipc;
using RemoteAssist.Host.Core.Runbooks;
using RemoteAssist.Host.Core.Security;
using RemoteAssist.Host.Core.Windows;
using RemoteAssist.Protocol;

namespace RemoteAssist.Host.Service;

/// <summary>
/// Session 0 감독 서비스. 대상 사용자가 관리자 권한으로 한 번 실행해 남긴 허가가 있으면
/// 고정 지원 ID로 지원 대기를 계속 연다. 지원자가 나가면 1초 뒤 새 대기를 연다.
/// 캡처·인코딩·WebRTC·입력은 대상 사용자 세션의 헬퍼가 맡는다.
/// </summary>
internal sealed class HostSessionSupervisor(ILogger log, IHostApplicationLifetime lifetime)
{
    const string NoGrantStatus = "관리자 권한으로 RemoteAssist를 한 번 실행하면 지원을 시작한다.";
    readonly object gate = new();
    readonly object reportGate = new();
    HelperScope? helper;
    IpcPeerSession? tray;
    ScreenShareOptions options = new();
    uint sessionId;
    string userSid = "";
    string phase = "configuration";
    InitialSupportGrant? grant;
    long nextStartTick;
    AuditJournal? audit;
    DateTime noticeTime;

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        try
        {
            try { audit = AuditJournal.OpenProtected(); }
            catch (Exception ex) { Report("audit-unavailable", ex); }
            var config = Path.Combine(AppContext.BaseDirectory, "host.settings.json");
            if (File.Exists(config)) options = HostSettings.Parse(await File.ReadAllBytesAsync(config, stoppingToken));
            options = options with { DeviceCode = DeviceId.LoadOrCreate() };
            options.Validate(256);
            InstallationState.CheckCandidate();
            phase = "startup-preparation";
            await StartupPreparation.VerifyAsync(stoppingToken);
            await WaitForConsoleUser(stoppingToken);
            if (stoppingToken.IsCancellationRequested) return;
            SessionProcess.VerifyStartup(sessionId);
            grant = InitialSupportLaunch.Load(sessionId, userSid);
            StartupPreparation.WriteStatus("ready", "os-prerequisites", null);
            InstallationState.MarkReady();

            phase = "tray-launch";
            var name = Guid.NewGuid().ToString("N"); var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            using var server = LocalIpcEndpoint.CreateServer(name, userSid);
            using var ui = SessionProcess.Start(HostChildRole.Tray, sessionId, name, nonce);
            Report("tray-launched", childPid: ui.Pid);
            var channel = await LocalIpcEndpoint.AcceptAsync(server, ui, nonce, stoppingToken);
            await using var uiPeer = new IpcPeerSession(channel, HandleTray, stoppingToken, _ => StopSupport("대상자가 즉시 종료했다."), reason => StopHelper(reason));
            tray = uiPeer; uiPeer.Start();
            uiPeer.Post("device", new { code = FormatCode(options.DeviceCode!), granted = grant is not null });
            uiPeer.Post("state", new { status = grant is null ? NoGrantStatus : "지원자 접속 대기", ended = true });

            phase = "supervision";
            Report("tray-ready");
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                // Tray를 닫는 것은 프로그램 종료다. 허가도 함께 없앤다.
                if (ui.HasExited || uiPeer.Token.IsCancellationRequested || uiPeer.Completion.IsCompleted) { StopSupport("대상자 UI가 종료됐다."); break; }
                await ForwardUpdateNotice(uiPeer, stoppingToken);
                if (!SessionProcess.IsInteractiveConsole(sessionId) || SessionProcess.ConsoleUserSid(sessionId) != userSid) { StopSupport("대상 Windows 사용자가 변경됐다."); break; }
                if (grant is { } waiting && helper is null && Environment.TickCount64 >= nextStartTick)
                {
                    // 시그널링 서버가 잠시 없어도 지원은 끝나지 않는다. 잠시 뒤 다시 연다.
                    try { await StartHelper(waiting, stoppingToken); }
                    catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { Report("helper-start-retry", ex); nextStartTick = Environment.TickCount64 + 5000; }
                }
                HelperScope? current; lock (gate) current = helper;
                if (current is not null && (current.Child?.HasExited == true || current.Peer?.Token.IsCancellationRequested == true || current.Peer?.Completion.IsCompleted == true)) StopHelper("사용자 헬퍼가 종료됐다.", current);
                if (current?.Admin?.HasExited == true) StopHelper("관리자 헬퍼가 종료됐다.", current);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (phase == "startup-preparation") StartupPreparation.WriteStatus("failed", phase, ex.Message);
            log.LogError("Host 감독 실패: {Kind}", ex.GetType().Name);
            Report("supervisor-error", ex);
        }
        finally
        {
            StopHelper("Host 서비스 종료");
            tray = null;
            lifetime.StopApplication();
        }
    }

    async Task WaitForConsoleUser(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            sessionId = SessionProcess.ActiveConsoleSession;
            if (!SessionProcess.IsInteractiveConsole(sessionId)) { await Task.Delay(250, ct); continue; }
            try { userSid = SessionProcess.ConsoleUserSid(sessionId); return; }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1008) { await Task.Delay(250, ct); }
        }
    }

    async Task ForwardUpdateNotice(IpcPeerSession peer, CancellationToken ct)
    {
        var notice = Path.Combine(InstallationState.Root, "update.notice.json");
        if (!File.Exists(notice) || File.GetLastWriteTimeUtc(notice) == noticeTime) return;
        InstallationState.DemandProtected(notice);
        var bytes = await File.ReadAllBytesAsync(notice, ct);
        if (bytes.Length > 1024) throw new InvalidDataException("업데이트 통지 상한이다.");
        using var doc = JsonDocument.Parse(bytes);
        peer.Post("update-status", doc.RootElement.Clone());
        noticeTime = File.GetLastWriteTimeUtc(notice);
    }

    static string FormatCode(string code) => code.Length == 9 ? $"{code[..3]}-{code[3..6]}-{code[6..]}" : code;

    // ---- Tray -------------------------------------------------------------

    Task HandleTray(LocalIpcMessage message, CancellationToken ct)
    {
        switch (message.Type)
        {
            case "stop": StopSupport("대상자가 즉시 종료했다."); break;
            case "chat": case "file-send": case "file-cancel": case "clipboard-response":
                lock (gate)
                {
                    var owner = helper;
                    if (owner?.Peer is null || owner.SessionBinding is null) break; // 지원자가 없으면 버린다.
                    owner.Peer.Post(message.Type, message.Body, message.Id);
                }
                break;
            default: throw new InvalidDataException("Tray에 허용되지 않은 IPC다.");
        }
        return Task.CompletedTask;
    }

    // ---- 헬퍼 ---------------------------------------------------------------

    async Task StartHelper(InitialSupportGrant expected, CancellationToken ct)
    {
        var scope = new HelperScope(ct);
        lock (gate)
        {
            if (!ReferenceEquals(grant, expected) || helper != null) { scope.StartupFinished.TrySetResult(); scope.Dispose(); return; }
            helper = scope;
        }
        try
        {
            var name = Guid.NewGuid().ToString("N"); var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var server = LocalIpcEndpoint.CreateServer(name, userSid); scope.Adopt(server);
            var child = SessionProcess.Start(HostChildRole.Helper, sessionId, name, nonce); scope.Adopt(child);
            Report("helper-launched", childPid: child.Pid);
            var channel = await LocalIpcEndpoint.AcceptAsync(server, child, nonce, scope.Token);
            var peer = new IpcPeerSession(channel, (message, token) => HandleHelper(scope, message, token), scope.Token, onStopped: reason => StopHelper(reason, scope));
            scope.Adopt(peer);
            peer.Start(); peer.Post("config", options); peer.Post("start");
        }
        catch (Exception ex) { Report("helper-error", ex); StopHelper("사용자 헬퍼 시작 실패", scope); throw; }
        finally { scope.StartupFinished.TrySetResult(); }
    }

    async Task HandleHelper(HelperScope scope, LocalIpcMessage message, CancellationToken ct)
    {
        lock (gate) if (helper != scope) return;
        switch (message.Type)
        {
            case "credentials":
                scope.SessionId = message.Body?.GetProperty("sessionId").GetString() ?? throw new InvalidDataException("세션 ID 누락");
                Audit(AuditKind.SessionCreated, AuditResult.Completed, scope.SessionId, "", 0);
                break;
            case "supporter":
                var supporter = message.Body ?? throw new InvalidDataException("지원자 정보 누락");
                if (supporter.GetProperty("sessionId").GetString() != scope.SessionId) throw new InvalidDataException("다른 세션의 지원자다.");
                lock (gate)
                {
                    scope.SessionBinding = scope.SessionId;
                    scope.ConnectionId = supporter.GetProperty("connectionId").GetString() ?? throw new InvalidDataException("지원자 연결 ID 누락");
                    scope.SupporterName = supporter.GetProperty("displayName").GetString() ?? "지원자";
                    scope.Monitor = supporter.GetProperty("monitor").Deserialize<MonitorDescriptor>();
                    scope.AdminAuthorization = new(new(scope.SessionId!, scope.ConnectionId, 1, Capability.All));
                    // 지원자가 연결된 동안은 업데이트 설치가 기다린다.
                    scope.SessionLease = InstallationState.TrySessionLease();
                }
                Audit(AuditKind.SupporterConnected, AuditResult.Allowed, scope.SessionId!, scope.ConnectionId, 1);
                tray?.Post("supporter", new { displayName = scope.SupporterName });
                break;
            case "state":
                var state = message.Body ?? throw new InvalidDataException("상태 누락");
                if (state.TryGetProperty("generation", out var generation)) scope.ConnectionGeneration = generation.GetInt32();
                if (state.TryGetProperty("monitor", out var monitor)) scope.Monitor = monitor.Deserialize<MonitorDescriptor>();
                tray?.Post("state", state);
                if (state.TryGetProperty("ended", out var ended) && ended.GetBoolean()) StopHelper(state.GetProperty("status").GetString() ?? "지원 세션 종료", scope);
                break;
            case "chat":
                tray?.Post("chat", BoundFeature(scope, message), message.Id);
                break;
            case "clipboard-request":
                tray?.Post("clipboard-request", BoundFeature(scope, message), message.Id);
                break;
            case "file-state":
                tray?.Post("file-state", message.Body, message.Id);
                break;
            case "file-audit":
                var notice = message.Body?.Deserialize<FileAuditNotice>() ?? throw new InvalidDataException("파일 감사 누락이다.");
                notice.Validate();
                scope.FileAudits.Record(notice, n => PersistFileAudit(n, scope.ConnectionId ?? ""));
                scope.Peer!.Post("file-audit-ack", id: message.Id);
                break;
            case "runbook-request":
                var request = message.Body?.Deserialize<RunbookMessage>() ?? throw new InvalidDataException("Runbook 요청 누락이다.");
                if (request.SessionId != scope.SessionBinding || request.Id != message.Id) throw new InvalidOperationException("현재 세션의 Runbook 요청이 아니다.");
                if (scope.BookCancel != null) { scope.Peer!.Post("runbook-result", new RunbookResult("failed", null, "", "다른 작업이 실행 중이다.", ""), request.Id); break; }
                scope.RunBackground(() => ExecuteRunbook(scope, request));
                break;
            case "runbook-cancel":
                try { scope.BookCancel?.Cancel(); } catch (ObjectDisposedException) { }
                break;
            case "admin-request":
                if (scope.AdminAuthorization is null || scope.Monitor is null) throw new InvalidOperationException("지원자 연결 전 관리자 요청이다.");
                var approval = scope.AdminAuthorization.Authorize(message.Id);
                scope.RunBackground(() => ActivateAdmin(scope, approval));
                break;
            case "admin-sas":
                if (scope.Admin is null) throw new InvalidOperationException("관리자 모드가 아니다.");
                Audit(AuditKind.SasRequested, AuditResult.Allowed, scope.SessionBinding ?? "", scope.ConnectionId ?? "", 1);
                try { SasBroker.Send(); }
                catch (Exception ex) { Report("sas-error", ex); } // 서비스 SAS 정책이 없으면 보내지 못한다. 세션은 유지한다.
                break;
            case "admin-monitor":
                scope.Admin?.Monitor(message.Body?.Deserialize<MonitorDescriptor>() ?? throw new InvalidDataException("모니터 누락이다."));
                break;
            case "admin-input":
                var batch = message.Body?.Deserialize<AdminInputBatch>() ?? throw new InvalidDataException("관리자 입력 본문 누락이다.");
                AdminInputValidation.Validate(batch);
                var admin = scope.Admin;
                if (admin is null || scope.AdminAuthorization?.Allows(batch.AdminGeneration) != true) throw new InvalidOperationException("현재 관리자 세대의 입력이 아니다.");
                var count = await admin.Input(batch, ct);
                scope.Peer!.Post("admin-input-ack", new AdminInputResult(count), message.Id);
                break;
            default: throw new InvalidDataException("일반 헬퍼의 허용되지 않은 통지다.");
        }
    }

    FeatureMessage BoundFeature(HelperScope scope, LocalIpcMessage message)
    {
        var value = message.Body?.Deserialize<FeatureMessage>() ?? throw new InvalidDataException("보조 기능 본문 누락이다.");
        _ = FeatureMessage.Parse(ProtocolJson.Encode(value));
        if (value.Id != message.Id || value.SessionId != scope.SessionBinding) throw new InvalidDataException("현재 세션의 보조 기능 메시지가 아니다.");
        return value;
    }

    /// <summary>관리자 헬퍼(SYSTEM, 사용자 세션)를 띄워 화면·입력 경로를 넘긴다. 실패하면 일반 모드로 계속한다.</summary>
    async Task ActivateAdmin(HelperScope owner, AdminApproval approval)
    {
        var privileged = new AdminScope();
        try
        {
            owner.Adopt(privileged);
            await privileged.Start(new(approval, sessionId, userSid, owner.Monitor!, "", options.TargetFps, options.MaxOutputLongEdge, options.MaxOutputShortEdge), owner.Token, reason => StopHelper(reason, owner));
            owner.Token.ThrowIfCancellationRequested();
            Audit(AuditKind.AdminGranted, AuditResult.Allowed, approval.SessionId, approval.ConnectionId, 1);
            owner.Peer!.Post("admin-state", new { state = "active", approval, mapping = privileged.Mapping }, approval.RequestId);
        }
        catch (Exception ex) when (!owner.Token.IsCancellationRequested)
        {
            Report("admin-start-error", ex);
            owner.ReleaseAdmin();
            owner.Peer?.Post("admin-state", new { state = "denied" }, approval.RequestId);
        }
    }

    async Task ExecuteRunbook(HelperScope owner, RunbookMessage request)
    {
        var book = RunbookCatalog.Demand(request);
        using var worker = new RunbookScope();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(owner.Token);
        owner.BookCancel = cancel;
        RunbookResult result;
        try
        {
            Audit(AuditKind.RunbookStarted, AuditResult.Allowed, request.SessionId, owner.ConnectionId ?? "", 1, new(request.Id, book.BookId, book.Hash));
            result = await worker.Run(request, sessionId, userSid, cancel.Token);
        }
        catch (OperationCanceledException) { result = new("cancelled", null, "", "작업을 중단했다.", book.Impact); }
        catch (Exception ex) { Report("runbook-error", ex); result = new("failed", null, "", "worker 시작 또는 실행 실패다.", book.Impact); }
        finally { owner.BookCancel = null; }
        Audit(AuditKind.RunbookFinished, result.State switch { "completed" => AuditResult.Completed, "cancelled" => AuditResult.Stopped, _ => AuditResult.Failed }, request.SessionId, owner.ConnectionId ?? "", 1, new(request.Id, book.BookId, book.Hash));
        lock (gate) if (helper == owner) owner.Peer!.Post("runbook-result", result, request.Id);
    }

    void PersistFileAudit(FileAuditNotice notice, string connection) => Audit(notice.Phase == "started" ? AuditKind.FileStarted : AuditKind.FileFinished,
        notice.Outcome switch { "allowed" => AuditResult.Allowed, "denied" => AuditResult.Denied, "completed" => AuditResult.Completed, "stopped" => AuditResult.Stopped, _ => AuditResult.Failed },
        notice.SessionId, connection, notice.PermissionGeneration, new(notice.Id, Direction: notice.Direction, Size: notice.Size));

    /// <summary>감사 기록은 지원을 막지 않는다. 기록하지 못하면 상태 로그에만 남긴다.</summary>
    void Audit(AuditKind kind, AuditResult result, string session, string connection, long generation, AuditOperation? operation = null)
    {
        if (audit is null) return;
        try { audit.Append(kind, result, session, connection, generation, operation); }
        catch (Exception ex) { Report("audit-error", ex); }
    }

    // ---- 종료 ---------------------------------------------------------------

    /// <summary>대상자의 즉시 종료·Tray 종료·사용자 변경. 허가를 없애므로 다시 관리자 실행해야 지원이 열린다.</summary>
    void StopSupport(string reason)
    {
        InitialSupportGrant? revoked;
        lock (gate) { revoked = grant; grant = null; }
        if (revoked is not null)
            try { InitialSupportLaunch.Revoke(revoked.Id); } catch (Exception ex) { Report("grant-revoke-error", ex); }
        StopHelper(reason);
        try { tray?.Post("state", new { status = NoGrantStatus, ended = true }); } catch (InvalidOperationException) { }
        try { tray?.Post("device", new { code = FormatCode(options.DeviceCode!), granted = false }); } catch (InvalidOperationException) { }
    }

    /// <summary>지금 지원 세션을 끝낸다. 허가가 남아 있으면 1초 뒤(연속 실패면 5초 뒤) 같은 지원 ID로 다시 연다.</summary>
    void StopHelper(string reason, HelperScope? expected = null)
    {
        HelperScope? scope;
        lock (gate)
        {
            if (expected != null && helper != expected) return;
            scope = helper; helper = null;
            nextStartTick = Environment.TickCount64 + (scope?.Connected == true ? 1000 : 5000);
        }
        try { tray?.Post("state", new { status = reason, ended = true }); } catch (InvalidOperationException) { }
        if (scope is null) return;
        var session = scope.SessionBinding; var connection = scope.ConnectionId ?? "";
        var hadAdmin = scope.Admin is not null;
        scope.Dispose();
        Report("helper-stopped");
        if (session is null) return;
        try { scope.FileAudits.End(n => PersistFileAudit(n, connection)); } catch (Exception ex) { Report("audit-file-stop-error", ex); }
        if (hadAdmin) Audit(AuditKind.AdminRevoked, AuditResult.Stopped, session, connection, 1);
        if (audit is not null) try { audit.AppendSessionEndedOnce(session, connection); } catch (Exception ex) { Report("audit-stop-error", ex); }
    }

    void Report(string kind, Exception? error = null, int? childPid = null)
    {
        // 메타데이터만 남긴다. 예외 본문·IPC 본문·자격 증명은 쓰지 않는다.
        var path = Path.Combine(AppContext.BaseDirectory, "service-status.ndjson");
        try
        {
            lock (reportGate)
            {
                if (File.Exists(path) && new FileInfo(path).Length > 4 * 1024 * 1024) File.Move(path, path + ".1", true);
                File.AppendAllText(path, JsonSerializer.Serialize(new
                {
                    utc = DateTimeOffset.UtcNow, kind, phase, sessionId, childPid, error = error?.GetType().Name,
                    nativeError = (error as System.ComponentModel.Win32Exception)?.NativeErrorCode, stack = error?.StackTrace
                }) + Environment.NewLine);
            }
        }
        catch (Exception ex) { log.LogError("감독 메타데이터 기록 실패: {Kind}", ex.GetType().Name); }
    }

    /// <summary>헬퍼 하나와 그에 딸린 관리자 헬퍼·Runbook 작업의 수명. Dispose하면 Job으로 모두 끝난다.</summary>
    sealed class HelperScope(CancellationToken ct) : IDisposable
    {
        readonly object ownership = new();
        readonly List<Task> background = new();
        readonly CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        NamedPipeServerStream? server;
        SessionProcess? child;
        IpcPeerSession? peer;
        AdminScope? admin;
        bool disposed;
        public CancellationToken Token => lifetime.Token;
        public TaskCompletionSource StartupFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? SessionId { get; set; }
        /// <summary>지원자가 연결된 세션 ID. 연결 전에는 null이다.</summary>
        public string? SessionBinding { get; set; }
        public string? ConnectionId { get; set; }
        public bool Connected => SessionBinding is not null;
        public string SupporterName { get; set; } = "지원자";
        public MonitorDescriptor? Monitor { get; set; }
        public int ConnectionGeneration { get; set; } = 1;
        public AdminAuthorization? AdminAuthorization { get; set; }
        public FileStream? SessionLease { get; set; }
        public CancellationTokenSource? BookCancel { get; set; }
        public FileAuditLedger FileAudits { get; } = new();
        public AdminScope? Admin { get { lock (ownership) return admin; } }
        public SessionProcess? Child { get { lock (ownership) return child; } }
        public IpcPeerSession? Peer { get { lock (ownership) return peer; } }
        public void Adopt(NamedPipeServerStream value) { lock (ownership) { if (!disposed) { server = value; return; } } value.Dispose(); throw new OperationCanceledException(Token); }
        public void Adopt(SessionProcess value) { lock (ownership) { if (!disposed) { child = value; return; } } value.Dispose(); throw new OperationCanceledException(Token); }
        public void Adopt(IpcPeerSession value) { lock (ownership) { if (!disposed) { peer = value; return; } } _ = ObserveDispose(value); throw new OperationCanceledException(Token); }
        public void Adopt(AdminScope value) { lock (ownership) { if (!disposed && admin == null) { admin = value; return; } } value.Dispose(); throw new OperationCanceledException(Token); }
        public void ReleaseAdmin() { AdminScope? old; lock (ownership) { old = admin; admin = null; } old?.Dispose(); }
        public void RunBackground(Func<Task> operation)
        {
            lock (ownership)
            {
                if (disposed) throw new OperationCanceledException(Token);
                background.RemoveAll(task => task.IsCompleted);
                background.Add(Task.Run(async () => { try { await operation(); } catch (OperationCanceledException) when (Token.IsCancellationRequested) { } }));
            }
        }
        public void Dispose()
        {
            SessionProcess? ownedChild; NamedPipeServerStream? ownedServer; IpcPeerSession? ownedPeer; AdminScope? ownedAdmin;
            lock (ownership) { if (disposed) return; disposed = true; ownedChild = child; ownedServer = server; ownedPeer = peer; ownedAdmin = admin; }
            // Job을 먼저 끝내 멈춘 네이티브 호출이나 IPC가 정리를 막지 않게 한다.
            ownedChild?.Dispose(); ownedAdmin?.Fence();
            try { lifetime.Cancel(); } catch (AggregateException) { }
            AdminAuthorization?.End(); ownedAdmin?.Dispose();
            ownedServer?.Dispose();
            SessionLease?.Dispose();
            _ = CompleteCleanup(ownedPeer);
        }
        async Task CompleteCleanup(IpcPeerSession? ownedPeer)
        {
            await StartupFinished.Task;
            Task[] running; lock (ownership) running = background.ToArray();
            try { await Task.WhenAll(running); } catch { /* 정리 실패도 관찰만 한다. */ }
            if (ownedPeer != null) await ObserveDispose(ownedPeer);
            lifetime.Dispose();
        }
        static async Task ObserveDispose(IpcPeerSession peer) { try { await peer.DisposeAsync(); } catch { } }
    }
}
