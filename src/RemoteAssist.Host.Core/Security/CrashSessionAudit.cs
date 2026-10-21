namespace RemoteAssist.Host.Core.Security;

/// <summary>Pipe loss is insufficient. Call only after input fence/up; the caller
/// retains the authenticated service handle used by serviceExited.</summary>
public static class CrashSessionAudit
{
    public static bool Record(string? session,string? connection,Func<bool> serviceExited,Func<AuditJournal> journal)
    {
        if(session is null||connection is null||!serviceExited())return false;
        journal().AppendSessionEndedOnce(session,connection);return true;
    }
}
