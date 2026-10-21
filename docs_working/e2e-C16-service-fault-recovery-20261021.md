# C16 — 서비스 장애·입력 복원 상세 실행 계획

작성일: 2026-10-21. 실행 코드 연결과 입력 없는 검증 결과는 11절에 기록한다. 사용자의 당일 실제 E2E 중단 지시는 유지한다.

이 문서는 C16 실행 스크립트의 독립 구현 명세다. 환경·설정·호출·단계 함수·관측·판정·복구를 본문에 모두 정의한다. 다른 계획 문서를 읽어 필수 절차를 채우지 않는다. 전체 목록/상태는 [통합 색인](e2e-acceptance-plan-20261021.md)에서 참고할 수 있지만 이 C 구현의 선행 문서는 아니다.

**구현 상태:** 부분 구현 / NOT READY다. 진입 스크립트·고정 단계·판정·공통 실행기와 DTD 호출 코드를 추가했다. 실제 bootstrap·관측 source·독립 복구는 미완료이며 11절에 파일·함수·검증 결과와 남은 조건을 연결한다.

## 1. 목적과 고정 범위

실제 원격 F12 down의 예약 ACK 뒤 소유 서비스 장애 1회를 주입해 독립 Recovery의 up과 생산자 차단을 확인한다.

**순서:** 일반 제어 동의 → A Viewer F12 down 동작 → 제품 내부 Recovery 예약 ACK → B native down 관측 → 소유 서비스 장애 1회 → 독립 Recovery up·held=0·Helper/세션 종료 → Default 복귀.

다른 C 항목, 환경 조합, 마지막 종합 재시험을 추가하지 않는다. 실패 수정 뒤 재시험은 같은 ID의 새 회차로 연결한다.

## 2. 권한·채널·대상

| 구분 | 이번 회차의 고정값 |
|---|---|
| A | 192.168.0.61 — Controller/Viewer/시그널링 |
| B | 192.168.0.74 — 제품 Host, SSH 사용자 jacking75NoteBook |
| 도구 | F:/github/DesktopTestDriver/bin/live_Release/Controller/Controller.exe |
| 사용자 세션 | 관측 기준 A Session1/B Session2·Default. 실행 전 현재 SID/Session을 재확인한다. |
| 동의 | screen+control만 승인한다. 관리자/Runbook/file은 없다. 서비스 장애는 소유 시험 도구의 관리 작업이며 제품 세션 권한으로 확대하지 않는다. |
| 권한 mask | 3 |
| 채널 | control/input/pointer+video. 실제 down은 A Viewer→제품→B 경로로만 발생해야 한다. |

SSH는 기존 환경 인증과 등록 호스트 키만 사용한다. 비밀값·환경 변수 전체·코드/PIN은 공개 출력에 넣지 않는다. 로컬 머신 루트 신뢰 저장소를 다음 시험의 기본 준비로 변경하지 않는다.

## 3. 시작 전에 끝낼 준비

- B InputTarget 독립 기록과 Recovery bind/예약 ACK 시각을 결합한다. 예약 내용/key/unicode를 제품 일반 로그에 추가하지 않고, 정해진 F12에 대한 상관 ID·revision/성공 boolean만 시험 보호 증거로 수집한다.
- 서비스 PID/image/start/회차 dev-owner/service 등록 ImagePath와 Helper/Recovery PID·handle을 장애 전에 고정한다. 기존 사용자 서비스나 이름만 일치하는 프로세스를 대상으로 삼지 않는다.
- A DTD의 down은 최대 1500ms 뒤 자동 up된다. 여러 CLI 왕복 뒤 수동 kill하면 제품 keyup이 먼저 전달될 수 있다. B 단발 장애 도구를 사전 Arm해 예약 ACK와 B F12 down을 확인한 뒤 즉시 서비스를 1회 종료하도록 준비한다.
- 장애 트리거는 독립 deadline/실행 소유권/소비 1회 ledger와 max-lag를 가져야 한다. 조건 미충족이면 어떤 프로세스도 종료하지 않고 실패한다.
- 기존 invoke-product-local의 Arm은 차단돼 있다. 그 스크립트를 직접 실행하거나 일반 Stop-Service를 서비스 crash의 대체 증거로 사용하지 않는다.

공통으로 후보 commit/dirty 변경 목록, candidate.json SHA256, 도구/설정 hash, 실제 PID/image/start/SID/Session/desktop, 소유 경로·포트, 수집/중단/복구 수단을 고정한다. 빌드·게시·파일 전달·UI 요소 결합·코드 리뷰는 제품 실행 전에 끝낸다.

## 4. 실행 스크립트·설정·UI·함수 계약

### 4.1 구현할 진입 파일과 호출 계약

**이 절은 목표 인터페이스 계약이며 현재 구현 범위는 11절이 기준이다.** 진입 파일은 추가했지만 모든 live adapter가 연결된 상태는 아니다. 아래 실행 계약을 구현 완료나 실전 검증 완료로 해석하지 않는다.

| 항목 | 구현 계약 |
|---|---|
| 진입 파일 | `scripts/e2e-C16.ps1` — 이번 C 전용 진입점, 다른 C 실행 금지 |
| 필수 인자 | `-Action Validate\|Prepare\|Run\|Abort\|Collect -RunDir <A 절대경로> -Config <이번 C 설정 JSON 절대경로>` |
| 실제 실행 | `Run`에만 `-Execute`가 필수다. 그 외 action에 붙이면 거부한다. 별도 사용자 실행 지시와 config/runId/hash가 일치해야 한다. |
| Validate | 로컬 설정·코드·후보 파일 검사만 한다. GUI/Agent 입력·제품/서비스·인증서·네트워크 실행을 하지 않는다. |
| Prepare | 입력 없는 A/B identity·포트·기존 파일/호스트 키·도구 지원·복구 준비를 확인하고 설정/hash를 동결한다. 제품 기동·UI 조작·신뢰 저장소 변경·설치를 하지 않는다. 원격 staging/예약 작업 생성은 검토된 Run 시작 절차와 구분한다. |
| Run | immutable origin과 독립 guard를 만든 뒤 이 문서 5절의 함수만 순차 실행한다. 조건 분기는 관측 성공/실패/중단뿐이다. 재접속·재시도·추가 시나리오는 없다. |
| Abort | 로컬 stop을 먼저 기록하고 소유 B stop/Recovery를 요청한다. 원래 deadline·회차를 유지한다. 원래 요청 결과 조회와 안전 정리는 허용하지만 기능 action 재전송은 금지다. |
| Collect | 저장된 증거와 독립 현재 상태를 읽어 결과를 재구성한다. 설치/기동/입력·시나리오 재실행을 하지 않는다. |
| 종료 코드 | 0=해당 action 완료. 단, Run의 0은 기능 PASS+복구 검증까지 완료한 경우만 허용한다. 10=시작 전 BLOCKED, 20=시작 뒤 FAIL·복구 확인, 30=FAIL_RECOVERY_REQUIRED, 40=설정/명령 오류다. Prepare/Collect의 0을 기능 PASS로 세지 않는다. |

구현 후 호출 모양은 다음과 같다. **현재 실행하지 않는다.** `$CaseRun`/`$CaseConfig`는 Prepare에서 확정한 실제 절대경로이며 예시 경로를 성공값으로 대입하지 않는다.

```powershell
& powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass `
  -File scripts/e2e-C16.ps1 -Action Validate -RunDir $CaseRun -Config $CaseConfig
