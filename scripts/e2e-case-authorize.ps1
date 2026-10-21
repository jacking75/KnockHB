$ErrorActionPreference='Stop'
$owner=Get-E2eOwner
$authorization=Read-E2eJson (Join-Path $Staging 'execution-authorization.json')
if($authorization.id-ne$owner.id-or$authorization.manifestSha256-ne$CampaignManifestSha256-or$authorization.scope-ne$CampaignManifest.profile-or$authorization.requestedBy-ne'user-after-readiness-report'){throw 'Case execution authorization mismatch'}
if(Test-Path (Join-Path $owner.root 'product-start.json')){throw 'Started case cannot be authorized again'}
@{status='completed';id=$owner.id}|ConvertTo-Json -Compress
