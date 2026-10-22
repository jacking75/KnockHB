using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using RemoteAssist.Host.Core.Deployment;

/// <summary>Interactive entry point for the explicitly owned, protected unsigned development install.</summary>
internal static class DevSupportLauncher
{
    static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RemoteAssist-Dev");
    static string Image => Path.Combine(Root, "RemoteAssist.Host.Service.exe");
    internal static bool IsInstalledDirectory(string directory) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)), Root, StringComparison.OrdinalIgnoreCase);

    internal static async Task Run()
    {
        if (!IsInstalledDirectory(AppContext.BaseDirectory)) throw new UnauthorizedAccessException("support-dev requires the protected development installation.");
        InstallationState.DemandProtected(Environment.ProcessPath!);
        var marker = ReadOwner(Path.Combine(Root, "dev-owner.json"));
        var stateOwner = ReadOwner(Path.Combine(InstallationState.Data, "dev-owner.json"));
        if (marker != stateOwner) throw new UnauthorizedAccessException("Development installation and state ownership differ.");
        DemandServiceImage();
        if (File.Exists(Path.Combine(Root, "startup-firewall.pending.json")))
            throw new InvalidOperationException("A previous firewall preparation requires recovery before support can start.");
        var sasJournal = Path.Combine(InstallationState.Data, "startup-sas.json");
        if (File.Exists(sasJournal))
        {
            InstallationState.DemandProtected(sasJournal);
            using var sas = JsonDocument.Parse(File.ReadAllBytes(sasJournal));
            if (sas.RootElement.GetProperty("State").GetString() != "committed")
                throw new InvalidOperationException("A previous SAS preparation requires recovery before support can start.");
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        var manager = OpenSCManager(null, null, 1);
        if (manager == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        nint service = 0;
        InitialSupportGrant? grant = null;
        bool prepared = false, started = false;
        try
        {
            service = OpenService(manager, "RemoteAssistHost", 4 | 16 | 32);
            if (service == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            // Issue performs the interactive console/elevated identity checks before any mutation.
            grant = InitialSupportLaunch.Issue();
            await Stop(service, deadline.Token);
            prepared = true;
            await Prepare("Prepare", deadline.Token);
            await Prepare("Verify", deadline.Token);
            var ready = Path.Combine(InstallationState.Data, "ready.json");
            if (File.Exists(ready)) { InstallationState.DemandProtected(ready); File.Delete(ready); }
            DemandServiceImage();
            started = true;
            if (!StartService(service, 0, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                var status = Status(service);
                if (status.State == 1) throw new IOException("Development service stopped before startup preparation completed.");
                if (status.State == 4 && status.Pid > 0 && File.Exists(ready))
                {
                    InstallationState.DemandProtected(ready);
                    using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(ready, deadline.Token));
                    if (document.RootElement.GetProperty("Pid").GetInt32() == status.Pid &&
                        string.Equals(document.RootElement.GetProperty("Image").GetString(), Image, StringComparison.OrdinalIgnoreCase))
                    {
                        using var process = Process.GetProcessById(checked((int)status.Pid));
                        if (!process.HasExited && string.Equals(process.MainModule?.FileName, Image, StringComparison.OrdinalIgnoreCase)) break;
                    }
                }
                await Task.Delay(100, deadline.Token);
            }
            await Prepare("Commit", deadline.Token);
            Console.WriteLine("DEVELOPMENT_STARTUP_READY_NOT_E2E_PASS");
        }
        catch
        {
            // Cleanup has its own finite allowance; never roll rules back beneath a running child.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            if (started && service != 0) await Stop(service, cleanup.Token);
            if (prepared) await Prepare("Rollback", cleanup.Token);
            throw;
        }
        finally
        {
            try { if (grant != null) InitialSupportLaunch.Cancel(grant.Id); }
            finally
            {
                if (service != 0) CloseServiceHandle(service);
                CloseServiceHandle(manager);
            }
        }
    }

    static string ReadOwner(string path)
    {
        InstallationState.DemandProtected(path);
        if (new FileInfo(path).Length > 8192) throw new InvalidDataException("Development marker too large.");
        using var owner = JsonDocument.Parse(File.ReadAllBytes(path));
        var value = owner.RootElement;
        var id = value.GetProperty("id").GetString();
        if (!Guid.TryParseExact(id, "N", out _) || value.GetProperty("kind").GetString() != "UNSIGNED_DEVELOPMENT_NOT_RELEASE" ||
            value.GetProperty("root").GetString() != Root || value.GetProperty("data").GetString() != InstallationState.Data)
            throw new UnauthorizedAccessException("Development ownership marker mismatch.");
        return id!;
    }

    static void DemandServiceImage()
    {
        InstallationState.DemandProtected(Image);
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\RemoteAssistHost");
        if (key?.GetValue("ImagePath") is not string image || !string.Equals(image, "\"" + Image + "\"", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(key.GetValue("ObjectName") as string, "LocalSystem", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Service does not belong to this SYSTEM development installation.");
    }

    static async Task Prepare(string action, CancellationToken cancellation)
    {
        var script = Path.Combine(Root, "prepare-host.ps1");
        InstallationState.DemandProtected(script);
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Action", action, "-InstallationRoot", Root }) start.ArgumentList.Add(arg);
        using var child = Process.Start(start) ?? throw new IOException("Cannot start development preparation.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await child.WaitForExitAsync(timeout.Token);
            if (child.ExitCode != 0) throw new IOException("Development preparation failed: " + action);
        }
        catch { if (!child.HasExited) { child.Kill(true); child.WaitForExit(2000); } throw; }
    }

    static async Task Stop(nint service, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var current = Status(service);
        if (current.State == 1) return;
        if (current.State != 3 && !ControlService(service, 1, out _) && Marshal.GetLastWin32Error() != 1062)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        while (Status(service).State != 1) await Task.Delay(100, timeout.Token);
    }

    static ServiceStatus Status(nint service)
    {
        if (!QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<ServiceStatus>(), out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return status;
    }
    [StructLayout(LayoutKind.Sequential)] struct ServiceStatus { public uint Type, State, Controls, Win32Exit, ServiceExit, Checkpoint, WaitHint, Pid, Flags; }
    [StructLayout(LayoutKind.Sequential)] struct BasicStatus { public uint Type, State, Controls, Win32Exit, ServiceExit, Checkpoint, WaitHint; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint OpenService(nint manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool QueryServiceStatusEx(nint service, int info, out ServiceStatus status, int size, out int needed);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool ControlService(nint service, uint control, out BasicStatus status);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool StartService(nint service, int count, nint args);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool CloseServiceHandle(nint handle);
}
