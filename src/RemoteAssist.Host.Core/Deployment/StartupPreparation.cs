using System.Diagnostics;
using System.Text.Json;
using RemoteAssist.Host.Core.Windows;

namespace RemoteAssist.Host.Core.Deployment;

/// <summary>Read-only startup gate. Only the elevated installer may change prerequisite rules.</summary>
public static class StartupPreparation
{
    public static async Task VerifyAsync(CancellationToken cancellation)
    {
        WriteStatus("checking", "installation-and-firewall", null);
        try
        {
            foreach (var role in new[] { "Service", "Tray", "Helper", "AdminHelper", "RunbookWorker" })
                _ = ProtectedInstallation.ResolveExecutable($"RemoteAssist.Host.{role}.exe");
            var script = Path.Combine(AppContext.BaseDirectory, "prepare-host.ps1");
            InstallationState.DemandProtected(script);
            if (!InstallationState.Development)
            {
                InstallationState.DemandProtected(Path.Combine(AppContext.BaseDirectory, "release.json"));
                InstallationState.CheckDirectory(AppContext.BaseDirectory);
            }
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Action", "Verify", "-InstallationRoot", Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory) })
                start.ArgumentList.Add(argument);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            using var process = Process.Start(start) ?? throw new IOException("시작 준비 검사 프로세스를 실행하지 못했다.");
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var error = process.StandardError.ReadToEndAsync(deadline.Token);
            try
            {
                await process.WaitForExitAsync(deadline.Token);
                var stdout = await output;
                var stderr = await error;
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("방화벽 준비 검사 실패. 최초 관리자 실행에서 준비를 완료해야 한다. " + stderr[..Math.Min(stderr.Length, 1024)]);
                using var result = JsonDocument.Parse(stdout);
                if (result.RootElement.GetProperty("state").GetString() != "PREREQUISITES_VERIFIED_NOT_FUNCTION_PASS")
                    throw new InvalidDataException("시작 준비 검사 결과가 일치하지 않는다.");
            }
            catch
            {
                if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)); }
                throw;
            }
            WriteStatus("checking", "interactive-session", null);
        }
        catch (Exception error) when (InstallationState.Development && !cancellation.IsCancellationRequested)
        {
            // 개발 실행은 방화벽 준비가 없어도 시작한다. 직접 연결이 막히면 prepare-host.ps1로 준비한다.
            WriteStatus("warning", "installation-and-firewall", error.Message);
        }
        catch (Exception error)
        {
            WriteStatus("failed", "installation-and-firewall", error.Message);
            throw;
        }
    }

    public static void WriteStatus(string state, string stage, string? detail)
        => InstallationState.Atomic(Path.Combine(InstallationState.Data, "startup-preparation.json"), JsonSerializer.SerializeToUtf8Bytes(new
        { state, stage, detail, pid = Environment.ProcessId, image = Environment.ProcessPath, utc = DateTimeOffset.UtcNow }));
}
