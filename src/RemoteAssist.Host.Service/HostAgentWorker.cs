namespace RemoteAssist.Host.Service;

/// <summary>
/// Session 0 서비스의 진입점이다. 캡처·인코딩은 별도 사용자 세션 헬퍼에서 실행한다.
/// </summary>
public sealed class HostAgentWorker : BackgroundService
{
    private readonly ILogger<HostAgentWorker> _log;
    private readonly IHostApplicationLifetime _lifetime;

    public HostAgentWorker(ILogger<HostAgentWorker> log,IHostApplicationLifetime lifetime) { _log=log;_lifetime=lifetime; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await new HostSessionSupervisor(_log,_lifetime).RunAsync(stoppingToken);
    }
}
