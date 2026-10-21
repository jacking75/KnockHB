$ErrorActionPreference='Stop'
$owner=Get-E2eOwner
$authorization=Read-E2eJson (Join-Path $Staging 'execution-authorization.json')
if($authorization.id-ne$owner.id-or$authorization.manifestSha256-ne$CampaignManifestSha256-or$authorization.scope-ne$CampaignManifest.profile-or$authorization.requestedBy-ne'user-after-readiness-report'){throw 'Separate execution instruction required'}
if($RemoteDeadlineTick-le0-or$RemoteActiveEndTick-le0){throw 'Existing A/B monotonic grant required'}
if(Test-Path (Join-Path $owner.root 'product-start.json')){throw 'Replay forbidden'}
if((Get-Service RemoteAssistHost -ErrorAction SilentlyContinue)-or(Test-Path "$env:ProgramFiles/RemoteAssist-Dev")-or(Test-Path "$env:ProgramData/RemoteAssist")){throw 'Product environment changed'}
$plan=@{id=$owner.id;profile=$CampaignManifest.profile;manifestSha256=$CampaignManifestSha256;review='ACCEPTED';candidateSha256=$CampaignManifest.candidateSha256;package=(Join-Path $Staging 'tools/candidate');remoteDeadlineTick=$RemoteDeadlineTick;remoteActiveEndTick=$RemoteActiveEndTick;watchdogTask=$owner.watchdog}
Write-E2eNewJson (Join-Path $owner.root 'product-plan.json') $plan
# Exact action/root and SYSTEM identity are verified before starting the one owned task.
$task=Get-ScheduledTask -TaskName $owner.task
if($task.Principal.UserId-notin@('SYSTEM','S-1-5-18')-or$task.Actions.Count-ne1-or$task.Actions[0].Arguments-notlike('*"'+$owner.root+'"*')){throw 'Product task ownership changed'}
Start-ScheduledTask -TaskName $owner.task
Write-Output 'PRODUCT_CAMPAIGN_START_REQUESTED'
