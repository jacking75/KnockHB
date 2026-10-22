using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using RemoteAssist.Protocol;
namespace RemoteAssist.Host.Core.Deployment;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReleaseFile(string Path,long Size,string Sha256);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReleaseManifest(string Product,string Platform,int Protocol,string Version,string ArchiveSha256,ReleaseFile[] Files)
{
    public const long MaxPackageBytes=1024L*1024*1024;
    public static ReleaseManifest Verify(byte[] bytes,byte[] signature,string publicKey)
    {
        using var rsa=RSA.Create();rsa.ImportFromPem(publicKey);
        if(bytes.Length>131072||rsa.KeySize<3072||!rsa.VerifyData(bytes,signature,HashAlgorithmName.SHA256,RSASignaturePadding.Pss))throw new CryptographicException("Release manifest 서명 오류다.");
        using var document=ProtocolJson.ParseObject(bytes,131072);var m=JsonSerializer.Deserialize<ReleaseManifest>(bytes)??throw new InvalidDataException("manifest 누락이다.");m.Validate();return m;
    }
    public void Validate()
    {
        if(Product!="RemoteAssist"||Platform!="windows-x64"||Protocol!=1||!System.Text.RegularExpressions.Regex.IsMatch(Version??"",@"^(0|[1-9]\d{0,4})(\.(0|[1-9]\d{0,4})){3}$")||!System.Version.TryParse(Version,out _)||!Hash(ArchiveSha256)||Files is not {Length:>0 and <=4096})throw new InvalidDataException("Release 제품/버전/상한 오류다.");
        var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);long size=0;
        foreach(var f in Files){if(f is null||!SafePath(f.Path)||!paths.Add(f.Path)||!Hash(f.Sha256)||f.Size<0||f.Size>MaxPackageBytes||(size+=f.Size)>MaxPackageBytes)throw new InvalidDataException("Release 파일 오류다.");}
        foreach(var required in new[]{"RemoteAssist.Host.Service.exe","RemoteAssist.Host.Tray.exe","RemoteAssist.Host.Helper.exe","RemoteAssist.Host.AdminHelper.exe","RemoteAssist.Host.Recovery.exe","RemoteAssist.Host.RunbookWorker.exe","RemoteAssist.Update.exe","install.ps1","prepare-host.ps1","prepare-sas.ps1","host.settings.json","runbooks/network-summary.ps1","runbooks/clear-dns-cache.ps1","notices/NOTICE.md"})if(!paths.Contains(required))throw new InvalidDataException("필수 설치 파일 누락이다.");
    }
    public static bool Hash(string? value)=>value is {Length:64}&&value.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static bool SafePath(string? path)=>path is {Length:>0 and <=240}&&!path.Contains('\\')&&!path.Contains(':')&&!path.StartsWith('/')&&path.Split('/').All(p=>p.Length>0&&p is not ("." or "..")&&!p.EndsWith('.')&&!p.EndsWith(' ')&&!System.Text.RegularExpressions.Regex.IsMatch(p,@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)",System.Text.RegularExpressions.RegexOptions.IgnoreCase)&&p.All(c=>char.IsAsciiLetterOrDigit(c)||c is '-' or '_' or '.'));
    public async Task Extract(string archive,string destination,CancellationToken ct)
    {
        if(new FileInfo(archive).Length>MaxPackageBytes)throw new InvalidDataException("Release ZIP 크기 상한이다.");
        await using(var input=File.OpenRead(archive))if(Convert.ToHexStringLower(await SHA256.HashDataAsync(input,ct))!=ArchiveSha256)throw new CryptographicException("Release ZIP hash 오류다.");
        if(Directory.Exists(destination))throw new IOException("staging이 이미 있다.");Directory.CreateDirectory(destination);
        using var zip=ZipFile.OpenRead(archive);if(zip.Entries.Count!=Files.Length)throw new InvalidDataException("ZIP 파일 수 오류다.");var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();var f=Files.SingleOrDefault(f=>f.Path==entry.FullName);
            if(f==null||!seen.Add(f.Path)||entry.Length!=f.Size||(entry.ExternalAttributes>>16&0xf000)==0xa000)throw new InvalidDataException("ZIP 경로/링크/크기 오류다.");
            var output=System.IO.Path.Combine(destination,f.Path.Replace('/',System.IO.Path.DirectorySeparatorChar));Directory.CreateDirectory(System.IO.Path.GetDirectoryName(output)!);
            await using(var target=new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.None)){await using var source=entry.Open();var buffer=new byte[65536];long total=0;int n;while((n=await source.ReadAsync(buffer,ct))!=0){if((total+=n)>f.Size)throw new InvalidDataException("ZIP 해제 크기 초과다.");await target.WriteAsync(buffer.AsMemory(0,n),ct);}if(total!=f.Size)throw new InvalidDataException("ZIP 해제 크기 부족이다.");}
            await using var check=File.OpenRead(output);if(check.Length!=f.Size||Convert.ToHexStringLower(await SHA256.HashDataAsync(check,ct))!=f.Sha256)throw new CryptographicException("Release 파일 hash 오류다.");
        }
    }
}
