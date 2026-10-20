$ErrorActionPreference='Stop'
$owner=Get-E2eOwner
$runRoot=$owner.root
$started=Test-Path (Join-Path $runRoot 'product-start.json')
# Collection does not retire a watchdog, even when a cleanup file claims CLEAN.
Add-Type -Path (Join-Path $runRoot 'scripts/e2e-native.cs')
$collectEnd=[E2eOwnedCommand]::Deadline(20000)
if($started){
 $start=Read-E2eJson (Join-Path $runRoot 'product-start.json')
 if($start.id-ne$owner.id-or$start.deadline-le0){throw 'Campaign clock mismatch'}
 $collectEnd=[Math]::Min($collectEnd,[long]$start.deadline)
}
$path=Join-Path $runRoot ('resource-inspect-'+[guid]::NewGuid().ToString('N')+'.json')
$command=[E2eOwnedCommand]::Start((Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'),@('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',(Join-Path $runRoot 'scripts/e2e-resource-inspect.ps1'),'-Output',$path,'-Thumbprint',$CampaignManifest.tls.thumbprint))
try{if($command.Wait($collectEnd,10000)-ne0){throw 'Resource observation failed'}}finally{$command.Dispose()}
$actual=Read-E2eJson $path
$cleanup=$null;if(Test-Path (Join-Path $runRoot 'product-cleanup.json')){$cleanup=Read-E2eJson (Join-Path $runRoot 'product-cleanup.json')}
$trustOwned=$false
if(Test-Path (Join-Path $runRoot 'tls-owner.json')){
 $trust=Read-E2eJson (Join-Path $runRoot 'tls-owner.json')
 if($trust.thumb-ne$CampaignManifest.tls.thumbprint){throw 'Trust owner changed'}
 $trustOwned=[bool]$trust.added
}
$resourcesAbsent=$actual.serviceAbsent-and$actual.productDirectoryAbsent-and!@($actual.ownedProcesses).Count-and(!$trustOwned-or!$actual.trustPresent)
@{id=$owner.id;productStarted=$started;resourcesAbsent=[bool]$resourcesAbsent;actual=$actual;cleanup=$cleanup;watchdogsRetired=$false;recoveryVerified=$false;state='COLLECTED_NOT_RECOVERY_PASS';protectedEvidence=$runRoot}|ConvertTo-Json -Depth 8
