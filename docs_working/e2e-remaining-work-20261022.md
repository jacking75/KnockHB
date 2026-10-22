# E2E 실행 준비 잔여 작업과 예상 시간 — 2026-10-22

- [x] A01. C11 독립 OLE 사본과 지원표
- [x] A02. C11 복원 수명과 경합
- [x] A03. 부분 기동 회수
- [x] A04. 비동기 관측과 첫 위반
- [x] A05. deadline과 시간/호출 지표
- [x] A06. 실측 증거 생성 경로
- [x] A07. UI/파일/브라우저 계약
- [x] A08. C16 제품 종료 감사
- [x] A09. 재현 빌드와 신규 회차 설정
- [x] A10. TURN 제외와 기준 문서
- [x] A11. 전체 입력 없는 회귀/리뷰
- [x] A12. 실행/중단 인수인계

체크는 이번 허용 범위인 구현·코드 검토·입력 없는 검증의 완료를 뜻한다. B/C 실증과 기능 PASS는 별도이며 아래 최종 수용 체크를 대신하지 않는다.

후속 RV01~RV06 보완은 [최신 리뷰 기록](e2e-case-implementation-review-20261021.md)의 상단 태스크에서 추적한다. A01~A12와 11절의 수치·hash는 해당 시점의 결과다. 현재 스크립트 변경 뒤에는 새 도구 hash에 맞는 readiness/UI/복원 증거와 신규 Prepare가 필요하며 과거 동결 회차를 재실행하지 않는다. 실제 DTD/A/B/TLS/현장 담당자 및 B01~B04 실증은 여전히 미확보이며 이번에 실행하지 않는다.

**구현자는 [함수·자료구조·실패 분기·검사 벡터별 상세 구현 지시서](e2e-implementation-handoff-20261022.md)를 함께 읽는다.** 이 문서는 범위와 진행 목록이며, 실제 수정 방법은 상세 지시서가 보완한다. 상세 지시서의 `신규` 표시는 아직 코드가 존재한다는 뜻이 아니다.

이 문서는 현재 구현을 실행 가능한 상태로 마무리하기 위한 작업 목록이다. 사용자 요청에 따라 남은 구현·검토·검증·외부 준비·향후 실행을 구분한다. 체크하지 않은 항목은 완료되지 않았다. 예상 시간은 실측이 아닌 코드 검토에 근거한 작업량 추정이다.

## 1. 범위와 현재 상태

- 이번 대상은 **C01~C14·C16, 총 15개**다. C15 TURN은 사용자 지시로 `EXCLUDED`이며 coturn 및 allocation collector 구현·배포·검증은 이번 작업량에서 제외한다.
- NAT 환경에서 직접 연결된다고 가정한다. NAT 통과 성능·다양한 NAT 조합·TURN 폴백의 수용을 주장하지 않는다. `ForceRelay=false`는 relay 사용을 완전히 금지하는 설정과 같지 않으므로, 실제 연결 경로가 전제와 다른 경우 어떻게 보고할지는 실행 전 설정·판정에 고정한다.
- 기존 실행 스크립트·하네스·DesktopTestDriver 연결은 존재한다. 그러나 **15개 모두 실행 준비 완료인 상태는 아니다.** 단위 검사 성공은 실제 A/B 동작 성공을 뜻하지 않는다.
- C11의 독립 stream/storage/target-device 복제는 보완했다. 지원 외 FILE/GDI 종류/provider/stream timestamp는 변경 전 차단하며 실제 baseline 전체 지원 여부는 B03 전까지 미확인이다. 비동기·정리·증거 항목의 실제 변경과 검사는 아래 기록에 연결한다.
- 이전 Python 53개와 브라우저 5개는 당시 기록이다. 이번 회귀·빌드·후보 hash는 마지막 검증 표가 최신이며 실제 A/B 기능 PASS와 구분한다.
- **이번 문서 작성에서는 실제 E2E를 실행하지 않는다.** 날짜가 바뀌어도 기존 중단 지시와 이번 요청의 실행 금지가 자동 해제되지 않는다. B의 실증 작업과 C의 실제 E2E는 별도 실행 지시 뒤 수행한다.

근거: [기존 구현·검토 기록](e2e-case-implementation-review-20261021.md), [전체 실행 계획](e2e-acceptance-plan-20261021.md), 각 C 상세 문서, `scripts/e2e_case_*.py`, `scripts/e2e-desktop/ClipboardSnapshot.cs`.

## 2. 시간 산정과 순서

**이전의 사람 기준 55~110시간 산정은 철회한다.** 사용자가 요구한 것은 Codex가 직접 수행할 때의 경과 시간이다. 아래는 현재 문맥과 코드를 이어받아 내가 단독으로 수정·빌드·입력 없는 검증까지 수행하는 잠정 추정이다. 이번 변경에서 작업 속도를 실측한 것은 아니므로 완료 시각을 보장하는 수치가 아니다. 외부 대기·실제 E2E·새 제품 결함 수정은 구현 시간에 포함하지 않는다.

| 구간 | 예상 시간 | 의미 |
|---|---:|---|
| A01~A12 | **215~430분, 약 4~8시간** | 내가 수행하는 필수 보완·리뷰·입력 없는 검증. 아래 후순위 확장 제외 |
| B01~B04 | **약 1~2시간 + 현장 대기** | 기존 도구 재사용 전제의 환경 확인·필요한 사전 실증. 실행 허용 후 별도 측정 |
| C 실행 | **실행 창 목표 합계 150분, 상한 합계 225분** | 15개 × 목표 10분/상한 15분. 성공 소요 시간 예측이 아니라 회차별 예산 합계 |
| 실제 결과 검토·기록 | **약 30~60분** | 자동 수집 결과 검토. 실패 분석·수정·재실행은 별도 |

실제 E2E 한 회차의 목표 10분·절대 상한 15분과 위 개발 시간은 별개다. 15개 회차의 실행 창은 모두 상한까지 사용하면 최대 225분이지만, 검토·담당자 준비·결과 정리는 그 밖의 시간이다. 실패한 회차 안에서 수정·재빌드·재시도하지 않는다.

우선순위는 **확인된 누락·실행 차단 수정 → 기존 검사로 회귀 확인 → 실행 자료 정리**다. 아래 A 항목은 12개의 새로운 하위 시스템을 만들라는 뜻이 아니라 기존 코드의 점검 단위다. 이미 만족하는 항목은 근거를 연결하고 닫는다. 첫 30~60분 작업 뒤 실제 수정량과 남은 문제로 추정치를 갱신하며, 오래 걸리는 항목은 이유와 영향을 명시한다. 하위 에이전트 병렬 작업을 전제로 하지 않는다.

상세 지시서 작성 중 C16 정상 종료 audit 생산자와 강제 종료 후 기대값의 차이를 확인했다. 위 A08의 20~40분은 생산자 추적·기존 경로 연결·계약 검사 추정이다. 실제로 제품의 다중 프로세스 audit 원자성 보완이 필요하다면 이 시간에 포함됐다고 주장하지 않고, 조사 결과를 근거로 별도 수정 시간을 산정한다. 현재 코드만으로 그 제품 변경의 완료 시간을 확약하지 않는다.

### 이번 필수 범위와 후순위 확장

- **필수:** C11 기존 명세에 필요한 복제/복원 보완, 부분 기동 실패 정리, 실제 관측값의 누락·지연 처리, deadline·증거 정확성, C16 인과관계, 고정 설정/진입점, 관련 회귀 검사다. 사용자 원본 보존·동의·소유 자원·독립 복귀 요건은 시간 단축을 이유로 없애지 않는다.
- **기존 구현 재사용:** readiness 증거는 기존 수집 결과를 검증해 묶는 작은 고정 스크립트로 연결한다. 별도 범용 증거 관리 플랫폼·새 하네스·새 DTD를 만들지 않는다. 이미 통과한 검사와 공통 실증은 적용 범위와 hash가 맞으면 재사용한다.
- **후순위:** 임의 OLE 제공자의 모든 변형, 모든 locale/Chrome 버전, 범용 UI 탐색, 전체 환경 조합, 모든 이벤트 순열에 대한 테스트 프레임워크 확장이다. 아래 상세 체크리스트의 이런 항목은 지원 범위를 판별하는 검토 질문으로 보존하며 모두 구현해야 한다는 선행 조건으로 삼지 않는다.
- 후순위 형식이 실제 C11 baseline에 포함되면 변경 전에 차단하고 C11의 미지원 조건으로 보고한다. C11 명세에 필요한 형식까지 후순위로 넘겨 놓고 완료라고 하지 않는다. 명세 축소가 필요하면 별도 변경 사항으로 명시한다.
- B는 필요한 경계별 짧은 사전 확인이다. 15개 제품 시나리오 전체를 예행연습한 뒤 다시 본시험하는 구조로 만들지 않는다.