# 이후 별도 준비/실전 실행 요청이 있을 때만 해당 action을 호출한다.
# Run 호출에는 -Action Run -RunDir $CaseRun -Config $CaseConfig -Execute를 사용한다.
# 중단은 -Action Abort, 읽기 전용 수집은 -Action Collect다.
```

### 4.2 필수 설정·회차 상태

**입력과 생성 결과를 구분한다.** 입력 config에는 caseId/expectedMask, 새 runId/runDir, 후보·DTD·관측/복구 도구 경로, UI 선택 규칙과 이번 C 고정 입력을 넣는다. 아래 표에서 campaignSha256/toolManifest/준비 증거는 Prepare가 계산·저장하는 결과다. DTD sessionId/UI windowId/elementId/process handle/QPC origin은 Run에서 관측해 결합하는 결과다. 미래 실행값을 사람이 임의로 채워 Validate를 통과시키지 않는다.

1. Validate는 입력 config와 로컬 실제 파일·CLI 계약을 검사한다. 아직 Prepare/Run이 생성하지 않은 결과 파일이 없다는 이유로 실패하지 않는다. 입력 자체가 누락/모순이면40, 필요한 로컬 도구/후보가 없으면10이다.
2. Prepare는 입력 config의 hash를 고정하고 `frozen-inputs.json`, `campaign.json`, `prepared.json`에 후보/도구 hash와 명시한 원격 inventory·복구 준비를 저장한다. prepared에는 configSha256/campaignSha256/toolManifestSha256와 검증 결과를 넣는다. B staging/root/task 이름은 현재 runId에서 계산한 **예정 소유 값**이며 이 단계에서 제품을 설치하거나 실제 owner가 이미 존재한다고 가정하지 않는다.
3. Run은 prepared와 원본 config/hash를 읽어 변경/재사용을 거부한다. 실행 원점을 기록한 뒤 검토된 소유 bootstrap으로 B 보호 owner/예약 작업을 만들고 실제 identity/hash가 예정 값과 일치하는지 확인한 다음 Arm한다. 기존의 비소유 제품/서비스/task가 발견되면 덮어쓰거나 제거하지 않는다.
4. 실제 DTD 세션·창·PID는 Run 중 응답에서 한 번씩 결합해 Context/owner ledger로 보존한다. runtime 관측 ID를 Prepare 파일에 소급해 써서 hash를 바꾸지 않는다. config를 바꾸려면 이전 실행 상태/복구를 확인한 뒤 새로운 회차를 준비한다.

| 설정 키 | 실제 값/검증 | 실패 처리 |
|---|---|---|
| caseId / expectedMask | `C16` / `3`. 파일 내부 상수와 같아야 한다. 사용자 인자로 권한을 확대하지 않는다. | 불일치 40 |
| runId / runDir | 새로운 GUID N 32자리 소문자, `F:/github/KnockHB/.artifacts/e2e-C16-<runId>/`. 실패/기실행 ID와 launch-once가 있으면 재사용 불가다. | replay 40 |
| campaignPath / campaignSha256 / expectedProfile | Prepare가 생성할/생성한 현재 회차 campaign.json 절대경로·파일 SHA256·이 C에 결합된 지원 profile. owner의 manifest hash 및 caseId/mask와 모두 일치해야 한다. C02~C16에 C01 profile을 이름만 바꿔 사용하지 않는다. | 미지원/불일치 10 |
| candidateDirectory / candidateSha256 / sourceEvidence | 검증된 실제 후보 경로·candidate.json SHA256·commit/dirty 변경 경계·유효한 기존 build/회귀 증거. 후보를 자동 재빌드하지 않는다. | 누락/변경 10 |
| toolManifest | 이 C 진입 파일과 사용하는 기존 script/exe/config/observer/fixture의 경로·SHA256·역할 목록. 동결 후 변경은 새 회차다. | 불일치 10 |
| a / b | `192.168.0.61`의 pc-a, `192.168.0.74`의 pc-b, SSH 사용자 `jacking75NoteBook`. A/B SID·Session·Default는 최신 관측과 일치해야 한다. | 잘못된 대상 10 |
| dtdExe / dtdConfig | `F:/github/DesktopTestDriver/bin/live_Release/Controller/Controller.exe`, 기존 `F:/github/KnockHB/.artifacts/dtd-e2e-20261020/Controller.json` 경로·hash. 외부 저장소를 임의 수정하지 않는다. | 부재/지원 부족 10 |
| signalingOrigin / viewerUrl / tls | origin=`https://192.168.0.61:25001`, 같은 origin의 준비된 Viewer URL, 실제 서버 identity/SAN·유효기간·SHA256·신뢰 검증 증거. 머신 Root 등록/전체 TLS 오류 무시를 준비 단계의 기본값으로 넣지 않는다. 지원되는 검증 경로가 없으면 실행 전 차단한다. | 미검증 10 |
| staging / owner | B의 검증할 `C:/Users/jacking75NoteBook/RemoteAssist-E2E-<숫자>` staging과 예정 보호 owner root·task·watchdog·manifest hash. 실제 owner 생성 뒤 Arm 전에 일치를 확인한다. 기존 SSH transport의 허용 경로 형식과 맞아야 한다. | 경로/owner 오류 10 |
| ownedResources | A/B process PID/image/start/handle·생성 Session, 서비스 ImagePath/owner, 시험 폴더/파일·포트·예약 작업 이름·원래 상태. 불명확한 PID나 재사용된 PID를 종료하지 않는다. | 모호성 10/실행 뒤20 |
| uiRules | 아래 4.4절의 selector/실제 창 owner·요소 종류·유일성 규칙. 미래 창의 HWND를 미리 만들지 않는다. 실행 시 스크립트가 현재 응답으로 결합한다. | 0개/여러 개면 다음 조작 금지 |
| observations / recovery | 이 문서 7절의 모든 필드 제공 adapter·버전·출력 경로와 8절의 독립 복구 수단·실측 시간 증거. `null`/추정값은 준비 완료가 아니다. | 10 |

설정은 비밀값을 넣지 않는 UTF-8 JSON이다. 비밀번호·개인 키·TURN/operator secret의 내용은 환경/기존 보호 파일에서만 읽는다. SSH는 기존 transport의 등록 호스트 키와 환경 인증을 재사용하고 credential/전체 환경을 출력하지 않는다. PairingCode/PairingPin은 실제 B UI에서 읽어 현재 runId에 묶은 비공개 임시 값으로만 사용한다. 아래의 `Context`는 config, runId, 단계, 단조 clock/grant, DTD 세션, owner, 관측기·evidence 경로를 담는 실행 중 객체다.

### 4.3 재사용할 실제 도구와 한계

