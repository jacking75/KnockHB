# 개발 마감용 E2E — 확정 16개와 실행 계획

> **2026-10-22 사용자 지시가 아래 기존 범위보다 우선한다.** 이번 실행 대상은 C01~C14·C16의 15개다. C15 TURN은 `EXCLUDED`이며 PASS나 준비 실패로 집계하지 않는다. NAT 환경에서 직접 연결된다고 가정하며 NAT 통과 자체의 검증을 주장하지 않는다. coturn 운영자·allocation collector·TURN 전용 포트 및 G14는 이번 준비 조건에서 제외한다. 나머지 시나리오의 동의·TLS·시간 예산·정리·복귀 조건은 유지한다. 실제 E2E 실행은 이번 변경에 포함되지 않는다.

작성일: 2026-10-21. **사용자 지시에 따라 오늘 실전 테스트를 더 실행하지 않는다.** 이 묶음은 실행 중 즉흥적으로 단계를 정하지 않기 위한 계획이다. 문서 작성은 실행 준비 완료나 기능 PASS가 아니다. 자동 재개·예약 실행을 설정하지 않는다.

## 1. 범위·상태·실패 이력

이 문서가 확정 시험 범위·실행 상태·개별 상세 계획·공통 실행/복구 절차의 단일 진입점이다. **실제로 실행할 시험은 C01~C16, 총 16개로 고정한다.** V 41개/F 12개는 요구사항 추적표이며 별도로 53회를 실행하지 않는다. N01~N20도 과거 검토 분류로만 보존한다.

한 회차에 한 C만 실행한다. 아래 표가 고정 시나리오·PASS 기준·실행 상태를 함께 관리하며, 연결된 개별 문서가 해당 C의 권한·단계·관측·중단을 구체화한다. 이전 후보의 성공을 현재 후보의 PASS로 옮기지 않는다.

### 반복하지 않는 검사

- 후보가 같은데 매번 반복하던 전체 빌드·자동 회귀·publish·배포. 기존 유효한 성공 증거를 연결한다.
- 기동·동의·감사·정리만 따로 반복하는 시험. 각 C 항목의 공통 수용 조건이며 별도 테스트가 아니다.
- 이미 자동 회귀로 확인한 IPC/프로토콜 변조, PIN/재생·권한 위조, 인코더 폴백, 파일 0바이트/변조/경로 탈출, 비동기 취소 경합의 수동 조합 반복. 후보·변경 영향이 맞는 증거만 재사용한다.
- V/F/N의 같은 동작을 다른 이름으로 다시 실행하는 시험과 마지막 전체 종합 재시험.

### 이번 개발 마감 범위 밖

다음은 이번 16개에 자동으로 추가하지 않는다. 미검증/범위 외로 남기며 해당 기능·환경의 정식 배포 수용을 통과했다고 주장하지 않는다.

- 기존에 제외한 2시간 부하·100회 반복·장시간 누수·지속 복합 부하.
- GPU/VM/OS/브라우저/NAT 전 조합, 역할 교환, 복수 모니터·회전·DPI·음수 원점 조합. 이번은 A/B의 현재 환경과 기본 모니터다.
- 실제 TURN 8분 갱신, TURN TCP/TLS 전 조합, 별도 품질 적응·대역 제한 실측, 모든 장애 지점의 개별 주입. C15는 실제 relay 연결 한 경로다.
- 실제 IME composition 별도 검증. C03의 확정 문자열 전송을 IME 전체 수용으로 확대하지 않는다.
- 깨끗한 VM의 정식 서명 설치·업데이트·전원 차단 rollback·SmartScreen·조직 배포 수용. 개발 설치·제거 성공을 정식 배포 성공으로 바꾸지 않는다.
- 디스크 full·쓰기 장애·모든 파일 크기/저장 API·모든 동의 타이머/거절 경로의 수동 반복. 자동 증거가 없는 조건은 PASS로 바꾸지 않는다.

이는 이번 **개발 마감 검증**의 범위다. 기존 전체 배포 수용 기준의 충족을 선언하지 않는다. 상용/조직 배포 검증을 별도 요청 없이 자동 후속 작업으로 생성하지 않는다.

