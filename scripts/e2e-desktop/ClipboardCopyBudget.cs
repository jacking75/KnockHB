using System.Diagnostics;
using System.IO;

namespace RemoteAssist.E2e;

internal sealed class ClipboardCopyBudget(ulong maxBytes=64*1024*1024,int maxFormats=256,int maxDepth=16,
    Func<long>? clock=null,long? deadline=null)
{
    readonly Func<long> clock=clock??Stopwatch.GetTimestamp;
    readonly long end=deadline??checked(Stopwatch.GetTimestamp()+Stopwatch.Frequency*10);
    ulong bytes;
    int formats,elements;
    public int MaxDepth => maxDepth;
    public void CheckTime() { if(clock()>=end)throw new TimeoutException("clipboard-copy-deadline"); }
    public void ReserveBytes(ulong amount) { CheckTime();bytes=checked(bytes+amount);if(bytes>maxBytes)throw new InvalidDataException("clipboard-private-memory-bound"); }
    public void ReserveFormat() { CheckTime();if(++formats>maxFormats)throw new InvalidDataException("clipboard-format-bound"); }
    public void ReserveElement() { CheckTime();if(++elements>4096)throw new InvalidDataException("clipboard-storage-elements"); }
}
