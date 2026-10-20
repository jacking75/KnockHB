param([Parameter(Mandatory=$true)][string]$Output,[Parameter(Mandatory=$true)][string]$Thumbprint)
$ErrorActionPreference='Stop'
if($Thumbprint-notmatch'^[a-fA-F0-9]{40}$'){throw 'Exact certificate identity required'}
$root=Join-Path $env:ProgramFiles 'RemoteAssist-Dev'
$registry='HKLM:\SYSTEM\CurrentControlSet\Services\RemoteAssistHost'
$processes=@(Get-Process -Name 'RemoteAssist.Host.*' -ErrorAction SilentlyContinue|Where-Object {
    # Access-denied/unknown paths are a failed observation, never proof of absence.
    if(!$_.Path){throw 'Product process image unavailable'}
    $_.Path.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)
}|ForEach-Object {@{pid=$_.Id;image=$_.Path;start=$_.StartTime.ToUniversalTime().Ticks;session=$_.SessionId}})
$store=New-Object Security.Cryptography.X509Certificates.X509Store('Root','LocalMachine')
try{$store.Open('ReadOnly');$trustPresent=@($store.Certificates|Where-Object {$_.Thumbprint-eq$Thumbprint}).Count-gt0}finally{$store.Close()}
$service=Get-Service RemoteAssistHost -ErrorAction SilentlyContinue
$listeners=@(Get-NetTCPConnection -LocalPort 25001 -State Listen -ErrorAction SilentlyContinue)
$value=@{serviceAbsent=!(Test-Path -LiteralPath $registry);serviceState=$(if($service){[string]$service.Status}else{$null});productDirectoryAbsent=!(Test-Path -LiteralPath $root);ownedProcesses=$processes;trustPresent=$trustPresent;productListenerCount=$listeners.Count;tick=[Diagnostics.Stopwatch]::GetTimestamp();frequency=[Diagnostics.Stopwatch]::Frequency}
[IO.File]::WriteAllText($Output,($value|ConvertTo-Json -Depth 6),(New-Object Text.UTF8Encoding($false)))
