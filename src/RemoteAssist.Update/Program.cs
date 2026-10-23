using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using RemoteAssist.Host.Core.Deployment;

return await UpdateProgram.Run(args);
static class UpdateProgram
{
    record Policy(string Endpoint,string PublicKey);
    static readonly string root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"RemoteAssist");
    static readonly string data=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"RemoteAssist");
    public static async Task<int> Run(string[] args)
    {
        string? ownedDownload=null,ownedStage=null;
        InitialSupportGrant? initialGrant=null;
        try
        {
            // A downloaded, verified release folder can be started by double-click.
            // The executable manifest requests the one initial Windows elevation.
            var initialLaunch=args.Length==0 || args is ["support"];
            if(args.Length==0&&DevSupportLauncher.IsInstalledDirectory(AppContext.BaseDirectory))args=["support-dev"];
            if(args.Length==0)args=Path.GetFullPath(AppContext.BaseDirectory).StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)
                ? ["support"]
                : ["install",Path.Combine(AppContext.BaseDirectory,"release.json"),Path.Combine(AppContext.BaseDirectory,"release.sig"),Path.Combine(AppContext.BaseDirectory,"package.zip"),Path.Combine(AppContext.BaseDirectory,"trusted-public.pem")];
            using var identity=WindowsIdentity.GetCurrent();if(!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))throw new UnauthorizedAccessException("관리자 설치 권한이 필요하다.");
            if(Directory.Exists(root))InstallationState.DemandProtected(root);
            if(Directory.Exists(data))InstallationState.DemandProtected(data);
            if(args is ["support-dev"])
            {
                await DevSupportLauncher.Run();
                return 0;
            }
            if(args.Length==1&&args[0] is "update" or "support"){var forwarded=await ForwardToActive(args);if(forwarded.HasValue)return forwarded.Value;}
            if(args is ["support"])
            {
                // 다시 실행하면 허가를 새로 쓰고 서비스를 다시 시작한다. 진행 중인 지원은 다시 접속한다.
                InstallationState.CheckDirectory(AppContext.BaseDirectory);
                DemandRecoveredInstall();
                initialGrant=InitialSupportLaunch.Issue();
                var supported=await Installer("Support",AppContext.BaseDirectory);
                if(supported==0)initialGrant=null; // 성공한 허가는 서비스가 계속 쓴다.
                return supported;
            }
            if(args.Length==1&&args[0]=="recover")return await Installer("Recover",root);
            if(args.Length==1&&args[0]=="remove")return await Installer("Remove",root);
            string manifest,signature,archive,key;bool online=args.Length==1&&args[0]=="update";
            using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(10));
            if(online)
            {
                if(!identity.IsSystem)throw new UnauthorizedAccessException("자동 업데이트는 보호된 SYSTEM 작업 전용이다.");
                InstallationState.DemandProtected(Path.Combine(root,"update.policy.json"));var policy=JsonSerializer.Deserialize<Policy>(await File.ReadAllBytesAsync(Path.Combine(root,"update.policy.json"),deadline.Token))??throw new InvalidDataException("업데이트 정책 누락이다.");
                if(!Uri.TryCreate(policy.Endpoint,UriKind.Absolute,out var endpoint)||endpoint.Scheme!="https"||endpoint.UserInfo.Length!=0||endpoint.Query.Length!=0||endpoint.Fragment.Length!=0)throw new InvalidDataException("고정 HTTPS endpoint 오류다.");
                Directory.CreateDirectory(Path.Combine(data,"updates"));CleanupAbandoned("updates");var directory=Path.Combine(data,"updates",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);ownedDownload=directory;
                using var http=new HttpClient(new HttpClientHandler {AllowAutoRedirect=false}) {Timeout=Timeout.InfiniteTimeSpan};
                manifest=Path.Combine(directory,"release.json");signature=Path.Combine(directory,"release.sig");archive=Path.Combine(directory,"package.zip");key=policy.PublicKey;
                await Download(http,new Uri(endpoint,"release.json"),manifest,131072,deadline.Token);await Download(http,new Uri(endpoint,"release.sig"),signature,1024,deadline.Token);
                var approved=ReleaseManifest.Verify(await File.ReadAllBytesAsync(manifest,deadline.Token),await File.ReadAllBytesAsync(signature,deadline.Token),key);
                InstallationState.Atomic(Path.Combine(root,"update.notice.json"),JsonSerializer.SerializeToUtf8Bytes(new {Version=approved.Version,Status="새 버전은 지원 세션 종료 뒤 설치한다."}));
                var highest=File.ReadAllText(Path.Combine(root,"highest.version")).Trim();if(System.Version.Parse(approved.Version)<=System.Version.Parse(highest))return 0;
                // Wait for local support to finish; updater never terminates an active consented session.
                using var gate=TrySessionGate();if(gate==null)return 0;
                await Download(http,new Uri(endpoint,"package.zip"),archive,ReleaseManifest.MaxPackageBytes,deadline.Token);
            }
            else
            {
                if(args.Length!=5||args[0]!="install")throw new ArgumentException("install <release.json> <release.sig> <package.zip> <trusted-public.pem> | update | recover | remove");
                manifest=Path.GetFullPath(args[1]);signature=Path.GetFullPath(args[2]);archive=Path.GetFullPath(args[3]);key=File.ReadAllText(Path.GetFullPath(args[4]));
            }
            var raw=await File.ReadAllBytesAsync(manifest,deadline.Token);var release=ReleaseManifest.Verify(raw,await File.ReadAllBytesAsync(signature,deadline.Token),key);
            Directory.CreateDirectory(data);ProtectDirectory(data);using var session=TrySessionGate();if(session==null)return 2;
            var staging=Path.Combine(data,"staging",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.GetDirectoryName(staging)!);ProtectDirectory(Path.GetDirectoryName(staging)!);CleanupAbandoned("staging");ownedStage=staging;
            await release.Extract(archive,staging,deadline.Token);await File.WriteAllBytesAsync(Path.Combine(staging,"release.json"),raw,deadline.Token);await File.WriteAllBytesAsync(Path.Combine(staging,"release.sig"),await File.ReadAllBytesAsync(signature,deadline.Token),deadline.Token);await File.WriteAllTextAsync(Path.Combine(staging,"trusted-public.pem"),key,deadline.Token);
            ProtectDirectory(staging);
            if(initialLaunch){DemandRecoveredInstall();initialGrant=InitialSupportLaunch.Issue();}
            var installed=await Installer("Install",staging);
            if(installed==0)initialGrant=null;
            return installed;
        }
        catch(Exception ex){Console.Error.WriteLine("업데이트/설치 실패: "+ex.Message);return 1;}
        finally{if(initialGrant!=null){try{InitialSupportLaunch.Revoke(initialGrant.Id);}catch{Console.Error.WriteLine("실패한 실행의 지원 허가를 지우지 못했다.");}}foreach(var path in new[]{ownedDownload,ownedStage})if(path!=null&&Directory.Exists(path)){try{InstallationState.DemandProtected(path);Directory.Delete(path,true);}catch{Console.Error.WriteLine("보호된 업데이트 임시 디렉터리 정리가 보류됐다.");}}}
    }
    static void DemandRecoveredInstall()
    {
        var transaction=Path.Combine(root,"transaction.json");if(!File.Exists(transaction))return;
        InstallationState.DemandProtected(transaction);
        using var document=JsonDocument.Parse(File.ReadAllBytes(transaction));
        if(document.RootElement.GetProperty("Status").GetString()=="pending")
            throw new InvalidOperationException("중단된 설치 복구가 필요하다. 설치된 RemoteAssist.Update.exe recover를 실행한 뒤 지원을 다시 시작해야 한다.");
    }
    static async Task<int?> ForwardToActive(string[] args)
    {
        using var service=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\RemoteAssistHost");var raw=service?.GetValue("ImagePath") as string;if(raw==null)return null;
        var image=raw.Trim('"');var prefix=Path.Combine(root,"versions")+Path.DirectorySeparatorChar;
        if(!image.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)||Path.GetFileName(image)!="RemoteAssist.Host.Service.exe")throw new UnauthorizedAccessException("현재 서비스 소유권 오류다.");
        var directory=Path.GetDirectoryName(image)!;var worker=Path.Combine(directory,"RemoteAssist.Update.exe");if(string.Equals(Environment.ProcessPath,worker,StringComparison.OrdinalIgnoreCase))return null;
        InstallationState.DemandProtected(directory);if(!File.Exists(Path.Combine(directory,"release.json")))throw new InvalidDataException("현재 버전 manifest 누락이다.");InstallationState.CheckDirectory(directory);
        var start=new ProcessStartInfo(worker){UseShellExecute=false,CreateNoWindow=true};foreach(var arg in args)start.ArgumentList.Add(arg);
        using var process=Process.Start(start)??throw new IOException("현재 updater 시작 실패다.");using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(14));try{await process.WaitForExitAsync(deadline.Token);}catch{process.Kill(true);throw;}return process.ExitCode;
    }
    static void CleanupAbandoned(string category)
    {
        var parent=Path.Combine(data,category);if(!Directory.Exists(parent))return;InstallationState.DemandProtected(parent);
        foreach(var directory in Directory.EnumerateDirectories(parent))if(Guid.TryParseExact(Path.GetFileName(directory),"N",out _)&&Directory.GetLastWriteTimeUtc(directory)<DateTime.UtcNow.AddDays(-1)){InstallationState.DemandProtected(directory);Directory.Delete(directory,true);}
    }
    static FileStream? TrySessionGate(){try{return new FileStream(Path.Combine(data,"session.gate"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.Read);}catch(IOException){return null;}}
    static void ProtectDirectory(string path)
    {
        var start=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"icacls.exe")){UseShellExecute=false,CreateNoWindow=true};
        foreach(var a in new[]{path,"/inheritance:r","/grant:r","*S-1-5-18:(OI)(CI)F","*S-1-5-32-544:(OI)(CI)F"})start.ArgumentList.Add(a);
        RunAcl(start);
        var owner=new ProcessStartInfo(start.FileName){UseShellExecute=false,CreateNoWindow=true};foreach(var arg in new[]{path,"/setowner","*S-1-5-32-544","/T"})owner.ArgumentList.Add(arg);RunAcl(owner);
    }
    static void RunAcl(ProcessStartInfo start)
    {
        using var process=Process.Start(start)??throw new IOException("보호 ACL 명령 시작 실패다.");
        if(!process.WaitForExit(30000)){process.Kill(true);process.WaitForExit(2000);throw new TimeoutException("보호 ACL 명령의 30초 기한을 넘었다.");}
        if(process.ExitCode!=0)throw new UnauthorizedAccessException("보호 ACL 명령 실패다.");
    }
    static async Task Download(HttpClient http,Uri url,string target,long limit,CancellationToken ct)
    {
        using var response=await http.GetAsync(url,HttpCompletionOption.ResponseHeadersRead,ct);response.EnsureSuccessStatusCode();if(response.Content.Headers.ContentLength>limit)throw new IOException("다운로드 상한이다.");
        await using var input=await response.Content.ReadAsStreamAsync(ct);await using var output=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None);var buffer=new byte[65536];long total=0;int n;
        while((n=await input.ReadAsync(buffer,ct))!=0){if((total+=n)>limit)throw new IOException("다운로드 상한이다.");await output.WriteAsync(buffer.AsMemory(0,n),ct);}await output.FlushAsync(ct);
    }
    static async Task<int> Installer(string action,string path)
    {
            var script=Path.Combine(path,"install.ps1");var start=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe")){UseShellExecute=false,CreateNoWindow=true};
        InstallationState.DemandProtected(script);
        // The package was verified before protected staging; the root bootstrap was
        // copied from that verified package. No persistent execution policy is changed.
        InstallationState.CheckDirectory(path);
        foreach(var arg in new[]{"-NoLogo","-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File",script,"-Action",action,"-Package",path})start.ArgumentList.Add(arg);
        using var process=Process.Start(start)??throw new IOException("installer 시작 실패다.");using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(3));try{await process.WaitForExitAsync(deadline.Token);}catch{process.Kill(true);throw;}return process.ExitCode;
    }
}
