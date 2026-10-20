$ErrorActionPreference='Stop'
$owner=Get-E2eOwner
@{reason='requested-stop';utc=[DateTime]::UtcNow.ToString('o')}|ConvertTo-Json|Set-Content (Join-Path $owner.root 'finish.json') -Encoding UTF8
Write-Output 'STOP_REQUESTED'
