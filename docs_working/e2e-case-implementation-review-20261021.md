# C01~C16 실행 스크립트 구현·검토 기록 — 2026-10-21

- [x] RV01. C09 파일 거절과 세션 종료 대기를 분리하고 정상 거절 흐름을 검사했다.
- [x] RV02. 고정 권한에 따른 영상·캡처·입력 금지를 모든 사례에 적용했다.
- [x] RV03. C03/C11 종료 감사 판정과 원본 증거를 연결했다.
- [x] RV04. 준비 증거 손상과 독립된 소유 회차 Abort를 구현했다.
- [x] RV05. 기한 초과 뒤에도 유한 안전 복구를 수행하고 시간 위반을 유지했다.
- [x] RV06. 범위·상세 절차·도구 매핑·환경 준비 자료를 맞추고 입력 없는 전체 회귀를 완료했다.

## 2026-10-22 리뷰 지적 보완

RV01~RV05의 코드 수정과 입력 없는 회귀를 반영했다. 아래 최초 리뷰는 결함 발견 당시 기록으로 보존한다. 현재 상태도 실제 DTD/A/B/TLS/UI·시간·복귀 실증이 없어 NOT READY이며 C15는 EXCLUDED다. 실제 E2E·제품·서비스·UAC·잠금·입력·clipboard·SSH·CI는 실행하지 않는다.

| 보완 | 현재 동작·검증 연결 |
|---|---|
| RV01 | C09의 declined는 세션 종료를 기다리지 않는다. C02의 접속 거절과 audit/end/final/revoked만 Viewer 복귀를 기다린다. 정상 거절→다음 단계 HostStop 흐름을 합성 source로 검사했다. |
| RV02 | ID별 예외 목록을 고정 mask로 바꿨다. screen이 없으면 track/frame/srcObject/capture, control이 없으면 inputEnabled/native-input을 즉시 실패로 판정한다. 미디어·입력 위반을 각각 주입하고 허용된 screen/control 경로도 검사했다. |
| RV03 | C03은 held 확인 뒤 HostStop·감사 단계, C11은 원본 복원 뒤 종료 감사 단계를 추가했다. 현재 session/connection/permission generation의 동의·종료 감사 각 1회가 필요하다. 빈/누락/중복/다른 연결·세대·결과를 거부하고 audit 미도착은 원 deadline까지만 기다린다. |
| RV04 | Prepare가 recovery-config 사본을 frozen hash에 묶고 Run도 무결성을 검사한다. Abort는 실행 전제 파일들을 다시 검사하지 않고 소유 campaign/run/launch 확인 뒤 로컬 stop을 먼저 기록한다. 사본 또는 이전의 hash 동일 원 config만 사용한다. 원래 실행기 생존 중 동시 복구 금지와 기존 request ID 재조회 규칙을 유지한다. |
| RV05 | 원 origin/시간 위반은 유지하고, 지난 phase deadline에는 호출별 최대 10초의 안전 복구 상한을 적용한다. 하위 product client의 recovery-only도 stop/읽기 4종으로 한정했다. 원격 collect의 과거 deadline 차단을 제거했다. 기한을 넘긴 결과는 FAIL_RECOVERY_REQUIRED다. |
| 추가로 확인한 복구 누락 | Prepare→Run에서 lifecycle 순번이 1로 재시작되어 Abort 파싱을 깨던 문제를 수정했다. 기존 owner ledger를 읽어 연속 순번을 사용한다. 제품 기동 전 실패도 독립 복귀 자료를 보존한 뒤 준비 task를 unprepare하고 DTD 세션을 닫는다. |

관련 검사는 [리뷰 회귀](../scripts/test_e2e_case_review.py)에 있다. 실제 transport/OS 경계는 대체했다. 원격 PowerShell collect 변경은 실행하지 않고 코드 리뷰·Parser.ParseFile로 확인하며 실제 상한과 복귀의 입증은 B02에 남긴다. 수정된 스크립트의 hash가 포함되므로 이전 readiness/UI/도구 증거로 새 회차를 Arm할 수 없다.

Run은 finish marker 또는 launch-once가 이미 있는 준비를 재사용하지 않는다. Abort로 준비 task를 회수한 뒤 같은 회차가 다시 DTD 세션을 여는 경로도 차단했다. C15의 기존 회차 정리 경로는 유지하지만 Validate/Prepare/Run 제외는 그대로다.

### 현재 보완 검증과 인수인계

2026-10-22 11:43 KST의 소스 확인 뒤 아래 결과를 연결했다. C#/Go/브라우저 제품 코드는 변경하지 않았으며 빌드·publish·실제 준비/실행을 다시 수행하지 않았다. 기존 C# 후보·도구는 파일 검사만 수행했다.

