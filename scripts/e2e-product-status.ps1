$ErrorActionPreference='Stop'
$owner=Get-E2eOwner
$result=@{}
$result['_clock']=@{tick=[Diagnostics.Stopwatch]::GetTimestamp();frequency=[Diagnostics.Stopwatch]::Frequency;id=$owner.id}
foreach($name in @('product-start.json','product-running.json','product-watchdog.json','product-watchdog-error.json','product-error.json','product-cleanup.json','product-recovery-required.json')){
 $file=Join-Path $owner.root $name
 if(Test-Path $file){$result[$name]=[IO.File]::ReadAllText($file)|ConvertFrom-Json}
}
if($CampaignManifest.version-eq4-and$result.ContainsKey('product-watchdog.json')){
 $wd=$result['product-watchdog.json'];$process=Get-Process -Id $wd.pid -ErrorAction SilentlyContinue
 $alive=$false
 if($process){try{$null=$process.Handle;$alive=!$process.HasExited-and$process.StartTime.ToUniversalTime().Ticks-eq$wd.start-and$process.Path-eq(Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe')}finally{$process.Dispose()}}
 $result['_watchdogAlive']=[bool]$alive
}
foreach($name in @('ui.json','pairing.json','observer-error.json')){
 $file=Join-Path $owner.output $name
 if(Test-Path $file){$result[$name]=[IO.File]::ReadAllText($file)|ConvertFrom-Json}
}
$result|ConvertTo-Json -Depth 5
