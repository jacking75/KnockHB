using System.Runtime.InteropServices;

namespace RemoteAssist.Host.Core.Capture;

internal sealed class ComApartment : IDisposable
{
    private readonly bool _owned;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private bool _disposed;
    public ComApartment()
    {
        var result=CoInitializeEx(0,0);
        if(result<0&&result!=unchecked((int)0x80010106))Marshal.ThrowExceptionForHR(result);
        _owned=result>=0;
    }
    public void Dispose()
    {
        if(_disposed)return;
        if(_thread!=Environment.CurrentManagedThreadId)throw new InvalidOperationException("COM apartment는 생성 스레드에서 해제해야 한다.");
        _disposed=true;if(_owned)CoUninitialize();
    }
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved,uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
}
