using System.Runtime.InteropServices;
using RemoteAssist.Host.Core;
using RemoteAssist.Host.Core.Ipc;
using RemoteAssist.Host.Core.Security;

SetProcessDpiAwarenessContext(-4);
try
{
    var bootstrap=IpcBootstrap.Parse(args);
    using var stop=new CancellationTokenSource();
    await using var channel=await LocalIpcEndpoint.ConnectAsync(bootstrap.Pipe,bootstrap.Nonce,bootstrap.ServicePid,"adminhelper",stop.Token);
    using var runtime=new AdminDesktopRuntime();
    await using var peer=new IpcPeerSession(channel,runtime.Handle,stop.Token,_=>runtime.Stop(),_=>runtime.Stop());runtime.Peer=peer;peer.Start();
    using var timer=new PeriodicTimer(TimeSpan.FromMilliseconds(100));
    while(await timer.WaitForNextTickAsync(stop.Token))if(runtime.Expired||runtime.Completion.IsCompleted||peer.Completion.IsCompleted){runtime.Stop();break;}
    return 0;
}
catch(Exception ex){LocalDiagnostic.Failure("admin-helper",ex);return 1;}
[DllImport("user32.dll")]static extern bool SetProcessDpiAwarenessContext(nint value);
