$ErrorActionPreference='Stop'
& 'C:/Users/jacking75NoteBook/RemoteAssist-Test/20261022-2243/guard.ps1' -Mode Stop
$end=[DateTime]::UtcNow.AddSeconds(15)
while(!(Test-Path 'C:/Users/jacking75NoteBook/RemoteAssist-Test/20261022-2243/cleanup.json')){if([DateTime]::UtcNow-gt$end){throw 'Cleanup not confirmed'};Start-Sleep -Milliseconds 200}
Get-Content 'C:/Users/jacking75NoteBook/RemoteAssist-Test/20261022-2243/cleanup.json' -Raw
Unregister-ScheduledTask -TaskName 'RemoteAssist-Manual-E2E-20261022-2243' -Confirm:$false
