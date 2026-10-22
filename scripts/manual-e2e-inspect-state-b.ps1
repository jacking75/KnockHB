$ErrorActionPreference='Stop'
$root='C:\ProgramData\RemoteAssist'
@{files=@(Get-ChildItem -LiteralPath $root -Force | Select-Object Name,Length,LastWriteTime);owner=if(Test-Path "$root\dev-owner.json"){Get-Content "$root\dev-owner.json" -Raw | ConvertFrom-Json}else{$null};processes=@(Get-CimInstance Win32_Process | Where-Object {$_.ExecutablePath -like '*RemoteAssist*' -or $_.ExecutablePath -like '*DesktopTestDriver*'} | Select-Object ProcessId,Name,SessionId,ExecutablePath);regTestAbsent=!(Test-Path 'HKLM:\SOFTWARE\RemoteAssistE2E-20261022-2243')} | ConvertTo-Json -Depth 5
