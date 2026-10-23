using RemoteAssist.Host.Core.Deployment;

internal static class InitialLaunchRegression
{
    public static Task Run()
    {
        // 허가는 같은 콘솔 세션·사용자·로그온 시각에서만 유효하다. 서비스 재시작은 이 조건을 바꾸지 않는다.
        const long logon = 134050000000000000;
        var grant = new InitialSupportGrant(Guid.NewGuid().ToString("N"), 1, "S-1-5-21-100-100-100-1001", DateTimeOffset.UtcNow, logon);
        Check(grant.Matches(1, grant.UserSid, logon), "valid grant of this logon rejected");
        Check(!grant.Matches(2, grant.UserSid, logon), "grant moved to another Windows session");
        Check(!grant.Matches(1, "S-1-5-21-100-100-100-1002", logon), "grant moved to another user");
        Check(!grant.Matches(1, grant.UserSid, logon + 1), "grant survived a reboot, logoff or new logon");
        Check(!(grant with { LogonTime = 0 }).Matches(1, grant.UserSid, 0), "grant without logon time accepted");
        Check(!(grant with { Id = "reused-external-id" }).Matches(1, grant.UserSid, logon), "malformed grant id accepted");
        Check(!(grant with { SessionId = 0 }).Matches(0, grant.UserSid, logon), "Session 0 grant accepted");
        // WTSINFO 구조체 배치가 틀리면 로그온 시각이 엉뚱한 값이 된다. 현재 세션의 값이 그럴듯한 FILETIME인지 본다.
        var session = (uint)System.Diagnostics.Process.GetCurrentProcess().SessionId;
        if (session != 0)
        {
            var current = DateTime.FromFileTimeUtc(InitialSupportLaunch.LogonTime(session));
            Check(current > new DateTime(2015, 1, 1, 0, 0, 0, DateTimeKind.Utc) && current <= DateTime.UtcNow.AddMinutes(1), "logon time read from the wrong WTSINFO field");
        }
        return Task.CompletedTask;
    }

    static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
}
