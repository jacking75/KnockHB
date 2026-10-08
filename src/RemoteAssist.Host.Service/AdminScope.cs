using System.IO.Pipes;
using System.Security.Cryptography;
using RemoteAssist.Host.Core.Ipc;
using RemoteAssist.Host.Core.Media;
using RemoteAssist.Host.Core.Security;
using RemoteAssist.Host.Core.Windows;

namespace RemoteAssist.Host.Service;

internal sealed class AdminScope:IDisposable
{
    readonly object gate=new();
    readonly TaskCompletionSource ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly IpcInputRequests inputs=new();
    NamedPipeServerStream? server;
    SessionProcess? child;
    IpcPeerSession? peer;
    SharedFrameMap? mapping;
    bool disposed;
    public bool HasExited {get{lock(gate)return disposed||child?.HasExited==true||peer?.Token.IsCancellationRequested==true;}}
    public string Mapping {get{lock(gate)return mapping?.Name??throw new InvalidOperationException("관리자 mapping이 없다.");}}
    public async Task Start(AdminConfiguration configuration,PermissionSnapshot processGrant,RecoveryScope recovery,CancellationToken ct,Action<string> failed)
    {
        var name=Guid.NewGuid().ToString("N");var nonce=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        lock(gate)
        {
            if(disposed)throw new OperationCanceledException(ct);
            mapping=SharedFrameMap.Create(configuration.UserSid);server=LocalIpcEndpoint.CreateServer(name,configuration.UserSid);
            child=SessionProcess.Start(HostChildRole.AdminHelper,configuration.WindowsSessionId,name,nonce,processGrant);
        }
        // Pin the additional producer before any privileged input or capture is enabled.
        await recovery.AttachAdmin(child.Pid,ct);
        var channel=await LocalIpcEndpoint.AcceptAsync(server,child,nonce,ct);
        lock(gate)
        {
            if(disposed){_=channel.DisposeAsync();throw new OperationCanceledException(ct);}
            peer=new(channel,(message,_)=>
            {
                if(message.Type=="admin-ready")
                {if(message.Id!=configuration.Approval.RequestId||message.Body!=null||!ready.TrySetResult())throw new InvalidDataException("오래되거나 중복 관리자 준비 ACK다.");}
                else inputs.Accept(message);
                return Task.CompletedTask;
            },ct,_=>failed("대상자가 관리자 지원을 종료했다."),failed);
            peer.Start();peer.Post("admin-config",configuration with {Mapping=mapping.Name});
        }
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5),ct);
    }
    public Task<int> Input(AdminInputBatch batch,CancellationToken ct)
    {lock(gate){if(disposed||peer is null||!ready.Task.IsCompletedSuccessfully)throw new UnauthorizedAccessException("관리자 입력 준비가 없다.");return inputs.Request(peer,batch,ct);}}
    public void Lease(){lock(gate){if(disposed||peer is null)throw new IOException("관리자 lease 경로 상실이다.");peer.Post("admin-lease");}}
    public void Dispose()
    {
        lock(gate)
        {
            if(disposed)return;disposed=true;child?.Dispose();server?.Dispose();mapping?.Dispose();ready.TrySetCanceled();
            if(peer!=null)_=ClosePeer(peer);
        }
    }
    static async Task ClosePeer(IpcPeerSession peer){try{await peer.DisposeAsync();}catch{}}
}
