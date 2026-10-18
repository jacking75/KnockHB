using DesktopTestDriver;
#if REMOTEASSIST_TESTS
using DesktopTestDriver.RemoteAssist;
#endif

internal static class ExtensionTests
{
    public static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName,"DesktopTestDriver.slnx"))) return dir.FullName;
        throw new Exception("independent tool root missing");
    }
    public static async Task Fixtures()
    {
        var paths = new[] { Path.Combine(Root(),"fixtures","generic-desktop.json") };
#if REMOTEASSIST_TESTS
        paths = Directory.GetFiles(Path.Combine(Root(),"fixtures"),"*.json");
#endif
        foreach (var path in paths)
        {
            var fixture = Wire.Decode<FixtureDefinition>(File.ReadAllText(path)); fixture.Validate();
            var pass = await FixtureRunner.Run(fixture,"mock-run",new SimulatedFixtureBackend(),true,fixture.Environments,_ => Task.FromResult(false));
            Tests.Check(pass.Verdict == Verdict.Pass && pass.Restored && pass.Simulated,"mock fixture failed");
            var fail = await FixtureRunner.Run(fixture,"mock-run",new SimulatedFixtureBackend { FailStep = fixture.Steps[0].Id },true,fixture.Environments,_ => Task.FromResult(false));
            Tests.Check(fail.Verdict == Verdict.Fail && fail.Restored && fail.Receipts.Length == 1 + fixture.Restore.Length,"first fixture failure continued");
            var blocked = await FixtureRunner.Run(fixture,"mock-run",new SimulatedFixtureBackend(),false,fixture.Environments,_ => Task.FromResult(false));
            Tests.Check(blocked.Verdict == Verdict.Blocked && blocked.Receipts.Length == 0,"real recovery bypassed");
            var dirty = await FixtureRunner.Run(fixture,"mock-run",new SimulatedFixtureBackend { FailStep = fixture.Restore[0].Id },true,fixture.Environments,_ => Task.FromResult(false));
            Tests.Check(dirty.Verdict == Verdict.Fail && !dirty.Restored,"cleanup failure passed");
        }
    }
    public static async Task AcceptanceAndPreparation()
    {
        using var f = new Fixture(); var plan = Plan(); var context = Context(f); var registry = new AdapterRegistry(); registry.Register(new CounterAdapter());
        var report = await new ScenarioRunner(registry).Run(plan,context);
        var output = Path.Combine(f.Parent,"bundle"); EvidenceBundle.Write(output,plan,report); EvidenceBundle.Verify(output);
        File.AppendAllText(Path.Combine(output,"report.json")," "); Tests.Reject(() => EvidenceBundle.Verify(output),Error.DelegationMismatch);
#if REMOTEASSIST_TESTS
        var cases = Wire.Decode<AcceptanceCase[]>(File.ReadAllText(Path.Combine(Root(),"adapters","remoteassist","acceptance.json"))); AcceptanceGate.Validate(cases);
        foreach (var id in cases.SelectMany(c => c.PlanIds).Distinct())
        {
            var fixedPlan = Wire.Decode<ScenarioPlan>(File.ReadAllText(Path.Combine(Root(),"scenarios","remoteassist",id + ".json"))); fixedPlan.Validate();
            Tests.Check(fixedPlan.Id == id && cases.Where(c => c.PlanIds.Contains(id)).All(c => fixedPlan.Coverage.Contains(c.Id)),"catalog plan coverage drift");
        }
        Tests.Reject(() => AcceptanceGate.Validate(cases[..^1]),Error.InvalidRequest);
#endif
        var requirement = new AcceptanceCase("F01","two PC",[plan.Id],["two-pc"],["all-subconditions"]);
        Tests.Check(AcceptanceGate.Evaluate(requirement,[report],context.CandidateHash,["two-pc"],new Dictionary<string,bool>{{"all-subconditions",true}},2) == Verdict.NotRun,"mock report promoted");
        Tests.Check(AcceptanceGate.Evaluate(requirement,[report with { Simulated=false }],context.CandidateHash,["two-pc"],new Dictionary<string,bool>{{"all-subconditions",true}},1) == Verdict.Blocked,"single PC promoted");
        var proc = Processes.Identify(System.Diagnostics.Process.GetCurrentProcess());
        var status = new ClientStatus(proc,proc,1,"Default",f.Store.ManifestHash,"mock");
        var observed = new PreparedMachine("test",status,f.Desktop.Observation,new("test",f.Store.Config.RunId,true,true,true,true,true,new('a',64)));
        var ready = Preparation.Check(plan,context,[new(f.Store.PathFor("run.json"),f.Store.ManifestHash)],[observed],f.Store.Root);
        Tests.Check(ready.ContractsReady && !ready.ReadyForRealExecution && ready.Simulated,"preparation mock promoted");
        var fail = Preparation.Check(plan,context,[new(f.Store.PathFor("run.json"),new('f',64))],[observed],f.Store.Root);
        Tests.Check(!fail.ContractsReady,"changed candidate accepted");
    }