| 현재 검사 | 결과 |
|---|---|
| Python 기존 4개 검사군 + 새 리뷰 회귀 | 37 + 13 + 3 + 30 + 18 = **101개 통과**. 정상 16개·단계별 실패 105개, 합계 121개 executor 경로를 포함한 합성 검사 |
| Node 브라우저 관측기 | 8개 통과. 실제 브라우저·네트워크·파일 창을 열지 않는 가짜 객체 검사 |
| 구문 | PowerShell 54개 / Python 29개 / Node 2개, 오류 0. PowerShell script 실행 없음 |
| 문서 매핑 / 태스크 개요 | 16개 생성 구역 / 116개 태스크 일치. C03 9단계·C11 8단계 및 감사 원본 연결 |
| 기존 로컬 후보·도구 | source/파일 hash 일치, payload 497개·도구 26개·FFmpeg DLL 7개. 기존 manifest hash는 최초 리뷰 기록과 같음 |
| 신규 config 생성 / inspect | 서로 다른 새 UUID 15개, run 디렉터리 생성 0. C15 없음. DTD exe·설정/신원/TLS/UI/현장 담당자·C11 lifetime 누락을 표기하며 DRAFT_NOT_READY/exit10, 실측 7종 누락과 functionalActions=0 유지 |
| 코드/문서 자체 검토 | RV01~RV05, lifecycle 연속 순번, 기동 전 task 회수, 중단/소비된 회차 재실행 차단을 관련 회귀와 대조. 실제 환경 증거로 확대하지 않음 |

검증 요약은 `.artifacts/e2e-rv-20261022-114030/validation.json`, 새 설정·중단 명령 인수인계는 같은 디렉터리의 `configs/draft-report.json`, 준비 검사 결과는 `inspect.json`이다. 이 파일들은 Git에 포함하지 않는 현재 개발 머신의 산출물이며 실행 허가가 아니다.

남은 항목은 실제 DTD 실행본·설정, A/B SID/session·기존 신뢰 TLS endpoint, UI/locale/owner/capture, 후보·현재 도구에 결합한 7종 실측, C11의 실제 OLE 원본 복원·helper 종료 뒤 유지, C04~C06 현장/VM 복귀 담당자다. C16 실제 fault 인과와 각 사례의 기능 PASS도 미실행이다. 이 값들을 추정하거나 verified=true로 채우지 않았다. 실행 금지 범위에서 보완 가능한 코드·문서·검사·신규 draft는 완료했으며 **활성 15개의 실전 준비 상태는 NOT READY**로 유지한다.

## 2026-10-22 실행 준비 재검토 — NOT READY

검토 시각은 시스템 `Get-Date`로 확인한 2026-10-22 10:37 KST다. 검토 기준은 `b4b306cc1b68b4482b220db43634121a26c90ab4`이며 착수 시 작업 트리는 깨끗했다. C01~C16 상세 절차, 통합 색인, 최신 잔여 작업/인수인계, 실행·관측·준비 증거·중단·복구 스크립트와 기존 로컬 산출물을 대조했다.

**준비 완료 0개, 준비 미완료 15개, 범위 제외 1개(C15)다.** 아래의 기존 구현 완료 체크와 과거 검사 통과는 이번 재검토에서 발견한 결함의 해소나 실제 실행 준비 완료를 뜻하지 않는다. C15는 현재 색인과 코드의 제외 결정을 유지하며, 이번 사용자의 16개 검토 요청을 TURN 재포함 지시로 해석하지 않았다.

이번에는 E2E, Prepare/Run/Abort의 실제 실행, DTD/SSH 연결, 제품/Viewer 기동, 서비스 설치·조작, UAC/SAS/잠금, 실제 입력·사용자 clipboard, CI를 수행하지 않았다. 아래 재현은 외부 호출을 대체한 메모리 데이터·가짜 clock·임시 파일만 사용했다. **실제 제품의 권한 누출이나 복구 실패를 실증한 결과가 아니라 시험 도구의 판정·복구 결함을 확인한 결과다.** 제품·시험 스크립트는 수정하지 않고 이 리뷰 문서만 갱신했다.

### 새로 확인한 스크립트 결함

