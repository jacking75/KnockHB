namespace RemoteAssist.Host.Core.Ipc;

public sealed class IpcBootstrap
{
    public required string Pipe {get;init;}
    public required string Nonce {get;init;}
    public required int ServicePid {get;init;}
    public static IpcBootstrap Parse(string[] args)
    {
        if(args.Length!=6)throw new ArgumentException("서비스가 실행한 헬퍼만 시작할 수 있다.");
        var fields=new Dictionary<string,string>(StringComparer.Ordinal);
        for(var i=0;i<args.Length;i+=2)
            if(args[i] is not ("--connect" or "--nonce" or "--service-pid")||!fields.TryAdd(args[i],args[i+1]))throw new ArgumentException("잘못된 헬퍼 시작 인자다.");
        if(!fields.TryGetValue("--connect",out var pipe)||!Guid.TryParseExact(pipe,"N",out _)||
           !fields.TryGetValue("--nonce",out var nonce)||nonce.Length!=64||!nonce.All(Uri.IsHexDigit)||
           !fields.TryGetValue("--service-pid",out var pid)||!int.TryParse(pid,out var servicePid)||servicePid<=0)
            throw new ArgumentException("서비스 IPC bootstrap 오류다.");
        return new() {Pipe=pipe,Nonce=nonce,ServicePid=servicePid};
    }
}
