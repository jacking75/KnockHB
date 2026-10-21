$ErrorActionPreference='Stop'
if($Staging-notmatch'^C:/Users/jacking75NoteBook/RemoteAssist-E2E-[0-9]+$'-or!$CampaignManifest){throw 'Explicit frozen staging required'}
$archive=Assert-E2ePath (Join-Path $Staging 'prepared.zip')
if((Get-FileHash -LiteralPath $archive).Hash-ne$CampaignManifest.archiveSha256){throw 'Product harness archive mismatch'}
$source=Join-Path $Staging 'tools'
# Deployment is separately verified. Do not overwrite it, register tasks or start the product.
Assert-E2eTools $CampaignManifest $source
$package=Join-Path $source 'candidate'
if((Get-FileHash -LiteralPath (Join-Path $package 'candidate.json')).Hash-ne$CampaignManifest.candidateSha256){throw 'Prepared candidate mismatch'}
$certificate=Assert-E2ePath (Join-Path $source 'lan-test.cer')
if((Get-FileHash -LiteralPath $certificate).Hash-ne$CampaignManifest.tls.sha256){throw 'Frozen TLS changed'}
if((Test-Path "$env:ProgramFiles\RemoteAssist-Dev")-or(Get-Service RemoteAssistHost -ErrorAction SilentlyContinue)){throw 'Clean product environment required'}
# Preserve the ended campaign's audit/state, with both resolved move roots checked first.
$data=[IO.Path]::GetFullPath((Join-Path $env:ProgramData 'RemoteAssist'))
if(Test-Path -LiteralPath $data){
 $previous=Read-E2eJson (Join-Path $data 'dev-owner.json')
 if($previous.id-notmatch'^[a-f0-9]{32}$'-or$previous.data-ne$data-or$previous.root-ne(Join-Path $env:ProgramFiles 'RemoteAssist-Dev')){throw 'Previous state owner mismatch'}
 $previousRoot=Assert-E2ePath (Join-Path $env:ProgramData ('RemoteAssist-E2E-Product-'+$previous.id))
 $target=[IO.Path]::GetFullPath((Join-Path $previousRoot ('preserved-before-'+$CampaignManifest.id)))
 if($data-ne(Join-Path $env:ProgramData 'RemoteAssist')-or!$target.StartsWith($previousRoot+'\',[StringComparison]::OrdinalIgnoreCase)-or(Test-Path -LiteralPath $target)){throw 'Unsafe previous-state move'}
 $null=Assert-E2ePath $data
 foreach($item in Get-ChildItem -LiteralPath $data -Recurse -Force){if($item.Attributes-band[IO.FileAttributes]::ReparsePoint){throw 'Previous state reparse refused'}}
 Write-E2eNewJson (Join-Path $Staging 'preserved-state.intent.json') @{previousId=$previous.id;source=$data;destination=$target}
 Move-Item -LiteralPath $data -Destination $target
 Write-E2eNewJson (Join-Path $Staging 'preserved-state.json') @{previousId=$previous.id;source=$data;destination=$target}
}
$id=$CampaignManifest.id
$runRoot=Join-Path $env:ProgramData ('RemoteAssist-E2E-Product-'+$id)
if((Test-Path -LiteralPath $runRoot)-or(Test-Path (Join-Path $Staging 'product-owner.json'))){throw 'Prepared campaign replay forbidden'}
New-Item -ItemType Directory -Path $runRoot|Out-Null
$acl=New-Object Security.AccessControl.DirectorySecurity
$acl.SetAccessRuleProtection($true,$false);$acl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))
foreach($sid in @('S-1-5-18','S-1-5-32-544')){$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($sid)),'FullControl','ContainerInherit,ObjectInherit','None','Allow')))}
$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-545')),'ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow')))
Set-Acl -LiteralPath $runRoot -AclObject $acl
Copy-Item -LiteralPath (Join-Path $source 'scripts') -Destination $runRoot -Recurse
if($CampaignManifest.version-eq4){
 $trace=Join-Path $runRoot 'trace'
 New-Item -ItemType Directory -Path $trace|Out-Null
 $traceAcl=Get-Acl -LiteralPath $trace
 $traceAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($CampaignManifest.identity.b.sid)),'Modify','ContainerInherit,ObjectInherit','None','Allow')))
 Set-Acl -LiteralPath $trace -AclObject $traceAcl
 foreach($tool in @('desktop-tool','input-target')){
  $toolSource=Join-Path $source $tool
  $entries=$CampaignManifest.testTools.$tool
  foreach($entry in $entries){
   if($entry.path-notmatch'^[A-Za-z0-9_./-]+$'-or$entry.path.Contains('..')){throw 'Unsafe test tool path'}
   $file=Assert-E2ePath (Join-Path $toolSource $entry.path)
   if((Get-FileHash -LiteralPath $file).Hash-ne$entry.sha256-or(Get-Item -LiteralPath $file).Length-ne$entry.size){throw 'Test tool changed'}
  }
  if(@(Get-ChildItem -LiteralPath $toolSource -Recurse -File).Count-ne@($entries).Count){throw 'Unlisted test tool file'}
  Copy-Item -LiteralPath $toolSource -Destination $runRoot -Recurse
 }
}
Copy-Item -LiteralPath $certificate -Destination $runRoot
Copy-Item -LiteralPath (Join-Path $Staging 'campaign.json') -Destination $runRoot
$out=Join-Path $Staging 'observer';New-Item -ItemType Directory -Path $out|Out-Null
if($CampaignManifest.version-eq4){New-Item -ItemType Directory -Path (Join-Path $out 'receive')|Out-Null}
$owner=@{id=$id;root=$runRoot;task=('RemoteAssist-E2E-Product-'+$id);watchdog=('RemoteAssist-E2E-Watchdog-'+$id);observer=('RemoteAssist-E2E-Observe-'+$id);output=$out;manifestSha256=$CampaignManifestSha256}
Write-E2eNewJson (Join-Path $Staging 'product-owner.json') $owner
$shell=Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
$settings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Seconds 900) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
$principal=New-ScheduledTaskPrincipal -UserId SYSTEM -LogonType ServiceAccount -RunLevel Highest
foreach($role in @('Controller','Watchdog')){
 $name=if($role-eq'Controller'){$owner.task}else{$owner.watchdog}
 if(Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue){throw 'Owned task already exists'}
 $arguments='-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+(Join-Path $runRoot 'scripts/e2e-product-guard.ps1')+'" -RunDir "'+$runRoot+'" -ManifestSha256 '+$CampaignManifestSha256+' -Role '+$role
 Register-ScheduledTask -TaskName $name -Action (New-ScheduledTaskAction -Execute $shell -Argument $arguments) -Principal $principal -Settings $settings|Out-Null
}
@{state='PREPARED_NOT_AUTHORIZED';id=$id;manifestSha256=$CampaignManifestSha256;tasksRegistered=$true;productStarted=$false}|ConvertTo-Json
