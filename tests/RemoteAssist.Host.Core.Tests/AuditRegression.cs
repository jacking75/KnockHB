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
            var entry=journal.Append(AuditKind.SupporterConnected,AuditResult.Allowed,"session","connection",1);
            var path=Path.Combine(directory,"audit.ndjson");var text=File.ReadAllText(path);
            if(JsonSerializer.Deserialize<AuditEvent>(text)!=entry||!checkedPaths.Contains(path))throw new Exception("durable bound audit missing");
            Reject(()=>journal.Append(AuditKind.SupporterConnected,AuditResult.Allowed,"secret\nPIN","connection",1));
            Reject(()=>journal.Append((AuditKind)999,AuditResult.Allowed,"session","connection",1));
            using(var file=new FileStream(path,FileMode.Open,FileAccess.Write)){file.SetLength(AuditJournal.MaxFileBytes);}
            journal.Append(AuditKind.AdminRevoked,AuditResult.Stopped,"session","connection",1);
            if(!File.Exists(Path.Combine(directory,"audit.1.ndjson"))||new FileInfo(path).Length>=AuditJournal.MaxFileBytes)throw new Exception("rotation failed");
            var deny=new AuditJournal(directory,validatePath:_=>throw new UnauthorizedAccessException());
            Reject(()=>deny.Append(AuditKind.AdminGranted,AuditResult.Allowed,"session","connection",1));
            if(Directory.GetFiles(directory).Length!=3)throw new Exception("failure created unprotected journal");
        }
        finally{Directory.Delete(directory,true);} // Unique test-owned path, never a user directory.
        return Task.CompletedTask;
    }
    static void Reject(Action action)
    {try{action();}catch(Exception ex)when(ex is InvalidDataException or UnauthorizedAccessException){return;}throw new Exception("invalid audit accepted");}

    public static async Task Terminal()
    {
        var directory=Path.Combine(Path.GetTempPath(),"RemoteAssist-audit-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try
        {
            var journal=new AuditJournal(directory,_=>{});var second=new AuditJournal(directory,_=>{});
            var service=journal.AppendSessionEndedOnce("session","connection");
            if(second.AppendSessionEndedOnce("session","connection")!=service)throw new Exception("normal/recovery duplicate");
            var recovery=second.AppendSessionEndedOnce("reverse","connection");
            if(journal.AppendSessionEndedOnce("reverse","connection")!=recovery)throw new Exception("recovery/normal duplicate");
            var spool=Path.Combine(directory,"pending");Directory.CreateDirectory(spool);
            var pending=new AuditEvent(Guid.NewGuid().ToString("N"),DateTimeOffset.UtcNow,AuditKind.SessionEnded,AuditResult.Stopped,"spool-session","connection",0);
            File.WriteAllBytes(Path.Combine(spool,pending.EventId+".json"),JsonSerializer.SerializeToUtf8Bytes(pending));
            if(second.AppendSessionEndedOnce("spool-session","connection")!=pending)throw new Exception("spool id changed");
            second.Acknowledge(pending);if(File.Exists(Path.Combine(spool,pending.EventId+".json")))throw new Exception("ACK retained");
            if(second.AppendSessionEndedOnce("spool-session","connection")!=pending)throw new Exception("ACK lost terminal");
            var executable=Environment.ProcessPath??throw new Exception();
            var processes=Enumerable.Range(0,4).Select(_=>
            {
                var info=new System.Diagnostics.ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true};
                info.ArgumentList.Add("--audit-child");info.ArgumentList.Add(directory);return System.Diagnostics.Process.Start(info)??throw new Exception();
            }).ToArray();
            try
            {
                foreach(var process in processes){await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));if(process.ExitCode!=0)throw new Exception("competing writer failed");}
            }
            finally{foreach(var process in processes){if(!process.HasExited)process.Kill(true);process.Dispose();}}
            var rows=File.ReadAllLines(Path.Combine(directory,"audit.ndjson")).Select(line=>JsonSerializer.Deserialize<AuditEvent>(line)!).ToArray();
            if(rows.Count(e=>e.SessionId=="crash-session")!=1)throw new Exception("cross process duplicate");
            using(var held=new FileStream(Path.Combine(directory,"writer.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None))
            {try{journal.AppendSessionEndedOnce("blocked","c");throw new Exception("lock ignored");}catch(IOException){}}
            var originalText=File.ReadAllText(Path.Combine(directory,"audit.ndjson"));
            File.AppendAllText(Path.Combine(directory,"audit.ndjson"),"{\"unknown\":true}\n");
            Reject(()=>journal.AppendSessionEndedOnce("malformed","c"));File.WriteAllText(Path.Combine(directory,"audit.ndjson"),originalText);
            File.AppendAllText(Path.Combine(directory,"audit.ndjson"),"{");
            Reject(()=>journal.AppendSessionEndedOnce("torn","c"));
        }
        finally{Directory.Delete(directory,true);}
    }
}
