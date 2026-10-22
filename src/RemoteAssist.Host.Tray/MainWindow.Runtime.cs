using System.IO;
using System.Windows;
using System.Windows.Media;
using RemoteAssist.Host.Core.Ipc;
using System.Text.Json;
using RemoteAssist.Protocol;
using RemoteAssist.Host.Core.Runbooks;

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
    DateTimeOffset? initialSupportExpires;
    int capabilities;
    int connectionGeneration=1;
    LocalActionWindow? fileDialog;
    ActionConsentWindow? actionDialog;
    string? pendingRunbookId;
    string? fileId;
    void InitializeRuntime()
    {
        SourceInitialized+=(_,_)=>icon=new NativeTrayIcon(this,StopLocal);
        StateChanged+=(_,_)=>{if(WindowState==WindowState.Minimized)Hide();};
        Closing+=(_,_)=>{closed=true;PostStop();EndLocal();icon?.Dispose();};
    }
    public void Attach(IpcPeerSession value){peer=value;StartButton.IsEnabled=true;Status.Text="지원 코드를 만들면 대상자의 승인 후 연결한다.";}
    public void Disconnected(string reason)
    {EndLocal();peer=null;StartButton.IsEnabled=false;Status.Text=reason;}
    public void Receive(LocalIpcMessage message)
    {
        if(closed)return;
        switch(message.Type)
        {
            case "initial-support":
                var initial=message.Body??throw new InvalidDataException("최초 지원 권한 정보 누락이다.");
                initialSupportExpires=initial.GetProperty("expiresAt").GetDateTimeOffset();
                if(initialSupportExpires<=DateTimeOffset.UtcNow)throw new InvalidDataException("최초 지원 권한이 만료됐다.");
                PermissionNotice.Text="최초 관리자 실행으로 이번 지원의 화면·제어·관리자·파일 권한을 부여했다. 신뢰하는 지원자에게만 코드/PIN을 전달한다. 즉시 종료로 모든 지원을 끝낸다.";
                Status.Text="이번 지원을 준비 중이다. 추가 승인 없이 연결한다.";
                StartButton.IsEnabled=false;
                break;
            case "credentials":
                var body=message.Body??throw new InvalidDataException("코드 표시 누락");
                currentSession=body.GetProperty("sessionId").GetString();
                Code.Text=body.GetProperty("code").GetString();Pin.Text="PIN "+body.GetProperty("pin").GetString();StartButton.IsEnabled=false;
                break;
            case "update-status":UpdateStatus.Text=message.Body?.GetProperty("Status").GetString()??"";break;
            case "state":
                var state=message.Body??throw new InvalidDataException("상태 표시 누락");
                if(state.TryGetProperty("generation",out var generationState))connectionGeneration=generationState.GetInt32();
                if(state.TryGetProperty("ended",out var ended)&&ended.GetBoolean()){EndLocal();StartButton.IsEnabled=peer!=null;}
                Status.Text=state.GetProperty("status").GetString();
                var active=state.TryGetProperty("active",out var sharing)&&sharing.GetBoolean();
                if(active)
                {
                    capabilities=state.GetProperty("capabilities").GetInt32();_=FeatureContract.Permissions(capabilities);
                    if((capabilities&1)!=0)
                    {
                        var monitor=state.GetProperty("monitor").Deserialize<MonitorDescriptor>()??throw new InvalidDataException("모니터 표시 바인딩 누락");
                        indicator?.Dispose();indicator=new SharingIndicator(monitor,StopLocal);
                    }
                    Status.Text=((capabilities&2)!=0?"화면 공유·원격 제어 중":(capabilities&1)!=0?"화면 공유 중":"승인한 진단·보조 기능 연결 중")+" · Ctrl+Alt+Shift+F10으로 즉시 종료";
                }
                else {capabilities=state.TryGetProperty("capabilities",out var caps)?caps.GetInt32():0;indicator?.Dispose();indicator=null;}
                ChatText.IsEnabled=ChatSend.IsEnabled=active&&(capabilities&128)!=0;
                FileSend.IsEnabled=active&&(capabilities&8)!=0;
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
                    RemoteAssist.Host.Core.Diagnostics.CaseTrace.Write("consent-ui",session,dialog.SelectedCapabilities==0?0:1,
                        new {id,connectionId=connection,mask=dialog.SelectedCapabilities,decision=dialog.Decision});
                    TryPost("consent",new {sessionId=session,connectionId=connection,capabilities=dialog.SelectedCapabilities},id);
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
                    RemoteAssist.Host.Core.Diagnostics.CaseTrace.Write("admin-consent-ui",adminSession,generation,new {id=adminId,approved=adminDialog.Approved});
                    TryPost("admin-response",new {sessionId=adminSession,connectionId=adminConnection,permissionGeneration=generation,approved=adminDialog.Approved},adminId);
                }));break;
            case "runbook-request":
                var bookRequest=message.Body?.Deserialize<RunbookMessage>()??throw new InvalidDataException("Runbook 요청 누락이다.");
                var book=RunbookCatalog.Demand(bookRequest);
                if((capabilities&64)==0||book.RequiresAdmin&&(capabilities&4)==0||bookRequest.SessionId!=currentSession||bookRequest.Generation!=connectionGeneration||bookRequest.PermissionGeneration!=1||bookRequest.Id!=message.Id||actionDialog!=null)throw new UnauthorizedAccessException("현재 Runbook 승인 요청이 아니다.");
                var bookSession=currentSession;var bookDialog=new ActionConsentWindow(book.RequiresAdmin?"이번 관리자 Runbook 실행 승인":"이번 Runbook 실행 승인",$"{book.Title}\n버전 {book.BookVersion} · SHA256 {book.Hash}\n인자 detail={bookRequest.Args!["detail"]}\n{book.Impact}\n최대 20초 · 출력 제한 · 취소는 이미 수행한 변경을 복원하지 않는다.\n\n실제 실행 스크립트:\n{book.Source}"){Owner=this};actionDialog=bookDialog;pendingRunbookId=bookRequest.Id;
                Dispatcher.BeginInvoke(new Action(()=>{if(actionDialog!=bookDialog||currentSession!=bookSession)return;Show();WindowState=WindowState.Normal;Activate();bookDialog.ShowDialog();if(actionDialog!=bookDialog||currentSession!=bookSession)return;actionDialog=null;pendingRunbookId=null;RemoteAssist.Host.Core.Diagnostics.CaseTrace.Write("runbook-consent-ui",bookSession,1,new {id=bookRequest.Id,approved=bookDialog.Approved});TryPost("runbook-response",new {approved=bookDialog.Approved},bookRequest.Id);}));break;
            case "runbook-dismiss":
                RemoteAssist.Host.Core.Diagnostics.CaseTrace.Write("dismiss-received",currentSession,1,new {id=message.Id});
                if(message.Body!=null||!Guid.TryParseExact(message.Id,"N",out _)||currentSession==null)throw new InvalidDataException("Runbook 창 닫기 바인딩 오류다.");
                if(pendingRunbookId==message.Id){var closing=actionDialog;actionDialog=null;pendingRunbookId=null;closing?.Close();}
                RemoteAssist.Host.Core.Diagnostics.CaseTrace.Write("dialog-closed",currentSession,1,new {id=message.Id,dialogPresent=actionDialog!=null});
                // The dialog may have closed just before cancellation. Its old response is ignored by the service.
                TryPost("runbook-dismissed",id:message.Id);break;
            case "monitor-request":
                var monitorRequest=message.Body??throw new InvalidDataException("모니터 요청 누락이다.");
                if((capabilities&1)==0||currentSession!=monitorRequest.GetProperty("sessionId").GetString()||actionDialog!=null)throw new UnauthorizedAccessException("현재 모니터 요청이 아니다.");
                var target=monitorRequest.GetProperty("monitor").Deserialize<MonitorDescriptor>()!;var monitorId=message.Id;var monitorSession=currentSession;
                var monitorDialog=new ActionConsentWindow("공유 모니터 전환 승인",$"{target.Identity}\n{target.Width}×{target.Height} @ ({target.X},{target.Y})\n이 모니터의 화면을 지원자에게 공유한다. 거부하면 기존 모니터를 유지한다."){Owner=this};actionDialog=monitorDialog;
                Dispatcher.BeginInvoke(new Action(()=>{if(actionDialog!=monitorDialog||currentSession!=monitorSession)return;monitorDialog.ShowDialog();if(actionDialog!=monitorDialog||currentSession!=monitorSession)return;actionDialog=null;TryPost("monitor-response",new {approved=monitorDialog.Approved},monitorId);}));break;
            case "file-request":case "file-select":
                if((capabilities&8)==0||currentSession==null||fileDialog!=null)throw new UnauthorizedAccessException("파일 승인 상태 오류다.");
                var incoming=message.Type=="file-request";
                var offer=incoming?message.Body?.Deserialize<FileMessage>():null;
                if(incoming&&(offer==null||offer.Id!=message.Id||offer.SessionId!=currentSession||offer.PermissionGeneration!=1||offer.Generation!=connectionGeneration))throw new InvalidDataException("파일 승인 바인딩 오류다.");
                var requestId=message.Id;var fileSession=currentSession;
                if(incoming&&initialSupportExpires>DateTimeOffset.UtcNow)
                {
                    string? folder=null;
                    try{folder=InitialSupportDownloadFolder();}
                    catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
                    {FileStatus.Text="자동 수신 폴더를 준비하지 못했다. 전송을 거부한다.";}
                    fileId=folder is null?null:requestId;FileCancel.IsEnabled=fileId!=null;
                    RemoteAssist.Host.Core.Diagnostics.CaseTrace.Write("file-initial-support",fileSession,1,new {id=requestId,approved=folder is not null});
                    TryPost("file-response",new {folder},requestId);
                    break;
                }
                RemoteAssist.Host.Core.Diagnostics.CaseTrace.Write("file-dialog-open",fileSession,1,new {id=requestId,incoming,name=offer?.Name,size=offer?.Size});
                var fileWindow=new LocalActionWindow(incoming?"수신 파일 저장 승인":"보낼 파일 선택",incoming?$"지원자가 보낸 {offer!.Name} ({offer.Size} bytes)의 저장 폴더를 승인한다.":"지원자에게 보낼 파일의 전체 경로를 선택한다.",incoming?Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments):""){Owner=this};fileDialog=fileWindow;
                Dispatcher.BeginInvoke(new Action(()=>
                {
                    if(fileDialog!=fileWindow||currentSession!=fileSession)return;
                    fileWindow.ShowDialog();if(fileDialog!=fileWindow||currentSession!=fileSession)return;fileDialog=null;
                    fileId=fileWindow.SelectedPath==null?null:requestId;FileCancel.IsEnabled=fileId!=null;
                    RemoteAssist.Host.Core.Diagnostics.CaseTrace.Write("file-consent-ui",fileSession,1,new {id=requestId,incoming,
                        approved=fileWindow.SelectedPath is not null,decision=fileWindow.Decision,
                        pathHash=fileWindow.SelectedPath is null?null:Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                            System.Text.Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(fileWindow.SelectedPath).ToUpperInvariant())))});
                    TryPost("file-response",new {folder=fileWindow.SelectedPath},requestId);
                }));break;
            case "file-state":
                var fileState=message.Body?.Deserialize<RemoteAssist.Host.Core.Files.FileTransferState>()??throw new InvalidDataException("파일 상태 누락이다.");
                FileStatus.Text=fileState.State+" · "+fileState.Size+" bytes";FileCancel.IsEnabled=false;fileId=null;break;
            case "chat":
                var chat=ReadFeature(message,128);if(chat.T!="chat")throw new InvalidDataException("채팅 방향 오류다.");AppendChat("지원자: "+chat.Text);break;
            case "clipboard-request":
                var clipboard=ReadFeature(message,16);FeatureMessage response;
                try
                {
                    if(clipboard.T=="clipboard-get")
                    {
                        var text=Clipboard.ContainsText(TextDataFormat.UnicodeText)?Clipboard.GetText(TextDataFormat.UnicodeText):"";
                        response=FeatureMessage.ClipboardTextReply(clipboard,text);
                    }
                    else if(clipboard.T=="clipboard-set")
                    {
                        if(clipboard.Text!.Length==0)Clipboard.Clear();else Clipboard.SetText(clipboard.Text,TextDataFormat.UnicodeText);
                        response=clipboard with {T="clipboard-applied",Text=null};
                    }
                    else throw new InvalidDataException("클립보드 요청 방향 오류다.");
                }
                catch(System.Runtime.InteropServices.ExternalException){response=clipboard with {T="clipboard-rejected",Text=null,Hash=null,Code="clipboard-busy"};}
                TryPost("clipboard-response",response,message.Id);break;
            default:throw new InvalidDataException("Tray 상태 메시지 오류");
        }
    }
    FeatureMessage ReadFeature(LocalIpcMessage message,int permission)
    {
        var value=message.Body?.Deserialize<FeatureMessage>()??throw new InvalidDataException("보조 기능 본문 누락이다.");_=FeatureMessage.Parse(ProtocolJson.Encode(value));
        if((capabilities&permission)!=permission||value.SessionId!=currentSession||value.Generation!=connectionGeneration||value.PermissionGeneration!=1||value.Id!=message.Id)throw new UnauthorizedAccessException("현재 로컬 보조 기능 승인이 아니다.");return value;
    }
    void ChatClicked(object sender,RoutedEventArgs e)
    {
        if((capabilities&128)==0||string.IsNullOrEmpty(ChatText.Text))return;
        var text=ChatText.Text;if(!FeatureMessage.ValidUnicode(text)||text.Contains('\0'))return;
        TryPost("chat",new {text},Guid.NewGuid().ToString("N"));AppendChat("나: "+text);ChatText.Clear();
    }
    void AppendChat(string text)
    {var lines=(ChatHistory.Text+text+Environment.NewLine);if(lines.Length>8192)lines=lines[^8192..];ChatHistory.Text=lines;ChatHistory.ScrollToEnd();}
    void StartClicked(object sender,RoutedEventArgs e){StartButton.IsEnabled=false;Status.Text="지원 코드를 준비 중이다.";TryPost("start");}
    void FileClicked(object sender,RoutedEventArgs e)
    {if((capabilities&8)==0)return;fileId=Guid.NewGuid().ToString("N");FileCancel.IsEnabled=true;TryPost("file-send",id:fileId);}
    void FileCancelClicked(object sender,RoutedEventArgs e){if(fileId!=null)TryPost("file-cancel",id:fileId);}
    void StopClicked(object sender,RoutedEventArgs e)=>StopLocal();
    void PostStop(){var mark=LocalStopMark.Create();RemoteAssist.Host.Core.Diagnostics.CaseTrace.Write("stop-ui",currentSession,1,
        new {requestId=mark.RequestId,requestedTick=mark.RequestedTick,frequency=mark.Frequency});TryPost("stop",new {requestId=mark.RequestId,requestedTick=mark.RequestedTick,frequency=mark.Frequency});}
    void StopLocal(){PostStop();EndLocal();Status.Text="종료 요청을 보냈다.";}
    void EndLocal()
    {
        initialSupportExpires=null;
        PermissionNotice.Text="신뢰하는 지원자에게 별도 연락으로 전달한다. 수동 지원은 연결할 때 권한을 선택한다.";
        pendingId=null;currentSession=null;
        capabilities=0;ChatText.IsEnabled=ChatSend.IsEnabled=false;ChatText.Clear();ChatHistory.Clear();
        FileSend.IsEnabled=FileCancel.IsEnabled=false;fileId=null;var localFile=fileDialog;fileDialog=null;localFile?.Close();FileStatus.Text="파일 전송 대기";
        var localAction=actionDialog;actionDialog=null;pendingRunbookId=null;localAction?.Close();
        adminPending=null;var adminDialog=adminConsent;adminConsent=null;adminDialog?.Close();
        var dialog=consent;consent=null;dialog?.Close();
        indicator?.Dispose();indicator=null;
        Code.Text="—";Pin.Text="PIN —";Topmost=false;SessionBorder.BorderBrush=Brushes.SlateGray;
        try{icon?.SetActive(false);}catch{ /* The stop was already sent, or the authenticated service lease is lost. */ }
    }
    static string InitialSupportDownloadFolder()
    {
        // Resolve the user's real Downloads folder (including Windows folder redirection).
        var downloadsId=new Guid("374DE290-123F-4565-9164-39C4925E467B");
        System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(SHGetKnownFolderPath(ref downloadsId,0,IntPtr.Zero,out var path));
        string downloads;
        try{downloads=System.Runtime.InteropServices.Marshal.PtrToStringUni(path)??throw new IOException("Downloads 경로 누락이다.");}
        finally{System.Runtime.InteropServices.Marshal.FreeCoTaskMem(path);}
        var folder=Path.GetFullPath(Path.Combine(downloads,"RemoteAssist"));
        DemandRegularDirectories(folder);Directory.CreateDirectory(folder);DemandRegularDirectories(folder);
        return folder;
    }
    static void DemandRegularDirectories(string path)
    {
        for(var directory=new DirectoryInfo(path);directory!=null;directory=directory.Parent)
            if(directory.Exists&&(directory.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new IOException("자동 수신 경로는 재분석 지점을 포함할 수 없다.");
    }
    [System.Runtime.InteropServices.DllImport("shell32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int SHGetKnownFolderPath(ref Guid folder,uint flags,IntPtr token,out IntPtr path);
    void TryPost(string type,object? body=null,string id="")
    {try{peer?.Post(type,body,id);}catch(InvalidOperationException){Disconnected("서비스 연결이 종료됐다.");}}
}
