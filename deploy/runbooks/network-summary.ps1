param([ValidateSet('summary','counts')][string]$detail='summary')
$ErrorActionPreference='Stop'
try {
    $adapters=@(Get-NetAdapter)
    Write-Output ('adapter-count='+$adapters.Count)
    if($detail -eq 'counts'){Write-Output ('up-count='+@($adapters|Where-Object Status -eq 'Up').Count)}
    exit 0
} catch { Write-Output 'provider-unavailable'; exit 1 }
