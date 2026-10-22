param([Parameter(Mandatory=$true)][ValidateSet('Arm','Watch','Stop','Cleanup')][string]$Mode,
 [Parameter(Mandatory=$true)][ValidatePattern('^\d{8}-\d{6}$')][string]$RunId)
$ErrorActionPreference='Stop'
$base='C:\Users\jacking75NoteBook\RemoteAssist-Test\'+$RunId
$task='RemoteAssist-Manual-E2E-'+$RunId
$key='HKLM:\SOFTWARE\RemoteAssistE2E-'+$RunId
$image='C:\Program Files\RemoteAssist-Dev\RemoteAssist.Host.Service.exe'
if($Mode-eq'Stop'){[IO.File]::WriteAllText("$base/stop",'stop');exit}
if($Mode-eq'Arm'){
 if(!(Test-Path -LiteralPath $base -PathType Container)){throw 'Fresh prepared directory required'}
 if((Test-Path "$base/start.json")-or(Test-Path $key)){throw 'Run already used or test key exists'}
 $svc=Get-CimInstance Win32_Service -Filter "Name='RemoteAssistHost'" -OperationTimeoutSec 3
 if($svc.PathName-ne('"'+$image+'"')-or$svc.State-ne'Stopped'){throw 'Service identity/state mismatch'}
 if(@(Get-CimInstance Win32_Process -OperationTimeoutSec 3 | Where-Object {$_.ExecutablePath -like 'C:\Program Files\RemoteAssist-Dev\*'}).Count){throw 'Existing product processes; refuse ownership'}
 @{tick=[Diagnostics.Stopwatch]::GetTimestamp();frequency=[Diagnostics.Stopwatch]::Frequency;utc=[DateTime]::UtcNow.ToString('o');deadlineSeconds=300}|ConvertTo-Json|Set-Content "$base/start.json" -Encoding UTF8
 $action=New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ('-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+$PSCommandPath+'" -Mode Watch -RunId '+$RunId)
 $principal=New-ScheduledTaskPrincipal -UserId SYSTEM -LogonType ServiceAccount -RunLevel Highest
 Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Settings (New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::FromMinutes(12)))|Out-Null
 Start-ScheduledTask -TaskName $task
 $wait=[Diagnostics.Stopwatch]::StartNew()
 while(!(Test-Path "$base/guard-ready.json")){if($wait.Elapsed.TotalSeconds-gt10){throw 'Guard not ready'};Start-Sleep -Milliseconds 100}
 Write-Output 'ARMED_LAUNCHER_NOT_STARTED';exit
}
if($Mode-eq'Watch'){
$start=Get-Content "$base/start.json" -Raw | ConvertFrom-Json
$deadline=[long]$start.tick+[long]$start.frequency*300
@{pid=$PID;deadline=$deadline}|ConvertTo-Json|Set-Content "$base/guard-ready.json" -Encoding UTF8
while([Diagnostics.Stopwatch]::GetTimestamp()-lt$deadline-and!(Test-Path "$base/stop")){Start-Sleep -Milliseconds 250}
# Isolate all CIM/registry/process cleanup calls behind one finite worker budget.
$args='-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+$PSCommandPath+'" -Mode Cleanup -RunId '+$RunId
$worker=Start-Process powershell.exe -ArgumentList $args -WindowStyle Hidden -PassThru
if(!$worker.WaitForExit(30000)){
 try{$worker.Kill()}catch{}
 @{utc=[DateTime]::UtcNow.ToString('o');complete=$false;reason='CleanupTimeout';requiresLocalRecovery=$true}|ConvertTo-Json|Set-Content "$base/cleanup-timeout.json" -Encoding UTF8
 exit 1
}
if($worker.ExitCode-ne0-or!(Test-Path "$base/cleanup.json")){throw 'Cleanup incomplete; local recovery required'}
exit
}
$errors=@()
try{
 if(!(Test-Path "$base/start.json")){throw 'Arm ownership evidence missing'}
 $svc=Get-CimInstance Win32_Service -Filter "Name='RemoteAssistHost'" -OperationTimeoutSec 3
 if($svc.PathName-ne('"'+$image+'"')){throw 'Service identity changed'}
 $p=Start-Process sc.exe -ArgumentList 'stop RemoteAssistHost' -WindowStyle Hidden -PassThru
 if(!$p.WaitForExit(5000)){$p.Kill()}
 foreach($process in Get-CimInstance Win32_Process -OperationTimeoutSec 3){
  $owned=$process.ExecutablePath -like 'C:\Program Files\RemoteAssist-Dev\*'
  # A shell is owned only when its exact PID/start time was recorded after observation.
  $testShell=$false
  if(Test-Path "$base/owned-processes.json"){
   foreach($entry in @(Get-Content "$base/owned-processes.json" -Raw | ConvertFrom-Json)){
    if($entry.pid-eq$process.ProcessId-and$entry.image-eq$process.ExecutablePath-and[DateTime]$entry.createdUtc-eq$process.CreationDate.ToUniversalTime()){$testShell=$true}
   }
  }
  if(($owned-or$testShell)-and$process.ProcessId-ne$PID){
   $live=Get-Process -Id $process.ProcessId -ErrorAction SilentlyContinue
   if($live-and$live.Path-eq$process.ExecutablePath-and$live.StartTime.ToUniversalTime()-eq$process.CreationDate.ToUniversalTime()){$live.Kill()}
  }
 }
 if(Test-Path $key){Remove-Item -LiteralPath $key -Recurse -Force}
}catch{$errors+=$_.Exception.Message}
$state=(Get-Service RemoteAssistHost).Status.ToString()
$keyAbsent=!(Test-Path $key)
@{utc=[DateTime]::UtcNow.ToString('o');keyAbsent=$keyAbsent;serviceState=$state;errors=$errors;desktopReturnVerified=$false}|ConvertTo-Json|Set-Content "$base/cleanup.json" -Encoding UTF8
if($errors.Count-or!$keyAbsent-or$state-ne'Stopped'){exit 1}
