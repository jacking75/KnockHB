using RemoteAssist.Host.Core.Media;

namespace RemoteAssist.Host.Core;

public sealed record ScreenShareOptions
{
    public const int ConsentDecisionSeconds = 120;
    public Uri SignalingUri { get; init; } = new("http://localhost:25001");
    public bool AllowLoopbackHttp { get; init; }
    public string? SignalingCertificateBase64 { get; init; }
    public int MonitorIndex { get; init; }
    public int TargetFps { get; init; } = 30;
    public int BitrateKbps { get; init; } = 4000;
    public string? PreferredEncoder { get; init; }
    public string[] StunServers { get; init; } = [];
    public bool ForceRelay { get; init; }
    public int MaxOutputLongEdge { get; init; } = 1920;
    public int MaxOutputShortEdge { get; init; } = 1080;

    public void Validate(int monitorCount)
    {
        ValidateEndpoint(SignalingUri,AllowLoopbackHttp);
        using var certificate = Signaling.SignalingTrust.Load(SignalingCertificateBase64);
        if (certificate is not null && SignalingUri.Scheme != "https") throw new ArgumentException("Pinned trust requires HTTPS.");
        if(MaxOutputShortEdge is < 2 or > 2160 || MaxOutputLongEdge < MaxOutputShortEdge || MaxOutputLongEdge > 3840)
            throw new ArgumentOutOfRangeException(nameof(MaxOutputLongEdge), "출력 크기 상한이 지원 범위를 벗어났다.");
        if (TargetFps is < 1 or > 60) throw new ArgumentOutOfRangeException(nameof(TargetFps), "FPS는 1~60이어야 한다.");
        if (BitrateKbps is < 100 or > 50000) throw new ArgumentOutOfRangeException(nameof(BitrateKbps), "비트레이트는 100~50000kbps이어야 한다.");
        if (MonitorIndex < 0 || MonitorIndex >= monitorCount) throw new ArgumentOutOfRangeException(nameof(MonitorIndex));
        if (PreferredEncoder is not null && !FFmpegH264Encoder.AllowedEncoders.Contains(PreferredEncoder))
            throw new ArgumentException("허용하지 않는 인코더다.", nameof(PreferredEncoder));
        foreach (var url in StunServers)
            if (!url.StartsWith("stun:", StringComparison.Ordinal) && !url.StartsWith("stuns:", StringComparison.Ordinal))
                throw new ArgumentException("잘못된 STUN 주소다.");
    }
    public static void ValidateEndpoint(Uri uri,bool allowLoopbackHttp)
    {
        if(uri is null||!uri.IsAbsoluteUri||(uri.Scheme!="https"&&!(allowLoopbackHttp&&uri.Scheme=="http"&&uri.IsLoopback))||
           !string.IsNullOrEmpty(uri.UserInfo)||!string.IsNullOrEmpty(uri.Query)||!string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("HTTPS 시그널링 주소가 필요하다. 평문은 명시적 loopback 개발 모드에서만 허용한다.");
    }
}