### 종료 조건

1. 해당 후보/변경 경계에 유효한 자동 회귀 근거와 **C01~C16 모두 PASS**를 확인하면 이번 범위의 개발 검증을 종료한다. 마지막 전체 재시험은 없다.
2. FAIL/BLOCKED/NOT RUN은 PASS가 아니다. C05/C06의 독립 복귀나 C15의 실제 coturn이 없으면 해당 ID만 BLOCKED로 남기고 필요한 자원을 적는다. 개수는 그대로 16개다.
3. 결함 수정 뒤에는 영향받은 기존 ID만 별도 회차로 재시험한다. 재시험은 새 테스트가 아니며 같은 ID에 이력을 연결한다.
4. 새 ID·환경 조합·종합 재시험 추가에는 사용자의 명시적 범위 변경이 필요하다. 발견한 권한 우회·crash·데이터 손실 등은 숨기지 않고 관련 기존 ID의 결함으로 처리한다.
5. 한 회차에는 C 한 개만 실행한다. 첫 실패에서 중단하며 탐색·수정·재빌드·재시도·시나리오 추가를 금지한다. 정리·복귀·보고까지 목표 600초, 절대 900초다.
6. 동의·최소 권한·암호화·감사·입력 해제·소유 자원 정리·실제 복귀는 제거하지 않는다. 정리 실패는 FAIL_RECOVERY_REQUIRED이며 다음 시험을 시작하지 않는다.

### 현재 실행 이력


C01 첫 회차는 실행 도구 실패다. ID `6a2f55ebaee74a4099177a271ea0b88c`, 제품 후보 SHA256 `abca2eec7fa1bab4c6a7167d8420bebdbb2a7376febe4e45d8956abcc8881723`, manifest revision2 SHA256 `f92b333f957b492cee99daa06b940ed7dbed35d463739f314e0bb17a1888ef93`. 해당 launch/manifest는 다시 사용하지 않는다. [실행 결과](../.artifacts/e2e-ready-view-6a2f55ebaee74a4099177a271ea0b88c/result.md)를 보존한다.

제품 후보는 무서명 개발용 payload 497파일이다. 준비 증거는 `.artifacts/e2e-preparation-resume-20261020/`, 회차 원본은 `.artifacts/e2e-ready-view-6a2f55ebaee74a4099177a271ea0b88c/`에 보존한다. C02~C16은 C01 전용 manifest로 실행하지 않고 각 ID의 권한·관측·복귀 조건을 별도로 고정한다.

당시 로컬 watchdog은 RuntimeException 종류만 기록했고 Viewer는 승인 전에 종료됐다. 별도 입력 없는 실제 Save/Read 경쟁 재현에서는 파일 공유 충돌이 발견됐다: [재현 결과](../.artifacts/local-heartbeat-052ebd22c61247caa012e1e01be18e47/result.json). 이 재현은 MethodInvocationException이므로 원래 RuntimeException과 동일 원인이라고 단정하지 않는다. 공유 충돌과 예외 상세 누락을 모두 해결해야 한다.

실패 회차의 A/B 제품 서비스·프로세스·시험 포트·시험 인증서는 제거됐다. A/B Default 및 도구 입력 해제·세션 종료, B 소유 예약 작업 정리를 독립 확인했다. 최초 cleanup 실패 기록은 보존하며 복구 성공으로 시험 실패를 지우지 않는다.

