using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using DesktopTestDriver;

namespace DesktopTestDriver.RemoteAssist;

public sealed record InputBaseline(long StartTick, int Events, int Clicks, int Drag, int Wheel);
public sealed record InputExpectation(WindowIdentity Window, int Session, InputBaseline Baseline, string TextHash, bool HostInputSealed, bool ExpectRemote);
public static class ProductObservations
{
    public static JsonElement ReadPrivateJson(string path)
    {
        path = Path.GetFullPath(path); RunStore.CheckPrivate(Path.GetDirectoryName(path)!);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > Wire.MaxMessage) throw new DriverException(Error.ObservationUnavailable);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8); return Wire.Decode<JsonElement>(reader.ReadToEnd());
    }
    public static Dictionary<string, string> InputTarget(JsonElement sample, InputExpectation expected, bool simulation)
    {
        if (sample.GetProperty("pid").GetInt32() != expected.Window.Pid || sample.GetProperty("session").GetInt32() != expected.Session ||
            sample.GetProperty("window").GetInt64() != expected.Window.Hwnd || !string.Equals(sample.GetProperty("image").GetString(), expected.Window.ImagePath, StringComparison.OrdinalIgnoreCase) ||
            sample.GetProperty("startTick").GetInt64() != expected.Baseline.StartTick || !sample.GetProperty("foreground").GetBoolean() ||
            !sample.GetProperty("observationValid").GetBoolean() || sample.GetProperty("observationOverflow").GetBoolean() ||
            DateTimeOffset.UtcNow - sample.GetProperty("utc").GetDateTimeOffset() > TimeSpan.FromSeconds(2) || sample.GetProperty("utc").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddSeconds(1) ||
            !simulation && !Processes.Alive(new(expected.Window.Pid, expected.Window.CreatedAt, expected.Window.ImagePath, expected.Window.ImageHash)) ||
            expected.ExpectRemote && !expected.HostInputSealed) throw new DriverException(Error.ObservationUnavailable);
        var events = sample.GetProperty("events").EnumerateArray().ToArray();
        if (events.Length < expected.Baseline.Events) throw new DriverException(Error.ObservationUnavailable);
        var held = new HashSet<(string Kind, int Code)>(); long previous = expected.Baseline.StartTick;
        foreach (var e in events.Skip(expected.Baseline.Events))
        {
            var tick = e.GetProperty("tick").GetInt64();
            if (tick < previous) throw new DriverException(Error.ObservationUnavailable); previous = tick;
            var key = (e.GetProperty("kind").GetString()!, e.GetProperty("code").GetInt32()); var action = e.GetProperty("action").GetString();
            if (action == "down") held.Add(key);
            else if (action == "up") held.Remove(key);
            else throw new DriverException(Error.ObservationUnavailable);
        }
        int clicks = sample.GetProperty("clicks").GetInt32() - expected.Baseline.Clicks, drag = sample.GetProperty("drag").GetInt32() - expected.Baseline.Drag, wheel = sample.GetProperty("wheel").GetInt32() - expected.Baseline.Wheel;
        if (clicks < 0 || drag < 0 || wheel < 0) throw new DriverException(Error.ObservationUnavailable);
        return new() { ["events"] = (events.Length - expected.Baseline.Events).ToString(CultureInfo.InvariantCulture), ["clicks"] = clicks.ToString(CultureInfo.InvariantCulture),
            ["drag"] = drag.ToString(CultureInfo.InvariantCulture), ["wheel"] = wheel.ToString(CultureInfo.InvariantCulture),
            ["keys-released"] = (held.Count == 0 && sample.GetProperty("heldVirtualKeys").GetArrayLength() == 0).ToString().ToLowerInvariant(),
            ["text-matches"] = (Wire.Hash(Encoding.UTF8.GetBytes(sample.GetProperty("text").GetString()!)) == expected.TextHash).ToString().ToLowerInvariant(),
            ["host-input-sealed"] = expected.HostInputSealed.ToString().ToLowerInvariant() };
    }
    public static Dictionary<string, string> Video(JsonElement before, JsonElement after, double elapsedSeconds, bool markerMatches, int width, int height)
    {
        int decoded = after.GetProperty("decodedFrames").GetInt32() - before.GetProperty("decodedFrames").GetInt32();
        int dropped = after.GetProperty("droppedFrames").GetInt32() - before.GetProperty("droppedFrames").GetInt32();
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0 || decoded < 0 || dropped < 0) throw new DriverException(Error.ObservationUnavailable);
        return new() { ["decoded-delta"] = decoded.ToString(CultureInfo.InvariantCulture), ["measured-fps"] = (decoded / elapsedSeconds).ToString("R", CultureInfo.InvariantCulture),
            ["dropped-delta"] = dropped.ToString(CultureInfo.InvariantCulture), ["marker-matches"] = markerMatches.ToString().ToLowerInvariant(),
            ["monitor-matches"] = (after.GetProperty("videoWidth").GetInt32() == width && after.GetProperty("videoHeight").GetInt32() == height).ToString().ToLowerInvariant() };
    }
    public static Dictionary<string, string> FileTransfer(string sandbox, string source, string destination, string expectedHash)
    {
        sandbox = Path.GetFullPath(sandbox); RunStore.CheckPrivate(sandbox);
        foreach (var path in new[] { source, destination })
        {
            var full = Path.GetFullPath(path);
            if (!full.StartsWith(sandbox + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(full) ||
                new FileInfo(full).Length > 67_108_864) throw new DriverException(Error.DelegationMismatch);
            for (var info = new FileInfo(full) as FileSystemInfo; info != null; info = info is FileInfo file ? file.Directory : ((DirectoryInfo)info).Parent)
            {
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new DriverException(Error.DelegationMismatch);
                if (info.FullName.Equals(sandbox, StringComparison.OrdinalIgnoreCase)) break;
            }
        }
        return new() { ["file-hash-matches"] = (RunConfig.IsHash(expectedHash) && Wire.FileHash(source) == expectedHash && Wire.FileHash(destination) == expectedHash).ToString().ToLowerInvariant() };
    }
    public static Dictionary<string, string> Chat(string observed, string expected)
    {
        if (expected.Length is < 1 or > 4096 || observed.Length > 1_048_576) throw new DriverException(Error.InvalidRequest);
        int count = 0, start = 0; while ((start = observed.IndexOf(expected, start, StringComparison.Ordinal)) >= 0) { count++; start += expected.Length; }
        return new() { ["chat-count"] = count.ToString(CultureInfo.InvariantCulture), ["chat-hash"] = Wire.Hash(Encoding.UTF8.GetBytes(expected)) };
    }
    public static Dictionary<string, string> Audit(JsonElement[] entries, string session, string connection, string kind, string result)
    {
        var matches = entries.Where(e => e.GetProperty("SessionId").GetString() == session && e.GetProperty("ConnectionId").GetString() == connection &&
            e.GetProperty("Kind").ToString() == kind && e.GetProperty("Result").ToString() == result).ToArray();
        return new() { ["audit-matches"] = matches.Length.ToString(CultureInfo.InvariantCulture) };
    }
    public static ObservationRecord Record(ScenarioPlan plan, ScenarioStep step, ScenarioContext context, string role, string source, string hash, Dictionary<string, string> facts) =>
        new(context.RunId, plan.Id, step.Id, context.Machine(role).MachineId, role, source, context.CandidateHash, context.ToolHash, context.SettingsHash,
            hash, Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow, context.Simulation, facts);
}
