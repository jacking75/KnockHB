[CmdletBinding()]
param([ValidateSet('Controller','Watchdog')][string]$Role,[Parameter(Mandatory=$true)][string]$RunDir,[switch]$Fixture)
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
Add-Type -Path (Join-Path $PSScriptRoot 'e2e-native.cs')
$RunDir=[IO.Path]::GetFullPath($RunDir)
$plan=Get-Content -LiteralPath (Join-Path $RunDir 'plan.json') -Raw -Encoding UTF8|ConvertFrom-Json
if($plan.version-ne2-or$plan.id-notmatch'^[a-f0-9]{32}$'-or$plan.totalSeconds-gt900-or$plan.totalSeconds-lt1){throw 'Invalid fixed campaign'}
if(!$Fixture){throw 'Product Arm remains blocked until independent watchdog/cleanup rehearsal and readiness review are accepted.'}
if($plan.profile-ne'input-free-fixture'){throw 'Only an input-free fixture is permitted'}
$utf8=New-Object Text.UTF8Encoding($false)
$shell=Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
function Save([string]$Name,$Value,[switch]$New){
    $path=Join-Path $RunDir $Name;$bytes=$utf8.GetBytes(($Value|ConvertTo-Json -Depth 6))
    if($New){$stream=New-Object IO.FileStream($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read);try{$stream.Write($bytes,0,$bytes.Length);$stream.Flush($true)}finally{$stream.Dispose()};return}
    $temporary=$path+'.'+$PID+'.tmp';[IO.File]::WriteAllBytes($temporary,$bytes)
    if(Test-Path $path){[IO.File]::Replace($temporary,$path,[NullString]::Value)}else{[IO.File]::Move($temporary,$path)}
}
function Read([string]$Name){
    $stream=New-Object IO.FileStream((Join-Path $RunDir $Name),[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite-bor[IO.FileShare]::Delete))
    $reader=New-Object IO.StreamReader($stream,[Text.Encoding]::UTF8)
    try{$reader.ReadToEnd()|ConvertFrom-Json}finally{$reader.Dispose()}
}
function Identity { $p=Get-Process -Id $PID; @{pid=$PID;start=$p.StartTime.ToUniversalTime().Ticks;tick=[E2eOwnedCommand]::Tick;campaign=$plan.id} }
function Match-Process($Identity){
    if(!$Identity-or$Identity.campaign-ne$plan.id){return $null}
    $p=Get-Process -Id $Identity.pid -ErrorAction SilentlyContinue
    if($p){
        try{
            if($p.StartTime.ToUniversalTime().Ticks-eq$Identity.start-and$p.Path-eq$shell){return $p}
        }catch{
            # Job termination can race property reads. Only confirmed exit is absence.
            if(!$p.HasExited){throw}
        }
    }
    return $null
}
function Stop-Reason([string]$Reason){
    try{Save 'stop.json' @{reason=$Reason;tick=[E2eOwnedCommand]::Tick;campaign=$plan.id} -New}catch [IO.IOException] {}
}
function Cleanup {
    # OS-released lock permits the surviving monitor to take over after owner death.
    $lock=$null;$until=[Math]::Min($script:deadline,[E2eOwnedCommand]::Deadline(2000))
    while(!$lock-and[E2eOwnedCommand]::Remaining($until)-gt0){
        try{$lock=New-Object IO.FileStream((Join-Path $RunDir 'cleanup.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)}catch [IO.IOException]{Start-Sleep -Milliseconds 20}
    }
    if(!$lock){throw 'Cleanup ownership unavailable'}
    try{
        if(Test-Path (Join-Path $RunDir 'cleanup.json')){return}
        # Fixture only. Product SCM/Recovery cleanup remains a separate acceptance gate.
        $childFile=Join-Path $RunDir 'child.json'
        if(Test-Path $childFile){
            $child=Read 'child.json';$p=Match-Process $child
            $wait=[Math]::Min($script:deadline,[E2eOwnedCommand]::Deadline(2000))
            while($p-and!$p.HasExited-and[E2eOwnedCommand]::Remaining($wait)-gt0){Start-Sleep -Milliseconds 20;$p.Refresh()}
            if($p-and!$p.HasExited){throw 'Owned fixture child survived Job cleanup'}
        }
        Save 'cleanup.json' @{state='FIXTURE_CLEAN';campaign=$plan.id;tick=[E2eOwnedCommand]::Tick;frequency=[Diagnostics.Stopwatch]::Frequency;addresses=@(Get-NetIPAddress -AddressFamily IPv4|ForEach-Object {$_.IPAddress});by=$Role} -New
    }finally{$lock.Dispose()}
}
if($Role-eq'Controller'){
    Save 'armed-once.json' @{campaign=$plan.id;tick=[E2eOwnedCommand]::Tick} -New
    $script:deadline=[E2eOwnedCommand]::Deadline([int]($plan.totalSeconds*1000))
    Save 'start.json' @{campaign=$plan.id;deadline=$deadline;identity=(Identity)} -New
    Save 'heartbeat.json' (Identity)
    $watcher=$null;$command=$null
    try{
        $arguments='-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+$PSCommandPath+'" -Role Watchdog -RunDir "'+$RunDir+'" -Fixture'
        $watcher=Start-Process -FilePath $shell -ArgumentList $arguments -WindowStyle Hidden -PassThru
        $ready=[Math]::Min($deadline,[E2eOwnedCommand]::Deadline(5000))
        while(!(Test-Path (Join-Path $RunDir 'watchdog.json'))){
            if($watcher.HasExited-or[E2eOwnedCommand]::Remaining($ready)-eq0){throw 'Independent watchdog readiness missing'}
            Save 'heartbeat.json' (Identity);Start-Sleep -Milliseconds 50
        }
        $wd=Read 'watchdog.json';if(!(Match-Process $wd)){throw 'Watchdog identity mismatch'}
        $command=[E2eOwnedCommand]::Start($shell,@('-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 60'))
        $p=Get-Process -Id $command.Pid
        Save 'child.json' @{pid=$p.Id;start=$p.StartTime.ToUniversalTime().Ticks;campaign=$plan.id} -New
        $activeEnd=[Math]::Min($deadline,[E2eOwnedCommand]::Deadline([int]($plan.executionSeconds*1000)))
        while([E2eOwnedCommand]::Remaining($activeEnd)-gt0-and!(Test-Path (Join-Path $RunDir 'stop.json'))){
            if(!(Match-Process $wd)){Stop-Reason 'watchdog-lost';break}
            if($plan.fault-ne'heartbeat'){Save 'heartbeat.json' (Identity)}
            Start-Sleep -Milliseconds 50
        }
        Stop-Reason 'execution-deadline'
    }catch{Save 'controller-error.json' @{error=$_.Exception.Message;line=$_.InvocationInfo.ScriptLineNumber};Stop-Reason ('controller-failure: '+$_.Exception.GetType().Name)}finally{
        if($command){$command.Dispose()}
        try{Cleanup}catch{Save 'recovery-required.json' @{reason=$_.Exception.Message;line=$_.InvocationInfo.ScriptLineNumber;campaign=$plan.id}}
        Save 'controller-ended.json' @{tick=[E2eOwnedCommand]::Tick;campaign=$plan.id}
        if($watcher){$null=$watcher.WaitForExit([Math]::Min(2000,[E2eOwnedCommand]::Remaining($deadline)));if(!$watcher.HasExited){$watcher.Kill()}}
    }
    exit 0
}
$start=Read 'start.json';if($start.campaign-ne$plan.id){throw 'Campaign mismatch'}
$script:deadline=[long]$start.deadline
Save 'watchdog.json' (Identity) -New
try{
    while([E2eOwnedCommand]::Remaining($deadline)-gt0){
        if(Test-Path (Join-Path $RunDir 'cleanup.json')){break}
        $controller=Match-Process $start.identity
        if(!$controller){Stop-Reason 'controller-lost';Cleanup;break}
        $heartbeat=Read 'heartbeat.json'
        if($heartbeat.campaign-ne$plan.id-or([E2eOwnedCommand]::Tick-$heartbeat.tick)-gt[Diagnostics.Stopwatch]::Frequency*3){
            Stop-Reason 'controller-lease-expired'
            # Exact retained Process identity, never a name-wide kill. This closes its owned Job.
            $controller.Kill();$null=$controller.WaitForExit(1000);Cleanup;break
        }
        Start-Sleep -Milliseconds 50
    }
}catch{Save 'watchdog-error.json' @{reason=$_.Exception.GetType().Name;campaign=$plan.id}}finally{
    if(!(Test-Path (Join-Path $RunDir 'cleanup.json'))){Stop-Reason 'watchdog-deadline';$controller=Match-Process $start.identity;if($controller){$controller.Kill();$null=$controller.WaitForExit(1000)};try{Cleanup}catch{Save 'recovery-required.json' @{reason=$_.Exception.Message;line=$_.InvocationInfo.ScriptLineNumber;campaign=$plan.id}}}
}
