using System.Text.Json;
using DesktopTestDriver;
using DesktopTestDriver.RemoteAssist;

internal sealed class ConsentSurface(Fixture fixture) : IUiSurface
{
    public int Clicks; public string? Last; public bool Replaced;
    public readonly Dictionary<string, bool> Toggles = new() { ["screen"] = false, ["control"] = false, ["admin"] = false, ["file"] = false, ["chat"] = false, ["clipboard"] = false, ["diagnostics"] = false, ["runbook"] = false };
    public Task<Frame> Read(string title, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var controls = new List<UiNode> { new("접속 요청: fixture-requester", "", "Text", new(0,0,1,1), true, null), new("공유 대상: monitor-0", "", "Text", new(0,0,1,1), true, null) };
        string[] names = ["", "", "", "파일 전송 — 수신할 때마다 저장 폴더 승인", "텍스트 채팅", "텍스트 클립보드 읽기·쓰기 — 지원자가 요청할 때만", "시스템 진단 — CPU·메모리·서비스 등 읽기 전용", "등록 Runbook — 실행마다 실제 내용·영향 승인"];
        int i = 0;
        foreach (var t in Toggles)
        {
            var id = i < 3 ? "Consent" + char.ToUpperInvariant(t.Key[0]) + t.Key[1..] : "";
            bool enabled = t.Key == "control" ? Toggles["screen"] : t.Key != "admin" || Toggles["control"];
            controls.Add(new(names[i++], id, "CheckBox", fixture.Window.Bounds, enabled, t.Value ? "On" : "Off"));
        }
        foreach (var id in new[] { "ConsentApprove", "ConsentDeny", "HostStart", "HostStop" }) controls.Add(new(id,id,"Button",fixture.Window.Bounds,true,null));
        var window = fixture.Window with { Title = title, Hwnd = Replaced ? 321 : fixture.Window.Hwnd };
        return Task.FromResult(new Frame(Guid.NewGuid().ToString("N"), System.Diagnostics.Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow, fixture.Desktop.Observation, window, null, null, controls.ToArray()));
    }
    public Task Click(Frame frame, UiSelector selector, CancellationToken cancellation)
    {
        var control = NodeUi.Select(frame, selector); Clicks++; Last = control.AutomationId;
        var ids = new Dictionary<string,string> { ["ConsentScreen"]="screen", ["ConsentControl"]="control", ["ConsentAdmin"]="admin" };
        var key = ids.GetValueOrDefault(control.AutomationId) ?? control.Name switch
        {
            "파일 전송 — 수신할 때마다 저장 폴더 승인" => "file", "텍스트 채팅" => "chat", "텍스트 클립보드 읽기·쓰기 — 지원자가 요청할 때만" => "clipboard",
            "시스템 진단 — CPU·메모리·서비스 등 읽기 전용" => "diagnostics", "등록 Runbook — 실행마다 실제 내용·영향 승인" => "runbook", _ => ""
        };
        if (key != "") { Toggles[key] = !Toggles[key]; if (!Toggles["screen"]) Toggles["control"] = false; if (!Toggles["control"]) Toggles["admin"] = false; }
        return Task.CompletedTask;
    }
    public Task Chord(Frame frame, int[] keys, CancellationToken cancellation) { Clicks++; Last = "close"; return Task.CompletedTask; }
}
internal sealed class FakeCdp : ICdpConnection
{
    public readonly List<(string Method, JsonElement Parameters)> Calls = [];
    public string Url = "https://lab.test/viewer/";
    public Task<JsonElement> Call(string method, object parameters, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested(); var json = Wire.Decode<JsonElement>(Wire.Encode(parameters)); Calls.Add((method,json));
        if (method != "Runtime.evaluate") return Task.FromResult(Wire.Decode<JsonElement>("{}"));
        var expression = json.GetProperty("expression").GetString()!;
        object value = expression == "location.href" ? Url : expression.StartsWith("document.activeElement===",StringComparison.Ordinal) ? true : expression.Contains("querySelectorAll", StringComparison.Ordinal)
            ? new { x = 10, y = 20, width = 800, height = 600, disabled = false, visible = true, hit = true, videoWidth = 1920, videoHeight = 1080 }
            : new { decodedFrames = 10, droppedFrames = 0, readyState = 4, videoWidth = 1920, videoHeight = 1080, status = "connected", fileStatus = "", chat = "", quality = "" };
        return Task.FromResult(Wire.Decode<JsonElement>(Wire.Encode(new { result = new { value } })));
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
