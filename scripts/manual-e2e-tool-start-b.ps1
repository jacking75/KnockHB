$ErrorActionPreference='Stop'
$task=Get-ScheduledTask -TaskName 'RemoteAssist-C01-DTD-20261022-2143-B'
$expected=Join-Path $env:USERPROFILE 'DesktopTestDriver-E2E-20261020'
foreach($action in $task.Actions){
 if($action.Execute -notlike ($expected+'\*') -and $action.Arguments -notlike ('*'+$expected+'*')){throw 'Unexpected tool task action'}
}
if($task.Principal.LogonType -ne 'Interactive'){throw 'Interactive tool task required'}
if(Get-Process Agent -ErrorAction SilentlyContinue){throw 'Agent already running; inspect first'}
Start-ScheduledTask -InputObject $task
@{task=$task.TaskName;started=$true;productStarted=$false}|ConvertTo-Json
