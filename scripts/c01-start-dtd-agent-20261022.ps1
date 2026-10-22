param([switch]$Remote)
$ErrorActionPreference='Stop'
if($Remote){
 $root=Join-Path $env:USERPROFILE 'DesktopTestDriver-E2E-20261020'
 $exe=Join-Path $root 'Agent/Agent.exe';$config=Join-Path $root 'Agent-e2e.json';$side='B'
}else{
 $exe='F:/github/DesktopTestDriver/bin/live_Release/Agent/Agent.exe'
 $config='F:/github/KnockHB/.artifacts/dtd-e2e-20261020/Agent.json';$side='A'
}
if(!(Test-Path -LiteralPath $exe)-or!(Test-Path -LiteralPath $config)){throw 'DTD files missing'}
$settings=Get-Content -LiteralPath $config -Raw -Encoding UTF8|ConvertFrom-Json
if($settings.machineId-ne('pc-'+$side.ToLowerInvariant())-or$settings.controllerHost-ne'192.168.0.61'-or$settings.controllerPort-ne25190){throw 'DTD endpoint mismatch'}
if(Get-Process Agent -ErrorAction SilentlyContinue){throw 'Existing Agent requires inspection'}
$name='RemoteAssist-C01-DTD-20261022-2143-'+$side
if(Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue){throw 'Owned task already exists; do not replay'}
$action=New-ScheduledTaskAction -Execute $exe -Argument ('--config "'+$config+'" --serve') -WorkingDirectory (Split-Path $exe)
$principal=New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Limited
$taskSettings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::FromHours(2)) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName $name -Action $action -Principal $principal -Settings $taskSettings -Description 'C01 owned DTD Agent, no product or input started'|Out-Null
Start-ScheduledTask -TaskName $name
@{state='AGENT_START_REQUESTED';task=$name;exe=$exe;configSha256=(Get-FileHash -LiteralPath $config).Hash;sid=$principal.UserId}|ConvertTo-Json -Compress