| ID / 우선순위 | 영향 | 확인한 문제와 코드 근거 | 실행 전에 필요한 조치 |
|---|---|---|---|
| RV01 / P1 | C09 | [await_completion](../scripts/e2e_case_observations.py) 63~64행이 `source=denied`에 `joinVisible=true`를 요구한다. C09는 4단계에서 파일 거절만 확인하고 HostStop은 [CASES](../scripts/e2e_case_steps.py) 221행의 5단계에서 수행한다. 정상적인 파일 거절로 세션이 유지되면 앞 단계가 `Pending(viewer-end)`로 대기해 다음 종료 단계에 도달하지 못한다. | C02의 접속 거절과 C09의 파일 거절 완료 조건을 분리한다. 세션을 유지한 정상 declined 응답이 4단계를 완료하고 5단계에서 종료되는 관측 흐름을 입력 없이 검사한다. |
| RV02 / P1 | C07~C11, C13 | [invariants](../scripts/e2e_case_observations.py) 34~35행의 무영상·무캡처·무입력 검사가 C02/C12에만 적용된다. 해당 6개는 screen/control 권한이 없지만 승인 뒤 `frames=3`, `capture-start`, `native-input(count=1)`이 함께 있는 합성 기록도 invariant를 통과했다. 채널 whitelist와 승인 전 검사만으로 승인 후 미허용 미디어/입력 동작을 판정하지 못한다. | 고정 권한 mask에 따라 승인 후에도 video track/frame/capture/native-input 금지를 계속 검사한다. 실제로 도착한 위반은 Pending으로 숨기지 않고 첫 실패로 끝내며 각 해당 사례에 음성 벡터를 추가한다. |
| RV03 / P1 | C03, C11 | 각 상세 계획 7.2절은 ConsentGranted/Allowed와 SessionEnded/Stopped를 각 1회 요구한다. 그러나 [C03 최종 단계](../scripts/e2e_case_steps.py) 179행은 HELD만 검사하고 감사 파일도 요구하지 않는다. C11의 236행은 복원 boolean만 판정하고 audit.ndjson은 수집 대상으로만 둔다. [derive](../scripts/e2e_case_observations.py)의 C03 held/C11 restored는 빈 audit에서도 최종 predicate를 통과했다. C11은 빈 감사 파일이 존재하기만 해도 이 누락을 가려낼 판정이 없다. | 세션 종료 뒤 현재 session/connection/permission generation의 필수 감사 개수·종류·결과를 공통 최종 판정에 연결한다. C03도 원본 감사 증거를 보존하고, 누락·중복·다른 세션/세대의 감사는 PASS를 거부한다. |
| RV04 / P1 | 모든 활성 사례의 명시적 Abort | [runtime.main](../scripts/e2e_case_runtime.py) 600행에서 공통 `read_config`를 먼저 실행하고 611행에서 Abort로 분기한다. 이 검사는 현재 candidate hash와 DTD/config/sourceEvidence 파일 존재를 요구한다. 이 중 하나라도 사라지거나 바뀌면 stop marker 생성·`finish_existing` 진입 이전에 종료한다. `missing-file(sourceEvidence)`를 주입한 재현에서 exit10, 복구 함수 호출 0회, finish.json 생성 0개였다. | 실행 허용용 준비 검사와 기존 회차의 중단·복구에 필요한 최소 소유권 검사를 분리한다. 검증된 원 회차에 stop을 먼저 기록하고 원 origin/owner/세션으로만 복구한다. 변경된 후보를 새로 실행하는 우회 경로는 만들지 않는다. |
| RV05 / P1 | 모든 활성 사례의 기한 초과 복구 | [stop_and_recover](../scripts/e2e_case_runtime.py) 294행과 305행은 복구 deadline을 항상 `origin+900초` 이하로 자른다. 이미 900초가 지났으면 deadline이 과거가 되어 gateway/transport 직전의 `remaining()`에서 실패한다. origin 이후 901초 가짜 clock에서 finish/release/stop/restore/inspect/close에 전달된 deadline 모두 과거였고 외부 호출 전달은 0회였다. 문서의 “900초 초과도 안전 복구 계속, 결과는 FAIL_RECOVERY_REQUIRED” 계약과 다르다. | 기존 origin과 시간 위반 판정은 유지하되 기한 후 안전 복구 호출에는 별도의 짧은 유한 호출 상한을 준다. 기능 실행·재시도는 허용하지 않고 입력 해제·소유 정리·독립 복귀만 가능해야 한다. 이는 Python 복구 경로의 결함이며 별도 watchdog 전체의 실제 실패를 뜻하지 않는다. |

RV01~RV05는 코드 수정과 관련 입력 없는 회귀·재리뷰가 먼저 필요한 항목이다. 기존 83개 Python 검사의 통과로 이 항목을 닫지 않는다. 각 검사군은 가짜 payload/transport와 개별 predicate를 사용하므로 실제 source 조합 전체를 검증했다는 뜻이 아니다.

