using FFmpeg.AutoGen;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.FFmpeg;
using Microsoft.Extensions.Logging;

namespace RemoteAssist.Host.Core.Media;

internal interface IEncoderCandidate : IDisposable
{
    void Configure(int width, int height, int fps, int bitrateKbps);
    byte[]? Encode(int width, int height, byte[] bgra);
    void ForceKeyFrame();
}
internal interface IEncoderCandidateFactory
{
    void Prepare(ILogger log);
    IEncoderCandidate Create(string name, Dictionary<string, string> options);
}
internal sealed class NativeEncoderCandidateFactory : IEncoderCandidateFactory
{
    public void Prepare(ILogger log) => FFmpegLoader.Initialise(log: log);
    public IEncoderCandidate Create(string name, Dictionary<string, string> options) => new NativeEncoderCandidate(name, options);
}
internal sealed class NativeEncoderCandidate : IEncoderCandidate
{
    private readonly FFmpegVideoEncoder _encoder;
    public NativeEncoderCandidate(string name, Dictionary<string, string> options)
    {
        _encoder = new FFmpegVideoEncoder(options, AVHWDeviceType.AV_HWDEVICE_TYPE_NONE);
        try
        {
            if (!_encoder.SetCodec(AVCodecID.AV_CODEC_ID_H264, name, options)) throw new InvalidOperationException("인코더 미등록이다.");
        }
        catch { _encoder.Dispose(); throw; }
    }
    public void Configure(int width, int height, int fps, int bitrateKbps)
    {
        _encoder.SetBitrate(bitrateKbps * 1000L, null, null, null);
        _encoder.InitialiseEncoder(AVCodecID.AV_CODEC_ID_H264, width, height, fps);
    }
    public byte[]? Encode(int width, int height, byte[] bgra) => _encoder.EncodeVideo(width, height, bgra, VideoPixelFormatsEnum.Bgra, VideoCodecsEnum.H264);
    public void ForceKeyFrame() => _encoder.ForceKeyFrame();
    public void Dispose() => _encoder.Dispose();
}
