$ErrorActionPreference='Stop'
$script=Join-Path $env:USERPROFILE 'RemoteAssist-C01-Readiness-20261022-2158.ps1'
if((Get-FileHash -LiteralPath $script).Hash-ne'D8E3808D73E63E94667A0C1FCD74F078BB655B9D6537A2BB71173AFFC53A07D4'){throw 'Readiness script changed'}
& $script