| 실제 파일 | 재사용할 기능·정확한 입력 | 한계/확장 조건 |
|---|---|---|
| `scripts/e2e-run-contract.ps1` | manifest/hash/경로/owner 검사, 원자적 create-once ledger | 현재 `lan-view-consent-stop` 고정이다. 이 C의 config·허용 mask/profile을 검증하도록 명시적으로 확장하고 닫힌 검증을 유지한다. 검사 삭제/강제 우회 금지다. |
| `scripts/e2e-native.cs` | E2eOwnedCommand의 소유 Job, 절대 exe+분리 인자, monotonic Deadline/Remaining | 계층/Job 소유 및 KILL_ON_JOB_CLOSE를 유지한다. 셸 문자열 조합으로 임의 프로세스를 종료하지 않는다. |
| `scripts/e2e-local-session.ps1` | RunDir/ManifestSha256, Controller/Watchdog/Recovery 역할의 A 수명 감독 | 현재 Certificate Add/Remove를 직접 호출하며 예외 상세가 부족하다. TLS·heartbeat·복구 결함 수정 없이 원본을 그대로 실행하지 않는다. |
| `scripts/e2e-product-arm.ps1`, `scripts/e2e-product-guard.ps1` | 현재 owner·보호 설치·B SYSTEM 감독·보수적 clock grant | 현재 profile과 단발 auth가 C01용이다. 해당 C 지원을 검증한 버전만 호출한다. Prepare/기동을 PASS로 세지 않는다. |
| `scripts/e2e-product-client.py` | `arm/status/finish/collect/evidence --campaign <현재 절대경로>` | `observe`는 legacy 차단이다. 다른 C의 권한/관측이 지원된다고 추정하지 않는다. 모든 호출은 단계 상한으로 감싼다. |
| `scripts/e2e-ssh-command.py` | `--script <검토한 로컬PS1> --campaign <현재manifest> --staging <현재B경로> --output <새A파일> --timeout <남은초>` | 이 C의 읽기/소유 stop·수집 코드만 전달한다. 기존 호스트 키/인증 재사용, bounded output, 알려진 staging 검증을 유지한다. |
| `scripts/e2e-product-status.ps1`, `evidence.ps1`, `collect.ps1` | 현재 회차 상태·원본 증거·자원 부재 관측 | collect의 recoveryVerified=false는 실제 복귀 미검증이다. 파일 목록에 없는 감사/필드가 수집된다고 가정하지 않는다. |
| `scripts/e2e-resource-inspect.ps1` | `-Output <새절대경로> -Thumbprint <검증한40hex>` | LocalMachine Root만 읽는다. CurrentUser 부재·desktop·clipboard·제품 held는 따로 검사한다. |
| `scripts/e2e-viewer.cjs` | 현재 A DOM 프레임/상태와 signaling WS 타입 관측 | DC/held/worker/진단/TURN 전체 관측이 아니다. 7절 필드가 없으면 observer를 확장하고 사전 검증한다. |

`product-e2e-harness.ps1`/`invoke-product-local`의 차단된 과거 Arm을 대체 실행 경로로 호출하지 않는다. C01 실패 회차의 고정 launch/manifest/시간 origin도 복사 실행하지 않는다.

### 4.4 DTD 명령·응답과 UI 선택 규칙

```powershell
$DtdExe = 'F:/github/DesktopTestDriver/bin/live_Release/Controller/Controller.exe'
$DtdConfig = 'F:/github/KnockHB/.artifacts/dtd-e2e-20261020/Controller.json'
# 호출 형식 참조이며 이 문서 보강 작업에서는 실행하지 않는다.
& $DtdExe --config $DtdConfig --status --machine pc-a
& $DtdExe --config $DtdConfig --status --machine pc-b
& $DtdExe --config $DtdConfig --session-open --machine pc-a --mode control --idle-seconds 900
& $DtdExe --config $DtdConfig --session-open --machine pc-b --mode control --idle-seconds 900
& $DtdExe --config $DtdConfig --request $RequestJson
& $DtdExe --config $DtdConfig --request-status $ControlSessionId $OriginalRequestId
& $DtdExe --config $DtdConfig --session-close $ControlSessionId
```

두 session-open 응답의 controlSessionId를 pc-a/pc-b에 각각 결합한다. `data.desktop=Default`, `inputAvailable=true`, API1 및 필요한 supportedOperations가 있어야 한다. 이전 관측 A Session1/B Session2와 다르면 입력 전에 SID/현재 콘솔을 재검토한다.

GUI 요청 파일의 정확한 envelope는 아래와 같다. requestId는 매 동작의 새 GUID N이다. 예시 문자열은 실제 실행값이 아니다. request JSON은 UTF-8로 파일에 직접 저장하고 stdout/셸 history에 비밀 text를 노출하지 않는다.

```json
{"apiVersion":1,"requestId":"<new-guid-n>","controlSessionId":"<actual-session-id>","operation":"controls","timeoutMilliseconds":5000,"arguments":{"windowId":"<observed-window-id>"}}
```

| operation | arguments | 진행 조건 |
|---|---|---|
| windows | `{}` | 실제 title/pid/imagePath/createdAt/foreground/windowId를 읽는다. |
| controls | `windowId` | 실제 elementId/automationId/name/controlType/enabled/value를 읽는다. 없는 ToggleState/체크 상태를 기본 false로 가정하지 않는다. |
| focus | `windowId` | foreground=true 뒤에만 click/type/key를 보낸다. |
| click | `windowId`와 `elementId` 또는 `automationId` 중 하나 | element가 유일·enabled이고 현재 owner여야 한다. 좌표 click은 position의 현재 captureId가 필요하다. |
| type-text | `windowId,text` | 클릭해 focus한 실제 입력칸을 다시 읽는다. 길이1..4096 UTF-16, surrogate 손실 없음. |
| key/chord | `windowId,key,action` 또는 `windowId,keys[]` | action=press/down/up. 도구 down 최대1500ms, 해당 C에 정한 조합만 사용한다. |
| capture | `windowId,output` | 새 A 파일에 실제 이미지와 captureId/hash/유효시각을 얻는다. 기존 파일 덮어쓰기 금지다. |
| drag/wheel | drag=`windowId,captureId,start,end,button,steps`; wheel=`windowId,position,delta` | 창 클라이언트 물리 픽셀 기준이다. position={x,y,captureId}. 실제 영상/모니터 변환을 적용한다. |
| release-input | `{}` | 이 DTD 세션의 소유 입력만 해제한다. 제품/Recovery 해제 PASS 증거가 아니다. |

응답 status=completed만 해당 도구 동작 완료다. rejected/failed는 첫 실패, running/completionUnknown/응답 유실은 원래 request-status와 실제 상태를 한정 시간 안에 조회한다. **변경 요청을 새 ID로 자동 재전송하지 않는다.** 변경 결과가 미확정이면 기능을 중단하고 복구한다. 읽기 관측은 새 ID로 반복할 수 있지만 전역/단계 deadline을 연장하지 않는다.

DOM `#id`와 WPF x:Name을 DTD AutomationId로 추정하지 않는다. 실제 명시적 WPF AutomationId는 직접 결합할 수 있다. 그 밖에는 owner 창의 label/name·ControlType·부모 컨테이너·유일성을 사용한다. checkbox 상태나 PIN을 실제 도구가 읽을 수 없으면 지원되는 비공개 관측을 준비하고 검증하기 전까지 BLOCKED다. blind click 또는 허용 mask를 임의 바꿔 진행하지 않는다.

### 4.5 구현할 로컬 함수·오류 계약

개별 DTD timeoutMilliseconds는 min(5000, 단계/active 남은 밀리초)로 계산한다. 출력 상한은 기존 SSH transport와 같은1048576 bytes, 이번 action/관측 ledger 상한은4096건으로 고정하고 초과는 실패다. 거대한 원문·전체환경을 덤프하지 않는다.

아래 이름은 실제 코드에 연결해야 할 함수 계약이다. 같은 기능을 기존 함수로 재사용해도 입력/출력/실패 의미를 바꾸지 않고 매핑을 기록한다.

