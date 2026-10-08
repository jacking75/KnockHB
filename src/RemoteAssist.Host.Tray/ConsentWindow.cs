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
    public bool ApprovedScreen {get;private set;}
    public bool ApprovedControl {get;private set;}
    public bool ApprovedAdmin {get;private set;}
    public ConsentWindow(string supporter,string monitor)
    {
        Title="원격 지원 권한 승인";Width=440;Height=420;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;Topmost=true;
        var panel=new StackPanel {Margin=new Thickness(24)};Content=panel;
        panel.Children.Add(new TextBlock {Text="접속 요청: "+supporter,FontSize=20,TextWrapping=TextWrapping.Wrap});
        panel.Children.Add(new TextBlock {Text="위 이름은 지원자가 입력한 이름이다. 별도 연락으로 상대를 확인한다. 30초 동안 승인하지 않으면 거부한다. 제어 중에는 로컬 키보드·마우스의 동시 조작을 피하며 즉시 종료로 통제권을 회수한다.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,8)});
        panel.Children.Add(new TextBlock {Text="공유 대상: "+monitor,TextWrapping=TextWrapping.Wrap});
        System.Windows.Automation.AutomationProperties.SetAutomationId(screen,"ConsentScreen");panel.Children.Add(screen);
        System.Windows.Automation.AutomationProperties.SetAutomationId(control,"ConsentControl");panel.Children.Add(control);
        System.Windows.Automation.AutomationProperties.SetAutomationId(admin,"ConsentAdmin");panel.Children.Add(admin);
        control.Checked+=(_,_)=>admin.IsEnabled=true;control.Unchecked+=(_,_)=>{admin.IsChecked=false;admin.IsEnabled=false;};
        foreach(var label in new[]{"파일 전송","텍스트 클립보드","진단·Runbook"})panel.Children.Add(new CheckBox {Content=label+" — 준비 중",IsEnabled=false,Margin=new Thickness(0,4,0,0)});
        var buttons=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(0,16,0,0)};panel.Children.Add(buttons);
        var grant=new Button {Content="선택 권한 승인",Padding=new Thickness(12,6,12,6),IsEnabled=false};
        System.Windows.Automation.AutomationProperties.SetAutomationId(grant,"ConsentApprove");
        screen.Checked+=(_,_)=>{grant.IsEnabled=true;control.IsEnabled=true;};screen.Unchecked+=(_,_)=>{grant.IsEnabled=false;control.IsChecked=false;control.IsEnabled=false;};
        grant.Click+=(_,_)=>{ApprovedScreen=screen.IsChecked==true;ApprovedControl=ApprovedScreen&&control.IsChecked==true;ApprovedAdmin=ApprovedControl&&admin.IsChecked==true;Close();};buttons.Children.Add(grant);
        var deny=new Button {Content="거부",Padding=new Thickness(12,6,12,6),Margin=new Thickness(8,0,0,0),IsCancel=true};
        System.Windows.Automation.AutomationProperties.SetAutomationId(deny,"ConsentDeny");deny.Click+=(_,_)=>Close();buttons.Children.Add(deny);
        expiry.Tick+=(_,_)=>Close();Loaded+=(_,_)=>expiry.Start();Closed+=(_,_)=>expiry.Stop();
    }
}