| ID | 개별 상세 계획 | 고정 시나리오·PASS 기준 | 기능 실행 상태 | 주요 준비 조건 |
|---|---|---|---|---|
| C01 | [화면 보기·대상자 종료](e2e-C01-screen-view-stop-20261021.md) | 실제 화면 보기만 승인한다. 승인 전 영상 0·입력 disabled, 승인 후 올바른 B 영상 3프레임 이상과 추가 증가·상시 표시를 확인한다. HostStop 한 번으로 Tray 처리부터 Helper 실제 종료 관측까지 1초 이내, Viewer 복귀와 동의/종료 감사가 있어야 한다. | FAIL — 첫 회차 도구 실패 | G01 heartbeat 공유 충돌 및 원래 RuntimeException의 관계 확인 |
| C02 | [접속 동의 거절](e2e-C02-consent-deny-20261021.md) | 실제 승인 창에서 거부 한 번. 영상·원격 입력 0, 연결 종료, 무동의 재개 없음. | NOT RUN | G03 승인 전부터의 누적 프레임/채널/입력 관측 |
| C03 | [일반 입력·해제](e2e-C03-input-reset-20261021.md) | screen+control 세션에서 버튼 클릭 1회 → `RA-한글😀` 전송 1회 → F12 down/up 1쌍 → drag 1회 → wheel +120 1회 → blur/reset 1회. B 독립 기록과 정확히 일치하고 reset ACK 뒤 제품 소유 held 상태가 0이어야 한다. | NOT RUN | G05 fixture 포커스·wheel delta·drag 독립 계측 |
| C04 | [관리자 승인·UAC·해제](e2e-C04-admin-uac-revoke-20261021.md) | 별도 2차 승인 뒤 준비한 UAC 창을 Viewer에서 취소하고 Default 복귀 후 관리자 모드를 해제한다. 2차 승인 전 특권 실행 0, 승인 뒤 실제 secure desktop 조작, 해제 뒤 특권 입력 거부와 감사가 있어야 한다. | NOT RUN | G07 현장/VM 독립 복귀 담당자·실제 수단 미지정 |
| C05 | [SAS·복귀](e2e-C05-sas-return-20261021.md) | 관리자 승인 뒤 SendSAS 한 번 → 실제 Windows 보안 화면 → 취소 → Default 복귀. 입력 해제와 동일 사용자 세션 복귀를 확인한다. 독립 복귀 수단 없이는 시작하지 않는다. | NOT RUN | G07 독립 현장/VM 복귀 담당자·수단 |
| C06 | [잠금·복귀](e2e-C06-lock-return-20261021.md) | 관리자 승인 뒤 Windows 잠금 한 번 → 허용된 현장/VM 절차로 같은 사용자 Default 복귀. 이전 권한으로 무동의 조작이 재개되지 않아야 한다. 독립 복귀 수단·로그인 주체 없이는 시작하지 않는다. | NOT RUN | G07 로그인 가능한 현장/VM 담당자·복귀 절차 |
| C07 | [파일 A→B](e2e-C07-file-a-to-b-20261021.md) | 고정 256KiB 합성 파일 1개를 B가 건별 수락해 새 시험 경로에 저장한다. 최종 길이·SHA256 일치, 부분 파일 부재, 완료 감사. | NOT RUN | G09 파일 binary·receipt·감사와 최종NTFS 파일 관측 |
| C08 | [파일 B→A](e2e-C08-file-b-to-a-20261021.md) | 같은 규격의 합성 파일 1개를 A가 수락해 새 시험 경로에 저장한다. 최종 길이·SHA256 일치, 부분 파일 부재, 완료 감사. | NOT RUN | G09 A 저장창·stream확정·partial파일 독립 관측 |
| C09 | [파일 건별 거절](e2e-C09-file-deny-20261021.md) | A의 파일 제안 1개를 B가 실제 수신 창에서 거절한다. 본문 수신·저장 파일 0, 전송 종료와 거절 기록. | NOT RUN | G09 수락전 binary누계·거절감사·폴더baseline 관측 |
| C10 | [양방향 채팅](e2e-C10-bidirectional-chat-20261021.md) | A가 `RA-한글😀`, B가 `ACK-RA`를 각각 한 번 보낸다. 상대 UI에 정확히 한 번씩 일반 텍스트로 표시된다. | NOT RUN | G10 도구 stdout UTF-8 보장 및 실제UI 텍스트관측 |
| C11 | [명시적 클립보드](e2e-C11-explicit-clipboard-20261021.md) | 별도 clipboard 권한으로 `RA-CLIP😀`를 한 번 쓰고 한 번 명시적으로 읽는다. 실제 B 클립보드와 응답이 일치하고 자동 왕복이 없어야 한다. 시험 baseline을 복원하며 원래 민감한 내용은 로그에 남기지 않는다. | NOT RUN | G11 B STA clipboard 전체baseline 보관·복원 및 내용없는 검증도구 |
| C12 | [진단 전용](e2e-C12-diagnostics-only-20261021.md) | screen 없이 diagnostics만 승인한다. 영상·캡처·원격 입력 0, 실제 CPU/메모리 정보 수신. 실패 provider는 N/A/stale이며 빈 정상 성공으로 바꾸지 않는다. | NOT RUN | G12 실제captureStartCount0와 telemetry/null/stale 관측 |
| C13 | [Runbook 승인·완료](e2e-C13-runbook-approve-20261021.md) | 등록된 일반 권한 `network-summary` 1건의 내용·hash를 실제 창에서 확인하고 승인한다. 해당 hash 작업이 정확히 한 번 실행되고 완료 결과·감사가 있어야 한다. | NOT RUN | G13 설치resource/승인hash/worker실제실행1회를연결하는관측 |
| C14 | [Runbook 승인 대기 취소](e2e-C14-runbook-pending-cancel-20261021.md) | `network-summary` 승인 창 대기 중 해당 요청 취소. worker 실행 0, 실제 창 닫기 ACK 뒤에만 입력 재개, 이후 시험 버튼 클릭 1회가 B 기록에 +1로 반영돼야 한다. | NOT RUN | G13 worker0·실제닫기ACK·입력재개시각의인과관측 |
| C15 | [실제 TURN 연결](e2e-C15-turn-relay-20261021.md) | 2026-10-22 사용자 지시로 이번 테스트에서 제외. 기존 명세는 향후 검증용으로 보존한다. | EXCLUDED | 이번 준비 조건 아님 |
| C16 | [서비스 장애·입력 복원](e2e-C16-service-fault-recovery-20261021.md) | 일반 제어 세션에서 예약 ACK와 F12 down을 확인한 뒤, PID/image/start time을 검증한 소유 서비스에 장애를 한 번 주입한다. 독립 Recovery의 up·OS held 0·생산 Helper 종료·세션 종료·무동의 재개 없음·정상 Default 복귀를 확인한다. | NOT RUN | G06 비밀없는예약ACK/실제down/Recovery up인과관측 |

