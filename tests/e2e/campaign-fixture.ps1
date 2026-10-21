param([string]$RunId,[string]$Output)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$shell=Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
. (Join-Path $repo 'scripts/e2e-run-contract.ps1')
$output=if($Output){[IO.Path]::GetFullPath($Output)}else{Join-Path $repo ('.artifacts/campaign-fixture-'+[guid]::NewGuid().ToString('N'))}
if(!$output.StartsWith((Join-Path $repo '.artifacts')+'\',[StringComparison]::OrdinalIgnoreCase)-or(Test-Path -LiteralPath $output)){throw 'New workspace fixture output required'}
if($RunId-and$RunId-notmatch'^[a-f0-9]{32}$'){throw 'Fixture run ID required'}
$null=Assert-E2ePath (Split-Path $output -Parent)
New-Item -ItemType Directory -Path $output|Out-Null
$results=@()
foreach($fault in $(if($RunId){@('controller')}else{@('none','controller','watchdog','heartbeat')})){
    $run=Join-Path $output $fault;New-Item -ItemType Directory -Path $run|Out-Null
    @{version=2;id=$(if($RunId){$RunId}else{[guid]::NewGuid().ToString('N')});profile='input-free-fixture';totalSeconds=12;executionSeconds=$(if($fault-eq'none'){1}else{8});fault=$fault}|ConvertTo-Json|Set-Content (Join-Path $run 'plan.json') -Encoding UTF8
    $args='-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+(Join-Path $repo 'scripts/e2e-campaign.ps1')+'" -Role Controller -RunDir "'+$run+'" -Fixture'
    $timer=[Diagnostics.Stopwatch]::StartNew()
    $controller=Start-Process $shell -ArgumentList $args -WindowStyle Hidden -PassThru
    try{
        while(!(Test-Path (Join-Path $run 'child.json'))-and$timer.Elapsed.TotalSeconds-lt6-and!$controller.HasExited){Start-Sleep -Milliseconds 20}
        if(!(Test-Path (Join-Path $run 'child.json'))){throw ('Fixture not ready: '+$fault)}
        if($fault-eq'controller'){$controller.Kill()}
        if($fault-eq'watchdog'){
            $identity=Get-Content (Join-Path $run 'watchdog.json') -Raw|ConvertFrom-Json
            $watchdog=Get-Process -Id $identity.pid
            if($watchdog.StartTime.ToUniversalTime().Ticks-ne$identity.start){throw 'Fixture PID reused'}
            $watchdog.Kill()
        }
        while(!(Test-Path (Join-Path $run 'cleanup.json'))-and$timer.Elapsed.TotalSeconds-lt12){Start-Sleep -Milliseconds 20}
        if(!(Test-Path (Join-Path $run 'cleanup.json'))){throw ('Cleanup missing: '+$fault)}
        $clean=Get-Content (Join-Path $run 'cleanup.json') -Raw|ConvertFrom-Json
        if($clean.state-ne'FIXTURE_CLEAN'){throw 'Cleanup failed'}
        $null=$controller.WaitForExit(3000)
        if(!$controller.HasExited){throw 'Controller did not finish'}
        # Replay must fail before any replacement clock/process can start.
        $replay=Start-Process $shell -ArgumentList $args -WindowStyle Hidden -PassThru
        if(!$replay.WaitForExit(3000)-or$replay.ExitCode-eq0){if(!$replay.HasExited){$replay.Kill()};throw 'Campaign replay was not rejected'}
        $results+=@{fault=$fault;status='PASS';seconds=$timer.Elapsed.TotalSeconds}
    }finally{if(!$controller.HasExited){$controller.Kill()}}
}
$results|ConvertTo-Json|Set-Content (Join-Path $output 'results.json') -Encoding UTF8
$results|Format-Table -AutoSize
Write-Output ('Evidence: '+$output)
