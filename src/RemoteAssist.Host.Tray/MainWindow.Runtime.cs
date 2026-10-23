using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Text.Json;
using RemoteAssist.Host.Core.Ipc;
using RemoteAssist.Protocol;

namespace RemoteAssist.Host.Tray;

/// <summary>
/// 대상 PC 사용자 화면. 대상자는 아무것도 승인하지 않는다. 지원 ID·접속 상태를 보여 주고,
/// 원할 때 즉시 종료하거나 채팅·파일 보내기를 할 수 있다.
/// </summary>
public partial class MainWindow
{
    IpcPeerSession? peer;
    NativeTrayIcon? icon;
    SharingIndicator? indicator;
    bool closed, active;
    string? fileId;

    void InitializeRuntime()
    {
        SourceInitialized+=(_,_)=>icon=new NativeTrayIcon(this,StopLocal);
        StateChanged+=(_,_)=>{if(WindowState==WindowState.Minimized)Hide();};
        Closing+=(_,_)=>{closed=true;TryPost("stop");EndLocal();icon?.Dispose();};
    }
    public void Attach(IpcPeerSession value){peer=value;Status.Text="서비스에 연결했다.";}
    public void Disconnected(string reason){EndLocal();peer=null;Status.Text=reason;}

    public void Receive(LocalIpcMessage message)
    {
        if(closed)return;
        switch(message.Type)
        {
            case "device":
                var device=message.Body??throw new InvalidDataException("지원 ID 누락");
                Code.Text=device.GetProperty("code").GetString();
                PermissionNotice.Text=device.GetProperty("granted").GetBoolean()
                    ?"지원자가 이 지원 ID로 접속하면 화면·제어·관리자 작업을 바로 할 수 있다. 연결이 끊기면 자동으로 다시 접속을 기다린다."
                    :"관리자 권한으로 RemoteAssist를 실행하면 지원을 시작한다.";
                break;
            case "supporter":
                Status.Text=(message.Body?.GetProperty("displayName").GetString()??"지원자")+" 연결 중";
                break;
            case "update-status":UpdateStatus.Text=message.Body?.GetProperty("Status").GetString()??"";break;
            case "state":
                var state=message.Body??throw new InvalidDataException("상태 누락");
                Status.Text=state.GetProperty("status").GetString();
                var sharing=state.TryGetProperty("active",out var value)&&value.GetBoolean();
                if(sharing)
                {
                    if(!active&&state.TryGetProperty("monitor",out var monitor))
                    {indicator?.Dispose();indicator=new SharingIndicator(monitor.Deserialize<MonitorDescriptor>()!,StopLocal);}
                    Status.Text="원격 지원 중 · 즉시 종료 버튼이나 Ctrl+Alt+Shift+F10으로 끝낼 수 있다.";
                    SetActive(true);
                }
                else if(state.TryGetProperty("ended",out var ended)&&ended.GetBoolean())EndLocal();
                break;
            case "chat":
                var chat=Feature(message);if(chat.T!="chat")throw new InvalidDataException("채팅 방향 오류다.");AppendChat("지원자: "+chat.Text);break;
            case "clipboard-request":
                TryPost("clipboard-response",Clipboard(Feature(message)),message.Id);break;
            case "file-state":
                var file=message.Body?.Deserialize<RemoteAssist.Host.Core.Files.FileTransferState>()??throw new InvalidDataException("파일 상태 누락이다.");
                FileStatus.Text=(file.Direction=="to-host"?"받은 파일 ":"보낸 파일 ")+file.State+" · "+file.Size+" bytes";
                if(file.Id==fileId){fileId=null;FileCancel.IsEnabled=false;}
                break;
            default:throw new InvalidDataException("Tray 상태 메시지 오류");
        }
    }

    static FeatureMessage Feature(LocalIpcMessage message)
    {
        var value=message.Body?.Deserialize<FeatureMessage>()??throw new InvalidDataException("보조 기능 본문 누락이다.");
        _=FeatureMessage.Parse(ProtocolJson.Encode(value));
        if(value.Id!=message.Id)throw new InvalidDataException("보조 기능 ID 불일치다.");
        return value;
    }

    /// <summary>지원자의 클립보드 요청. WPF 클립보드는 이 STA 스레드에서만 쓸 수 있다.</summary>
    static FeatureMessage Clipboard(FeatureMessage request)
    {
        try
        {
            if(request.T=="clipboard-get")
            {
                var text=System.Windows.Clipboard.ContainsText(TextDataFormat.UnicodeText)?System.Windows.Clipboard.GetText(TextDataFormat.UnicodeText):"";
                return FeatureMessage.ClipboardTextReply(request,text);
            }
            if(request.T=="clipboard-set")
            {
                if(request.Text!.Length==0)System.Windows.Clipboard.Clear();else System.Windows.Clipboard.SetText(request.Text,TextDataFormat.UnicodeText);
                return request with {T="clipboard-applied",Text=null};
            }
            throw new InvalidDataException("클립보드 요청 방향 오류다.");
        }
        catch(System.Runtime.InteropServices.ExternalException){return request with {T="clipboard-rejected",Text=null,Hash=null,Code="clipboard-busy"};}
    }

    void SetActive(bool value)
    {
        active=value;
        ChatText.IsEnabled=ChatSend.IsEnabled=value;
        FileSend.IsEnabled=value;
        SessionBorder.BorderBrush=value?Brushes.Crimson:Brushes.SlateGray;
        Topmost=value;
        try{icon?.SetActive(value);}catch{ /* 서비스 연결이 이미 끊겼다. */ }
    }
    void ChatClicked(object sender,RoutedEventArgs e)
    {
        if(!active||string.IsNullOrEmpty(ChatText.Text))return;
        var text=ChatText.Text;if(!FeatureMessage.ValidUnicode(text)||text.Contains('\0'))return;
        TryPost("chat",new {text},Guid.NewGuid().ToString("N"));AppendChat("나: "+text);ChatText.Clear();
    }
    void AppendChat(string text)
    {var lines=ChatHistory.Text+text+Environment.NewLine;if(lines.Length>8192)lines=lines[^8192..];ChatHistory.Text=lines;ChatHistory.ScrollToEnd();}
    void FileClicked(object sender,RoutedEventArgs e)
    {
        if(!active||fileId!=null)return;
        var dialog=new Microsoft.Win32.OpenFileDialog {Title="지원자에게 보낼 파일",CheckFileExists=true,Multiselect=false};
        if(dialog.ShowDialog(this)!=true)return;
        fileId=Guid.NewGuid().ToString("N");FileCancel.IsEnabled=true;FileStatus.Text="파일을 보내는 중이다.";
        TryPost("file-send",new {path=dialog.FileName},fileId);
    }
    void FileCancelClicked(object sender,RoutedEventArgs e){if(fileId!=null)TryPost("file-cancel",id:fileId);}
    void StopClicked(object sender,RoutedEventArgs e)=>StopLocal();
    void StopLocal(){TryPost("stop");EndLocal();Status.Text="지원을 중지했다. 다시 지원받으려면 관리자 권한으로 실행한다.";}
    void EndLocal()
    {
        SetActive(false);
        ChatText.Clear();fileId=null;FileCancel.IsEnabled=false;
        indicator?.Dispose();indicator=null;
    }
    void TryPost(string type,object? body=null,string id="")
    {try{peer?.Post(type,body,id);}catch(InvalidOperationException){Disconnected("서비스 연결이 종료됐다.");}}
}
