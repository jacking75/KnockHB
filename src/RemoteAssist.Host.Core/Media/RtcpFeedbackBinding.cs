using SIPSorcery.Net;

namespace RemoteAssist.Host.Core.Media;

/// <summary>Call only with an authenticated, decrypted OnReceiveReport from the same send-only peer.</summary>
public sealed class RtcpFeedbackBinding
{
    uint? bound;
    readonly object gate=new();
    public bool Observe(SDPMediaTypesEnum media,RTCPCompoundPacket report,MediaStreamTrack local,MediaStreamTrack? remote)
    {
        if(media!=SDPMediaTypesEnum.video||remote is null||local.StreamStatus!=MediaStreamStatusEnum.SendOnly)return false;
        var receiver=report.ReceiverReport;
        if(receiver is null||receiver.SSRC==0||receiver.SSRC==local.Ssrc||receiver.ReceptionReports?.Any(x=>x.SSRC==local.Ssrc)!=true)return false;
        lock(gate)
        {
            if(bound.HasValue)return false;
            // An incoming RTP stream has its own authenticated SSRC; do not overwrite it.
            if(remote.Ssrc!=0&&remote.Ssrc!=receiver.SSRC)return false;
            bound=receiver.SSRC;remote.Ssrc=receiver.SSRC;return true;
        }
    }
}