### 공통 준비 차단과 사례별 판정

로컬에서 지정 DTD 저장소 `F:/github/DesktopTestDriver`, `bin/live_Release/Controller/Controller.exe`, 기존 `.artifacts/dtd-e2e-20261020/Controller.json`은 모두 존재하지 않았다. 원격 A/B에는 접근하지 않았으므로 원격 부재로 확대하지 않는다.

`.artifacts/e2e-remaining-configs-20261022-v3/draft-report.json`의 활성 15개는 모두 `DRAFT_NOT_READY`다. 공통으로 DTD 설정, TLS/local 설정, 실제 A/B SID/session, sourceEvidence, uiEvidence가 빠져 있다. C04는 UAC trigger hash와 recovery.operator, C05/C06은 recovery.operator도 미확보다. 필수 실측 7종은 monotonicDeadline, independentWatchdog, boundedScm, boundedUac, cleanup, externalReturn, tlsIdentity다. 현재 inspect는 이 7종 누락과 config-field-required를 출력하며 기능 동작 0개로 차단한다.

아래 표의 “공통”은 현재 A/B 신원·DTD·TLS·UI/locale/owner/capture, 후보/도구에 결합한 7종 실측과 시간 예산, 독립 정리·복귀 증거 및 RV04/RV05를 뜻한다. 기능 실행 이력과 준비 상태는 별개다. C01의 과거 도구 실패는 기능 PASS가 아니며 새 회차를 실행하지 않았다.

| 사례 | 현재 준비 상태 | 공통 이외 실행 전 확인할 핵심 사항 |
|---|---|---|
| C01 화면·종료 | NOT READY | 실제 marker/monitor/frame 증가, 표시·HostStop 1초·종료 감사, 과거 RuntimeException의 후속 통합 확인 |
| C02 동의 거절 | NOT READY | 승인 전부터 누적 영상·입력·채널 0, 실제 거부·종료·재개 없음 |
| C03 일반 입력·해제 | NOT READY | RV03, 실제 fixture 포커스/좌표/drag/wheel/F12/reset ACK와 OS held |
| C04 관리자·UAC·해제 | NOT READY | 현장/VM 복귀 담당자·수단, 서명 trigger hash, 실제 OCR/secure desktop/권한 회수 |
| C05 SAS·복귀 | NOT READY | 담당자·독립 복귀, 실제 Windows 보안 화면 분류와 Viewer 취소 |
| C06 잠금·복귀 | NOT READY | 동일 사용자 로그인 담당자·콘솔, WTS 잠금·권한 폐기·무동의 재개 없음 |
| C07 A→B 파일 | NOT READY | RV02, 실제 저장 승인·offer ID·최종 NTFS 길이/hash·partial 부재 |
| C08 B→A 파일 | NOT READY | RV02, 실제 native picker·writer.close·receipt·최종 파일·감사 |
| C09 파일 거절 | NOT READY | RV01/RV02, 실제 declined와 본문/저장 0, 세션 종료는 다음 단계 |
| C10 양방향 채팅 | NOT READY | RV02, 양쪽 실제 UI의 Unicode 텍스트·정확히 1회 표시 |
| C11 명시적 clipboard | NOT READY | RV02/RV03, 실제 OLE baseline 전체 지원·독립 복원·helper 종료 뒤 유지 증거 |
| C12 진단 전용 | NOT READY | 실제 무캡처/무입력과 OS 대조, provider N/A/stale 표시 |
| C13 Runbook 승인 | NOT READY | RV02, 설치/catalog/dialog/실행 hash, 일반 권한 worker 정확히 1회·종료·감사 |
| C14 승인 대기 취소 | NOT READY | worker 0, 실제 창 닫기 ACK→새 epoch/입력 재개 순서와 취소 후 클릭 |
| C15 TURN | EXCLUDED | 기존 제외 결정 유지. 검토 대상에는 포함했으나 이번 준비 실패/PASS 집계에 넣지 않음 |
| C16 서비스 장애 | NOT READY | 실제 ACK→down→소유 fault→fence/up 인과, DTD 자동 up 배제, 재시작 없음·정상 복귀 |

### 절차 문서 정합성

P2 문서 문제도 남는다. [통합 색인](e2e-acceptance-plan-20261021.md) 선두는 15개/C15 제외지만 1절과 종료 조건에는 여전히 “실행할 시험 16개”, “C01~C16 모두 PASS”가 있다. 현재 15개 종료 조건으로 본문을 맞추고 기존 16개 명세를 보존한 범위를 구분해야 한다. 여러 상세 문서 7행의 “bootstrap/source/독립 복구 미완료”는 이후 코드 연결 기록보다 오래됐으며, 미구현과 구현 후 미실증을 다시 구분해야 한다. 이 재검토는 범위를 늘리거나 기존 실전 게이트를 승격하지 않는다.

