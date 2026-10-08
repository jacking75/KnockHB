using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace RemoteAssist.Host.Tray;

internal sealed class ConsentWindow : Window
{
    readonly CheckBox screen=new(){Content="화면 보기",Margin=new Thickness(0,8,0,0)};
    readonly CheckBox control=new(){Content="원격 제어",Margin=new Thickness(0,4,0,0),IsEnabled=false};
    readonly DispatcherTimer expiry=new(){Interval=TimeSpan.FromSeconds(30)};
    readonly CheckBox admin=new(){Content="관리자 요청 허용 — 실제 진입은 별도 승인",Margin=new Thickness(0,4,0,0),IsEnabled=false};
    readonly CheckBox chat=new(){Content="텍스트 채팅",Margin=new Thickness(0,4,0,0)};
    readonly CheckBox clipboard=new(){Content="텍스트 클립보드 읽기·쓰기 — 지원자가 요청할 때만",Margin=new Thickness(0,4,0,0)};
    readonly CheckBox diagnostics=new(){Content="시스템 진단 — CPU·메모리·서비스 등 읽기 전용",Margin=new Thickness(0,4,0,0)};
    public int SelectedCapabilities {get;private set;}
    public bool ApprovedScreen {get;private set;}
    public bool ApprovedControl {get;private set;}
    public bool ApprovedAdmin {get;private set;}
    public ConsentWindow(string supporter,string monitor)
    {
        Title="원격 지원 권한 승인";Width=480;Height=510;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;Topmost=true;
        var panel=new StackPanel {Margin=new Thickness(24)};Content=panel;
        panel.Children.Add(new TextBlock {Text="접속 요청: "+supporter,FontSize=20,TextWrapping=TextWrapping.Wrap});
        panel.Children.Add(new TextBlock {Text="위 이름은 지원자가 입력한 이름이다. 별도 연락으로 상대를 확인한다. 30초 동안 승인하지 않으면 거부한다. 제어 중에는 로컬 키보드·마우스의 동시 조작을 피하며 즉시 종료로 통제권을 회수한다.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,8)});
        panel.Children.Add(new TextBlock {Text="공유 대상: "+monitor,TextWrapping=TextWrapping.Wrap});
        System.Windows.Automation.AutomationProperties.SetAutomationId(screen,"ConsentScreen");panel.Children.Add(screen);
        System.Windows.Automation.AutomationProperties.SetAutomationId(control,"ConsentControl");panel.Children.Add(control);
        System.Windows.Automation.AutomationProperties.SetAutomationId(admin,"ConsentAdmin");panel.Children.Add(admin);
        control.Checked+=(_,_)=>admin.IsEnabled=true;control.Unchecked+=(_,_)=>{admin.IsChecked=false;admin.IsEnabled=false;};
        foreach(var item in new[]{chat,clipboard,diagnostics})panel.Children.Add(item);
        foreach(var label in new[]{"파일 전송","Runbook"})panel.Children.Add(new CheckBox {Content=label+" — 준비 중",IsEnabled=false,Margin=new Thickness(0,4,0,0)});
        var buttons=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(0,16,0,0)};panel.Children.Add(buttons);
        var grant=new Button {Content="선택 권한 승인",Padding=new Thickness(12,6,12,6),IsEnabled=false};
        System.Windows.Automation.AutomationProperties.SetAutomationId(grant,"ConsentApprove");
        void Refresh()=>grant.IsEnabled=new[]{screen,chat,clipboard,diagnostics}.Any(x=>x.IsChecked==true);
        screen.Checked+=(_,_)=>{Refresh();control.IsEnabled=true;};screen.Unchecked+=(_,_)=>{control.IsChecked=false;control.IsEnabled=false;Refresh();};
        foreach(var item in new[]{chat,clipboard,diagnostics}){item.Checked+=(_,_)=>Refresh();item.Unchecked+=(_,_)=>Refresh();}
        grant.Click+=(_,_)=>{ApprovedScreen=screen.IsChecked==true;ApprovedControl=ApprovedScreen&&control.IsChecked==true;ApprovedAdmin=ApprovedControl&&admin.IsChecked==true;SelectedCapabilities=(ApprovedScreen?1:0)|(ApprovedControl?2:0)|(ApprovedAdmin?4:0)|(clipboard.IsChecked==true?16:0)|(diagnostics.IsChecked==true?32:0)|(chat.IsChecked==true?128:0);Close();};buttons.Children.Add(grant);
        var deny=new Button {Content="거부",Padding=new Thickness(12,6,12,6),Margin=new Thickness(8,0,0,0),IsCancel=true};
        System.Windows.Automation.AutomationProperties.SetAutomationId(deny,"ConsentDeny");deny.Click+=(_,_)=>Close();buttons.Children.Add(deny);
        expiry.Tick+=(_,_)=>Close();Loaded+=(_,_)=>expiry.Start();Closed+=(_,_)=>expiry.Stop();
    }
}