#if REMOTEASSIST_TESTS
    public static async Task Tray()
    {
        using var f = new Fixture(); var surface = new ConsentSurface(f); var tray = new TrayUi(surface, f.Store.Config);
        var request = new ConsentRequest("request-1", "fixture-requester", "monitor-0", ["screen", "control"], "approve");
        Task<ConsentRequest> Log(CancellationToken _) => Task.FromResult(request);
        await tray.Start(default); Tests.Check(surface.Last == "HostStart", "start selector mismatch");
        await tray.Decide(request, Log, default);
        Tests.Check(surface.Last == "ConsentApprove" && surface.Toggles["screen"] && surface.Toggles["control"] && !surface.Toggles["admin"], "scope drift");
        await tray.Decide(request with { Decision = "deny" }, Log, default); Tests.Check(surface.Last == "ConsentDeny", "deny not real UI");
        await tray.Decide(request with { Decision = "close" }, Log, default); Tests.Check(surface.Last == "close", "close not keyboard");
        var clicks = surface.Clicks;
        try { await tray.Decide(request with { Requester = "intruder" }, Log, default); throw new Exception("identity accepted"); }
        catch (DriverException e) when (e.Code == Error.DelegationMismatch) { }
        try { await tray.Decide(request, _ => Task.FromResult(request with { RequestId = "replacement" }), default); throw new Exception("request replacement accepted"); }
        catch (DriverException e) when (e.Code == Error.DelegationMismatch) { }
        Tests.Check(surface.Clicks == clicks, "wrong identity clicked");
        try { await tray.Decide(request, _ => { surface.Replaced = true; return Task.FromResult(request); }, default); throw new Exception("replaced HWND accepted"); }
        catch (DriverException e) when (e.Code == Error.WindowChanged) { }
        surface.Replaced = false;
        await tray.Decide(request with { Decision = "expire" }, Log, default); Tests.Check(surface.Clicks == clicks, "timeout performed input");
        await tray.End(default); Tests.Check(surface.Last == "HostStop", "end selector mismatch");
        using var seal = new Fixture(); var frame = await seal.Snapshot();
        Tests.Check((await seal.Engine.Execute(seal.Command("seal-input"))).Verified, "seal failed");
        Tests.Check((await seal.Engine.Execute(seal.Command("click") with { FrameId = frame.Id })).ErrorCode == Error.DelegationMismatch && seal.Backend.Events.Count == 0, "sealed host injected input");
    }
    public static async Task Viewer()
    {
        using var f = new Fixture();
        var step = new ScenarioStep("join","viewer","viewer-join",[],[new("action-receipt","eq","true","driver-local","viewer")]);
        ViewerScope.Require(f.Store.Config,step);
        Tests.Reject(() => ViewerScope.Require(f.Store.Config with { AllowInput = false },step),Error.DelegationMismatch);
        Tests.Reject(() => ViewerScope.Require(f.Store.Config with { AllowedKeys = [65] },step),Error.DelegationMismatch);
        await using var cdp = new FakeCdp(); var viewer = new CdpViewer(cdp, new("https://lab.test/viewer/"));
        await viewer.Join("fixture-requester", "123-456-789", "1234", default);
        Tests.Check(cdp.Calls.Count(c => c.Method == "Input.insertText") == 3 && cdp.Calls.Count(c => c.Method == "Input.dispatchMouseEvent") == 8, "join bypassed browser input");
        Tests.Check(cdp.Calls.Where(c => c.Method == "Runtime.evaluate").All(c => !c.Parameters.GetProperty("expression").GetString()!.Contains(".value=", StringComparison.Ordinal)), "DOM mutation used");
        await viewer.Pointer(0, 0, "move", default);
        var position = cdp.Calls.Last().Parameters;
        Tests.Check(position.GetProperty("x").GetDouble() == 10 && position.GetProperty("y").GetDouble() == 95, "letterbox coordinates drifted");
        await viewer.Key("Shift", "ShiftLeft", 16, true, default); await viewer.Pointer(.5,.5,"down",default);
        cdp.Url = "https://wrong.test/";
        try { await viewer.Click("#hangup",default); throw new Exception("navigated page controlled"); } catch (DriverException e) when(e.Code == Error.WindowChanged) { }
        await viewer.Release(default);
        Tests.Check(cdp.Calls.Last().Parameters.GetProperty("type").GetString() == "mouseReleased", "release blocked by navigation");
    }
    public static Task Observations()
    {
        using var f = new Fixture();
        var tick = System.Diagnostics.Stopwatch.GetTimestamp();
        System.Text.Json.JsonElement Sample(bool held = false, bool valid = true) => Wire.Decode<System.Text.Json.JsonElement>(Wire.Encode(new
        {
            pid = f.Window.Pid, session = 1, window = f.Window.Hwnd, image = f.Window.ImagePath, startTick = tick, utc = DateTimeOffset.UtcNow,
            foreground = true, observationValid = valid, observationOverflow = !valid, events = new[] { new { tick = tick + 1, kind = "key", code = 65, action = "down" }, new { tick = tick + 2, kind = "key", code = 65, action = "up" } },
            clicks = 1, drag = 2, wheel = 1, heldVirtualKeys = held ? new[] { 65 } : Array.Empty<int>(), text = "한글 입력"
        }));
        var expected = new InputExpectation(f.Window, 1, new(tick, 0, 0, 0, 0), Wire.Hash(System.Text.Encoding.UTF8.GetBytes("한글 입력")), true, true);
        var facts = ProductObservations.InputTarget(Sample(), expected, true);
        Tests.Check(facts["text-matches"] == "true" && facts["keys-released"] == "true" && facts["events"] == "2", "independent input observation failed");
        Tests.Check(ProductObservations.InputTarget(Sample(held: true), expected, true)["keys-released"] == "false", "OS key state ignored");
        Tests.Reject(() => ProductObservations.InputTarget(Sample(), expected with { HostInputSealed = false }, true), Error.ObservationUnavailable);
        Tests.Reject(() => ProductObservations.InputTarget(Sample(valid:false), expected, true), Error.ObservationUnavailable);
        Tests.Reject(() => ProductObservations.InputTarget(Sample(), expected with { Window = f.Window with { Pid = 10 } }, true), Error.ObservationUnavailable);
        var before = Wire.Decode<System.Text.Json.JsonElement>("{\"decodedFrames\":10,\"droppedFrames\":0}");
        var after = Wire.Decode<System.Text.Json.JsonElement>("{\"decodedFrames\":30,\"droppedFrames\":1,\"videoWidth\":1920,\"videoHeight\":1080}");
        var video = ProductObservations.Video(before,after,2,true,1920,1080);
        Tests.Check(video["measured-fps"] == "10" && video["marker-matches"] == "true", "measured video drift");
        Tests.Check(ProductObservations.Chat("상대: unique-text", "unique-text")["chat-count"] == "1" && ProductObservations.Chat("xx", "x")["chat-count"] == "2", "chat loop hidden");
        var source = f.Store.PathFor("source.txt"); var dest = f.Store.PathFor("received.txt"); File.WriteAllText(source,"fixture"); File.Copy(source,dest);
        Tests.Check(ProductObservations.FileTransfer(f.Store.Root,source,dest,Wire.FileHash(source))["file-hash-matches"] == "true", "file hash verification missing");
        File.WriteAllText(dest,"tampered"); Tests.Check(ProductObservations.FileTransfer(f.Store.Root,source,dest,Wire.FileHash(source))["file-hash-matches"] == "false", "corrupt transfer passed");
        Tests.Reject(() => ProductObservations.FileTransfer(f.Store.Root,source,Path.Combine(f.Parent,"source.json"),Wire.FileHash(source)), Error.DelegationMismatch);
        Tests.Check(VisualMarker.Colors("run-a").SequenceEqual(VisualMarker.Colors("run-a")) && !VisualMarker.Colors("run-a").SequenceEqual(VisualMarker.Colors("run-b")), "marker not run-bound");
        return Task.CompletedTask;
    }
