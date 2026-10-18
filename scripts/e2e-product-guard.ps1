[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RunDir,[ValidateSet('Controller','Watchdog')][string]$Role='Controller')
$ErrorActionPreference='Stop'
Add-Type -Path (Join-Path $PSScriptRoot 'e2e-native.cs')
$utf8=New-Object Text.UTF8Encoding($false)
$plan=Get-Content (Join-Path $RunDir 'product-plan.json') -Raw -Encoding UTF8|ConvertFrom-Json
if($plan.id-notmatch'^[a-f0-9]{32}$'-or$plan.profile-ne'lan-view-consent-stop'-or$plan.review-ne'ACCEPTED'){throw 'Reviewed selected campaign required'}
$shell=Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
$sc=Join-Path $env:SystemRoot 'System32/sc.exe'
$image=Join-Path $env:ProgramFiles 'RemoteAssist-Dev/RemoteAssist.Host.Service.exe'
$cert=Join-Path $RunDir 'lan-test.cer'
if((Get-FileHash $cert).Hash-ne'45EEBA84A6D2E5E30EC083C4E1981F6152C25625A0FF28E4F1E314A70087AED2'){throw 'TLS certificate changed'}
$thumb=(New-Object Security.Cryptography.X509Certificates.X509Certificate2($cert)).Thumbprint
function Save($Name,$Value){
 $path=Join-Path $RunDir $Name;$temp=$path+'.'+$PID+'.tmp'
 [IO.File]::WriteAllText($temp,($Value|ConvertTo-Json -Depth 5),$utf8)
 if(Test-Path $path){[IO.File]::Replace($temp,$path,[NullString]::Value)}else{[IO.File]::Move($temp,$path)}
}
function Read($Name){
 $stream=New-Object IO.FileStream((Join-Path $RunDir $Name),[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite-bor[IO.FileShare]::Delete))
 $reader=New-Object IO.StreamReader($stream,[Text.Encoding]::UTF8)
 try{$reader.ReadToEnd()|ConvertFrom-Json}finally{$reader.Dispose()}
}
function Beat {if($Role-eq'Controller'){Save 'product-heartbeat.json' @{tick=[E2eOwnedCommand]::Tick}}}
function Owned([string]$Exe,[string[]]$Values,[int]$Milliseconds){
 $command=[E2eOwnedCommand]::Start($Exe,$Values)
 $end=[Math]::Min($deadline,[E2eOwnedCommand]::Deadline($Milliseconds))
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
function Cleanup {
 $script:cleaning=$true
 $lock=New-Object IO.FileStream((Join-Path $RunDir 'product-cleanup.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
 try{
  if(Test-Path (Join-Path $RunDir 'product-cleanup.json')){return}
  $errors=New-Object 'Collections.Generic.List[string]'
  try{
   $status=Join-Path (Split-Path $image) 'service-status.ndjson'
   if(Test-Path $status){Copy-Item -LiteralPath $status -Destination (Join-Path $RunDir 'service-status.ndjson')}
   Installer 'Remove'
  }catch{$errors.Add($_.Exception.Message)}
  if(Test-Path (Join-Path $RunDir 'tls-owner.json')){
   $ownership=Read 'tls-owner.json'
   if($ownership.added-and$ownership.thumb-eq$thumb){
    try{$store=New-Object Security.Cryptography.X509Certificates.X509Store('Root','LocalMachine');$store.Open('ReadWrite');foreach($entry in @($store.Certificates|Where-Object {$_.Thumbprint-eq$thumb})){$store.Remove($entry)};$store.Close()}catch{$errors.Add('TLS trust removal failed')}
   }
  }
  Save 'product-cleanup.json' @{state=$(if($errors.Count){'RECOVERY_REQUIRED'}else{'CLEAN'});errors=@($errors);tick=[E2eOwnedCommand]::Tick;by=$Role;serviceAbsent=!(Test-Path 'HKLM:\SYSTEM\CurrentControlSet\Services\RemoteAssistHost');productDirectoryAbsent=!(Test-Path (Split-Path $image))}
 }finally{$lock.Dispose()}
}
if($Role-eq'Watchdog'){
 $start=Read 'product-start.json';$deadline=[long]$start.deadline
 Save 'product-watchdog.json' @{pid=$PID;start=(Get-Process -Id $PID).StartTime.ToUniversalTime().Ticks}
 while([E2eOwnedCommand]::Remaining($deadline)-gt0){
  if(Test-Path (Join-Path $RunDir 'product-cleanup.json')){exit 0}
  $p=Get-Process -Id $start.pid -ErrorAction SilentlyContinue
  $alive=$p-and$p.StartTime.ToUniversalTime().Ticks-eq$start.start-and$p.Path-eq$shell
  $beat=Read 'product-heartbeat.json'
  if(!$alive-or([E2eOwnedCommand]::Tick-$beat.tick)-gt[Diagnostics.Stopwatch]::Frequency*10-or[E2eOwnedCommand]::Tick-ge$start.activeEnd){
   if($alive){$p.Kill();$null=$p.WaitForExit(1000)}
   try{Cleanup}catch{Save 'product-recovery-required.json' @{error=$_.Exception.Message}}
   exit 0
  }
  Start-Sleep -Milliseconds 50
 }
 throw 'Global campaign deadline exhausted'
}
if(Test-Path (Join-Path $RunDir 'product-start.json')){throw 'Campaign already started; no replay'}
if((Get-FileHash (Join-Path $plan.package 'candidate.json')).Hash-ne$plan.candidateSha256){throw 'Candidate manifest changed'}
if((Get-FileHash $cert).Hash-ne'45EEBA84A6D2E5E30EC083C4E1981F6152C25625A0FF28E4F1E314A70087AED2'){throw 'TLS certificate changed'}
$deadline=[E2eOwnedCommand]::Deadline(540000)
$activeEnd=[E2eOwnedCommand]::Deadline(300000)
Save 'product-start.json' @{deadline=$deadline;activeEnd=$activeEnd;pid=$PID;start=(Get-Process -Id $PID).StartTime.ToUniversalTime().Ticks;tick=[E2eOwnedCommand]::Tick;utc=[DateTime]::UtcNow.ToString('o')}
Beat
$script:watcher=$null
try{
 $args='-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+$PSCommandPath+'" -Role Watchdog -RunDir "'+$RunDir+'"'
 $script:watcher=Start-Process $shell -ArgumentList $args -WindowStyle Hidden -PassThru
 $ready=[E2eOwnedCommand]::Deadline(5000)
 while(!(Test-Path (Join-Path $RunDir 'product-watchdog.json'))){if($watcher.HasExited-or[E2eOwnedCommand]::Remaining($ready)-eq0){throw 'Watchdog not ready'};Beat;Start-Sleep -Milliseconds 50}
 $store=New-Object Security.Cryptography.X509Certificates.X509Store('Root','LocalMachine');$store.Open('ReadWrite')
 try{
  $exists=@($store.Certificates|Where-Object {$_.Thumbprint-eq$thumb}).Count-gt0
  Save 'tls-owner.json' @{added=!$exists;thumb=$thumb}
  if(!$exists){$store.Add((New-Object Security.Cryptography.X509Certificates.X509Certificate2($cert)))}
 }finally{$store.Close()}
 Installer 'Install'
 if((Owned $sc @('start','RemoteAssistHost') 10000)-ne0){throw 'Product service start failed'}
 $runningEnd=[Math]::Min($activeEnd,[E2eOwnedCommand]::Deadline(15000));$started=Get-Service RemoteAssistHost
 while($started.Status-ne'Running'-and[E2eOwnedCommand]::Remaining($runningEnd)-gt0){Beat;Start-Sleep -Milliseconds 50;$started.Refresh()}
 if($started.Status-ne'Running'){throw 'Product service running signal missing'}
 $started.Dispose()
 Save 'product-running.json' @{tick=[E2eOwnedCommand]::Tick;state='STARTED_NOT_FUNCTION_PASS'}
 while([E2eOwnedCommand]::Remaining($activeEnd)-gt0-and!(Test-Path (Join-Path $RunDir 'finish.json'))){
  if($watcher.HasExited){throw 'Watchdog lost'}
  $service=Get-Service RemoteAssistHost -ErrorAction SilentlyContinue
  if(!$service-or$service.Status-eq'Stopped'){throw 'Product service exited unexpectedly'}
  $service.Dispose();Beat;Start-Sleep -Milliseconds 100
 }
 if(!(Test-Path (Join-Path $RunDir 'finish.json'))){throw 'Selected execution time limit'}
}catch{Save 'product-error.json' @{error=$_.Exception.Message;line=$_.InvocationInfo.ScriptLineNumber}}
finally{
 try{Cleanup}catch{Save 'product-recovery-required.json' @{error=$_.Exception.Message}}
 if($watcher){$null=$watcher.WaitForExit(2000)}
}
