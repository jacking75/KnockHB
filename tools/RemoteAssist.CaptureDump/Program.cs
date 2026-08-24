// 화면 캡처 확인 도구 (T1.2 DoD: 캡처한 프레임을 PNG로 덤프해 눈으로 확인).
//
// 사용법:
//   dotnet run --project tools/RemoteAssist.CaptureDump -- --list
//   dotnet run --project tools/RemoteAssist.CaptureDump -- --monitor 0 --frames 5 --out ./capture
//   dotnet run --project tools/RemoteAssist.CaptureDump -- --backend dxgi     (폴백 경로 확인용)
//
// 개발용 도구다. 배포본에 들어가지 않는다.

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RemoteAssist.Host.Core.Capture;

// 캡처는 물리 픽셀 기준이다. 프로세스가 DPI를 모르면 좌표·크기 보고가 어긋난다.
const nint DpiAwarenessContextPerMonitorAwareV2 = -4;
SetProcessDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2);

var opts = ParseArgs(args);

if (opts.List)
{
    Console.WriteLine($"WGC 지원: {(WgcScreenCapture.IsSupported() ? "예" : "아니오")}");
    Console.WriteLine("연결된 모니터:");
    foreach (var m in Monitors.Enumerate())
    {
        Console.WriteLine("  " + m);
    }
    return 0;
}

Directory.CreateDirectory(opts.OutDir);

IScreenCapture? capture = null;
try
{
    capture = CreateCapture(opts);

    Console.WriteLine($"캡처 대상: {capture.Monitor}");
    Console.WriteLine($"출력 경로: {Path.GetFullPath(opts.OutDir)}");
    Console.WriteLine($"{opts.Frames}장을 캡처한다. 화면이 완전히 정적이면 프레임이 안 나올 수 있다.");
    Console.WriteLine();

    var saved = 0;
    var attempts = 0;
    var sw = Stopwatch.StartNew();

    while (saved < opts.Frames && attempts < opts.Frames * 100)
    {
        attempts++;

        if (!capture.TryGetFrame(TimeSpan.FromMilliseconds(200), out var frame))
        {
            continue; // 화면이 정적이다. 정상.
        }

        var path = Path.Combine(opts.OutDir, $"frame-{saved:D3}.png");
        SavePng(frame, path);
        saved++;

        Console.WriteLine($"  {Path.GetFileName(path)}  {frame.Width}x{frame.Height}  " +
                          $"stride {frame.Stride}  {DescribeDirty(frame)}");
    }

    sw.Stop();
    Console.WriteLine();
    Console.WriteLine($"완료: {saved}장 저장, 시도 {attempts}회, {sw.Elapsed.TotalSeconds:F1}초 " +
                      $"({saved / Math.Max(sw.Elapsed.TotalSeconds, 0.001):F1} fps)");

    return saved == 0 ? 2 : 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"캡처 실패: {ex.Message.Split('\n')[0]}");
    Console.Error.WriteLine();
    Console.Error.WriteLine("흔한 원인:");
    Console.Error.WriteLine("  - DXGI: 하이브리드 GPU 노트북에서 Desktop Duplication 미지원 (--backend wgc 사용)");
    Console.Error.WriteLine("  - 원격 데스크톱 세션");
    Console.Error.WriteLine("  - 보호 콘텐츠가 떠 있는 상태");
    return 1;
}
finally
{
    capture?.Dispose();
}

// 기본은 WGC. DXGI는 명시했을 때만 쓴다 — 설계서 5장의 우선순위와 같다.
static IScreenCapture CreateCapture(Options opts)
{
    switch (opts.Backend)
    {
        case "dxgi":
            Console.WriteLine("백엔드: DXGI Desktop Duplication (폴백 경로)");
            return new DxgiScreenCapture(opts.Monitor);

        case "wgc":
            Console.WriteLine("백엔드: Windows Graphics Capture");
            return new WgcScreenCapture(opts.Monitor);

        default:
            if (WgcScreenCapture.IsSupported())
            {
                Console.WriteLine("백엔드: Windows Graphics Capture (자동 선택)");
                return new WgcScreenCapture(opts.Monitor);
            }

            Console.WriteLine("백엔드: DXGI Desktop Duplication (WGC 미지원이라 폴백)");
            return new DxgiScreenCapture(opts.Monitor);
    }
}

static string DescribeDirty(CapturedFrame f) => f.DirtyRectCount switch
{
    < 0 => "변경영역 정보 없음 (WGC는 제공하지 않는다)",
    0 => "변경영역 없음 (커서만 이동)",
    _ => $"변경영역 {f.DirtyRectCount}개, 경계 {f.DirtyBounds.Width}x{f.DirtyBounds.Height} " +
         $"(화면의 {Percent(f.DirtyBounds.Area, (long)f.Width * f.Height)})",
};

// PNG 저장. BGRA 그대로 WPF 인코더에 넘긴다.
static void SavePng(CapturedFrame frame, string path)
{
    var bmp = BitmapSource.Create(
        frame.Width, frame.Height, 96, 96,
        PixelFormats.Bgra32, null,
        frame.Bgra.ToArray(), frame.Stride);
    bmp.Freeze();

    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bmp));

    using var fs = File.Create(path);
    encoder.Save(fs);
}

static string Percent(long part, long whole) =>
    whole == 0 ? "0%" : $"{100.0 * part / whole:F1}%";

static Options ParseArgs(string[] args)
{
    var o = new Options();
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--list" or "-l":
                o.List = true;
                break;
            case "--monitor" or "-m" when i + 1 < args.Length:
                o.Monitor = int.Parse(args[++i]);
                break;
            case "--frames" or "-n" when i + 1 < args.Length:
                o.Frames = int.Parse(args[++i]);
                break;
            case "--out" or "-o" when i + 1 < args.Length:
                o.OutDir = args[++i];
                break;
            case "--backend" or "-b" when i + 1 < args.Length:
                o.Backend = args[++i].ToLowerInvariant();
                break;
        }
    }
    return o;
}

[DllImport("user32.dll")]
static extern bool SetProcessDpiAwarenessContext(nint value);

internal sealed class Options
{
    public bool List { get; set; }
    public int Monitor { get; set; }
    public int Frames { get; set; } = 3;
    public string OutDir { get; set; } = "capture-dump";
    public string Backend { get; set; } = "auto";
}
