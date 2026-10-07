using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using RemoteAssist.Host.Core;
using RemoteAssist.Host.Core.Media;
using RemoteAssist.Host.Core.Security;
using RemoteAssist.Host.Core.Signaling;
using RemoteAssist.Host.Core.Capture;
using RemoteAssist.Protocol;
using SIPSorcery.Net;

var tests = new (string Name, Func<Task> Run)[]
{
    ("missing and substituted FFmpeg DLL fail before native load", async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "remoteassist-test-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            await Reject(() => { FFmpegLoader.ValidateBundle(directory); return Task.CompletedTask; });
            using var manifest = typeof(FFmpegLoader).Assembly.GetManifestResourceStream("RemoteAssist.FFmpeg.Libraries")!;
            using var doc = JsonDocument.Parse(manifest);
            foreach (var file in doc.RootElement.GetProperty("files").EnumerateArray())
                File.WriteAllBytes(Path.Combine(directory, file.GetProperty("name").GetString()!), [1,2,3]);
            await Reject(() => { FFmpegLoader.ValidateBundle(directory); return Task.CompletedTask; });
            Check(FFmpegLoader.LoadedFrom is null, "invalid bundle was initialized");
        }
        finally { Directory.Delete(directory, true); }
    }),
    ("encoder fallback disposes failed HW before next HW and SW", async () =>
    {
        foreach (var winner in new[] { "h264_qsv", "libopenh264" })
        {
            var factory = new FakeEncoderFactory(winner);
            using (var encoder = new FFmpegH264Encoder(null, null, factory))
            {
                encoder.Configure(2, 2, 30, 4000);
                Check(encoder.EncoderName == winner, "wrong fallback winner");
                Check(factory.Candidates.Take(factory.Candidates.Count - 1).All(x => x.Disposed), "failed candidate alive");
                encoder.Encode(new byte[16], true);
                Check(factory.Candidates.Last().Forced >= 2, "forceKeyframe ignored");
            }
            Check(factory.Candidates.All(x => x.Disposed), "winner leaked on dispose");
        }
        var allFail = new FakeEncoderFactory(null);
        using (var encoder = new FFmpegH264Encoder(null, null, allFail))
            await Reject(() => { encoder.Configure(2, 2, 30, 4000); return Task.CompletedTask; });
        Check(allFail.Candidates.Count == 5 && allFail.Candidates.All(x => x.Disposed), "all-fail path leaked or skipped");
        var strict = new FakeEncoderFactory("libopenh264");
        using (var encoder = new FFmpegH264Encoder("h264_nvenc", null, strict))
            await Reject(() => { encoder.Configure(2, 2, 30, 4000); return Task.CompletedTask; });
        Check(strict.Candidates.Count == 1, "forced encoder silently fell back");
    }),
    ("RTCP PLI/FIR requests are video-only and coalesced", () =>
    {
        var requests = new KeyframeRequests();
        Check(requests.Consume() && !requests.Consume(), "initial keyframe was not singular");
        foreach (var type in new[] { PSFBFeedbackTypesEnum.PLI, PSFBFeedbackTypesEnum.FIR })
        {
            var report = new RTCPCompoundPacket(Array.Empty<byte>()) { Feedback = new RTCPFeedback(1, 2, type) };
            requests.Observe(SDPMediaTypesEnum.audio, report);
            Check(!requests.Consume(), "audio report requested video frame");
            requests.Observe(SDPMediaTypesEnum.video, report);
            requests.Observe(SDPMediaTypesEnum.video, report);
            Check(requests.Consume() && !requests.Consume(), "PLI/FIR were lost or duplicated");
        }
        var nack = new RTCPCompoundPacket(Array.Empty<byte>()) { Feedback = new RTCPFeedback(1, 2, RTCPFeedbackTypesEnum.NACK) };
        requests.Observe(SDPMediaTypesEnum.video, nack);
        Check(!requests.Consume(), "transport feedback treated as picture loss");
        return Task.CompletedTask;
    }),
    ("UTF8 all fragment boundaries", async () =>
    {
        var bytes = Encoding.UTF8.GetBytes("{\"v\":1,\"type\":\"error\",\"reason\":\"한글 😀\"}");
        for (var split = 1; split < bytes.Length; split++)
        {
            using var socket = new FragmentSocket([bytes[..split], bytes[split..]]);
            var message = await SignalingFraming.ReceiveAsync(socket, CancellationToken.None);
            Check(message?.Reason == "한글 😀", "split UTF8 was corrupted");
        }
    }),
    ("framing rejects binary, oversize, duplicate and absent version", async () =>
    {
        using var binary = new FragmentSocket([Encoding.UTF8.GetBytes("{\"v\":1,\"type\":\"ping\"}")], WebSocketMessageType.Binary);
        await Reject(() => SignalingFraming.ReceiveAsync(binary, default));
        using var large = new FragmentSocket([new byte[128 * 1024], new byte[1]]);
        await Reject(() => SignalingFraming.ReceiveAsync(large, default));
        foreach (var text in new[] { "{\"v\":1,\"v\":1,\"type\":\"ping\"}", "{\"type\":\"ping\"}", "{\"v\":0,\"type\":\"ping\"}" })
            await Reject(() => Task.FromResult(SignalingFraming.Parse(Encoding.UTF8.GetBytes(text))));
    }),
    ("permissions deny before consent, stale grants and after termination", async () =>
    {
        var clock = new FakeTime(); var gate = new PermissionGate(clock);
        Check(!gate.Allows(Capability.Screen, 0), "pre-consent screen allowed");
        await Reject(() => Task.FromResult(gate.Grant("sid", "peer", Capability.Control, TimeSpan.FromMinutes(1))));
        var grant = gate.Grant("sid", "peer", Capability.Screen | Capability.Admin, TimeSpan.FromMinutes(1));
        Check(gate.Allows(Capability.Screen, grant.Generation), "screen denied");
        Check(!gate.Allows(Capability.Control, grant.Generation), "admin implied control");
        Check(!gate.Allows(Capability.File, grant.Generation), "admin implied file");
        await Reject(() => Task.FromResult(gate.Grant("sid", "other", Capability.Screen, TimeSpan.FromMinutes(1))));
        clock.Utc = clock.Utc.AddMinutes(1); Check(!gate.Allows(Capability.Screen, grant.Generation), "expired lease allowed");
        gate.End(); Check(!gate.Allows(Capability.Screen, grant.Generation), "ended grant allowed");
        await Reject(() => Task.FromResult(gate.Grant("sid", "peer", Capability.Screen, TimeSpan.FromMinutes(1))));
    }),
    ("invalid options fail before native resource allocation", async () =>
    {
        foreach (var value in new[] { 0, -1, 61, int.MaxValue }) await Reject(() => { new ScreenShareOptions { TargetFps=value }.Validate(1); return Task.CompletedTask; });
        await Reject(() => { new ScreenShareOptions { SignalingUri=new Uri("http://192.0.2.1:25001"), AllowLoopbackHttp=true }.Validate(1); return Task.CompletedTask; });
        new ScreenShareOptions { AllowLoopbackHttp=true }.Validate(1);
        await Reject(() => Task.FromResult(new FFmpegH264Encoder("libx264")));
    }),
    ("BGRA length, stride and rotations", async () =>
    {
        await Reject(() => { FrameLayout.Validate(new byte[15], 2, 2, 8); return Task.CompletedTask; });
        await Reject(() => { FrameLayout.Validate(new byte[16], 2, 2, 4); return Task.CompletedTask; });
        await Reject(() => { FrameLayout.ValidateDimensions(int.MaxValue, int.MaxValue); return Task.CompletedTask; });
        var source = new byte[2 * 3 * 4]; for (var i = 0; i < 6; i++) source[i*4]=(byte)(i+1);
        var expected = new Dictionary<int,byte[]> { [0]=[1,2,3,4,5,6], [90]=[5,3,1,6,4,2], [180]=[6,5,4,3,2,1], [270]=[2,4,6,1,3,5] };
        foreach (var (rotation, order) in expected) { var output=new byte[source.Length];FrameLayout.Rotate(source,output,2,3,rotation);Check(Enumerable.Range(0,6).All(i=>output[i*4]==order[i]), "rotation mapping incorrect"); }
    }),
    ("shared golden protocol fixtures", async () =>
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"signals.json")));
        foreach (var fixture in doc.RootElement.EnumerateArray())
        {
            var bytes=Encoding.UTF8.GetBytes(fixture.GetProperty("message").GetRawText());
            if (!fixture.GetProperty("valid").GetBoolean()) await Reject(()=>Task.FromResult(SignalingFraming.Parse(bytes)));
            else {var parsed=SignalingFraming.Parse(bytes);var encoded=JsonSerializer.SerializeToUtf8Bytes(parsed);var roundtrip=SignalingFraming.Parse(encoded);Check(parsed==roundtrip || parsed.Type==roundtrip.Type && parsed.SessionId==roundtrip.SessionId && parsed.Reason==roundtrip.Reason,"golden roundtrip changed");}
        }
    }),
    ("zero output and slow encoder never pass diagnostics", () =>
    {
        foreach(var result in new[]{new EncoderProbeResult(0,0,1000,true),new EncoderProbeResult(60,60,29.9,true),new EncoderProbeResult(60,1,100,true),new EncoderProbeResult(60,60,100,false),new EncoderProbeResult(60,60,double.NaN,true)})Check(!result.Passed,"false encoder success");
        Check(new EncoderProbeResult(60,60,30,true).Passed,"boundary failed");return Task.CompletedTask;
    }),
    ("probe SPS/PPS survives discarded first output", async () =>
    {
        var cache=new H264ParameterSets();byte[] parameters=[0,0,0,1,0x67,0xaa,0,0,0,1,0x68,0xbb];
        cache.Process(parameters);byte[] idr=[0,0,0,1,0x65,0xcc];var output=cache.Process(idr);
        Check(output.AsSpan().SequenceEqual(parameters.Concat(idr).ToArray()),"IDR lost SPS/PPS");
        Check(H264ParameterSets.ContainsIdr(output),"IDR detection failed");
        await Reject(()=>Task.FromResult(new H264ParameterSets().Process(idr)));
    }),
    ("DXGI cursor masks, clipping and transparent pixels", () =>
    {
        byte[] background=[10,20,30,255,40,50,60,255];
        CursorComposition.Draw(background,2,1,[0xff,0xff],1,2,1,1,0,0);
        Check(background[0]==245&&background[1]==235&&background[2]==225&&background[4]==40,"monochrome XOR failed");
        CursorComposition.Draw(background,2,1,[1,2,3,255],1,1,4,2,1,0);Check(background[4]==1&&background[5]==2&&background[6]==3,"color cursor failed");
        CursorComposition.Draw(background,2,1,[8,9,10,0],1,1,4,2,1,0);Check(background[4]==1,"transparent cursor changed frame");
        CursorComposition.Draw(background,2,1,[8,9,10,255],1,1,4,4,1,0);Check(background[4]==9&&background[5]==11&&background[6]==9,"masked XOR failed");
        CursorComposition.Draw(background,2,1,[8,9,10,255],1,1,4,2,-1,0);Check(background[4]==9,"clipped cursor changed frame");return Task.CompletedTask;
    }),
    ("input binding and normalized coordinate validation", async () =>
    {
        var input=new InputMsg {T="mouse",A="down",Btn="left",SessionId="sid",Id="i1",Generation=1,PermissionGeneration=1,Sequence=1,TopologyGeneration=1,MonitorIdentity="DISPLAY1",X=.5,Y=.7};
        Check(ProtocolJson.ParseInput(JsonSerializer.SerializeToUtf8Bytes(input)).Btn=="left","input roundtrip");
        foreach(var invalid in new[]{input with{X=1.1},input with{Sequence=0},input with{TopologyGeneration=0},input with{Btn="unknown"},input with{SessionId=""}})
            await Reject(()=>Task.FromResult(ProtocolJson.ParseInput(JsonSerializer.SerializeToUtf8Bytes(invalid))));
    }),
    ("file chunk RFC UUID endian and tamper rejection", async () =>
    {
        var id=Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");var header=new FileChunkHeader(id,42,3);var encoded=header.Encode([1,2,3]);
        Check(encoded[8]==0&&encoded[9]==0x11&&encoded[10]==0x22&&encoded[11]==0x33,"GUID byte order drift");
        Check(FileChunkHeader.Decode(encoded)==header,"header roundtrip");
        var bad=encoded.ToArray();bad[6]=1;await Reject(()=>Task.FromResult(FileChunkHeader.Decode(bad)));
        await Reject(()=>Task.FromResult(FileChunkHeader.Decode(encoded[..^1])));
    }),
    ("Host termination is idempotent, observer failures cannot reopen it", async () =>
    {
        await using var session=new ScreenShareSession(new ScreenShareOptions());
        session.StateChanged+=_=>throw new Exception("observer failed");
        session.EndSession("first",false);session.EndSession("late",true);
        Check((await session.Completion)==new SessionOutcome("first",false),"terminal reason changed");
        await Reject(()=>session.StartAsync());
    }),
};
var failures = 0;
foreach (var (name, run) in tests) try { await run(); Console.WriteLine($"PASS {name}"); } catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {name}: {ex.Message}"); }
Console.WriteLine(JsonSerializer.Serialize(new { total=tests.Length,failed=failures,passed=tests.Length-failures }));
return failures==0?0:1;