### 이번에 직접 수행한 입력 없는 확인

| 확인 | 현재 결과 |
|---|---|
| 기존 Python 검사 4개 파일 | 37 + 13 + 3 + 30 = 83개 통과. 프로세스/네트워크 경계를 대체한 검사 |
| `node --test tests/e2e/browser-observer.test.cjs` | 가짜 DOM/WebSocket/Peer/native picker를 사용하는 8개 통과. 브라우저 기동 없음 |
| PowerShell Parser.ParseFile | scripts/e2e*.ps1 54개, 구문 오류 0. dot-source·실행 없음 |
| Python ast.parse | scripts의 e2e-/e2e_case_/test_e2e_case 계열 28개, 구문 오류 0 |
| `e2e_case_artifacts.py` | 현재 source/payload/도구 hash 일치. 후보 497개, 도구 26개, FFmpeg DLL 7개. LOCAL_ARTIFACTS_VERIFIED_NOT_READY |
| `update-e2e-case-mappings.py --check` | 16개 생성 구역 일치. 문서-코드 링크 일치이며 동작 검증 아님 |
| `update-task-overview.py --check` | 116개 태스크 일치. E/V/F/R30 수용 체크 변경 없음 |
| 현재 C01 draft evidence inspect | NOT_READY/config-field-required, 필수 실측 7종 누락, functionalActions=0 |
| RV01~RV05 합성 재현 | 정상 파일 거절 대기 오류, 미허용 캡처/입력 invariant 누락, 빈 감사 최종 predicate 통과, Abort 이전 차단, 900초 이후 과거 복구 deadline을 확인 |

후보 manifest SHA-256은 `94457430cfa22827be65047613b8a3139cf9ee8c075b9f0fed5c2ccac0504929`, 도구 build-result SHA-256은 `4318be8e53d96b9dc6a2a12b26b43a52b771bde5e2d8af62aa0e456709a62c66`으로 기존 기록과 일치했다. 빌드·게시를 다시 수행하지 않았다. 다음 순서는 **RV01~RV05 수정·입력 없는 회귀·재검토 → 문서 정합성 보완 → 별도 허용 범위의 B01~B04 준비/실증 → 사례별 Prepare/READY 검토**다. 이번 검토에서 B/C 실행을 시작하지 않는다.

## 2026-10-22 후속 구현·검증

[잔여 작업 완료 기록](e2e-remaining-work-20261022.md)의 A01~A12에 OLE 독립 복제/복원, 부분 기동 회수, 비동기 관측, 단일 deadline, 실측 증거 생성, UI/파일 관측, C16 crash audit와 인수인계를 반영했다. 아래 기존 기록은 당시 상태다. 최신 검사 개수·산출물 hash·제한·고정 절차는 완료 기록 9~11절을 따른다. 실제 DTD/SSH/제품/서비스/UI/사용자 clipboard는 실행하지 않았다. C01~C14·C16은 외부 실측 증거가 없어 NOT READY이며 C15는 EXCLUDED다.

## 2026-10-22 범위 변경

사용자 지시로 C15 TURN을 이번 테스트에서 제외했다. C01~C14·C16은 NAT 환경의 직접 연결을 전제로 한다. coturn allocation collector 미구현은 이번 범위의 차단 조건에서 제외하며, 구현 완료나 TURN PASS로 바꾸지 않는다. C11의 지원하지 않는 OLE medium 및 실제 A/B 실행·시간·복귀 미검증 상태는 그대로다.

`e2e_case_runtime.main`은 C15 Validate/Prepare/Run을 외부 호출 전에 `EXCLUDED/10`으로 종료하고, `e2e_case_readiness.prerequisites`도 C15 준비를 거부한다. Abort/Collect는 기존 회차 정리를 위해 유지한다. 다른 C의 기존 `ForceRelay=false` 후보 검사는 유지한다. 직접 연결 성공은 환경 전제이며 이 변경으로 입증하지 않는다.

입력 없는 검증: [test_e2e_case_scope.py](../scripts/test_e2e_case_scope.py) 3개(설정 접근 전 제외, 준비 검사 차단, 기존 정리·다른 C 진입 유지), 기존 Python 검사 37개와 live 계약 검사 13개가 통과했다. 16개 문서 매핑 일치와 `git diff --check`도 통과했다. 실제 E2E·DTD·SSH·화면 입력은 실행하지 않았다.