| 함수 | 입력 → 반환 | 필수 동작 |
|---|---|---|
| ReadCaseConfig | path, expectedCase → Config | 필수키·case/mask·실제 절대경로·hash·외부 자원 검증. null/예시값 거부. |
| BeginCase | Config → Context | create-once run/lock·action ledger·전역 origin, 독립 guard identity. 이전 실패 회차 재사용 금지. |
| StartRuntimes | Context → OwnedRuntime | A 검증된 signaling/Viewer/관측기 ready 뒤 B 소유 서비스/Tray를 한 번 시작한다. 준비된 작업 등록/기동도 origin 안에서 감독한다. |
| OpenPairing | Context → PrivatePairingBinding | 실제 B HostStart 1회→9자리코드/4자리PIN 비공개 읽기→A 이름/코드/PIN 실제 UI 입력→접속1회. 승인 창은 아직 승인하지 않는다. |
| GrantCapabilities | Context, fixed mask → ApprovalBinding | 새 실제 창의 체크 상태를 확인하고 필요한 권한만 순서대로 선택·승인1회. mask0은 거부 전용이며 approve하지 않는다. admin 요청허용 뒤 실제2차승인은 별도다. |
| InvokeDtdOnce | Context, machine, operation, arguments, stepId → DtdResult | 위 JSON/응답 계약과 소유·focus 확인, 변경 intent를 flush한 뒤 실행, 같은 action 재전송 없음. |
| WaitObservation | Context, source, predicate, stepDeadline → Snapshot | 7절의 실제 metadata를 조회한다. 필드없음/overflow/원천오류는 성공값으로 대체하지 않는다. 50~250ms 유한 polling은 가능하나 고정 sleep 후 성공 추정은 불가다. |
| AssertCase | Context, assertionId, actual, expected, evidence → Assertion | 관측 원본/같은회차/현재세대/조건을 검증, false 또는 unverified면 첫 실패를 latch한다. |
| StopAndRecover | Context, firstFailure → Recovery | 8절 전부를 try/finally와 독립 guard로 수행한다. 원래 기능 실패/시간은 보존한다. |
| WriteCaseResult | Context, assertions, Recovery → Result | PASS는 모든 필수 assertion=true와 독립 복귀=true인 경우뿐이다. stdout은 비밀 없는 요약이다. |

첫 단계가 기동/접속/승인을 포함하면 `StartRuntimes→OpenPairing→GrantCapabilities(3)`를 그 단계 내부에 코드로 넣는다. C01은 표의02/03/05에서 각각 pairing발급/Viewer참가/승인을 분리한다. C02는 승인하지 않고 거부한다. C11은 clipboard baseline owner 준비를 먼저 한다. 어느 단계도 실행자에게 다음 명령을 자유롭게 작성하도록 넘기지 않는다.

### 4.6 이번 C의 실제 UI 선택 규칙

| 대상 | 선택값 | 필수 검증 |
|---|---|---|
| B 준비 | RemoteAssist 격리 입력 시험 / 소유 서비스 ImagePath | 실제 service PID/image/start/handle과 dev-owner를 사전 고정한다. |
| A Viewer | #screen focus 후 key F12 down | A→제품→B 경로다. B DTD key down으로 대체하지 않는다. |
| B 독립 trigger | 예약 ACK와 B F12 down을 기다리는 소유 단발 fault observer | 실제 조건을 확인해 서비스 한 개만 종료한다. 원격 CLI 왕복 뒤 수동 kill을 하지 않는다. |

### 4.7 이번 C의 고정 입력과 fixture 계약

스크립트 내부의 고정 채널 상수는 `['control', 'input', 'pointer']`이고 `expectVideo=true`다. 이는 승인 뒤의 기대 집합이며 승인 전 허용을 뜻하지 않는다. 관측 source가 비어 있으면 실제 채널0으로 취급하지 않는다.

| 입력 키 | 고정값/준비할 실제 값 | 검증 규칙 |
|---|---|---|
| faultKey / faultCount | F12 / 1 | 다른 장애·다른 키를 추가하지 않는다. |
| faultService / serviceExe / startTick | RemoteAssistHost / 실제 보호 설치 image / 현재 생성 시각 | 이름만 맞는 프로세스는 대상이 아니다. |
| faultLagLimitMs / recoveryObserveLimitMs / toolHoldMaxMs | 500 / 3000 / 1500 | 앞의 두 값은 사전 실증할 계획 상한이다. DTD 자동 up과 순서를 구분한다. |
| targetExe / faultObserverExe / hashes | 준비된 실제 B 경로와 hash | observer 구현·상한 검증이 없으면 사전 BLOCKED다. |

단발 trigger는 입력을 보내지 않는다. 예약 ACK→native down→서비스 crash→생산자 차단→Recovery up은 B 단조 시각으로 결합한다. A의 DTD 자동 up보다 먼저 서비스/채널이 닫혔는지 별도 인과 증거를 남긴다. 실패해도 안전 up은 즉시 수행한다.

## 5. 단계별 실행표

표의 시간은 고정 sleep이 아니라 해당 준비 신호/결과를 기다리는 최대 계획값이다. 전체 active 300초 안에서만 진행하며, 단계 상한과 남은 전역 시간 중 작은 값을 적용한다. 이 표의 예산은 사전 실측 증거를 연결하기 전에는 실행 허가가 아니다.

| 단계 | 동작 | 진행 조건·관측·즉시 실패 조건 | 계획 상한 | 증거 |
|---|---|---|---|---|
| 01 | 일반 제어 승인·baseline | B target foreground, held=0, Recovery의 현재 Helper 바인딩, 서비스/Helper/Recovery 소유 handle 준비. | 공통 준비/승인 140초 내 | fault-owner.json / target-before.json |
| 02 | B 단발 fault observer 준비 신호 | 현재 회차·서비스 신원·예약 ACK/down 조건·소비 0회·유한 deadline 확인. 이 준비는 서비스 장애가 아니다. | 10초 | fault-armed.json |
| 03 | A 영상 focus에서 F12 down 1회 | 제품 recovery-reserve ACK가 native down보다 앞서고 B f12Down=baseline+1이다. | ACK→down→fault <=500ms 목표, 실측 검증 필수 | reservation-order.ndjson / target-down.json |
| 04 | B 조건 충족 후 소유 서비스 장애 1회 | fault 도구가 신원을 재검증해 서비스 프로세스 한 개를 종료한다. 이때 B f12Up이 증가하지 않았어야 한다. | down부터 500ms 내 상한 제안·사전 실증 전 금지 | fault-fired.json |
| 05 | 독립 Recovery의 생산자 차단/up 관측 | 서비스/Helper가 종료되고 Recovery가 up을 보낸다. 주입 전 A 일반 keyup/DTD 자동 해제로 복원된 경우 PASS가 아니다. | fault 뒤 3초 내 계획 상한·사전 실증 필수 | recovery-order.ndjson / target-after.json |
| 06 | A 도구 up 정리·OS held=0·세션 종료 | 안전 해제는 반드시 수행하되 원래 복원 증거와 구분한다. B Default/동일 SID, 새 Helper/무동의 재개 부재를 확인한다. | 10초 | tool-release.json / fault-after.json |
| 07 | 증거 보존·소유 설치 정리 | 기존 서비스가 자동 재시작되지 않았는지 확인하고 독립 finish/recovery/collect를 수행한다. | 20초 내 수집 후 공통 정리 | audit.ndjson / final-recovery.json |

### 5.1 실행 상태와 전역 시간 계약

`NEW→VALIDATED→PREPARED→RUNNING→FUNCTION_CHECKED→STOPPING→RECOVERED→REPORTED` 순서다. 첫 실패는 `RUNNING→STOPPING`으로 바로 이동한다. RUNNING으로 되돌아가거나 다른 시나리오를 붙이지 않는다. 실행 스크립트는 고정 step 목록을 순차 호출하고 case당 한 회차만 소비한다.

