$ErrorActionPreference='Stop'
$base='C:/Users/jacking75NoteBook/RemoteAssist-Test/20261022-2304'
& "$base/guard.ps1" -Mode Stop -RunId 20261022-2304
$end=[DateTime]::UtcNow.AddSeconds(15)
while(!(Test-Path "$base/cleanup.json")){if([DateTime]::UtcNow-gt$end){throw 'Cleanup not confirmed'};Start-Sleep -Milliseconds 200}
Get-Content "$base/cleanup.json" -Raw
Unregister-ScheduledTask -TaskName 'RemoteAssist-Manual-E2E-20261022-2304' -Confirm:$false
