namespace RemoteAssist.Host.Core.Media;

/// <summary>90kHz sample timestamp from a monotonic clock, independent of frame skips and encoder PTS.</summary>
public sealed class FrameRtpClock(uint seed,TimeProvider? time=null)
{
    readonly TimeProvider clock=time??TimeProvider.System;
    readonly long origin=(time??TimeProvider.System).GetTimestamp();
    public uint Timestamp=>unchecked(seed+(uint)(ulong)Math.Round(clock.GetElapsedTime(origin,clock.GetTimestamp()).TotalSeconds*90000));
}
