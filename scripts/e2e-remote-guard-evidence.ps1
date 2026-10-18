$ErrorActionPreference='Stop'
if($Staging-notmatch'^C:/Users/jacking75NoteBook/RemoteAssist-E2E-[0-9]+$'){throw 'Unexpected staging root'}
$root=Join-Path $Staging 'guards/.artifacts'
$evidence=@(Get-ChildItem -LiteralPath $root -Directory -Filter 'campaign-fixture-*'|ForEach-Object {
    Get-ChildItem -LiteralPath $_.FullName -Recurse -File -Filter '*.json'|ForEach-Object {@{file=$_.FullName;value=(Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8|ConvertFrom-Json)}}
})
@{evidence=$evidence;productServicePresent=[bool](Get-Service RemoteAssistHost -ErrorAction SilentlyContinue);fixtureProcesses=@(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -OperationTimeoutSec 5|Where-Object {$_.CommandLine-like('*'+$Staging+'*')}|Select-Object ProcessId,ParentProcessId,CommandLine)}|ConvertTo-Json -Depth 9
