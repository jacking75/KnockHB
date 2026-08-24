using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace RemoteAssist.Host.Core.Capture;

/// <summary>
/// DXGI Desktop Duplication 기반 화면 캡처.
///
/// <para>
/// 다중 GPU 노트북(내장+외장)에서는 <b>출력을 소유한 어댑터의 디바이스로만</b> 복제할 수 있다.
/// 그래서 어댑터를 먼저 고르고 그 위에 D3D11 디바이스를 만든다. 아무 디바이스나 쓰면
/// <c>DuplicateOutput</c> 이 실패한다.
/// </para>
///
/// <para>
/// <b>스레드 안전하지 않다.</b> 한 인스턴스는 한 스레드에서만 쓴다.
/// </para>
/// </summary>
public sealed class DxgiScreenCapture : IScreenCapture
{
    // DXGI HRESULT. Vortice의 상수 이름에 기대지 않고 값으로 직접 비교한다.
    private const int DxgiErrorWaitTimeout = unchecked((int)0x887A0027);
    private const int DxgiErrorAccessLost = unchecked((int)0x887A0026);

    private readonly ILogger _log;
    private readonly int _monitorIndex;

    private IDXGIAdapter1 _adapter = null!;
    private IDXGIOutput1 _output = null!;
    private ID3D11Device _device = null!;
    private ID3D11DeviceContext _context = null!;
    private ID3D11Texture2D _staging = null!;
    private IDXGIOutputDuplication? _duplication;

    private byte[] _buffer = [];
    private RawRect[] _dirtyRects = new RawRect[64];
    private bool _disposed;

    public MonitorInfo Monitor { get; private set; }

    public DxgiScreenCapture(int monitorIndex = 0, ILogger<DxgiScreenCapture>? log = null)
    {
        _log = (ILogger?)log ?? NullLogger.Instance;
        _monitorIndex = monitorIndex;
        Initialize();
    }

    /// <summary>연결된 모니터를 나열한다. 인덱스는 캡처 생성자에 넘기는 값과 같다.</summary>
    public static IReadOnlyList<MonitorInfo> EnumerateMonitors()
    {
        var result = new List<MonitorInfo>();
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        var index = 0;
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            using (adapter)
            {
                for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                {
                    using (output)
                    {
                        var desc = output.Description;
                        var r = desc.DesktopCoordinates;
                        result.Add(new MonitorInfo(
                            Index: index++,
                            DeviceName: desc.DeviceName,
                            X: r.Left,
                            Y: r.Top,
                            Width: r.Right - r.Left,
                            Height: r.Bottom - r.Top,
                            // 가상 데스크톱 원점에 있는 것이 주 모니터다.
                            IsPrimary: r.Left == 0 && r.Top == 0,
                            Handle: desc.Monitor));
                    }
                }
            }
        }

