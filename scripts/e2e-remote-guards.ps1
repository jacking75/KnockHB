$ErrorActionPreference='Stop'
if($Staging-notmatch'^C:/Users/jacking75NoteBook/RemoteAssist-E2E-[0-9]+$'){throw 'Unexpected staging root'}
$zip=Join-Path $Staging 'prepared.zip'
if((Get-FileHash -LiteralPath $zip).Hash-ne'1FB749963BC87621E95169B478250E7E3CE7D5F644FAF592FA2EEF5771A67810'){throw 'Transferred tools hash mismatch'}
$destination=Join-Path $Staging 'guards'
if(Test-Path $destination){throw 'Fresh preparation directory required'}
Expand-Archive -LiteralPath $zip -DestinationPath $destination
$timer=[Diagnostics.Stopwatch]::StartNew()
& (Join-Path $destination 'tests/e2e/native-deadline.ps1')
& (Join-Path $destination 'tests/e2e/campaign-fixture.ps1')
@{state='INPUT_FREE_GUARDS_ONLY';seconds=$timer.Elapsed.TotalSeconds;productStarted=$false;sshIndependenceProven=$false}|ConvertTo-Json
