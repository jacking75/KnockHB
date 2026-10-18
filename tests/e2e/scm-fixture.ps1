$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output=Join-Path $repo ('.artifacts/scm-fixture-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output -Force|Out-Null
$results=@()
foreach($fault in @('none','controller','watchdog','heartbeat')){
 $run=Join-Path $output $fault;New-Item -ItemType Directory -Path $run|Out-Null
 Copy-Item (Join-Path $repo 'ScmFixture.exe') -Destination $run
 @{id=[guid]::NewGuid().ToString('N');mode='scm-fixture';fault=$fault;imageSha256=(Get-FileHash (Join-Path $run 'ScmFixture.exe')).Hash}|ConvertTo-Json|Set-Content (Join-Path $run 'service-plan.json') -Encoding UTF8
 $shell=Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
 $args='-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+(Join-Path $repo 'scripts/e2e-service-guard.ps1')+'" -RunDir "'+$run+'"'
 $timer=[Diagnostics.Stopwatch]::StartNew()
 $controller=Start-Process $shell -ArgumentList $args -WindowStyle Hidden -PassThru
 while(!(Test-Path (Join-Path $run 'service-cleanup.json'))-and$timer.Elapsed.TotalSeconds-lt65){
  if(Test-Path (Join-Path $run 'service-recovery-required.json')){throw ('SCM recovery required: '+$run)}
  Start-Sleep -Milliseconds 50
 }
 if(!(Test-Path (Join-Path $run 'service-cleanup.json'))){throw ('SCM cleanup missing: '+$run)}
 if(Test-Path (Join-Path $run 'service-error.json')){throw ('SCM controller failed: '+$run)}
 if(!(Test-Path (Join-Path $run 'service-running.json'))){throw 'SCM service was not started'}
 if(!$controller.WaitForExit(3000)){throw 'SCM controller remains'}
 $results+=@{fault=$fault;status='PASS';seconds=$timer.Elapsed.TotalSeconds}
}
$results|ConvertTo-Json|Set-Content (Join-Path $output 'results.json') -Encoding UTF8
$results|ConvertTo-Json
