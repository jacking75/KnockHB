$ErrorActionPreference='Stop'
$root='C:\ProgramData\RemoteAssist'
$roots=@($root,"$env:LOCALAPPDATA\RemoteAssist")
$logs=@($roots|Where-Object {Test-Path $_}|ForEach-Object {Get-ChildItem $_ -Recurse -File}|Where-Object {$_.Extension-in@('.log','.jsonl','.txt','.ndjson','.json')-and$_.Name-ne'dev-owner.json'})
@{files=@($logs|Select-Object FullName,Length);logs=@($logs|ForEach-Object {@{path=$_.FullName;tail=@(Get-Content -LiteralPath $_.FullName -Tail 50 -Encoding UTF8)}});events=@(Get-WinEvent -FilterHashtable @{LogName='Application';StartTime=(Get-Date).AddMinutes(-10)} -MaxEvents 20 -ErrorAction SilentlyContinue | Where-Object {$_.Message-like'*RemoteAssist*'} | Select-Object TimeCreated,ProviderName,Id,Message)}|ConvertTo-Json -Depth 8