        return result;
    }

    private void Initialize()
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        // 요청한 인덱스의 출력과 그 출력을 소유한 어댑터를 함께 찾는다.
        var index = 0;
        IDXGIAdapter1? foundAdapter = null;
        IDXGIOutput? foundOutput = null;

        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            var keepAdapter = false;
            for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
            {
                if (index++ == _monitorIndex)
                {
                    foundAdapter = adapter;
                    foundOutput = output;
                    keepAdapter = true;
                    break;
                }
                output.Dispose();
            }

            if (keepAdapter) break;
            adapter.Dispose();
        }

        if (foundAdapter is null || foundOutput is null)
        {
            throw new ArgumentOutOfRangeException(
                nameof(_monitorIndex),
                $"모니터 {_monitorIndex} 를 찾을 수 없습니다. 연결된 모니터: {EnumerateMonitors().Count}개");
        }

        _adapter = foundAdapter;

        var outDesc = foundOutput.Description;
        var rect = outDesc.DesktopCoordinates;
        Monitor = new MonitorInfo(
            _monitorIndex, outDesc.DeviceName,
            rect.Left, rect.Top,
            rect.Right - rect.Left, rect.Bottom - rect.Top,
            rect.Left == 0 && rect.Top == 0,
            outDesc.Monitor);

        _output = foundOutput.QueryInterface<IDXGIOutput1>();
        foundOutput.Dispose();

        // DriverType은 반드시 Unknown이어야 한다. 어댑터를 명시했을 때의 규칙이다.
        // BgraSupport는 B8G8R8A8 스테이징 텍스처를 쓰기 위해 필요하다.
        FeatureLevel[] levels =
        [
            FeatureLevel.Level_11_1, FeatureLevel.Level_11_0,
            FeatureLevel.Level_10_1, FeatureLevel.Level_10_0,
        ];

        D3D11.D3D11CreateDevice(
            _adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, levels,
            out _device!, out _context!).CheckError();

        _staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Monitor.Width,
            Height = (uint)Monitor.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        });

        _buffer = new byte[(long)Monitor.Width * Monitor.Height * 4];

        _log.LogInformation("DXGI 캡처 초기화: {Monitor} (어댑터 {Adapter})",
            Monitor, _adapter.Description1.Description);

        StartDuplication();
    }

    private void StartDuplication()
    {
        _duplication?.Dispose();
        _duplication = _output.DuplicateOutput(_device);
    }

    public bool TryGetFrame(TimeSpan timeout, out CapturedFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        frame = default;

        if (_duplication is null)
        {
            // 직전 호출에서 접근 권한을 잃었다. 다시 잡아 본다.
            StartDuplication();
        }

        var timeoutMs = (uint)Math.Clamp(timeout.TotalMilliseconds, 0, uint.MaxValue);
        var result = _duplication!.AcquireNextFrame(timeoutMs, out var info, out var resource);

        if (result.Code == DxgiErrorWaitTimeout)
        {
            // 화면이 그대로다. 오류가 아니다.
            return false;
        }

        if (result.Code == DxgiErrorAccessLost)
        {
            // 해상도 변경, 전체화면 전환, 데스크톱 전환(UAC 보안 데스크톱 포함)에서 발생한다.
            // 복제를 다시 만들면 대개 회복된다. UAC 승격 창 자체는 T5.1 전까지 보이지 않는다.
            _log.LogWarning("DXGI 복제 접근 권한 상실 — 재시도한다 (해상도 변경·데스크톱 전환 등)");
            _duplication?.Dispose();
            _duplication = null;
            return false;
        }

        result.CheckError();

        try
        {
            using var texture = resource.QueryInterface<ID3D11Texture2D>();

            // GPU 텍스처를 CPU가 읽을 수 있는 스테이징으로 복사한다.
            // T1.3b에서 HW 인코더에 직접 넘기게 되면 이 왕복이 사라진다.
            _context.CopyResource(_staging, texture);

            var map = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                CopyToBuffer(map);
            }
            finally
            {
                _context.Unmap(_staging, 0);
            }

            var (count, bounds) = ReadDirtyRects(info);

            frame = new CapturedFrame(
                _buffer, Monitor.Width, Monitor.Height, Monitor.Width * 4,
                (int)info.AccumulatedFrames, count, bounds);
            return true;
        }
        finally
        {
            resource.Dispose();
            _duplication.ReleaseFrame();
        }
    }

    /// <summary>매핑된 스테이징 텍스처를 관리 배열로 옮긴다. RowPitch가 폭과 다를 수 있다.</summary>
    private void CopyToBuffer(MappedSubresource map)
    {
        var rowBytes = Monitor.Width * 4;
        var pitch = (int)map.RowPitch;

        if (pitch == rowBytes)
        {
            Marshal.Copy(map.DataPointer, _buffer, 0, _buffer.Length);
            return;
        }

        // 패딩이 있으면 줄 단위로 옮긴다.
        var src = map.DataPointer;
        for (var y = 0; y < Monitor.Height; y++)
        {
            Marshal.Copy(src, _buffer, y * rowBytes, rowBytes);
            src += pitch;
        }
    }

    /// <summary>
    /// 변경 영역을 읽어 개수와 전체 경계를 돌려준다.
    ///
    /// 지금은 로깅용이다. 정적인 트러블슈팅 화면에서 실제로 얼마나 작은 영역만
    /// 바뀌는지 수치로 확인해 두면, T6.3에서 대역폭 최적화를 할 때 근거가 된다.
    /// </summary>
    private (int Count, Rect Bounds) ReadDirtyRects(OutduplFrameInfo info)
    {
        if (info.TotalMetadataBufferSize == 0)
        {
            return (0, Rect.Empty);
        }

        var rectSize = Marshal.SizeOf<RawRect>();
        var needed = (int)info.TotalMetadataBufferSize / rectSize + 1;
        if (_dirtyRects.Length < needed)
        {
            _dirtyRects = new RawRect[needed];
        }

        var bufferBytes = (uint)(_dirtyRects.Length * rectSize);
        var result = _duplication!.GetFrameDirtyRects(bufferBytes, _dirtyRects, out var usedBytes);
        if (result.Failure)
        {
            return (0, Rect.Empty);
        }

        var count = (int)(usedBytes / rectSize);
        var bounds = Rect.Empty;
        for (var i = 0; i < count; i++)
        {
            var r = _dirtyRects[i];
            bounds = bounds.Union(new Rect(r.Left, r.Top, r.Right, r.Bottom));
        }

        return (count, bounds);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _duplication?.Dispose();
        _staging?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
        _output?.Dispose();
        _adapter?.Dispose();
    }
}
