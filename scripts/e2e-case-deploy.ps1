$ErrorActionPreference='Stop'
if($Staging-notmatch'^C:/Users/jacking75NoteBook/RemoteAssist-E2E-[0-9]+$'){throw 'New staging required'}
$manifestPath=Join-Path $Staging 'campaign.json'
$m=Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8|ConvertFrom-Json
if($m.version-ne4-or$m.staging-ne$Staging-or$m.id-notmatch'^[a-f0-9]{32}$'){throw 'Campaign staging mismatch'}
$archive=Join-Path $Staging 'prepared.zip'
if((Get-FileHash -LiteralPath $archive).Hash-ne$m.archiveSha256){throw 'Archive hash mismatch'}
$destination=Join-Path $Staging 'tools'
if(Test-Path -LiteralPath $destination){throw 'Deployment replay refused'}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=[IO.Compression.ZipFile]::OpenRead($archive)
try{
 $seen=New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
 foreach($entry in $zip.Entries){
  if($entry.FullName-notmatch'^[A-Za-z0-9_./-]+$'-or$entry.FullName.Contains('..')-or!$seen.Add($entry.FullName)){throw 'Unsafe archive entry'}
  $target=[IO.Path]::GetFullPath((Join-Path $destination $entry.FullName))
  if(!$target.StartsWith([IO.Path]::GetFullPath($destination)+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Archive path escape'}
 }
}finally{$zip.Dispose()}
[IO.Compression.ZipFile]::ExtractToDirectory($archive,$destination)
$contract=@($m.tools|Where-Object {$_.path-eq'scripts/e2e-run-contract.ps1'})
if($contract.Count-ne1-or(Get-FileHash -LiteralPath (Join-Path $destination $contract[0].path)).Hash-ne$contract[0].sha256){throw 'Contract mismatch'}
. (Join-Path $destination 'scripts/e2e-run-contract.ps1')
$script:CampaignManifestSha256=(Get-FileHash -LiteralPath $manifestPath).Hash
$script:CampaignManifest=Read-E2eManifest $manifestPath $CampaignManifestSha256
Assert-E2eTools $CampaignManifest $destination
& (Join-Path $destination 'scripts/e2e-product-prepare.ps1')
