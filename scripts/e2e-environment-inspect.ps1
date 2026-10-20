# Read-only inventory. No product, DTD, task, trust or input changes.
$ErrorActionPreference='Stop'
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
$principal=New-Object Security.Principal.WindowsPrincipal($identity)
$dtdRoot=Join-Path $env:USERPROFILE 'DesktopTestDriver-E2E-20261020'
$paths=@((Join-Path $dtdRoot 'Agent/Agent.exe'),(Join-Path $dtdRoot 'Agent-e2e.json'),(Join-Path $dtdRoot 'agent-state/identity.cer'),"$env:ProgramFiles/RemoteAssist-Dev", "$env:ProgramData/RemoteAssist")
$files=@(foreach($path in $paths){if(Test-Path -LiteralPath $path){$item=Get-Item -LiteralPath $path -Force;@{path=$item.FullName;directory=$item.PSIsContainer;reparse=[bool]($item.Attributes-band[IO.FileAttributes]::ReparsePoint);sha256=$(if(!$item.PSIsContainer){(Get-FileHash -LiteralPath $path).Hash}else{$null})}}})
$processes=@(Get-Process -Name 'Agent','Controller','RemoteAssist.Host.*' -ErrorAction SilentlyContinue|ForEach-Object {@{pid=$_.Id;image=$_.Path;session=$_.SessionId;start=$_.StartTime.ToUniversalTime().Ticks}})
# Public task identity only; do not print task arguments or environment variables.
$tasks=@(Get-ScheduledTask|Where-Object {$_.TaskName-like'DesktopTestDriver*'-or$_.TaskName-like'RemoteAssist-E2E*'}|ForEach-Object {@{name=$_.TaskName;state=[string]$_.State;actionHash=$(if($_.Actions){$bytes=[Text.Encoding]::UTF8.GetBytes(($_.Actions|ConvertTo-Json -Compress));$sha=[Security.Cryptography.SHA256]::Create();try{([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-','')}finally{$sha.Dispose()}}else{$null})}})
$service=Get-Service RemoteAssistHost -ErrorAction SilentlyContinue
$settings=Join-Path $env:ProgramFiles 'RemoteAssist-Dev/host.settings.json'
$deployed=@(if(Test-Path (Join-Path $dtdRoot 'Agent')){Get-ChildItem -LiteralPath (Join-Path $dtdRoot 'Agent') -Recurse -File|ForEach-Object {@{path=$_.FullName.Substring($dtdRoot.Length+7);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}}})
@{state='READ_ONLY_NOT_E2E';computer=$env:COMPUTERNAME;user=$identity.Name;sid=$identity.User.Value;administrator=$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator);tick=[Diagnostics.Stopwatch]::GetTimestamp();frequency=[Diagnostics.Stopwatch]::Frequency;files=$files;deployedDtdFiles=$deployed;processes=$processes;tasks=$tasks;servicePresent=[bool]$service;serviceState=$(if($service){[string]$service.Status}else{$null})}|ConvertTo-Json -Depth 8