## 2. 고정 환경과 회차 입력

| 대상 | 고정 정보 | 실행 전 확인 |
|---|---|---|
| A | 192.168.0.61, Controller/Viewer/시그널링 | 사용자 SID, 현재 Session, Default, 일반 작업 복귀 창 |
| B | 192.168.0.74, SSH 사용자 jacking75NoteBook, 제품 Host | 등록 SSH 호스트 키, 현재 콘솔 SID/Session/Desktop, 일반/특권 실행 경계 |
| DesktopTestDriver | F:/github/DesktopTestDriver | 별도 저장소의 사용 규약과 실제 실행 파일 hash |
| 실행본 | bin/live_Release | Controller/Controller.exe, Agent/Agent.exe |
| DTD 설정 | 기존 .artifacts/dtd-e2e-20261020/Controller.json | 원문/인증서 개인키/환경 전체 출력 금지, 실제 경로 hash만 기록 |
| 개발 포트 | signaling TCP25001, 도구 TCP는 기존 구성, TURN TCP25002/TLS25003·relay UDP27100~27199 | 기존 비소유 listener가 있으면 덮어쓰지 않고 BLOCKED |
| 제품 설치 | B Program Files/RemoteAssist-Dev, 서비스 RemoteAssistHost | 기존 제품/회차 dev-owner 소유권, 재사용/자동제거 금지 |
| 이전 관측 Session | A1 / B2 | 고정 상수가 아니라 재확인할 기대값. 다른 값이면 입력 전에 검토 |

SSH 인증은 기존 환경 설정을 사용한다. 비밀번호를 새로 요구하거나 문자열로 출력하지 않는다. `Get-ChildItem Env:`·명령줄 credential·pairing 공개 출력을 금지한다. 코드/PIN·operator/TURN secret·원래 clipboard는 비공개 영역만 사용한다. Viewer에는 operator-secret 입력 필드가 없다.

