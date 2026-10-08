using SIPSorcery.Net;
namespace RemoteAssist.Host.Core.Media;

public static class H264RtpPayloads
{
    public sealed record Payload(byte[] Bytes,int Marker);
    public static IEnumerable<Payload> Packetize(byte[] accessUnit)
    {
        if(accessUnit.Length>16*1024*1024)throw new InvalidDataException("H264 access unit 상한이다.");
        var count=0;
        foreach(var unit in H264Packetiser.ParseNals(accessUnit))
        {
            if(++count>4096||unit.NAL.Length==0||(unit.NAL[0]&31) is <1 or >23)throw new InvalidDataException("H264 NAL 경계 오류다.");
            if(unit.NAL.Length<=1200){yield return new(unit.NAL,unit.IsLast?1:0);continue;}
            for(var at=1;at<unit.NAL.Length;at+=1198)
            {
                var size=Math.Min(1198,unit.NAL.Length-at);var last=at+size==unit.NAL.Length;var packet=new byte[size+2];
                packet[0]=(byte)((unit.NAL[0]&0xe0)|28);packet[1]=(byte)((unit.NAL[0]&31)|(at==1?0x80:0)|(last?0x40:0));unit.NAL.AsSpan(at,size).CopyTo(packet.AsSpan(2));
                yield return new(packet,unit.IsLast&&last?1:0);
            }
        }
        if(count==0)throw new InvalidDataException("H264 Annex-B NAL이 없다.");
    }
}
