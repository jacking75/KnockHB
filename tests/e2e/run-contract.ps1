$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $repo 'scripts/e2e-run-contract.ps1')
$output=Join-Path $repo ('.artifacts/run-contract-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output|Out-Null
$script:checks=0
function Require($Condition,[string]$Reason){if(!$Condition){throw $Reason};$script:checks++}
function Reject([scriptblock]$Work){$rejected=$false;try{& $Work|Out-Null}catch{$rejected=$true};Require $rejected 'Unsafe contract accepted'}
$tools=@(Get-ChildItem -LiteralPath (Join-Path $repo 'scripts') -File|Where-Object {$_.Name-like'e2e-*'-or$_.Name-eq'install-host-dev.ps1'}|ForEach-Object {@{path=('scripts/'+$_.Name);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
$m=@{version=3;id=[guid]::NewGuid().ToString('N');profile='lan-view-consent-stop';state='PREPARED_NOT_AUTHORIZED';archiveSha256=('a'*64);candidateSha256=('b'*64);tls=@{sha256=('c'*64);thumbprint=('d'*40)};tools=$tools;budget=@{totalSeconds=900;targetSeconds=600;activeSeconds=300;cleanupSeconds=240;recoverySeconds=300;reportSeconds=60}}
$path=Join-Path $output 'campaign.json'
Write-E2eNewJson $path $m
$hash=(Get-FileHash -LiteralPath $path).Hash
$valid=Read-E2eManifest $path $hash
Require ($valid.id-eq$m.id) 'GUID drift'
Assert-E2eTools $valid $repo;$script:checks++
Reject {Read-E2eManifest $path ('e'*64)}
foreach($case in @('guid','budget','duplicate','traversal','missing-tool')){
 $bad=$m|ConvertTo-Json -Depth 10|ConvertFrom-Json
 switch($case){
  'guid' {$bad.id='842dd89df2104c0ba7a2aa99cc9fb3e00'}
  'budget' {$bad.budget.totalSeconds=1800}
  'duplicate' {$bad.tools+=@($bad.tools[0])}
  'traversal' {$bad.tools[0].path='scripts/../e2e-native.cs'}
  'missing-tool' {$bad.tools=@($bad.tools|Where-Object {$_.path-ne'scripts/e2e-native.cs'})}
 }
 $file=Join-Path $output ($case+'.json');Write-E2eNewJson $file $bad
 Reject {Read-E2eManifest $file (Get-FileHash -LiteralPath $file).Hash}
}
$badHash=$m|ConvertTo-Json -Depth 10|ConvertFrom-Json;$badHash.tools[0].sha256='e'*64
Reject {Assert-E2eTools $badHash $repo}
# Reparse ancestors must be rejected even when the terminal file itself is ordinary.
$junction=Join-Path $output 'linked-scripts'
New-Item -ItemType Junction -Path $junction -Target (Join-Path $repo 'scripts')|Out-Null
try{Reject {Assert-E2ePath (Join-Path $junction 'e2e-native.cs')}}finally{[IO.Directory]::Delete($junction)}
$ledger=Join-Path $output 'once.json';Write-E2eNewJson $ledger @{status='한글 😀';id=$m.id}
Require ((Read-E2eJson $ledger).status-eq'한글 😀') 'UTF8 damaged'
Reject {Write-E2eNewJson $ledger @{status='replayed'}}
Require ((Read-E2eJson $ledger).status-eq'한글 😀') 'Replay overwrote ledger'
Require ((Get-E2eRemoteDeadline 1230000 10000000 2500)-eq26230000) 'Remote QPC grant extended'
Reject {Get-E2eRemoteDeadline 1 10000000 900001}
Reject {Get-E2eRemoteDeadline 1 10000000 0}
# Check actual Arm entrypoint with fake ownership, without any task/product action.
function Get-E2eOwner {return @{id='fixture'}}
$script:starts=0
function Start-ScheduledTask {$script:starts++;throw 'Task start must never be reached'}
Reject {& (Join-Path $repo 'scripts/e2e-product-arm.ps1')}
Require ($script:starts-eq0) 'Arm started a task'
# Exercise the actual product cleanup adapter with fixture resources. No product/SCM/trust
# operations run: only those three boundary functions are replaced, not the cleanup verdict.
Add-Type -Path (Join-Path $repo 'scripts/e2e-native.cs')
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $repo 'scripts/e2e-product-guard.ps1'),[ref]$null,[ref]$null)
foreach($name in @('Save','Read','Cleanup')){
 $definition=$ast.Find({param($node) $node-is[Management.Automation.Language.FunctionDefinitionAst]-and$node.Name-eq$name},$true)
 Invoke-Expression $definition.Extent.Text
}
function Installer([string]$Action){if($Action-ne'Remove'){throw 'Unexpected fixture mutation'}}
function Certificate([string]$Action){if($script:fault-eq'trust'){throw 'fixture trust removal failure'}}
function Inspect {
 if($script:fault-eq'observation'){throw 'fixture observation missing'}
 return @{serviceAbsent=($script:fault-ne'service');productDirectoryAbsent=$true;ownedProcesses=$(if($script:fault-eq'process'){@(@{pid=42})}else{@()});trustPresent=($script:fault-eq'trust')}
}
$manifest=@{id=$m.id};$thumb='d'*40;$Role='Controller';$utf8=New-Object Text.UTF8Encoding($false)
foreach($fault in @('none','service','process','trust','observation')){
 $script:fault=$fault;$RunDir=Join-Path $output ('cleanup-'+$fault);New-Item -ItemType Directory -Path $RunDir|Out-Null
 $image=Join-Path $RunDir 'not-installed/service.exe'
 $deadline=[E2eOwnedCommand]::Deadline(90000);$activeEnd=[E2eOwnedCommand]::Deadline(30000)
 Write-E2eNewJson (Join-Path $RunDir 'tls-owner.json') @{added=$true;thumb=$thumb}
 Cleanup
 $record=Read-E2eJson (Join-Path $RunDir 'product-cleanup.json')
 $expected=if($fault-eq'none'){'RESOURCES_ABSENT_RECOVERY_UNVERIFIED'}else{'RECOVERY_REQUIRED'}
 Require ($record.state-eq$expected-and!$record.recoveryVerified) ('Incorrect product cleanup verdict: '+$fault)
}
@{state='PASS_INPUT_FREE_CONTRACT_ONLY';checks=$script:checks;artifact=$output;notTested='product cleanup, independent recovery, GUI and runtime handshake'}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $output 'result.json') -Encoding UTF8
Write-Output ('PASS '+$script:checks+' input-free contract checks; '+$output)