#endif
    public static ScenarioPlan Plan(string adapter = "counter-example") => new("self-test", adapter, "제품 없는 시나리오 판정", ["D14"], ["모의 노드"], ["독립 관측"],
        "첫 실패 즉시 종료", "소유 입력·자원 해제와 복귀", "계약 검사 재사용", 10, 20, 0, RecoveryGrade.R0,
        [new("one", "host", "increment", [], [new("count", "eq", "1", "sample-observer", "host")]),
         new("two", "host", "increment", [], [new("count", "eq", "2", "sample-observer", "host")])]);
    public static ScenarioContext Context(Fixture f) => new(f.Store.Config.RunId, new('a', 64), new('b', 64), f.Store.ManifestHash, [f.Store.Config], true);
    public static async Task Scenarios()
    {
        using var f = new Fixture(); var registry = new AdapterRegistry(); var adapter = new CounterAdapter(); registry.Register(adapter);
        var plan = Plan(); var context = Context(f);
        Tests.Check(Wire.Decode<ScenarioPlan>(Wire.Encode(plan)).Hash == plan.Hash, "plan roundtrip drift");
        var report = await new ScenarioRunner(registry).Run(plan, context);
        Tests.Check(report.Verdict == Verdict.Pass && report.Simulated && report.CleanupVerified && adapter.Cleaned && report.Steps.Length == 2, "sample failed");
        var failing = plan with { Steps = [plan.Steps[0] with { Assertions = [new("count", "eq", "9", "sample-observer", "host")] }, plan.Steps[1]] };
        var failingContext = context with { Machines = [f.ForPlan(failing)] };
        report = await new ScenarioRunner(registry).Run(failing, failingContext);
        Tests.Check(report.Verdict == Verdict.Fail && report.Steps.Length == 1 && report.CleanupVerified, "first failure continued");
        var wrongSource = new Assertion("count", "eq", "1", "product-remote", "host");
        Tests.Check(!ResultJudge.Matches(wrongSource, report.Steps[0].Evidence), "local action passed as product input");
        var evidence = report.Steps[0].Evidence[0];
        Tests.Reject(() => ResultJudge.Bind(evidence with { CandidateHash = new('f',64) }, failing.Steps[0], failing, context), Error.ObservationUnavailable);
        Tests.Reject(() => (plan with { Steps = [plan.Steps[0] with { Assertions = [] }] }).Validate(), Error.InvalidRequest);
        Tests.Reject(() => context.Validate(failing),Error.DelegationMismatch);
        Tests.Check(!ResultJudge.Matches(new("count", "ge", "1", "sample-observer", "host"), [evidence with { Facts = new() { ["count"] = "NaN" } }]), "NaN passed");
    }
    public static async Task AuthorizationPolicy()
    {
        using var f = new Fixture();
        Tests.Reject(() => Authorization.Verify(f.Store.Config with { MachineId = "different" }), Error.AuthenticationFailed);
        Tests.Reject(() => Authorization.Verify(f.Store.Config with { Delegation = null }), Error.DelegationMismatch);
        var restricted = f.Store.Config with { Delegation = f.Store.Config.Delegation! with { Actions = ["inspect"] } };
        Tests.Reject(() => Authorization.Require(restricted, "click"), Error.DelegationMismatch);
        Tests.Reject(() => Authorization.Require(f.Store.Config, "inspect", "outside-scenario"), Error.DelegationMismatch);
        Authorization.Require(restricted, "stop");
        var frame = await f.Snapshot();
        var text = "private-PIN-password-한글";
        var result = await f.Engine.Execute(f.Command("text") with { FrameId = frame.Id, Text = text });
        Tests.Check(result.ErrorCode == Error.None, "authorized text rejected");
        Tests.Check(!File.ReadAllText(f.Store.PathFor("audit.jsonl")).Contains(text), "audit leaked text");
        Tests.Reject(() => EvidenceVault.PurgeExpired(f.Store, DateTimeOffset.UtcNow.AddDays(2)), Error.CleanupIncomplete);
        await f.Engine.Execute(f.Command("stop"));
        Tests.Check(EvidenceVault.PurgeExpired(f.Store, DateTimeOffset.UtcNow) == 0, "retention ended early");
        Tests.Check(EvidenceVault.PurgeExpired(f.Store, DateTimeOffset.UtcNow.AddDays(2)) > 0 && File.Exists(f.Store.PathFor("ledger.json")) && File.Exists(f.Store.PathFor("secret")), "purge damaged recovery state");
        using var full = new Fixture(); var latest = await full.Snapshot();
        using (var log = new FileStream(full.Store.PathFor("audit.jsonl"),FileMode.Open,FileAccess.Write)) log.SetLength(4_194_305);
        var denied = await full.Engine.Execute(full.Command("click") with { FrameId = latest.Id });
        Tests.Check(denied.ErrorCode == Error.Expired && full.Backend.Events.Count == 0 && full.Store.Stopped,"audit capacity failure allowed input");
        Tests.Check((await full.Engine.Execute(full.Command("stop"))).Verified,"audit failure blocked emergency cleanup");
    }
}