## 3. A — 구현·코드 검토·입력 없는 검증

### A01. C11 OLE baseline 복제와 복원 지원 완성 — 45~90분

상태: **IMPLEMENTED_OFFLINE_VERIFIED**. 실제 baseline 전체 지원 여부는 B03 전까지 미확인이다. 위치: [ClipboardSnapshot.cs](../scripts/e2e-desktop/ClipboardSnapshot.cs), [E2eDesktop Program.cs](../scripts/e2e-desktop/Program.cs), [C11](e2e-C11-explicit-clipboard-20261021.md).

- [x] 현재 HGLOBAL/GDI/ENHMF/MFPICT 외의 IStream·IStorage를 원본 객체 수명에 의존하지 않는 독립 복사본으로 보관한다. 원본 스트림 위치·소유권을 훼손하지 않는다.
- [x] FORMATETC의 target-device 데이터 복사·반환·해제를 구현한다. 열거기 Clone/Next와 GetData/QueryGetData의 형식 일치 및 메모리 소유권을 함께 정리한다.
- [x] TYMED_FILE, 지연 렌더링, 가상 파일, 동일 format의 복수 medium/aspect/index, 빈 클립보드도 지원표에 넣는다. 지원하지 않는 형식을 묵시적으로 버리지 않는다. 완전 복원이 불가능한 경우 변경 전 차단하고 제한을 명시한다.
- [x] 형식 수·전체 메모리·stream/storage 용량·재귀 깊이·시간 상한을 정한다. 현재 HGLOBAL 이외의 자원까지 상한을 적용한다.
- [x] 복제 중 COM 오류, 원본 프로세스 종료, restore 오류·시간 초과·guard 강제 종료에서 성공을 기록하지 않도록 한다. 사용자 클립보드의 내용·형식명·길이·hash를 로그에 남기지 않는다.
- [x] 합성 IDataObject와 메모리 stream/storage로 복제·열거·독립 수명·해제·실패 경로를 검사한다. 이 단계에서 실제 사용자 클립보드는 읽거나 바꾸지 않는다.

완료 조건: 지원표의 각 형식에 복제/복원 계약 검사와 제한이 연결되고, 요구되는 전체 baseline 보존과 차이가 남으면 C11을 완료로 표기하지 않는다. 실제 OleSetClipboard/OleFlushClipboard와 helper 종료 후 유지 검증은 B03에서 별도로 수행한다.

지원표와 검증: `CaptureFrom`은 OS clipboard와 분리했다. 합성 검사 프로젝트 `tests/RemoteAssist.E2e.Clipboard.Tests`가 14개 검사를 통과했다. SDK interop는 로컬 Windows SDK `10.0.26100.0/um/objidl.h`의 IStorage(11345), IEnumSTATSTG(11193), ILockBytes(12028) vtable/IID와 대조했다.

| 원래 baseline | 보존과 반환 | 변경 전 차단 조건 |
|---|---|---|
| HGLOBAL | 독립 handle·byte 비교 | null/중복 descriptor/총량 초과 |
| GDI bitmap | 독립 bitmap·pixel byte 비교 | bitmap 이외 GDI 종류는 지원 제한 |
| ENHMF / MFPICT | 독립 metafile/container·bits 비교 | 조회/복제 실패 |
| IStream | clone으로 읽고 별도 메모리 stream에 deep copy, 원본 cursor 유지 | clone도 seek 복원도 불가능하면 차단 |
| IStorage / 가상 파일의 storage | 별도 memory ILockBytes compound storage, 중첩 stream 복제 | 지원 외 element·깊이/총량 초과. 비어 있지 않은 독립 stream timestamp를 보존할 수 없는 provider는 변경 전 차단한다 |
| target-device | 검증된 CoTaskMem 사본과 각 열거기/반환의 독립 ptd | 잘못된 크기/offset |
| 동일 format의 여러 medium/aspect/index | descriptor 전체와 medium 교집합으로 구분 | 완전히 같은 descriptor 중복은 모호성으로 차단 |
| 빈 clipboard | 빈 snapshot 보존 및 복원 후 재열거 비교 | 실제 OS 복원은 B03 미실증 |
| 지연 렌더링·가상 파일 | 열거된 지원 medium 전부를 변경 전에 즉시 독립 보존 | provider 오류/시간 초과 시 차단 |
| TYMED_FILE / 알 수 없는 medium | 지원하지 않음. FILE은 GetData조차 호출하지 않아 원본 파일의 물질화/삭제를 피한다 | 해당 baseline의 C11은 BLOCKED. 일부 형식을 버리고 진행하지 않는다 |

상한은 expanded medium 256개, 총량 64MiB, storage 요소 4096개/깊이16, capture/restore 각 10초다. 복원은 OleFlushClipboard 후 다시 캡처한 사본과 비공개 메모리 비교가 일치해야 성공이다. 실제 flush·helper 종료 후 유지·실사용 baseline은 B03 전까지 미검증이다. helper의 10초 독립 timer가 COM 정지를 종료하면 성공 파일은 생성되지 않는다.

### A02. C11 독립 복원 수명·경합 검토 — 15~30분

선행: A01. 위치: `e2e_case_live.py`의 clipboard/restore 경로, `e2e_case_desktop.py`, `e2e_case_recovery.py`, E2eDesktop guard.

- [x] baseline 확보 완료 이전에는 제품 clipboard 변경이 불가능한지 확인한다.
- [x] 정상 종료·첫 실패·runner 종료·외부 Abort·네트워크 단절·deadline 만료별 guard 수명과 복원 책임을 표로 고정한다.
- [x] 복원 요청 중복, 늦은 응답, guard 종료 시점, 원래 runner와 Abort의 경합을 검사한다.
- [x] 10초 복원 상한 초과와 원본 미복원은 기능 FAIL과 복귀 실패로 구분해 기록한다.

완료 조건: 실제 clipboard를 사용하지 않는 실패 주입 검사에서 복원 시도 누락·이중 소유·거짓 복원 성공이 없다. 제품 실증과 동일하다고 주장하지 않는다.

구현·검토 기록: 복원은 원 guard의 run/case/PID/start/frequency와 private comparison·flush 결과를 검증한다. 10초 초과·owner 불일치·부분 기동 응답 유실·중복 signal의 실패 경로는 `test_e2e_case_remaining.py`의 clipboard 검사로 검증했다. 실제 OS flush/guard 종료 뒤 유지는 B03 미실증이다.

### A03. Prepare·begin 중간 실패의 소유 자원 회수 — 20~40분

상태: **검토·보완 필요**. 위치: `e2e_case_campaign.py:Campaign.deploy`, `e2e_case_runtime.py:prepare_case`, `e2e_case_live.py:begin/retire`, `e2e-case-unprepare.ps1`.

현재 deploy 호출은 preflight 실패 처리의 try 바깥에 있다. 배포가 부분 성공한 뒤 실패했을 때의 회수까지 확인해야 한다.

- [x] archive 전송·압축 해제·소유 task 등록·preflight 각 경계에서 실패한 상태를 정의하고 실제 생성된 자원만 회수한다.
- [x] A session만 열린 상태, B baseline 없는 상태, clipboard guard만 뜬 상태, local runtime만 시작된 상태에서도 정리를 수행한다.
- [x] baseline/owner 기록 누락 때문에 뒤의 정리 단계 전체가 건너뛰어지지 않도록 한다. 신원 미확인 자원을 임의 종료하지 않는다.
- [x] 정리 실패가 최초 오류를 덮지 않도록 하며, 남은 자원·독립 복귀 필요 여부를 결과에 남긴다.
- [x] 전송 시간 초과·task 등록 부분 성공·Abort 중복·PID 재사용·기록 누락을 가짜 transport로 주입한다.

