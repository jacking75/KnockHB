using System.Configuration;
using System.Windows;
using RemoteAssist.Host.Core.Ipc;
using RemoteAssist.Host.Core;

namespace RemoteAssist.Host.Tray;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    readonly CancellationTokenSource lifetime=new();
    IpcPeerSession? peer;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var window=new MainWindow();MainWindow=window;window.Show();
        try
        {
            var bootstrap=IpcBootstrap.Parse(e.Args);
            var channel=await LocalIpcEndpoint.ConnectAsync(bootstrap.Pipe,bootstrap.Nonce,bootstrap.ServicePid,"tray",lifetime.Token);
            peer=new IpcPeerSession(channel,messageHandler,lifetime.Token,onStopped:reason=>window.Dispatcher.BeginInvoke(new Action(()=>window.Disconnected(reason))));
            window.Attach(peer);peer.Start();
            await peer.Completion;
            if(!lifetime.IsCancellationRequested)window.Disconnected(peer.EndReason);
            await peer.DisposeAsync();
        }
        catch(Exception ex){LocalDiagnostic.Failure("tray",ex);window.Disconnected("서비스 연결을 사용할 수 없다. "+ex.GetType().Name);}
        Task messageHandler(LocalIpcMessage message,CancellationToken ct)=>window.Dispatcher.InvokeAsync(()=>window.Receive(message)).Task;
    }
    protected override void OnExit(ExitEventArgs e)
    {
        try{peer?.Post("stop");}catch(InvalidOperationException){}
        lifetime.Cancel();base.OnExit(e);
    }
}

