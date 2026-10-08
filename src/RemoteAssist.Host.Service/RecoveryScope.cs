using System.IO.Pipes;
using System.Security.Cryptography;
using RemoteAssist.Host.Core.Input;
using RemoteAssist.Host.Core.Ipc;
using RemoteAssist.Host.Core.Windows;

namespace RemoteAssist.Host.Service;

/// <summary>Independent key-up watchdog. Closing its pipe causes restoration; it is deliberately outside the general helper job.</summary>
internal sealed class RecoveryScope:IDisposable
{
    readonly object gate=new();
    readonly IpcAcknowledgements acknowledgements=new();
    NamedPipeServerStream? server;
    SessionProcess? child;
    IpcPeerSession? peer;
    bool disposed;
    public bool HasExited {get{lock(gate)return disposed||child?.HasExited==true||peer?.Token.IsCancellationRequested==true;}}
    public async Task Start(uint session,string sid,int helperPid,string binding,CancellationToken ct,Action<string> failed)
    {
        var pipe=Guid.NewGuid().ToString("N");var nonce=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        lock(gate){if(disposed)throw new OperationCanceledException(ct);server=LocalIpcEndpoint.CreateServer(pipe,sid);child=SessionProcess.Start(HostChildRole.Recovery,session,pipe,nonce);}
        var channel=await LocalIpcEndpoint.AcceptAsync(server,child,nonce,ct);
        lock(gate)
        {
            if(disposed){_=channel.DisposeAsync();throw new OperationCanceledException(ct);}
            peer=new(channel,(message,_)=>{acknowledgements.Accept(message);return Task.CompletedTask;},ct,onStopped:failed);peer.Start();
        }
        await acknowledgements.Request(peer,"recovery-bind",new {SessionId=binding,PermissionGeneration=1,UserSid=sid,HelperPid=helperPid},ct);
    }
    public Task Reserve(RecoveryReservation reservation,CancellationToken ct)
    {lock(gate){if(disposed||peer==null)throw new IOException("복원 감독이 없다.");return acknowledgements.Request(peer,"recovery-reserve",reservation,ct);}}
    public Task AttachAdmin(int pid,CancellationToken ct)
    {lock(gate){if(disposed||peer==null)throw new IOException("복원 감독이 없다.");return acknowledgements.Request(peer,"recovery-admin",new {Pid=pid},ct);}}
    public void Lease()
    {lock(gate){if(disposed||peer==null)throw new IOException("복원 감독이 없다.");peer.Post("recovery-lease");}}
    public void Dispose()
    {
        lock(gate)
        {
            if(disposed)return;disposed=true;
            // No force-kill here: the watchdog must restore ups after ordinary helper has been fenced.
            server?.Dispose();child?.Dispose();if(peer!=null)_=DisposePeer(peer);
        }
    }
    static async Task DisposePeer(IpcPeerSession value){try{await value.DisposeAsync();}catch{}}
}
