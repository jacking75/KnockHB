namespace RemoteAssist.Host.Core.Capture;

/// <summary>
/// 화면 캡처 추상화. Windows Graphics Capture를 기본으로 두고,
/// 미지원 시 DXGI Desktop Duplication을 선택한다 — docs/technical-specification.md 참고.
///
/// UAC 보안 데스크톱(Winlogon) 대응은 대상 사용자 세션의 승인된 특권 실행 경로가 필요하다.
/// Phase 5(T5.1)에서 다룬다. 그 전까지 승격 창은 검은 화면이거나 캡처가 끊기는 것이 정상이다.
/// </summary>
public interface IScreenCapture : IDisposable
{
    /// <summary>캡처 중인 모니터.</summary>
    MonitorInfo Monitor { get; }

    /// <summary>
    /// 새 프레임을 가져온다. timeout 안에 새 프레임이 없으면 false.
    ///
    /// 화면이 정적이면 false가 계속 돌아오는 것이 정상이다. DXGI는 변경이 있을 때만
    /// 프레임을 준다. 인코더는 이때 직전 프레임을 유지하면 된다.
    /// </summary>
    bool TryGetFrame(TimeSpan timeout, out CapturedFrame frame);
}

/// <summary>
/// 캡처된 한 장. 픽셀은 BGRA 8888.
///
/// <para>
/// <b>주의:</b> <see cref="Bgra"/> 는 캡처 객체가 재사용하는 내부 버퍼를 가리킨다.
/// 다음 <see cref="IScreenCapture.TryGetFrame"/> 호출 전에 소비하거나 복사해야 한다.
/// 프레임마다 버퍼를 새로 잡으면 1080p 기준 초당 240MB를 할당하게 되므로 재사용한다.
/// </para>
/// </summary>
public readonly struct CapturedFrame
{
    public CapturedFrame(
        ReadOnlyMemory<byte> bgra, int width, int height, int stride,
        int accumulatedFrames, int dirtyRectCount, Rect dirtyBounds,
        string monitorIdentity = "", long generation = 1)
    {
        Bgra = bgra;
        Width = width;
        Height = height;
        Stride = stride;
        AccumulatedFrames = accumulatedFrames;
        DirtyRectCount = dirtyRectCount;
        DirtyBounds = dirtyBounds;
        MonitorIdentity=monitorIdentity;Generation=generation;
    }

    public ReadOnlyMemory<byte> Bgra { get; }
    public string MonitorIdentity {get;}
    public long Generation {get;}
    public int Width { get; }
    public int Height { get; }

    /// <summary>한 줄의 바이트 수. <c>Width * 4</c> 와 다를 수 있다.</summary>
    public int Stride { get; }

    /// <summary>이 프레임에 합쳐진 화면 갱신 횟수. 0이면 마우스 커서만 움직인 것이다.</summary>
    public int AccumulatedFrames { get; }

    /// <summary>변경 영역 개수. 지금은 로깅용이고, 대역폭 최적화는 T6.3에서 쓴다.</summary>
    public int DirtyRectCount { get; }

    /// <summary>변경 영역 전체를 감싸는 사각형. 정적 화면에서 얼마나 작은지 보려는 것이다.</summary>
    public Rect DirtyBounds { get; }
}

/// <summary>모니터 정보. 좌표는 가상 데스크톱 기준 물리 픽셀이다.</summary>
public readonly record struct MonitorInfo(
    int Index,
    string DeviceName,
    int X,
    int Y,
    int Width,
    int Height,
    bool IsPrimary,
    nint Handle = 0)
{
    public override string ToString() =>
        $"[{Index}] {DeviceName} {Width}x{Height} @ ({X},{Y}){(IsPrimary ? " *주*" : "")}";
}

/// <summary>사각형. 외부 그래픽 타입에 의존하지 않기 위해 직접 둔다.</summary>
public readonly record struct Rect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsEmpty => Right <= Left || Bottom <= Top;
    public long Area => IsEmpty ? 0 : (long)Width * Height;

    public static readonly Rect Empty = new(0, 0, 0, 0);

    public Rect Union(Rect other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;
        return new Rect(
            Math.Min(Left, other.Left),
            Math.Min(Top, other.Top),
            Math.Max(Right, other.Right),
            Math.Max(Bottom, other.Bottom));
    }
}
