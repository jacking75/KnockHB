param([string]$RunId,[string]$Output,[string]$ScmFixtureDirectory)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $repo 'scripts/e2e-run-contract.ps1')
$output=if($Output){[IO.Path]::GetFullPath($Output)}else{Join-Path $repo ('.artifacts/scm-fixture-'+[guid]::NewGuid().ToString('N'))}
if(!$output.StartsWith((Join-Path $repo '.artifacts')+'\',[StringComparison]::OrdinalIgnoreCase)-or(Test-Path -LiteralPath $output)){throw 'New workspace fixture output required'}
if($RunId-and$RunId-notmatch'^[a-f0-9]{32}$'){throw 'Fixture run ID required'}
$null=Assert-E2ePath (Split-Path $output -Parent)
New-Item -ItemType Directory -Path $output|Out-Null
$results=@()
foreach($fault in $(if($RunId){@('controller')}else{@('none','controller','watchdog','heartbeat')})){
 $run=Join-Path $output $fault;New-Item -ItemType Directory -Path $run|Out-Null
 if(!$ScmFixtureDirectory){throw 'Frozen .NET 10 scm-fixture directory required'}
 $null=Assert-E2ePath $ScmFixtureDirectory
 $fixtureRoot=[IO.Path]::GetFullPath($ScmFixtureDirectory).TrimEnd('\')
 $fixtureFiles=@(Get-ChildItem -LiteralPath $fixtureRoot -File -Recurse|ForEach-Object {
  $null=Assert-E2ePath $_.FullName
  $relative=$_.FullName.Substring($fixtureRoot.Length+1)
  $destination=[IO.Path]::GetFullPath((Join-Path $run $relative))
  if(!$destination.StartsWith([IO.Path]::GetFullPath($run)+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Fixture dependency path escape'}
  New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force|Out-Null
  Copy-Item -LiteralPath $_.FullName -Destination $destination
  @{name=$relative.Replace('\','/');sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
 })
 @{id=$(if($RunId){$RunId}else{[guid]::NewGuid().ToString('N')});mode='scm-fixture';fault=$fault;fixtureFiles=$fixtureFiles;imageSha256=(Get-FileHash (Join-Path $run 'ScmFixture.exe')).Hash}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $run 'service-plan.json') -Encoding UTF8
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
