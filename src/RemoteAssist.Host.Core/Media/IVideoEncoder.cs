namespace RemoteAssist.Host.Core.Media;

/// <summary>
/// 영상 인코더 추상화.
///
/// 구현체는 FFmpeg(경로 A, 확정)로 시작하지만, LGPL 동봉 부담이 배포 단계에서
/// 문제가 되면 Media Foundation 직접 구현(경로 B)으로 갈아탈 수 있어야 한다.
/// 이 인터페이스가 그 보험이다 — 계획서 T1.3 참고.
/// </summary>
public interface IVideoEncoder : IDisposable
{
    /// <summary>
    /// 실제 선택된 FFmpeg 인코더 이름(예: h264_nvenc).
    /// libx264는 GPL이므로 절대 선택되어서는 안 된다. 로그·상태바에 노출한다.
    /// </summary>
    string EncoderName { get; }

    /// <summary>하드웨어 인코더 사용 여부.</summary>
    bool IsHardware { get; }

    void Configure(int width, int height, int fps, int bitrateKbps);

    /// <summary>BGRA 프레임을 인코딩한다. 반환이 비어 있으면 이번 프레임은 출력이 없다.</summary>
    ReadOnlyMemory<byte> Encode(ReadOnlyMemory<byte> bgraFrame, bool forceKeyframe);
}
