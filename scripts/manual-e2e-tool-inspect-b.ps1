$ErrorActionPreference='Stop'
$dtd=Join-Path $env:USERPROFILE 'DesktopTestDriver-E2E-20261020'
$settings=Get-Content "$dtd/Agent-e2e.json" -Raw | ConvertFrom-Json
$state=$settings.stateDirectory
@{agentSettings=@{stateDirectory=$state;machineId=$settings.machineId};tasks=@(Get-ScheduledTask | Where-Object {$_.TaskName -like '*DTD*'} | Select-Object TaskName,State);recovery=if(Test-Path "$state/watchdog-recovery.json"){Get-Content "$state/watchdog-recovery.json" -Raw | ConvertFrom-Json}else{$null};packages=@(Get-ChildItem "$env:USERPROFILE/RemoteAssist-E2E-196568206174101705998361604504128602726" | Select-Object Name);logs=@(Get-ChildItem $state -File | Select-Object Name,Length,LastWriteTime)} | ConvertTo-Json -Depth 6
