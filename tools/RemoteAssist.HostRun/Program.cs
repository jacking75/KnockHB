using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RemoteAssist.Host.Core;
using RemoteAssist.Host.Core.Capture;
using RemoteAssist.Host.Core.Media;
using RemoteAssist.Host.Core.Security;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.FFmpeg;

SetProcessDpiAwarenessContext(-4);
using var loggerFactory = LoggerFactory.Create(builder => builder.AddSimpleConsole(options => { options.SingleLine = true; options.TimestampFormat = "HH:mm:ss "; }).SetMinimumLevel(LogLevel.Information));
var log = loggerFactory.CreateLogger("HostRun");
try
{
    var parsed = Parse(args);
    var options = new ScreenShareOptions
    {
        SignalingUri = new Uri(parsed.GetValueOrDefault("--signaling") ?? "http://localhost:25001"),
        AllowLoopbackHttp = parsed.ContainsKey("--dev-loopback"),
        TargetFps = Number(parsed,"--fps",30), MonitorIndex = Number(parsed,"--monitor",0),
        BitrateKbps = Number(parsed,"--bitrate",4000), PreferredEncoder = parsed.GetValueOrDefault("--encoder")
    };
    // Diagnostics have no network endpoint or screen capture, but still validate encoding values.
    var validation = parsed.ContainsKey("--encoder-check") ? options with { AllowLoopbackHttp=true, MonitorIndex=0 } : options;
    validation.Validate(parsed.ContainsKey("--encoder-check") ? 1 : Monitors.Enumerate().Count);
    if (parsed.ContainsKey("--encoder-check")) return EncoderCheck(options.PreferredEncoder, parsed.GetValueOrDefault("--out"), log);

    await using var session = new ScreenShareSession(options, loggerFactory);
    // 진단 도구는 화면만 보낸다. 입력·관리자·파일·Runbook은 서비스 설치본에서만 동작한다.
    session.SupporterJoined = supporter => Console.WriteLine($"지원자 접속: {supporter.DisplayName}");
    session.CreateInputBackend = _ => new IgnoredInput();
    session.AdminCommand = command => { if (command.T == "admin-request") session.DenyAdmin(command.Id); };
    session.ChatReceived = chat => Console.WriteLine($"지원자 채팅: {chat.Text}");
    session.ClipboardOperation = (request, _) => Task.FromResult(request with { T = "clipboard-rejected", Text = null, Hash = null, Code = "unavailable" });
    session.IncomingFileFolder = (_, _) => Task.FromResult<string?>(null);
    session.AuditFile = (_, _) => Task.CompletedTask;
    session.RunbookOperation = (_, _) => Task.FromResult(new RemoteAssist.Protocol.RunbookResult("denied", null, "", "HostRun 진단 도구는 Runbook을 실행하지 않는다.", ""));
    session.StateChanged += state => Console.WriteLine($"상태: {state}");
    var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; session.EndSession(); stop.TrySetResult(); };
    Console.CancelKeyPress += cancel;
    try
    {
        var credentials = await session.StartAsync();
        // Pairing secrets are local presentation only; never send them to ILogger.
        Console.WriteLine($"지원 ID: {credentials.DisplayCode}\nCtrl+C로 즉시 종료한다.");
        var last = Stopwatch.StartNew();
        session.FrameSent += (count, size) => { if(last.ElapsedMilliseconds>=1000){Console.WriteLine($"전송 {count}프레임, 최근 {size}바이트, {session.EncoderName}");last.Restart();} };
        await Task.WhenAny(stop.Task,session.Completion);
        session.EndSession();
        var result = await session.Completion;
        return result.Failed ? 1 : 0;
    }
    finally { Console.CancelKeyPress -= cancel; }
}
catch(Exception ex) { log.LogError("실행 실패: {Reason}",ex.Message);return 1; }

