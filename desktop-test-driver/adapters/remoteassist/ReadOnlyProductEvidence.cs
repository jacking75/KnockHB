using System.Diagnostics;
using System.Text.Json;
using DesktopTestDriver;

namespace DesktopTestDriver.RemoteAssist;

public sealed record EvidenceBindings(string? InputId, string? AuditId, string? SourceFileId, string? DestinationFileId, string? FileHash,
    Rect MarkerRegion, int[] MarkerColors, int VideoWidth, int VideoHeight, string TextHash, string ChatText, string SessionId, string ConnectionId);
public sealed class ReadOnlyProductEvidence(NodeUi host, NodeUi viewerNode, IViewerUi viewer, EvidenceBindings bindings,
    Func<CancellationToken, Task<bool>> verifyRecovery) : IProductEvidence
{
    private JsonElement videoBefore;
    private long videoTick;
    private InputBaseline? inputBefore;
    private WindowIdentity? inputWindow;
    private NodeLease? lease;
    public async Task Prepare(ScenarioPlan plan, ScenarioContext context, CancellationToken cancellation)
    {
        if (bindings.MarkerColors.Length != 0 && !bindings.MarkerColors.SequenceEqual(VisualMarker.Colors(context.RunId))) throw new DriverException(Error.DelegationMismatch);
        lease = viewerNode.BeginLease();
        var viewerInspection = Wire.Decode<InspectResult>(Wire.Encode((await viewerNode.Send("inspect",cancellation)).Evidence));
        if (context.Machine("viewer").Windows.Any(scope => !viewerInspection.Observation.Windows.Any(w =>
            w.ImageHash.Equals(scope.ImageHash,StringComparison.OrdinalIgnoreCase) && w.ImagePath.Equals(scope.ImagePath,StringComparison.OrdinalIgnoreCase) && w.Title == scope.Title)))
            throw new DriverException(Error.WindowChanged);
        videoBefore = await viewer.Observe(cancellation); videoTick = Stopwatch.GetTimestamp();
        if (bindings.InputId != null)
        {
            var sample = (await Artifact(host, bindings.InputId, cancellation)).GetProperty("value");
            var inspection = Wire.Decode<InspectResult>(Wire.Encode((await host.Send("inspect", cancellation)).Evidence));
            inputWindow = inspection.Observation.Windows.SingleOrDefault(w => w.Hwnd == sample.GetProperty("window").GetInt64() && w.Pid == sample.GetProperty("pid").GetInt32())
                ?? throw new DriverException(Error.WindowChanged);
            inputBefore = new(sample.GetProperty("startTick").GetInt64(), sample.GetProperty("events").GetArrayLength(), sample.GetProperty("clicks").GetInt32(), sample.GetProperty("drag").GetInt32(), sample.GetProperty("wheel").GetInt32());
        }
    }
    private static async Task<JsonElement> Artifact(NodeUi ui, string id, CancellationToken cancellation) =>
        Wire.Decode<JsonElement>(Wire.Encode((await ui.Send("read-artifact", cancellation, condition: id)).Evidence));
    public async Task<ObservationRecord[]> Read(string kind, bool hostInputSealed, ScenarioStep step, ScenarioPlan plan, ScenarioContext context, CancellationToken cancellation)
    {
        if (lease?.Healthy != true) throw new DriverException(Error.TransportLost);
        Dictionary<string, string> facts; string hash; string role; string source;
        switch (kind)
        {
            case "observe-video": case "observe-monitor": case "observe-quality":
                var after = await viewer.Observe(cancellation);
                var marker = bindings.MarkerColors.Length == 16 && await viewer.Marker(bindings.MarkerRegion, bindings.MarkerColors, cancellation);
                facts = ProductObservations.Video(videoBefore, after, Stopwatch.GetElapsedTime(videoTick).TotalSeconds, marker, bindings.VideoWidth, bindings.VideoHeight);
                hash = Wire.Hash(System.Text.Encoding.UTF8.GetBytes(Wire.Encode(after))); role = "viewer"; source = "browser-decoder"; break;
            case "observe-input": case "observe-permissions":
                if (bindings.InputId == null || inputBefore == null || inputWindow == null) throw new DriverException(Error.ObservationUnavailable);
                var artifact = await Artifact(host, bindings.InputId, cancellation);
                facts = ProductObservations.InputTarget(artifact.GetProperty("value"), new(inputWindow, context.Machine("host").TargetSession, inputBefore, bindings.TextHash, hostInputSealed, true), context.Simulation);
                hash = artifact.GetProperty("hash").GetString()!; role = "host"; source = "host-input-target"; break;
            case "observe-file":
                if (bindings.SourceFileId == null || bindings.DestinationFileId == null || bindings.FileHash == null) throw new DriverException(Error.ObservationUnavailable);
                var from = await Artifact(viewerNode, bindings.SourceFileId, cancellation); var to = await Artifact(host, bindings.DestinationFileId, cancellation);
                facts = new() { ["file-hash-matches"] = (from.GetProperty("hash").GetString() == bindings.FileHash && to.GetProperty("hash").GetString() == bindings.FileHash).ToString().ToLowerInvariant() };
                hash = to.GetProperty("hash").GetString()!; role = "host"; source = "received-file"; break;
            case "observe-chat":
                var chat = await viewer.Observe(cancellation);
                facts = ProductObservations.Chat(chat.GetProperty("chat").GetString()!, bindings.ChatText);
                hash = facts["chat-hash"]; role = "viewer"; source = "viewer-chat"; break;
            case "observe-audit":
                if (bindings.AuditId == null) throw new DriverException(Error.ObservationUnavailable);
                var audit = await Artifact(host, bindings.AuditId, cancellation);
                // Product audit uses numeric enums. Preserve that wire format; do not link product DTOs.
                var kindName = step.Arguments.GetValueOrDefault("kind") ?? throw new DriverException(Error.InvalidRequest);
                var resultName = step.Arguments.GetValueOrDefault("result") ?? throw new DriverException(Error.InvalidRequest);
                facts = ProductObservations.Audit(audit.GetProperty("value").EnumerateArray().ToArray(), bindings.SessionId, bindings.ConnectionId, kindName, resultName);
                hash = audit.GetProperty("hash").GetString()!; role = "host"; source = "product-audit"; break;
            default: throw new DriverException(Error.Unsupported);
        }
        return [ProductObservations.Record(plan, step, context, role, source, hash, facts)];
    }
    public async Task<bool> Cleanup(ScenarioContext context, CancellationToken cancellation)
    {
        bool clean;
        try { clean = (await viewerNode.Send("stop",cancellation)).Verified; } catch { clean = false; }
        if (lease != null) await lease.DisposeAsync();
        return await verifyRecovery(cancellation) && clean;
    }
}
