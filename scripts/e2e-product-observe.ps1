$ErrorActionPreference='Stop'
$owner=Get-Content (Join-Path $Staging 'product-owner.json') -Raw -Encoding UTF8|ConvertFrom-Json
if($owner.root-ne(Join-Path $env:ProgramData ('RemoteAssist-E2E-Product-'+$owner.id))){throw 'Owner mismatch'}
if(!(Test-Path (Join-Path $owner.root 'product-running.json'))){throw 'Product not running; do not launch observer'}
Start-ScheduledTask -TaskName $owner.observer
Write-Output 'OBSERVING_ONLY_NO_CONSENT_AUTOMATION'