16개 상세 문서의 103개 단계를 실행 코드와 실제 관측 어댑터에 연결했다. 이전의 일괄 `live-adapter-blocked` 진입점은 제거했다. **실제 E2E는 실행하지 않았으며 실행 준비 완료나 기능 PASS를 주장하지 않는다.** 신규 후보·현장 복귀·시간 예산 증거가 없으면 준비 검사에서 차단한다.

## 구현 연결

| 파일/함수 | 역할 |
|---|---|
| `scripts/e2e-C01.ps1`~`e2e-C16.ps1` | case가 고정된 Validate/Prepare/Run/Abort/Collect 진입점 |
| [e2e_case_steps.py](../scripts/e2e_case_steps.py) `CASES` | 103개 단계의 조작·관측·판정·예산·증거 목록 |
| [e2e_case_runtime.py](../scripts/e2e_case_runtime.py) `Context, execute_steps, stop_and_recover, write_result` | 단일 origin, 단발 ledger, 첫 실패 중단, 300/540/840/900초 경계, 정리·독립 복귀와 증거 무결성 판정 |
| [e2e_case_campaign.py](../scripts/e2e_case_campaign.py) `package_campaign, Campaign` | v4 manifest, 실제 후보/도구 hash, 고정 A/B 하네스 배치·기동 |
| [e2e_case_readiness.py](../scripts/e2e_case_readiness.py) `readiness, verify_frozen` | 사전 실측·watchdog/SCM/UAC/정리/복귀/TLS 증거와 파일 동결 검사 |
| [e2e_case_adapters.py](../scripts/e2e_case_adapters.py) `Dtd, CaseBackend, Harness, OwnedGateway` | DesktopTestDriver API1·기존 제품 하네스·소유 Windows Job 재사용 |
| [e2e_case_ui.py](../scripts/e2e_case_ui.py) `rules` | 제품 AutomationId와 기본 한국어 selector. 다른 locale/Chrome 제목은 Prepare 전에 고정한 설정에서만 변경 |
| [e2e_case_live.py](../scripts/e2e_case_live.py) `LiveObserver` | 실제 창/프로세스 소유 결합, 동의 상태, runtime 준비, fixture, 정리·복귀·guard retirement |
| [e2e_case_sources.py](../scripts/e2e_case_sources.py) `assemble, read` / [e2e_case_observations.py](../scripts/e2e_case_observations.py) `derive` | 제품·브라우저·OS 기록에서 판정값 구성. 현재 run/session/generation 결합, 미도착/불완전 source 대기, 모순·누락 실패 |
| [e2e-browser-observer.cjs](../scripts/e2e-browser-observer.cjs) `installBrowserObserver` | 실제 WebSocket/DC/selected ICE pair/RAFT/native save picker/writer.close/DOM 관측. 원래 호출을 한 번 전달하고 결과를 관측 |
| [e2e_case_desktop.py](../scripts/e2e_case_desktop.py) `DesktopSources` / [E2eDesktop](../scripts/e2e-desktop/Program.cs) | DTD가 소유한 WinExe의 UIA toggle·OS held/SID/session·WTS·OCR·UAC·단발 fault 관측 |
| [ClipboardSnapshot.cs](../scripts/e2e-desktop/ClipboardSnapshot.cs) | STA 메모리에 OLE baseline 복제, 독립 deadline/finally 복원. 원본 내용/길이/format/hash는 기록하지 않음 |
| [InputTarget](../tools/RemoteAssist.InputTarget/Program.cs) | 고유 영상 marker, 실제 foreground/text/focus, F12·버튼·drag·wheel·held/QPC 증거 |
| [CaseTrace](../src/RemoteAssist.Host.Core/Diagnostics/CaseTrace.cs) | 소유 개발 설치에서만 입력 예약 ACK/native 전이·Recovery·캡처·Runbook 등 메타데이터 기록 |
| [e2e_case_recovery.py](../scripts/e2e_case_recovery.py) `finish_existing` | Abort 시 원래 실행기와 복구 동시 수행 방지, 실행기 종료 후 같은 origin/소유 세션으로 복구 |
| [e2e_case_turn.py](../scripts/e2e_case_turn.py) `observe_turn` | 외부 coturn collector 기록과 양쪽 실제 selected relay 주소·포트·session·generation 상관 확인 |

C04~C06은 지정된 현장/VM 담당자의 준비·복귀 기록과 OS 증거를 함께 요구한다. Windows 로그인 비밀번호나 동의 우회 경로는 없다. C16은 정확한 소유 서비스 handle/image/start를 확인한 SYSTEM 도구가 실제 F12 down을 보고 단 한 번 종료한다. 정상 서비스 재시작이나 공유 coturn 중단 코드는 없다.

