using System.Runtime.InteropServices;

namespace RemoteAssist.Host.Core.Capture;

/// <summary>
/// 모니터 열거. Win32 <c>EnumDisplayMonitors</c> 를 쓴다.
///
/// DXGI로도 열거할 수 있지만, 하이브리드 GPU 노트북에서는 어느 어댑터가 출력을
/// 보고하는지가 상황에 따라 달라진다. 캡처 백엔드와 무관하게 같은 목록을 얻으려면
/// Win32 쪽이 안정적이다. WGC가 요구하는 HMONITOR도 여기서 바로 나온다.
/// </summary>
public static class Monitors
{
    private const int MonitorInfoFlagPrimary = 0x1;

    /// <summary>연결된 모니터를 왼쪽 위 기준으로 정렬해 돌려준다. 인덱스는 이 순서다.</summary>
    public static IReadOnlyList<MonitorInfo> Enumerate()
    {
        var found = new List<(nint Handle, RectL Bounds, string Name, bool Primary)>();

        var callback = new MonitorEnumProc((nint hMonitor, nint _, ref RectL _, nint _) =>
        {
            var info = new MonitorInfoEx { CbSize = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfo(hMonitor, ref info))
            {
                found.Add((hMonitor, info.RcMonitor, info.SzDevice, (info.DwFlags & MonitorInfoFlagPrimary) != 0));
            }
            return true; // 계속 열거
        });

        if (!EnumDisplayMonitors(nint.Zero, nint.Zero, callback, nint.Zero))
        {
            throw new InvalidOperationException("EnumDisplayMonitors 실패");
        }

        GC.KeepAlive(callback);

        // 주 모니터를 0번으로, 나머지는 화면 배치 순서로 안정적인 인덱스를 만든다.
        return found
            .OrderByDescending(m => m.Primary)
            .ThenBy(m => m.Bounds.Left)
            .ThenBy(m => m.Bounds.Top)
            .Select((m, i) => new MonitorInfo(
                Index: i,
                DeviceName: m.Name,
                X: m.Bounds.Left,
                Y: m.Bounds.Top,
                Width: m.Bounds.Right - m.Bounds.Left,
                Height: m.Bounds.Bottom - m.Bounds.Top,
                IsPrimary: m.Primary,
                Handle: m.Handle))
            .ToList();
    }

    /// <summary>인덱스로 모니터를 찾는다. 없으면 예외.</summary>
    public static MonitorInfo Get(int index)
    {
        var all = Enumerate();
        if (index < 0 || index >= all.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index), $"모니터 {index} 를 찾을 수 없습니다. 연결된 모니터: {all.Count}개");
        }
        return all[index];
    }

    private delegate bool MonitorEnumProc(nint hMonitor, nint hdc, ref RectL rect, nint data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc callback, nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MonitorInfoEx info);

    [StructLayout(LayoutKind.Sequential)]
    private struct RectL
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int CbSize;
        public RectL RcMonitor;
        public RectL RcWork;
        public int DwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string SzDevice;
    }
}