완료 조건: 각 부분 기동 지점에 회수 경로 또는 명시적인 `FAIL_RECOVERY_REQUIRED`와 남은 소유 자원 목록이 있고, 공유 서비스/경로를 정리하지 않는다.

구현·검토 기록: `Context.resource`의 intent/acquired/released와 owner 파일을 남긴다. deploy/preflight 실패 뒤 unprepare 실패는 최초 오류와 별도로 보존한다. A만 열린 세션·baseline 미확보·watchdog 미생성·clipboard 실패에서도 나머지 정리를 시도한다. owner가 불명확하면 종료하지 않고 FAIL_RECOVERY_REQUIRED와 unresolvedOwnedResources를 반환한다. Abort는 원 runner 생존 중 stop만 요청하고 과거 result는 SAVED_RESULT_ONLY로 반환한다.

### A04. 15개 사례의 비동기 관측 완료 조건 — 25~45분

위치: `e2e_case_sources.py:assemble/read`, `e2e_case_observations.py:derive`, `e2e_case_steps.py:CASES`.

- [x] 모든 활성 단계의 원본 필드·생산자·도착 시점·run/session/generation·상관 ID·tick 단위를 표로 만든다.
- [x] 아직 미도착한 정상 이벤트는 같은 deadline 안에서 Pending으로 기다리고, 도착한 위반은 즉시 실패시킨다. 조작 재전송이나 새 timeout 부여로 바꾸지 않는다.
- [x] 부분 JSON/NDJSON, 중복·역순 sequence, 이전 회차 기록, 브라우저 snapshot 지연, audit 지연, 정상 종료 직전 파일 flush를 검사한다.
- [x] 채널 허가 위반·승인 전 입력/worker·파일 deny 후 binary·동의 철회 후 재개는 대기 끝까지 숨기지 않고 즉시 판정한다.
- [x] `book-consent`의 중복 `dialogBookHash` 대입 등 검토 중 발견된 중복을 제거하고 대응 회귀 검사를 유지한다.

완료 조건: 모든 활성 단계에 정상·지연 도착·끝내 미도착·모순·stale source 벡터가 연결된다. 현재 executor 순서 검사 119개 경로와 실제 live 필드 검사를 혼동하지 않는다.

구현·검토 기록: `e2e_case_records.decode/sequence`, `invariants`, `await_completion`을 연결했다. 부분 NDJSON만 기다리고 완성 행 오류·다른 run·중복/역순 sequence·허가 전 전송은 즉시 실패한다. 각 C의 생성 11.2절에 원본 필드·생산자·도착 경계·ID·clock을 연결했다. book-consent의 중복 대입을 제거했다.

### A05. deadline·화면 점유·도구 호출 비용 — 15~30분

위치: `e2e_case_runtime.py`, `e2e_case_live.py:independent_recovery`, `e2e_case_command.py`, `e2e-case-command.ps1`, A/B watchdog.

현재 `screenOccupiedSeconds`는 origin부터 독립 복귀 확인까지의 전체 경과다. 실제 화면 점유를 관측한 값으로 오해하지 않게 수정해야 한다.

- [x] 전체 회차 시간, 제품 동작 시간, 화면 점유 시간, 정리·복귀 시간을 별도 필드로 정의한다. 관측하지 못한 값은 unknown 또는 보수적 상한으로 표시한다.
- [x] 화면 점유 시작/종료 관측을 연결하고 실패 경로에서도 기록한다. 전체 origin은 재설정하지 않는다.
- [x] active 300초, cleanup 누적 540초, recovery 누적 840초, 절대 900초 및 결과 전달 여유를 코드·문서·watchdog에서 대조한다.
- [x] DTD CLI·PowerShell Add-Type·SSH·상태 poll의 횟수와 예산을 산출한다. 실제 비용은 B02에서 재고 예산이 안 맞으면 호출 구조를 줄인다.
- [x] 취소 불가능한 COM/UIA/SCM/UAC 호출, 자식 프로세스·Job 종료의 최종 상한을 검사한다.
- [x] C01/C03/C11/C14/C16의 기능 SLA와 관측 명령 수행 시간을 구별한 경계 검사를 추가한다.

완료 조건: 코드 상한과 실측값이 구분되고, 원격 clock 변환·초과 시 종료·결과 전달까지 단일 deadline 안에서 처리한다.

구현·검토 기록: 단일 origin과 300/540/840/900초를 유지한다. screenOccupiedSeconds는 null, screenMeasurement는 bounded/unknown이며 첫 visible request부터 독립 복귀까지 상한만 별도 기록한다. product/cleanup/recovery phase ticks, 실제 gateway 호출 수·시간을 기록한다. gateway의 종전 timeout+2초 연장을 제거했다. DTD/SSH/gateway의 active/cleanup/recovery 호출 상한은 각각 320/96/64, 160/64/48, 640/192/128이다. 각 호출은 남은 단조 예산을 함께 소비하며 이 숫자는 실측 보장이 아니다.

### A06. readiness 증거 생성과 최초 준비 절차 — 20~40분

위치: `e2e_case_readiness.py:readiness/verify_frozen`, `e2e_case_runtime.py:freeze_inputs`, 준비 스크립트.

- [x] 현재는 읽고 검사하는 `sourceEvidence`를 어떤 고정 절차가 생성하는지 구현·연결한다. 사람이 임의로 `verified=true`를 쓰는 절차로 남기지 않는다.
- [x] monotonicDeadline/watchdog/SCM/UAC/cleanup/externalReturn/TLS별 생산자·artifact·hash·환경/후보/도구 결합과 유효 범위를 정한다.
- [x] UAC 없는 C에도 현재 boundedUac를 요구하는 정책을 검토한다. 공통 증거 재사용 또는 해당 없음의 근거를 명시하며 게이트를 조용히 생략하지 않는다.
- [x] 사전 실측 증거가 있어야 Arm 가능한 조건과 최초 사전 실증 방법의 순환 의존을 해소한다. 제품 E2E 외의 한정된 도구 자체 실증도 별도 실행 허용과 독립 복귀 조건을 갖춘다.
- [x] 바뀐 후보·도구 hash에 대해 어떤 증거를 재수집해야 하는지 고정한다. 다른 case의 증거를 재사용할 때는 적용 범위를 검증한다.
- [x] 증거 누락·변조·다른 SID/session·오래된 구성·가짜 예산을 거부하는 입력 없는 검사를 추가한다.

완료 조건: 신규 체크아웃에서 증거를 준비하는 순서가 문서와 스크립트로 재현되며, 현재 미확보 증거 목록을 자동 출력한다. 실측 미확보 상태를 READY로 바꾸지 않는다.

구현·검토 기록: `e2e_case_evidence.py inspect/assemble`와 `e2e_case_fixture_evidence.py collect`, `e2e-readiness-return.ps1`의 고정 phase recorder를 추가했다. 실제 campaign/SCM/UAC/TLS 산출물·A/B 독립 snapshot/자원 부재·담당자 RETURNED·source hash를 요구한다. 처음에는 sourceEvidence 파일 없이 inspect 가능하며 실제 측정이 없으면 7개 missing을 출력한다. UAC 공통 증거도 모든 사례에 유지한다. 합성 테스트는 실측 증거로 승격하지 않는다.

### A07. UI·브라우저·파일·동의 경계 계약 보강 — 15~30분

위치: `e2e_case_ui.py`, `e2e_case_adapters.py`, `e2e-browser-observer.cjs`, `e2e-viewer.cjs`, E2eDesktop UIA/OCR.

