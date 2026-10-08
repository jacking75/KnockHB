using RemoteAssist.Protocol;

namespace RemoteAssist.Host.Core.Media;

public sealed class AdaptiveQuality
{
    readonly object gate=new();readonly QualitySetting maximum;readonly TimeProvider clock;
    QualitySetting current;long changed,stable;bool hasSample;long lastSample;
    public AdaptiveQuality(int fps,int bitrate,int longEdge,int shortEdge,TimeProvider? time=null)
    {clock=time??TimeProvider.System;maximum=current=new(fps,bitrate,longEdge,shortEdge,"auto");current.Validate();changed=stable=clock.GetTimestamp();}
    public QualitySetting Current {get{lock(gate)return current;}}
    public void Mode(string mode){lock(gate){if(mode is not ("auto" or "detail" or "motion"))throw new ArgumentException("품질 모드 오류다.");current=current with {Mode=mode};changed=stable=clock.GetTimestamp();}}
    public bool Observe(QualitySample sample)
    {
        sample.Validate();lock(gate)
        {
            var now=clock.GetTimestamp();if(hasSample&&clock.GetElapsedTime(lastSample,now)<TimeSpan.FromSeconds(1))return false;
            if(hasSample&&clock.GetElapsedTime(lastSample,now)>TimeSpan.FromSeconds(3))stable=now;
            hasSample=true;lastSample=now;var bad=sample.LossPercent>=3||sample.RttMs>=250||sample.DecodeMs>=1000d/current.Fps;
            if(bad)stable=now;if(clock.GetElapsedTime(changed,now)<TimeSpan.FromSeconds(5))return false;
            var next=current;
            if(bad)next=current with {BitrateKbps=Math.Max(100,(int)(current.BitrateKbps*.75)),Fps=Math.Max(Math.Min(5,maximum.Fps),current.Fps-5),LongEdge=Math.Max(Math.Min(640,maximum.LongEdge),(int)(current.LongEdge*.8)&~1),ShortEdge=Math.Max(Math.Min(360,maximum.ShortEdge),(int)(current.ShortEdge*.8)&~1)};
            else if(clock.GetElapsedTime(stable,now)>=TimeSpan.FromSeconds(15))next=current with {BitrateKbps=Math.Min(maximum.BitrateKbps,current.BitrateKbps+250),Fps=Math.Min(maximum.Fps,current.Fps+2),LongEdge=Math.Min(maximum.LongEdge,current.LongEdge+128),ShortEdge=Math.Min(maximum.ShortEdge,current.ShortEdge+72)};
            if(current.Mode=="detail")next=next with {Fps=Math.Min(next.Fps,20)};
            if(current.Mode=="motion")next=next with {LongEdge=Math.Min(next.LongEdge,1280),ShortEdge=Math.Min(next.ShortEdge,720)};
            if(next==current)return false;current=next;changed=now;return true;
        }
    }
}
