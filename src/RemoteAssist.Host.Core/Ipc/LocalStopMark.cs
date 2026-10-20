using System.Diagnostics;
using System.Text.Json;

namespace RemoteAssist.Host.Core.Ipc;

/// <summary>Diagnostics only. An invalid mark must never prevent a local stop.</summary>
public sealed record LocalStopMark(string RequestId,long RequestedTick,long Frequency)
{
    public static LocalStopMark Create()=>new(Guid.NewGuid().ToString("N"),Stopwatch.GetTimestamp(),Stopwatch.Frequency);
    public static LocalStopMark? Read(LocalIpcMessage message)
    {
        try
        {
            if(message.Body is not {} body||body.ValueKind!=JsonValueKind.Object||body.EnumerateObject().Count()!=3)return null;
            var id=body.GetProperty("requestId").GetString();
            var tick=body.GetProperty("requestedTick").GetInt64();var frequency=body.GetProperty("frequency").GetInt64();
            var now=Stopwatch.GetTimestamp();
            if(!Guid.TryParseExact(id,"N",out _)||frequency!=Stopwatch.Frequency||tick<=0||tick>now||Stopwatch.GetElapsedTime(tick,now)>TimeSpan.FromMinutes(15))return null;
            return new(id!,tick,frequency);
        }
        catch(Exception ex)when(ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException){return null;}
    }
}
