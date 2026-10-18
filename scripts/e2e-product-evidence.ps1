$ErrorActionPreference='Stop'
$owner=Get-Content (Join-Path $Staging 'product-owner.json') -Raw -Encoding UTF8|ConvertFrom-Json
if($owner.root-ne(Join-Path $env:ProgramData ('RemoteAssist-E2E-Product-'+$owner.id))){throw 'Owner mismatch'}
$result=@{}
foreach($name in @('service-status.ndjson','product-error.json','product-cleanup.json')){
 $path=Join-Path $owner.root $name
 if((Test-Path $path)-and(Get-Item $path).Length-lt524288){$result[$name]=[Convert]::ToBase64String([IO.File]::ReadAllBytes($path))}
}
$result|ConvertTo-Json
