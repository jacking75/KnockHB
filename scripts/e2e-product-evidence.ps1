$ErrorActionPreference='Stop'
$owner=Get-E2eOwner
$result=@{}
foreach($name in @('service-status.ndjson','product-error.json','product-cleanup.json')){
 $path=Join-Path $owner.root $name
 if((Test-Path $path)-and(Get-Item $path).Length-lt524288){$result[$name]=[Convert]::ToBase64String([IO.File]::ReadAllBytes($path))}
}
$result|ConvertTo-Json
