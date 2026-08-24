using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.FFmpeg;

namespace RemoteAssist.Host.Core.Media;

/// <summary>
/// FFmpeg 기반 H.264 인코더. 화면 공유의 실사용 코덱이다.
///
/// <para>
/// <b>인코더를 명시적으로 고르는 것이 이 클래스의 핵심이다</b>(T1.3b). 코덱 ID만 주고
/// 기본값에 맡기면 FFmpeg가 알아서 고르는데, 그건 두 가지 문제를 동시에 일으킨다.
/// </para>
/// <list type="number">
///   <item><b>성능</b> — SW 인코더로 떨어지면 1080p를 실시간으로 못 따라간다.</item>
///   <item><b>라이선스</b> — 기본값이 <c>libx264</c>(GPL)일 수 있다. 그런 빌드를 동봉하면
///         제품 전체가 GPL 조건에 걸린다.</item>
/// </list>
/// <para>
/// 그래서 HW 인코더를 우선순위대로 시도하고, 전부 없으면 <c>libopenh264</c>(BSD)로만
/// 폴백한다. <see cref="EncoderName"/> 을 로그·뷰어 상태바에 노출해 현장에서 확인할 수 있게 한다.
/// </para>
/// </summary>
public sealed class FFmpegH264Encoder : IVideoEncoder
{
    /// <summary>
    /// 시도 순서. 앞쪽이 우선이다.
    ///
    /// nvenc(NVIDIA) → qsv(Intel) → amf(AMD) → mf(Media Foundation 범용) → openh264(SW).
    /// <b>libx264는 목록에 없다. 넣지 말 것</b> — GPL이다.
    /// </summary>
    private static readonly (string Name, bool Hardware)[] Candidates =
    [
        ("h264_nvenc", true),
        ("h264_qsv", true),
        ("h264_amf", true),
        ("h264_mf", true),
        ("libopenh264", false),
    ];

    private readonly ILogger _log;
    private readonly FFmpegVideoEncoder _encoder = null!;
    private bool _disposed;

    public string EncoderName { get; }
    public bool IsHardware { get; }

    private int _width;
    private int _height;
    private int _fps;

    /// <param name="preferred">특정 인코더를 강제할 때 지정. null이면 우선순위대로 자동 선택.</param>
    public FFmpegH264Encoder(string? preferred = null, ILogger<FFmpegH264Encoder>? log = null)
    {
        _log = (ILogger?)log ?? NullLogger.Instance;
        FFmpegLoader.Initialise(log: _log);

        var order = preferred is null
            ? Candidates
            : [.. Candidates.Where(c => c.Name == preferred), .. Candidates.Where(c => c.Name != preferred)];

        foreach (var (name, hardware) in order)
        {
            // 옵션은 생성자에도 넘긴다. SetCodec에만 주면 avcodec_open2까지 닿지 않아
            // nvenc의 delay 기본값(INT_MAX)이 그대로 남고 십수 프레임이 밀린다.
            var options = Options(name);
            var candidate = new FFmpegVideoEncoder(options, AVHWDeviceType.AV_HWDEVICE_TYPE_NONE);

            if (!TrySelect(candidate, name, options))
            {
                candidate.Dispose();
                _log.LogDebug("인코더 사용 불가: {Encoder}", name);
                continue;
            }

            _encoder = candidate;
            EncoderName = name;
            IsHardware = hardware;
            _log.LogInformation("H.264 인코더 선택: {Encoder} ({Kind})", name, hardware ? "하드웨어" : "소프트웨어");
            return;
        }

        throw new InvalidOperationException(
            "쓸 수 있는 H.264 인코더가 없습니다. 동봉한 FFmpeg 빌드에 " +
            "h264_nvenc/h264_qsv/h264_amf/h264_mf/libopenh264 중 하나가 있어야 합니다.");
    }

    private bool TrySelect(FFmpegVideoEncoder encoder, string codecName, Dictionary<string, string> options)
    {
        try
        {
            return encoder.SetCodec(AVCodecID.AV_CODEC_ID_H264, codecName, options);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "인코더 {Encoder} 선택 실패", codecName);
            return false;
        }
    }

    /// <summary>
    /// 화면 공유에 맞춘 인코더 옵션.
    ///
    /// 지연을 최소화하고(프레임 재정렬·B프레임 없음), 정적 화면이 많은 특성상
    /// 키프레임을 자주 넣지 않는다. 키프레임이 잦으면 아무 변화가 없어도 대역폭을 먹는다.
    /// </summary>
    private static Dictionary<string, string> Options(string codecName) => codecName switch
    {
        "h264_nvenc" => new()
        {
            ["preset"] = "p1",          // 가장 빠른 프리셋
            ["tune"] = "ull",           // ultra low latency
            ["rc"] = "cbr",
            ["delay"] = "0",            // 출력 지연 0 — 넣지 않으면 십수 프레임을 물고 있는다
            ["zerolatency"] = "1",
        },
        "h264_qsv" => new()
        {
            ["preset"] = "veryfast",
            ["low_power"] = "1",
            ["async_depth"] = "1",
        },
        "h264_amf" => new()
        {
            ["usage"] = "ultralowlatency",
            ["quality"] = "speed",
        },
        "h264_mf" => new()
        {
            ["hw_encoding"] = "true",
            ["low_latency"] = "true",
        },
        "libopenh264" => new()
        {
            ["allow_skip_frames"] = "1",
        },
        _ => [],
    };

    public void Configure(int width, int height, int fps, int bitrateKbps)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _width = width;
        _height = height;
        _fps = fps;

        // 비트레이트를 먼저 걸고 인코더를 연다. 순서가 바뀌면 무시된다.
        _encoder.SetBitrate(bitrateKbps * 1000L, null, null, null);
        _encoder.InitialiseEncoder(AVCodecID.AV_CODEC_ID_H264, width, height, fps);

        _log.LogInformation("인코더 설정: {W}x{H} @{Fps}fps, {Kbps}kbps ({Encoder})",
            width, height, fps, bitrateKbps, EncoderName);
    }

    public ReadOnlyMemory<byte> Encode(ReadOnlyMemory<byte> bgraFrame, bool forceKeyframe)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_width == 0)
        {
            throw new InvalidOperationException($"{nameof(Configure)} 를 먼저 호출해야 합니다.");
        }

        // BGRA → I420 변환은 SIPSorceryMedia.FFmpeg 안에서 swscale이 처리한다.
        // 이 CPU 왕복이 지금 파이프라인의 가장 큰 비용이다. GPU 텍스처를 인코더에
        // 직접 넘기는 zero-copy 경로는 T6.3에서 다룬다.
        var encoded = _encoder.EncodeVideo(
            _width, _height,
            bgraFrame.ToArray(),
            VideoPixelFormatsEnum.Bgra,
            VideoCodecsEnum.H264);

        return encoded ?? ReadOnlyMemory<byte>.Empty;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _encoder.Dispose();
    }
}
