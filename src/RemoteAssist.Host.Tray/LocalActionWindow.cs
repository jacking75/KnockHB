using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace RemoteAssist.Host.Tray;

internal sealed class LocalActionWindow:Window
{
    readonly DispatcherTimer expiry=new(){Interval=TimeSpan.FromSeconds(28)};
    public string? SelectedPath {get;private set;}
    public LocalActionWindow(string title,string summary,string initialPath)
    {
        Title=title;Width=560;Height=320;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var panel=new StackPanel {Margin=new Thickness(24)};Content=panel;
        panel.Children.Add(new TextBlock {Text=summary,TextWrapping=TextWrapping.Wrap});
        panel.Children.Add(new TextBlock {Text="로컬 고정 NTFS 경로를 입력한다. 기존 파일을 덮어쓰거나 실행하지 않는다. 28초 뒤 거부한다.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,8)});
        var path=new TextBox {Text=initialPath};panel.Children.Add(path);
        var approve=new Button {Content="이 작업 승인",Margin=new Thickness(0,16,0,0),Padding=new Thickness(8)};
        approve.Click+=(_,_)=>{if(string.IsNullOrWhiteSpace(path.Text))return;SelectedPath=path.Text;Close();};panel.Children.Add(approve);
        var deny=new Button {Content="거부",Margin=new Thickness(0,8,0,0),IsCancel=true};deny.Click+=(_,_)=>Close();panel.Children.Add(deny);
        Loaded+=(_,_)=>expiry.Start();expiry.Tick+=(_,_)=>Close();Closed+=(_,_)=>expiry.Stop();
    }
}
