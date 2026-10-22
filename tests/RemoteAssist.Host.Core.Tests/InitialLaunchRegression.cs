using RemoteAssist.Host.Core.Deployment;

internal static class InitialLaunchRegression
{
    public static Task Run()
    {
        var now = new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero);
        var grant = new InitialSupportGrant(Guid.NewGuid().ToString("N"), 1, "S-1-5-21-100-100-100-1001",
            now, now.AddMinutes(10), now.AddHours(2), 42, now.UtcDateTime);
        grant.Validate(1, grant.UserSid, now);
        grant.Validate(1, grant.UserSid, now.AddMinutes(9));
        Reject(() => grant.Validate(2, grant.UserSid, now));
        Reject(() => grant.Validate(1, "S-1-5-21-100-100-100-1002", now));
        Reject(() => grant.Validate(1, grant.UserSid, now.AddMinutes(10)));
        Reject(() => grant.Validate(1, grant.UserSid, now.AddTicks(-1)));
        Reject(() => (grant with { ClaimDeadline = now.AddHours(1) }).Validate(1, grant.UserSid, now));
        Reject(() => (grant with { ExpiresAt = now.AddDays(1) }).Validate(1, grant.UserSid, now));
        Reject(() => (grant with { Id = "reused-external-id" }).Validate(1, grant.UserSid, now));
        Reject(() => (grant with { SessionId = 0 }).Validate(0, grant.UserSid, now));
        Reject(() => (grant with { LauncherPid = 0 }).Validate(1, grant.UserSid, now));
        Reject(() => (grant with { LauncherStartedUtc = now.AddSeconds(1).UtcDateTime }).Validate(1, grant.UserSid, now));
        return Task.CompletedTask;
    }

    static void Reject(Action action)
    {
        try { action(); }
        catch (UnauthorizedAccessException) { return; }
        throw new Exception("Invalid initial launch authorization was accepted.");
    }
}
