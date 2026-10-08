using RemoteAssist.Host.Core.Media;
using RemoteAssist.Protocol;
static class QualityRegression
{
    sealed class Clock:TimeProvider {public long Tick;public override long GetTimestamp()=>Tick;public override long TimestampFrequency=>1000;}
    public static Task Run()
    {
        var time=new Clock();var quality=new AdaptiveQuality(30,4000,1920,1080,time);
        if(quality.Observe(new(500,10,40,2000)))throw new Exception("품질 최소 유지 시간을 무시했다.");time.Tick=5000;
        if(!quality.Observe(new(500,10,40,2000))||quality.Current.Fps>=30)throw new Exception("혼잡에서 품질을 낮추지 않았다.");
        var low=quality.Current;time.Tick=10000;if(quality.Observe(new(10,0,1,3000)))throw new Exception("회복 안정 시간을 무시했다.");
        for(var i=11;i<=26;i++){time.Tick=i*1000;quality.Observe(new(10,0,1,3000));}
        if(quality.Current.BitrateKbps<=low.BitrateKbps)throw new Exception("연속 안정 후 회복하지 않았다.");
        var beforeGap=quality.Current;time.Tick=90000;if(quality.Observe(new(10,0,1,3000))||quality.Current!=beforeGap)throw new Exception("누락된 통계를 안정 구간으로 계산했다.");
        try{new QualitySample(double.NaN,0,0,0).Validate();throw new Exception("NaN을 허용했다.");}catch(InvalidDataException){}
        var rtpTime=new Clock();var rtp=new FrameRtpClock(uint.MaxValue-1000,rtpTime);rtpTime.Tick=100;
        if(rtp.Timestamp!=unchecked(uint.MaxValue-1000+9000u))throw new Exception("RTP elapsed/wrap 계약 오류다.");
        rtpTime.Tick=2100;if(rtp.Timestamp!=unchecked(uint.MaxValue-1000+189000u))throw new Exception("프레임 생략 시간이 RTP에 반영되지 않았다.");
        var answer="v=0\r\nm=video 9 UDP/TLS/RTP/SAVPF 102\r\na=rtpmap:102 H264/90000\r\na=fmtp:102 packetization-mode=1;profile-level-id=42e01f\r\n";
        if(H264Contract.FromAnswer(answer).LongEdge!=1280)throw new Exception("Peer H264 level의 출력 한도를 무시했다.");
        foreach(var invalid in new[]{answer.Replace("42e01f","64001f"),answer.Replace("mode=1","mode=0"),answer.Replace("42e01f","42e00a")})
        {try{H264Contract.FromAnswer(invalid);throw new Exception("미지원 H264 계약을 허용했다.");}catch(InvalidDataException){}}
        var nal=new byte[2401];nal[0]=0x65;for(var i=1;i<nal.Length;i++)nal[i]=(byte)(i%250+1);
        var packets=H264RtpPayloads.Packetize(new byte[]{0,0,0,1}.Concat(nal).ToArray()).ToArray();
        if(packets.Length!=3||packets.Count(p=>p.Marker==1)!=1||packets[^1].Marker!=1||packets.Any(p=>p.Bytes.Length>1200)||packets[0].Bytes[1]!=0x85||packets[^1].Bytes[1]!=0x45)throw new Exception("H264 FU-A marker/start/end 경계 오류다.");
        if(!packets.SelectMany(p=>p.Bytes.Skip(2)).SequenceEqual(nal.Skip(1)))throw new Exception("H264 FU-A에서 payload가 바뀌었다.");
        return Task.CompletedTask;
    }
}