각 새 회차는 `.artifacts/e2e-Cxx-<GUID N>/` 하나를 소유한다. `readiness.json`에는 다음 값을 실제 확인 결과로 채운다. 빈 값/예시/false는 준비 미완료다.

| 필드 | 필요한 실제 값 |
|---|---|
| caseId, runId, previousRun | Cxx, 고유 GUID, 실패 재시험일 때 이전 result 경로 |
| candidate | commit, dirty 파일 목록, candidate.json hash, payload 개수, 변경 영향/자동 회귀 증거 |
| tools/configuration | 실행할 모든 script/exe/settings hash, 절대경로, 실제 CLI 인자 |
| identity | A/B 사용자 SID·Session·desktop·권한, 서비스/Helper/Recovery 소유 PID/image/start/handle |
| endpoint | signaling origin, 검증된 TLS identity, DTD machineId/config hash, 실제 port/listener |
| uiMap | 실제 windowId/elementId/AutomationId, 소유 PID/image, 이름·종류·유일성·현재 bounds |
| observations | 각 합격 조건을 읽는 도구/필드/주기/출력상한, 필요한 증거 경로 |
| recovery | 실행자와 독립 복귀 담당자/수단, 유한 중단/정리 명령, 소유 경로 목록 |
| budgets | 전역 QPC origin/grant, 단계 상한, 사전 실측 근거, cleanup/recovery/report reserve |
| review | 실행 스크립트·관측·중단·복구 검토자/시각/미해결 항목, READY 판정 근거 |

## 3. 명령과 UI 결합 계약

아래는 **현재 지원되는 명령 형식의 참조**다. 오늘 실행하지 않는다. 회차별 실제 입력값을 채우고 hash를 고정하기 전에는 복사 실행하지 않는다.

```powershell
$DtdExe = 'F:/github/DesktopTestDriver/bin/live_Release/Controller/Controller.exe'
$DtdConfig = 'F:/github/KnockHB/.artifacts/dtd-e2e-20261020/Controller.json'
& $DtdExe --config $DtdConfig --status --machine pc-a
& $DtdExe --config $DtdConfig --status --machine pc-b
# controlSessionId는 아래 응답을 비공개 회차 파일에 보관해 결합한다.
& $DtdExe --config $DtdConfig --session-open --machine pc-a --mode control --idle-seconds 900
& $DtdExe --config $DtdConfig --session-open --machine pc-b --mode control --idle-seconds 900
& $DtdExe --config $DtdConfig --request <이번-회차-요청.json>
& $DtdExe --config $DtdConfig --request-status <현재-sessionId> <원래-requestId>
& $DtdExe --config $DtdConfig --session-close <현재-sessionId>
```

요청 JSON 예시는 인자 구조만 보여 준다. 문자열 `현재-...`는 실행 가능한 ID가 아니다. windows/controls 관측 결과로 결합해야 한다.

```json
{"apiVersion":1,"requestId":"새-GUID","controlSessionId":"현재-세션-ID","operation":"windows","timeoutMilliseconds":5000,"arguments":{}}
```

| 작업 | 정확한 arguments | 확인할 결과 |
|---|---|---|
| windows | {} | title/pid/imagePath/createdAt/windowId/foreground |
| controls | windowId | elementId/automationId/name/controlType/enabled/value |
| focus | windowId | foreground=true, 소유 PID 동일 |
| click | windowId, elementId 또는 automationId 중 하나 | inputEventsSent/inputReleased, 실제 UI 후속 결과 |
| type-text | windowId, text | 현재 포커스된 입력칸 값과 원문 일치. 비밀값 stdout 금지 |
| key | windowId, key, action=press/down/up | 실제 대상 전달/OS held, 최대 hold1500ms |
| chord | windowId, keys 배열 | 단일 조합 완료와 해제 |
| capture | windowId, output=회차 안의 새 절대경로 | 파일 hash·captureId·유효시각. 좌표 입력 전 실제 이미지 열기 |
| drag | windowId, captureId, start/end {x,y}, button, steps | 실제 고정 경로/전이 |
| wheel | windowId, position {x,y,captureId}, delta | 실제 delta/횟수 |
| release-input | {} | 이 도구 세션 소유 입력만 해제. 제품 입력 복원 증거가 아님 |

