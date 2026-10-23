namespace RemoteAssist.Protocol;

/// <summary>지원자는 항상 전 권한을 받는다. 권한 목록과 채널 목록은 고정이다.</summary>
public static class FeatureContract
{
    public static IReadOnlyList<string> Permissions {get;}=Array.AsReadOnly(new[]{"screen","control","admin","file","clipboard","diagnostics","runbook","chat"});
    public static IReadOnlyList<string> Channels {get;}=Array.AsReadOnly(new[]{"control","input","pointer","file","chat","telemetry","runbook"});
}
