namespace RemoteAssist.Host.Core.Security;

[Flags]
public enum Capability { None=0, Screen=1, Control=2, Admin=4, File=8, Clipboard=16, Diagnostics=32, Runbook=64, Chat=128, All=255 }
public sealed record SupporterIdentity(string ConnectionId, string DisplayName);
public sealed record PermissionSnapshot(string SessionId, string ConnectionId, long Generation, Capability Capabilities);

/// <summary>
/// 지원자와 대상 PC는 서로 신뢰한다. 연결된 지원자는 항상 전 권한을 받는다.
/// 이 게이트는 권한을 고르지 않고, 세션이 끝난 뒤의 입력·송신만 막는다.
/// </summary>
public sealed class PermissionGate
{
    private readonly object _gate = new();
    private PermissionSnapshot? _grant;
    private bool _ended;

    public PermissionSnapshot Grant(string sessionId, string connectionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(connectionId)) throw new ArgumentException("세션과 지원자 연결 ID가 필요하다.");
        lock (_gate)
        {
            if (_ended) throw new InvalidOperationException("종료한 세션이다.");
            if (_grant is not null) throw new InvalidOperationException("한 세션은 한 지원자만 연결한다.");
            return _grant = new(sessionId, connectionId, 1, Capability.All);
        }
    }
    public PermissionSnapshot? Current { get { lock (_gate) return _grant; } }
    public bool Allows(Capability capability, long generation)
    {
        lock (_gate) return !_ended && _grant is { } grant && generation == grant.Generation && capability != Capability.None;
    }
    /// <summary>세션 종료와 경합하지 않도록 동작을 게이트 안에서 실행한다.</summary>
    public bool Execute(Capability capability, long generation, Action action)
    {
        lock (_gate)
        {
            if (!Allows(capability, generation)) return false;
            action(); return true;
        }
    }
    public void End() { lock (_gate) { _ended = true; _grant = null; } }
}
