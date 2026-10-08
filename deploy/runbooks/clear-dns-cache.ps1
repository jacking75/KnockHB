param([ValidateSet('summary','counts')][string]$detail='summary')
$ErrorActionPreference='Stop'
try { Clear-DnsClientCache; Write-Output 'dns-cache-cleared'; exit 0 }
catch { Write-Output 'dns-cache-clear-failed'; exit 1 }
