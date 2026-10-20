using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using RemoteAssist.Host.Core.Windows;
using RemoteAssist.Protocol;
namespace RemoteAssist.Host.Core.Runbooks;

public sealed record RunbookConfiguration(RunbookMessage Request,DateTimeOffset ExpiresAt,uint WindowsSessionId);
public static class RunbookExecution
{
    public static async Task<RunbookResult> Execute(RunbookConfiguration config,CancellationToken ct)
    {
        var book=RunbookCatalog.Demand(config.Request);using var identity=WindowsIdentity.GetCurrent();
        if(Process.GetCurrentProcess().SessionId!=config.WindowsSessionId||config.ExpiresAt<=DateTimeOffset.UtcNow||config.ExpiresAt>DateTimeOffset.UtcNow.AddSeconds(21)||identity.IsSystem!=book.RequiresAdmin)throw new UnauthorizedAccessException("Runbook 실행 권한/기한 오류다.");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(config.ExpiresAt-DateTimeOffset.UtcNow);
        var exe=ProtectedInstallation.ResolveExecutable("RemoteAssist.Host.RunbookWorker.exe");var script=Path.Combine(Path.GetDirectoryName(exe)!,"runbooks",book.BookId+".ps1");
        var info=new FileInfo(script);
        if(info.Length>32768||info.Attributes.HasFlag(FileAttributes.ReparsePoint)||RunbookCatalog.Hash(await File.ReadAllTextAsync(script,deadline.Token))!=book.Hash)throw new UnauthorizedAccessException("설치 Runbook이 승인 내용과 다르다.");
        var engine=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32","WindowsPowerShell","v1.0","powershell.exe");
        var start=new ProcessStartInfo(engine){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
        foreach(var arg in new[]{"-NoLogo","-NoProfile","-NonInteractive","-ExecutionPolicy","RemoteSigned","-File",script,"-detail",config.Request.Args!["detail"]})start.ArgumentList.Add(arg);
        deadline.Token.ThrowIfCancellationRequested();
        if(config.ExpiresAt<=DateTimeOffset.UtcNow)throw new UnauthorizedAccessException("Runbook 실행 준비 중 승인이 만료됐다.");
        using var process=Process.Start(start)??throw new IOException("Runbook engine 시작 실패다.");
        int overflow=0;
        try
        {
            Action breached=()=>{Interlocked.Exchange(ref overflow,1);deadline.Cancel();};
            var stdout=ReadBounded(process.StandardOutput,deadline.Token,breached);var stderr=ReadBounded(process.StandardError,deadline.Token,breached);
            await Task.WhenAll(stdout,stderr,process.WaitForExitAsync(deadline.Token)).WaitAsync(deadline.Token);
            var result=new RunbookResult(process.ExitCode==0?"completed":"failed",process.ExitCode,await stdout,(await stderr).Length>0?"실행 엔진 오류다.":"",book.Impact);result.Validate();return result;
        }
        catch(OperationCanceledException){if(!process.HasExited)process.Kill(true);return new(Volatile.Read(ref overflow)!=0?"failed":ct.IsCancellationRequested?"cancelled":"timed-out",null,"",Volatile.Read(ref overflow)!=0?"작업 출력 상한이다.":"작업을 중단했다.",book.Impact);}
        catch{if(!process.HasExited)process.Kill(true);return new("failed",null,"","작업 출력 상한 또는 실행 오류다.",book.Impact);}
    }
    static async Task<string> ReadBounded(StreamReader reader,CancellationToken ct,Action breached)
    {var text=new StringBuilder();var buffer=new char[512];while(true){var count=await reader.ReadAsync(buffer,ct);if(count==0)return text.ToString();if(text.Length+count>4096){breached();throw new IOException("Runbook 출력 상한이다.");}text.Append(buffer,0,count);}}
}
