using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RemoteAssist.Host.Core.Media;

/// <summary>후보별 실제 초기화·시험 출력을 통과한 인코더만 선택한다. 호출은 소유 스레드 하나로 제한한다.</summary>
public sealed class FFmpegH264Encoder : IVideoEncoder
{
    public static IReadOnlyList<string> AllowedEncoders { get; } = Array.AsReadOnly(new[] { "h264_nvenc", "h264_qsv", "h264_amf", "h264_mf", "libopenh264" });
    private readonly string? _preferred;
    private readonly ILogger _log;
    private readonly IEncoderCandidateFactory _factory;
    private IEncoderCandidate? _encoder;
    private int _width, _height;
    private bool _disposed;
    private byte[] _inputBuffer=[];
    private H264ParameterSets _parameterSets = new();
    public string EncoderName { get; private set; } = "(미선택)";
    public int MaximumLevel {get;set;}=52;
    public bool IsHardware => EncoderName != "libopenh264" && _encoder is not null;

    public FFmpegH264Encoder(string? preferred = null, ILogger<FFmpegH264Encoder>? log = null)
        : this(preferred, log, new NativeEncoderCandidateFactory()) { }

    internal FFmpegH264Encoder(string? preferred, ILogger<FFmpegH264Encoder>? log, IEncoderCandidateFactory factory)
    {
        if (preferred is not null && !AllowedEncoders.Contains(preferred)) throw new ArgumentException("허용하지 않는 인코더다.", nameof(preferred));
        _preferred = preferred;
        _log = (ILogger?)log ?? NullLogger.Instance;
        _factory = factory;
    }

    public void Configure(int width, int height, int fps, int bitrateKbps)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        FrameLayout.ValidateDimensions(width, height);
        if ((width & 1) != 0 || (height & 1) != 0) throw new ArgumentException("H.264 출력 크기는 짝수여야 한다.");
        if (fps is < 1 or > 60 || bitrateKbps is < 100 or > 50000) throw new ArgumentOutOfRangeException(nameof(fps));
        _factory.Prepare(_log);
        _encoder?.Dispose(); _encoder = null; EncoderName = "(미선택)";
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(_inputBuffer);_inputBuffer=[];
        var errors = new List<string>();
        var probe = new byte[checked(width * height * 4)];
        foreach (var name in _preferred is null ? AllowedEncoders : new[] { _preferred })
        {
            IEncoderCandidate? candidate = null;
            try
            {
                var options = Options(name);
                options["profile"]=name switch {"h264_qsv"=>"66","libopenh264"=>"constrained_baseline",_=>"baseline"};
                if(name=="h264_nvenc")options["level"]=MaximumLevel.ToString(System.Globalization.CultureInfo.InvariantCulture);
                candidate = _factory.Create(name, options);
                candidate.Configure(width, height, fps, bitrateKbps);
                var deadline = Environment.TickCount64 + 2000;
                var emitted = false;
                var parameterSets = new H264ParameterSets();
                for (var i = 0; i < 8 && Environment.TickCount64 < deadline; i++)
                {
                    probe[0] = (byte)i;
                    var output = candidate.Encode(width, height, probe);
                    if (output is { Length: > 0 }) { parameterSets.Process(output);parameterSets.DemandBaseline(MaximumLevel); emitted = true; break; }
                }
                if (!emitted) throw new InvalidOperationException("시험 인코딩 출력이 없다.");
                // The pinned SetCodec API binds an AVCodec pointer by encoder name.
                // GetCodecName returns the codec ID name ("h264"), not the encoder name.
                candidate.ForceKeyFrame();
                _encoder = candidate; candidate = null;
                _parameterSets = parameterSets;
                EncoderName = name; _width = width; _height = height;
                _inputBuffer=new byte[checked(width*height*4)];
                _log.LogInformation("H.264 실제 초기화·출력 성공: {Encoder}, {W}x{H}, {Fps}fps", name, width, height, fps);
                return;
            }
            catch (Exception ex)
            {
                errors.Add($"{name}: {ex.Message}");
                _log.LogWarning("H.264 초기화 실패: {Encoder}", name);
            }
            finally { candidate?.Dispose(); }
        }
        throw new InvalidOperationException("H.264 초기화 후보가 모두 실패했다. " + string.Join(" | ", errors));
    }

    private static Dictionary<string, string> Options(string name) => name switch
    {
        "h264_nvenc" => new() { ["preset"] = "p1", ["tune"] = "ull", ["rc"] = "cbr", ["delay"] = "0", ["zerolatency"] = "1", ["forced-idr"] = "1" },
        "h264_qsv" => new() { ["preset"] = "veryfast", ["low_power"] = "1", ["async_depth"] = "1" },
        "h264_amf" => new() { ["usage"] = "ultralowlatency", ["quality"] = "speed" },
        "h264_mf" => new() { ["hw_encoding"] = "true", ["low_latency"] = "true" },
        "libopenh264" => new() { ["allow_skip_frames"] = "1" },
        _ => throw new ArgumentException("허용하지 않는 인코더다.")
    };
    public ReadOnlyMemory<byte> Encode(ReadOnlyMemory<byte> bgraFrame, bool forceKeyframe)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_encoder is null) throw new InvalidOperationException("Configure를 먼저 호출해야 한다.");
        if (bgraFrame.Length != checked(_width * _height * 4)) throw new ArgumentException("BGRA 길이가 인코더 크기와 다르다.");
        if (forceKeyframe) _encoder.ForceKeyFrame();
        bgraFrame.Span.CopyTo(_inputBuffer);
        var encoded = _encoder.Encode(_width, _height, _inputBuffer);
        if(encoded is not {Length:>0})return ReadOnlyMemory<byte>.Empty;var output=_parameterSets.Process(encoded);_parameterSets.DemandBaseline(MaximumLevel);return output;
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _encoder?.Dispose(); _encoder = null; System.Security.Cryptography.CryptographicOperations.ZeroMemory(_inputBuffer);_inputBuffer=[]; }
}
