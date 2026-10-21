[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RunDir,[Parameter(Mandatory=$true)][string]$ManifestSha256,[ValidateSet('Controller','Watchdog','Recovery')][string]$Role='Controller')
$ErrorActionPreference='Stop'
# Bootstrap only after authenticating both the manifest and the contract loader.
$manifestPath=Join-Path $RunDir 'campaign.json'
if($ManifestSha256-notmatch'^[a-fA-F0-9]{64}$'-or(Get-FileHash -LiteralPath $manifestPath).Hash-ne$ManifestSha256){throw 'Explicit frozen campaign required'}
$bootstrap=Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8|ConvertFrom-Json
$contract=@($bootstrap.tools|Where-Object {$_.path-eq'scripts/e2e-run-contract.ps1'})
if($contract.Count-ne1-or(Get-FileHash -LiteralPath (Join-Path $RunDir $contract[0].path)).Hash-ne$contract[0].sha256){throw 'Frozen contract mismatch'}
. (Join-Path $RunDir $contract[0].path)
$manifest=Read-E2eManifest $manifestPath $ManifestSha256
Assert-E2eTools $manifest $RunDir
Add-Type -Path (Join-Path $PSScriptRoot 'e2e-native.cs')
$utf8=New-Object Text.UTF8Encoding($false)
$plan=Get-Content (Join-Path $RunDir 'product-plan.json') -Raw -Encoding UTF8|ConvertFrom-Json
if($plan.id-ne$manifest.id-or$plan.manifestSha256-ne$ManifestSha256-or$plan.profile-ne$manifest.profile-or$plan.review-ne'ACCEPTED'){throw 'Reviewed selected campaign required'}
$shell=Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
$sc=Join-Path $env:SystemRoot 'System32/sc.exe'
$image=Join-Path $env:ProgramFiles 'RemoteAssist-Dev/RemoteAssist.Host.Service.exe'
$cert=Join-Path $RunDir 'lan-test.cer'
if((Get-FileHash $cert).Hash-ne$manifest.tls.sha256){throw 'TLS certificate changed'}
$thumb=(New-Object Security.Cryptography.X509Certificates.X509Certificate2($cert)).Thumbprint
if($thumb-ne$manifest.tls.thumbprint){throw 'TLS identity changed'}
$script:cleaning=$false;$script:watcher=$null
function Save($Name,$Value,[switch]$New){
 $path=Join-Path $RunDir $Name
 if($New){Write-E2eNewJson $path $Value;return}
 Write-E2eAtomicJson $path $Value
}
function Read($Name){
 return Read-E2eJson (Join-Path $RunDir $Name)
}
function Beat {if($Role-eq'Controller'){Save 'product-heartbeat.json' @{tick=[E2eOwnedCommand]::Tick}}}
function Owned([string]$Exe,[string[]]$Values,[int]$Milliseconds){
 $command=[E2eOwnedCommand]::Start($Exe,$Values)
 $end=[Math]::Min($deadline,[E2eOwnedCommand]::Deadline($Milliseconds))
 if(!$script:cleaning){$end=[Math]::Min($end,$activeEnd)}
 try{
  while(!$command.HasExited){
   if([E2eOwnedCommand]::Remaining($end)-eq0){throw 'Owned operation timeout'}
   if(!$script:cleaning-and$script:watcher-and$script:watcher.HasExited){throw 'Watchdog lost'}
   Beat;Start-Sleep -Milliseconds 50
  }
  return $command.Wait($end,1000)
 }finally{$command.Dispose()}
}
function Installer([string]$Action){
 $limit=[Math]::Min($deadline,[E2eOwnedCommand]::Deadline(120000))
 $exit=Owned $shell @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',(Join-Path $PSScriptRoot 'install-host-dev.ps1'),'-Action',$Action,'-Package',$plan.package,'-OwnerId',$plan.id,'-DeadlineTick',[string]$limit) 120000
 if($exit-ne0){throw ('Installer '+$Action+' exit '+$exit)}
}
function Certificate([string]$Action){
 $exit=Owned $shell @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',(Join-Path $PSScriptRoot 'e2e-certificate.ps1'),'-Action',$Action,'-RunDir',$RunDir,'-CertificateSha256',$manifest.tls.sha256) 10000
 if($exit-ne0){throw 'Bounded certificate operation failed'}
}
function Inspect {
 $path=Join-Path $RunDir ('resource-inspect-'+[guid]::NewGuid().ToString('N')+'.json')
 $exit=Owned $shell @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',(Join-Path $PSScriptRoot 'e2e-resource-inspect.ps1'),'-Output',$path,'-Thumbprint',$thumb) 10000
 if($exit-ne0){throw 'Bounded resource observation failed'}
 return Read-E2eJson $path
}
function Cleanup {
 $script:cleaning=$true
 if($Role-ne'Recovery'){$deadline=[Math]::Min($deadline,($activeEnd+[Diagnostics.Stopwatch]::Frequency*240))}
 if(!(Test-Path (Join-Path $RunDir 'product-cleaning.json'))){try{Save 'product-cleaning.json' @{id=$manifest.id;tick=[E2eOwnedCommand]::Tick;deadline=$deadline} -New}catch [IO.IOException]{}}
 $lock=$null;$lockEnd=[Math]::Min($deadline,[E2eOwnedCommand]::Deadline(2000))
 while(!$lock-and[E2eOwnedCommand]::Remaining($lockEnd)-gt0){try{$lock=New-Object IO.FileStream((Join-Path $RunDir 'product-cleanup.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)}catch [IO.IOException]{Start-Sleep -Milliseconds 20}}
 if(!$lock){throw 'Cleanup ownership unavailable'}
 try{
  $cleanupName=if($Role-eq'Recovery'){'product-recovery-cleanup.json'}else{'product-cleanup.json'}
  if(Test-Path (Join-Path $RunDir $cleanupName)){throw 'Cleanup already attempted; inspect evidence'}
  $errors=New-Object 'Collections.Generic.List[string]'
  try{
   $status=Join-Path (Split-Path $image) 'service-status.ndjson'
   if(Test-Path $status){Copy-Item -LiteralPath $status -Destination (Join-Path $RunDir 'service-status.ndjson')}
   Installer 'Remove'
  }catch{$errors.Add($_.Exception.Message)}
  if(Test-Path (Join-Path $RunDir 'tls-owner.json')){
   $ownership=Read 'tls-owner.json'
   if($ownership.added-and$ownership.thumb-eq$thumb){
    try{Certificate 'Remove'}catch{$errors.Add('TLS trust removal failed')}
   }
  }
  $actual=$null
  try{
   $actual=Inspect
   if(!$actual.serviceAbsent-or!$actual.productDirectoryAbsent-or@($actual.ownedProcesses).Count-or((Test-Path (Join-Path $RunDir 'tls-owner.json'))-and(Read 'tls-owner.json').added-and$actual.trustPresent)){$errors.Add('Owned resources still present')}
  }catch{$errors.Add('Resource absence not observed')}
  Save $cleanupName @{id=$manifest.id;state=$(if($errors.Count){'RECOVERY_REQUIRED'}else{'RESOURCES_ABSENT_RECOVERY_UNVERIFIED'});errors=@($errors);tick=[E2eOwnedCommand]::Tick;by=$Role;actual=$actual;recoveryVerified=$false} -New
 }finally{$lock.Dispose()}
}
if($Role-eq'Recovery'){
 $start=Read 'product-start.json';$deadline=[long]$start.deadline;$activeEnd=[long]$start.activeEnd
 if($start.id-ne$manifest.id){throw 'Recovery identity missing'}
 # An exhausted test budget must not disable finite safety cleanup.
 if([E2eOwnedCommand]::Remaining($deadline)-eq0){$deadline=[E2eOwnedCommand]::Deadline(30000)}
 $running=Read 'product-running.json'
 $service=@($running.ownedProcesses|Where-Object {$_.image-eq$image})
 if($service.Count-eq1){
  $p=Get-Process -Id $service[0].pid -ErrorAction SilentlyContinue
  if($p){
   $owner=Read-E2eJson (Join-Path (Split-Path $image) 'dev-owner.json')
   if($owner.id-ne$manifest.id-or$p.Path-ne$image-or$p.StartTime.ToUniversalTime().Ticks-ne$service[0].start){throw 'Recovery service ownership changed'}
   $p.Kill();$null=$p.WaitForExit(1000)
  }
 }
 Cleanup
 exit 0
}
if($Role-eq'Watchdog'){
 $start=Read 'product-start.json';$deadline=[long]$start.deadline;$activeEnd=[long]$start.activeEnd
 if($start.id-ne$manifest.id-or$deadline-ne$plan.remoteDeadlineTick){throw 'Watchdog campaign clock mismatch'}
 Save 'product-watchdog.json' @{pid=$PID;start=(Get-Process -Id $PID).StartTime.ToUniversalTime().Ticks}
 try{while([E2eOwnedCommand]::Remaining($deadline)-gt0){
  if(Test-Path (Join-Path $RunDir 'product-recovery-verified.json')){exit 0}
  if(Test-Path (Join-Path $RunDir 'product-cleanup.json')){Start-Sleep -Milliseconds 100;continue}
  $p=Get-Process -Id $start.pid -ErrorAction SilentlyContinue
  $alive=$p-and$p.StartTime.ToUniversalTime().Ticks-eq$start.start-and$p.Path-eq$shell
  $beat=Read 'product-heartbeat.json'
  $cleaning=Test-Path (Join-Path $RunDir 'product-cleaning.json')
  $phaseEnd=if($cleaning){[long](Read 'product-cleaning.json').deadline}else{$activeEnd}
  if(!$alive-or([E2eOwnedCommand]::Tick-$beat.tick)-gt[Diagnostics.Stopwatch]::Frequency*10-or[E2eOwnedCommand]::Tick-ge$phaseEnd){
   if($alive){$p.Kill();$null=$p.WaitForExit(1000)}
   try{Cleanup}catch{Save 'product-recovery-required.json' @{error=$_.Exception.Message}}
   continue
  }
  Start-Sleep -Milliseconds 50
 }}catch{Save 'product-watchdog-error.json' (Get-E2eFailure $_ 'watchdog' $Role $script:cleaning)}
 finally{
  if(!(Test-Path (Join-Path $RunDir 'product-cleanup.json'))){
   $p=Get-Process -Id $start.pid -ErrorAction SilentlyContinue
   if($p-and$p.StartTime.ToUniversalTime().Ticks-eq$start.start-and$p.Path-eq$shell){$p.Kill();$null=$p.WaitForExit(1000)}
   try{Cleanup}catch{Save 'product-recovery-required.json' (Get-E2eFailure $_ 'cleanup' $Role $script:cleaning)}
  }
  if(!(Test-Path (Join-Path $RunDir 'product-recovery-verified.json'))){Save 'product-recovery-required.json' @{error='Independent recovery confirmation required'}}
 }
 exit 0
}
if(Test-Path (Join-Path $RunDir 'product-start.json')){throw 'Campaign already started; no replay'}
if($plan.candidateSha256-ne$manifest.candidateSha256-or(Get-FileHash (Join-Path $plan.package 'candidate.json')).Hash-ne$manifest.candidateSha256){throw 'Candidate manifest changed'}
# These B QPC anchors must be derived once from a B sample/A receipt handshake. Never mint
# a fresh duration on B. Missing/late grants are rejected before any product action.
$deadline=[long]$plan.remoteDeadlineTick;$activeEnd=[long]$plan.remoteActiveEndTick
if($deadline-le[E2eOwnedCommand]::Tick-or$activeEnd-le[E2eOwnedCommand]::Tick-or$activeEnd-ge$deadline-or($deadline-$activeEnd)-lt[Diagnostics.Stopwatch]::Frequency*540){throw 'Global deadline grant/reserve missing'}
Save 'product-start.json' @{id=$manifest.id;deadline=$deadline;activeEnd=$activeEnd;pid=$PID;start=(Get-Process -Id $PID).StartTime.ToUniversalTime().Ticks;tick=[E2eOwnedCommand]::Tick;utc=[DateTime]::UtcNow.ToString('o')} -New
Beat
try{
 if($plan.watchdogTask-ne('RemoteAssist-E2E-Watchdog-'+$manifest.id)){throw 'Independent watchdog task mismatch'}
 $wdTask=Get-ScheduledTask -TaskName $plan.watchdogTask
 if($wdTask.Principal.UserId-notin@('SYSTEM','S-1-5-18')-or$wdTask.Actions.Count-ne1-or$wdTask.Actions[0].Arguments-notlike('*"'+$RunDir+'"*')-or$wdTask.Actions[0].Arguments-notlike'* -Role Watchdog'){throw 'Independent watchdog ownership changed'}
 if((Owned (Join-Path $env:SystemRoot 'System32/schtasks.exe') @('/Run','/TN',$plan.watchdogTask) 10000)-ne0){throw 'Independent watchdog start failed'}
 $ready=[E2eOwnedCommand]::Deadline(5000)
 while(!(Test-Path (Join-Path $RunDir 'product-watchdog.json'))){if([E2eOwnedCommand]::Remaining($ready)-eq0){throw 'Watchdog not ready'};Beat;Start-Sleep -Milliseconds 50}
 $wd=Read 'product-watchdog.json';$script:watcher=Get-Process -Id $wd.pid
 if($watcher.StartTime.ToUniversalTime().Ticks-ne$wd.start-or$watcher.Path-ne$shell){throw 'Watchdog identity changed'}
 if($manifest.version-eq4){
  if((Owned $shell @('-NoProfile','-NonInteractive','-File',(Join-Path $PSScriptRoot 'e2e-tls-identity.ps1'),'-Certificate',$cert,'-Sha256',$manifest.tls.sha256) 10000)-ne0){throw 'Existing TLS chain validation failed'}
 }else{Certificate 'Add'}
 Installer 'Install'
 if((Owned $sc @('start','RemoteAssistHost') 10000)-ne0){throw 'Product service start failed'}
 $runningEnd=[Math]::Min($activeEnd,[E2eOwnedCommand]::Deadline(15000));$started=Inspect
 while($started.serviceState-ne'Running'-and[E2eOwnedCommand]::Remaining($runningEnd)-gt0){Beat;Start-Sleep -Milliseconds 100;$started=Inspect}
 if($started.serviceState-ne'Running'){throw 'Product service running signal missing'}
 Save 'product-running.json' @{id=$manifest.id;ownedProcesses=$started.ownedProcesses;tick=[E2eOwnedCommand]::Tick;state='STARTED_NOT_FUNCTION_PASS'}
 while([E2eOwnedCommand]::Remaining($activeEnd)-gt0-and!(Test-Path (Join-Path $RunDir 'finish.json'))){
  if($watcher.HasExited){throw 'Watchdog lost'}
  $service=Inspect
  if($service.serviceAbsent-or$service.serviceState-eq'Stopped'){
   $expectedFault=$false
   if($manifest.version-eq4-and$manifest.caseId-eq'C16'){
    $intentPath=Join-Path (Join-Path $manifest.staging 'observer') 'fault-fired.json.intent'
    if(Test-Path -LiteralPath $intentPath){
     $intent=Read-E2eJson $intentPath
     $bound=@($started.ownedProcesses|Where-Object {$_.image-eq$image-and$_.pid-eq$intent.servicePid})
     $expectedFault=$intent.runId-eq$manifest.id-and$bound.Count-eq1
    }
   }
   if(!$expectedFault){throw 'Product service exited unexpectedly'}
   # Deliberate C16 crash: keep observers/watchdogs alive, never restart SCM.
  }
  Beat;Start-Sleep -Milliseconds 500
 }
 if(!(Test-Path (Join-Path $RunDir 'finish.json'))){throw 'Selected execution time limit'}
}catch{Save 'product-error.json' @{error=$_.Exception.Message;line=$_.InvocationInfo.ScriptLineNumber}}
finally{
 try{Cleanup}catch{Save 'product-recovery-required.json' @{error=$_.Exception.Message}}
 if($watcher){$null=$watcher.WaitForExit(2000)}
}
