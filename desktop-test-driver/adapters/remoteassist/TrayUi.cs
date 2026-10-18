using DesktopTestDriver;

namespace DesktopTestDriver.RemoteAssist;

public sealed record ConsentRequest(string RequestId, string Requester, string Monitor, string[] Permissions, string Decision);
public sealed class TrayUi(IUiSurface ui, RunConfig config)
{
    public const string MainTitle = "RemoteAssist — 대상자 지원";
    public const string ConsentTitle = "원격 지원 권한 승인";
    private static readonly Dictionary<string, UiSelector> Toggles = new()
    {
        ["screen"] = new("ConsentScreen"), ["control"] = new("ConsentControl"), ["admin"] = new("ConsentAdmin"),
        ["file"] = new(Name: "파일 전송 — 수신할 때마다 저장 폴더 승인"), ["chat"] = new(Name: "텍스트 채팅"),
        ["clipboard"] = new(Name: "텍스트 클립보드 읽기·쓰기 — 지원자가 요청할 때만"),
        ["diagnostics"] = new(Name: "시스템 진단 — CPU·메모리·서비스 등 읽기 전용"),
        ["runbook"] = new(Name: "등록 Runbook — 실행마다 실제 내용·영향 승인")
    };
    public async Task Start(CancellationToken cancellation) => await ui.Click(await ui.Read(MainTitle, cancellation), new("HostStart"), cancellation);
    public async Task End(CancellationToken cancellation) => await ui.Click(await ui.Read(MainTitle, cancellation), new("HostStop"), cancellation);
    public async Task<ConsentRequest> ReadRequest(CancellationToken cancellation)
    {
        var frame = await ui.Read(ConsentTitle, cancellation);
        var names = frame.Controls.Select(c => c.Name).ToArray();
        var requester = names.SingleOrDefault(n => n.StartsWith("접속 요청: ", StringComparison.Ordinal));
        var monitor = names.SingleOrDefault(n => n.StartsWith("공유 대상: ", StringComparison.Ordinal));
        if (requester == null || monitor == null) throw new DriverException(Error.ObservationUnavailable);
        // UI-scoped identity, not the product's private IPC request ID.
        return new(Wire.Hash(System.Text.Encoding.UTF8.GetBytes(Wire.Encode(frame.Window))), requester[7..], monitor[7..], [], "");
    }
    public async Task Decide(ConsentRequest expected, Func<CancellationToken, Task<ConsentRequest>> readRequestLog, CancellationToken cancellation)
    {
        if (!Wire.Id(expected.RequestId) || expected.Requester != config.Delegation!.Requester || expected.Decision is not ("approve" or "deny" or "close" or "expire") ||
            expected.Permissions.Except(config.Delegation.ProductPermissions).Any() || expected.Permissions.Except(Toggles.Keys).Any() ||
            expected.Permissions.Contains("control") && !expected.Permissions.Contains("screen") || expected.Permissions.Contains("admin") && !expected.Permissions.Contains("control"))
            throw new DriverException(Error.DelegationMismatch);
        var first = await ui.Read(ConsentTitle, cancellation);
        async Task<Frame> Checked()
        {
            var logged = await readRequestLog(cancellation);
            if (logged.RequestId != expected.RequestId || logged.Requester != expected.Requester || logged.Monitor != expected.Monitor) throw new DriverException(Error.DelegationMismatch);
            var frame = await ui.Read(ConsentTitle, cancellation);
            if (frame.Window != first.Window || !frame.Controls.Any(c => c.Name == "접속 요청: " + expected.Requester) ||
                !frame.Controls.Any(c => c.Name == "공유 대상: " + expected.Monitor)) throw new DriverException(Error.WindowChanged);
            return frame;
        }
        var current = await Checked();
        if (expected.Decision == "expire") return; // Observe timeout in the next step; never fabricate a rejection.
        if (expected.Decision == "close") { await ui.Chord(current, [18, 115], cancellation); return; }
        if (expected.Decision == "deny") { await ui.Click(current, new("ConsentDeny"), cancellation); return; }
        // First remove excess privileges in reverse dependency order; then enable only the requested set.
        foreach (var permission in Toggles.Keys.Reverse().Concat(Toggles.Keys))
        {
            current = await Checked(); var control = current.Controls.SingleOrDefault(c =>
                Toggles[permission].AutomationId != null ? c.AutomationId == Toggles[permission].AutomationId : c.Name == Toggles[permission].Name)
                ?? throw new DriverException(Error.ObservationUnavailable);
            if (control.Toggle is not ("On" or "Off")) throw new DriverException(Error.ObservationUnavailable);
            bool wanted = expected.Permissions.Contains(permission);
            // During the removal pass do not enable a dependent control before its parent.
            if (control.Toggle == "On" && !wanted || control.Toggle == "Off" && wanted && control.Enabled)
            {
                await ui.Click(current, Toggles[permission], cancellation);
                var after = await Checked();
                if (NodeUi.Select(after, Toggles[permission]).Toggle != (wanted ? "On" : "Off")) throw new DriverException(Error.ObservationUnavailable);
            }
        }
        current = await Checked();
        foreach (var item in Toggles)
        {
            var control = current.Controls.Single(c => item.Value.AutomationId != null ? c.AutomationId == item.Value.AutomationId : c.Name == item.Value.Name);
            if (control.Toggle != (expected.Permissions.Contains(item.Key) ? "On" : "Off")) throw new DriverException(Error.DelegationMismatch);
        }
        await ui.Click(current, new("ConsentApprove"), cancellation);
    }
}
public sealed class TrayPairingSecrets(IUiSurface ui) : IEphemeralSecrets
{
    public async Task<(string Code, string Pin)> Pairing(CancellationToken cancellation)
    {
        var frame = await ui.Read(TrayUi.MainTitle, cancellation);
        var code = NodeUi.Select(frame, new("PairingCode")).Name;
        var pin = NodeUi.Select(frame, new("PairingPin")).Name;
        if (!pin.StartsWith("PIN ", StringComparison.Ordinal)) throw new DriverException(Error.ObservationUnavailable);
        return (code, pin[4..]);
    }
}
