$ErrorActionPreference='Stop'
if($Staging-ne'C:/Users/jacking75NoteBook/RemoteAssist-E2E-1792314412549972400'){throw 'Reviewed tools staging required'}
$owner=Get-Content -LiteralPath (Join-Path $Staging 'independent-owner.json') -Raw -Encoding UTF8|ConvertFrom-Json
if($owner.id-notmatch'^[a-f0-9]{32}$'-or$owner.kind-ne'INPUT_FREE_INDEPENDENCE_FIXTURE'){throw 'Owner mismatch'}
$expected=Join-Path $env:ProgramData ('RemoteAssist-E2E-Guard-'+$owner.id)
if($owner.root-ne$expected-or$owner.task-ne('RemoteAssist-E2E-Guard-'+$owner.id)){throw 'Scope mismatch'}
$task=Get-ScheduledTask -TaskName $owner.task -ErrorAction SilentlyContinue
$info=if($task){Get-ScheduledTaskInfo -TaskName $owner.task}else{$null}
if($task.State-eq'Running'){ @{state='RUNNING';task=$owner.task}|ConvertTo-Json;exit 0 }
$root=$owner.root
$proof=Get-Content -LiteralPath (Join-Path $root 'owner.json') -Raw -Encoding UTF8|ConvertFrom-Json
if($proof.id-ne$owner.id){throw 'Protected owner mismatch'}
$results=@(Get-ChildItem -LiteralPath (Join-Path $root '.artifacts') -Filter results.json -Recurse -File|ForEach-Object {foreach($item in (Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8|ConvertFrom-Json)){$item}})
$errors=if(Test-Path (Join-Path $root 'failure.txt')){[IO.File]::ReadAllText((Join-Path $root 'failure.txt'))}else{''}
$processes=@(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -OperationTimeoutSec 5|Where-Object {$_.CommandLine-like('*'+$root+'*')})
if($processes.Count){throw 'Owned fixture processes remain; preserve evidence'}
if($task){
    if($task.Actions.Execute-ne(Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe')-or$task.Actions.Arguments-notlike('*'+$root+'*')){throw 'Scheduled action changed'}
    Unregister-ScheduledTask -TaskName $owner.task -Confirm:$false
}
# Preserve the small protected evidence directory; no recursive deletion required.
$finished=(Test-Path (Join-Path $root 'finished.txt'))-and((Get-Content (Join-Path $root 'finished.txt') -Raw).Trim()-eq'PASS')
@{state=$(if($finished-and$results.Count-eq4-and!@($results|Where-Object {$_.status-ne'PASS'}).Count-and!$errors){'PASS'}else{'FAIL'});taskRemoved=$true;exitCode=$(if($info){$info.LastTaskResult}else{$null});results=$results;errors=$errors;root=$root;productStarted=$false}|ConvertTo-Json -Depth 6
