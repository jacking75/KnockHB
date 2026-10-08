using RemoteAssist.Host.Core.Capture;

namespace RemoteAssist.Host.Core.Input;

public static class InputCoordinates
{
    public static (int X,int Y) Pixel(MonitorInfo monitor,double x,double y)
    {
        if(monitor.Width<2||monitor.Height<2||!double.IsFinite(x)||!double.IsFinite(y)||x is <0 or >1||y is <0 or >1)throw new ArgumentException("입력 좌표 범위 오류다.");
        return (checked(monitor.X+(int)Math.Round(x*(monitor.Width-1))),checked(monitor.Y+(int)Math.Round(y*(monitor.Height-1))));
    }
    public static (int X,int Y) Absolute(MonitorInfo monitor,double x,double y,Rect virtualScreen)
    {
        var (px,py)=Pixel(monitor,x,y);
        if(virtualScreen.Width<2||virtualScreen.Height<2||px<virtualScreen.Left||px>=virtualScreen.Right||py<virtualScreen.Top||py>=virtualScreen.Bottom)throw new ArgumentException("가상 데스크톱 바깥의 입력이다.");
        return ((int)Math.Round((px-(double)virtualScreen.Left)*65535/(virtualScreen.Width-1)),(int)Math.Round((py-(double)virtualScreen.Top)*65535/(virtualScreen.Height-1)));
    }
}
