$ErrorActionPreference='Stop'
$owner=Get-Content (Join-Path $Staging 'independent-owner.json') -Raw -Encoding UTF8|ConvertFrom-Json
if($owner.root-ne(Join-Path $env:ProgramData ('RemoteAssist-E2E-Guard-'+$owner.id))){throw 'Owner root mismatch'}
$files=@(Get-ChildItem -LiteralPath $owner.root -Recurse -File|Where-Object {$_.Extension-in@('.json','.txt')}|Select-Object FullName,Length)
$proof=@(Get-ChildItem -LiteralPath $owner.root -Recurse -File -Filter '*.json'|Where-Object {$_.Length-lt10000}|ForEach-Object {@{name=$_.FullName;value=(Get-Content $_.FullName -Raw -Encoding UTF8|ConvertFrom-Json)}})
@{files=$files;proof=$proof;taskPresent=[bool](Get-ScheduledTask -TaskName $owner.task -ErrorAction SilentlyContinue);services=@(Get-Service -Name 'RAE2E-*' -ErrorAction SilentlyContinue|Select-Object Name,Status)}|ConvertTo-Json -Depth 8
