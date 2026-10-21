# C01~C16 실행 스크립트 구현·검토 기록 — 2026-10-21

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
