using System.Diagnostics;
using System.Security.Principal;
using System.Security.AccessControl;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace RemoteAssist.Host.Core.Deployment;

public sealed record InitialSupportGrant(string Id, uint SessionId, string UserSid,
    DateTimeOffset IssuedAt, DateTimeOffset ClaimDeadline, DateTimeOffset ExpiresAt,
    int LauncherPid, DateTime LauncherStartedUtc)
{
    public void Validate(uint session, string sid, DateTimeOffset now)
    {
        if (!Guid.TryParseExact(Id, "N", out _) || SessionId == 0 || SessionId != session || UserSid != sid ||
            string.IsNullOrWhiteSpace(sid) || LauncherPid <= 0 || LauncherStartedUtc > IssuedAt.UtcDateTime ||
            IssuedAt > now || ClaimDeadline != IssuedAt.AddMinutes(10) ||
            ExpiresAt != IssuedAt.AddHours(2) || now >= ClaimDeadline)
            throw new UnauthorizedAccessException("최초 실행 허가가 만료됐거나 현재 Windows 사용자·세션과 다르다. 대상 사용자가 프로그램을 다시 실행해야 한다.");
    }
}

/// <summary>A protected, one-use authorization from an interactive elevated launch, never a startup task.</summary>
public static class InitialSupportLaunch
{
    static string Ticket => Path.Combine(InstallationState.Data, "initial-support.json");

    public static InitialSupportGrant Issue()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var session = checked((uint)Process.GetCurrentProcess().SessionId);
        if (identity.IsSystem || session == 0 || !IsInteractiveConsole(session) ||
            !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("대상 PC의 현재 사용자가 관리자 권한으로 직접 실행해야 한다.");
        InstallationState.DemandProtected(InstallationState.Data);
        var now = DateTimeOffset.UtcNow;
        using var launcher = Process.GetCurrentProcess();
        var grant = new InitialSupportGrant(Guid.NewGuid().ToString("N"), session,
            ConsoleUserSid(session),
            now, now.AddMinutes(10), now.AddHours(2), launcher.Id, launcher.StartTime.ToUniversalTime());
        if (File.Exists(Ticket)) InstallationState.DemandProtected(Ticket);
        try
        {
        try
        {
            InstallationState.Atomic(Ticket, JsonSerializer.SerializeToUtf8Bytes(grant));
            var acl = new FileSecurity();
            acl.SetSecurityDescriptorSddlForm("O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)");
            new FileInfo(Ticket).SetAccessControl(acl);
        }
        catch { if (File.Exists(Ticket)) File.Delete(Ticket); throw; }
        }
        catch { if (File.Exists(Ticket)) File.Delete(Ticket); throw; }
        return grant;
    }

    // Elevation with another administrator's credentials changes identity.User, but
    // the authorization must belong to the desktop user who launched support.
    static string ConsoleUserSid(uint session)
    {
        var user = QueryText(session, 5); // WTSUserName
        var domain = QueryText(session, 7); // WTSDomainName
        if (string.IsNullOrWhiteSpace(user)) throw new UnauthorizedAccessException("현재 콘솔 사용자 조회 실패");
        var account = string.IsNullOrWhiteSpace(domain) ? new NTAccount(user) : new NTAccount(domain, user);
        return ((SecurityIdentifier)account.Translate(typeof(SecurityIdentifier))).Value;
    }

    static string QueryText(uint session, int information)
    {
        if (!WTSQuerySessionInformation(0, session, information, out var buffer, out var bytes))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (bytes < 2 || bytes > 4096 || (bytes & 1) != 0) throw new InvalidDataException("콘솔 사용자 정보 크기 오류");
            return (Marshal.PtrToStringUni(buffer, bytes / 2) ?? "").TrimEnd('\0');
        }
        finally { WTSFreeMemory(buffer); }
    }

    static bool IsInteractiveConsole(uint session)
    {
        if (session == uint.MaxValue || session != WTSGetActiveConsoleSessionId()) return false;
        if (!WTSQuerySessionInformation(0, session, 8, out var state, out var stateBytes)) return false;
        try { if (stateBytes < 4 || Marshal.ReadInt32(state) != 0) return false; }
        finally { WTSFreeMemory(state); }
        if (!WTSQuerySessionInformation(0, session, 16, out var protocol, out var protocolBytes)) return false;
        try { return protocolBytes >= 2 && Marshal.ReadInt16(protocol) == 0; }
        finally { WTSFreeMemory(protocol); }
    }

    [DllImport("kernel32.dll")] static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool WTSQuerySessionInformation(nint server, uint session, int information, out nint buffer, out int bytes);
    [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(nint memory);

    public static InitialSupportGrant? Consume(uint session, string userSid)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!identity.IsSystem || Process.GetCurrentProcess().SessionId != 0)
            throw new UnauthorizedAccessException("서비스만 최초 실행 허가를 소비할 수 있다.");
        if (!File.Exists(Ticket)) return null;
        InstallationState.DemandProtected(Ticket);
        // Delete before interpretation: an invalid or interrupted launch cannot replay the ticket.
        byte[] bytes;
        using (var stream = new FileStream(Ticket, FileMode.Open, FileAccess.Read, FileShare.None, 4096, FileOptions.DeleteOnClose))
        {
            if (stream.Length > 4096) throw new InvalidDataException("최초 실행 허가 크기 초과");
            bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
        }
        var grant = JsonSerializer.Deserialize<InitialSupportGrant>(bytes) ?? throw new InvalidDataException("최초 실행 허가 누락");
        grant.Validate(session, userSid, DateTimeOffset.UtcNow);
        // A crashed launcher, reboot or PID reuse must not turn a leftover file into unattended access.
        using var launcher = Process.GetProcessById(grant.LauncherPid);
        if (launcher.HasExited || launcher.SessionId != session || launcher.StartTime.ToUniversalTime() != grant.LauncherStartedUtc)
            throw new UnauthorizedAccessException("최초 실행 프로세스가 종료됐거나 교체됐다.");
        return grant;
    }

    public static void Cancel(string id)
    {
        if (!File.Exists(Ticket)) return;
        InstallationState.DemandProtected(Ticket);
        var grant = JsonSerializer.Deserialize<InitialSupportGrant>(File.ReadAllBytes(Ticket));
        if (grant?.Id == id) File.Delete(Ticket);
    }
}