- [x] AutomationId·창 제목·locale·enabled·toggle·foreground·owner PID/start/image를 실제 제품 소스 및 DTD API와 다시 대조한다.
- [x] 현재 capture와 좌표의 결합·만료·DPI·모니터 origin·영상 letterbox·해상도 축소를 합성 데이터로 검증한다.
- [x] C04 UAC Cancel/No와 C05 Windows 보안 화면의 OCR 모호성·누락을 fail closed로 처리한다. 추측 좌표/Enter 대체를 넣지 않는다.
- [x] C07~C09 native 파일 창의 경로 재확인, offer ID, 단일 승인/거부, 실제 writer.close·최종 hash·잔여 partial 검사를 연결한다.
- [x] browser observer가 원래 WS/DC/picker 동작을 한 번만 전달하고 PIN·토큰·SDP·사용자 원문을 노출하지 않는지 재검토한다.
- [x] 실제 UI 사전 확인 결과로 selector를 동결하는 절차를 만든다. 실행 중 selector 탐색·수정은 허용하지 않는다.

완료 조건: 코드 계약 검사가 통과하고, 실제 창/OCR/native picker와 대조할 B03 체크리스트가 준비된다. 실제 UI 미검증 표시는 B03 전까지 유지한다.

구현·검토 기록: 기본 UI rules의 ownerRole 변경과 임의 필드 삽입을 차단했다. required targets와 제품 ID/고정 창 제목을 대조했다. Native picker/stream close hook 중복과 거부 경로를 보강했다. OCR은 UAC/보안 화면을 구분하고 unique target 및 현재 capture/video bounds 안에서만 좌표를 허용한다. `e2e_case_ui_evidence.py`는 사례별 사전 관측을 현재 rules/owner/capture hash에 묶어 동결한다. 실제 UIA mapping/locale/OCR은 B03 전까지 미검증이다.

### A08. C16 장애·Recovery·audit 인과관계 검토 — 20~40분

위치: E2eDesktop faultguard, `e2e_case_sources.py`, `e2e_case_observations.py`, RecoveryLedger/WindowsRecoveryBackend/Recovery/Service의 CaseTrace.

- [x] 예약 ACK → 실제 F12 down → 소유 SYSTEM fault trigger → 서비스 종료 → producer fence → native up 순서를 동일 프로세스·세대·QPC로 결합한다.
- [x] 서비스 PID/image/start/handle/dev-owner 변경·중복 trigger·지연된 down에서 kill을 하지 않는지 검사한다.
- [x] 실제 down 후 500ms 이내 단발 종료와 3초 이내 복원을 검사하고, DTD 1500ms 자동 해제가 Recovery 성공으로 집계되지 않게 한다.
- [x] 서비스 강제 종료 후 SessionEnded audit를 누가 실제로 생성·보존할 수 있는지 코드에서 확인한다. 현 계약을 충족할 수 없으면 기준 문서부터 수정한 뒤 제품/관측 코드를 보완한다. 관측기가 audit를 만들어 대신하지 않는다.
- [x] Helper/AdminHelper/mapping/입력 생산자 잔존·자동 서비스 재시작·새 세션 재개를 검출한다.

완료 조건: 합성 사건 순서의 정상/역전/누락/다른 프로세스 검사가 통과하고 실제 실증에 필요한 기록 생산자가 모두 존재한다. 실제 장애 실증은 C 단계다.

구현·검토 기록: 정상 서비스만 SessionEnded를 쓰던 제품 결함을 보완했다. 기준 architecture/protocol을 먼저 갱신하고 recovery-bind에 실제 ConnectionId를 연결했다. Recovery는 인증된 service handle 사망을 확인해 fence/up 이후 같은 AppendSessionEndedOnce를 호출한다. 보호 파일 mutex가 append/rotation/Reporter ACK를 직렬화하고 spool-only 사건의 EventId/Utc를 보존한다. 정상/역순/4개 writer 경쟁/spool/ACL 실패/torn journal 및 서비스 생존 시 audit 0을 검사했다. C16의 실제 fault는 미실행이다.

### A09. 빌드·후보·설정 생성의 재현성 — 10~20분

- [x] `e2e-case-build-tools.ps1` 자체를 새 `.artifacts` 출력에서 실행·검증한다. 이전 직접 dotnet build 통과와 wrapper 검증을 구분한다.
- [x] 최신 CaseTrace 및 수정 코드를 포함한 제품 후보를 고정 패키지 스크립트로 만들고 바이너리/source hash·설정·FFmpeg/인코더 의존성을 검사한다.
- [x] A/B용 신규 설정의 필수 필드·경로·TLS·포트·SID/session·DTD exe/config·도구 디렉터리와 사례별 입력을 검증한다. 비밀은 문서나 출력에 복사하지 않는다.
- [x] 15개 신규 회차 설정을 만드는 고정 방법을 제공한다. 과거 실패 run ID/launch/manifest를 재사용하지 않는다.
- [x] 별도 DTD 저장소 및 `bin/live_Release`의 실제 파일 유무를 확인하고 버전/hash 확보 여부와 제품 기동을 분리한다. 지정 exe는 현재 개발 머신에 없어 버전/hash는 B01 미확보로 남긴다.

완료 조건: 신규 출력 경로에서 빌드 결과·후보 manifest·설정 검증 결과를 재생성할 수 있다. 게시·서비스 설치·실행은 이 항목에 포함하지 않는다.

구현·검증 기록: 고정 wrapper로 신규 도구 출력 v2를 만들었고 desktop-tool/InputTarget/SCM fixture 모두 경고·오류 0개다. 고정 패키지의 후보 v3를 source/payload/의존성 해시로 재검사했다. 15개 신규 UUID draft는 C15를 제외하고 run 디렉터리를 만들지 않았다. 실제 SID/session/TLS/DTD를 추정하지 않아 생성 exit10, inspect exit10·7개 증거 미확보다. 최종 경로/hash는 11절을 따른다.

### A10. TURN 제외·현재 문서·추적 기준 일치 — 5~15분

- [x] 각 C의 생성된 11절, mapping generator, 공통 검토 기록에 남은 C15 collector 필수 표현을 현재 범위와 일치시킨다. 보존 명세는 향후 범위임을 표시한다.
- [x] CLI 및 내부 Prepare 경로의 C15 차단, Abort/Collect 유지, 다른 사례에 TURN 증거를 요구하지 않는 검사를 유지한다.
- [x] 실제 selected pair가 relay인 경우 직접 연결 전제와 다르다는 결과를 명확히 남기는 정책을 코드/문서에 연결한다. TURN 수용 PASS를 만들지 않는다.
- [x] AGENTS가 지시하는 `docs_working/원격제어-구현계획.md`와 R30을 복원했다. 파일은 과거 `9029f50`에서 삭제됐음을 이력으로 확인하고 직전 버전을 복원했다. 첫 머리와 R30에 현재 범위를 연결하고 과거 수용 이력은 보존했다.
- [x] 통합 구현계획의 상태 목록 생성기와 `--check`를 적용했다. 실행하지 않은 E/V/F를 완료로 바꾸지 않았다.

완료 조건: 이번 대상은 모든 진입점·목록에서 15개이며 C15는 EXCLUDED다. 기준 추적 문서 누락도 해결하거나 명시적으로 남긴다.

구현·검증 기록: 16개 상세 문서의 생성 구역과 source/clock 계약을 갱신했다. mapping `--check` 16개, 통합 목록 `--check` 116개, scope 회귀 3개를 통과했다. C15 내부 package/deploy/start도 차단하고 기존 회차 Abort/Collect는 유지했다.

### A11. 변경 전체 코드 리뷰·입력 없는 회귀 — 15~30분

선행: A01~A10.

- [x] 소유 자원/동의/비밀정보/실패 우선/timeout/정리 경로를 제품과 시험 도구 양쪽에서 리뷰한다.
- [x] Python 기존 검사와 신규 검사, Node observer 검사, PowerShell 구문, Python AST, JS 구문 검사를 실행한다.
- [x] 제품 솔루션과 E2eDesktop/InputTarget 빌드, 문서 mapping check, diff whitespace 검사를 수행한다.
- [x] 입력 없는 테스트가 실제 DTD/SSH/SCM/클립보드/UI를 호출하지 않도록 transport 차단 및 검사 경계를 확인한다.
- [x] 실행 명령·종료 코드·검사 개수·후보/도구 hash·미실행 항목을 검증 기록에 남긴다.

