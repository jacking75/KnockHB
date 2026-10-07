using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace RemoteAssist.Host.Core.Capture;

/// <summary>
/// Windows Graphics Capture를 쓰기 위한 COM/WinRT 상호운용.
///
/// WGC의 두 지점은 순수 WinRT 투영만으로는 닿지 않는다.
///   1. 모니터 핸들(HMONITOR)로 <c>GraphicsCaptureItem</c> 만들기 → IGraphicsCaptureItemInterop
///   2. 캡처 프레임의 표면에서 D3D11 텍스처 꺼내기 → IDirect3DDxgiInterfaceAccess
/// 둘 다 여기서 처리한다.
/// </summary>
internal static class WgcInterop
{
    private static readonly Guid IidGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IidGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IidD3D11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    /// <summary>HMONITOR로 캡처 대상을 만든다.</summary>
    public static GraphicsCaptureItem CreateItemForMonitor(nint hMonitor)
    {
        var factory = (IGraphicsCaptureItemInterop)GetActivationFactory(
            "Windows.Graphics.Capture.GraphicsCaptureItem", IidGraphicsCaptureItemInterop);

        var itemPtr=nint.Zero;
        try
        {
            var iid = IidGraphicsCaptureItem;
            itemPtr = factory.CreateForMonitor(hMonitor, ref iid);
            if(itemPtr==nint.Zero)throw new InvalidOperationException("GraphicsCaptureItem 생성 실패 (HMONITOR)");
            return MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPtr);
        }
        finally
        {
            if(itemPtr!=nint.Zero)Marshal.Release(itemPtr);
            Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>D3D11 디바이스를 WinRT <c>IDirect3DDevice</c> 로 감싼다.</summary>
    public static IDirect3DDevice CreateDirect3DDevice(nint dxgiDevicePtr)
    {
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevicePtr, out var abi));
        try
        {
            return MarshalInspectable<IDirect3DDevice>.FromAbi(abi);
        }
        finally
        {
            Marshal.Release(abi);
        }
    }

    /// <summary>
    /// 캡처 표면에서 ID3D11Texture2D 원시 포인터를 꺼낸다.
    /// 호출자가 <see cref="Marshal.Release"/> 책임을 진다(Vortice 래퍼에 넘기면 Dispose가 처리한다).
    /// </summary>
    public static nint GetTexturePointer(IDirect3DSurface surface)
    {
        using var objRef = MarshalInspectable<IDirect3DSurface>.CreateMarshaler(surface);
        var thisPtr = MarshalInspectable<IDirect3DSurface>.GetAbi(objRef);

        var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(thisPtr);
        try
        {
            var iid = IidD3D11Texture2D;
            return access.GetInterface(ref iid);
        }
        finally
        {
            Marshal.ReleaseComObject(access);
        }
    }

    private static object GetActivationFactory(string className, Guid iid)
    {
        Marshal.ThrowExceptionForHR(WindowsCreateString(className, className.Length, out var hstr));
        try
        {
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstr, ref iid, out var factoryPtr));
            try
            {
                return Marshal.GetObjectForIUnknown(factoryPtr);
            }
            finally
            {
                Marshal.Release(factoryPtr);
            }
        }
        finally
        {
            WindowsDeleteString(hstr);
        }
    }

    /// <summary>
    /// RoGetActivationFactory 는 스레드의 COM이 초기화돼 있어야 한다.
    /// .NET은 순수 P/Invoke 경로에서는 알아서 초기화해 주지 않으므로 직접 부른다.
    /// </summary>
    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string sourceString, int length, out nint hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(nint hstring);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(nint activatableClassId, ref Guid iid, out nint factory);

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow(nint window, ref Guid iid);
        nint CreateForMonitor(nint monitor, ref Guid iid);
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        nint GetInterface(ref Guid iid);
    }
}
