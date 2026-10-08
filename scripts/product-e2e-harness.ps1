[CmdletBinding()]
param([ValidateSet('Prepare','Arm','Finish','Abort','Collect')][string]$Action='Prepare',[string]$RunDir,[ValidateRange(30,300)][int]$Seconds=240,[string]$Observation,[string]$Readiness)
$ErrorActionPreference='Stop'
if($Action-eq'Arm'){throw '30분 전역 deadline/독립 watchdog/유한 정리의 구현·실증이 없어 Arm을 차단한다. 원격제어-구현계획.md R30-05~08을 먼저 완료한다.'}
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'));$artifactRoot=Join-Path $repo '.artifacts';$utf8=New-Object Text.UTF8Encoding($false)
function Save($name,$value){[IO.File]::WriteAllText((Join-Path $RunDir $name),($value|ConvertTo-Json -Depth 10),$utf8)}
if($Action-eq'Prepare'){
    if(Get-Service -Name RemoteAssistHost -ErrorAction SilentlyContinue){throw '기존 제품 서비스가 있다. 깨끗한 VM 또는 별도 시험 환경이 필요하다'}
    foreach($name in @('dotnet','go','node','powershell.exe')){Get-Command $name -CommandType Application -ErrorAction Stop|Out-Null}
    $prepared=@(& (Join-Path $PSScriptRoot 'invoke-product-local.ps1') -PrepareOnly -Seconds $Seconds);$RunDir=[IO.Path]::GetFullPath($prepared[-1])
    $manifest=Get-Content -LiteralPath (Join-Path $RunDir 'manifest.json') -Raw -Encoding UTF8|ConvertFrom-Json
    if(!$manifest.Count){throw '후보 파일 목록이 없다'}
    $plan=Get-Content -LiteralPath (Join-Path $repo 'tests/e2e/scenarios.json') -Raw -Encoding UTF8|ConvertFrom-Json
    Save 'prepared.json' @{commit=(& git -C $repo rev-parse HEAD).Trim();dirty=!!(& git -C $repo status --porcelain);manifestSha256=(Get-FileHash -LiteralPath (Join-Path $RunDir 'manifest.json')).Hash;account=[Security.Principal.WindowsIdentity]::GetCurrent().Name;sessionId=(Get-Process -Id $PID).SessionId;tools=@(foreach($name in @('dotnet','go','node')){@{name=$name;path=(Get-Command $name).Source}});preparedUtc=[datetime]::UtcNow.ToString('o');deadlineSeconds=$Seconds;scenarios=$plan.scenarios;state='PREPARED_NOT_PASS'}
    Write-Output $RunDir;exit 0
}
$RunDir=[IO.Path]::GetFullPath($RunDir)
if(!$RunDir.StartsWith($artifactRoot+'\',[StringComparison]::OrdinalIgnoreCase)){throw '이 저장소 artifact 안의 준비 경로가 필요하다'}
$prepared=Get-Content -LiteralPath (Join-Path $RunDir 'prepared.json') -Raw -Encoding UTF8|ConvertFrom-Json
if($Action-eq'Arm'){
    if(Test-Path -LiteralPath (Join-Path $RunDir 'armed.json')){throw '동일 준비 묶음은 재실행하지 않는다. 원인/수정 리뷰 후 새 Prepare를 수행한다'}
    foreach($item in Get-Content -LiteralPath (Join-Path $RunDir 'manifest.json') -Raw -Encoding UTF8|ConvertFrom-Json){$file=[IO.Path]::GetFullPath((Join-Path (Join-Path $RunDir 'publish') $item.path));if(!$file.StartsWith((Join-Path $RunDir 'publish')+'\',[StringComparison]::OrdinalIgnoreCase)-or(Get-FileHash -LiteralPath $file).Hash-ne$item.sha256){throw '준비 뒤 후보가 변경됐다'}}
    if((Get-Process -Id $PID).SessionId-ne$prepared.sessionId){throw '준비한 Windows 세션이 아니다'}
    if(!$Readiness){throw '리뷰한 실행 계획과 실제 환경의 readiness JSON이 필요하다. tests/e2e/README.md를 따른다'}
    $ready=Get-Content -LiteralPath $Readiness -Raw -Encoding UTF8|ConvertFrom-Json
    if(!$ready.reviewed-or!$ready.stopRecoveryReviewed-or!$ready.operator-or!$ready.desktop-or!$ready.scenario-or$ready.scenario-notin$prepared.scenarios.id-or!$ready.endpoint){throw '실행/중단 리뷰·현장 조작자·desktop·시나리오·endpoint 확인이 필요하다'}
    if($ready.scenario-eq'input-focus'){$target=Get-Process -Id $ready.targetPid -ErrorAction Stop;if($target.Path-ne$ready.targetImage-or$target.SessionId-ne$prepared.sessionId-or!$ready.foregroundConfirmed-or!$ready.geometry){throw '실제 대상 PID/경로·전경·geometry 준비가 필요하다'}}
    if($ready.scenario-eq'admin-desktop'-and(!$ready.localRecoveryOperator-or!$ready.recoveryProcedure)){throw '현장 관리자 화면 복귀 준비가 필요하다'}
    $endpoint=[uri]$ready.endpoint;if($endpoint.Scheme-ne'https'-and!($endpoint.Scheme-eq'http'-and$endpoint.IsLoopback)){throw '안전한 signaling endpoint가 아니다'}
    $hostOptions=Get-Content -LiteralPath (Join-Path $RunDir 'publish/host.settings.json') -Raw -Encoding UTF8|ConvertFrom-Json;if(([uri]$hostOptions.SignalingUri).Authority-ne$endpoint.Authority){throw '준비한 Host와 signaling endpoint가 다르다'}
    $probe=Invoke-WebRequest -Uri (New-Object uri($endpoint,'/readyz')) -TimeoutSec 5 -UseBasicParsing;if($probe.StatusCode-ne200){throw 'signaling ready 실패'}
    Save 'readiness.json' $ready
    # The operator must read the plan, check actual foreground/coordinates, and consent in the real Tray.
    $launchArgs='-NoProfile -ExecutionPolicy Bypass -File "{0}" -Admin -RunDir "{1}" -Seconds {2}'-f(Join-Path $PSScriptRoot 'invoke-product-local.ps1'),$RunDir,$prepared.deadlineSeconds
    $process=Start-Process -FilePath (Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe') -Verb RunAs -WindowStyle Hidden -ArgumentList $launchArgs -PassThru
    Save 'armed.json' @{utc=[datetime]::UtcNow.ToString('o');installerPid=$process.Id;deadlineSeconds=$prepared.deadlineSeconds;state='AWAITING_REAL_TRAY_AND_MANUAL_CONSENT'}
    $wait=[Diagnostics.Stopwatch]::StartNew();$liveTray=$null
    while($wait.Elapsed.TotalSeconds-lt20-and!$process.HasExited){$installed=Join-Path $RunDir 'install.json';if(Test-Path -LiteralPath $installed){$record=Get-Content -LiteralPath $installed -Raw -Encoding UTF8|ConvertFrom-Json;$liveTray=@(Get-Process -Name RemoteAssist.Host.Tray -ErrorAction SilentlyContinue|Where-Object {$_.Path-eq(Join-Path $record.protectedDirectory 'RemoteAssist.Host.Tray.exe')-and$_.MainWindowHandle-ne0});if($liveTray.Count-eq1-and(Test-Path -LiteralPath (Join-Path $RunDir 'ready.json'))){break}};Start-Sleep -Milliseconds 100}
    if(!$liveTray-or$liveTray.Count-ne1){[IO.File]::WriteAllText((Join-Path $RunDir 'finish'),'readiness-failed',$utf8);throw '실제 Tray 준비 신호가 없어 중단·정리를 요청했다. Collect 후 원인을 분석한다'}
    Write-Output '제품 시험을 시작했다. 실제 Tray·Viewer 준비 신호를 확인하고 대상자가 직접 승인한다. 실패 시 Abort한다.';exit 0
}
if($Action-in@('Finish','Abort')){[IO.File]::WriteAllText((Join-Path $RunDir 'finish'),$Action,$utf8);Save 'stop-request.json' @{utc=[datetime]::UtcNow.ToString('o');reason=$Action};Write-Output '소유 서비스·Job·복원 헬퍼 정리를 요청했다. Collect로 정리 결과를 판정한다.';exit 0}
if($Observation){
    $source=[IO.Path]::GetFullPath($Observation);$observations=Get-Content -LiteralPath $source -Raw -Encoding UTF8|ConvertFrom-Json
    foreach($item in $observations){if($item.id-notin$prepared.scenarios.id-or$item.result-notin@('PASS','FAIL','BLOCKED')-or!$item.evidence-or!$item.actualSeconds){throw '시나리오 ID·판정·관찰 증거·실제 시간은 필수다'}}
    Save 'observations.json' @($observations)
}
$cleanupPath=Join-Path $RunDir 'cleanup.json';$clean=$false;if(Test-Path -LiteralPath $cleanupPath){$cleanup=Get-Content -LiteralPath $cleanupPath -Raw -Encoding UTF8|ConvertFrom-Json;$clean=$cleanup.serviceAbsent-and$cleanup.stageAbsent-and$cleanup.ownedChildrenAbsent}
$observed=@();$observationPath=Join-Path $RunDir 'observations.json';if(Test-Path -LiteralPath $observationPath){$observed=@(Get-Content -LiteralPath $observationPath -Raw -Encoding UTF8|ConvertFrom-Json)}
$result=if(!$clean){'CLEANUP_INCOMPLETE'}elseif(!$observed.Count){'BLOCKED_NO_FUNCTIONAL_EVIDENCE'}elseif(@($observed|Where-Object result -ne 'PASS').Count){'FAIL_OR_BLOCKED'}else{'OBSERVED_SCENARIOS_PASS'}
Save 'acceptance.json' @{utc=[datetime]::UtcNow.ToString('o');state=$result;clean=$clean;observed=$observed;unexecuted=@($prepared.scenarios|Where-Object {$_.id-notin$observed.id}|Select-Object -ExpandProperty id);scope='single PC local, not F or production acceptance'}
Write-Output $result;if(!$clean){throw '정리가 완료되지 않았다. 일반 작업 복구 전 다음 시험을 시작하지 않는다'}
