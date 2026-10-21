[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Request,
      [Parameter(Mandatory=$true)][string]$Python,
      [Parameter(Mandatory=$true)][ValidateRange(1,180000)][int]$Milliseconds)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'e2e-run-contract.ps1')
$null=Assert-E2ePath $Request
$null=Read-E2eJson $Request
Add-Type -Path (Join-Path $PSScriptRoot 'e2e-native.cs')
$end=[E2eOwnedCommand]::Deadline($Milliseconds)
$child=[E2eOwnedCommand]::Start($Python,@('-X','utf8',(Join-Path $PSScriptRoot 'e2e_case_command.py'),$Request))
try {
 while(!$child.HasExited){
  if([E2eOwnedCommand]::Remaining($end)-eq0){throw 'Owned command deadline'}
  Start-Sleep -Milliseconds 20
 }
 exit $child.Wait($end,1000)
} finally { $child.Dispose() }
