using System.Text.Json;
using RemoteAssist.Host.Core.Security;

internal static class AuditRegression
{
    public static Task Run()
    {
        var directory=Path.Combine(Path.GetTempPath(),"RemoteAssist-audit-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try
        {
            var checkedPaths=new List<string>();var journal=new AuditJournal(directory,validatePath:checkedPaths.Add);
            var entry=journal.Append(AuditKind.ConsentGranted,AuditResult.Allowed,"session","connection",1);
            var path=Path.Combine(directory,"audit.ndjson");var text=File.ReadAllText(path);
            if(JsonSerializer.Deserialize<AuditEvent>(text)!=entry||!checkedPaths.Contains(path))throw new Exception("durable bound audit missing");
            Reject(()=>journal.Append(AuditKind.ConsentGranted,AuditResult.Allowed,"secret\nPIN","connection",1));
            Reject(()=>journal.Append((AuditKind)999,AuditResult.Allowed,"session","connection",1));
            using(var file=new FileStream(path,FileMode.Open,FileAccess.Write)){file.SetLength(AuditJournal.MaxFileBytes);}
            journal.Append(AuditKind.SessionEnded,AuditResult.Stopped,"session","connection",1);
            if(!File.Exists(Path.Combine(directory,"audit.1.ndjson"))||new FileInfo(path).Length>=AuditJournal.MaxFileBytes)throw new Exception("rotation failed");
            var deny=new AuditJournal(directory,validatePath:_=>throw new UnauthorizedAccessException());
            Reject(()=>deny.Append(AuditKind.AdminGranted,AuditResult.Allowed,"session","connection",1));
            if(Directory.GetFiles(directory).Length!=2)throw new Exception("failure created unprotected journal");
        }
        finally{Directory.Delete(directory,true);} // Unique test-owned path, never a user directory.
        return Task.CompletedTask;
    }
    static void Reject(Action action)
    {try{action();}catch(Exception ex)when(ex is InvalidDataException or UnauthorizedAccessException){return;}throw new Exception("invalid audit accepted");}
}
