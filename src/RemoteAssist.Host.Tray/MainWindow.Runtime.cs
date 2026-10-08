using System.IO;
using System.Windows;
using System.Windows.Media;
using RemoteAssist.Host.Core.Ipc;
using System.Text.Json;
using RemoteAssist.Protocol;

namespace RemoteAssist.Host.Tray;

public partial class MainWindow
{
    IpcPeerSession? peer;
    ConsentWindow? consent;
    AdminConsentWindow? adminConsent;
    string? adminPending;
    NativeTrayIcon? icon;
    SharingIndicator? indicator;
    string? currentSession,pendingId;
    bool closed;
    void InitializeRuntime()
    {
        SourceInitialized+=(_,_)=>icon=new NativeTrayIcon(this,StopLocal);
        StateChanged+=(_,_)=>{if(WindowState==WindowState.Minimized)Hide();};
        Closing+=(_,_)=>{closed=true;TryPost("stop");EndLocal();icon?.Dispose();};
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
                var active=state.TryGetProperty("active",out var sharing)&&sharing.GetBoolean();
                if(active)
                {
                    var monitor=state.GetProperty("monitor").Deserialize<MonitorDescriptor>()??throw new InvalidDataException("모니터 표시 바인딩 누락");
                    indicator??=new SharingIndicator(monitor,StopLocal);
                    Status.Text=(state.GetProperty("capabilities").GetInt32()&2)!=0?"화면 공유·원격 제어 중 · Ctrl+Alt+Shift+F10으로 즉시 종료":"화면 공유 중 · Ctrl+Alt+Shift+F10으로 즉시 종료";
                }
                else {indicator?.Dispose();indicator=null;}
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
                    TryPost("consent",new {sessionId=session,connectionId=connection,capabilities=dialog.ApprovedScreen?(dialog.ApprovedControl?(dialog.ApprovedAdmin?7:3):1):0},id);
                }));
                break;
            case "admin-request":
                var adminBody=message.Body??throw new InvalidDataException("관리자 동의 본문 누락이다.");
                if(adminConsent!=null||currentSession!=adminBody.GetProperty("sessionId").GetString()||!Guid.TryParseExact(message.Id,"N",out _))throw new InvalidDataException("관리자 동의 대상 불일치다.");
                var adminId=message.Id;var adminSession=currentSession;var adminConnection=adminBody.GetProperty("connectionId").GetString();var generation=adminBody.GetProperty("permissionGeneration").GetInt64();
                adminPending=adminId;
                var adminDialog=new AdminConsentWindow(adminBody.GetProperty("displayName").GetString()??"지원자",adminBody.GetProperty("monitor").GetString()??""){Owner=this};adminConsent=adminDialog;
                Dispatcher.BeginInvoke(new Action(()=>
                {
                    if(adminPending!=adminId||adminConsent!=adminDialog)return;
                    Show();WindowState=WindowState.Normal;Activate();adminDialog.ShowDialog();
                    if(adminPending!=adminId||currentSession!=adminSession||adminConsent!=adminDialog)return;
                    adminPending=null;adminConsent=null;
                    TryPost("admin-response",new {sessionId=adminSession,connectionId=adminConnection,permissionGeneration=generation,approved=adminDialog.Approved},adminId);
                }));break;
            default:throw new InvalidDataException("Tray 상태 메시지 오류");
        }
    }
    void StartClicked(object sender,RoutedEventArgs e){StartButton.IsEnabled=false;Status.Text="지원 코드를 준비 중이다.";TryPost("start");}
    void StopClicked(object sender,RoutedEventArgs e)=>StopLocal();
    void StopLocal(){TryPost("stop");EndLocal();Status.Text="종료 요청을 보냈다.";}
    void EndLocal()
    {
        pendingId=null;currentSession=null;
        adminPending=null;var adminDialog=adminConsent;adminConsent=null;adminDialog?.Close();
        var dialog=consent;consent=null;dialog?.Close();
        indicator?.Dispose();indicator=null;
        Code.Text="—";Pin.Text="PIN —";Topmost=false;SessionBorder.BorderBrush=Brushes.SlateGray;
        try{icon?.SetActive(false);}catch{ /* The stop was already sent, or the authenticated service lease is lost. */ }
    }
    void TryPost(string type,object? body=null,string id="")
    {try{peer?.Post(type,body,id);}catch(InvalidOperationException){Disconnected("서비스 연결이 종료됐다.");}}
}