- origin은 첫 제품/Viewer/신뢰/서비스 변경 전에 A QPC로 한 번 만든다. `activeEnd=origin+300s`, `cleanupEnd=origin+540s`, `recoveryEnd=origin+840s`, `hardEnd=origin+900s`다. 목표는 보고까지600초이며 예비는 기능 시간에 빌려 쓰지 않는다.
- 단계 deadline은 `min(해당 단계 진입QPC+단계상한, activeEnd)`다. 제품 고유 ACK/lease 상한이 더 짧으면 그것을 우선한다. 5초 부재 관측은 timer 기반 이벤트 감시이고, 5초 sleep 뒤 값 한 번 읽기가 아니다.
- B grant는 B QPC 샘플과 A 수신 시각의 남은 예산을 이용해 보수적으로 계산한다. A/B UTC를 빼서 지연·선후관계를 만들지 않는다. 같은 B ACK/exit 순서는 B QPC로 판정한다.
- 독립 A/B watchdog identity와 소유 Job을 기능 시작 전에 확인한다. controller heartbeat10초 초과·guard 상실·activeEnd 도달은 기능 중단이다. heartbeat 파일 replace/read 공유 충돌을 먼저 고치며 무제한 파일 재시도로 시간을 연장하지 않는다.
- 모든 child/SSH/SCM/수집 호출은 `min(남은 phase 예산, 개별 상한)`과 출력 상한으로 감싼다. 과거 transport의25초나 certificate45초를1초 ACK 판정에 그대로 사용하지 않는다. 더 늦게 읽더라도 판정은 저장된 실제 발생 시각으로 한다.
- 이 시간은 실행 정책/계획 상한이다. 실제 observer·중단·설치 제거·복귀의 사전 관측 근거가 연결되기 전에는 실행 준비 완료가 아니다. 제품 성능 실측값으로 주장하지 않는다.

### 5.2 단계와 구현 함수의 일대일 매핑

각 함수는 `(Context, StepDeadlineTick) → {status, evidencePaths, observationIds}` 계약이다. DTD/native/원격 입력 호출은 내부에서 위의 단발 함수를 사용한다. 모든 단계의 입력은 config·이전 단계의 검증된 반환값이고, 자연어로 새 명령을 받아 실행하지 않는다.

| 단계 | 구현할 함수 | 연결할 동작 |
|---|---|---|
| 01 | `Invoke-C16-OpenControlRecoveryTarget` | 일반 제어 승인·baseline |
| 02 | `Invoke-C16-ArmOneShotFault` | B 단발 fault observer 준비 신호 |
| 03 | `Invoke-C16-SendRemoteF12Down` | A 영상 focus에서 F12 down 1회 |
| 04 | `Invoke-C16-ObserveVerifiedServiceCrash` | B 조건 충족 후 소유 서비스 장애 1회 |
| 05 | `Invoke-C16-ObserveIndependentRecoveryUp` | 독립 Recovery의 생산자 차단/up 관측 |
| 06 | `Invoke-C16-ReleaseToolAndVerifyNoResume` | A 도구 up 정리·OS held=0·세션 종료 |
| 07 | `Invoke-C16-PreserveAndRecoverFaultResources` | 증거 보존·소유 설치 정리 |

`try` 안에서 위 순서대로 호출하고, 예외/거부/미확정/시간초과 시 첫 실패를 한 번만 기록해 다음 step을 실행하지 않는다. `finally`에서 StopAndRecover와 WriteCaseResult를 호출한다. controller 강제 종료 때도 독립 guard가 같은 소유 정보와 원래 deadline으로 정리한다.


## 6. PASS / FAIL 판정

- 예약 ACK→실제 B down→서비스 장애 1회→생산 Helper 차단→Recovery up의 인과 순서가 독립 단조 시각으로 확인된다.
- fault 당시 F12가 실제로 held였고 제품 일반 keyup/도구 자동 해제가 복원 원인이 아니다.
- OS held=0·제품 생산자 종료·세션 종료·무동의 재개 부재·동일 사용자 Default 복귀를 모두 확인한다.
- 진단 저널 장애로 SessionEnded 기록이 누락되면 관측 결과와 감사 한계를 분리하고 누락을 PASS로 꾸미지 않는다.

필수 증거 하나라도 없으면 PASS가 아니다. 준비 신호 누락·잘못된 대상·예상하지 않은 권한·채널/감사 오류·시간 초과는 첫 실패로 즉시 중단한다. 자원/담당자/관측기가 실행 전에 없으면 BLOCKED, 실행에 들어간 뒤 고정 결과를 못 얻으면 FAIL이다.

## 7. 증거 수집과 판정표

아래 파일명은 이번 회차에서 생성·검증할 증거 계약이며 현재 존재한다고 주장하지 않는다. 실제 파일 경로/크기/SHA256와 판정에 사용한 필드를 result.json에 연결한다.

- `fault-owner.json`
- `fault-armed.json`
- `reservation-order.ndjson`
- `target-down.json`
- `fault-fired.json`
- `recovery-order.ndjson`
- `target-after.json`
- `tool-release.json`
- `fault-after.json`
- `final-recovery.json`

추가 공통 증거: readiness.json, frozen-inputs.json, action-ledger.ndjson, local-start.json, 양쪽 단조 시각/clock grant, finish/cleanup, independent-recovery.json, result.json. 순서·시간 비교는 같은 PC의 QPC 또는 명시적 clock handshake를 사용하고 A/B UTC를 빼서 지연을 만들지 않는다.

### 7.1 기록 스키마와 실제 관측 필드

관측 adapter의 표준 레코드는 `{runId,caseId,source,machine,sessionId,stepId,requestId,connectionGeneration,tick,frequency,available,payload}`다. 시간은 Int64/명시한 frequency로 보존한다. 해당 source가 주지 않는 값은 null/available=false다. 서로 다른 PC의 tick을 정렬하는 대신 검증된 protocol ID/순서 또는 clock grant를 쓴다. 아래 표의 표준 필드명은 adapter 구현 계약이며 제품이 이미 같은 JSON을 내보낸다는 뜻이 아니다.

| 관측 필드 | 실제 원천 | 필수 기대값 | 현재 상태/구현 주의 |
|---|---|---|---|
| reservation.id,revision,ackTick / nativeDownTick / target.f12Down | B 예약 ACK metadata·OS fixture | ACK<nativeDown, down delta1 | 예약 원문/Unicode를 일반로그에 남기지 않는 관측이 필요하다. |
| fault.consumedCount,pid,image,startTick,tick / f12HeldAtFault | 단발 trigger와 B OS 독립 관측 | 1, 현재 identity / true | fault 당시 이미 up이면 실패다. |
| producerFencedTick,recoveryUpTick,toolUpTick / osHeld / newHelperCount | B Recovery와 A 도구 해제 상관관계 | 생산자 차단 뒤 Recovery up / 0 / 0 | 도구 자동 up이 실제 복원 원인이면 PASS하지 않는다. |
| return.sid,session,desktop / sessionEnded / serviceRestarted | 독립 B OS·A 종료·SCM | 동일 사용자/Session2/Default / true / false | 진단/감사 누락은 관측으로 꾸며 채우지 않는다. |

### 7.2 감사 해석

B 원본은 보호된 `%ProgramData%/RemoteAssist/audit/audit.ndjson`이다. install owner/runId를 검증하고 현재 SessionId/ConnectionId/PermissionGeneration, 해당 기능의 Operation.Id로 제한한다. B 서비스 제거 전에 현재 시나리오의 감사와 service-status를 보존하고 종료/flush 여부를 확인한다.

실제 JSON 키는 `EventId,Utc,Kind,Result,SessionId,ConnectionId,PermissionGeneration,Operation`이다. 현재 enum은 숫자다. Kind: SessionCreated0, ConsentGranted1, ConsentDenied2, SessionEnded3, AdminGranted4, AdminDenied5, AdminRevoked6, FileStarted7, FileFinished8, RunbookStarted9, RunbookFinished10, SasRequested11. Result: Allowed0, Denied1, Stopped2, Completed3, Failed4. 숫자는 후보의 AuditJournal enum과 대조하며 문자열 이벤트를 기다리지 않는다.

| 이번 C의 Kind | Result | 현재 session/operation의 기대 개수 |
|---|---|---|
| 1 | 0 | 1회 |
| 3 | 2 | 필수 종료 감사. crash로 실제 누락되면 미충족으로 기록 |

