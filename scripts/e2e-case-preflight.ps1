$ErrorActionPreference='Stop'
$owner=Get-E2eOwner
$tlsArgs=@{Certificate=(Join-Path $owner.root 'lan-test.cer');Sha256=$CampaignManifest.tls.sha256}
if($CampaignManifest.tls.mode-eq'pinned-leaf'){$tlsArgs.PinnedTool=Join-Path $owner.root 'desktop-tool/E2eDesktop.exe'}
$tls=& (Join-Path $owner.root 'scripts/e2e-tls-identity.ps1') @tlsArgs
$verified=$tls|ConvertFrom-Json
if(!$verified.chainValid-or$verified.storeMutated){throw 'Read-only TLS verification failed'}
$uac=$null
if($CampaignManifest.caseId-eq'C04'){
 $trigger=Join-Path $env:SystemRoot 'System32/cmd.exe'
 $signature=Get-AuthenticodeSignature -LiteralPath $trigger
 if($signature.Status-ne'Valid'-or(Get-FileHash -LiteralPath $trigger).Hash-ne$CampaignManifest.uacTriggerSha256){throw 'Signed Windows UAC trigger changed'}
 $uac=@{signatureValid=$true;sha256=$CampaignManifest.uacTriggerSha256}
}
@{status='completed';id=$owner.id;tls=$verified;uac=$uac;productStarted=$false}|ConvertTo-Json -Depth 4 -Compress
