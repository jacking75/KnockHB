namespace RemoteAssist.Host.Core.Security;

[Flags]
public enum Capability { None=0, Screen=1, Control=2, Admin=4, File=8, Clipboard=16, Diagnostics=32, Runbook=64 }
public sealed record SupporterIdentity(string ConnectionId, string DisplayName);
public sealed record PermissionSnapshot(string SessionId, string ConnectionId, long Generation,
    Capability Capabilities, DateTimeOffset ExpiresAt);

/// <summary>로컬 승인만 기록한다. Viewer DTO를 이 객체로 역직렬화하지 않는다.</summary>
public sealed class PermissionGate(TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private PermissionSnapshot? _grant;
    private long _generation;
    private bool _ended;
    private string? _sessionId, _connectionId;

    public PermissionSnapshot Grant(string sessionId, string connectionId, Capability capabilities, TimeSpan lease)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(connectionId)) throw new ArgumentException("승인 주체가 필요하다.");
        if (lease <= TimeSpan.Zero || lease > TimeSpan.FromHours(8)) throw new ArgumentOutOfRangeException(nameof(lease));
        if ((capabilities & ~((Capability)127)) != 0) throw new ArgumentException("알 수 없는 권한이다.");
        if (capabilities.HasFlag(Capability.Control) && !capabilities.HasFlag(Capability.Screen)) throw new ArgumentException("제어에는 화면 승인이 필요하다.");
        lock (_gate)
        {
            if (_ended) throw new InvalidOperationException("종료한 세션은 다시 승인할 수 없다.");
            if (_sessionId is not null && (_sessionId != sessionId || _connectionId != connectionId))
                throw new InvalidOperationException("다른 지원자가 기존 승인을 승계할 수 없다.");
            _sessionId = sessionId; _connectionId = connectionId;
            return _grant = new(sessionId, connectionId, ++_generation, capabilities, _time.GetUtcNow().Add(lease));
        }
    }
    public PermissionSnapshot? Current { get { lock (_gate) return _grant; } }
    public bool Allows(Capability capability, long generation)
    {
        lock (_gate) return !_ended && _grant is { } grant && generation == grant.Generation &&
            _time.GetUtcNow() < grant.ExpiresAt && (grant.Capabilities & capability) == capability && capability != Capability.None;
    }
    public void End() { lock (_gate) { _ended = true; _grant = null; ++_generation; } }
    public void Revoke() { lock (_gate) { _grant = null; ++_generation; } }
}