## 실행 계약과 필요한 입력

각 wrapper는 `-Action Validate|Prepare|Run|Abort|Collect -RunDir <신규 회차 경로> -Config <동결 설정>`을 받는다. 실제 Run만 `-Execute`가 필요하다. 이번 작업에서 Prepare/Run/Abort나 DTD·SSH를 호출하지 않았다.

설정은 기존 상세 문서의 공통 필드에 `desktopToolDirectory`, `inputTargetDirectory`, `local.signaling/node/python/chrome/tlsCert/tlsKey`, A/B의 실제 SID/session을 추가한다. `uiRules={}`는 코드의 기본 selector를 사용한다. selector 변경은 준비 전 설정에서만 허용되고 PID/start/image는 실제 기동 관측으로 결정한다.

두 시험 도구의 고정 빌드 진입점은 [e2e-case-build-tools.ps1](../scripts/e2e-case-build-tools.ps1)의 `-Output <새 .artifacts 하위 경로>`다. 이 스크립트는 desktop-tool/input-target을 만들고 제품이나 UI를 실행하지 않는다. 후보의 sources hash도 현재 제품 관측 코드와 일치해야 한다.

C07~C09의 파일은 패키지가 생성하는 고정 262144바이트다. A 소스는 `<runDir>/inputs/ra-cNN.bin`, B 소스는 `<staging>/tools/inputs/ra-c08.bin`이다. B 수신은 `<staging>/observer/receive`, A 수신은 `<runDir>/receive/ra-c08.bin`으로 제한한다. 임의 사용자 파일/폴더를 실행 중 지정할 수 없다.

`sourceEvidence`는 `remoteassist-e2e-readiness-v1` 형식이다. caseId, candidateSha256, 현재 도구 파일들의 toolHashes, artifact/sha256/verified를 가진 `checks.monotonicDeadline/independentWatchdog/boundedScm/boundedUac/cleanup/externalReturn/tlsIdentity`, 실제 `measuredBudgets`를 요구한다. 증거 파일이 없거나 hash가 다르면 Arm하지 않는다. 입력 없는 단위 테스트 결과를 실제 복귀/예산 증거로 대신 넣지 않는다.

C04는 실제 서명이 유효한 고정 Windows cmd.exe의 SHA256이 `caseInputs.uacTriggerSha256`과 일치해야 한다. C04~C06은 `recovery.operator/operatorEvidence`가 필요하다. 담당자는 runId/caseId/operator/consoleAvailable/state=READY를 기록하고 실제 복귀 뒤 RETURNED로 바꾼다. 정리 시작 시 `external-recovery-request.json`을 생성한다. OS의 Default·동일 SID/session·held 0을 별도로 확인한다.

C15는 `package-host-dev.ps1 -ForceRelay`로 만든 별도 후보와 실제 coturn이 필요하다. `caseInputs.turnEvidence/relayInstance`는 coturn 운영자가 제공하는 현재 회차 allocation collector 기록이다. provider, relayInstance, collectorReady, relayRunning 및 allocations의 allocationId/sourceRecordId/runId/sessionId/connectionGeneration/relayAddress/relayPort/state를 검사한다. 이 저장소는 외부 coturn 서버에 collector를 설치하거나 운영 권한을 생성하지 않는다.

Prepare는 검증 뒤 신규 archive/manifest를 배치하고 실행되지 않은 소유 task를 등록한다. TLS를 검증하며 루트 저장소를 변경하지 않는다. 사전 검사 실패 시 비기동 task를 회수하고 파일은 검토 증거로 보존한다. Collect는 저장된 증거 hash를 확인하는 기능이며 과거 결과를 현재 데스크톱 복귀 증명으로 바꾸지 않는다.

## 코드 검토에서 수정한 문제