static void Check(bool condition,string reason) {if(!condition) throw new Exception(reason);}
static async Task Reject(Func<Task> action) {try {await action();}catch {return;}throw new Exception("expected rejection did not occur");}

sealed class FakeTime : TimeProvider
{ public DateTimeOffset Utc = DateTimeOffset.UnixEpoch; public override DateTimeOffset GetUtcNow()=>Utc; }
sealed class FakeEncoderFactory(string? winner) : IEncoderCandidateFactory
{
    public List<FakeEncoder> Candidates { get; } = [];
    public void Prepare(Microsoft.Extensions.Logging.ILogger log) { }
    public IEncoderCandidate Create(string name, Dictionary<string, string> options)
    {
        if (Candidates.Any(x => !x.Disposed)) throw new Exception("previous native candidate is still alive");
        var candidate = new FakeEncoder(name == winner, name == "h264_nvenc");
        Candidates.Add(candidate); return candidate;
    }
}
sealed class FakeEncoder(bool success, bool failConfigure) : IEncoderCandidate
{
    public bool Disposed; public int Forced;
    public void Configure(int width, int height, int fps, int bitrateKbps)
    { if (failConfigure) throw new InvalidOperationException("injected device init failure"); }
    public byte[]? Encode(int width, int height, byte[] bgra) => success ? [0,0,0,1,0x67,1,0,0,0,1,0x68,2,0,0,0,1,0x65,3] : null;
    public void ForceKeyFrame() => Forced++;
    public void Dispose() => Disposed = true;
}
sealed class FragmentSocket(byte[][] chunks, WebSocketMessageType type=WebSocketMessageType.Text) : WebSocket
{
    private int _chunk, _offset;
    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override WebSocketState State => WebSocketState.Open;
    public override string? SubProtocol => null;
    public override void Abort() { }
    public override Task CloseAsync(WebSocketCloseStatus s,string? d,CancellationToken ct)=>Task.CompletedTask;
    public override Task CloseOutputAsync(WebSocketCloseStatus s,string? d,CancellationToken ct)=>Task.CompletedTask;
    public override void Dispose() { }
    public override Task SendAsync(ArraySegment<byte> b,WebSocketMessageType t,bool end,CancellationToken ct)=>Task.CompletedTask;
    public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var count=Math.Min(buffer.Count,chunks[_chunk].Length-_offset);
        chunks[_chunk].AsSpan(_offset,count).CopyTo(buffer.AsSpan()); _offset+=count;
        if(_offset==chunks[_chunk].Length){_chunk++;_offset=0;}
        return Task.FromResult(new WebSocketReceiveResult(count,type,_chunk==chunks.Length));
    }
}
