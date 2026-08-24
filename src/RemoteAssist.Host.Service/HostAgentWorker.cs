namespace RemoteAssist.Host.Service;

/// <summary>
/// Host 서비스의 진입점. 현재는 골격만 있다(T0.3).
///
/// 앞으로 여기에 붙는 것들:
///   T1.2  화면 캡처(DXGI)
///   T1.3  FFmpeg H.264 인코딩
///   T1.4  SIPSorcery PeerConnection 송신
///   T2.3  SendInput 입력 주입
///   T2.4  트레이 UI와의 Named Pipe IPC
///   T5.1  데스크톱 전환(UAC/보안 데스크톱)
/// </summary>
public sealed class HostAgentWorker : BackgroundService
{
    private readonly ILogger<HostAgentWorker> _log;

    public HostAgentWorker(ILogger<HostAgentWorker> log) => _log = log;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("RemoteAssist Host 서비스 시작 (골격)");

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
