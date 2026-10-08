using System.IO;
using System.Windows;
using System.Windows.Media;
using RemoteAssist.Host.Core.Ipc;

namespace RemoteAssist.Host.Tray;

public partial class MainWindow
{
    IpcPeerSession? peer;
    ConsentWindow? consent;
    NativeTrayIcon? icon;
    string? currentSession,pendingId;
    bool closed;
    void InitializeRuntime()
    {
        SourceInitialized+=(_,_)=>icon=new NativeTrayIcon(this);
        StateChanged+=(_,_)=>{if(WindowState==WindowState.Minimized)Hide();};
        Closing+=(_,_)=>{closed=true;EndLocal();TryPost("stop");icon?.Dispose();};
    }
    public void Attach(IpcPeerSession value){peer=value;StartButton.IsEnabled=true;Status.Text="지원 코드를 만들면 대상자의 승인 후 연결한다.";}
    public void Disconnected(string reason)
    {EndLocal();peer=null;StartButton.IsEnabled=false;Status.Text=reason;}
    public void Receive(LocalIpcMessage message)
    {
        if(closed)return;
        switch(message.Type)
        {
            case "credentials":
                var body=message.Body??throw new InvalidDataException("코드 표시 누락");
                currentSession=body.GetProperty("sessionId").GetString();
                Code.Text=body.GetProperty("code").GetString();Pin.Text="PIN "+body.GetProperty("pin").GetString();StartButton.IsEnabled=false;
                break;
            case "state":
                var state=message.Body??throw new InvalidDataException("상태 표시 누락");
                if(state.TryGetProperty("ended",out var ended)&&ended.GetBoolean()){EndLocal();StartButton.IsEnabled=peer!=null;}
                Status.Text=state.GetProperty("status").GetString();
                var active=Status.Text=="승인한 화면 공유 중";
                SessionBorder.BorderBrush=active?Brushes.Crimson:Brushes.SlateGray;
                Topmost=active;icon?.SetActive(active);
                break;
            case "consent-request":
                var request=message.Body??throw new InvalidDataException("동의 본문 누락");
                if(consent!=null||pendingId!=null||currentSession!=request.GetProperty("sessionId").GetString()||!Guid.TryParseExact(message.Id,"N",out _))throw new InvalidDataException("동의 대상 불일치");
                pendingId=message.Id;
                var session=currentSession;var connection=request.GetProperty("connectionId").GetString();var id=message.Id;
                var dialog=new ConsentWindow(request.GetProperty("displayName").GetString()??"지원자",request.GetProperty("monitor").GetString()??""){Owner=this};consent=dialog;
                Dispatcher.BeginInvoke(new Action(()=>
                {
                    if(pendingId!=id||consent!=dialog)return;
                    Show();WindowState=WindowState.Normal;Activate();dialog.ShowDialog();
                    if(pendingId!=id||currentSession!=session||consent!=dialog)return;
                    pendingId=null;consent=null;
                    TryPost("consent",new {sessionId=session,connectionId=connection,capabilities=dialog.ApprovedScreen?1:0},id);
                }));
                break;
            default:throw new InvalidDataException("Tray 상태 메시지 오류");
        }
    }
    void StartClicked(object sender,RoutedEventArgs e){StartButton.IsEnabled=false;Status.Text="지원 코드를 준비 중이다.";TryPost("start");}
    void StopClicked(object sender,RoutedEventArgs e){EndLocal();Status.Text="종료 요청을 보냈다.";TryPost("stop");}
    void EndLocal()
    {
        pendingId=null;currentSession=null;
        var dialog=consent;consent=null;dialog?.Close();
        Code.Text="—";Pin.Text="PIN —";Topmost=false;SessionBorder.BorderBrush=Brushes.SlateGray;icon?.SetActive(false);
    }
    void TryPost(string type,object? body=null,string id="")
    {try{peer?.Post(type,body,id);}catch(InvalidOperationException){Disconnected("서비스 연결이 종료됐다.");}}
}
