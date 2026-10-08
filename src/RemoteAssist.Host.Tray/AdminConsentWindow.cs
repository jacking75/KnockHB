using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace RemoteAssist.Host.Tray;

internal sealed class AdminConsentWindow:Window
{
    readonly DispatcherTimer expiry=new(){Interval=TimeSpan.FromSeconds(30)};
    public bool Approved {get;private set;}
    public AdminConsentWindow(string supporter,string monitor)
    {
        Title="관리자 지원 2차 승인";Width=470;Height=310;ResizeMode=ResizeMode.NoResize;Topmost=true;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var panel=new StackPanel {Margin=new Thickness(24)};Content=panel;
        panel.Children.Add(new TextBlock {Text="관리자 요청: "+supporter,FontSize=20,TextWrapping=TextWrapping.Wrap});
        panel.Children.Add(new TextBlock {Text="대상: "+monitor+"\n승격 앱·UAC·잠금 화면의 보기와 입력을 최대 2분 허용한다. 보안 화면은 최대 30초이며 현장 종료 버튼 또는 Ctrl+Alt+Shift+F9으로 종료한다. 관리자 모드를 해제하면 지원 전체가 끝난다. 계정 비밀번호를 지원자에게 전달하지 않는다.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,12)});
        var check=new CheckBox {Content="현재 지원자에게 관리자 화면 조작을 허용한다."};panel.Children.Add(check);
        var row=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(0,16,0,0)};panel.Children.Add(row);
        var accept=new Button {Content="관리자 지원 승인",IsEnabled=false,Padding=new Thickness(12,6,12,6)};
        check.Checked+=(_,_)=>accept.IsEnabled=true;check.Unchecked+=(_,_)=>accept.IsEnabled=false;
        accept.Click+=(_,_)=>{Approved=check.IsChecked==true;Close();};row.Children.Add(accept);
        var deny=new Button {Content="거부",IsCancel=true,Padding=new Thickness(12,6,12,6),Margin=new Thickness(8,0,0,0)};deny.Click+=(_,_)=>Close();row.Children.Add(deny);
        System.Windows.Automation.AutomationProperties.SetAutomationId(check,"AdminConsentScope");System.Windows.Automation.AutomationProperties.SetAutomationId(accept,"AdminConsentApprove");
        expiry.Tick+=(_,_)=>Close();Loaded+=(_,_)=>expiry.Start();Closed+=(_,_)=>expiry.Stop();
    }
}