static int EncoderCheck(string? forced,string? output,ILogger log)
{
    FFmpegLoader.Initialise(log:log);
    var directory=Path.GetFullPath(output??Path.Combine(".artifacts","encoders",DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ")));
    if(Directory.Exists(directory)) throw new IOException("기존 검사 결과를 덮어쓰지 않는다. 새 --out 경로가 필요하다.");
    Directory.CreateDirectory(directory);
    var reports=new List<object>();var successes=0;var failures=0;
    foreach(var name in forced is null ? FFmpegH264Encoder.AllowedEncoders : new[]{forced})
    {
        try
        {
            using var encoder=new FFmpegH264Encoder(name);
            try {encoder.Configure(1920,1080,30,4000);} catch(Exception ex) {reports.Add(new{name,status="UNSUPPORTED",reason=ex.Message});Console.WriteLine($"{name}: UNSUPPORTED");if(forced is not null)failures++;continue;}
            using var decoder=new FFmpegVideoEncoder();
            var frame=new byte[1920*1080*4];var path=Path.Combine(directory,name+".h264");
            using var bitstream=File.Create(path);
            var clock=Stopwatch.StartNew();var emitted=0;var decoded=0;var recoveryDecoded=false;var bytes=0L;
            var packets=new List<byte[]>();
            for(var i=0;i<60;i++)
            {
                for(var y=0;y<1080;y+=4) Array.Fill(frame,(byte)((y+i*8)%256),y*1920*4,1920*4);
                var encoded=encoder.Encode(frame,i==0||i==30);
                if(encoded.IsEmpty)continue;
                emitted++;bytes+=encoded.Length;bitstream.Write(encoded.Span);
                packets.Add(encoded.ToArray());
                // Decode cost is deliberately excluded from encoder throughput.
            }
            clock.Stop();bitstream.Flush();bitstream.Dispose();
            var throughput=emitted/clock.Elapsed.TotalSeconds;
            foreach(var packet in packets) decoded+=decoder.DecodeVideo(packet,VideoPixelFormatsEnum.Bgra,VideoCodecsEnum.H264).Count();
            // A fresh decoder must recover from a requested keyframe without earlier history.
            using var freshDecoder=new FFmpegVideoEncoder();
            var key=encoder.Encode(frame,true);
            recoveryDecoded=!key.IsEmpty&&freshDecoder.DecodeVideo(key.ToArray(),VideoPixelFormatsEnum.Bgra,VideoCodecsEnum.H264).Any();
            var pass=new EncoderProbeResult(emitted,decoded,throughput,recoveryDecoded).Passed;
            if(pass)successes++;else failures++;
            reports.Add(new{name,status=pass?"PASS":"FAIL",emitted,decoded,throughput,bytes,recoveryDecoded});
            Console.WriteLine($"{name}: {(pass?"PASS":"FAIL")}, 출력 {emitted}/60, {throughput:F1}fps, 디코딩 {decoded}, 강제 키프레임 복구 {recoveryDecoded}");
        }
        catch(Exception ex){failures++;reports.Add(new{name,status="FAIL",reason=ex.Message});Console.WriteLine($"{name}: FAIL {ex.Message}");}
    }
    File.WriteAllText(Path.Combine(directory,"report.json"),JsonSerializer.Serialize(new{machine=Environment.MachineName,utc=DateTimeOffset.UtcNow,reports},new JsonSerializerOptions{WriteIndented=true}));
    Console.WriteLine($"검사 결과: {directory}");
    return failures==0&&successes>0?0:2;
}
static Dictionary<string,string?> Parse(string[] args)
{
    var result=new Dictionary<string,string?>();
    for(var i=0;i<args.Length;i++)
    {
        var name=args[i];if(!result.TryAdd(name,null))throw new ArgumentException("중복 CLI 옵션이다: "+name);
        if(name is "--encoder-check" or "--dev-loopback")continue;
        if(name is not ("--encoder" or "--fps" or "--monitor" or "--bitrate" or "--signaling" or "--out"))throw new ArgumentException("알 수 없는 CLI 옵션이다: "+name);
        if(++i>=args.Length||args[i].StartsWith("--",StringComparison.Ordinal))throw new ArgumentException("CLI 값이 필요하다: "+name);
        result[name]=args[i];
    }
    return result;
}
static int Number(Dictionary<string,string?> options,string name,int fallback) => !options.TryGetValue(name,out var value)?fallback:int.TryParse(value,out var number)?number:throw new ArgumentException("정수 CLI 값이 필요하다: "+name);
[DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(nint value);

sealed class IgnoredInput : RemoteAssist.Host.Core.Input.IInputBackend
{
    public int Send(IReadOnlyList<RemoteAssist.Host.Core.Input.InputStroke> strokes) => strokes.Count;
}
