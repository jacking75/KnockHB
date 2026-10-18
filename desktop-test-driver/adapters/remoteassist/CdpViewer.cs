using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DesktopTestDriver;

namespace DesktopTestDriver.RemoteAssist;

public interface ICdpConnection : IAsyncDisposable
{
    Task<JsonElement> Call(string method, object parameters, CancellationToken cancellation);
}
public sealed class CdpConnection : ICdpConnection
{
    private readonly ClientWebSocket socket = new();
    private readonly SemaphoreSlim gate = new(1);
    private int id;
    public static async Task<CdpConnection> Connect(Uri endpoint, Uri viewer, CancellationToken cancellation)
    {
        if (!endpoint.IsLoopback || endpoint.Scheme != "http" || endpoint.Port is < 25001 or > 25199 || viewer.Scheme != "https" ||
            viewer.UserInfo.Length != 0 || viewer.Query.Length != 0 || viewer.Fragment.Length != 0) throw new DriverException(Error.InvalidRequest);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(3000);
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { MaxResponseContentBufferSize = Wire.MaxMessage };
        var tabs = Wire.Decode<JsonElement>(await client.GetStringAsync(new Uri(endpoint, "/json/list"), timeout.Token));
        var matches = tabs.EnumerateArray().Where(t => t.GetProperty("type").GetString() == "page" && t.GetProperty("url").GetString() == viewer.AbsoluteUri).ToArray();
        if (matches.Length != 1) throw new DriverException(Error.WindowChanged);
        var address = new Uri(matches[0].GetProperty("webSocketDebuggerUrl").GetString()!);
        if (!address.IsLoopback || address.Scheme != "ws" || address.Port != endpoint.Port) throw new DriverException(Error.AuthenticationFailed);
        var connection = new CdpConnection();
        try { await connection.socket.ConnectAsync(address, timeout.Token); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }
    public async Task<JsonElement> Call(string method, object parameters, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(3000);
        await gate.WaitAsync(timeout.Token);
        try
        {
            int requestId = ++id;
            await socket.SendAsync(Encoding.UTF8.GetBytes(Wire.Encode(new { id = requestId, method, @params = parameters })), WebSocketMessageType.Text, true, timeout.Token);
            var buffer = new byte[8192];
            while (true)
            {
                using var message = new MemoryStream(); ValueWebSocketReceiveResult received;
                do
                {
                    received = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
                    if (received.MessageType != WebSocketMessageType.Text || message.Length + received.Count > Wire.MaxMessage) throw new DriverException(Error.InvalidRequest);
                    message.Write(buffer, 0, received.Count);
                } while (!received.EndOfMessage);
                var response = Wire.Decode<JsonElement>(Encoding.UTF8.GetString(message.ToArray()));
                if (!response.TryGetProperty("id", out var responseId) || responseId.GetInt32() != requestId) continue;
                if (response.TryGetProperty("error", out _)) throw new DriverException(Error.ObservationUnavailable);
                return response.GetProperty("result").Clone();
            }
        }
        finally { gate.Release(); }
    }
    public ValueTask DisposeAsync() { socket.Abort(); socket.Dispose(); gate.Dispose(); return ValueTask.CompletedTask; }
}
public interface IViewerUi
{
    Task Join(string requester, string code, string pin, CancellationToken cancellation);
    Task Click(string selector, CancellationToken cancellation);
    Task Text(string selector, string value, CancellationToken cancellation);
    Task<JsonElement> Observe(CancellationToken cancellation);
    Task Pointer(double x, double y, string action, CancellationToken cancellation);
    Task Key(string key, string code, int virtualKey, bool down, CancellationToken cancellation);
    Task Release(CancellationToken cancellation);
    Task<bool> Marker(Rect videoRegion, int[] colors, CancellationToken cancellation);
}
// Runtime evaluation reads DOM only. Product actions use actual browser input events, never product API/IPC/state mutation.
public sealed class CdpViewer(ICdpConnection connection, Uri expectedUrl) : IViewerUi
{
    private static readonly HashSet<string> Selectors = ["#name", "#code", "#pin", "#join-form button", "#hangup", "#remote-text", "#send-text", "#chat-text", "#chat-send", "#monitor-list", "#monitor-choice", "#monitor-select", "#quality-mode", "#screen", "#admin-request", "#admin-revoke", "#admin-sas", "#file-send", "#file-accept", "#file-reject", "#file-cancel", "#clipboard-text", "#clipboard-send", "#clipboard-get", "#runbook-send", "#runbook-cancel"];
    private readonly Dictionary<string, (string Code, int Vk)> heldKeys = [];
    private bool heldButton;
    private async Task<JsonElement> Read(string expression, CancellationToken cancellation)
    {
        var result = await connection.Call("Runtime.evaluate", new { expression, returnByValue = true }, cancellation);
        if (result.TryGetProperty("exceptionDetails", out _)) throw new DriverException(Error.ObservationUnavailable);
        return result.GetProperty("result").GetProperty("value").Clone();
    }
    private async Task Guard(CancellationToken cancellation)
    {
        if (expectedUrl.Scheme != "https" || expectedUrl.Query.Length != 0 || expectedUrl.UserInfo.Length != 0 ||
            (await Read("location.href", cancellation)).GetString() != expectedUrl.AbsoluteUri) throw new DriverException(Error.WindowChanged);
    }
    private async Task<JsonElement> Element(string selector, CancellationToken cancellation)
    {
        await Guard(cancellation);
        if (!Selectors.Contains(selector)) throw new DriverException(Error.DelegationMismatch);
        return await Read("(()=>{const es=document.querySelectorAll(" + JsonSerializer.Serialize(selector) + ");if(es.length!==1) return null;const e=es[0],r=e.getBoundingClientRect();return {x:r.x,y:r.y,width:r.width,height:r.height,disabled:!!e.disabled,visible:!!e.getClientRects().length,hit:document.elementFromPoint(r.x+r.width/2,r.y+r.height/2)===e,videoWidth:e.videoWidth||0,videoHeight:e.videoHeight||0}})()", cancellation);
    }
    public async Task Click(string selector, CancellationToken cancellation)
    {
        var e = await Element(selector, cancellation);
        if (e.ValueKind == JsonValueKind.Null || e.GetProperty("disabled").GetBoolean() || !e.GetProperty("visible").GetBoolean() || !e.GetProperty("hit").GetBoolean()) throw new DriverException(Error.ObservationUnavailable);
        double x = e.GetProperty("x").GetDouble() + e.GetProperty("width").GetDouble() / 2, y = e.GetProperty("y").GetDouble() + e.GetProperty("height").GetDouble() / 2;
        heldButton = true;
        await connection.Call("Input.dispatchMouseEvent", new { type = "mousePressed", x, y, button = "left", clickCount = 1 }, cancellation);
        await connection.Call("Input.dispatchMouseEvent", new { type = "mouseReleased", x, y, button = "left", clickCount = 1 }, cancellation);
        heldButton = false;
    }
    public async Task Text(string selector, string value, CancellationToken cancellation)
    {
        if (value.Length > 8192) throw new DriverException(Error.InvalidRequest);
        await Click(selector, cancellation);
        if (!(await Read("document.activeElement===document.querySelector(" + JsonSerializer.Serialize(selector) + ")",cancellation)).GetBoolean()) throw new DriverException(Error.ForegroundMismatch);
        // Select existing field content using real keyboard events; no DOM value assignment.
        await Key("Control", "ControlLeft", 17, true, cancellation);
        try { await Key("a", "KeyA", 65, true, cancellation); await Key("a", "KeyA", 65, false, cancellation); }
        finally { await Key("Control", "ControlLeft", 17, false, cancellation); }
        await Guard(cancellation);
        await connection.Call("Input.insertText", new { text = value }, cancellation);
    }
    public async Task Join(string requester, string code, string pin, CancellationToken cancellation)
    {
        if (requester.Length is < 1 or > 80 || code.Count(char.IsAsciiDigit) != 9 || code.Any(c => !char.IsAsciiDigit(c) && c != '-') || pin.Length != 4 || !pin.All(char.IsAsciiDigit)) throw new DriverException(Error.InvalidRequest);
        await Text("#name", requester, cancellation); await Text("#code", code, cancellation); await Text("#pin", pin, cancellation); await Click("#join-form button", cancellation);
    }
    public async Task<JsonElement> Observe(CancellationToken cancellation)
    {
        await Guard(cancellation);
        return await Read("(()=>{const v=document.querySelector('#screen'),q=v?.getVideoPlaybackQuality();return {status:document.querySelector('#session-status')?.textContent||document.querySelector('#status')?.textContent||'',decodedFrames:Number.isSafeInteger(v?.webkitDecodedFrameCount)?v.webkitDecodedFrameCount:0,droppedFrames:q?.droppedVideoFrames||0,videoWidth:v?.videoWidth||0,videoHeight:v?.videoHeight||0,readyState:v?.readyState||0,activeElement:document.activeElement?.id||'',fileStatus:document.querySelector('#file-status')?.textContent||'',chat:document.querySelector('#chat-history')?.textContent||'',quality:document.querySelector('#quality-status')?.textContent||''}})()", cancellation);
    }
    public async Task<bool> Marker(Rect videoRegion, int[] colors, CancellationToken cancellation)
    {
        await Guard(cancellation);
        if (colors.Length != 16 || colors.Any(c => c is < 0 or > 0xFFFFFF) || videoRegion.Width <= 0 || videoRegion.Height <= 0 || videoRegion.X < 0 || videoRegion.Y < 0)
            throw new DriverException(Error.InvalidRequest);
        var samples = await Read("(()=>{const v=document.querySelector('#screen'),r=" + Wire.Encode(videoRegion) + ";if(!v||v.readyState<2||r.x+r.width>v.videoWidth||r.y+r.height>v.videoHeight)return [];const c=document.createElement('canvas');c.width=v.videoWidth;c.height=v.videoHeight;const x=c.getContext('2d',{willReadFrequently:true});x.drawImage(v,0,0);return Array.from({length:16},(_,i)=>Array.from(x.getImageData(Math.floor(r.x+(i+.5)*r.width/16),Math.floor(r.y+r.height/2),1,1).data).slice(0,3))})()", cancellation);
        if (samples.GetArrayLength() != colors.Length) return false;
        for (int i = 0; i < colors.Length; i++)
            for (int channel = 0; channel < 3; channel++)
                if (Math.Abs(samples[i][channel].GetInt32() - ((colors[i] >> (16 - channel * 8)) & 255)) > 40) return false;
        return true;
    }
    public async Task Pointer(double x, double y, string action, CancellationToken cancellation)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || x is < 0 or > 1 || y is < 0 or > 1 || action is not ("move" or "down" or "up" or "wheel-up" or "wheel-down")) throw new DriverException(Error.InvalidRequest);
        var element = await Element("#screen", cancellation);
        if (!element.GetProperty("hit").GetBoolean() || element.GetProperty("videoWidth").GetDouble() <= 0 || element.GetProperty("videoHeight").GetDouble() <= 0) throw new DriverException(Error.ObservationUnavailable);
        double w = element.GetProperty("width").GetDouble(), h = element.GetProperty("height").GetDouble();
        var scale = Math.Min(w / element.GetProperty("videoWidth").GetDouble(), h / element.GetProperty("videoHeight").GetDouble());
        var vw = element.GetProperty("videoWidth").GetDouble() * scale; var vh = element.GetProperty("videoHeight").GetDouble() * scale;
        x = element.GetProperty("x").GetDouble() + (w - vw) / 2 + x * Math.Max(0, vw - 1);
        y = element.GetProperty("y").GetDouble() + (h - vh) / 2 + y * Math.Max(0, vh - 1);
        if (action == "down") heldButton = true;
        await connection.Call("Input.dispatchMouseEvent", new { type = action.StartsWith("wheel", StringComparison.Ordinal) ? "mouseWheel" : action == "down" ? "mousePressed" : action == "up" ? "mouseReleased" : "mouseMoved", x, y,
            button = action is "down" or "up" ? "left" : "none", buttons = action == "up" ? 0 : heldButton ? 1 : 0, clickCount = 1, deltaX = 0, deltaY = action == "wheel-up" ? -120 : action == "wheel-down" ? 120 : 0 }, cancellation);
        if (action == "up") heldButton = false;
    }
    public async Task Key(string key, string code, int virtualKey, bool down, CancellationToken cancellation)
    {
        await Guard(cancellation);
        if (key.Length is < 1 or > 32 || !Wire.Id(code) || virtualKey is < 1 or > 254) throw new DriverException(Error.InvalidRequest);
        if (down) heldKeys[key] = (code, virtualKey);
        bool Modifier(string name) => heldKeys.ContainsKey(name) && (down || name != key);
        int modifiers = (Modifier("Alt") ? 1 : 0) | (Modifier("Control") ? 2 : 0) | (Modifier("Meta") ? 4 : 0) | (Modifier("Shift") ? 8 : 0);
        await connection.Call("Input.dispatchKeyEvent", new { type = down ? "keyDown" : "keyUp", key, code, windowsVirtualKeyCode = virtualKey, modifiers }, cancellation);
        if (!down) heldKeys.Remove(key);
    }
    public async Task Release(CancellationToken cancellation)
    {
        bool clean = true;
        // Release even after navigation/lease failure. Never press anything during cleanup.
        foreach (var pair in heldKeys.ToArray())
        {
            try
            {
                await connection.Call("Input.dispatchKeyEvent", new { type = "keyUp", key = pair.Key, code = pair.Value.Code, windowsVirtualKeyCode = pair.Value.Vk, modifiers = 0 }, cancellation);
                heldKeys.Remove(pair.Key);
            }
            catch { clean = false; }
        }
        if (heldButton)
        {
            try { await connection.Call("Input.dispatchMouseEvent", new { type = "mouseReleased", x = 0, y = 0, button = "left", buttons = 0, clickCount = 1 }, cancellation); heldButton = false; }
            catch { clean = false; }
        }
        if (!clean) throw new DriverException(Error.CleanupIncomplete);
    }
}
