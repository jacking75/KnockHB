[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RunDir,[ValidateSet('Inspect','Start','ApproveScreen','Deny','Stop','Close','CloseConsent','ReadPairing','CrashTray')][string]$Action='Inspect')
$ErrorActionPreference='Stop'
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$RunDir=[IO.Path]::GetFullPath($RunDir)
if(!$RunDir.StartsWith((Join-Path $repository '.artifacts')+'\',[StringComparison]::OrdinalIgnoreCase)){throw '로컬 실증 artifact 경로가 필요하다'}
$installation=Get-Content -LiteralPath (Join-Path $RunDir 'install.json') -Encoding utf8 -Raw | ConvertFrom-Json
$expected=Join-Path $installation.protectedDirectory 'RemoteAssist.Host.Tray.exe'
$process=@(Get-Process -Name RemoteAssist.Host.Tray -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq $expected})
if($process.Count -ne 1){throw '이 실증의 Tray 한 개가 필요하다'}
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$root=[Windows.Automation.AutomationElement]::RootElement
$condition=New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty,$process[0].Id)
$windows=$root.FindAll([Windows.Automation.TreeScope]::Children,$condition)
function Find-Control([string]$id) {
    $match=New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty,$id)
    foreach($window in $windows){$found=$window.FindFirst([Windows.Automation.TreeScope]::Descendants,$match);if($found){return $found}}
    throw "실증 UI 요소 누락: $id"
}
function Invoke-Control([string]$id){$button=Find-Control $id;if(!$button.Current.IsEnabled){throw "실증 버튼 비활성: $id"};$button.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()}
switch($Action) {
    'Start' {Invoke-Control 'HostStart'}
    'Stop' {
        $helperPath=Join-Path $installation.protectedDirectory 'RemoteAssist.Host.Helper.exe'
        $children=@(Get-Process -Name RemoteAssist.Host.Helper -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq $helperPath})
        $clock=[Diagnostics.Stopwatch]::StartNew();Invoke-Control 'HostStop'
        while($clock.Elapsed.TotalSeconds -lt 2 -and @($children | Where-Object {Get-Process -Id $_.Id -ErrorAction SilentlyContinue}).Count -gt 0){Start-Sleep -Milliseconds 10}
        @{utc=[DateTimeOffset]::UtcNow.ToString('o');ownedChildren=$children.Count;elapsedMs=$clock.Elapsed.TotalMilliseconds;
            helperAbsent=(@($children | Where-Object {Get-Process -Id $_.Id -ErrorAction SilentlyContinue}).Count -eq 0)} | ConvertTo-Json
    }
    'ApproveScreen' {
        (Find-Control 'ConsentScreen').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle()
        Invoke-Control 'ConsentApprove'
    }
    'Deny' {Invoke-Control 'ConsentDeny'}
    'Close' {$windows[0].GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()}
    'CloseConsent' {
        $dialog=Find-Control 'ConsentDeny'
        for($i=0;$i -lt 20 -and $dialog -and $dialog.Current.Name -ne '원격 지원 권한 승인';$i++){$dialog=[Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($dialog)}
        if(!$dialog -or $dialog.Current.Name -ne '원격 지원 권한 승인'){throw '이 실증의 동의 창이 필요하다'}
        $dialog.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
    }
    'CrashTray' {
        $helperPath=Join-Path $installation.protectedDirectory 'RemoteAssist.Host.Helper.exe'
        $children=@(Get-Process -Name RemoteAssist.Host.Helper -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq $helperPath})
        $clock=[Diagnostics.Stopwatch]::StartNew();Stop-Process -Id $process[0].Id -Force
        while($clock.Elapsed.TotalSeconds -lt 2 -and @($children | Where-Object {Get-Process -Id $_.Id -ErrorAction SilentlyContinue}).Count -gt 0){Start-Sleep -Milliseconds 10}
        @{utc=[DateTimeOffset]::UtcNow.ToString('o');ownedChildren=$children.Count;elapsedMs=$clock.Elapsed.TotalMilliseconds;
            helperAbsent=(@($children | Where-Object {Get-Process -Id $_.Id -ErrorAction SilentlyContinue}).Count -eq 0)} | ConvertTo-Json
    }
    'ReadPairing' {
        $code=(Find-Control 'PairingCode').Current.Name;$pin=(Find-Control 'PairingPin').Current.Name.Replace('PIN ','')
        if($code -eq '—' -or $pin -eq '—'){throw '발급 코드가 없다'}
        # Private ignored artifact only. Never print pairing credentials to console/report.
        @{code=$code;pin=$pin} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $RunDir 'pairing.json') -Encoding utf8
    }
    'Inspect' {
        $stop=Find-Control 'HostStop';$bounds=$stop.Current.BoundingRectangle
        $window=[Windows.Automation.AutomationElement]::FromHandle($process[0].MainWindowHandle);$windowBounds=$window.Current.BoundingRectangle
        @{utc=[DateTimeOffset]::UtcNow.ToString('o');pid=$process[0].Id;session=$process[0].SessionId;windowCount=$windows.Count;
            stopOffscreen=$stop.Current.IsOffscreen;stopInsideWindow=($bounds.Bottom -le $windowBounds.Bottom -and $bounds.Top -ge $windowBounds.Top);
            status=(Find-Control 'HostStatus').Current.Name;startEnabled=(Find-Control 'HostStart').Current.IsEnabled;
            codePresent=((Find-Control 'PairingCode').Current.Name -ne '—');pinPresent=((Find-Control 'PairingPin').Current.Name -ne 'PIN —')} | ConvertTo-Json
    }
}
