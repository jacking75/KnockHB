$ErrorActionPreference='Stop'
$owner=Get-Content (Join-Path $Staging 'product-owner.json') -Raw -Encoding UTF8|ConvertFrom-Json
if($owner.root-ne(Join-Path $env:ProgramData ('RemoteAssist-E2E-Product-'+$owner.id))){throw 'Owner mismatch'}
$result=@{}
foreach($name in @('product-start.json','product-running.json','product-error.json','product-cleanup.json','product-recovery-required.json')){
 $file=Join-Path $owner.root $name
 if(Test-Path $file){$result[$name]=[IO.File]::ReadAllText($file)|ConvertFrom-Json}
}
foreach($name in @('ui.json','pairing.json','observer-error.json')){
 $file=Join-Path $owner.output $name
 if(Test-Path $file){$result[$name]=[IO.File]::ReadAllText($file)|ConvertFrom-Json}
}
$result|ConvertTo-Json -Depth 5
