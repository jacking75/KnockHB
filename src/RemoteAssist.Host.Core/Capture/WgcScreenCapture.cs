using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace RemoteAssist.Host.Core.Capture;

/// <summary>
/// Windows Graphics Capture(WGC) 기반 화면 캡처. <b>기본 경로다.</b>
///
/// <para>
/// WGC를 우선하는 배경은 docs/technical-specification.md에 정리한다. 하이브리드 GPU 노트북
/// (내장 + 외장, 예: Intel UHD + NVIDIA Max-Q)에서는 DXGI Desktop Duplication이
/// 어떤 어댑터 조합으로도 <c>DXGI_ERROR_UNSUPPORTED</c> 로 실패할 수 있다.
/// WGC는 컴포지터를 통해 가져오므로 이 문제를 겪지 않는다.
/// </para>
///
/// <para>
/// 대가: <b>변경 영역(dirty rect) 정보를 주지 않는다.</b> T6.3의 대역폭 최적화 중
/// dirty rect 기반 부분은 DXGI 경로에서만 쓸 수 있다.
/// 또 캡처 중인 화면에 노란 테두리가 표시된다(OS 정책).
/// </para>
///
/// <para><b>스레드 안전하지 않다.</b></para>
/// </summary>
public sealed class WgcScreenCapture : IScreenCapture
{
    private readonly ILogger _log;

    private ID3D11Device _device = null!;
    private ID3D11DeviceContext _context = null!;
    private GraphicsCaptureItem _item = null!;
    private Direct3D11CaptureFramePool _pool = null!;
    private GraphicsCaptureSession _session = null!;
    private ID3D11Texture2D? _staging;

    private byte[] _buffer = [];
    private int _width;
    private int _height;
    private bool _disposed;

    public MonitorInfo Monitor { get; private set; }

    /// <summary>이 시스템에서 WGC를 쓸 수 있는지.</summary>
    public static bool IsSupported()
    {
        try
        {
            return GraphicsCaptureSession.IsSupported();
        }
        catch
        {
            return false;
        }
    }

    public WgcScreenCapture(int monitorIndex = 0, ILogger<WgcScreenCapture>? log = null)
    {
        _log = (ILogger?)log ?? NullLogger.Instance;
        Monitor = Monitors.Get(monitorIndex);

        if (!IsSupported())
        {
            throw new PlatformNotSupportedException("이 시스템에서 Windows Graphics Capture를 쓸 수 없습니다.");
        }

        FeatureLevel[] levels =
        [
            FeatureLevel.Level_11_1, FeatureLevel.Level_11_0,
            FeatureLevel.Level_10_1, FeatureLevel.Level_10_0,
        ];

        // WGC는 DXGI 복제와 달리 출력을 소유한 어댑터를 맞출 필요가 없다.
        D3D11.D3D11CreateDevice(
            null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels,
            out _device!, out _context!).CheckError();

        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        var rtDevice = WgcInterop.CreateDirect3DDevice(dxgiDevice.NativePointer);

        _item = WgcInterop.CreateItemForMonitor(Monitor.Handle);
        _width = _item.Size.Width;
        _height = _item.Size.Height;

        // CreateFreeThreaded 를 쓰면 DispatcherQueue 없이 폴링만으로 프레임을 받을 수 있다.
        // 서비스(Session 0)에는 메시지 펌프가 없으므로 이 선택이 중요하다.
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            rtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _item.Size);

        _session = _pool.CreateCaptureSession(_item);
        _session.StartCapture();

        AllocateBuffers();

        _log.LogInformation("WGC 캡처 시작: {Monitor}", Monitor);
    }

    private void AllocateBuffers()
    {
        _staging?.Dispose();
        _staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_width,
            Height = (uint)_height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        });

        _buffer = new byte[(long)_width * _height * 4];
    }

    public bool TryGetFrame(TimeSpan timeout, out CapturedFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        frame = default;

        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;

        while (true)
        {
            using var captured = _pool.TryGetNextFrame();
            if (captured is not null)
            {
                return Convert(captured, out frame);
            }

            if (Environment.TickCount64 >= deadline)
            {
                // 화면이 정적이다. 오류가 아니다.
                return false;
            }

            Thread.Sleep(2);
        }
    }

    private bool Convert(Direct3D11CaptureFrame captured, out CapturedFrame frame)
    {
        frame = default;

        // 해상도가 바뀌면 풀과 스테이징을 다시 만든다. 이 프레임은 버린다.
        var size = captured.ContentSize;
        if (size.Width != _width || size.Height != _height)
        {
            _log.LogInformation("해상도 변경 감지: {OldW}x{OldH} → {NewW}x{NewH}",
                _width, _height, size.Width, size.Height);

            _width = size.Width;
            _height = size.Height;
            AllocateBuffers();
            _pool.Recreate(
                WgcInterop.CreateDirect3DDevice(_device.QueryInterface<IDXGIDevice>().NativePointer),
                DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
            return false;
        }

        var texPtr = WgcInterop.GetTexturePointer(captured.Surface);
        using var texture = new ID3D11Texture2D(texPtr);

        _context.CopyResource(_staging!, texture);

        var map = _context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            CopyToBuffer(map);
        }
        finally
        {
            _context.Unmap(_staging!, 0);
        }

        // WGC는 변경 영역을 주지 않는다. DXGI 경로에서만 얻을 수 있는 정보다.
        frame = new CapturedFrame(
            _buffer, _width, _height, _width * 4,
            accumulatedFrames: 1, dirtyRectCount: -1, dirtyBounds: Rect.Empty);
        return true;
    }

    private void CopyToBuffer(MappedSubresource map)
    {
        var rowBytes = _width * 4;
        var pitch = (int)map.RowPitch;

        if (pitch == rowBytes)
        {
            Marshal.Copy(map.DataPointer, _buffer, 0, _buffer.Length);
            return;
        }

        var src = map.DataPointer;
        for (var y = 0; y < _height; y++)
        {
            Marshal.Copy(src, _buffer, y * rowBytes, rowBytes);
            src += pitch;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _session?.Dispose();
        _pool?.Dispose();
        _staging?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
    }
}
