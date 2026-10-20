$ErrorActionPreference='Stop'
$owner=Get-E2eOwner
$result=@{}
$result['_clock']=@{tick=[Diagnostics.Stopwatch]::GetTimestamp();frequency=[Diagnostics.Stopwatch]::Frequency;id=$owner.id}
foreach($name in @('product-start.json','product-running.json','product-error.json','product-cleanup.json','product-recovery-required.json')){
 $file=Join-Path $owner.root $name
 if(Test-Path $file){$result[$name]=[IO.File]::ReadAllText($file)|ConvertFrom-Json}
}
foreach($name in @('ui.json','pairing.json','observer-error.json')){
 $file=Join-Path $owner.output $name
 if(Test-Path $file){$result[$name]=[IO.File]::ReadAllText($file)|ConvertFrom-Json}
}
$result|ConvertTo-Json -Depth 5
