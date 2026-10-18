[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RunDir,[ValidateSet('Controller','Watchdog')][string]$Role='Controller')
$ErrorActionPreference='Stop'
Add-Type -Path (Join-Path $PSScriptRoot 'e2e-native.cs')
$utf8=New-Object Text.UTF8Encoding($false)
$plan=Get-Content (Join-Path $RunDir 'service-plan.json') -Raw -Encoding UTF8|ConvertFrom-Json
if($plan.id-notmatch'^[a-f0-9]{32}$'-or$plan.mode-ne'scm-fixture'){throw 'Only reviewed SCM fixture enabled; product remains blocked'}
$service='RAE2E-'+$plan.id
$image=Join-Path $RunDir 'ScmFixture.exe'
$expected='"'+$image+'" '+$service
$shell=Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
$sc=Join-Path $env:SystemRoot 'System32/sc.exe'
$registry='HKLM:\SYSTEM\CurrentControlSet\Services\'+$service
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
function Beat {if($Role-eq'Controller'){Save 'service-heartbeat.json' @{tick=[E2eOwnedCommand]::Tick}}}
function Native([string[]]$Values){
 $command=[E2eOwnedCommand]::Start($sc,$Values)
 $end=[Math]::Min($deadline,[E2eOwnedCommand]::Deadline(10000))
 try{
  while(!$command.HasExited){
   if([E2eOwnedCommand]::Remaining($end)-eq0){throw 'SCM command timeout'}
   Beat;Start-Sleep -Milliseconds 50
  }
  return $command.Wait($end,1000)
 }finally{$command.Dispose()}
}
function Cleanup {
 $lock=New-Object IO.FileStream((Join-Path $RunDir 'service-cleanup.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
 try{
  if(Test-Path (Join-Path $RunDir 'service-cleanup.json')){return}
  if(Test-Path $registry){
   if((Get-ItemProperty $registry).ImagePath-ne$expected){throw 'SCM fixture ownership changed'}
   $exit=Native @('stop',$service);if($exit-notin@(0,1062)){throw ('SCM stop failed: '+$exit)}
   $stopEnd=[Math]::Min($deadline,[E2eOwnedCommand]::Deadline(10000))
   do{
    $s=Get-Service $service -ErrorAction SilentlyContinue
    if(!$s-or$s.Status-eq'Stopped'){break}
    Beat;Start-Sleep -Milliseconds 50
   }while([E2eOwnedCommand]::Remaining($stopEnd)-gt0)
   if($s-and$s.Status-ne'Stopped'){throw 'SCM fixture did not stop'}
   if((Native @('delete',$service))-ne0){throw 'SCM delete failed'}
   $s=$null
  }
  $end=[Math]::Min($deadline,[E2eOwnedCommand]::Deadline(5000))
  while((Test-Path $registry)-and[E2eOwnedCommand]::Remaining($end)-gt0){Beat;Start-Sleep -Milliseconds 50}
  $left=@(Get-Process -Name ScmFixture -ErrorAction SilentlyContinue|Where-Object {$_.Path-eq$image})
  if((Test-Path $registry)-or$left.Count){throw 'Fixture service or process remains'}
  Save 'service-cleanup.json' @{state='PASS';tick=[E2eOwnedCommand]::Tick;by=$Role;serviceAbsent=$true;processAbsent=$true}
 }finally{$lock.Dispose()}
}
if($Role-eq'Watchdog'){
 $start=Read 'service-start.json';$deadline=[long]$start.deadline
 Save 'service-watchdog.json' @{pid=$PID;start=(Get-Process -Id $PID).StartTime.ToUniversalTime().Ticks}
 while([E2eOwnedCommand]::Remaining($deadline)-gt0){
  if(Test-Path (Join-Path $RunDir 'service-cleanup.json')){exit 0}
  $p=Get-Process -Id $start.pid -ErrorAction SilentlyContinue
  $alive=$p-and$p.StartTime.ToUniversalTime().Ticks-eq$start.start-and$p.Path-eq$shell
  $beat=Read 'service-heartbeat.json'
  if(!$alive-or([E2eOwnedCommand]::Tick-$beat.tick)-gt[Diagnostics.Stopwatch]::Frequency*10-or[E2eOwnedCommand]::Tick-ge$start.activeEnd){
   if($alive){$p.Kill();$null=$p.WaitForExit(1000)}
   try{Cleanup}catch{Save 'service-recovery-required.json' @{error=$_.Exception.Message}}
   exit 0
  }
  Start-Sleep -Milliseconds 50
 }
 throw 'SCM fixture global deadline exhausted'
}
if((Test-Path $registry)-or(Test-Path (Join-Path $RunDir 'service-start.json'))){throw 'Existing service or campaign; no replay'}
if((Get-FileHash $image).Hash-ne$plan.imageSha256){throw 'SCM fixture image changed'}
$deadline=[E2eOwnedCommand]::Deadline(60000)
$activeEnd=[E2eOwnedCommand]::Deadline(25000)
Save 'service-start.json' @{deadline=$deadline;activeEnd=$activeEnd;pid=$PID;start=(Get-Process -Id $PID).StartTime.ToUniversalTime().Ticks;tick=[E2eOwnedCommand]::Tick}
Beat
$watcher=$null
try{
 $args='-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+$PSCommandPath+'" -Role Watchdog -RunDir "'+$RunDir+'"'
 $watcher=Start-Process $shell -ArgumentList $args -WindowStyle Hidden -PassThru
 $ready=[E2eOwnedCommand]::Deadline(5000)
 while(!(Test-Path (Join-Path $RunDir 'service-watchdog.json'))){if($watcher.HasExited-or[E2eOwnedCommand]::Remaining($ready)-eq0){throw 'SCM watchdog not ready'};Beat;Start-Sleep -Milliseconds 50}
 if((Native @('create',$service,'binPath=',$expected,'start=','demand','obj=','LocalSystem'))-ne0){throw 'SCM create failed'}
 if((Native @('start',$service))-ne0){throw 'SCM start failed'}
 # sc start may return START_PENDING. Do not race OnStart with stop/fault injection.
 $runningEnd=[Math]::Min($deadline,[E2eOwnedCommand]::Deadline(5000))
 $started=Get-Service $service
 while($started.Status-ne'Running'-and[E2eOwnedCommand]::Remaining($runningEnd)-gt0){Beat;Start-Sleep -Milliseconds 50;$started.Refresh()}
 if($started.Status-ne'Running'){throw 'SCM running readiness missing'}
 $started.Dispose()
 Save 'service-running.json' @{tick=[E2eOwnedCommand]::Tick}
 if($plan.fault-eq'controller'){[Diagnostics.Process]::GetCurrentProcess().Kill()}
 if($plan.fault-eq'watchdog'){$watcher.Kill()}
 if($plan.fault-eq'heartbeat'){Start-Sleep -Seconds 20}
}catch{Save 'service-error.json' @{error=$_.Exception.Message;line=$_.InvocationInfo.ScriptLineNumber}}
finally{
 try{Cleanup}catch{Save 'service-recovery-required.json' @{error=$_.Exception.Message}}
 if($watcher){$null=$watcher.WaitForExit(2000)}
}
