$ErrorActionPreference='Stop'
$owner=Get-Content (Join-Path $Staging 'product-owner.json') -Raw -Encoding UTF8|ConvertFrom-Json
if($owner.id-ne'842dd89df2104c0ba7a2aa99cc9fb3e0'-or$owner.root-ne(Join-Path $env:ProgramData ('RemoteAssist-E2E-Product-'+$owner.id))){throw 'Frozen campaign mismatch'}
$root=$owner.root
if(Test-Path (Join-Path $root 'product-start.json')){throw 'Replay forbidden'}
if((Get-Service RemoteAssistHost -ErrorAction SilentlyContinue)-or(Test-Path "$env:ProgramFiles\RemoteAssist-Dev")-or(Test-Path "$env:ProgramData\RemoteAssist")){throw 'Product environment changed'}
$manifest=@'
[{"path": "scripts/e2e-native.cs", "sha256": "0398cabca8557fda9dde3babf60a58b3c90fbb9da8d17dd40b08abc9616689c9"}, {"path": "scripts/e2e-product-guard.ps1", "sha256": "3b82dd2cb27d456cc8feaad910bcc018918a09dabf0d05606c570a2c0475dac0"}, {"path": "scripts/e2e-console-observer.ps1", "sha256": "406a161ef20d4ddc6953da6b8276ca65acb07d1ddbb1d00db6b3fad1e0f81879"}, {"path": "scripts/install-host-dev.ps1", "sha256": "1db318e3353008113be1895a590a97f2c3a8d3cda0d4bb36aa2a2c4e77ec523f"}]
'@|ConvertFrom-Json
foreach($entry in $manifest){
 if($entry.path-notmatch'^scripts/[A-Za-z0-9.-]+$'){throw 'Tool path mismatch'}
 if((Get-FileHash (Join-Path $root $entry.path)).Hash-ne$entry.sha256){throw 'Protected tool changed'}
}
$planPath=Join-Path $root 'product-plan.json'
$plan=Get-Content $planPath -Raw -Encoding UTF8|ConvertFrom-Json
if($plan.review-ne'PENDING'-or$plan.id-ne$owner.id){throw 'Frozen plan state mismatch'}
$plan.review='ACCEPTED'
$plan|ConvertTo-Json|Set-Content $planPath -Encoding UTF8
Start-ScheduledTask -TaskName $owner.task
Write-Output 'PRODUCT_CAMPAIGN_STARTED'
