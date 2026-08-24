using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SIPSorceryMedia.FFmpeg;

namespace RemoteAssist.Host.Core.Media;

/// <summary>
/// 동봉한 FFmpeg 공유 라이브러리를 한 번만 로드한다.
///
/// <para>
/// <b>왜 경로를 직접 지정하는가.</b> <c>FFmpegInit.Initialise</c> 는 인자가 없으면 PATH에서
/// 라이브러리를 찾는다. 그러면 사용자 머신에 설치된 다른 FFmpeg를 잡을 수 있는데, 그건
/// 두 가지 이유로 위험하다.
/// </para>
/// <list type="number">
///   <item>ABI 불일치 — 이 패키지는 <b>FFmpeg 8.1 ABI</b> 를 타깃한다. 버전이 다르면 PInvoke가 깨진다.</item>
///   <item><b>라이선스 오염</b> — 배포용 FFmpeg 빌드는 대개 <c>--enable-gpl</c> + libx264 다.
///         그런 빌드를 쓰면 제품 전체가 GPL 조건에 걸린다. 우리는 LGPL 빌드만 쓴다.</item>
/// </list>
/// <para>
/// 그래서 <c>third_party/ffmpeg/bin</c> 의 동봉본만 로드하고, 못 찾으면 조용히 PATH로
/// 넘어가지 않고 <b>실패시킨다</b>. 자세한 조건은 <c>third_party/ffmpeg/README.md</c> 참고.
/// </para>
/// </summary>
public static class FFmpegLoader
{
    private static readonly Lock Gate = new();
    private static bool _initialized;

    /// <summary>실제로 로드한 경로. 진단·로그용.</summary>
    public static string? LoadedFrom { get; private set; }

    /// <summary>
    /// FFmpeg를 로드한다. 여러 번 불러도 한 번만 수행한다.
    /// </summary>
    /// <param name="libraryPath">
    /// 공유 라이브러리 디렉터리. null이면 실행 파일 옆과 저장소의 third_party 경로를 차례로 찾는다.
    /// </param>
    public static void Initialise(string? libraryPath = null, ILogger? log = null)
    {
        log ??= NullLogger.Instance;

        lock (Gate)
        {
            if (_initialized) return;

            var path = libraryPath ?? Locate()
                ?? throw new DirectoryNotFoundException(
                    "동봉한 FFmpeg를 찾을 수 없습니다. third_party/ffmpeg/bin 에 " +
                    "FFmpeg 8.1 LGPL shared 빌드를 배치하세요 (third_party/ffmpeg/README.md 참고). " +
                    "시스템에 설치된 FFmpeg는 ABI·라이선스 문제로 쓰지 않습니다.");

            if (!Directory.Exists(path))
            {
                throw new DirectoryNotFoundException($"FFmpeg 경로가 없습니다: {path}");
            }

            FFmpegInit.Initialise(FfmpegLogLevelEnum.AV_LOG_ERROR, path, log);

            _initialized = true;
            LoadedFrom = path;
            log.LogInformation("FFmpeg 로드: {Path}", path);
        }
    }

    /// <summary>배포본(실행 파일 옆)과 개발 트리(저장소 루트)를 차례로 본다.</summary>
    private static string? Locate()
    {
        var candidates = new List<string>();

        var baseDir = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(baseDir, "ffmpeg"));
        candidates.Add(Path.Combine(baseDir, "runtimes", "win-x64", "native"));

        // 개발 중에는 bin/Debug/... 깊이에서 저장소 루트로 거슬러 올라간다.
        var dir = new DirectoryInfo(baseDir);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            candidates.Add(Path.Combine(dir.FullName, "third_party", "ffmpeg", "bin"));
        }

        return candidates.FirstOrDefault(c => Directory.Exists(c) && Directory.EnumerateFiles(c, "avcodec-*.dll").Any());
    }
}
