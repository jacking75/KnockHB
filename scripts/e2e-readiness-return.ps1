[CmdletBinding()]
param([Parameter(Mandatory=$true)][ValidateSet('Start','ActiveEnd','CleanupEnd','Return','Finish')][string]$Action,
      [Parameter(Mandatory=$true)][string]$Directory,[Parameter(Mandatory=$true)][string]$Context,
      [switch]$Execute)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'e2e-run-contract.ps1')
if(!$Execute){throw 'Separate fixture execution authorization required'}
$c=Read-E2eJson $Context
if($c.schema-ne'remoteassist-e2e-fixture-context-v1'-or$c.runId-notmatch'^[a-f0-9]{32}$'-or
   $c.authorization.scope-ne'product-independent-readiness-fixture'-or$c.authorization.runId-ne$c.runId-or
   !$c.authorization.operator-or!$c.authorization.consoleAvailable){throw 'Fixture authorization/independent return missing'}
$Directory=[IO.Path]::GetFullPath($Directory)
if($Directory-ne[IO.Path]::GetFullPath($c.directory)){throw 'Fixture owner directory mismatch'}
$null=Assert-E2ePath $Directory
$tick=[Diagnostics.Stopwatch]::GetTimestamp()
$frequency=[Diagnostics.Stopwatch]::Frequency
$origin=Join-Path $Directory 'phase-Start.json'
if($Action-eq'Start'){
 Write-E2eNewJson $origin @{runId=$c.runId;phase=$Action;tick=$tick;frequency=$frequency;contextSha256=(Get-FileHash $Context).Hash}
 return
}
$start=Read-E2eJson $origin
if($start.runId-ne$c.runId-or$start.contextSha256-ne(Get-FileHash $Context).Hash-or$start.frequency-ne$frequency){throw 'Fixture origin/context changed'}
$limit=switch($Action){'ActiveEnd'{300};'CleanupEnd'{540};'Return'{840};'Finish'{900}}
if($tick-le$start.tick-or($tick-$start.tick)/$frequency-gt$limit){throw 'Fixture phase deadline exceeded; no fresh origin permitted'}
$previous=switch($Action){'ActiveEnd'{'Start'};'CleanupEnd'{'ActiveEnd'};'Return'{'CleanupEnd'};'Finish'{'Return'}}
$prior=Read-E2eJson (Join-Path $Directory ('phase-'+$previous+'.json'))
if($prior.runId-ne$c.runId-or$prior.tick-ge$tick){throw 'Fixture phase order'}
if($Action-in@('Return','Finish')){
 $operator=Read-E2eJson $c.operatorEvidence
 if($operator.runId-ne$c.runId-or$operator.operator-ne$c.authorization.operator-or$operator.state-ne'RETURNED'){throw 'Independent operator return not witnessed'}
 foreach($side in @('a','b')){
  $snapshot=Read-E2eJson $c.snapshots.$side
  if($snapshot.runId-ne$c.runId-or!$snapshot.available-or$snapshot.desktop-ne'Default'-or
     $snapshot.sid-ne$c.identity.$side.sid-or$snapshot.session-ne$c.identity.$side.session-or
     $snapshot.activeSessionId-ne$c.identity.$side.session-or@($snapshot.heldVirtualKeys).Count){throw 'Fixture desktop return incomplete'}
  $absence=Read-E2eJson $c.absenceEvidence.$side
  if($absence.runId-ne$c.runId-or!$absence.resourcesAbsent-or@($absence.remaining).Count-or@($absence.changed).Count){throw 'Independent fixture resource inspection missing'}
  if($side-eq'a'){
   $cleanup=Read-E2eJson (Join-Path $Directory 'phase-CleanupEnd.json')
   if($snapshot.frequency-ne$frequency-or$absence.frequency-ne$frequency-or$snapshot.tick-lt$cleanup.tick-or$absence.tick-lt$cleanup.tick){throw 'A independent return evidence is stale'}
  }
  $expectedPids=@(foreach($path in $c.ownedProcessFiles.$side){$record=Read-E2eJson $path;if($record.PSObject.Properties.Name-contains'identity'){$record=$record.identity};if($record.pid-le0){throw 'Fixture process identity missing'};[int]$record.pid})
  if(!$expectedPids.Count-or@((Compare-Object ($expectedPids|Sort-Object -Unique) ($absence.inspectedPids|Sort-Object -Unique))).Count){throw 'Fixture inspected owner set differs'}
 }
 # Services belong to this fixture only. Never stop/delete a shared service.
 if(Get-Service -Name ('RAE2E-'+$c.runId) -ErrorAction SilentlyContinue){throw 'Owned fixture service remains'}
}
Write-E2eNewJson (Join-Path $Directory ('phase-'+$Action+'.json')) @{runId=$c.runId;phase=$Action;tick=$tick;frequency=$frequency;operator=$c.authorization.operator}
