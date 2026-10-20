# Reproduce the actual local owner's Save/Read heartbeat path without product,
# certificate, desktop or input changes. Independent processes exercise replace/read.
param([string]$Fixture,[switch]$Writer)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $repo 'scripts/e2e-run-contract.ps1')
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $repo 'scripts/e2e-local-session.ps1'),[ref]$null,[ref]$null)
foreach($name in @('Save','Read')){
 $fn=$ast.Find({param($n) $n-is[Management.Automation.Language.FunctionDefinitionAst]-and$n.Name-eq$name},$true)
 Invoke-Expression $fn.Extent.Text
}
$utf8=New-Object Text.UTF8Encoding($false)
if(!$Fixture){$Fixture=Join-Path $repo ('.artifacts/local-heartbeat-'+[guid]::NewGuid().ToString('N'));New-Item -ItemType Directory -Path $Fixture|Out-Null}
$RunDir=$Fixture
$limit=[Diagnostics.Stopwatch]::StartNew()
if($Writer){
 try{
  while($limit.Elapsed.TotalSeconds-lt12){Save 'heartbeat.json' @{id='fixture';tick=[Diagnostics.Stopwatch]::GetTimestamp()};Start-Sleep -Milliseconds 5}
 }catch{Save 'writer-error.json' @{message=$_.Exception.Message;line=$_.InvocationInfo.ScriptLineNumber;stack=$_.ScriptStackTrace};exit 1}
 exit 0
}
Save 'heartbeat.json' @{id='fixture';tick=[Diagnostics.Stopwatch]::GetTimestamp()}
$shell=Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
$child=Start-Process $shell -ArgumentList ('-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+$PSCommandPath+'" -Fixture "'+$Fixture+'" -Writer') -WindowStyle Hidden -PassThru
$reads=0;$failure=$null
try{
 while(!$child.HasExited){
  if($limit.Elapsed.TotalSeconds-gt20){throw 'Input-free heartbeat deadline exceeded'}
  $beat=Read 'heartbeat.json'
  if($beat.id-ne'fixture'-or$beat.tick-le0){throw 'Incomplete heartbeat'}
  $reads++
 }
 $child.Refresh();if($child.ExitCode-ne0){throw 'Writer failed'}
}catch{$failure=@{type=$_.Exception.GetType().Name;message=$_.Exception.Message;line=$_.InvocationInfo.ScriptLineNumber;stack=$_.ScriptStackTrace}}
finally{if(!$child.HasExited){$child.Kill();$null=$child.WaitForExit(1000)}}
$result=@{state=$(if($failure){'FAIL'}else{'PASS_INPUT_FREE_HEARTBEAT'});reads=$reads;failure=$failure;fixture=$Fixture}
$result|ConvertTo-Json -Depth 6|Set-Content -Encoding UTF8 (Join-Path $Fixture 'result.json')
$result|ConvertTo-Json -Depth 6
if($failure){exit 1}
