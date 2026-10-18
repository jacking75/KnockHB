# Read-only host checks and extraction/validation in the newly owned user staging folder.
if(!$Staging){throw 'Explicit owned staging folder required'}
$archive=Join-Path $Staging 'prepared.zip'
$expected='0f35edd34a746385ea38d4230e0654753ffd24f2ce467481b2b65e7b83962323'
if((Get-FileHash -LiteralPath $archive).Hash.ToLowerInvariant()-ne$expected){throw 'Transferred archive hash mismatch'}
$destination=Join-Path $Staging 'candidate'
if(Test-Path $destination){throw 'Fresh extraction directory required'}
$clock=[Diagnostics.Stopwatch]::StartNew()
Expand-Archive -LiteralPath $archive -DestinationPath $destination
$extractSeconds=$clock.Elapsed.TotalSeconds
$shell=Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
Add-Type -Path (Join-Path $destination 'scripts/e2e-native.cs')
$deadline=[E2eOwnedCommand]::Deadline(180000)
$command=[E2eOwnedCommand]::Start($shell,@('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',(Join-Path $destination 'tests/e2e/native-deadline.ps1')))
try{if($command.Wait($deadline,10000)-ne0){throw 'Native deadline regression failed'}}finally{$command.Dispose()}
$validationClock=[Diagnostics.Stopwatch]::StartNew()
$command=[E2eOwnedCommand]::Start($shell,@('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',(Join-Path $destination 'scripts/install-host-dev.ps1'),'-Action','Validate','-Package',$destination))
try{
    while(!$command.HasExited){if([E2eOwnedCommand]::Remaining($deadline)-eq0){throw 'Candidate validation timeout'};Start-Sleep -Milliseconds 50}
    if($command.Wait($deadline,1000)-ne0){throw 'Candidate validation failed'}
}finally{$command.Dispose()}
@{state='PREFLIGHT_ONLY_NOT_E2E_PASS';candidate=$destination;archiveSha256=$expected;extractSeconds=$extractSeconds;validationSeconds=$validationClock.Elapsed.TotalSeconds;totalSeconds=$clock.Elapsed.TotalSeconds;productServiceAbsent=!(Test-Path 'HKLM:\SYSTEM\CurrentControlSet\Services\RemoteAssistHost')}|ConvertTo-Json