포커스·요소 선택·텍스트·버튼 클릭처럼 의존하는 작업은 각 결과를 확인하며 순차 실행한다. 이를 미리 준비한 짧은 실행 묶음 안에서 처리해 매 단계 긴 도구 왕복/사고 시간을 소모하지 않는다. 자동 승인 API를 제품에 넣지 않으며 실제 WPF 동의 창의 고정된 허용 범위만 조작한다. 변경 응답 유실 시 원래 request-status와 실제 UI를 확인하고 자동 재입력하지 않는다.

### 기존 제품 하네스의 실제 제한

| 도구 | 현재 동작/제한 | 다음 회차 전 조치 |
|---|---|---|
| e2e-product-client.py | arm/status/finish/collect/evidence. arm auth/scope가 C01 profile 고정 | 각 C 권한·분기 검토 없이 C02~16에 재사용 금지 |
| e2e-product-arm.ps1 / guard / run-contract | lan-view-consent-stop 및 고정 budget/owner/hash 검증 | 문서와 실제 허용 profile·관측의 일치가 필요 |
| e2e-viewer.cjs | 화면 보기용 DOM 프레임/상태와 signaling WS 타입 관측 | DC 메시지·held·worker·telemetry·TURN 양끝을 이미 수집한다고 주장하지 않음 |
| e2e-product-observe.ps1 | legacy observer 명시적 차단 | 과거 observe/준비 pointer 직접 실행 금지 |
| product-e2e-harness.ps1 / invoke-product-local Arm | 차단된 과거 경로 | 차단 해제 없이 새 C 명령이라고 포장하지 않음 |
| e2e-product-collect.ps1 | resourcesAbsent 관측, recoveryVerified=false | 실제 Default/입력/clipboard 복귀는 별도 독립 검증 |
| e2e-resource-inspect.ps1 | LocalMachine Root만 조회 | CurrentUser 임시 신뢰가 있다면 별도 명시적 부재 검증 필요 |

현재 finish 명령의 형태는 `python scripts/e2e-product-client.py finish --campaign <이번 회차 campaign.json>`이다. 로컬 finish 파일을 SSH보다 먼저 생성한다. 이 경로를 유지/확장하는 경우 새 회차에서도 같은 독립 로컬 중단 성질과 소유 검증을 보존한다. 예전 고정 campaign 경로를 새 회차 명령에 넣지 않는다.

## 4. 전역 시간·관측·준비 게이트

기존 계약은 전체 목표600초·절대900초, active300초, cleanup240초, recovery예비300초, report60초다. 300+240+300+60=900초이며 정상 경로는 예비를 쓰지 않고 보고까지600초를 목표로 한다. 정상600초를 실제 충족하려면 cleanup+통상복귀를240초 이내로 관측해야 한다. 문서에 시간만 적어 충족으로 처리하지 않는다.

전역 origin은 로컬 신뢰/브라우저 등 첫 변경 전에 한 번만 만든다. 새 터미널/Prepare/SSH로 초기화하지 않는다. B는 원격 QPC 표본과 A 수신시각의 남은 예산으로 보수적 deadline을 받는다. UTC 차이로 A/B 경과 시간을 계산하지 않는다. 각 단계는 min(단계 상한, active 잔여)을 사용하며 cleanup/recovery/report 예비를 기능에 빌려 쓰지 않는다.

observer는 입력을 보내지 않고 메타데이터만 읽는다. 프레임 카운터·권한/채널·요청 ID·epoch·단조 시각·실제 OS/worker/file 상태를 필요한 범위에서 수집한다. 코드/PIN, 원래 clipboard, 비밀번호, 전체 데이터채널 본문, 네트워크 주소 목록을 일반 로그에 남기지 않는다. 250ms 프레임 polling은 1초 종료를 정밀 증명하지 못하므로 C01의 제품 exit-probe QPC를 사용한다.

### 실제 감사 파일 형식

