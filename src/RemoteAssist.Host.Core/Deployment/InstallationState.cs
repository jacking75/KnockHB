using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
namespace RemoteAssist.Host.Core.Deployment;

public static class InstallationState
{
    public static string Root=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"RemoteAssist");
    public static string Data=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"RemoteAssist");
    /// <summary>
    /// 정식 설치(Program Files/RemoteAssist/versions) 밖에서 실행 중인지. 개발 설치(RemoteAssist-Dev)나 빌드 출력에서 실행하면 true다.
    /// 개발 중에는 보호 경로 ACL·릴리스 manifest·개발 소유 표식 검사를 하지 않는다. 정식 설치 검사는 배포 단계에서 다시 검토한다.
    /// </summary>
    public static bool Development {get;}=!Path.GetFullPath(AppContext.BaseDirectory).StartsWith(Path.Combine(Root,"versions")+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    public static FileStream? TrySessionLease()
    {
        try{return new FileStream(Path.Combine(Data,"session.gate"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}catch(IOException){return null;}
    }
    public static void CheckCandidate()
    {if(!Development)CheckDirectory(AppContext.BaseDirectory);}
    public static void CheckDirectory(string directory)
    {
        var path=Path.Combine(directory,"release.json");if(!File.Exists(path))return; // Developer diagnostics have no installation manifest.
        var bytes=File.ReadAllBytes(path);if(bytes.Length>131072)throw new InvalidDataException("설치 manifest 상한이다.");var manifest=JsonSerializer.Deserialize<ReleaseManifest>(bytes)??throw new InvalidDataException("설치 manifest 누락이다.");manifest.Validate();
        foreach(var f in manifest.Files)
        {
            var file=new FileInfo(Path.Combine(directory,f.Path.Replace('/',Path.DirectorySeparatorChar)));
            if(!file.Exists||file.Attributes.HasFlag(FileAttributes.ReparsePoint)||file.Length!=f.Size)throw new InvalidDataException("설치 파일/버전 불일치다.");
            using var input=file.OpenRead();if(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(input))!=f.Sha256)throw new InvalidDataException("설치 hash 불일치다.");
        }
    }
    public static void MarkReady()=>Atomic(Path.Combine(Data,"ready.json"),JsonSerializer.SerializeToUtf8Bytes(new {Pid=Environment.ProcessId,Image=Environment.ProcessPath,Version=Path.GetFileName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))}));
    public static void Atomic(string path,byte[] data){var temporary=path+"."+Guid.NewGuid().ToString("N");try{using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)){output.Write(data);output.Flush(true);}File.Move(temporary,path,true);}finally{if(File.Exists(temporary))File.Delete(temporary);}}
    public static void DemandProtected(string path)
    {
        if(Development)return;
        var root=Path.GetFullPath(path);var anchors=new[]{Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)};
        var anchor=anchors.FirstOrDefault(a=>root.StartsWith(a+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))??throw new UnauthorizedAccessException("보호된 배포 루트 밖이다.");
        for(FileSystemInfo? item=Directory.Exists(root)?new DirectoryInfo(root):new FileInfo(root);item!=null;item=item is DirectoryInfo d?d.Parent:((FileInfo)item).Directory)
        {
            if(!item.Exists||item.Attributes.HasFlag(FileAttributes.ReparsePoint))throw new UnauthorizedAccessException("배포 경로 변조다.");
            if(string.Equals(item.FullName,anchor,StringComparison.OrdinalIgnoreCase))break;
            var acl=item is DirectoryInfo dir?(FileSystemSecurity)dir.GetAccessControl():((FileInfo)item).GetAccessControl();Windows.ProtectedInstallation.CheckAcl(acl);
        }
    }
}
