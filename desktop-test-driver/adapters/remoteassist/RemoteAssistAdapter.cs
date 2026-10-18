using System.Text;
using DesktopTestDriver;

namespace DesktopTestDriver.RemoteAssist;

public interface IProductEvidence
{
    // Implementations read fresh, identity-bound artifacts. They never modify product consent/state.
    Task<ObservationRecord[]> Read(string kind, bool hostInputSealed, ScenarioStep step, ScenarioPlan plan, ScenarioContext context, CancellationToken cancellation);
    Task Prepare(ScenarioPlan plan, ScenarioContext context, CancellationToken cancellation);
    Task<bool> Cleanup(ScenarioContext context, CancellationToken cancellation);
}
public interface IEphemeralSecrets
{
    Task<(string Code, string Pin)> Pairing(CancellationToken cancellation);
}
public sealed class RemoteAssistAdapter(NodeUi host, TrayUi tray, IViewerUi viewer, IProductEvidence observer, IEphemeralSecrets secrets, bool simulated,
    Func<CancellationToken,Task<PreparationReport>>? preparation = null) : IScenarioAdapter
{
    public string Id => "remoteassist";
    private bool sealedInput;
    private NodeLease? lease;
    public async Task Prepare(ScenarioPlan plan, ScenarioContext context, CancellationToken cancellation)
    {
        if (context.Simulation != simulated) throw new DriverException(Error.DelegationMismatch);
        if (!context.Simulation)
        {
            if (preparation == null) throw new DriverException(Error.RecoveryRequired);
            var ready = await preparation(cancellation);
            if (!ready.ReadyForRealExecution || ready.Simulated || !ready.ContractsReady) throw new DriverException(Error.RecoveryRequired);
        }
        _ = context.Machine("host"); _ = context.Machine("viewer");
        sealedInput = false;
        lease = host.BeginLease();
        await observer.Prepare(plan, context, cancellation);
    }
    private static void Permission(ScenarioContext context, string permission)
    {
        if (context.Machines.Any(m => !m.Delegation!.ProductPermissions.Contains(permission))) throw new DriverException(Error.DelegationMismatch);
    }
    public async Task<ObservationRecord[]> Execute(ScenarioStep step, ScenarioPlan plan, ScenarioContext context, CancellationToken cancellation)
    {
        if (lease?.Healthy != true) throw new DriverException(Error.TransportLost);
        if (step.Action.StartsWith("viewer-",StringComparison.Ordinal)) ViewerScope.Require(context.Machine("viewer"),step);
        string Arg(string key) => step.Arguments.GetValueOrDefault(key) ?? throw new DriverException(Error.InvalidRequest);
        void Role(string role) { if (step.Role != role) throw new DriverException(Error.DelegationMismatch); }
        var changed = false;
        switch (step.Action)
        {
            case "host-start": Role("host"); if (sealedInput) throw new DriverException(Error.DelegationMismatch); await tray.Start(cancellation); changed = true; break;
            case "viewer-join":
                Role("viewer"); var pairing = await secrets.Pairing(cancellation);
                await viewer.Join(context.Machine("host").Delegation!.Requester, pairing.Code, pairing.Pin, cancellation); changed = true; break;
            case "consent":
                Role("host"); if (sealedInput) throw new DriverException(Error.DelegationMismatch);
                var request = await tray.ReadRequest(cancellation);
                if (request.Monitor != Arg("monitor")) throw new DriverException(Error.DelegationMismatch);
                var permissions = Arg("permissions").Split(',', StringSplitOptions.RemoveEmptyEntries);
                foreach (var permission in permissions) Permission(context, permission);
                await tray.Decide(request with { Permissions = permissions, Decision = Arg("decision") }, tray.ReadRequest, cancellation); changed = true; break;
            case "seal-host": Role("host"); await host.Send("seal-input", cancellation, condition: plan.Id); sealedInput = true; changed = true; break;
            case "target-end":
                Role("host"); await host.Send("resume-input", cancellation, condition: plan.Id); sealedInput = false; await tray.End(cancellation); changed = true; break;
            case "viewer-pointer":
                Role("viewer"); Permission(context, "control"); if (!sealedInput) throw new DriverException(Error.DelegationMismatch);
                await viewer.Pointer(double.Parse(Arg("x"), System.Globalization.CultureInfo.InvariantCulture), double.Parse(Arg("y"), System.Globalization.CultureInfo.InvariantCulture), Arg("action"), cancellation); changed = true; break;
            case "viewer-key":
                Role("viewer"); Permission(context, "control"); if (!sealedInput) throw new DriverException(Error.DelegationMismatch);
                if ((await viewer.Observe(cancellation)).GetProperty("activeElement").GetString() != "screen") throw new DriverException(Error.ForegroundMismatch);
                await viewer.Key(Arg("key"), Arg("code"), int.Parse(Arg("vk"), System.Globalization.CultureInfo.InvariantCulture), Arg("down") == "true", cancellation); changed = true; break;
            case "viewer-text":
                Role("viewer"); Permission(context, "control"); if (!sealedInput) throw new DriverException(Error.DelegationMismatch);
                await viewer.Text("#remote-text", Arg("text"), cancellation); await viewer.Click("#send-text", cancellation); changed = true; break;
            case "viewer-chat": Role("viewer"); Permission(context, "chat"); await viewer.Text("#chat-text", Arg("text"), cancellation); await viewer.Click("#chat-send", cancellation); changed = true; break;
            case "viewer-end": Role("viewer"); await viewer.Click("#hangup", cancellation); changed = true; break;
            case "observe-video": case "observe-input": case "observe-permissions": case "observe-file": case "observe-chat": case "observe-monitor": case "observe-quality": case "observe-audit":
                return await observer.Read(step.Action, sealedInput, step, plan, context, cancellation);
            default: throw new DriverException(Error.Unsupported);
        }
        var facts = new Dictionary<string, string> { ["action-receipt"] = changed.ToString().ToLowerInvariant(), ["application-pass"] = "false" };
        return [ProductObservations.Record(plan, step, context, step.Role, "driver-local", Wire.Hash(Encoding.UTF8.GetBytes(Wire.Encode(facts))), facts)];
    }
    public async Task<bool> Cleanup(ScenarioContext context, CancellationToken cancellation)
    {
        async Task<bool> Attempt(Func<Task<bool>> action) { try { return await action().WaitAsync(cancellation); } catch { return false; } }
        var results = await Task.WhenAll(
            Attempt(async () => { await viewer.Release(cancellation); return true; }),
            Attempt(async () => (await host.Send("stop",cancellation)).Verified),
            Attempt(() => observer.Cleanup(context,cancellation)));
        if (lease != null) await lease.DisposeAsync();
        return results.All(v => v);
    }
}
public static class ViewerScope
{
    public static void Require(RunConfig config, ScenarioStep step)
    {
        if (!config.AllowInput || config.Windows.Length == 0) throw new DriverException(Error.DelegationMismatch);
        Authorization.Require(config,"click");
        if (step.Action is "viewer-join" or "viewer-text" or "viewer-chat")
        {
            Authorization.Require(config,"text"); Authorization.Require(config,"chord");
            if (!new[] {17,65}.All(config.AllowedKeys.Contains)) throw new DriverException(Error.DelegationMismatch);
        }
        if (step.Action == "viewer-key")
        {
            var action = step.Arguments.GetValueOrDefault("down") == "true" ? "key-down" : "key-up";
            Authorization.Require(config,action);
            if (!int.TryParse(step.Arguments.GetValueOrDefault("vk"),out var vk) || !config.AllowedKeys.Contains(vk)) throw new DriverException(Error.DelegationMismatch);
        }
        if (step.Action == "viewer-pointer") Authorization.Require(config,"move");
    }
}
