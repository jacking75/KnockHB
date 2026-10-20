namespace RemoteAssist.Host.Core.Capture;

public static class ScreenCaptureFactory
{
    public static IScreenCapture Create(MonitorInfo target) => Create(target,WgcScreenCapture.IsSupported,
        m=>new WgcScreenCapture(m),m=>new DxgiScreenCapture(m),m=>Monitors.Enumerate().Contains(m));

    internal static IScreenCapture Create(MonitorInfo target,Func<bool> supported,
        Func<MonitorInfo,IScreenCapture> wgc,Func<MonitorInfo,IScreenCapture> dxgi,Func<MonitorInfo,bool> current)
    {
        Demand();
        Exception? primaryFailure=null;
        if(supported())
        {
            try{return Open(wgc);}
            catch(Exception ex) when(ex is System.Runtime.InteropServices.ExternalException or NotSupportedException or InvalidOperationException or SharpGen.Runtime.SharpGenException)
            {primaryFailure=ex;}
        }
        // A failed constructor owns its rollback. Never switch outputs to make capture succeed.
        Demand();
        try{return Open(dxgi);}
        catch(Exception ex) when(primaryFailure!=null&&ex is not (UnauthorizedAccessException or OperationCanceledException or OutOfMemoryException))
        {throw new AggregateException("승인 모니터의 WGC와 DXGI 초기화가 모두 실패했다.",primaryFailure,ex);}

        void Demand(){if(!current(target))throw new UnauthorizedAccessException("승인 모니터가 변경돼 캡처 선택을 중단한다.");}
        IScreenCapture Open(Func<MonitorInfo,IScreenCapture> create)
        {
            var capture=create(target);
            try{Demand();if(capture.Monitor!=target)throw new UnauthorizedAccessException("캡처가 승인 모니터와 다르다.");return capture;}
            catch{capture.Dispose();throw;}
        }
    }
}
