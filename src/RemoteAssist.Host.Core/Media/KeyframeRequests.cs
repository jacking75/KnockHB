using SIPSorcery.Net;

namespace RemoteAssist.Host.Core.Media;

/// <summary>RTCP 콜백은 요청만 기록하며 인코더 소유 스레드가 합쳐서 소비한다.</summary>
public sealed class KeyframeRequests
{
    private int _pending = 1;
    public void Request() => Interlocked.Exchange(ref _pending, 1);
    public bool Consume() => Interlocked.Exchange(ref _pending, 0) != 0;
    public void Observe(SDPMediaTypesEnum media, RTCPCompoundPacket report)
    {
        var header = report.Feedback?.Header;
        if (media == SDPMediaTypesEnum.video && header is not null &&
            header.PacketType == RTCPReportTypesEnum.PSFB &&
            header.PayloadFeedbackMessageType is PSFBFeedbackTypesEnum.PLI or PSFBFeedbackTypesEnum.FIR)
            Request();
    }
}