각 문서의 ConsentGranted/Completed 같은 표현은 의미 이름이다. 현재 `AuditJournal`은 기본 System.Text.Json으로 직렬화하므로 실제 필드는 `EventId/Utc/Kind/Result/SessionId/ConnectionId/PermissionGeneration/Operation`이고 enum은 숫자다. 감사 수집기는 아래 값으로 해석하며 문자열 enum을 기다리지 않는다. 현재 회차 session/connection/operation ID로 먼저 좁히고 필요한 metadata만 내보낸다.

| Kind | 숫자 | Kind | 숫자 |
|---|---|---|---|
| SessionCreated | 0 | ConsentGranted | 1 |
| ConsentDenied | 2 | SessionEnded | 3 |
| AdminGranted | 4 | AdminDenied | 5 |
| AdminRevoked | 6 | FileStarted | 7 |
| FileFinished | 8 | RunbookStarted | 9 |
| RunbookFinished | 10 | SasRequested | 11 |

`Result`는 Allowed=0, Denied=1, Stopped=2, Completed=3, Failed=4다. 실제 설치 후보의 enum과 대조하며 제품 스키마가 바뀌면 이 해석도 갱신한다.

준비 완료 신호는 `review=READY`와 실제 후보/도구 hash, 모든 필수 관측기 신호, 독립 watchdog PID/start, 복귀 수단 및 시간 실증 증거가 연결된 경우뿐이다. 현재 문서 묶음은 NOT READY다. 다음 항목은 시험 개수에 추가하지 않는 기존 C의 준비 작업이다.

| 준비 ID | 구체적인 완료물 | 영향 |
|---|---|---|
| G01 | 실제 Save/Read의 공유 충돌 재현→수정→동시읽기 회귀, 원래 RuntimeException 상세/phase 보존, watchdog 상실/정리 유한성 검증 | 전 C |
| G02 | 머신 Root 변경 없는 신원검증 방식 확정. TLS 유효성/hostname·서버 identity 유지. 전체 ignoreHTTPSErrors/무조건 인증서 수락 금지. 미지원이면 준비 미완료 | 전 C |
| G03 | 승인 전부터 프레임/권한/DC/감사/종료를 수집하는 현재 제품 경로의 읽기 전용 관측과 실제 UI 단발 묶음 | 전 C |
| G04 | 각 C의 고유 runId·권한·실제 도구 지원 범위·분기·명령·소유 자원·cleanup을 고정. 기존 실행 경로를 확장하되 새 시험 체계를 만들지 않음 | 전 C |
| G05 | InputTarget 포커스 전달·wheel delta/drag/F12 독립 관측, baseline·geometry·overflow 확인 | C03/C14/C16 |
| G06 | 비밀 없는 제품 held/reset·Recovery ACK/down/up 인과 관측. DTD의 자동1500ms 해제와 혼동 방지 | C03/C05/C16 |
| G07 | 실제 현장/VM 복귀 담당자·연락·보안 화면 취소·같은 사용자 로그인 준비 | C04/C05/C06 |
| G08 | secure desktop/특권 세대·mapping/Host selected pair 등 해당 실제 런타임 필드 수집 | C04/C05/C06/C15 |
| G09 | 고정합성 파일·실제 UI 저장/거절·binary byte/receipt·최종파일/partial/hash·감사 연결 | C07/C08/C09 |
| G10 | Python/Controller/PowerShell의 UTF-8 원문 보존, Unicode 정확 비교 | C03/C10/C11 |
| G11 | B STA clipboard 전체 baseline의 비공개 보관/finally 복원/boolean 검증 | C11 |
| G12 | 진단 실제 CPU/메모리와 capture start0·N/A/stale 수집 | C12 |
| G13 | 설치 Runbook source/hash와 worker1/0·실제 닫기ACK/입력재개 순서 관측 | C13/C14 |
| G14 | 실제 coturn/운영자/할당 조회, 양끝 selected pair와 연결할 한 relay 경로 | C15 |

TLS 대안은 문서만으로 해결됐다고 간주하지 않는다. 기존 인증서/운영 신뢰를 우선 조사하고, 격리된 시험 신뢰 방식이 제품의 hostname/identity 검증을 약화하지 않는지 코드·실증으로 확인한다. 인증서 생성·Root 등록을 매 회차 당연한 준비로 반복하지 않는다.

