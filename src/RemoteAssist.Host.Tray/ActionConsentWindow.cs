using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace RemoteAssist.Host.Tray;

internal sealed class ActionConsentWindow:Window
{
    public bool Approved {get;private set;}
    public ActionConsentWindow(string title,string summary)
    {
        Title=title;Width=650;Height=490;WindowStartupLocation=WindowStartupLocation.CenterOwner;ResizeMode=ResizeMode.NoResize;
        var panel=new StackPanel {Margin=new Thickness(24)};Content=panel;
        var description=new TextBox {Text=summary,TextWrapping=TextWrapping.Wrap,Height=310,IsReadOnly=true,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        System.Windows.Automation.AutomationProperties.SetAutomationId(description,"LocalActionSummary");
        panel.Children.Add(description);
        var approve=new Button {Content="표시된 작업만 승인",Margin=new Thickness(0,16,0,0),Padding=new Thickness(10)};approve.Click+=(_,_)=>{Approved=true;Close();};panel.Children.Add(approve);
        System.Windows.Automation.AutomationProperties.SetAutomationId(approve,"LocalActionApprove");
        var deny=new Button {Content="거부",IsCancel=true,Margin=new Thickness(0,8,0,0)};deny.Click+=(_,_)=>Close();panel.Children.Add(deny);
        var timer=new DispatcherTimer {Interval=TimeSpan.FromSeconds(28)};timer.Tick+=(_,_)=>Close();Loaded+=(_,_)=>timer.Start();Closed+=(_,_)=>timer.Stop();
    }
}
