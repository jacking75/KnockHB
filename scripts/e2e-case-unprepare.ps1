$ErrorActionPreference='Stop'
function Restore-PreparedData {
 $record=Join-Path $Staging 'preserved-state.json'
 if(!(Test-Path -LiteralPath $record)){$record=Join-Path $Staging 'preserved-state.intent.json'}
 if(!(Test-Path -LiteralPath $record)){return}
 if(Get-Service RemoteAssistHost -ErrorAction SilentlyContinue){throw 'FAIL_RECOVERY_REQUIRED: product service present; previous data retained'}
 $state=Read-E2eJson $record
 if($state.previousId-notmatch'^[a-f0-9]{32}$'){throw 'Previous data owner missing'}
 $source=[IO.Path]::GetFullPath((Join-Path $env:ProgramData 'RemoteAssist'))
 $previous=[IO.Path]::GetFullPath((Join-Path $env:ProgramData ('RemoteAssist-E2E-Product-'+$state.previousId)))
 $target=[IO.Path]::GetFullPath((Join-Path $previous ('preserved-before-'+$CampaignManifest.id)))
 if($state.source-ne$source-or$state.destination-ne$target-or!$target.StartsWith($previous+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe previous data restore path'}
 $existing=if(Test-Path -LiteralPath $source){$source}else{$target}
 $null=Assert-E2ePath $existing
 $acl=Get-Acl -LiteralPath $existing
 if($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value-notin@('S-1-5-18','S-1-5-32-544')){throw 'Previous data protection changed'}
 foreach($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])){
  if($rule.AccessControlType-eq'Allow'-and$rule.IdentityReference.Value-notin@('S-1-5-18','S-1-5-32-544')){throw 'Previous data ACL changed'}
 }
 $marker=Read-E2eJson (Join-Path $existing 'dev-owner.json')
 if($marker.id-ne$state.previousId-or$marker.data-ne$source-or$marker.root-ne(Join-Path $env:ProgramFiles 'RemoteAssist-Dev')){throw 'Previous data identity changed'}
 if($existing-eq$source){if(Test-Path -LiteralPath $target){throw 'Previous data restore conflict'};return}
 foreach($item in Get-ChildItem -LiteralPath $target -Recurse -Force){if($item.Attributes-band[IO.FileAttributes]::ReparsePoint){throw 'Previous data restore reparse refused'}}
 # Both resolved absolute move roots were checked above, in this same shell.
 Move-Item -LiteralPath $target -Destination $source
}
$ownerPath=Join-Path $Staging 'product-owner.json'
if(!(Test-Path -LiteralPath $ownerPath)){
 if(!$CampaignManifest-or$CampaignManifest.id-notmatch'^[a-f0-9]{32}$'-or$CampaignManifest.staging-ne$Staging){throw 'Partial preparation manifest missing'}
 $id=$CampaignManifest.id
 $root=Join-Path $env:ProgramData ('RemoteAssist-E2E-Product-'+$id)
 foreach($prefix in @('RemoteAssist-E2E-Product-','RemoteAssist-E2E-Watchdog-','RemoteAssist-E2E-Observe-','RemoteAssist-E2E-Fault-')){
  if(Get-ScheduledTask -TaskName ($prefix+$id) -ErrorAction SilentlyContinue){throw 'FAIL_RECOVERY_REQUIRED: task exists without owner; retained'}
 }
 if(Test-Path -LiteralPath (Join-Path $root 'product-start.json')){throw 'FAIL_RECOVERY_REQUIRED: runtime without preparation owner'}
 Restore-PreparedData
 @{status='completed';id=$id;preparedTasksRetired=$true;productStarted=$false;ownerAbsent=$true;filesRetainedForReview=$true}|ConvertTo-Json -Compress
 return
}
$owner=Get-E2eOwner
if(Test-Path (Join-Path $owner.root 'product-start.json')){throw 'Started campaign requires normal recovery'}
foreach($name in @($owner.task,$owner.watchdog)){
 $task=Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
 if(!$task){continue}
 $role=if($name-eq$owner.task){'Controller'}else{'Watchdog'}
 $shell=Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
 $arguments='-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+(Join-Path $owner.root 'scripts/e2e-product-guard.ps1')+'" -RunDir "'+$owner.root+'" -ManifestSha256 '+$owner.manifestSha256+' -Role '+$role
 if($task.State-eq'Running'-or$task.Principal.UserId-notin@('SYSTEM','S-1-5-18')-or$task.Actions.Count-ne1-or
    $task.Actions[0].Execute-ne$shell-or$task.Actions[0].Arguments-ne$arguments){throw 'Prepared task ownership/state changed'}
 Unregister-ScheduledTask -TaskName $name -Confirm:$false
}
Restore-PreparedData
@{status='completed';id=$owner.id;preparedTasksRetired=$true;productStarted=$false;filesRetainedForReview=$true}|ConvertTo-Json -Compress
