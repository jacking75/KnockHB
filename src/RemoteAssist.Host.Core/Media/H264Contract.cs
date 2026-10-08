using System.Globalization;
namespace RemoteAssist.Host.Core.Media;

public sealed record H264Contract(int Level,int Fps,int Bitrate,int LongEdge,int ShortEdge)
{
    public const string Offer="packetization-mode=1;profile-level-id=42e034;level-asymmetry-allowed=1";
    public static H264Contract FromAnswer(string sdp)
    {
        var lines=sdp.Split('\n').Select(l=>l.TrimEnd('\r')).ToArray();var video=Array.FindIndex(lines,l=>l.StartsWith("m=video ",StringComparison.Ordinal));if(video<0||lines.Count(l=>l.StartsWith("m=video ",StringComparison.Ordinal))!=1)throw new InvalidDataException("단일 영상 answer가 필요하다.");
        var media=lines[video].Split(' ',StringSplitOptions.RemoveEmptyEntries);if(media.Length<4||media[1]=="0"||!media.Skip(3).Contains("102"))throw new InvalidDataException("승인 영상 payload를 거부했다.");
        var section=lines.Skip(video+1).TakeWhile(l=>!l.StartsWith("m=",StringComparison.Ordinal)).ToArray();if(!section.Any(l=>l.Equals("a=rtpmap:102 H264/90000",StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException("H264/90000 계약이 아니다.");
        var fmtps=section.Where(l=>l.StartsWith("a=fmtp:102 ",StringComparison.Ordinal)).ToArray();if(fmtps.Length!=1)throw new InvalidDataException("단일 H264 fmtp가 필요하다.");var parameters=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var item in fmtps[0][11..].Split(';')){var pair=item.Trim().Split('=',2);if(pair.Length!=2||!parameters.TryAdd(pair[0].Trim(),pair[1].Trim()))throw new InvalidDataException("H264 fmtp 중복/형식 오류다.");}
        if(!parameters.TryGetValue("packetization-mode",out var mode)||mode!="1"||!parameters.TryGetValue("profile-level-id",out var profile)||profile.Length!=6||!int.TryParse(profile,NumberStyles.HexNumber,CultureInfo.InvariantCulture,out var value)||value>>16!=66||(value>>8&64)==0)throw new InvalidDataException("constrained baseline mode1이 아니다.");
        return Limits(value&255);
    }
    public static H264Contract Limits(int level)=>level switch
    {
        31=>new(31,30,14000,1280,720),32=>new(32,60,20000,1280,720),40=>new(40,30,20000,1920,1080),41=>new(41,30,50000,1920,1080),42=>new(42,60,50000,1920,1080),50=>new(50,30,50000,2560,1440),51=>new(51,30,50000,3840,2160),52=>new(52,60,50000,3840,2160),_=>throw new InvalidDataException("지원하지 않는 H264 level이다.")
    };
}