완료 조건: 실패 0 또는 남은 실패의 원인과 차단 범위가 명시된다. 단순 컴파일 통과를 기능 통과로 표시하지 않는다.

구현·검증 기록: Python 83개, Node 8개, 합성 OLE/OCR 14개와 audit-only 검사를 통과했다. PowerShell 77개/Python AST 30개/JS 2개 구문 오류가 없다. audit-only는 시험 소유 임시 파일·자식 프로세스로 다중 writer를 검사하며 실제 서비스나 제품 세션을 실행하지 않는다. 잘린 감사 행·ACL 거부·spool/rotation·정상/역순 경쟁을 포함한다. 각 명령과 산출물은 11절에 기록했다.

### A12. 실행 패키지·중단·결과 전달 자료 마감 — 10~20분

- [x] 각 C에 실제 구현 파일/함수, 새 검사 결과, 미검증·외부 조건, 준비/실행/중단/수집의 고정 진입점을 연결한다.
- [x] 회차별 목적·점유 예상·중단 조건·담당자·동결 설정·소유 자원 목록·시간표·증거 파일 목록을 완성한다. 담당자와 실제 환경은 B에서 확정하며 미확보 상태를 draft에 명시한다.
- [x] READY/EXCLUDED/BLOCKED/PASS/FAIL/FAIL_RECOVERY_REQUIRED의 의미와 종료 코드를 사용 문서에서 구분한다.
- [x] 준비 실패 또는 시간 초과 시 사용할 고정 Abort/독립 복귀 절차를 담당자에게 전달할 자료로 만든다. 실행 중 임의 명령 작성 단계를 남기지 않는다.
- [x] 결과에는 기능 판정·최초 실패·정리·독립 복귀·실제 시간·증거 무결성을 별도로 기록한다.

완료 조건: 모든 준비 자료가 있지만 B의 증거가 없으면 여전히 NOT READY다. 이 마감만으로 실행을 시작하지 않는다.

구현·검증 기록: 15개 draft-report.handoff와 16개 상세 문서의 구현/생산자/clock 연결, 9절 최초 준비 순서와 10절 상태·Abort 인수인계를 완성했다. 같은 run/config와 원 origin을 유지하고 기존 request를 재전송하지 않는다. 실제 담당자·UI·TLS·SID/session·DTD가 없는 현재 draft는 실행 불가다.

## 4. B — 외부 환경 확인과 사전 실증

아래에는 원격 접속·실제 서비스/화면/입력 등을 포함할 수 있다. 이번 문서 요청으로 실행하지 않으며 실제 조작 범위를 알리고 별도 실행 지시 뒤 수행한다. B03은 자체 fixture 검증을 먼저 수행하고 RemoteAssist 연동과 구분한다.

| ID / 예상 시간 | 해야 할 일 | 완료 조건·외부 의존 |
|---|---|---|
| B01 / 15~30분 | A `.61`, B `.74`, B SSH 사용자·등록 host key, DTD 상태와 버전, 계정/SID/session/Default, 기존 TLS 체인·hostname·유효기간, 포트 충돌·경로/ACL·디스크·브라우저 locale/version 확인. 직접 연결 전제와 후보 ForceRelay=false 대조 | 비밀을 출력하지 않은 현재 환경 기록. 기존 root trust를 임의 변경하지 않음. 충돌 자원을 덮지 않음 |
| B02 / 15~30분 | 독립 watchdog·단조 deadline·SCM/COM/UAC 시간 상한·SSH 단절·runner 소실·부분 기동/정리·외부 복귀를 고정 fixture로 실증. 실제 호출 비용과 단계/전체 예산 기록 | A06 형식의 실제 hash 결합 증거. 300/540/840/900초 경계 충족. 실패 시 Arm 금지 |
| B03 / 15~30분 | 실제 DTD owner/UIA/좌표/foreground/영상 marker, 브라우저/native 파일 창, OCR, C11 전체 OLE 복원 및 helper 종료 후 유지, 제품 observer/audit 생산을 짧고 분리된 승인 범위에서 확인 | UI·clipboard·서비스를 건드리므로 별도 허용 필요. 실제 baseline 복귀 확인. 코드 수정 시 관련 증거 재수집 |
| B04 / 15~30분 | C04~C06 현장 또는 VM 복귀 담당자, 콘솔 접근·같은 사용자 로그인, READY/RETURNED 기록 방법, 외부 recovery request 확인, 비밀번호 비자동화, C16 장애 후 정상 작업 복귀 수단 준비. 모든 C의 Prepare 결과 확인 | 각 C의 READY_NOT_EXECUTED와 실제 외부 복귀 준비. 담당자/복귀 수단이 없으면 해당 C BLOCKED |

외부 자원 목록: A/B 사용 가능 시간, B 사용자·SSH·DTD 접근, 현재 후보/도구, 신뢰 가능한 TLS 인증서와 접근 가능한 private key, C04 서명된 고정 Windows trigger, C04~C06 담당자/콘솔, 신규 회차 전용 디렉터리와 소유 task 권한이다. TURN 자원은 필요 목록에 없다.

## 5. 사례별 남은 확인 사항

아래는 A/B 작업을 각 상세 계획에 연결한 목록이다. 시간은 위 항목에 포함되므로 중복 합산하지 않는다. 모든 사례에 첫 실패 중단·기록·입력 해제·소유 자원 정리·A/B 독립 복귀가 공통으로 필요하다.

| 사례 / 상세 계획 | 실행 전 닫아야 할 확인 항목 | 주 연결 |
|---|---|---|
| [C01 화면·종료](e2e-C01-screen-view-stop-20261021.md) | 승인 전 5초 누출 0, 실제 marker/monitor/frame 증가, 접속 표시, HostStop 이후 Helper 종료 1초, 실제 end audit | A04/A05/A07, B02/B03 |
| [C02 동의 거부](e2e-C02-consent-deny-20261021.md) | 실제 거부 1회, 채널/영상/native/특권 입력 0, 거부 이후 5초 재개 없음, 세대와 deny/end audit 결합 | A04/A07 |
| [C03 입력·reset](e2e-C03-input-reset-20261021.md) | 정확한 Unicode text, F12 down/up, drag/wheel, 실제 blur, reset ID/epoch 및 1초 경계, 제품·OS held 0 | A04/A05/A07 |
| [C04 관리자·UAC](e2e-C04-admin-uac-revoke-20261021.md) | 독립 관리자 동의, 고정 서명 trigger, 현재 영상 Cancel/No OCR, Default 30초 복귀, revoke 후 helper/mapping/권한 제거 | A05/A07, B03/B04 |
| [C05 SAS](e2e-C05-sas-return-20261021.md) | SAS 단발·실제 보안 화면 분류, ESC 1회, 20초 내 같은 SID/session Default 복귀, 실제 SAS audit | A04/A07, B03/B04 |
| [C06 잠금](e2e-C06-lock-return-20261021.md) | WIN+L 단발, 독립 WTS lock, 현장 동일 사용자 60초 내 복귀, 이전 권한/입력/캡처 5초 재개 없음 | A04/A05, B04 |
| [C07 A→B 파일](e2e-C07-file-a-to-b-20261021.md) | 실제 폴더 승인·선택 경로, 승인 전 binary 0, 16개 chunk 순서/길이, receipt/고정 hash, partial 없음 | A04/A07/A09 |
| [C08 B→A 파일](e2e-C08-file-b-to-a-20261021.md) | 실제 native picker·writer.close, offer ID 상관, 최종 길이/hash/receipt, partial 없음 | A04/A07, B03 |
| [C09 파일 거부](e2e-C09-file-deny-20261021.md) | timeout이 아닌 실제 deny, accept/binary/complete/receipt/새 파일/재offer 0, 실제 deny audit | A04/A07 |
| [C10 채팅](e2e-C10-bidirectional-chat-20261021.md) | 양방향 실제 UI line 증가, Unicode/textContent, 중복 없음, 채널 권한 경계 | A04/A07 |
| [C11 clipboard](e2e-C11-explicit-clipboard-20261021.md) | 전체 baseline 지원, explicit set/get ID/hash·각 2초, 자동 동기화 없음, 원본 비공개, 10초 독립 복원 | A01/A02/A04/A05, B03 |
| [C12 진단](e2e-C12-diagnostics-only-20261021.md) | 실제 fresh 표본 2개, CPU 범위·메모리 OS 대조, capture/video/input 0, 관측한 N/A/stale만 판정 | A04 |
| [C13 Runbook 승인](e2e-C13-runbook-approve-20261021.md) | 설치/catalog/preview/dialog/실행 hash 일치, 실제 승인 1회, 승인 전 worker 0/후 1, 보통 권한 SID/session, 20초 exit 0 | A04/A07/A09 |
| [C14 Runbook 취소](e2e-C14-runbook-pending-cancel-20261021.md) | pending 상태에서 A cancel, 같은 ID dismiss ACK 뒤 새 epoch resume 2초, worker 0, 원격 click 1회·held 0, stopped audit | A04/A05/A07 |
| [C15 TURN](e2e-C15-turn-relay-20261021.md) | 이번 범위 EXCLUDED 유지. collector 구현·relay 실증·운영 준비 수행하지 않음 | A10 |
| [C16 서비스 장애](e2e-C16-service-fault-recovery-20261021.md) | 실제 held 중 소유 서비스만 단발 종료, ACK/fence/up 인과, DTD lease 배제, 재시작/잔존 없음, 실제 end audit | A03/A05/A08, B02/B04 |

