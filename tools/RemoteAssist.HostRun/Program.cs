// Host 파이프라인 확인 도구 (T1.4).
//
// 캡처 → H.264 인코딩 → WebRTC 송신을 실제로 돌린다.
// 시그널링 서버가 먼저 떠 있어야 한다:
//   cd server/signaling && go run .
//
// 사용법:
//   dotnet run --project tools/RemoteAssist.HostRun
//   dotnet run --project tools/RemoteAssist.HostRun -- --encoder libopenh264 --fps 15
//   dotnet run --project tools/RemoteAssist.HostRun -- --encoder-check     (인코더 선택만 확인)
//
// 개발용 도구다. 동의 게이트(T3.2)가 없으므로 배포본에 들어가지 않는다.

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using RemoteAssist.Host.Core;
using RemoteAssist.Host.Core.Capture;
using RemoteAssist.Host.Core.Media;

const nint DpiAwarenessContextPerMonitorAwareV2 = -4;
SetProcessDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2);

using var loggerFactory = LoggerFactory.Create(b => b
    .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
    .SetMinimumLevel(LogLevel.Information));

var log = loggerFactory.CreateLogger("HostRun");

var encoderName = GetArg(args, "--encoder");
var fps = int.TryParse(GetArg(args, "--fps"), out var f) ? f : 30;
var monitor = int.TryParse(GetArg(args, "--monitor"), out var m) ? m : 0;
var bitrate = int.TryParse(GetArg(args, "--bitrate"), out var b) ? b : 4000;
var signaling = GetArg(args, "--signaling") ?? "http://localhost:25001";

// --encoder-check: 인코더 선택 로직만 확인한다. T1.3b의 핵심 검증이다.
if (args.Contains("--encoder-check"))
{
    return EncoderCheck(loggerFactory, log);
}

log.LogInformation("모니터: {Monitors}", string.Join(" | ", Monitors.Enumerate()));

var session = new ScreenShareSession(new ScreenShareOptions
{
    SignalingUri = new Uri(signaling),
    MonitorIndex = monitor,
    TargetFps = fps,
    BitrateKbps = bitrate,
    PreferredEncoder = encoderName,
}, loggerFactory);

await using (session)
{
    var lastReport = 0L;
    session.FrameSent += (count, bytes) =>
    {
        // 1초에 한 번만 보고한다.
        var now = Environment.TickCount64;
        if (now - lastReport < 1000) return;
        lastReport = now;
        Console.WriteLine($"  전송 {count}프레임 · 마지막 {bytes / 1024.0:F1}KB · 인코더 {session.EncoderName}");
    };

    try
    {
        var creds = await session.StartAsync();

        Console.WriteLine();
        Console.WriteLine("  ┌──────────────────────────────────────┐");
        Console.WriteLine($"  │  세션 코드   {creds.DisplayCode,-24}│");
        Console.WriteLine($"  │  PIN         {creds.Pin,-24}│");
        Console.WriteLine("  └──────────────────────────────────────┘");
        Console.WriteLine();
        Console.WriteLine("  웹 뷰어(web/viewer/index.html)에서 위 코드와 PIN을 입력하세요.");
        Console.WriteLine("  Ctrl+C 로 종료합니다.");
        Console.WriteLine();

        var stop = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.TrySetResult(); };
        await stop.Task;
    }
    catch (HttpRequestException ex)
    {
        log.LogError("시그널링 서버에 연결할 수 없습니다 ({Uri}): {Message}", signaling, ex.Message);
        log.LogError("먼저 서버를 띄우세요:  cd server/signaling && go run .");
        return 1;
    }
    catch (Exception ex)
    {
        log.LogError(ex, "세션 실패");
        return 1;
    }
}

return 0;

// 인코더 선택·폴백 검증 (T1.3b DoD).
static int EncoderCheck(ILoggerFactory factory, ILogger log)
{
    try
    {
        FFmpegLoader.Initialise(log: log);
        Console.WriteLine($"FFmpeg 로드 경로: {FFmpegLoader.LoadedFrom}");
    }
    catch (Exception ex)
    {
        log.LogError("FFmpeg 로드 실패: {Message}", ex.Message);
        return 1;
    }

    Console.WriteLine();
    Console.WriteLine("인코더별 사용 가능 여부 (우선순위 순):");

    string[] candidates = ["h264_nvenc", "h264_qsv", "h264_amf", "h264_mf", "libopenh264", "libx264"];
    string? autoSelected = null;

    foreach (var name in candidates)
    {
        try
        {
            using var enc = new FFmpegH264Encoder(name, factory.CreateLogger<FFmpegH264Encoder>());
            var ok = enc.EncoderName == name;
            Console.WriteLine($"  {name,-14} {(ok ? "사용 가능" : $"불가 → {enc.EncoderName} 로 폴백")}" +
                              $"{(name == "libx264" ? "   ← GPL이므로 후보 목록에 없다(정상)" : "")}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  {name,-14} 실패: {ex.Message.Split('\n')[0]}");
        }
    }

    Console.WriteLine();
    using (var auto = new FFmpegH264Encoder(null, factory.CreateLogger<FFmpegH264Encoder>()))
    {
        autoSelected = auto.EncoderName;
        Console.WriteLine($"자동 선택: {autoSelected} ({(auto.IsHardware ? "하드웨어" : "소프트웨어")})");

        const int w = 1920, h = 1080, count = 60;
        auto.Configure(w, h, 30, 4000);

        // 매 프레임 내용을 바꿔 가며 인코딩한다. 같은 그림만 넣으면
        // 인코더가 아무것도 내보내지 않아 처리량을 잴 수 없다.
        var frame = new byte[w * h * 4];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long total = 0;
        var emitted = 0;
        var firstOutputAt = -1;

        for (var i = 0; i < count; i++)
        {
            // 가로 줄무늬를 프레임마다 밀어 실제 변화를 만든다.
            for (var y = 0; y < h; y += 4)
            {
                var v = (byte)((y + i * 8) % 256);
                Array.Fill(frame, v, y * w * 4, w * 4);
            }

            var encoded = auto.Encode(frame, forceKeyframe: i == 0);
            if (!encoded.IsEmpty)
            {
                if (firstOutputAt < 0) firstOutputAt = i;
                emitted++;
                total += encoded.Length;
            }
        }

        sw.Stop();
        var fps = count / sw.Elapsed.TotalSeconds;
        Console.WriteLine($"1080p x{count} 인코딩: {sw.Elapsed.TotalMilliseconds:F0}ms → {fps:F1} fps");
        Console.WriteLine($"  출력 프레임 {emitted}/{count}, 첫 출력 = {firstOutputAt}번째, 평균 {(emitted > 0 ? total / emitted : 0):N0} 바이트");

        if (fps < 30) Console.WriteLine("  ⚠️ 30fps 미만 — T1.3b DoD 미달");
        else Console.WriteLine("  ✅ 1080p 30fps 이상 (T1.3b DoD)");
    }

    if (autoSelected == "libx264")
    {
        log.LogError("libx264가 선택됐다. GPL 오염이므로 즉시 조사해야 한다.");
        return 2;
    }

    Console.WriteLine();
    Console.WriteLine("✅ 선택된 인코더가 libx264가 아니다 (T1.3b DoD).");
    return 0;
}

static string? GetArg(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

[DllImport("user32.dll")]
static extern bool SetProcessDpiAwarenessContext(nint value);
