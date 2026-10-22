$ErrorActionPreference='Stop'
$root=Join-Path $env:USERPROFILE 'RemoteAssist-C01-Readiness-20261022-2158'
$zip=Join-Path $env:USERPROFILE 'RemoteAssist-C01-Readiness-20261022-2158.zip'
if((Test-Path -LiteralPath $root)-or(Get-FileHash -LiteralPath $zip).Hash-ne'bba2b4feaeabd331d01048a4050e1a5be896f57a384cbed32ae424a1040f9118'){throw 'Readiness archive or owner mismatch'}
Expand-Archive -LiteralPath $zip -DestinationPath $root
New-Item -ItemType Directory -Path (Join-Path $root '.artifacts')|Out-Null
$task='RemoteAssist-C01-Readiness-20261022-2158'
if(Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue){throw 'Readiness task replay'}
$shell=Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
$out=Join-Path $root '.artifacts/scm'
$arguments='-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+(Join-Path $root 'tests/e2e/scm-fixture.ps1')+'" -RunId 4576f4c262fa4adca72c7287957d4f80 -Output "'+$out+'" -ScmFixtureDirectory "'+(Join-Path $root 'scm-fixture')+'"'
$action=New-ScheduledTaskAction -Execute $shell -Argument $arguments
$principal=New-ScheduledTaskPrincipal -UserId SYSTEM -LogonType ServiceAccount -RunLevel Highest
$settings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::FromSeconds(90)) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Settings $settings -Description 'C01 independent non-product SCM fixture'|Out-Null
Start-ScheduledTask -TaskName $task
$watch=[Diagnostics.Stopwatch]::StartNew()
while(!(Test-Path (Join-Path $out 'results.json'))-and$watch.Elapsed.TotalSeconds-lt70){
 if(Test-Path (Join-Path $out 'controller/service-recovery-required.json')){throw 'SCM recovery required'}
 Start-Sleep -Milliseconds 100
}
if(!(Test-Path (Join-Path $out 'results.json'))){throw 'SCM readiness timeout; inspect owned service'}
$clean=Get-Content (Join-Path $out 'controller/service-cleanup.json') -Raw|ConvertFrom-Json
if($clean.state-ne'PASS'-or!$clean.serviceAbsent-or!$clean.processAbsent){throw 'SCM cleanup unverified'}
if(Get-Service 'RAE2E-4576f4c262fa4adca72c7287957d4f80' -ErrorAction SilentlyContinue){throw 'SCM fixture remains'}
Unregister-ScheduledTask -TaskName $task -Confirm:$false
$cert=Join-Path $root 'lan-test.cer'
$tls=& (Join-Path $root 'scripts/e2e-tls-identity.ps1') -Certificate $cert -Sha256 (Get-FileHash $cert).Hash -PinnedTool (Join-Path $root 'desktop-tool/E2eDesktop.exe')
@{state='READINESS_CHECKED_NOT_PRODUCT_PASS';scm=$clean;results=(Get-Content (Join-Path $out 'results.json') -Raw|ConvertFrom-Json);tls=($tls|ConvertFrom-Json);taskAbsent=$true;root=$root}|ConvertTo-Json -Depth 8 -Compress