## 6. C — 향후 허용된 실제 E2E와 결과 마감

| 작업 ID / 예상 시간 | 작업 및 완료 조건 |
|---|---|
| C01 / 10~20분 | 실행 허용 확인 후 15개 각 회차의 동결 설정·준비 증거·담당자·중단/복귀 수단 검토. 시작 직전 목적·점유 예상·중단 조건 전달. 이 작업 ID는 시나리오 C01과 별개다 |
| C02 / 150~225분 | 각 사례를 신규 회차로 단발 실행. 목표 10분/상한 15분, 첫 실패에서 중단·정리·독립 복귀. 회차 중 수정/탐색/재빌드/재시도 금지. 하나라도 복귀 실패면 후속 실행 중지 |
| C03 / 30~60분 | 실제 시간·증거 hash·기능 판정·최초 오류·정리·복귀를 검토하고 각 상세 문서/통합 상태에 연결. 실행하지 못한 사례는 NOT RUN/BLOCKED, C15는 EXCLUDED 유지 |

새 결함이 나오면 증거로 원인을 좁히고 별도 구현 작업과 시간을 다시 산정한다. 동일 원인·동일 준비 상태로 재실행하지 않는다. 수정한 후보/도구 hash와 영향받는 준비 증거를 갱신하고, 영향 사례만 별도 허용된 신규 회차로 재실행한다.

## 7. 최종 완료 판정

- [ ] 15개 모든 단계에 구현·관측 생산자·판정·시간 상한·증거·정리·복귀 경로가 있다.
- [ ] 확정된 미구현을 외부 자원이나 미검증이라는 이름으로 숨기지 않는다. 지원 제한이 있으면 해당 사례의 완료 여부에 반영한다.
- [ ] 입력 없는 검증과 실제 A/B 실증 결과를 구분해 저장한다.
- [ ] 현장/UI/OCR/TLS/클립보드/장애/전체 시간 미검증 항목이 실제 증거로 해소되거나 해당 C가 명시적으로 차단돼 있다.
- [ ] 실제 시험에서 사용자의 일반 작업 환경 복귀가 독립 관측으로 확인됐다.
- [ ] 각 사례의 결과와 증거 링크, 제외·차단 이유, 남은 결함과 후속 작업이 문서에 남아 있다.

이 문서의 항목을 기록한 것 자체는 구현·검증 완료가 아니다. 후속 작업에서 각 체크박스에 실제 변경 파일·검사 명령·결과·증거 경로를 추가한다.

## 8. 구현 연결과 운영 경계

A는 구현·코드 검토·입력 없는 검증이다. B01~B04와 C의 실전 실행은 수행하지 않았다. 원래 중단 지시를 유지하며 준비 파일만으로 실행하지 않는다. 실제 15개 기능 PASS는 별도다.

| 작업 | 실제 연결 |
|---|---|
| A01/A02 | ClipboardSnapshot.CaptureFrom/CopyStream/Restore, ClipboardStorage.Copy, ClipboardCopyBudget, Program.ClipboardGuardInitialized/ClipboardLifetime, DesktopSources.restore_clipboard |
| A03 | Context.resource, Campaign.deploy, e2e-case-unprepare.ps1, finish_existing, restore_case_state, stop_and_recover |
| A04/A05 | decode/sequence, invariants/await_completion/derive, Context.remaining/tool_call, OwnedGateway, 각 C의 생성 11.2절 |
| A06 | tool_inventory, e2e_case_evidence.py inspect/assemble, e2e_case_fixture_evidence.py collect, e2e-readiness-return.ps1, 기존 독립 campaign/SCM fixture의 신규 RunId 단발 모드 |
| A07 | rules, targets, e2e_case_ui_evidence.validate, map_video_point/ocr_point, OcrPolicy, 브라우저 native picker/writer hook |
| A08 | CrashSessionAudit.Record, AppendSessionEndedOnce/Acknowledge/AcquireWriter, Recovery의 실제 service handle, fault_order, 독립 회수 뒤 재시작 trace 재검사 |
| A09 | e2e-case-build-tools.ps1, package-host-dev.ps1, e2e_case_artifacts.py, e2e_case_config.py |
| A10~A12 | mapping generator/source contract, 복원한 통합 구현계획의 R30, 아래 인수인계와 최종 검증 기록 |

추가 제품 변경은 서비스 사망 뒤 종료 감사다. 보호 audit 디렉터리의 배타 파일 handle을 프로세스 간 mutex로 사용하며 대기는 2초다. 모든 append/회전/Reporter ACK가 같은 잠금을 쓰고 spool-only 사건의 원래 EventId/Utc를 보존한다. 중앙 signaling 사건을 로컬 사건으로 대체하지 않는다. journal 손상·잠금/ACL 실패는 감사 실패이며 입력 복원 결과와 구분한다.

## 9. 최초 사전 증거 준비 순서 — B의 별도 허용 뒤 실행한다

