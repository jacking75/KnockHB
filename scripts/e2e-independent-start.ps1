$ErrorActionPreference='Stop'
if($Staging-ne'C:/Users/jacking75NoteBook/RemoteAssist-E2E-1792313321056489200'){throw 'Reviewed tools staging required'}
$id=[guid]::NewGuid().ToString('N')
$root=Join-Path $env:ProgramData ('RemoteAssist-E2E-Guard-'+$id)
$task='RemoteAssist-E2E-Guard-'+$id
New-Item -ItemType Directory -Path $root -ErrorAction Stop|Out-Null
$acl=New-Object Security.AccessControl.DirectorySecurity
$acl.SetAccessRuleProtection($true,$false)
$acl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))
foreach($sid in @('S-1-5-18','S-1-5-32-544')){$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($sid)),'FullControl','ContainerInherit,ObjectInherit','None','Allow')))}
Set-Acl -LiteralPath $root -AclObject $acl
foreach($name in @('scripts','tests')){Copy-Item -LiteralPath (Join-Path $Staging ('guards/'+$name)) -Destination $root -Recurse}
$utf8=New-Object Text.UTF8Encoding($false)
$runner=@'
$ErrorActionPreference='Stop'
try{
    & (Join-Path $PSScriptRoot 'tests/e2e/campaign-fixture.ps1') *> (Join-Path $PSScriptRoot 'output.txt')
    'PASS'|Set-Content (Join-Path $PSScriptRoot 'finished.txt')
}catch{$_|Out-String|Set-Content (Join-Path $PSScriptRoot 'failure.txt');exit 1}
'@
[IO.File]::WriteAllText((Join-Path $root 'runner.ps1'),$runner,$utf8)
$shell=Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
$action=New-ScheduledTaskAction -Execute $shell -Argument ('-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+(Join-Path $root 'runner.ps1')+'"')
$principal=New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$settings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Seconds 90) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
$owner=@{id=$id;root=$root;task=$task;kind='INPUT_FREE_INDEPENDENCE_FIXTURE';createdUtc=[DateTime]::UtcNow.ToString('o')}
$owner|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $Staging 'independent-owner.json') -Encoding UTF8
$owner|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $root 'owner.json') -Encoding UTF8
Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Settings $settings -ErrorAction Stop|Out-Null
Start-ScheduledTask -TaskName $task
@{state='INPUT_FREE_TASK_STARTED';task=$task;root=$root;productStarted=$false;next='Close SSH; observe from a new connection'}|ConvertTo-Json
