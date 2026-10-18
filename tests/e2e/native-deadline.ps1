$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Add-Type -Path (Join-Path $root 'scripts/e2e-native.cs')
$shell=Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
$deadline=[E2eOwnedCommand]::Deadline(10000)
$command=[E2eOwnedCommand]::Start($shell,@('-NoProfile','-NonInteractive','-Command','exit 7'))
try{if($command.Wait($deadline,5000)-ne7){throw 'Exit code lost'}}finally{$command.Dispose()}
$timer=[Diagnostics.Stopwatch]::StartNew()
$command=[E2eOwnedCommand]::Start($shell,@('-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 60'))
$ownedPid=$command.Pid
try{try{$null=$command.Wait([E2eOwnedCommand]::Deadline(300),10000);throw 'Hang accepted'}catch [TimeoutException] {}}finally{$command.Dispose()}
if($timer.Elapsed.TotalSeconds-gt5){throw 'Global deadline extended'}
$check=[Diagnostics.Stopwatch]::StartNew()
while((Get-Process -Id $ownedPid -ErrorAction SilentlyContinue)-and$check.Elapsed.TotalSeconds-lt2){Start-Sleep -Milliseconds 20}
if(Get-Process -Id $ownedPid -ErrorAction SilentlyContinue){throw 'Owned child survived Job close'}
# Closing the owner handle must stop the child even without waiting for its deadline.
$command=[E2eOwnedCommand]::Start($shell,@('-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 60'))
$ownedPid=$command.Pid;$command.Dispose()
$check.Restart()
while((Get-Process -Id $ownedPid -ErrorAction SilentlyContinue)-and$check.Elapsed.TotalSeconds-lt2){Start-Sleep -Milliseconds 20}
if(Get-Process -Id $ownedPid -ErrorAction SilentlyContinue){throw 'Owner close left a child'}
$fixtureRoot=Join-Path ([IO.Path]::GetTempPath()) ('ra-job-fixture-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixtureRoot|Out-Null
$fixture=Join-Path $fixtureRoot 'fixture.ps1'
$childFile=Join-Path $fixtureRoot 'child.txt'
@'
param([string]$Output)
$shell=Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
$child=Start-Process -FilePath $shell -ArgumentList '-NoProfile -NonInteractive -Command "Start-Sleep -Seconds 60"' -WindowStyle Hidden -PassThru
[IO.File]::WriteAllText($Output,[string]$child.Id)
Start-Sleep -Seconds 60
'@ | Set-Content -LiteralPath $fixture -Encoding UTF8
$command=$null
try{
    $command=[E2eOwnedCommand]::Start($shell,@('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$fixture,'-Output',$childFile))
    $check.Restart()
    while(!(Test-Path $childFile)-and$check.Elapsed.TotalSeconds-lt5){Start-Sleep -Milliseconds 20}
    if(!(Test-Path $childFile)){throw 'Descendant fixture readiness missing'}
    $descendantId=[int]([IO.File]::ReadAllText($childFile))
    $descendant=Get-Process -Id $descendantId -ErrorAction Stop
    $parentId=$command.Pid;$command.Dispose();$check.Restart()
    while((!$descendant.HasExited-or(Get-Process -Id $parentId -ErrorAction SilentlyContinue))-and$check.Elapsed.TotalSeconds-lt2){Start-Sleep -Milliseconds 20;$descendant.Refresh()}
    if(!$descendant.HasExited-or(Get-Process -Id $parentId -ErrorAction SilentlyContinue)){throw 'Job close left parent/descendant'}
    $descendant.Dispose()
}finally{
    if($command){$command.Dispose()}
    foreach($file in @($fixture,$childFile)){if(Test-Path -LiteralPath $file){Remove-Item -LiteralPath $file -Force}}
    Remove-Item -LiteralPath $fixtureRoot
}
'PASS: native exit code, global timeout, Job cleanup, owner handle close, descendant cleanup. No product/service/input executed.'