1. B01에서 실제 A/B SID/session, DTD exe/config, 브라우저 locale/version, 기존 신뢰 TLS와 cert/key **경로**, .NET 10 및 도구 배치를 확인한다. 비밀 내용은 출력하지 않는다. 지정 DTD exe는 현재 개발 머신에 없어 버전/hash를 미확보로 남겼다. 실제 A 체크아웃 경로가 현재 개발 출력과 다르면 A에서 신규 설정을 다시 생성한다.
2. `e2e-case-build-tools.ps1 -Output <신규 .artifacts 경로>`로 desktop-tool/input-target와 **.NET 10 SCM 자체 fixture**를 컴파일한다. `package-host-dev.ps1 -SignalingUri https://192.168.0.61:25001/ -SdkDirectory <실제 .NET10 SDK> -Output <신규 경로>`로 후보를 만든다. exe/서비스는 실행하지 않는다. `e2e_case_artifacts.py --candidate <후보> --tools <도구> --output <신규 JSON>`으로 sources/payload/FFmpeg 7개 DLL·ABI·LGPL 설정과 도구 전체 파일을 검사한다. 실제 HW 인코더 선택은 B/C 미검증이다.
3. 환경 template의 필드는 `candidateDirectory, desktopToolDirectory, inputTargetDirectory, dtdConfig, a, b, tls, local, uiRules`다. SID/session은 실제 값만 넣고 모르면 생략한다. C04 uacTriggerSha256은 B의 서명된 cmd.exe 실제 hash다. C04~06에는 실제 recovery.operator가 필요하다. `python -B -X utf8 scripts/e2e_case_config.py --template <template> --output <신규 .artifacts 경로>`로 15개 새 draft, draft-report.json, fixture-context-draft.json을 만든다. 미래 PID/tick이나 과거 launch/manifest를 넣지 않는다.
4. `e2e_case_evidence.py --action inspect --config <case-config>`는 sourceEvidence가 없어도 7개 누락 검사를 출력한다. DTD/SSH를 호출하지 않는다. 가짜 verified 파일을 만들지 않는다.
5. 도구 자체 실증을 별도로 허용받고 담당자/콘솔을 확인한다. context의 authorization.operator/consoleAvailable은 그 담당자가 확인한 값이며 증거 verified 플래그가 아니다. ownedProcessFiles는 나중에 실제 fixture가 쓸 identity **파일 경로**다. context를 확정하고 Directory를 신규 생성한 뒤 A에서 `e2e-readiness-return.ps1 -Action Start -Directory <context.directory> -Context <확정 context> -Execute`를 호출한다. 이후 context hash와 단일 origin은 변경할 수 없다.
6. A는 `tests/e2e/campaign-fixture.ps1 -RunId <fixture ID> -Output <context.directory/a>`의 controller-loss 단발 모드로 독립 감시를 확인한다. 실제 watchdog이 child 부재를 확인한 a/controller/cleanup.json을 생성한다. B는 같은 ID로 `tests/e2e/scm-fixture.ps1 -RunId <fixture ID> -Output <B 신규 .artifacts 경로> -ScmFixtureDirectory <검증한 scm-fixture 디렉터리>`를 호출한다. RemoteAssist 대신 RAE2E-ID 자체 서비스를 사용한다. B의 service-start/watchdog/cleanup 원본을 context의 raw-b 경로로 수집한다. 전체 바이너리 목록/hash가 현재 도구와 같아야 한다. 첫 실패면 뒤 검사를 이어가지 않는다.
7. B UAC 경계는 E2eDesktop의 uac-trigger를 별도 허용된 C04 fixture로 실행하고 실제 No/Cancel의 startedTick/tick/frequency/cancelled 원본을 수집한다. 자동 승인/비밀번호 입력은 없다. 콘솔 복귀와 독립 유한 종료 수단을 먼저 준비한다.
8. TLS는 `e2e-tls-fixture.py --config <설정> --run-id <fixture ID> --output <신규 경로> --execute`의 A 전용 listener로 확인한다. 최대90초·3회 handshake 뒤 닫히며 포트 충돌을 덮지 않는다. `e2e-tls-identity.ps1 -Certificate <기존 public cert> -Sha256 <실제 hash> -ProbeEndpoint`를 A 일반 사용자/B 일반 사용자/B SYSTEM의 별도 승인된 fixture로 수집한다. 기본 플랫폼 chain/hostname 검증과 기존 신뢰 저장소를 유지한다. tls-bundle.json은 schema=remoteassist-e2e-tls-bundle-v1, probes에 scope=a-user/b-user/b-system, artifact, sha256 세 참조를 둔다. collector는 실제 주소/SID/endpoint와 동일 인증서를 확인한다. chain만 검사한 preflight는 endpoint 증거가 아니다.
9. 도구 실행 뒤 A에서 ActiveEnd, 소유 정리 뒤 CleanupEnd를 같은 recorder로 기록한다. 그 뒤 A/B의 새 E2eDesktop snapshot/inspect-owned를 수집한다. 요청은 현재 fixture runId, 실제 SID/session/root/output, **해당 머신에서 계산한 QPC deadline**, 실제 owner identity만 사용한다. inspect-owned.owners는 context의 start/watchdog/child/TLS identity 파일들로 구성한다. A 관측 시각은 CleanupEnd 뒤, B는 SCM/UAC/TLS 완료 뒤여야 한다. inspectedPids가 실제 owner 집합과 같고 remaining/changed가 비어 있어야 한다. 담당자가 같은 사용자 Default 복귀를 확인하고 operatorEvidence를 RETURNED로 기록한 뒤 Return/Finish를 기록한다. 300/540/840/900초를 넘으면 증거 생성이 거부된다. 새 Start로 시간을 초기화하지 않는다.
10. `e2e_case_fixture_evidence.py --config <설정> --context <확정 context> --output <신규 측정 디렉터리>`로 원본을 검증해 measurements.json을 만든다. `e2e_case_evidence.py --action assemble --config <설정> --measurements <measurements.json> --output <config.sourceEvidence>`로 묶는다. 부족하면 exit10이며 READY 증거를 쓰지 않는다. 후보/도구/identity/적용 caseIds가 같을 때만 재사용하며 변경 시 관련 실증을 재수집한다. hash는 무결성이며 진실성/서명이 아니므로 담당자가 원본 생산자/실제 실행도 검토한다.
11. B03의 실제 UI를 `e2e_case_ui_evidence.py --config <설정> --observations <실제 UI 원본> --output <config.uiEvidence>`로 동결한다. target별 unique count/enabled/foreground, 실제 PID/start/image, locale/version, capture hash와 전체 rules가 필요하다. 실제 PIN value는 수집하지 않는다. 실행 중 selector를 탐색/변경하지 않는다.
12. C11은 caseInputs.clipboardEvidence가 추가로 필요하다. 제품 Arm 없이 E2eDesktop의 clipboard-lifetime 자체 fixture를 B 일반 사용자 STA에서 DTD keep 수명으로 실행한다. 요청 JSON은 공통 runId/caseId=C11/root/output/session/sid, 실제 candidateSha256, B QPC로 계산한 **40초 총 deadline**을 사용한다. 부모가 비공개 원본을 유지하고 자식 guard의 실제 OLE flush·종료 후 전체 사본을 비교하며 finally 복원한다. 종료/복원에 11초를 남긴다. 실제 exe/DLL hash·candidate·SID/session·helperExitSurvived가 현재 값과 같아야 C11 준비를 통과한다. SSH/runner 소실에도 guard를 유지할 독립 수명과 담당자 복귀가 선행이다. 이번에는 이 API를 실행하지 않았다. 지원 외 medium/stream timestamp/provider는 변경 전 차단하고 C11을 BLOCKED로 남긴다.

첫 자체 실증은 기존 readiness를 요구하지 않는다. 제품 Prepare/Run은 자체 실증·UI·C11 증거 확보 뒤에만 가능하다. 이번 소스 수정·컴파일·합성 검사를 B의 실제 증거로 대신하지 않는다.

## 10. 실행·중단·결과 인수인계

사례별 draft-report.handoff에 실제 단계 이름, 준비/실행/중단/수집 인자 배열, 담당자, 예정 runDir/staging/task/watchdog, 단계 상한과 증거 목록을 넣었다. 예정 자원은 아직 취득한 소유 자원이 아니다. 실행 뒤 lifecycle/action ledger와 실제 owner 파일이 기준이다.

| 상태 | 종료 코드와 의미 |
|---|---|
| DRAFT_NOT_READY | 설정 생성 exit10. 새 자료만 있으며 실행 준비가 아니다 |
| LOCAL_ARTIFACTS_VERIFIED_NOT_READY | 파일 검증 exit0. 제품/도구 실행 성공을 뜻하지 않는다 |
| LOCAL_CONFIG_VALID_NOT_READY | Validate exit0. 실전 준비 완료가 아니다 |
| READY_NOT_EXECUTED | Prepare exit0. 기능 PASS가 아니다 |
| EXCLUDED / BLOCKED / NOT_READY | exit10. C15는 EXCLUDED, 증거 부족은 BLOCKED다 |
| PASS | Run exit0. 기능·증거·상한·정리·독립 복귀 확인이 모두 필요하다 |
| FAIL | Run exit20. 첫 기능 실패가 있고 정리/복귀는 확인됐다 |
| FAIL_RECOVERY_REQUIRED | exit30. 자원/복원/복귀 미확정 또는 상한 초과다. 후속 시험을 시작하지 않는다 |
| PREPARATION_RETIRED_NOT_EXECUTED | Abort exit10. launch 이전 준비 소유 자원만 회수했으며 기능 실행·독립 복귀 성공을 뜻하지 않는다 |
| SAVED_RESULT_ONLY | Abort의 과거 결과 반환이며 exit30이다. 현재 복귀 증명이 아니다 |
| COLLECTED_SAVED_EVIDENCE_ONLY | Collect exit10. 저장 hash 재검사만 수행한다 |