프로세스 기동·기능 승인·종료 같은 필수 감사와 독립 OS 관측을 혼동하지 않는다. 필수 감사가 실제 누락됐으면 결과는 미충족이다. 감사 기록용 임의 이벤트/권한 JSON을 주입하지 않는다.

### 7.3 결과·증거 완전성

`action-ledger.ndjson`은 변경 전 intent와 응답을 `{caseId,runId,stepId,requestId,operation,targetOwner,tick,status,inputEventsSent,inputReleased}`로 flush한다. text/PIN/비밀번호/원래 clipboard는 넣지 않는다. 원래 요청이 미확정이면 이를 그대로 보존한다.

`result.json`은 `{caseId,runId,previousRun,candidateSha256,toolManifestSha256,result,firstFailureStep,startTick,endTick,frequency,actualSeconds,screenOccupiedSeconds,assertions,evidence,cleanup,recovery,unexecutedSteps}`를 가진다. `assertions[]`는 `{id,stepId,expected,actual,status,evidenceRefs}`이고 status는 PASS/FAIL/UNVERIFIED다. `evidence[]`는 `{relativePath,sha256,bytes,source}`다. cleanup은 자원 부재, recovery는 독립 Default/SID/입력/각 C 복구를 별도로 기록한다. 파일 부재·unknown은 false/UNVERIFIED이며 PASS로 바꾸지 않는다.

모든 필수 assertion=PASS, 필수 증거가 실제 존재/동일회차/hash일치, cleanup.resourcesAbsent=true, recovery.verified=true, 전체900초 이내일 때만 최종 PASS다. 시작 전 blocker는 BLOCKED이며 기능 실행0을 기록한다. 시작 뒤 첫 실패는 FAIL, 안전 복구 미확정은 FAIL_RECOVERY_REQUIRED다. 이미 정리됐다는 레코드 하나만 읽어 독립 복귀를 생략하지 않는다.


## 8. 중단·정리·독립 복귀

Recovery의 up 증거가 없어도 안전 복구는 즉시 수행한다. A release-input/소유 Helper 차단/현장 키 해제를 사용하되 본 기능은 FAIL로 유지한다. held/잠금/잔존 생산자가 미해결이면 FAIL_RECOVERY_REQUIRED이며 다른 시험을 금지한다.


정리 실패는 FAIL_RECOVERY_REQUIRED다. 안전 복구가 기능 결함을 가려서는 안 되며, 원래 실패 기록은 보존한다.

### 8.1 이 회차의 중단·정리 순서

| 순서/구현 함수 | 실행 명령·입력·동작 | 확인할 결과·상한 |
|---|---|---|
| R1 RequestStop | 첫 실패/단계/QPC 기록. A `<RunDir>/finish.json`을 먼저 create-once. 그 뒤 `python scripts/e2e-product-client.py finish --campaign <현재manifest>`를 유한 호출한다. | SSH 성공이 A 정리의 선행조건이 아니어야 한다. 과거 runDir를 쓰지 않는다. |
| R2 ReleaseOwnedInput | 현재 DTD 세션에 operation=`release-input`, arguments=`{}`. 제품은 HostStop/현재 owner의 stop/Recovery로 해제한다. | DTD inputReleased=true는 도구 입력에 한정한다. C03/C05/C16 제품/OS 해제 증거를 따로 판정한다. |
| R3 StopOwnedRuntime | 실제 등록된 소유 guard·Job/서비스 stop을 호출한다. 각 PID/image/start/handle·dev-owner·서비스 ImagePath를 재확인한다. B 복구는 같은 manifest와 guard의 Role Recovery 경로를 쓴다. | 서비스/Helper/AdminHelper/worker/기능 창·A Viewer/signaling의 소멸. 정리 phase 잔여와 검토한 SCM/child 상한으로 제한한다. |
| R4 RestoreCaseState | 아래 C 전용 복구와 소유 파일/임시 신뢰를 복원한다. 소유 NTFS 경로는 정규화해 회차 루트 내부임을 검사한다. | clipboard 등 원래 사용자 상태 복원을 최우선으로 한다. 사용자 파일·기존 인증서·공유 relay 전체를 삭제하지 않는다. |
| R5 InspectIndependently | A/B에 `e2e-resource-inspect.ps1 -Output <새파일> -Thumbprint <고정40hex>`를 읽기 전용 실행하고 DTD/현장/VM으로 SID/Session/Desktop/입력을 확인한다. | serviceAbsent, productDirectoryAbsent, ownedProcesses=[], A listener25001=0. LocalMachine와 CurrentUser Root의 시험 신뢰 부재는 각각 확인한다. 같은 SID/Session의 Default와 제품/도구 held 검증이 필요하다. |
| R6 RetireAndClose | 독립 복귀 증거를 먼저 기록한 뒤 동일회차 watchdog/task를 identity 확인해 종료·해제한다. DTD `--session-close <현재id>`를 A/B에 각각 호출한다. | inputReleased=true, applicationsStopped=true, 비소유 앱 유지. guard를 복귀 전에 없애지 않는다. |
| R7 Report | WriteCaseResult로 첫 실패·기능·cleanup·recovery·시간·미실행 step을 보고한다. | 목표600초/절대900초에 결과 전달까지 포함한다. 자동 새회차/재시도 없음. |

정상 종료는 origin+540초 안에 cleanup, 예비 복구는 origin+840초 안에 끝내고 보고60초를 남긴다. 900초를 넘겨도 남은 위험 자원을 방치하지 않고 안전 복구를 계속하되 deadline 위반과 FAIL_RECOVERY_REQUIRED를 숨기지 않는다. 외부 담당자 도움은 지정된 연락/콘솔 절차만 사용한다.

cleanup/recovery는 별도 검증된 소유 lock과 원자적 ledger로 동시 실행을 조정한다. Abort 재호출은 같은 정리 상태 확인/이어받기이며 시나리오 재시작이 아니다. 정리 도중 새 manifest/새 origin/새 PID 대상으로 바꾸지 않는다. 사용자 확인 창이 갑자기 필요하면 입력을 즉흥적으로 추가하지 않고 준비된 독립 복귀 절차를 사용한다.

### 8.2 복구 명령 연결과 독립 판정

기존 A 감독의 `-Role Recovery -RunDir <현재RunDir> -ManifestSha256 <현재hash>`, B 보호 감독의 같은 인자를 재사용하되 현재 코드의 reserve 요구·오류/정리 결함을 먼저 수정·검증한다. B 호출은 등록 host key를 사용하는 `e2e-ssh-command.py --script <검토한복구PS1> --campaign <현재manifest> --staging <현재staging> --output <새A결과> --timeout <남은상한>`으로 전달한다. 결과 유실은 완료로 추정하지 않는다.

`Collect`가 반환하는 resourcesAbsent=true와 recoveryVerified=false를 그대로 보존한다. 독립 확인 전에는 recovery.verified=true를 쓰지 않는다. B DTD는 UAC/잠금/Winlogon 조작을 지원하지 않으므로 그 구간 복귀는 해당 C의 실제 현장/VM 수단이 필수다. 이 문서 8절의 복구 함수/필수 증거가 구현돼 있지 않으면 Run 진입을 막는다.


## 9. 준비 완료 조건과 현재 미해결

- G06 비밀 없는 예약 ACK/실제 down/Recovery up 인과 관측
- G05 독립 F12/OS held 기록
- G04 1500ms 도구 up과 경합하지 않는 단발 fault 경로 및 상한 실증

