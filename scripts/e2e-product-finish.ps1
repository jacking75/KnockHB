$ErrorActionPreference='Stop'
$owner=Get-E2eOwner
if($CampaignManifest.version-eq4){
 foreach($name in @('clipboard-restore','target-finish','fault-stop')){
  $signal=Join-Path $owner.output $name
  if(!(Test-Path -LiteralPath $signal)){Write-E2eNewJson $signal @{id=$owner.id;tick=[Diagnostics.Stopwatch]::GetTimestamp()}}
 }
}
@{reason='requested-stop';utc=[DateTime]::UtcNow.ToString('o')}|ConvertTo-Json|Set-Content (Join-Path $owner.root 'finish.json') -Encoding UTF8
Write-Output 'STOP_REQUESTED'