중단은 생성 handoff의 **동일 case/config/runDir** `-Action Abort` 인자를 사용한다. 원 runner 생존 중에는 stop을 기록하고 원 runner/독립 guard가 복구한다. runner 소실 뒤에만 기존 owner/session과 원 origin으로 인계한다. 기존 intent가 있으면 같은 request ID 상태만 조회한다. 미확정 상태에서 새 launch나 추정 PID kill을 하지 않고 FAIL_RECOVERY_REQUIRED로 담당자에게 전달한다. 복귀가 미확정이면 guard와 원본을 유지한다. 현장/VM 담당자는 Default·동일 사용자·held0과 external-recovery-request를 확인하며 로그인 비밀번호를 자동 입력하지 않는다.

신규 Prepare는 정규화한 비밀 없는 설정을 `recovery-config.json`으로 보관하고 frozen-inputs의 `recoveryConfigSha256`에 묶는다. Run 직전에도 사본 hash를 확인한다. Abort는 실행용 read_config/readiness를 다시 요구하지 않고 campaign/회차/launch 소유권 확인 뒤 로컬 finish를 먼저 기록한다. 현재 config·candidate·readiness·TLS가 사라져도 준비 때의 검증된 사본으로 중단한다. 이전 Prepare에 사본이 없으면 frozen hash와 같은 원 config만 사용할 수 있다. 소유 자료가 변조되거나 유실되어 원격 복구를 안전하게 결합할 수 없으면 로컬 stop 요청을 유지하고 FAIL_RECOVERY_REQUIRED를 반환한다.

900초가 지난 뒤의 안전 복구는 기존 origin과 시간 위반을 그대로 남긴다. 호출별 최대 10초의 별도 유한 상한으로 stop/release/소유 정리·복원/독립 관측만 수행하며 기능 조작·새 UAC·fault·clipboard guard는 금지한다. 하위 product client는 recovery-only의 finish/status/collect/evidence만 허용하고 원격 collect도 과거 deadline으로 부재 관측을 차단하지 않는다. 초과 회차의 최종 판정은 FAIL_RECOVERY_REQUIRED이며 복구 성공으로 시간 위반을 지우지 않는다.

result.json은 기능 판정, firstFailureStep, cleanup, recovery.errors, unresolvedOwnedResources, phase ticks/실제 전체 시간, 화면 점유 bounded/unknown, 실제 toolCalls, evidence hash/bytes를 분리한다. prepare-failure.json은 최초 오류와 회수 오류를 분리한다. 실행 직전 목적·예상 점유·첫 실패 중단·복귀 수단을 전달한다. 이 자료는 이번 실제 실행 허가가 아니다.

## 11. 최종 입력 없는 검증과 산출물

A01~A12 구현·코드 검토·입력 없는 검증은 완료했다. B01~B04와 C01~C14·C16의 실제 수용은 미실행이며 NOT READY다. 7절의 실제 수용 체크를 이번 검사로 완료하지 않는다. 다음 명령은 저장소 루트에서 실행했으며 경로 인자를 받는 Python CLI에는 절대 경로를 사용한다.

| 명령/검사 | 종료 코드·결과 |
|---|---|
| `dotnet build RemoteAssist.slnx -c Release --nologo` | 0, 경고 0·오류 0 |
| `e2e-case-build-tools.ps1 -Output <신규 tools-v2 절대 경로>` | 0, desktop-tool/InputTarget/SCM fixture 각각 경고 0·오류 0. 컴파일만 수행 |
| `package-host-dev.ps1 -SignalingUri https://192.168.0.61:25001/ -SdkDirectory <확인한 SDK> -Output <신규 candidate-v3 절대 경로>` | 0, 서비스/Tray/Helper/AdminHelper/Recovery/RunbookWorker 게시 파일 생성. 설치·실행 없음 |
| `python scripts/e2e_case_artifacts.py --candidate <candidate-v3> --tools <tools-v2> --output <신규 JSON>` | 0, source/payload/설정/의존성 일치, 후보 497개·도구 26개·FFmpeg DLL 7개 |
| `python scripts/test_e2e_cases.py` | 0, 37개. 16개 정상 경로와 103개 단계별 실패 주입을 포함하며 실제 제품 PASS는 아님 |
| `python scripts/test_e2e_case_live.py` | 0, 13개 합성 관측 계약 |
| `python scripts/test_e2e_case_scope.py` | 0, 3개 C15 범위 검사 |
| `python scripts/test_e2e_case_remaining.py` | 0, 30개 실패/소유권/동의/deadline/증거 회귀 |
| `node --test tests/e2e/browser-observer.test.cjs` | 0, 8개. 원 호출·거부·중복 hook·metadata 검사 |
| `dotnet run --project tests/RemoteAssist.E2e.Clipboard.Tests -c Release` | 0, 14개 메모리 COM/GDI/OCR 계약. OS clipboard/UI 접근 없음 |
| `dotnet run --project tests/RemoteAssist.Host.Core.Tests -c Release --no-build -- --audit-only` | 0, rotation/ACL 거부/terminal ordering/spool replay/4 writer 경쟁/torn journal |
| PowerShell `Parser.ParseFile` / Python `ast.parse` / Node `--check` | 0, 각각 77개 / 30개 / 2개. PS 스크립트 실행·dot-source 없음 |
| `python scripts/update-e2e-case-mappings.py --check` | 0, 16개 생성 구역 일치 |
| `python scripts/update-task-overview.py --check` | 0, 116개 태스크 일치. E/V/F 실행 상태 승격 없음 |
| `git diff --check` | 0, whitespace 오류 없음 |
| `e2e_case_config.py --template <template-v3> --output <신규 configs-v3>` | 10, DRAFT_NOT_READY 15개·중복 없는 새 UUID·C15 제외·run 디렉터리 생성 0 |
| `e2e_case_evidence.py --action inspect --config <C01 draft>` | 10, NOT_READY/config-field-required. 7개 필수 실측 증거 누락을 정상 차단, functionalActions=0 |

이번 개발 머신의 산출물 위치는 아래와 같다. `.artifacts`는 Git에 넣지 않으며 다른 머신에서는 동일 스크립트로 신규 출력 경로에 재생성한다. 후보는 이번 소스 변경을 포함한 **미서명 개발 후보**이며 manifest의 commit은 착수 HEAD `2cc276d`, dirty=true다. 실제 소스 일치는 manifest의 개별 source SHA-256으로 검증했다. 정식 배포 수용·clean release commit 후보를 뜻하지 않는다.

- 후보: `.artifacts/e2e-remaining-candidate-20261022-v3/candidate.json`, SHA-256 `94457430cfa22827be65047613b8a3139cf9ee8c075b9f0fed5c2ccac0504929`다.
- 도구: `.artifacts/e2e-remaining-tools-20261022-v2/build-result.json`, SHA-256 `4318be8e53d96b9dc6a2a12b26b43a52b771bde5e2d8af62aa0e456709a62c66`다.
- 파일 검사: `.artifacts/e2e-remaining-artifact-verification-20261022-v3.json`이다.
- 최소 template/draft: `.artifacts/e2e-remaining-template-20261022-v3.json`, `.artifacts/e2e-remaining-configs-20261022-v3/draft-report.json`과 같은 디렉터리의 15개 config/fixture-context-draft.json이다.
- 명령/결과 요약: `.artifacts/e2e-remaining-validation-20261022.json`이다.

미확보 항목은 지정 DTD 실행본·버전/hash/config, 실제 A/B SID/session·TLS endpoint/chain·UI locale/version/owner/capture, 단조 시간·독립 watchdog·SCM/UAC/정리/외부 복귀 7개 실측 증거, C11 실제 baseline과 helper 종료 뒤 유지, C16 실제 장애 인과관계다. unsupported OLE/provider/stream timestamp는 C11 변경 전에 차단한다. 원본 clipboard·비밀·실제 PIN을 출력하지 않는다. SSH·제품·Viewer·서비스·UAC/SAS/잠금·실제 화면/입력·사용자 clipboard 시험과 CI는 실행하지 않았다. 실제 HW 인코더 선택·NAT 직접 연결 성능도 미검증이다.