현재 상태는 **NOT READY**다. 위 항목을 해결하고 입력 없는 사전 점검 결과·실측 시간·명령 hash를 연결한 뒤 reviewer가 준비 완료 여부를 기록한다. 문서 생성·정적 검토만으로 READY/기능 PASS를 부여하지 않는다. 외부 자원이 필요한 항목은 담당자/자원이 확보되지 않으면 그 ID만 BLOCKED로 둔다.

### 9.1 이 문서만으로 확인할 공통 구현 완료 조건

- `scripts/e2e-C16.ps1`의 Action/인자/종료 코드가 4.1절과 같고, 5.2절의 모든 step 함수와 7절 assertion·8절 복구가 실제 코드로 연결돼 있어야 한다. 현재 함수가 없으면 문서만으로 READY라고 하지 않는다.
- heartbeat replace/read 공유 충돌 재현을 수정하고 원래 watchdog 예외의 phase·예외형·줄·정리 상태를 비밀 없이 보존한다. 파일 공유 오류를 무제한 retry하거나 guard를 끄는 수정은 불가다.
- TLS 신원 검증 경로를 준비하고 기존 도구의 매회 Root Add/Remove 의존을 제거/분리한다. 검증을 무조건 허용하거나 개인 작업 PC의 신뢰 상태를 임의 변경하지 않는다. 필요한 경로 미지원이면 명시적 BLOCKED다.
- 각 필수 관측 필드의 actual source·버전·poll/상한·예상 스키마·예외/누락 처리가 구현돼 있어야 한다. capture/held/worker/ACK 관측이 없는데 UI enabled/서비스 시작으로 성공을 대신하지 않는다.
- read-only/offline fixture로 JSON 파싱, 현재 mask/지원 채널, 잘못된 대상/모호한 selector 거부, action 중복 방지, 단계 timeout/전역 origin 불변, 첫 실패 다음 단계 미실행, 미확정 응답 재전송 금지, owner 불일치 정리 거부, cleanup 실패의 결과 우선순위를 검증한다. 테스트가 자기 구현 문자열만 비교하는 검사가 되지 않게 실제 helper/분기 함수를 사용한다.
- 사용자 당일 중단 지시가 유지되는 동안 제품·실제 GUI 입력·서비스·UAC·잠금 시험은 하지 않는다. 입력 없는 검증과 실제 실증을 구분하고, 실측 예산 증거가 없으면 해당 준비 상태를 남긴다. 문서 검증 결과를 실전 PASS로 세지 않는다.

### 9.2 구현자가 남길 완료 매핑

각 step별로 `이 문서 단계 → 실제 scripts/e2e-C16.ps1 함수/줄 → 사용하는 기존 helper/adapter → 입력 없는 검증 이름/결과 → 남은 실전 준비`를 표로 기록한다. 계획의 함수 이름과 구현 이름이 다르면 이 문서를 실제 코드 기준으로 갱신한다. 함수명만 만들고 빈 본문/고정 PASS를 반환하는 것은 미구현이다. 이 문서의 필수 동작/증거가 코드 없이 다른 문서로 다시 위임된 상태는 구현 완료가 아니다.


### 9.3 이 C의 입력 없는 판정·오류 검증 벡터

아래는 구현할 판정 함수/step executor에 주는 메모리·파일 fixture다. 실제 Controller/Agent·제품·입력·SCM·인증서·잠금/SAS를 호출하지 않는다. 외부 호출 경계는 기록용 sink로 대체하되 실제 AssertCase/step 순서/StopAndRecover 분기를 실행해 결과와 호출 횟수를 확인한다. 이 검증은 실전 기능 PASS가 아니다.

| 벡터 | 입력/오류 | 기대 결과 |
|---|---|---|
| 정상 | B ACK<down<fault<fence<Recovery up, fault당시held, 도구up이복원원인아님, 같은사용자Default·held0 | PASS |
| 잘못된 타이밍 | DTD1500ms 자동up이먼저 전달됐거나 fault consumedCount2 | FAIL, 다시 down/crash하지 않음 |
| 미확정/안전 | fault PID identity불일치 또는 Recovery up증거없고 held잔존 | identity불일치면 kill0; held잔존이면 즉시복구·FAIL_RECOVERY_REQUIRED |

## 10. 구현 근거와 다음 회차 인계

- [src/RemoteAssist.Host.Core/Input/RecoveryLedger.cs](../src/RemoteAssist.Host.Core/Input/RecoveryLedger.cs)
- [src/RemoteAssist.Host.Service/RecoveryScope.cs](../src/RemoteAssist.Host.Service/RecoveryScope.cs)
- [src/RemoteAssist.Host.Recovery/Program.cs](../src/RemoteAssist.Host.Recovery/Program.cs)
- [src/RemoteAssist.Host.Core/Input/WindowsRecoveryBackend.cs](../src/RemoteAssist.Host.Core/Input/WindowsRecoveryBackend.cs)
- [tools/RemoteAssist.InputTarget/Program.cs](../tools/RemoteAssist.InputTarget/Program.cs)

다음 구현자는 이 문서 전체와 해당 ID의 마지막 result를 읽고 구현한다. 위 실제 소스 링크는 구현 위치를 확인하기 위한 코드 근거이며 다른 계획서를 읽어 호출/복구 규칙을 보충할 필요는 없다. 실패 회차는 재사용하지 않는다. 실제 시작/종료·화면 점유·첫 실패·정리·복귀 시각과 유효한 증거를 기존 ID에 기록한다. 오늘은 실행하지 않는다.

<!-- case-implementation-20261021:start -->
## 11. 2026-10-21 코드 연결·입력 없는 검증 결과

현재 대상은 C01~C14·C16의 15개다. C15/TURN collector는 이 사례의 외부 조건이 아니다.

**실행 구현 연결 / 실제 E2E 미실행이다.** 진입점·고정 단계·실제 source adapter·bootstrap·정리·독립 복귀를 연결했다. 아래 코드는 4절의 파일 미생성 상태와 이전 부분 구현 기록을 갱신한다. 입력 없는 검증 통과를 기능 PASS나 현장 실행 준비 완료로 세지 않는다.

- 진입점: [e2e-C16.ps1](../scripts/e2e-C16.ps1). `Validate`는 로컬 설정 검사다. `Prepare`는 사전 증거 검사·파일 동결·DTD status·신규 v4 패키지 배치·비기동 준비를 수행한다. 증거/외부 조건이 부족하면 `BLOCKED/10`이다. 이번 작업에서는 Prepare와 Run을 호출하지 않았다.
- 공통 실행: [e2e_case_runtime.py](../scripts/e2e_case_runtime.py)의 `Context.begin/once`, `execute_steps`, `stop_and_recover`, `write_result`. 문서의 `Invoke-C16-...` 함수들은 아래 `CASES`의 불변 `Step` 정의와 공통 executor로 구현 위치를 변경했다. 순서/상한/관측 predicate/증거 목록은 코드 상수다.
- 실제 호출: [e2e_case_adapters.py](../scripts/e2e_case_adapters.py)의 `Dtd`, `CaseBackend.action`, `Harness.call`, `OwnedGateway`. 기존 `e2e-native.cs`의 Job과 `e2e-product-client.py`의 소유 manifest transport를 재사용한다. selector 0/복수개·owner 불일치·미확정 요청은 실패하며 임의 명령 입력 경로는 없다.
- 실제 관측: [e2e_case_live.py](../scripts/e2e_case_live.py)의 `LiveObserver`, [e2e_case_sources.py](../scripts/e2e_case_sources.py)의 `assemble/read`, [e2e_case_observations.py](../scripts/e2e_case_observations.py)의 `derive`. 브라우저 메타데이터, 현재 제품 trace/audit, DTD 소유 창, OS/fixture 증거를 조합한다. 누락을 기대값으로 대체하지 않는다.
- `Run -Execute`는 `verify_frozen`과 `readiness`를 통과한 회차만 `execute_steps`에 연결한다. `Abort`는 먼저 stop을 기록하고 기존 실행기의 복구를 기다리며, 실행기 종료가 확인된 경우에만 `finish_existing`이 동일 origin/소유 session으로 복구한다. `Collect`는 저장 증거 재검사이며 현재 데스크톱 복귀 증명이 아니다.
- 부속 구현: [e2e_case_campaign.py](../scripts/e2e_case_campaign.py)의 `package_campaign/Campaign`, [e2e_case_desktop.py](../scripts/e2e_case_desktop.py)의 `DesktopSources`, [E2eDesktop Program.cs](../scripts/e2e-desktop/Program.cs), [ClipboardSnapshot.cs](../scripts/e2e-desktop/ClipboardSnapshot.cs), [e2e_case_recovery.py](../scripts/e2e_case_recovery.py)의 `finish_existing`. 기존 하네스·DesktopTestDriver API1을 재사용한다.

