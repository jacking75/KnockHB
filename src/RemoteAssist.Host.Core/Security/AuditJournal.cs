using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace RemoteAssist.Host.Core.Security;

public enum AuditKind {SessionCreated,ConsentGranted,ConsentDenied,SessionEnded,AdminGranted,AdminDenied,AdminRevoked,FileStarted,FileFinished,RunbookStarted,RunbookFinished,SasRequested}
public enum AuditResult {Allowed,Denied,Stopped,Completed,Failed}
public sealed record AuditEvent(string EventId,DateTimeOffset Utc,AuditKind Kind,AuditResult Result,string SessionId,string ConnectionId,long PermissionGeneration);

/// <summary>Closed metadata schema. No arbitrary text, payload or exception message can enter the journal.</summary>
public sealed class AuditJournal
{
    readonly object gate=new();
    readonly string directory;
    readonly TimeProvider time;
    readonly Action<string> validatePath;
    public const long MaxFileBytes=4*1024*1024;
    public AuditJournal(string directory,TimeProvider? time=null):this(directory,DemandProtected,time){}
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
    public AuditEvent Append(AuditKind kind,AuditResult result,string session,string connection,long generation)
    {
        if(!Enum.IsDefined(kind)||!Enum.IsDefined(result)||!Identifier(session)||!Identifier(connection)||generation<0||generation>9007199254740991L)throw new InvalidDataException("감사 메타데이터 오류다.");
        lock(gate)
        {
            validatePath(directory);
            var current=Path.Combine(directory,"audit.ndjson");
            var now=time.GetUtcNow();
            var value=new AuditEvent(Guid.NewGuid().ToString("N"),now,kind,result,session,connection,generation);
            var data=JsonSerializer.SerializeToUtf8Bytes(value);
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
            }
            using var file=new FileStream(current,FileMode.Append,FileAccess.Write,FileShare.Read,4096,FileOptions.WriteThrough);
            validatePath(current);file.Write(data);file.WriteByte(10);file.Flush(true);return value;
        }
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
