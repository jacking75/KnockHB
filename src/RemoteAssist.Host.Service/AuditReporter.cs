using System.Net.Http.Headers;
using System.Text.Json;
using RemoteAssist.Host.Core.Deployment;
namespace RemoteAssist.Host.Service;

internal sealed class AuditReporter(ILogger<AuditReporter> log):BackgroundService
{
    record Configuration(string Endpoint,string CredentialFile);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings=Path.Combine(InstallationState.Data,"audit.settings.json");
        if(!File.Exists(settings)){log.LogWarning("Host 감사 보고 설정이 없어 spool을 보존한다.");return;}
        try
        {
            InstallationState.DemandProtected(settings);if(new FileInfo(settings).Length>4096)throw new InvalidDataException();var config=JsonSerializer.Deserialize<Configuration>(await File.ReadAllBytesAsync(settings,stoppingToken))??throw new InvalidDataException();
            if(!Uri.TryCreate(config.Endpoint,UriKind.Absolute,out var endpoint)||endpoint.Scheme!="https"||endpoint.UserInfo.Length!=0||endpoint.Fragment.Length!=0||endpoint.Query.Length!=0)throw new InvalidDataException();
            InstallationState.DemandProtected(config.CredentialFile);if(new FileInfo(config.CredentialFile).Length>256)throw new InvalidDataException();var key=(await File.ReadAllTextAsync(config.CredentialFile,stoppingToken)).Trim();if(key.Length is <32 or >128||!key.All(c=>char.IsAsciiLetterOrDigit(c)||c is '-' or '_'))throw new InvalidDataException();
            using var http=new HttpClient(new HttpClientHandler {AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(10)};http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",key);
            using var timer=new PeriodicTimer(TimeSpan.FromSeconds(5));
            do
            {
                var spool=Path.Combine(InstallationState.Data,"audit","pending");if(!Directory.Exists(spool))continue;InstallationState.DemandProtected(spool);
                foreach(var file in Directory.EnumerateFiles(spool,"*.json").Take(32))
                {
                    stoppingToken.ThrowIfCancellationRequested();InstallationState.DemandProtected(file);if(new FileInfo(file).Length>4096)throw new InvalidDataException();var bytes=await File.ReadAllBytesAsync(file,stoppingToken);
                    try{using var content=new ByteArrayContent(bytes);content.Headers.ContentType=new MediaTypeHeaderValue("application/json");using var response=await http.PostAsync(endpoint,content,stoppingToken);if(response.StatusCode!=System.Net.HttpStatusCode.NoContent)throw new HttpRequestException("감사 durable ACK가 아니다.");RemoteAssist.Host.Core.Security.AuditJournal.OpenProtected().Acknowledge(JsonSerializer.Deserialize<RemoteAssist.Host.Core.Security.AuditEvent>(bytes)??throw new InvalidDataException());}catch(HttpRequestException){break;}catch(TaskCanceledException)when(!stoppingToken.IsCancellationRequested){break;}
                }
            }while(await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){}
        catch(Exception ex){log.LogError("감사 보고 중단; spool 보존: {Kind}",ex.GetType().Name);}
    }
}
