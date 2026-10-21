[CmdletBinding()]
param([Parameter(Mandatory=$true)][ValidateSet('Validate','Prepare','Run','Abort','Collect')][string]$Action,
      [Parameter(Mandatory=$true)][string]$RunDir,
      [Parameter(Mandatory=$true)][string]$Config,
      [switch]$Execute)
$ErrorActionPreference='Stop'
if(($Action-eq'Run')-ne[bool]$Execute){Write-Output '{"state":"ERROR","code":"execute-only-for-run"}';exit 40}
$arguments=@('-X','utf8',(Join-Path $PSScriptRoot 'e2e_case_runtime.py'),'--case','C16','--action',$Action,'--run-dir',$RunDir,'--config',$Config)
if($Execute){$arguments+='--execute'}
& python @arguments
exit $LASTEXITCODE
