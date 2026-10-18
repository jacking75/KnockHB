$ErrorActionPreference='Stop'
$root='C:\ProgramData\RemoteAssist-E2E-Guard-088e2a10203e420bb268a28f0dff44a5'
$run=Join-Path $root '.artifacts/scm-fixture-ee053862685448e58535caf499123fc1/watchdog'
$service='RAE2E-395f7ae8dba54f848eeee1bf8a40eeb3'
$image=Join-Path $run 'ScmFixture.exe'
$registry='HKLM:\SYSTEM\CurrentControlSet\Services\'+$service
if((Get-ItemProperty $registry).ImagePath-ne('"'+$image+'" '+$service)){throw 'Recovery owner mismatch'}
Add-Type -Path (Join-Path $root 'scripts/e2e-native.cs')
$deadline=[E2eOwnedCommand]::Deadline(25000)
$sc=Join-Path $env:SystemRoot 'System32/sc.exe'
$stop=[E2eOwnedCommand]::Start($sc,@('stop',$service))
try{$exit=$stop.Wait($deadline,10000)}finally{$stop.Dispose()}
$s=Get-Service $service
$until=[E2eOwnedCommand]::Deadline(5000)
while($s.Status-ne'Stopped'-and[E2eOwnedCommand]::Remaining($until)-gt0){Start-Sleep -Milliseconds 50;$s.Refresh()}
if($s.Status-ne'Stopped'){
 # This fallback is restricted to the inert fixture, never a product/Recovery process.
 if((Get-FileHash $image).Hash-ne'DD5D5179F45026CB6576D83EC56885E283044783994EB375AE3F61891F39F3D3'){throw 'Fixture binary changed'}
 $entry=Get-CimInstance Win32_Service -Filter ("Name='"+$service+"'") -OperationTimeoutSec 5
 $process=Get-Process -Id $entry.ProcessId -ErrorAction Stop
 if($process.Path-ne$image){throw 'SCM fixture PID image mismatch'}
 $process.Kill();if(!$process.WaitForExit(3000)){throw 'Inert fixture could not terminate'}
 $s.Refresh();if($s.Status-ne'Stopped'){throw 'SCM did not observe fixture termination'}
}
$s.Dispose()
$delete=[E2eOwnedCommand]::Start($sc,@('delete',$service))
try{$exitDelete=$delete.Wait($deadline,10000)}finally{$delete.Dispose()}
@{stopExit=$exit;deleteExit=$exitDelete;serviceAbsent=!(Test-Path $registry);processes=@(Get-Process ScmFixture -ErrorAction SilentlyContinue|Where-Object {$_.Path-eq$image}|Select-Object Id)}|ConvertTo-Json