## 5. 첫 실패 중단·정리·복귀

| 순서 | 실행 | 완료 증거/상한 |
|---|---|---|
| R1 | 기능 입력 즉시 중단, 첫 실패 ID/단계/단조 시각 저장, 로컬 finish 먼저 기록 후 B finish 전송 | 단발이며 SSH 장애가 로컬 정리를 막지 않음 |
| R2 | A 도구 held 해제, 제품 종료/Recovery 경로 호출 | 제품 held 해제와 도구 해제를 구분. C16 원래 인과증거 보존 |
| R3 | 소유 service·Job·Helper/AdminHelper/worker 종료, UI/브라우저/시그널링 정리 | PID/image/start/handle/회차 owner 검증. 유한 SCM/자식 호출 |
| R4 | clipboard baseline 등 각 C 복원, 소유파일/신뢰/설치 폴더 처리 | 원래 사용자 자료 보존. 실패하면 복구가 최우선 |
| R5 | 독립 SSH 자원 조회+DTD/현장/VM의 동일 SID·Session·Default·입력 해제 확인 | resourcesAbsent만으로 복귀 PASS하지 않는다. A CurrentUser/LocalMachine 신뢰 범위를 각각 확인한다. |
| R6 | 복귀 검증 증거를 ledger에 기록한 뒤 소유 watchdog/task 종료·DTD 세션 close | watchdog을 먼저 제거하지 않는다. inputReleased/applicationsStopped를 확인한다. |
| R7 | 결과·duration·화면점유·첫실패·정리/복귀·남은기존ID를 보고 | 목표600초/절대900초에 포함 |

SCM/Job/인증서 호출이 유한해도 사용자 확인 창이 필요하면 전체 복귀가 자동으로 끝난다는 보장은 없다. 그런 확인 창을 실행 중 처음 발견하는 구성을 READY로 만들지 않는다. 모달 UI는 준비 단계에 실제 대상/유일 요소/신원 일치·취소경로와 제한 시간을 확정한다.

## 6. 문서 검토·실행 순서·인계

1. 모든 개별 계획을 정적 검토한다. 실제 코드/AutomationId/CLI/권한mask/채널/시간/관측/중단경로를 대조하고 미구현을 표시한다.
2. 공통 G01~G04를 입력 없는 준비/회귀로 완료한다. 같은 후보의 유효한 기존 빌드/회귀/전달 증거를 재사용한다. 전체 재빌드/재회귀를 습관적으로 반복하지 않는다.
3. C별 추가 준비를 완료한다. 실제 외부 자원 없는 C04~C06/C15 등은 해당 ID만 BLOCKED로 기록한다. 다른 ID를 무조건 막지 않는다.
4. 이후 사용자가 실행을 요청하면 C01부터 준비 완료된 ID를 한 회차씩 진행한다. 각 회차 직전 목적·화면점유예상·중단조건을 짧게 알린다. 한 회차 실패로 다른 C를 같은 회차에 끼워 넣지 않는다.
5. 수정은 실패 회차 종료/복귀/보고 뒤 수행한다. 원인·수정·예상 결과·정리 절차가 바뀐 기존 ID만 새 회차로 재시험한다. 실패한 manifest/코드/PIN/요청ID를 재사용하지 않는다.
6. 최종 판정은 위 종료 조건을 따른다. 개별 회차의 기능 결과와 정리·복귀 결과를 구분해 같은 상태 표에 반영한다.

`result.json` 필수 필드: caseId/runId/previousRun/candidateSha256/toolManifestSha256/result/firstFailureStep/startTick/endTick/frequency/actualSeconds/screenOccupiedSeconds/assertions[]/evidence[]/cleanup/recovery/unexecutedSteps. 정리 완료와 기능 PASS는 별도 필드다. 증거 없음은 false/unverified로 기록한다.

오늘 문서 정적 검증은 16개 ID·링크·코드/도구 경로·권한mask와 기능별 실제 UI/계약·미해결 항목·중단복귀 절차가 모두 있는지 확인하는 작업이다. 준비 실행·실전 실행을 수행했다는 뜻이 아니다.