| 계획 단계/이름 | 실제 정의 | 고정 조작 → 관측/predicate | 단계 상한 | 수집할 증거 |
|---|---|---|---|---|
| 01 / OpenControlRecoveryTarget | [CASES["C16"][0]](../scripts/e2e_case_steps.py#L268) | `open-session` → `baseline` | 140초 | `fault-owner.json`, `target-before.json` |
| 02 / ArmOneShotFault | [CASES["C16"][1]](../scripts/e2e_case_steps.py#L269) | `arm-owned-fault` → `armed` | 10초 | `fault-armed.json` |
| 03 / SendRemoteF12Down | [CASES["C16"][2]](../scripts/e2e_case_steps.py#L270) | `video-key` → `down` | 5초 | `reservation-order.ndjson`, `target-down.json` |
| 04 / ObserveVerifiedServiceCrash | [CASES["C16"][3]](../scripts/e2e_case_steps.py#L271) | 읽기 관측 → `fault` | 5초 | `fault-fired.json` |
| 05 / ObserveIndependentRecoveryUp | [CASES["C16"][4]](../scripts/e2e_case_steps.py#L272) | 읽기 관측 → `recovery-order` | 10초 | `recovery-order.ndjson`, `target-after.json` |
| 06 / ReleaseToolAndVerifyNoResume | [CASES["C16"][5]](../scripts/e2e_case_steps.py#L273) | `release-input` → `return` | 10초 | `tool-release.json`, `fault-after.json` |
| 07 / PreserveAndRecoverFaultResources | [CASES["C16"][6]](../scripts/e2e_case_steps.py#L274) | 읽기 관측 → `end` | 20초 | `audit.ndjson`, `final-recovery.json` |

위 모든 단계의 **순서·각 단계 첫 실패 중단**은 `RuntimeTests.test_all_case_step_orders_and_first_failure_at_every_step`에서 실제 executor를 기록용 sink로 실행해 확인했다. 전체 16개 정상 순서와 103개 실패 지점, 합계119개 경로다. 이 검사는 live 관측 source나 UI의 실행 성공을 검증하지 않는다. 이 C의 주요 판정 벡터는 `PredicateTests.test_c16_fault_identity_order_and_tool_up`로 검증했다. 각 step의 모든 live 필드를 검증한 것으로 확대하지 않는다.

검증은 [test_e2e_cases.py](../scripts/test_e2e_cases.py)의 37개, [test_e2e_case_live.py](../scripts/test_e2e_case_live.py)의 13개, [브라우저 observer 테스트](../tests/e2e/browser-observer.test.cjs)의 5개를 통과했다. 실제 executor의 119개 경로와 live source의 run/sequence/partial record/ACK 인과관계/외부 중단/후보 hash 계약을 검증했다. 제품 전체·E2eDesktop 컴파일과 PowerShell/Python/JavaScript 구문 검사 결과는 [전체 검토·결과](e2e-case-implementation-review-20261021.md)에 연결했다. 테스트는 실제 DTD/SSH/제품/SCM/GUI/UAC/잠금을 호출하지 않았다.

### 11.1 이 C에 남은 미구현·미검증·외부 자원

- candidate/tool hashes and measured phase budgets require prior evidence
- independent watchdog, cleanup and external return require witnessed validation
- A/B existing-chain TLS identity and desktop identity require preparation

고정 조작 opcode에 남은 stub/임의 명령 콜백은 없다. C03 fixture/포커스 이탈 창, C11 STA OLE 복원 helper, C16 SYSTEM 단발 소유 프로세스 fault trigger도 구현했다. **실제 A/B 통합·UI selector·OCR·TLS 체인·종단 시간·복구 시간은 미검증이다.** C11은 완전 복제할 수 없는 OLE medium을 만나면 변경 전에 중단한다. C04~C06의 현장 담당자는 외부 자원이다. C15 collector는 향후 재포함 시 조건이며 이번 범위에서 제외한다. 소스 필드가 없거나 실제 제품이 기대 audit/복구 증거를 만들지 않으면 FAIL이며 PASS로 보정하지 않는다. 시간표는 코드 상한이고 실측 예산 증거가 없으면 Arm하지 않는다.

기존 heartbeat Save/Read는 공유 잠금으로 경로조회/open/replace 전체를 직렬화했다. 입력 없는 경쟁 검사에서3572회 읽기를 통과했지만 원래 C01 RuntimeException과 동일 원인이라고 단정하지 않는다. watchdog 예외는 phase/role/정리 여부/예외형 체인/HResult/파일/줄을 남기며 비밀이 들어갈 수 있는 메시지 원문을 출력하지 않는다. SYSTEM/SSH와 사용자 세션 사이 전체 통합 복구는 여전히 미검증이다.
### 11.2 현재 source 필드·생산자·비동기 경계

각 단계 source는 위 단계 표와 `derive(case, source)`의 실제 분기에 연결된다. 7.1절의 표준 관측 필드와 아래 사례별 원본 필드 집합을 함께 사용한다.

| 항목 | 현재 계약 |
|---|---|
| 원본 필드/생산자 | `fault.runId/caseId/servicePid/serviceStart/fixturePid/nativeDownTick/faultTick/frequency; trace.reservation-ack/native-input.metadata.revision/f12Down; trace.producers-fenced/recovery-native-up/recovery-up; metadata.f12HeldBefore; trace.pid/processStartUtcTicks/generation; inventory and os return` |
| 정상 도착을 기다리는 경계 | bound ACK, real down, one owned service death, same Recovery fence/native up |
| 회차·세대·상관 결합 | runId + sessionId/connectionId + permission generation; per-operation id/revision; producer pid/start |
| tick 단위 | browser performance.now/frequency=1000; B native QPC/own frequency; A perf_counter_ns/frequency=1e9. Cross-domain ticks are not subtracted. |
| 부분 기록 | NDJSON 마지막 newline 미도착만 Pending. 완성 행의 오류·중복 key·다른 run·중복/역순 sequence는 즉시 FAIL이다 |
| 관측 상한 | 원래 step deadline과 origin을 유지한다. 조작 재전송과 새 timeout은 없다. 허가/순서/ID/실제 SLA 위반은 즉시 FAIL이다 |

추가 회귀는 [잔여 작업 검사](../scripts/test_e2e_case_remaining.py), [OLE 메모리 검사](../tests/RemoteAssist.E2e.Clipboard.Tests/Program.cs), [감사 경쟁 검사](../tests/RemoteAssist.Host.Core.Tests/AuditRegression.cs)에 연결한다. 최신 검사 수·hash·진입점은 [잔여 작업 완료 기록](e2e-remaining-work-20261022.md)에서 확인한다. B/C 실전 결과와 별개다.
<!-- case-implementation-20261021:end -->
