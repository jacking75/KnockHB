using System.Security.AccessControl;
using System.Security.Principal;

namespace RemoteAssist.Host.Core.Windows;

public static class ProtectedInstallation
{
    public static string ResolveExecutable(string fileName)
    {
        if(fileName is not ("RemoteAssist.Host.RunbookWorker.exe" or "RemoteAssist.Host.Service.exe" or "RemoteAssist.Host.Tray.exe" or "RemoteAssist.Host.Helper.exe" or "RemoteAssist.Host.AdminHelper.exe" or "RemoteAssist.Host.Recovery.exe"))
            throw new ArgumentException("고정된 Host 실행 파일만 허용한다.");
        var root=Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var programFiles=Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles).TrimEnd(Path.DirectorySeparatorChar);
        if(!root.StartsWith(programFiles+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new UnauthorizedAccessException("Host 헬퍼는 보호된 설치 경로가 필요하다.");
        for(var directory=new DirectoryInfo(root);directory is not null&&directory.FullName!=programFiles;directory=directory.Parent)
        {
            if(directory.Attributes.HasFlag(FileAttributes.ReparsePoint))throw new UnauthorizedAccessException("설치 경로 reparse point를 거부한다.");
            CheckAcl(directory.GetAccessControl());
        }
        var file=new FileInfo(Path.Combine(root,fileName));
        if(!file.Exists||file.Attributes.HasFlag(FileAttributes.ReparsePoint))throw new UnauthorizedAccessException("설치 헬퍼가 없거나 경로가 변조됐다.");
        CheckAcl(file.GetAccessControl());
        var remaining=new Stack<DirectoryInfo>();remaining.Push(new DirectoryInfo(root));var count=0;
        while(remaining.TryPop(out var directory))
            foreach(var item in directory.EnumerateFileSystemInfos())
            {
                if(++count>4096||item.Attributes.HasFlag(FileAttributes.ReparsePoint))throw new UnauthorizedAccessException("설치 파일 트리 상한/경로 오류다.");
                if(item is DirectoryInfo child){CheckAcl(child.GetAccessControl());remaining.Push(child);}
                else if(item is FileInfo dependency)CheckAcl(dependency.GetAccessControl());
            }
        return file.FullName;
    }
    internal static void CheckAcl(FileSystemSecurity acl)
    {
        var owner=acl.GetOwner(typeof(SecurityIdentifier))?.Value;
        if(!Trusted(owner))throw new UnauthorizedAccessException("설치 소유자가 신뢰 계정이 아니다.");
        const FileSystemRights writes=FileSystemRights.Write|FileSystemRights.DeleteSubdirectoriesAndFiles|FileSystemRights.ChangePermissions|FileSystemRights.TakeOwnership|FileSystemRights.Delete;
        foreach(FileSystemAccessRule rule in acl.GetAccessRules(true,true,typeof(SecurityIdentifier)))
            if(rule.AccessControlType==AccessControlType.Allow&&(rule.PropagationFlags&PropagationFlags.InheritOnly)==0&&
               (rule.FileSystemRights&writes)!=0&&!Trusted(rule.IdentityReference.Value))
                throw new UnauthorizedAccessException("일반 사용자가 설치 파일을 수정할 수 있다.");
    }
    static bool Trusted(string? sid)=>sid is "S-1-5-18" or "S-1-5-32-544" or "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
}
