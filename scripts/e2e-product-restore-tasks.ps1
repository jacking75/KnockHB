$ErrorActionPreference='Stop'
$owner=Get-Content (Join-Path $Staging 'product-owner.json') -Raw -Encoding UTF8|ConvertFrom-Json
if($owner.id-ne'4ceb58484cec4936865317c7901b7570'-or$owner.root-ne(Join-Path $env:ProgramData ('RemoteAssist-E2E-Product-'+$owner.id))){throw 'Frozen candidate scope changed'}
$root=$owner.root
if(Test-Path (Join-Path $root 'product-start.json')){throw 'Started campaigns cannot be reset'}
$plan=Get-Content (Join-Path $root 'product-plan.json') -Raw -Encoding UTF8|ConvertFrom-Json
if($plan.review-ne'PENDING'){throw 'Already armed'}
$shell=Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
$action=New-ScheduledTaskAction -Execute $shell -Argument ('-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+(Join-Path $root 'scripts/e2e-product-guard.ps1')+'" -RunDir "'+$root+'"')
$principal=New-ScheduledTaskPrincipal -UserId SYSTEM -LogonType ServiceAccount -RunLevel Highest
$settings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Seconds 540) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName $owner.task -Action $action -Principal $principal -Settings $settings|Out-Null
$observeAction=New-ScheduledTaskAction -Execute $shell -Argument ('-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+(Join-Path $root 'scripts/e2e-console-observer.ps1')+'" -RunDir "'+$root+'" -OutputDir "'+$owner.output+'"')
$user=New-ScheduledTaskPrincipal -UserId ($env:COMPUTERNAME+'\jacking75NoteBook') -LogonType Interactive -RunLevel Limited
$observeSettings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Seconds 300) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName $owner.observer -Action $observeAction -Principal $user -Settings $observeSettings|Out-Null
Write-Output 'UNSTARTED_B_TASKS_RESTORED; NO CLOCK RESET'
