[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RunDir,[Parameter(Mandatory=$true)][string]$ManifestSha256,[ValidateSet('Controller','Watchdog','Recovery')][string]$Role='Controller')
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$manifestPath=Join-Path $RunDir 'campaign.json'
if($ManifestSha256-notmatch'^[a-fA-F0-9]{64}$'-or(Get-FileHash -LiteralPath $manifestPath).Hash-ne$ManifestSha256){throw 'Frozen local campaign required'}
$bootstrap=Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8|ConvertFrom-Json
$contract=@($bootstrap.tools|Where-Object {$_.path-eq'scripts/e2e-run-contract.ps1'})
if($contract.Count-ne1-or(Get-FileHash -LiteralPath (Join-Path $repo $contract[0].path)).Hash-ne$contract[0].sha256){throw 'Frozen local contract changed'}
. (Join-Path $repo $contract[0].path)
$manifest=Read-E2eManifest $manifestPath $ManifestSha256
Assert-E2eTools $manifest $repo
if([IO.Path]::GetFullPath($manifest.localRun)-ne[IO.Path]::GetFullPath($RunDir)){throw 'Local owner mismatch'}
foreach($file in $manifest.localFiles){$null=Assert-E2ePath $file.path;if((Get-FileHash -LiteralPath $file.path).Hash-ne$file.sha256){throw 'Local candidate/config changed'}}
$authorization=Read-E2eJson (Join-Path $RunDir 'execution-authorization.json')
if($authorization.id-ne$manifest.id-or$authorization.manifestSha256-ne$ManifestSha256-or$authorization.scope-ne$manifest.profile-or$authorization.requestedBy-ne'user-after-readiness-report'){throw 'Separate execution instruction required'}
Add-Type -Path (Join-Path $PSScriptRoot 'e2e-native.cs')
$shell=Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
$utf8=New-Object Text.UTF8Encoding($false)
$script:watcher=$null;$script:server=$null;$script:viewer=$null;$script:cleaning=$false
$script:engine=$null
function Save($Name,$Value,[switch]$New){
 $path=Join-Path $RunDir $Name
 if($New){Write-E2eNewJson $path $Value;return}
 Write-E2eAtomicJson $path $Value
}
function Read($Name){return Read-E2eJson (Join-Path $RunDir $Name)}
function Beat {if($Role-eq'Controller'){Save 'local-heartbeat.json' @{id=$manifest.id;tick=[E2eOwnedCommand]::Tick}}}
function Owned([string]$Exe,[string[]]$Arguments,[int]$Milliseconds){
 $end=[Math]::Min($deadline,[E2eOwnedCommand]::Deadline($Milliseconds));if(!$script:cleaning){$end=[Math]::Min($end,$activeEnd)}
 $command=[E2eOwnedCommand]::Start($Exe,$Arguments)
 try{
  while(!$command.HasExited){
   if([E2eOwnedCommand]::Remaining($end)-eq0){throw 'Local owned operation timeout'}
   if(!$script:cleaning-and$script:watcher-and$script:watcher.HasExited){throw 'Local watchdog lost'}
   Beat;Start-Sleep -Milliseconds 50
  }
  if($command.Wait($end,1000)-ne0){throw 'Local owned operation failed'}
 }finally{$command.Dispose()}
}
function Certificate([string]$Action){Owned $shell @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',(Join-Path $PSScriptRoot 'e2e-certificate.ps1'),'-Action',$Action,'-RunDir',$RunDir,'-CertificateSha256',$manifest.tls.sha256,'-StoreScope','CurrentUser') 45000}
function ProcessIdentity($Command){$p=Get-Process -Id $Command.Pid;return @{pid=$p.Id;image=$p.Path;start=$p.StartTime.ToUniversalTime().Ticks;session=$p.SessionId}}
function Cleanup {
 $script:cleaning=$true
 if($Role-ne'Recovery'){$deadline=[Math]::Min($deadline,($activeEnd+[Diagnostics.Stopwatch]::Frequency*240))}
 if(!(Test-Path (Join-Path $RunDir 'local-cleaning.json'))){try{Save 'local-cleaning.json' @{deadline=$deadline;id=$manifest.id} -New}catch [IO.IOException]{}}
 $lock=$null;$lockEnd=[Math]::Min($deadline,[E2eOwnedCommand]::Deadline(2000))
 while(!$lock-and[E2eOwnedCommand]::Remaining($lockEnd)-gt0){try{$lock=New-Object IO.FileStream((Join-Path $RunDir 'local-cleanup.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)}catch [IO.IOException]{Start-Sleep -Milliseconds 20}}
 if(!$lock){throw 'Local cleanup ownership unavailable'}
 try{
  $cleanupName=if($Role-eq'Recovery'){'local-recovery-cleanup.json'}else{'local-cleanup.json'}
  if(Test-Path (Join-Path $RunDir $cleanupName)){throw 'Cleanup already attempted; inspect evidence'}
  $errors=New-Object 'Collections.Generic.List[string]'
  if($manifest.version-eq4){
   # Independent local controller/watchdog can stop B even when the Python case
   # engine has died. The remote finish operation also signals clipboard restore.
   try{Owned $manifest.local.python @((Join-Path $PSScriptRoot 'e2e-product-client.py'),'finish','--campaign',$manifestPath) 10000}catch{$errors.Add('Remote stop not confirmed')}
  }
  if($script:viewer){$script:viewer.Dispose()};if($script:server){$script:server.Dispose()}
  if(Test-Path (Join-Path $RunDir 'tls-owner.json')){
   try{Certificate 'Remove';$trust=Read 'tls-owner.json';$status=Read 'tls-status.json';if($trust.added-and$status.present){throw 'Temporary trust remains'}}catch{$errors.Add('Local trust recovery required')}
  }
  $remaining=@()
  if(Test-Path (Join-Path $RunDir 'local-owned.json')){
   foreach($owner in (Read 'local-owned.json').processes){
    $p=Get-Process -Id $owner.pid -ErrorAction SilentlyContinue
    if($p){
     if($p.StartTime.ToUniversalTime().Ticks-ne$owner.start-or$p.Path-ne$owner.image){$errors.Add('Local PID identity changed');continue}
     $wait=[Math]::Min($deadline,[E2eOwnedCommand]::Deadline(2000))
     while(!$p.HasExited-and[E2eOwnedCommand]::Remaining($wait)-gt0){Start-Sleep -Milliseconds 20;$p.Refresh()}
     if(!$p.HasExited){$remaining+=@($owner);$errors.Add('Owned local process remains')}
    }
   }
  }
  # This campaign exclusively owns 25001; any remaining listener blocks recovery acceptance.
  $listenerCount=$null
  try{
   $inspection=Join-Path $RunDir ('local-resources-'+[guid]::NewGuid().ToString('N')+'.json')
   Owned $shell @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',(Join-Path $PSScriptRoot 'e2e-resource-inspect.ps1'),'-Output',$inspection,'-Thumbprint',$manifest.tls.thumbprint) 10000
   $listenerCount=(Read-E2eJson $inspection).productListenerCount
   if($listenerCount-ne0){$errors.Add('Product listener remains')}
  }catch{$errors.Add('Local resource observation failed')}
  Save $cleanupName @{id=$manifest.id;state=$(if($errors.Count){'RECOVERY_REQUIRED'}else{'RESOURCES_ABSENT_RECOVERY_UNVERIFIED'});errors=@($errors);ownedProcesses=$remaining;listeners=$listenerCount;tick=[E2eOwnedCommand]::Tick;recoveryVerified=$false} -New
 }finally{$lock.Dispose()}
}
if($Role-eq'Recovery'){
 $start=Read 'local-start.json';$deadline=[long]$start.deadline-[Diagnostics.Stopwatch]::Frequency*60;$activeEnd=[long]$start.activeEnd
 if($start.id-ne$manifest.id){throw 'Local recovery identity missing'}
 if([E2eOwnedCommand]::Remaining($deadline)-eq0){$deadline=[E2eOwnedCommand]::Deadline(30000)}
 $p=Get-Process -Id $start.pid -ErrorAction SilentlyContinue
 if($p){if($p.StartTime.ToUniversalTime().Ticks-ne$start.start-or$p.Path-ne$shell){throw 'Local controller ownership changed'};$p.Kill();$null=$p.WaitForExit(1000)}
 Cleanup
 exit 0
}
if($Role-eq'Watchdog'){
 $start=Read 'local-start.json';$deadline=[long]$start.deadline;$activeEnd=[long]$start.activeEnd
 if($start.id-ne$manifest.id){throw 'Local watchdog clock mismatch'}
 Save 'local-watchdog.json' @{pid=$PID;start=(Get-Process -Id $PID).StartTime.ToUniversalTime().Ticks;id=$manifest.id} -New
 try{
  while([E2eOwnedCommand]::Remaining($deadline)-gt0){
   if(Test-Path (Join-Path $RunDir 'local-recovery-verified.json')){exit 0}
   if(Test-Path (Join-Path $RunDir 'local-cleanup.json')){Start-Sleep -Milliseconds 100;continue}
   $p=Get-Process -Id $start.pid -ErrorAction SilentlyContinue
   $alive=$p-and$p.StartTime.ToUniversalTime().Ticks-eq$start.start-and$p.Path-eq$shell
   $beat=Read 'local-heartbeat.json'
   $phaseEnd=if(Test-Path (Join-Path $RunDir 'local-cleaning.json')){[long](Read 'local-cleaning.json').deadline}else{$activeEnd}
   if(!$alive-or$beat.id-ne$manifest.id-or([E2eOwnedCommand]::Tick-$beat.tick)-gt[Diagnostics.Stopwatch]::Frequency*10-or[E2eOwnedCommand]::Tick-ge$phaseEnd){
    if($alive){$p.Kill();$null=$p.WaitForExit(1000)}
    Cleanup
   }
   Start-Sleep -Milliseconds 100
  }
 }catch{Save 'local-watchdog-error.json' (Get-E2eFailure $_ 'watchdog' $Role $script:cleaning)}
 finally{
  if(!(Test-Path (Join-Path $RunDir 'local-cleanup.json'))){
   $p=Get-Process -Id $start.pid -ErrorAction SilentlyContinue
   if($p-and$p.StartTime.ToUniversalTime().Ticks-eq$start.start-and$p.Path-eq$shell){$p.Kill();$null=$p.WaitForExit(1000)}
   try{Cleanup}catch{Save 'local-recovery-required.json' (Get-E2eFailure $_ 'cleanup' $Role $script:cleaning)}
  }
  if(!(Test-Path (Join-Path $RunDir 'local-recovery-verified.json'))){Save 'local-recovery-required.json' @{error='Independent recovery confirmation required'}}
 }
 exit 0
}
# One immutable A origin, before trust/service/browser changes. No second terminal resets it.
$tick=[E2eOwnedCommand]::Tick;$frequency=[Diagnostics.Stopwatch]::Frequency
if($manifest.version-eq4){
 $origin=Read 'launch-once.json'
 if($origin.runId-ne$manifest.id-or$origin.caseId-ne$manifest.caseId-or$origin.frequency-ne1000000000){throw 'Immutable case origin missing'}
 $tick=[long][Math]::Floor([decimal]$origin.origin*$frequency/1000000000)
 if($tick-gt[E2eOwnedCommand]::Tick-or([E2eOwnedCommand]::Tick-$tick)-gt$frequency*30){throw 'Case origin expired'}
 $script:engine=Get-Process -Id $origin.controllerPid
 $null=$script:engine.Handle
 if($script:engine.Path-ne$origin.controllerImage){throw 'Case controller identity changed'}
}
$deadline=$tick+$frequency*900;$activeEnd=$tick+$frequency*300
Save 'local-start.json' @{id=$manifest.id;tick=$tick;frequency=$frequency;deadline=$deadline;activeEnd=$activeEnd;pid=$PID;start=(Get-Process -Id $PID).StartTime.ToUniversalTime().Ticks;utc=[DateTime]::UtcNow.ToString('o')} -New
Beat
try{
 $arguments='-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+$PSCommandPath+'" -RunDir "'+$RunDir+'" -ManifestSha256 '+$ManifestSha256+' -Role Watchdog'
 $script:watcher=Start-Process $shell -ArgumentList $arguments -WindowStyle Hidden -PassThru
 $ready=[E2eOwnedCommand]::Deadline(5000)
 while(!(Test-Path (Join-Path $RunDir 'local-watchdog.json'))){if($watcher.HasExited-or[E2eOwnedCommand]::Remaining($ready)-eq0){throw 'Independent local watchdog not ready'};Beat;Start-Sleep -Milliseconds 50}
 if($manifest.version-eq4){
  Owned $shell @('-NoProfile','-NonInteractive','-File',(Join-Path $PSScriptRoot 'e2e-tls-identity.ps1'),'-Certificate',(Join-Path $RunDir 'lan-test.cer'),'-Sha256',$manifest.tls.sha256) 10000
 }else{Certificate 'Add'}
 $secret=New-Object byte[] 48;$rng=[Security.Cryptography.RandomNumberGenerator]::Create();$rng.GetBytes($secret);$rng.Dispose()
 [IO.File]::WriteAllText((Join-Path $RunDir 'operator-secret'),[Convert]::ToBase64String($secret).Replace('+','-').Replace('/','_').TrimEnd('='))
 $env:RA_MODE='lan-test';$env:RA_LISTEN='192.168.0.61:25001';$env:RA_ALLOWED_ORIGINS='192.168.0.61:25001'
 $env:RA_TLS_CERT=$manifest.local.tlsCert;$env:RA_TLS_KEY=$manifest.local.tlsKey;$env:RA_VIEWER_DIR=Join-Path $repo 'web/viewer'
 $env:RA_OPERATOR_SECRET_FILE=Join-Path $RunDir 'operator-secret';$env:RA_AUDIT_DIR=Join-Path $RunDir 'audit'
 $script:server=[E2eOwnedCommand]::Start($manifest.local.signaling,@())
 Save 'local-owned.json' @{id=$manifest.id;processes=@((ProcessIdentity $server))}
 $ready=[Math]::Min($activeEnd,[E2eOwnedCommand]::Deadline(10000));$connected=$false
 while([E2eOwnedCommand]::Remaining($ready)-gt0){
  if($server.HasExited){throw 'Signaling exited'}
  try{$request=[Net.WebRequest]::Create('https://192.168.0.61:25001/healthz');$request.Timeout=1000;$response=$request.GetResponse();$response.Close();$connected=$true;break}catch{Beat;Start-Sleep -Milliseconds 100}
 }
 if(!$connected){throw 'Trusted HTTPS health missing'}
 $node=if($manifest.version-eq4){$manifest.local.node}else{(Get-Command node.exe).Source}
 $script:viewer=[E2eOwnedCommand]::Start($node,@((Join-Path $PSScriptRoot 'e2e-viewer.cjs'),$RunDir))
 Save 'local-owned.json' @{id=$manifest.id;processes=@((ProcessIdentity $server),(ProcessIdentity $viewer))}
 while([E2eOwnedCommand]::Remaining($activeEnd)-gt0-and!(Test-Path (Join-Path $RunDir 'finish.json'))){
  if($watcher.HasExited){throw 'Local watchdog lost'}
  if($script:engine-and$script:engine.HasExited){throw 'Case controller exited'}
  if($server.HasExited){throw 'Signaling exited'}
  if($viewer.HasExited){break}
  Beat;Start-Sleep -Milliseconds 100
 }
 if([E2eOwnedCommand]::Remaining($activeEnd)-eq0){throw 'Selected execution deadline'}
}catch{Save 'local-error.json' (Get-E2eFailure $_ 'controller' $Role $script:cleaning)}
finally{try{Cleanup}catch{Save 'local-recovery-required.json' (Get-E2eFailure $_ 'cleanup' $Role $script:cleaning)}}
