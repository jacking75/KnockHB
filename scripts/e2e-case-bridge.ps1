$ErrorActionPreference='Stop'
$owner=Get-E2eOwner
if($CampaignManifest.version-ne4-or$CaseRequestName-notmatch'^case-request-[a-f0-9]{32}\.json$'){throw 'Case bridge request required'}
$requestPath=Join-Path $Staging $CaseRequestName
$r=Read-E2eJson $requestPath
if($r.runId-ne$owner.id-or$r.caseId-ne$CampaignManifest.caseId){throw 'Bridge request owner'}
function Child([string]$Name){
 if($Name-notmatch'^[A-Za-z0-9_.-]+$'-or$Name.Contains('..')){throw 'Bridge artifact name'}
 return Join-Path $owner.output $Name
}
switch($r.operation){
 'clock' {@{status='completed';id=$owner.id;root=$owner.root;output=$owner.output;tick=[Diagnostics.Stopwatch]::GetTimestamp();frequency=[Diagnostics.Stopwatch]::Frequency}|ConvertTo-Json -Compress}
 'read' {
  $path=Child $r.name
  if(!(Test-Path -LiteralPath $path)){@{status='completed';available=$false}|ConvertTo-Json -Compress;break}
  $null=Assert-E2ePath $path
  if((Get-Item -LiteralPath $path).Length-gt1048576){throw 'Bridge evidence bound'}
  @{status='completed';available=$true;body=[Convert]::ToBase64String([IO.File]::ReadAllBytes($path));sha256=(Get-FileHash -LiteralPath $path).Hash}|ConvertTo-Json -Compress
 }
 'write-request' {
  $path=Child $r.name
  if($r.name-notmatch'^probe-[a-f0-9]{32}\.json$'){throw 'Probe request name'}
  $p=$r.probe
  if($p.runId-ne$owner.id-or$p.root-ne$owner.output-or$p.operation-notin@('snapshot','uia','clipboard-guard','clipboard-compare','fault-guard','uac-trigger','session-watch')){throw 'Probe request contract'}
  if($p.operation-eq'fault-guard'-and$CampaignManifest.caseId-ne'C16'){throw 'Fault case mismatch'}
  Write-E2eNewJson $path $p
  @{status='completed';path=$path}|ConvertTo-Json -Compress
 }
 'signal' {
  if($r.name-notin@('clipboard-restore','target-finish','fault-stop')){throw 'Signal not allowed'}
  $path=Child $r.name
  if(!(Test-Path -LiteralPath $path)){Write-E2eNewJson $path @{id=$owner.id;tick=[Diagnostics.Stopwatch]::GetTimestamp()}}
  @{status='completed'}|ConvertTo-Json -Compress
 }
 'start-fault' {
  if($CampaignManifest.caseId-ne'C16'-or$r.name-notmatch'^probe-[a-f0-9]{32}\.json$'){throw 'Fault request mismatch'}
  $spec=Child $r.name;$probe=Read-E2eJson $spec
  if($probe.operation-ne'fault-guard'-or$probe.runId-ne$owner.id){throw 'Fault probe mismatch'}
  $taskName='RemoteAssist-E2E-Fault-'+$owner.id
  if(Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue){throw 'Fault replay forbidden'}
  $exe=Join-Path $owner.root 'desktop-tool/E2eDesktop.exe'
  $entry=@($CampaignManifest.testTools.'desktop-tool'|Where-Object path -eq 'E2eDesktop.exe')
  if($entry.Count-ne1-or(Get-FileHash -LiteralPath $exe).Hash-ne$entry[0].sha256){throw 'Protected fault tool changed'}
  $action=New-ScheduledTaskAction -Execute $exe -Argument ('--request "'+$spec+'"')
  $principal=New-ScheduledTaskPrincipal -UserId SYSTEM -LogonType ServiceAccount -RunLevel Highest
  $settings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Seconds 20) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
  Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Settings $settings|Out-Null
  Start-ScheduledTask -TaskName $taskName
  @{status='completed';task=$taskName}|ConvertTo-Json -Compress
 }
 'retire' {
  if($r.name-notmatch'^probe-[a-f0-9]{32}-result\.json$'){throw 'Independent snapshot required'}
  $snapshot=Read-E2eJson (Child $r.name)
  $elapsed=([Diagnostics.Stopwatch]::GetTimestamp()-[long]$snapshot.tick)/[double]$snapshot.frequency
  if(!$snapshot.available-or$snapshot.desktop-ne'Default'-or$snapshot.sid-ne$CampaignManifest.identity.b.sid-or
     $snapshot.session-ne$CampaignManifest.identity.b.session-or$snapshot.activeSessionId-ne$snapshot.session-or
     @($snapshot.heldVirtualKeys).Count-or$elapsed-lt0-or$elapsed-gt10){throw 'Current independent B recovery missing'}
  if((Get-Service RemoteAssistHost -ErrorAction SilentlyContinue)-or(Test-Path "$env:ProgramFiles/RemoteAssist-Dev")){throw 'Product resources remain'}
  $remaining=@(Get-CimInstance Win32_Process -OperationTimeoutSec 3|Where-Object {$_.ExecutablePath-like "$env:ProgramFiles\RemoteAssist-Dev\*"})
  if($remaining.Count){throw 'Product processes remain'}
  $proof=Join-Path $owner.root 'product-recovery-verified.json'
  if(!(Test-Path $proof)){Write-E2eNewJson $proof @{id=$owner.id;snapshot=$r.name;tick=[Diagnostics.Stopwatch]::GetTimestamp()}}
  $end=[Diagnostics.Stopwatch]::GetTimestamp()+[Diagnostics.Stopwatch]::Frequency*3
  foreach($name in @($owner.task,$owner.watchdog,('RemoteAssist-E2E-Fault-'+$owner.id))){
   $task=Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
   if(!$task){continue}
   if($task.Principal.UserId-notin@('SYSTEM','S-1-5-18')-or$task.Actions.Count-ne1-or
      ($task.Actions[0].Arguments-notlike('*'+$owner.root+'*')-and$task.Actions[0].Execute-notlike($owner.root+'\*'))){throw 'Task owner changed'}
   while($task.State-eq'Running'-and[Diagnostics.Stopwatch]::GetTimestamp()-lt$end){Start-Sleep -Milliseconds 50;$task=Get-ScheduledTask -TaskName $name}
   if($task.State-eq'Running'){throw 'Guard has not exited; retained'}
   Unregister-ScheduledTask -TaskName $name -Confirm:$false
  }
  @{status='completed';retired=$true}|ConvertTo-Json -Compress
 }
 default {throw 'Unknown closed bridge operation'}
}
