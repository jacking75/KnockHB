namespace RemoteAssist.Host.Core.Tunnel;

public static class IceCandidateWire
{
    /// <summary>SIPSorcery의 후보 본문을 브라우저 RTCIceCandidate의 문자열로 변환한다.</summary>
    public static string FromNative(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 8192 || candidate.Contains('\r') || candidate.Contains('\n'))
            throw new InvalidDataException("잘못된 native ICE 후보다.");
        return candidate.StartsWith("candidate:", StringComparison.Ordinal) ? candidate : "candidate:" + candidate;
    }
}