- DTD API1의 실제 elements/launch/capture/session-close 응답과 running/completionUnknown 종료 코드를 소스와 대조했다. 변경 요청 재전송 없이 원래 request ID만 조회한다.
- DTD element bounds는 이미 window-client 좌표다. desktop 원점을 다시 빼던 오류를 수정했다.
- Python perf_counter_ns와 .NET Stopwatch QPC의 단위를 분리했다. B QPC는 A 응답 수신 시 남은 예산만 보수적으로 부여한다.
- C03의 실제 텍스트 포커스·drag 시작점·wheel 합계와 A 포커스 이탈용 소유 fixture를 추가했다.
- 브라우저가 세션 종료 즉시 관측을 끝내던 경로를 고쳐 종료 후 5초간 재개 여부를 볼 수 있게 했다.
- 각 기능 지연은 source의 실제 tick으로 검사한다. HostStop/reset/dismiss/clipboard의 관측·CLI 수행 예산과 1000/2000ms 기능 합격 경계를 분리했다.
- native save picker와 writer.close를 대체하지 않고 실제 호출 완료를 관측한다. 파일 선택/수신 경로는 다시 읽고 소유 폴더·최종 hash로 확인한다.
- 원래 실행기 생존 중 Abort가 같은 자원을 동시에 정리하지 않도록 했다. stop marker 이후 새 기능 mutation을 차단한다.
- OLE 열거 오류를 정상 종료로 처리하지 않는다. COM/UIA/Shell 호출에는 별도 유한 watchdog이 있으며 강제 종료를 관측/복원 성공으로 보고하지 않는다.
- Python CLI가 __main__과 import에서 서로 다른 CaseError 클래스를 생성하지 않도록 모듈을 결합했다.
- heartbeat의 읽기/replace 경쟁은 기존 회차에서 수정한 named mutex 계약을 유지한다. 최초 실패는 복구 오류로 덮어쓰지 않는다.

## 입력 없는 검증 결과

| 검사 | 결과 | 범위 |
|---|---|---|
| `python -B -X utf8 scripts/test_e2e_cases.py` | 37개 통과 | 정상 순서 16개 + 첫 실패 지점 103개 = 119 executor 경로, 주요 predicate/DTD 계약 |
| `python -B -X utf8 scripts/test_e2e_case_live.py` | 13개 통과 | 실제 live 어댑터 source decoding·run/sequence·부분 기록·ACK 상관·candidate·외부 중단·coturn 상관 |
| `node --test tests/e2e/browser-observer.test.cjs` | 5개 통과 | 네트워크 원래 호출 1회, 비밀 배제, RAFT, selected pair, native picker/close 전달 |
| .NET 제품 전체 build | 경고 0 / 오류 0 | 제품·도구·테스트 프로젝트 컴파일. 테스트 실행이나 제품 기동 아님 |
| E2eDesktop build | 경고 0 / 오류 0 | WinExe/COM/UIA/OCR 코드 컴파일. 데스크톱 조작 없음 |
| PowerShell/Python/JavaScript 구문 | 54개 PS, 19개 Python, 2개 JS 통과 | 실제 UI 동작 아님. 16개 문서 매핑 및 git diff --check도 통과 |
| 기존 입력 없는 heartbeat/Windows Job 검증 | 3572회 읽기와 유한 Job 정리 통과 | 이전 작업 결과 유지. SYSTEM/SSH 실증으로 확대하지 않음 |

119개 경로는 실제 executor의 순서/첫 실패/복구 분기 검사이며 실제 103개 E2E 단계 통합 통과가 아니다. predicate는 별도 경계 벡터로 검사했다. live source 검사 역시 저장된 합성 데이터와 가짜 transport를 사용했다. 제품·화면·입력·서비스·UAC·잠금·클립보드·원격 SSH 작업은 실행하지 않았다.

## 미검증·지원 제한·외부 조건

- 고정 action/observer dispatcher의 호출 대상과 복구 메서드는 구현했다. 실행 중 임의 명령을 추가하는 stub이나 callback 입력은 없다.
- **미검증:** A/B 전체 통합, Chrome/Windows UIA selector, 실제 OCR 인식, TLS 신뢰 체인, 300초 화면 점유와 전체 900초 안의 정리·복귀, 실제 파일 저장창, F12 자동 해제와 Recovery 경합.
- **C11 지원 제한:** HGLOBAL/GDI/ENHMF/MFPICT를 비공개 복제한다. 복제 불가능한 IStream/storage/target-device 형식은 제품 변경 전에 중단한다. 전 형식을 복원했다는 실증은 없다.
- **외부 자원:** 유효한 후보·빌드/회귀 증거, 현재 A/B 사용자와 DTD, 기존 신뢰 TLS, C04~C06 현장/VM 복귀 담당자, C15 coturn 및 allocation collector.
- 실제 제품이 기대하는 audit/worker/mapping/Recovery 증거를 생성하지 않으면 해당 단계는 실패한다. 특히 C16 강제 종료 뒤 SessionEnded audit의 실제 존재 여부는 미검증이다. 실행 도구 준비를 기능 PASS로 세지 않는다.
- 본 작업은 E/V/F 수용 상태를 올리지 않는다. 각 C 문서 11절의 코드·검증 링크가 구현 추적 기준이다.
