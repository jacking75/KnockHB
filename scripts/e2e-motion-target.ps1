[CmdletBinding()]
param([ValidateSet('latency','motion')][string]$Mode='latency',[ValidateRange(10,900)][int]$Seconds=180)
# 실연결 성능 실측용 대상 창(B 사용자 세션에서 실행). 입력을 기록하지 않고 화면만 바꾼다.
#   latency: 아무 키나 누를 때마다 창 전체가 흑/백으로 바뀐다. Viewer에서 키 전송→밝기 변화 프레임까지를 잰다.
#   motion : 막대가 매 렌더 프레임 움직여 화면이 계속 바뀐다. 60초 송신·표시 FPS 측정용.
# 정해진 시간이 지나면 스스로 닫힌다.
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework
$window=New-Object Windows.Window -Property @{Title="RemoteAssist $Mode target";Width=480;Height=360;Left=40;Top=120;Topmost=$true;WindowStyle='ToolWindow';ResizeMode='NoResize'}
$canvas=New-Object Windows.Controls.Canvas -Property @{Background=[Windows.Media.Brushes]::White}
$window.Content=$canvas
if($Mode-eq'latency'){
    $window.Add_KeyDown({param($s,$e)if(!$e.IsRepeat){$canvas.Background=if($canvas.Background-eq[Windows.Media.Brushes]::White){[Windows.Media.Brushes]::Black}else{[Windows.Media.Brushes]::White}}})
}else{
    $bar=New-Object Windows.Shapes.Rectangle -Property @{Width=40;Height=360;Fill=[Windows.Media.Brushes]::Black}
    [void]$canvas.Children.Add($bar)
    $clock=[Diagnostics.Stopwatch]::StartNew()
    [Windows.Media.CompositionTarget]::add_Rendering({[Windows.Controls.Canvas]::SetLeft($bar,($clock.Elapsed.TotalMilliseconds/2)%440)})
}
$timer=New-Object Windows.Threading.DispatcherTimer -Property @{Interval=[TimeSpan]::FromSeconds($Seconds)}
$timer.Add_Tick({$window.Close()});$timer.Start()
[void]$window.ShowDialog()
