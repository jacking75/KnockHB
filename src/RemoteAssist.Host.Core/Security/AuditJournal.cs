using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteAssist.Host.Core.Security;

public enum AuditKind {SessionCreated,ConsentGranted,ConsentDenied,SessionEnded,AdminGranted,AdminDenied,AdminRevoked,FileStarted,FileFinished,RunbookStarted,RunbookFinished,SasRequested}
public enum AuditResult {Allowed,Denied,Stopped,Completed,Failed}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AuditOperation(string Id,string? BookId=null,string? Hash=null,string? Direction=null,long? Size=null)
{
    public void Validate(){if(!Guid.TryParseExact(Id,"N",out var id)||id.ToString("N")!=Id||BookId!=null&&BookId is not ("network-summary" or "clear-dns-cache")||Hash!=null&&!Deployment.ReleaseManifest.Hash(Hash)||Direction!=null&&Direction is not ("from-host" or "to-host")||Size is <0 or >268435456)throw new InvalidDataException("감사 동작 메타데이터 오류다.");}
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AuditEvent(string EventId,DateTimeOffset Utc,AuditKind Kind,AuditResult Result,string SessionId,string ConnectionId,long PermissionGeneration,AuditOperation? Operation=null);

/// <summary>Closed metadata schema. No arbitrary text, payload or exception message can enter the journal.</summary>
public sealed class AuditJournal
{
    readonly string directory;
    readonly TimeProvider time;
    readonly Action<string> validatePath;
    readonly bool spoolEnabled;
    public const long MaxFileBytes=4*1024*1024;
    public AuditJournal(string directory,TimeProvider? time=null):this(directory,DemandProtected,time){spoolEnabled=true;}
    internal AuditJournal(string directory,Action<string> validatePath,TimeProvider? time=null)
    {this.directory=Path.GetFullPath(directory);this.time=time??TimeProvider.System;this.validatePath=validatePath;}
    public static AuditJournal OpenProtected()
    {
        using var identity=WindowsIdentity.GetCurrent();
        if(!identity.IsSystem)throw new UnauthorizedAccessException("감사 저장소는 서비스가 관리한다.");
        var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"RemoteAssist");
        Prepare(root);var audit=Path.Combine(root,"audit");Prepare(audit);
        return new(audit);
    }
    // Protected file handle is the cross-process mutex. No global-name squatting
    // or user-writable named mutex can bypass the directory ACL.
    internal IDisposable AcquireWriter()
    {
        validatePath(directory);
        var path=Path.Combine(directory,"writer.lock");
        var started=System.Diagnostics.Stopwatch.StartNew();
        while(true)
        {
            validatePath(directory);if(File.Exists(path))validatePath(path);
            try
            {
                var lease=new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
                try{validatePath(path);return lease;}catch{lease.Dispose();throw;}
            }
            catch(IOException) when(started.Elapsed<TimeSpan.FromSeconds(2)){Thread.Sleep(10);}
        }
    }
    public AuditEvent AppendSessionEndedOnce(string session,string connection)=>
        Append(AuditKind.SessionEnded,AuditResult.Stopped,session,connection,0);
    public AuditEvent Append(AuditKind kind,AuditResult result,string session,string connection,long generation,AuditOperation? operation=null)
    {
        operation?.Validate();
        if(!Enum.IsDefined(kind)||!Enum.IsDefined(result)||!Identifier(session)||!Identifier(connection)||generation<0||generation>9007199254740991L||
            kind==AuditKind.SessionEnded&&(generation!=0||result!=AuditResult.Stopped||operation!=null))throw new InvalidDataException("감사 저장소 검증 오류다.");
        // AcquireWriter bounds same-process and cross-process contention alike.
        {
            using var lease=AcquireWriter();
            if(kind==AuditKind.SessionEnded)
            {
                var journal=ReadJournal().Where(e=>e.Kind==kind&&e.SessionId==session&&e.ConnectionId==connection).ToArray();
                var pending=ReadPending().Where(e=>e.Kind==kind&&e.SessionId==session&&e.ConnectionId==connection).ToArray();
                var found=journal.Concat(pending).DistinctBy(e=>e.EventId).ToArray();
                if(found.Length>1)throw new InvalidDataException("감사 저장소 검증 오류다.");
                if(found.Length==1)
                {
                    if(journal.Concat(pending).Any(e=>e!=found[0]))throw new InvalidDataException("감사 저장소 검증 오류다.");
                    if(journal.Length==0)WriteJournal(found[0]);return found[0];
                }
            }
            var value=new AuditEvent(Guid.NewGuid().ToString("N"),time.GetUtcNow(),kind,result,session,connection,generation,operation);
            var data=JsonSerializer.SerializeToUtf8Bytes(value);
            if(spoolEnabled)
            {
                var spool=Path.Combine(directory,"pending");Prepare(spool);validatePath(spool);
                if(Directory.EnumerateFiles(spool).Take(4097).Count()>=4096||data.Length>4096)throw new IOException("감사 저장소 검증 오류다.");
                Deployment.InstallationState.Atomic(Path.Combine(spool,value.EventId+".json"),data);
            }
            WriteJournal(value);return value;
        }
    }
    IEnumerable<AuditEvent> ReadJournal()
    {
        for(var i=0;i<=3;i++)
        {
            var path=Path.Combine(directory,i==0?"audit.ndjson":$"audit.{i}.ndjson");
            if(!File.Exists(path))continue;validatePath(path);
            if(new FileInfo(path).Length>MaxFileBytes)throw new InvalidDataException("감사 저장소 검증 오류다.");
            using var file=File.OpenRead(path);
            if(file.Length>0){file.Position=file.Length-1;if(file.ReadByte()!=10)throw new InvalidDataException("감사 저장소 검증 오류다.");file.Position=0;}
            using var reader=new StreamReader(file);
            while(reader.ReadLine() is {} line)
            {
                if(line.Length is <2 or >4096)throw new InvalidDataException("감사 저장소 검증 오류다.");
                yield return Decode(line);
            }
        }
    }
    IEnumerable<AuditEvent> ReadPending()
    {
        var spool=Path.Combine(directory,"pending");if(!Directory.Exists(spool))yield break;validatePath(spool);
        var files=Directory.EnumerateFiles(spool,"*.json").Take(4097).ToArray();
        if(files.Length>4096)throw new IOException("감사 저장소 검증 오류다.");
        foreach(var file in files)
        {
            validatePath(file);if(new FileInfo(file).Length>4096)throw new IOException("감사 저장소 검증 오류다.");
            var value=Decode(File.ReadAllText(file));
            if(Path.GetFileNameWithoutExtension(file)!=value.EventId)throw new InvalidDataException("감사 저장소 검증 오류다.");yield return value;
        }
    }
    static AuditEvent Decode(string text)
    {
        AuditEvent value;
        try{value=JsonSerializer.Deserialize<AuditEvent>(text)??throw new InvalidDataException("감사 행 오류다.");}
        catch(JsonException){throw new InvalidDataException("감사 행 스키마 오류다.");}
        if(!Guid.TryParseExact(value.EventId,"N",out var id)||id.ToString("N")!=value.EventId||value.Utc==default||
            !Enum.IsDefined(value.Kind)||!Enum.IsDefined(value.Result)||!Identifier(value.SessionId)||!Identifier(value.ConnectionId)||
            value.PermissionGeneration<0||value.PermissionGeneration>9007199254740991L||value.Kind==AuditKind.SessionEnded&&
            (value.PermissionGeneration!=0||value.Result!=AuditResult.Stopped||value.Operation!=null))throw new InvalidDataException("감사 행 메타데이터 오류다.");
        value.Operation?.Validate();return value;
    }
    // Reporter may ACK a spool event before the crashed writer appended it.
    // Materialize that SAME event under the writer mutex before removing spool.
    public void Acknowledge(AuditEvent value)
    {
        // AcquireWriter bounds same-process and cross-process contention alike.
        {
            using var lease=AcquireWriter();
            var pending=ReadPending().SingleOrDefault(e=>e.EventId==value.EventId);
            if(pending is null)return;if(pending!=value)throw new InvalidDataException("감사 저장소 검증 오류다.");
            var existing=ReadJournal().Where(e=>e.EventId==value.EventId).ToArray();
            if(existing.Any(e=>e!=value))throw new InvalidDataException("감사 저장소 검증 오류다.");
            if(existing.Length==0)WriteJournal(value);
            var path=Path.Combine(directory,"pending",value.EventId+".json");validatePath(path);File.Delete(path);
        }
    }
    void WriteJournal(AuditEvent value)
    {
        var current=Path.Combine(directory,"audit.ndjson");var now=time.GetUtcNow();var data=JsonSerializer.SerializeToUtf8Bytes(value);
        if(data.Length>4096)throw new InvalidDataException("감사 저장소 검증 오류다.");
        for(var i=1;i<=3;i++)
        {
            var old=Path.Combine(directory,$"audit.{i}.ndjson");
            if(File.Exists(old)){validatePath(old);if(File.GetLastWriteTimeUtc(old)<now.UtcDateTime.AddDays(-7))File.Delete(old);}
        }
        if(File.Exists(current))
        {
            validatePath(current);
            if(new FileInfo(current).Length+data.Length+1>MaxFileBytes||File.GetLastWriteTimeUtc(current)<now.UtcDateTime.AddDays(-7))
            {
                var last=Path.Combine(directory,"audit.3.ndjson");if(File.Exists(last))File.Delete(last);
                for(var i=2;i>=0;i--){var from=i==0?current:Path.Combine(directory,$"audit.{i}.ndjson");if(File.Exists(from))File.Move(from,Path.Combine(directory,$"audit.{i+1}.ndjson"));}
            }
            else
            {
                using var check=File.OpenRead(current);if(check.Length>0){check.Position=check.Length-1;if(check.ReadByte()!=10)throw new InvalidDataException("감사 저장소 검증 오류다.");}
            }
        }
        using var file=new FileStream(current,FileMode.Append,FileAccess.Write,FileShare.Read,4096,FileOptions.WriteThrough);
        validatePath(current);file.Write(data);file.WriteByte(10);file.Flush(true);
    }
    static bool Identifier(string? value)=>value is not null&&value.Length<=64&&value.All(c=>char.IsAsciiLetterOrDigit(c)||c is '-' or '_');
    static void Prepare(string path)
    {
        if(Directory.Exists(path)){DemandProtected(path);return;}
        var acl=new DirectorySecurity();acl.SetAccessRuleProtection(true,false);
        var system=new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null);acl.SetOwner(system);
        foreach(var sid in new[]{system,new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null)})
            acl.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        new DirectoryInfo(path).Create(acl);DemandProtected(path);
    }
    static void DemandProtected(string path)
    {
        FileSystemInfo info=Directory.Exists(path)?new DirectoryInfo(path):new FileInfo(path);
        if(!info.Exists||info.Attributes.HasFlag(FileAttributes.ReparsePoint))throw new UnauthorizedAccessException("감사 저장 경로 오류다.");
        var acl=info is DirectoryInfo dir?(FileSystemSecurity)dir.GetAccessControl():((FileInfo)info).GetAccessControl();
        var owner=acl.GetOwner(typeof(SecurityIdentifier))?.Value;
        if(owner is not ("S-1-5-18" or "S-1-5-32-544"))throw new UnauthorizedAccessException("감사 소유자 오류다.");
        foreach(FileSystemAccessRule rule in acl.GetAccessRules(true,true,typeof(SecurityIdentifier)))
            if(rule.AccessControlType==AccessControlType.Allow&&(rule.PropagationFlags&PropagationFlags.InheritOnly)==0&&rule.IdentityReference.Value is not ("S-1-5-18" or "S-1-5-32-544"))
                throw new UnauthorizedAccessException("감사 저장소의 불필요한 접근 권한이다.");
    }
}
