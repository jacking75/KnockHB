using System.Security.Cryptography;
using System.Text.Json;
using System.IO.Compression;
using RemoteAssist.Host.Core.Deployment;
using RemoteAssist.Host.Core.Runbooks;
using RemoteAssist.Protocol;
static class DeploymentRegression
{
    static void Reject(Action action){try{action();}catch(Exception ex)when(ex is InvalidDataException or CryptographicException or UnauthorizedAccessException){return;}throw new Exception("malicious manifest/runbook was accepted");}
    public static async Task Run()
    {
        using var rsa=RSA.Create(3072);var key=rsa.ExportSubjectPublicKeyInfoPem();
        var names=new[]{"RemoteAssist.Host.Service.exe","RemoteAssist.Host.Tray.exe","RemoteAssist.Host.Helper.exe","RemoteAssist.Host.AdminHelper.exe","RemoteAssist.Host.Recovery.exe","RemoteAssist.Host.RunbookWorker.exe","RemoteAssist.Update.exe","install.ps1","prepare-host.ps1","prepare-sas.ps1","host.settings.json","runbooks/network-summary.ps1","runbooks/clear-dns-cache.ps1","notices/NOTICE.md"};
        var empty=Convert.ToHexStringLower(SHA256.HashData(Array.Empty<byte>()));var manifest=new ReleaseManifest("RemoteAssist","windows-x64",1,"1.0.0.0",empty,names.Select(n=>new ReleaseFile(n,0,empty)).ToArray());
        var bytes=JsonSerializer.SerializeToUtf8Bytes(manifest);var signature=rsa.SignData(bytes,HashAlgorithmName.SHA256,RSASignaturePadding.Pss);ReleaseManifest.Verify(bytes,signature,key);
        Reject(()=>(manifest with {Files=manifest.Files.Where(f=>f.Path!="prepare-host.ps1").ToArray()}).Validate());
        Reject(()=>(manifest with {Files=manifest.Files.Where(f=>f.Path!="prepare-sas.ps1").ToArray()}).Validate());
        var duplicate=System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(bytes).Replace("\"Protocol\":1","\"Protocol\":1,\"Protocol\":1"));
        Reject(()=>ReleaseManifest.Verify(duplicate,rsa.SignData(duplicate,HashAlgorithmName.SHA256,RSASignaturePadding.Pss),key));
        bytes[^2]^=1;Reject(()=>ReleaseManifest.Verify(bytes,signature,key));
        Reject(()=>(manifest with {Platform="linux-x64"}).Validate());Reject(()=>(manifest with {Protocol=2}).Validate());Reject(()=>(manifest with {Version="01.0.0.0"}).Validate());
        Reject(()=>(manifest with {Files=[..manifest.Files,manifest.Files[0] with {Path="REMOTEASSIST.HOST.SERVICE.EXE"}]}).Validate());
        foreach(var path in new[]{"../escape.exe","/escape.exe","a\\escape.exe","a:stream","a/../escape.exe","a./b","CON.txt","COM1.dll"})if(ReleaseManifest.SafePath(path))throw new Exception("unsafe ZIP path: "+path);
        var book=RunbookCatalog.Definition("network-summary");var request=new RunbookMessage {T="runbook-request",Id=Guid.NewGuid().ToString("N"),SessionId="sid",Generation=1,PermissionGeneration=1,Sequence=1,BookId=book.BookId,BookVersion=1,Hash=book.Hash,Args=new(){["detail"]="counts"}};
        RunbookCatalog.Demand(request);Reject(()=>RunbookCatalog.Demand(request with {Hash=new string('a',64)}));Reject(()=>RunbookCatalog.Demand(request with {BookId="cmd.exe"}));Reject(()=>RunbookCatalog.Demand(request with {Args=new(){["detail"]="counts; Start-Process cmd"}}));
        Reject(()=>new RunbookResult("completed",0,new string('x',4097),"","").Validate());
        var directory=Path.Combine(Path.GetTempPath(),"remoteassist-release-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try
        {
            for(var scenario=0;scenario<4;scenario++)
            {
                var path=Path.Combine(directory,scenario+".zip");using(var zip=ZipFile.Open(path,ZipArchiveMode.Create))for(var i=0;i<names.Length;i++){var entry=zip.CreateEntry(scenario==1&&i==0?"../outside.exe":names[i]);if(scenario==2&&i==0){using var output=entry.Open();output.WriteByte(1);}if(scenario==3&&i==0)entry.ExternalAttributes=unchecked((int)0xa1ff0000);}
                using var archive=File.OpenRead(path);var sample=manifest with {ArchiveSha256=Convert.ToHexStringLower(SHA256.HashData(archive))};
                var destination=Path.Combine(directory,"extracted-"+scenario);var rejected=false;try{await sample.Extract(path,destination,CancellationToken.None);}catch(InvalidDataException){rejected=true;}
                if(rejected!=(scenario!=0))throw new Exception("ZIP path/length/symlink validation drift");
            }
            if(File.Exists(Path.Combine(directory,"outside.exe")))throw new Exception("ZIP escaped staging");
        }
        finally{Directory.Delete(directory,true);}
    }
}
