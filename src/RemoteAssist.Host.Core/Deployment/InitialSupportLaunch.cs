using System.Diagnostics;
using System.Security.Principal;
using System.Security.AccessControl;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace RemoteAssist.Host.Core.Deployment;

/// <summary>
/// 대상 사용자가 관리자 권한으로 직접 실행해 연 지원 허가. 같은 Windows 로그온(콘솔 세션·사용자·로그온 시각) 동안 유효하다.
/// 재부팅·빠른 시작 종료·로그오프는 로그온 시각을 바꾸므로 허가가 무효가 된다.
/// </summary>
public sealed record InitialSupportGrant(string Id, uint SessionId, string UserSid, DateTimeOffset IssuedAt, long LogonTime)
{
    public bool Matches(uint session, string sid, long logonTime) =>
        Guid.TryParseExact(Id, "N", out _) && SessionId != 0 && SessionId == session && UserSid == sid && !string.IsNullOrWhiteSpace(sid) &&
        LogonTime != 0 && LogonTime == logonTime;
}

/// <summary>
/// 관리자 권한 최초 실행이 남기는 허가 파일. 서비스는 시작할 때마다 이 허가를 읽어 같은 지원 ID로 지원 대기를 연다.
/// 서비스 재시작·업데이트에서는 유지되고, 재부팅·로그오프·사용자 변경·대상자의 즉시 종료에서 사라진다.
/// </summary>
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
        var grant = new InitialSupportGrant(Guid.NewGuid().ToString("N"), session, ConsoleUserSid(session), DateTimeOffset.UtcNow, LogonTime(session));
        if (File.Exists(Ticket)) InstallationState.DemandProtected(Ticket);
        try
        {
            InstallationState.Atomic(Ticket, JsonSerializer.SerializeToUtf8Bytes(grant));
            var acl = new FileSecurity();
            acl.SetSecurityDescriptorSddlForm("O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)");
            new FileInfo(Ticket).SetAccessControl(acl);
        }
        catch { if (File.Exists(Ticket)) File.Delete(Ticket); throw; }
        return grant;
    }

    /// <summary>현재 콘솔 사용자·세션·부팅에 맞는 허가를 읽는다. 맞지 않는 허가는 지운다.</summary>
    public static InitialSupportGrant? Load(uint session, string userSid)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!identity.IsSystem || Process.GetCurrentProcess().SessionId != 0)
            throw new UnauthorizedAccessException("서비스만 지원 허가를 읽는다.");
        if (!File.Exists(Ticket)) return null;
        InstallationState.DemandProtected(Ticket);
        InitialSupportGrant? grant = null;
        try
        {
            if (new FileInfo(Ticket).Length <= 4096) grant = JsonSerializer.Deserialize<InitialSupportGrant>(File.ReadAllBytes(Ticket));
        }
        catch (JsonException) { }
        if (grant is not null && grant.Matches(session, userSid, LogonTime(session))) return grant;
        File.Delete(Ticket);
        return null;
    }

    /// <summary>대상자의 즉시 종료·사용자 변경에서 허가를 없앤다. id를 주면 그 허가일 때만 지운다.</summary>
    public static void Revoke(string? id = null)
    {
        if (!File.Exists(Ticket)) return;
        InstallationState.DemandProtected(Ticket);
        if (id is not null)
        {
            try { if (JsonSerializer.Deserialize<InitialSupportGrant>(File.ReadAllBytes(Ticket))?.Id != id) return; }
            catch (JsonException) { }
        }
        File.Delete(Ticket);
    }

    // 다른 관리자 계정으로 상승해도 identity.User가 바뀐다. 허가는 화면 앞의 콘솔 사용자에게 묶는다.
    static string ConsoleUserSid(uint session)
    {
        var user = QueryText(session, 5); // WTSUserName
        var domain = QueryText(session, 7); // WTSDomainName
        if (string.IsNullOrWhiteSpace(user)) throw new UnauthorizedAccessException("현재 콘솔 사용자 조회 실패");
        var account = string.IsNullOrWhiteSpace(domain) ? new NTAccount(user) : new NTAccount(domain, user);
        return ((SecurityIdentifier)account.Translate(typeof(SecurityIdentifier))).Value;
    }

    /// <summary>콘솔 세션의 로그온 시각(FILETIME). WTSSessionInfo로 읽는다.</summary>
    public static long LogonTime(uint session)
    {
        if (!WTSQuerySessionInformation(0, session, 24, out var buffer, out var bytes)) // WTSSessionInfo
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (bytes < Marshal.SizeOf<WtsInfo>()) throw new InvalidDataException("세션 정보 크기 오류");
            return Marshal.PtrToStructure<WtsInfo>(buffer).LogonTime;
        }
        finally { WTSFreeMemory(buffer); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WtsInfo
    {
        public int State; public uint SessionId, IncomingBytes, OutgoingBytes, IncomingFrames, OutgoingFrames, IncomingCompressedBytes, OutgoingCompressedBytes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string WinStationName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 17)] public string Domain;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)] public string UserName;
        public long ConnectTime, DisconnectTime, LastInputTime, LogonTime, CurrentTime;
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
}
