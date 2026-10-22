# E2E 잔여 작업 상세 구현 지시서 — 2026-10-22

현재 보완 사항은 [리뷰의 RV01~RV06](e2e-case-implementation-review-20261021.md)과 각 C 문서 11절을 따른다. C03/C11 필수 종료 감사, 모든 사례의 권한별 금지 동작, 소유 회차 Abort 사본과 기한 후 안전 복구를 추가했다. 아래 제안·착수 시점 수치로 최신 단계나 검증 결과를 덮어쓰지 않는다. B01~B04의 실제 환경·실증 증거는 미확보 상태이며 이번에 E2E를 실행하지 않는다.

2026-10-22 후속 구현: 제안 API와 검증 지시는 [잔여 작업 완료 기록](e2e-remaining-work-20261022.md)의 실제 API·지원표·검증·고정 실행 절차로 반영했다. 아래 “현재 제한/신규 제안”은 착수 시점 설명이며 최신 상태는 완료 기록을 따른다. B/C 실전 수용은 미실행이다.

대상 독자는 기존 대화 없이 이 저장소를 넘겨받은 구현 모델이다. [잔여 작업 목록](e2e-remaining-work-20261022.md)의 A01~A12를 실제 코드 변경으로 옮기는 방법을 설명한다. 이 문서의 제안 API·파일·검사 이름은 **신규 구현 지시**이며 이미 구현되었다는 뜻이 아니다. 현재 코드와 제안 코드를 구분한다.

## 0. 작업 시작과 금지 사항

### 0.1 목표

기존 C01~C14·C16 실행 스크립트의 실제 실행 차단 문제를 해결한다. 새 범용 자동화 프레임워크를 만드는 일이 아니다. C15 TURN은 `EXCLUDED`다. NAT 직접 연결은 환경 전제이며 TURN collector는 구현하지 않는다.

이번 허용 범위는 소스 수정·코드 검토·입력 없는 검사·빌드·문서 갱신이다. 다음은 실행하지 않는다.

- `e2e-C*.ps1 -Action Prepare/Run/Abort`, DTD CLI 및 원격 SSH 호출.
- 제품·Viewer·Tray 실행, 서비스 설치/기동/중단, UAC/SAS/잠금, 화면 캡처/입력 주입.
- 실제 사용자 clipboard 읽기/쓰기. 합성 IDataObject를 사용하는 메모리 내 검사와 구분한다.
- 기존 회차 재사용, root 인증서 저장소 변경, 사용자의 현재 프로세스 종료.

### 0.2 첫 읽기 순서

1. 저장소 AGENTS 지침, [범위·시간·잔여 목록](e2e-remaining-work-20261022.md), [기존 구현 기록](e2e-case-implementation-review-20261021.md)을 읽는다.
2. 담당 사례의 `docs_working/e2e-CNN-*-20261021.md`를 처음부터 읽는다. 생성된 11절만 읽지 않는다.
3. [runtime](../scripts/e2e_case_runtime.py), [단계 정의](../scripts/e2e_case_steps.py), [action adapter](../scripts/e2e_case_adapters.py), [LiveObserver](../scripts/e2e_case_live.py), [source 조립](../scripts/e2e_case_sources.py), [순수 관측 변환](../scripts/e2e_case_observations.py)을 읽는다.
4. 변경할 함수의 호출자와 테스트를 `rg`로 찾는다. 이 문서의 함수명과 현재 파일이 다르면 먼저 실제 위치를 확인한다. 없는 함수를 이미 구현된 것으로 가정하지 않는다.
5. `git status --short`로 기존 변경을 보존한다. 사용자 변경 전체를 reset/checkout/clean하지 않는다. 이 작업은 이미 많은 미커밋 변경 위에서 진행된다.

### 0.3 유지할 공통 계약

| 계약 | 고정 규칙 |
|---|---|
| run 소유 | 신규 32자리 소문자 hex runId, 신규 runDir, 실패 run `6a2f55ebaee74a4099177a271ea0b88c` 재사용 금지 |
| 시간 | `Context.origin` 1회, A clock은 perf_counter_ns, native clock은 QPC/frequency. 서로 다른 clock의 tick을 직접 빼지 않음 |
| 조작 | `Context.once`가 dispatch 전에 intent를 기록. 응답 유실은 동일 요청의 상태만 조회하며 조작 재전송 금지 |
| 첫 실패 | 첫 기능 실패를 유지. 이후는 입력 해제/정리/복귀만 허용 |
| 자원 | runId + 실제 PID/start/image/handle 또는 DTD processId 소유가 확인된 자원만 종료 |
| 관측 | 실제 생산자에서 온 값만 사용. expected 상수를 actual로 채워 넣지 않음 |
| 비밀 | PIN/token/key/SDP/사용자 원문/clipboard 원본을 로그·예외 메시지에 포함하지 않음 |
| 결과 | 구현 완료, 입력 없는 검사 통과, READY, 실제 기능 PASS를 별도 상태로 취급 |
| 준비 증거 | 실제 복귀·시간 실측을 합성 테스트 결과로 대체하지 않음 |

`CaseError`의 실제 생성자에는 `code, detail, exit_code=20`이 필요하다. `CaseError('x')`는 잘못된 호출이다. `require(condition, code, detail='', exit_code=20)`는 detail 생략 가능하다.

## 1. 현재 실행 흐름과 변경 경계

```text
e2e-CNN.ps1
  -> e2e_case_runtime.main
     -> C15 범위 차단 (Validate/Prepare/Run, 설정 읽기 전)
     -> read_config
     -> Prepare: freeze_inputs -> readiness -> DTD status -> package_campaign -> deploy
     -> Run: verify_frozen -> execute_steps
        -> Context.begin -> CaseBackend.begin -> LiveObserver.begin
        -> Step.actions -> CaseBackend.action -> Dtd/Harness/DesktopSources
        -> Step.checks  -> LiveObserver.read -> sources.assemble -> observations.derive
        -> predicate -> collect
        -> finally stop_and_recover -> write_result
     -> Abort: finish_existing (원래 runner가 끝난 뒤에만 정리 인계)
```

다음 순서로 작은 변경을 만든다. ① A03 부분 기동 회수, ② A04 관측 대기, ③ A05 시간 필드, ④ A01/A02 clipboard, ⑤ A08 C16 audit 경계, ⑥ A06/A09 준비 자료, ⑦ A07/A10/A11/A12 검토와 마감. C16에서 제품 변경이 필요하다고 확인되면 그 작업량은 별도 표시한다. 추정 시간에 맞추려고 audit 검사를 삭제하지 않는다.

각 변경 후 해당 검사만 먼저 실행하고, 마지막에 전체 입력 없는 검사를 한 번 실행한다. 매 함수 수정마다 전체 빌드·전체 테스트를 반복하지 않는다.

## 2. A01 — ClipboardSnapshot 구현

### 2.1 현재 구현의 제한

위치: [ClipboardSnapshot.cs](../scripts/e2e-desktop/ClipboardSnapshot.cs).

- `Capture()`가 `OleGetClipboard`를 직접 호출한다. 합성 객체를 주입하는 테스트 경로가 없다.
- `FORMATETC.ptd != 0`이면 즉시 중단한다.
- `STGMEDIUM.tymed`는 HGLOBAL/GDI/ENHMF/MFPICT만 허용한다. `Duplicate()`는 현재 모두 `OleDuplicateData`로 처리한다.
- 64MiB 한도는 HGLOBAL의 `GlobalSize`만 합산한다.
- `Restore()`는 `OleSetClipboard(this)`와 `OleFlushClipboard()` 호출 성공을 확인한다. helper가 종료된 뒤 전체 내용이 유지됐는지는 현재 코드만으로 입증하지 않는다.

### 2.2 먼저 분리할 API

기존 public 동작은 유지하고 내부 구현을 다음 책임으로 나눈다. 이름은 아래를 사용하거나 같은 책임의 기존 함수에 통합하되 테스트에서 직접 호출할 수 있게 한다.

```csharp
// 신규 내부 API. Capture()만 OS clipboard에 접근한다.
internal static ClipboardSnapshot CaptureFrom(
    ComData source, ClipboardCopyBudget budget);

internal sealed class ClipboardCopyBudget {
    // maxFormats=256, maxBytes=64*1024*1024, maxStorageDepth=16
    // deadlineTick 및 Func<long> clock 주입. 호출마다 deadline 갱신 금지.
    void CheckTime();
    void ReserveBytes(ulong bytes);
    void ReserveFormat();
}

internal sealed class OwnedFormat : IDisposable {
    // FORMATETC + 소유한 DVTARGETDEVICE 사본.
    // 외부로 반환할 때 다시 복제. 내부 포인터를 호출자에게 그대로 주지 않음.
}

internal sealed class OwnedMedium : IDisposable {
    // medium 종류별 독립 소유. 성공한 GetData마다 새 STGMEDIUM 반환.
    STGMEDIUM CopyForCaller(ClipboardCopyBudget budget);
}
```

한 클래스 안의 private helper로 구현해도 된다. 새 프레임워크나 서비스는 만들지 않는다. 메모리 소유권을 명확히 하기 위해 필요한 최소 타입만 추가한다.

`Capture()`는 `OleGetClipboard` → `CaptureFrom` → 자신이 획득한 COM reference 해제를 담당한다. `CaptureFrom`은 주입받은 source 자체를 Dispose/FinalRelease하지 않는다. 호출자 소유 객체와 자신이 취득한 interface reference를 구분한다.

### 2.3 FORMATETC와 target device

1. `IEnumFORMATETC.Next(1, ...)`의 HRESULT와 fetched를 함께 확인한다. `S_FALSE && fetched==0`만 정상 끝이다. 오류·예상 밖 조합은 부분 성공으로 처리하지 않는다.
2. source에서 돌려받은 `ptd`가 0이면 그대로 null target이다.
3. 0이 아니면 DVTARGETDEVICE의 `tdSize`를 읽는다. 고정 헤더 크기보다 작거나 사전 한도(예: 64KiB)를 넘으면 복제 전 실패한다. 모든 offset은 0 또는 `tdSize` 범위 내부여야 한다. 문자열 offset을 내용 로그로 출력하지 않는다.
4. `Marshal.AllocCoTaskMem(tdSize)`에 전체 bytes를 복사하고 원본 ptd는 `finally`에서 `FreeCoTaskMem`한다. GetData를 호출할 때는 수명이 유효한 원본 또는 사본의 FORMATETC를 전달한다.
5. 내부 entries가 사본의 소유권을 가진다. entries 추가 전에 실패하면 지역 소유자가 해제한다. 추가 후에는 snapshot Dispose만 해제한다.
6. EnumFormatEtc.Next가 반환하는 각 ptd는 **새 CoTaskMem 사본**이어야 한다. 호출자가 해제해도 snapshot과 다른 열거기에 영향이 없어야 한다.
7. GetData/QueryGetData는 `cfFormat`, `dwAspect`, `lindex`, 요청한 tymed와 실제 medium의 교집합, target-device 일치를 검사한다. 이름이나 format ID만 맞는다고 첫 entry를 반환하지 않는다.
8. GetCanonicalFormatEtc의 반환값과 output ptd 소유권도 대조한다. 원본 ptd 포인터를 shallow copy해서 이중 해제하게 만들지 않는다.

### 2.4 medium별 복제 알고리즘

모든 복제는 `CheckTime()`와 byte budget을 사용한다. `pUnkForRelease`가 있는 원본도 직접 free하지 않고 원본 `STGMEDIUM`에 대해 `ReleaseStgMedium`을 정확히 한 번 호출한다.

| medium | 구현 방법 | 실패 처리 |
|---|---|---|
| HGLOBAL | GlobalSize 확인 → byte reserve → handle 독립 복제 → 내용 읽기/비교는 메모리 안에서만 수행 | handle null, 크기 초과, 복제 실패 시 원본/이미 복제한 자원 해제 후 전체 Capture 실패 |
| GDI | 현재 OleDuplicateData 경로의 실제 format/handle 종류 호환 확인. BITMAP 크기와 row stride를 읽어 checked 산술로 메모리 예산에 포함 | 지원하지 않는 GDI 종류는 변경 전 unsupported. 원본 handle 삭제 금지 |
| ENHMF/MFPICT | 복제된 metafile과 관련 container의 소유권을 ReleaseStgMedium 규약에 맞춤. 크기를 API로 확인해 예산에 포함 | 중첩 handle 수명 누락·double free 금지 |
| IStream | clone 가능한 원본은 IStream.Clone 후 clone만 seek/read. 독립 메모리 stream에 64KiB 단위 복사. 각 chunk 후 deadline/누적 bytes 확인. snapshot에 저장할 stream은 position=0 | Clone 실패 시 원본 position을 저장하고 되돌릴 수 있는 seekable source만 제한적으로 복사. 비seekable 원본을 읽어 소모하지 말고 변경 전 unsupported |
| IStorage | 메모리 backing ILockBytes와 compound storage 생성. 원본 요소를 열거해 depth/요소 수/누적 크기 상한 확인. stream별 bounded copy, substorage 재귀 복제. destination commit 뒤 독립 보관 | 원본 storage 수정/commit 금지. deadline 초과·요소 상한·읽기 실패는 전체 실패 |
| FILE/알 수 없는 medium | 이번 고정 시험이 요구하는 지원 범위를 명시. 임의 원본 파일 전체를 복사하거나 삭제하는 구현을 즉흥적으로 추가하지 않음 | 변경 전 `clipboard-medium-not-restorable`. 해당 baseline을 요구하면 C11 BLOCKED로 남기며 완료로 표시하지 않음 |

IStream의 `Clone`은 일반적으로 backing storage를 공유하므로 **Clone 객체만 보관하면 deep copy가 아니다.** Clone은 읽는 동안 원본 cursor를 바꾸지 않기 위한 수단이며, 최종 사본은 독립 메모리로 만들어야 한다.

IStorage interop가 저장소에 없으면 E2eDesktop 전용 파일에 둔다. SDK 정의와 같은 IID·vtable 순서·32/64bit 포인터 크기·HRESULT marshal을 사용한다. 특히 Seek/CopyTo의 64bit 길이와 out 포인터, STATSTG 문자열 해제를 대조한다. 필요한 메서드만 선언하면서 vtable의 중간 메서드를 생략하지 않는다. 구현 전에 로컬 Windows SDK 정의와 대조하고 근거 경로를 코드 리뷰 기록에 남긴다. SDK를 확인하지 못하면 임의 시그니처로 빌드 통과만 시키지 않는다.

### 2.5 예외·Dispose 계약

```text
CaptureFrom:
  result = empty snapshot
  try:
    for each format:
      acquire owned format
      acquire source medium
      try: deep copy -> transfer copied ownership into result
      finally: release source medium
    return result
  catch:
    dispose all accumulated entries
    rethrow sanitized category
```

`Dispose()`는 여러 번 호출해도 동일하게 안전해야 한다. 부분 복제 단계에서 사본 COM interface, HGLOBAL, ptd 중 어떤 것이 누구 소유인지 표로 작성한다. `Marshal.FinalReleaseComObject`를 공유 RCW에 무조건 호출하는 방식은 사용하지 않는다.

### 2.6 입력 없는 검사 구현

신규 `scripts/e2e-desktop` 내부 순수 테스트 프로젝트 또는 기존 적합한 테스트 프로젝트에서 `CaptureFrom`을 호출한다. 테스트 프로세스가 `OleGetClipboard/OleSetClipboard/OleFlushClipboard`, UI, SendInput을 호출하지 않게 OS 경계와 copy 함수를 분리한다.

| 검사명 제안 | 입력 | 기대 결과 |
|---|---|---|
| CaptureFrom_Empty | format 0개 | empty snapshot, 성공, 실제 clipboard 호출 0 |
| Stream_Independent | `00 01 FF 41` memory stream, 원본 cursor=2 | 사본 bytes 동일, 원본 cursor=2, 원본 변경/해제 후 사본 동일 |
| Stream_TwoGetData | 동일 format GetData 두 번 | 서로 다른 독립 medium. 첫 반환을 해제해도 두 번째 정상 |
| Storage_Nested | root stream A + child/stream B | stream 이름/byte/type 구조 동일, 원본 해제 후 읽기 가능 |
| TargetDevice_Ownership | 유효 ptd + Next/Clone/GetData 반복 | 반환 ptd별 독립, 반환 해제 후 내부 정상 |
| Budget_Boundary | 정확히 한도 / 한도+1 / checked overflow | 한도는 허용, 초과는 변경 전 실패, 누적 자원 회수 |
| Capture_PartialFailure | 두 번째 GetData에서 COMException | 첫 사본 해제, 원본 clipboard 변경 0, restorable 성공 record 없음 |
| Unsupported_NoMutation | FILE/알 수 없는 medium | 정해진 unsupported 오류, mutation 0 |
| Enumeration_Invalid | HRESULT 실패, fetched 불일치 | 부분 snapshot 성공 금지 |

검사 로그에는 합성 데이터의 검사 이름만 남긴다. 실제 clipboard 내용을 테스트 fixture로 수집하지 않는다. 실제 flush 후 수명 검증은 B03이며 이 단계에서 통과로 표시하지 않는다.

## 3. A02 — clipboard guard와 복원 확인

수정 위치: `Program.cs:ClipboardGuardInitialized`, `DesktopSources.capture_clipboard/restore_clipboard`, `LiveObserver.restore_case_state`, `finish_existing`.

1. guard 상태를 `capturing -> captured -> restoring -> restored|restore-failed`로 구분한다. `captured` 출력은 모든 사본 확보 뒤 한 번만 쓴다.
2. 기존 session mutex는 유지한다. abandoned mutex 처리 시 다른 guard 생존 여부·baseline 소유를 검토하고 새 clipboard mutation을 자동 허용하지 않는다.
3. guard deadline은 `ctx.end(540)`에서 계산한 원래 lease다. restore signal이나 재접속으로 lease를 연장하지 않는다. 별도 native timer의 최대 10초 복원 여유와 Python phase 예산을 일치시킨다.
4. restore 성공 JSON에도 `runId`, `caseId`, guard PID/start 식별, `startedTick`, `tick`, `frequency`를 넣는다. Python에서 원래 clipboard owner와 대조한 뒤 수용한다. 현재처럼 boolean 두 개만 읽어 성공으로 처리하지 않는다.
5. `baselineRestored=true`는 최소한 원래 retained snapshot과 OLE가 제공하는 restored object를 메모리 내 대조한 뒤 기록한다. 검증 가능 범위·flush 완료 여부는 분리한다. helper 종료 뒤 유지 여부는 사전 실증으로 연결한다.
6. 복원 호출 실패·동일성 확인 실패·시간 초과는 `baselineRestored=false` 또는 error record다. finally가 실행됐다는 이유로 true를 쓰지 않는다. 원본 정보가 섞인 COM 예외 메시지 대신 오류 분류만 기록한다.
7. Abort는 기존 guard에 restore signal만 보낸다. 원본 baseline이 없는 새 guard를 생성해 현재 시험 텍스트를 원본으로 저장하면 안 된다.
8. `restore_case_state`는 clipboard 복원 예외가 target-finish/fault-stop/blur 회수를 막지 않게 각 독립 정리를 수행하고 오류 목록을 합친다. 첫 실패는 유지한다.

추가 검사: 잘못된 runId/guard PID 응답 거부, restore 완료 파일 없음, 파일 부분 기록, 10초 경계, duplicate signal, guard 생존 중 Abort, snapshot 실패 후 finish. 모든 검사는 fake desktop bridge와 fake clock을 사용한다. 성공/오류 파일의 key 목록을 검사해 원본 content/format/hash가 새지 않는지도 확인한다.

## 4. A03 — 부분 기동과 정리 인계

### 4.1 현재 취약 경계

- `Campaign.deploy()`의 deploy 호출은 현재 preflight try 바깥이다.
- `LiveObserver.begin()`은 A open/snapshot 뒤 B open/snapshot을 수행한다. B 실패 시 A만 존재할 수 있다.
- `start_runtimes()`는 실제 launch 전 `runtime_started=True`로 바꾼다. 이 boolean만으로 양쪽 runtime 존재를 판단하면 안 된다.
- `finish_existing()`은 A/B의 DTD session 및 baseline 파일이 모두 있다는 전제로 복원한다.
- `retire()`는 `local-watchdog.json`이 있다고 가정한다. product 시작 전 실패에서는 없을 수 있다.

### 4.2 신규 소유 진행 기록

기존 owner 파일을 그대로 이용하고 추가로 최소 `lifecycle.ndjson`을 둔다. action ledger와 달리 자원 취득/확인 상태를 기록하는 용도다.

```json
{"runId":"<32 hex>","seq":1,"resource":"dtd:pc-a","state":"acquired","ownerFile":"dtd-pc-a.json","tick":123,"frequency":1000000000}
```

`resource`는 코드 enum/allowlist(`dtd:pc-a`, `dtd:pc-b`, `runtime:local`, `runtime:remote`, `clipboard:pc-b`, `prepared:remote`)로 고정한다. 자원 생성 요청 **intent**와 실제 획득 **acquired**를 구분한다. 응답 유실로 unknown이면 absence로 취급하지 않는다. arbitrary command/path를 lifecycle에서 실행하지 않는다.

쓰기 순서: 요청 전 intent → 실제 응답 owner 파일 `new_json` → acquired record. acquired 쓰기 실패라도 owner 파일을 토대로 회수할 수 있어야 한다. 기록 실패가 다음 기능 동작을 허용하지 않도록 한다.

### 4.3 deploy 실패 처리 알고리즘

```python
try:
    deployed = remote(deploy_script, original_prepare_deadline, uploads, create=True)
    preflight = remote(preflight_script, original_prepare_deadline)
except Exception as primary:
    # 원래 오류를 저장한다. remote unprepare의 예외로 덮지 않는다.
    # 이전 원격 생성 intent가 있으면 소유 상태를 제한된 호출로 조회한다.
    # 미기동·정확한 owner가 확인된 task만 unprepare 한다.
    # 배포 파일은 증거용 보존. 임의 recursive delete 하지 않는다.
    persist_prepare_failure(primary, cleanup_outcome)
    raise primary
```

현재 `Get-E2eOwner`가 필요로 하는 보호 owner 파일이 아직 없을 수 있다. unprepare는 이 경우를 명시적으로 구분한다. 단순 “owner 없음=task 없음”으로 성공시키지 않는다. task가 등록될 수 있는 단계에 도달했는지 고정 기록과 실제 task identity를 확인한다. 신원을 검증할 수 없으면 prepared state를 BLOCKED로 하고 `remainingResources/ownershipUnknown`을 남긴다.

`product-start.json`이 존재하거나 task가 Running이면 unprepare로 지우지 않는다. 기존 정상 stop/recovery 경로에 인계해야 하며, 해당 실행 소유를 못 확인하면 복구 필요로 보고한다.

### 4.4 정리 메서드의 부분 상태 처리

| 메서드 | 수정 규칙 |
|---|---|
| `release-owned-input` | `dtd.sessions`에 실제 취득한 session만 사용. session 없음은 입력이 없었다는 증거가 있을 때만 no-op |
| `stop_runtimes` | local/remote intent·acquired를 각각 검사. 기동하지 않은 쪽 cleanup.json을 영원히 기다리지 않음. 응답 유실은 owner probe로 확인 |
| `restore_case_state` | clipboard/target/fault/blur를 독립 try로 수행. 하나 실패해도 나머지 시도 |
| `independent_recovery` | baseline 누락 시 새 snapshot을 원래 baseline으로 채워 PASS시키지 않음. 확보한 baseline은 비교하고 미확보 쪽은 unverified 사유 기록 |
| `retire` | 실제 존재하고 확인된 guard만 retire. guard 없음과 시작 여부 unknown을 구분. recovery 미확인 상태에서는 살아 있는 guard 유지 |
| `finish_existing` | 존재하는 owner/session/baseline 파일을 단계적으로 로드. malformed/owner mismatch는 오류로 남기되 안전한 다른 회수는 계속 수행 |

`Context.first_failure`는 한 번 설정한 뒤 변경하지 않는다. cleanup 오류에는 operation, sanitized code, 소유 식별 메타데이터만 기록한다. `stop_and_recover`의 6개 독립 시도 원칙을 유지한다.

### 4.5 필수 회귀 벡터

신규 테스트 파일은 `scripts/test_e2e_case_recovery.py`로 만들거나 기존 RuntimeTests에 추가한다.

1. A session open 성공/B open 실패: A release/close 호출, B 가짜 baseline 생성 없음.
2. A snapshot 성공/B snapshot 실패: A baseline 보존, 전체 independent recovery false, 첫 오류 유지.
3. local controller launch 응답 유실: 재launch 0, 동일 request 조회만 허용, unknown 소유 기록.
4. deploy가 task 하나 등록 뒤 실패: 해당 소유 미기동 task 회수 시도, 최초 deploy 오류 유지.
5. preflight와 unprepare 모두 실패: 두 오류 기록, 준비 성공 아님.
6. clipboard restore 실패: target/fault 종료 시도는 수행됨.
7. watchdog 시작 전 실패: 없는 watchdog 파일을 무조건 읽는 KeyError/FileNotFound가 없음.
8. PID 재사용·owner mismatch: 해당 process kill 0.
9. 원래 runner alive: Abort가 stop 신호만 기록하고 중복 정리하지 않음.
10. 결과 파일 존재: Abort는 과거 결과를 반환할 수 있지만 현재 독립 복귀를 새로 확인했다고 쓰지 않음.

## 5. A04 — 관측의 Pending/FAIL 구분

### 5.1 변경 원칙

현재 `read()`는 `Pending`만 재관측한다. predicate가 False인 것을 통째로 Pending으로 바꾸면 실제 위반을 숨긴다. 반대로 정상 비동기 상태를 너무 일찍 predicate에 넘기면 도구가 제품 실패를 만든다.

신규 `await_event` 같은 helper는 **원본 사건 선택만** 맡긴다. action을 호출하지 않는다.

```python
def correlated_event(rows, kind, key, expected_id):
    # 관련 사건이 아직 없으면 Pending.
    # 같은 작업의 중복 terminal, 잘못된 epoch/owner, 이미 도착한 실패는 CaseError.
    # 여러 독립 작업이 있을 수 있으므로 관계없는 ID를 무조건 위반으로 분류하지 않는다.
    ...
```

`KeyError`, `TypeError`, `CaseError` 전체를 catch해서 재시도하지 않는다. schema 오류와 실제 위반은 즉시 FAIL이다. 원본 파일이 아직 안 만들어질 수 있는 지점만 FileNotFound→Pending으로 바꾼다.

### 5.2 raw record 읽기

- product trace는 이미 마지막 불완전 줄을 Pending 처리하는 경로가 있다. browser events와 desktop NDJSON도 같은 정책으로 통일한다.
- 파일은 크기 상한 내 bytes로 한 번 읽는다. 마지막 newline이 없으면 전체 snapshot을 Pending으로 처리한다. 기록된 완전 줄 안의 JSON 파싱 오류는 malformed FAIL이다.
- sequence는 같은 producer 안에서 1..N이어야 한다. PID/start가 다른 producer를 하나의 전역 sequence로 합치지 않는다.
- runId mismatch, overflow, hash mismatch, 잘못된 UTF-8, 중복 key, clock frequency<=0은 FAIL이다.
- action 이후 freshness는 현재 `observation_after` wall-clock/mtime 검사를 쓰고 있다. 파일 mtime만으로 원격 사건의 시각을 단정하지 않는다. 가능하면 producer sequence 기준 before/after를 저장하고 새 snapshot sequence로 판정한다. wall-clock timestamp는 기능 SLA 계산에 쓰지 않는다.

### 5.3 사례별 준비 조건과 즉시 실패 조건

| source | Pending으로 기다릴 것 | 즉시 실패시킬 것 |
|---|---|---|
| consent/session | 실제 consent-ui/data-ready 및 허용 채널 도착 | 다른 mask, 미승인 채널, 승인 전 native input/video |
| C01 video | marker probe 완료, 서로 다른 tick의 frame 표본 2개 | 다른 monitor/run marker, 위조/누락 owner |
| C02 denied | 실제 deny와 5초 관측 창 종료 | window 중 frame/input/channel 생성, 새 offer/data-ready |
| C03 key/reset | 실제 down/up/reset ACK 도착 | 중복 down, 다른 reset ID/epoch, 관측된 SLA 초과 |
| C04~06 admin/return | 별도 승인·desktop 전환·operator return | 잘못된 SID/session, 무승인 특권 시작, revoke 후 재개 |
| C07~09 transfer | 해당 offer complete/receipt/stream close/최종 파일 | 승인 전 binary, 잘못된 seq/hash, deny 후 전송, 중복 완료 |
| C10 chat | 대응 UI line 반영 | 중복 line, 잘못된 Unicode, 예상 밖 추가 발송 |
| C11 set/get | 같은 id 응답·UI 반영·OS 비교 | 다른 id/hash, 자동 요청, 2초 초과 |
| C12 telemetry | fresh 표본 2개 | NaN/bool 숫자, CPU 범위 밖, 메모리 총량 불일치, capture/input |
| C13 worker | 같은 operation의 start/exit/result | 승인 전 시작, 복수 worker, 다른 hash/SID/session, 관리자 실행 |
| C14 dismiss | 같은 operation sent/closed/ACK/resume | ACK 전 resume, pending 중 worker, 잘못된 epoch |
| C16 fault/recovery | bound ACK/native down/fault/fence/up 사건 | 다른 PID/revision, trigger 중복, up 전 fence 없음, DTD 해제에 의한 복구 |

5초 negative window는 `observedWindowMs=5000` 상수로 결과를 채우지 않는다. 시작/끝의 같은 clock 차를 기록하고 5000ms 이상 관측했는지 검사한다. window 도중 입력 활성화뿐 아니라 해당 사례의 금지 사건이 나오면 바로 실패한다. 제품 상태의 모든 비용 높은 probe를 50ms마다 실행하지 말고 이미 수집 중인 event stream을 활용한다.

### 5.4 테스트를 만드는 방법

기존 `LiveContracts.state()`와 FakeClock을 재사용한다. source를 `[미도착, 미도착, 유효값]` 순서로 반환하는 fake를 만들고 `read()`가 같은 deadline 안에서 유효값을 반환하는지 확인한다. 반대 벡터는 `[미도착, 위반값, 유효값]`이며 위반값에서 끝나야 한다. 마지막 유효값으로 덮으면 실패다.

각 사례에서 모든 필드를 조합하는 거대한 프레임워크를 만들지 않는다. 원본 생산자별 대표 지연 1개, 위반 1개와 사례 고유 SLA/ID/권한 검사를 추가한다. 기존 103단계 순서 검사와 이를 구분해 보고한다. C15의 순수 predicate 검사는 보존 가능하지만 실행 대상 개수에는 넣지 않는다.

## 6. A05 — 시간 측정과 결과 schema

### 6.1 현재 문제와 최소 변경

`LiveObserver.independent_recovery()`는 현재 `(ctx.clock()-ctx.origin)/1e9`를 `screen_occupied_seconds`에 넣는다. 이는 화면 점유 실측이 아니다.

신규 Context 필드는 다음처럼 최소화한다.

```python
screen_start_tick: int | None = None
screen_end_tick: int | None = None
screen_measurement: str = 'unknown'  # 'bounded' or 'unknown'
phase_ticks: dict = field(default_factory=dict)
```

현재 DTD observer로 사용자의 화면 점유 시작을 정밀하게 알 수 없다면 `screenOccupiedSeconds`는 null로 둔다. 대신 A에서 첫 화면 변경 요청 **직전**의 tick과 독립 복귀 완료 **직후** tick으로 `screenOccupiedUpperBoundSeconds`를 계산하고 `screenMeasurement='bounded'`로 명시한다. 이것을 “실제 화면 점유 시간”으로 부르지 않는다.

시작은 `start_runtimes`의 첫 visible launch 또는 fixture/UI 조작보다 앞이다. clipboard-only처럼 아직 화면을 바꾸지 않은 작업의 origin을 곧바로 screen start로 쓰지 않는다. 모든 화면 동작 진입이 이 marker를 보장하는지 action 목록을 대조한다.

`write_result()`의 기존 `screen_occupied_seconds is not None` 조건을 그대로 두면 모든 새 결과가 실패하므로 함께 고친다. PASS 증거 요구를 `유효한 measurement 종류와 해당 값/근거`로 변경하고 tests의 최소 live context도 수정한다. unknown이면 실측 준비 완료로 판정하지 않는다.

### 6.2 deadline 수정 시 지킬 것

- `Context.end()`와 `remaining()` 의미는 유지한다. 새 phase 시작 때 origin을 바꾸지 않는다.
- step deadline은 `min(global_active_end, now+step.seconds)`로 한 번 계산한다.
- 원격 QPC grant는 B sample tick에 A 수신 시 남은 duration을 변환해 더한다. 이미 네트워크에서 지난 시간을 되돌려 주지 않는다.
- 이미 기한이 지난 경우의 안전 정리는 유한하게 시도하되 기능 PASS를 만들지 않는다. 900초 초과를 새로운 900초 창으로 재시작하지 않는다.
- `actualSeconds`는 결과를 만들 때의 전체 경과다. JSON write/최종 전달에 필요한 여유는 840~900초 구간에 남긴다. write 실패를 PASS로 보고하지 않는다.

필수 경계 검사: 300/540/840/900초 정확한 경계와 1ns 초과, 다른 frequency, 시간 역전, bool/NaN, cleanup이 상한 뒤 끝남, finish marker가 기능 action 직전에 생김, screen start 없는 begin 실패, screen start가 여러 번 호출됨(첫 값 유지).

## 7. A06 — 사전 증거를 만드는 고정 경로

### 7.1 범위

현재 `readiness()`는 증거를 읽어 검사한다. 이번 작업은 실제 측정을 자동으로 통과시키는 것이 아니라 **측정 결과를 엄격히 묶는 작은 로컬 도구**를 만드는 것이다. 신규 제안 파일은 `scripts/e2e_case_evidence.py`다.

CLI는 다음 2개 기능으로 제한한다.

```text
python -B scripts/e2e_case_evidence.py --action inspect --config <절대 경로>
python -B scripts/e2e_case_evidence.py --action assemble --config <절대 경로>
    --measurements <측정 인덱스 절대 경로> --output <신규 증거 경로>
```

inspect는 로컬 파일만 읽고 누락된 증거 항목·hash mismatch를 출력한다. assemble은 준비된 측정 artifact를 검증해 현재 `remoteassist-e2e-readiness-v1`을 생성한다. 어떤 기능도 DTD/SSH/SCM/product를 호출하지 않는다. 임의 shell command/callback 입력을 받지 않는다.

### 7.2 측정 인덱스의 필수 값

기존 sourceEvidence의 `checks.*`와 budgets를 유지하되, 입력 측정 결과에는 다음 provenance가 있어야 한다. 단순 boolean 파일만으로 실측을 인정하지 않는다.

```json
{
  "schema": "remoteassist-e2e-measurements-v1",
  "candidateSha256": "<actual sha256>",
  "caseIds": ["C01"],
  "toolHashes": ["<actual sha256>"],
  "identity": {"a": {"sid": "<actual>", "session": 1}, "b": {"sid": "<actual>", "session": 2}},
  "measurements": [
    {"check": "cleanup", "artifact": "<absolute path>", "sha256": "<actual>",
     "producer": "<fixed supported producer>", "mode": "witnessed",
     "runId": "<measurement run>", "startTick": 1, "endTick": 2, "frequency": 1000000000,
     "outcome": "PASS"}
  ]
}
```

이는 schema 예시이며 실제 증거 파일로 사용하지 않는다. `<...>`나 예시 tick을 넣은 파일은 reject한다. producer는 지원한 고정 측정 스크립트/record 종류의 allowlist다. 임의 producer 문자열을 신뢰하지 않는다. hash는 무결성이지 진실성/서명이 아니므로 신뢰한 측정 도구와 담당자의 실증 기록까지 연결해야 한다.

### 7.3 생성 알고리즘

1. config에서 사례·후보·도구·identity만 읽는다. 현재 `read_config()`는 sourceEvidence가 이미 있어야 하므로 그대로 호출하면 최초 생성이 막힌다. 공통 config 구조 검사와 “증거 파일 존재 요구”를 내부 함수로 분리한다. Validate/Prepare/Run의 엄격함은 유지한다.
2. `freeze_inputs`에서 도구 inventory 계산을 순수 `tool_inventory(config)`로 추출한다. config/sourceEvidence 자체는 현재처럼 toolHashes 집합에서 제외한다. 존재하지 않는 증거를 가짜 파일로 만들어 순환 의존을 해결하지 않는다.
3. 각 artifact를 safe_path/크기/hash/owner/schema로 검사하고 해당 check의 실제 성공 필드를 읽는다. stdout 문자열에 PASS가 들어 있는지로 판정하지 않는다.
4. 요구 checks의 case 적용 표를 코드에 둔다. 이번 구현에서는 기존 7개 checks를 그대로 유지하고, 동일 후보/도구/환경에 적용되는 공통 사전 UAC 경계 증거를 재사용한다. `not-applicable`로 통과시키는 신규 schema는 만들지 않는다. 공통 증거가 없으면 boundedUac missing으로 보고한다. 사례별 조건 완화는 별도 범위 변경 때 다룬다.
5. measuredBudgets는 같은 clock의 phase begin/end 차로 계산한다. 코드 상수 300/240/300을 측정값에 복사하지 않는다. 개별 측정의 합을 종단 실측으로 둔갑시키지 않는다. 해당 종단 측정이 없으면 total budget 미검증으로 남긴다.
6. 후보/도구/identity가 같고 case 적용 범위가 명시된 측정만 재사용한다. proof를 새 run에 연결할 때 실제 회차 전 readiness identity 확인도 유지한다.
7. 모든 요구가 충족할 때만 `new_json`으로 신규 output을 만든다. overwrite 금지. 부족하면 누락 목록 + 종료 코드 10을 출력하며 READY 증거를 쓰지 않는다.

테스트: sourceEvidence 없는 최초 inspect, config/proof hash 순환 없음, artifact 변경, wrong candidate/tool set, 합성 mode, 허용되지 않은 producer, wrong SID/session, phase 상한, terminal measurement 누락, C15 제외. 테스트는 임시 로컬 파일만 사용한다.

### 7.4 실측 생산자 연결

기존 heartbeat/Job/guard 자체 검사에서 어떤 record를 실제 생성하는지 먼저 찾는다. 재사용 가능한 생산자가 없으면 별도 fixture 검사 스크립트를 고정 입력·timeout·정리로 작성한다. 작성과 구문 검증까지만 이번 범위다. 실제 실행은 B에서 허용 후 진행한다. 제품 회차를 Arm해서 readiness 자체를 얻는 순환 경로를 만들지 않는다.

## 8. A07 — UI/파일/브라우저의 구체적인 수정 기준

### 8.1 selector 검토

`e2e_case_ui.py:rules()`의 모든 target에 대해 제품 XAML/C# 또는 Viewer DOM의 원본 ID를 확인한다. HTML id가 Chrome UIA AutomationId와 같을 것이라고 단정하지 않는다. 실제 UIA mapping은 B03 자료가 필요하다.

신규 로컬 계약 검사는 다음을 확인한다.

- CASES action이 참조하는 target이 rules에 존재한다.
- ownerRole은 viewer/tray/fixture 중 하나이며 실제 owner binding 생성 경로가 있다.
- 실제 launch에서 얻는 pid/createdAt/imagePath를 요청 직전에 다시 결합한다.
- selector가 0개면 deadline까지 읽기만 기다릴 수 있다. 2개 이상이면 즉시 실패하고 첫 번째를 고르지 않는다.
- overrides는 기존 target의 selector만 바꾸며 executable/PID/command/callback을 삽입하지 못한다.

### 8.2 좌표와 OCR

DTD elements bounds는 이미 window-client 좌표다. desktop origin을 다시 빼지 않는다. Browser 영상 viewport와 B monitor 실제 크기에서 normalized 좌표를 만든 뒤 A window-client 좌표로 변환한다.

테스트 벡터는 monitor origin (-1920,0), DPI 150%, letterbox 상하/좌우, downscaled video, capture 만료, windowId 변경, zero width/height, NaN 좌표다. 실제 클릭은 하지 않고 `map_video_point`의 출력 및 reject만 검사한다.

C04 OCR은 현재 A 캡처에서 UAC scene + 유일한 Cancel/No를 찾은 결과만 쓴다. 사각형이 capture 밖/영상 밖이거나 후보가 2개면 실패한다. OCR 실패를 ESC/Enter로 대체하지 않는다. C05 security screen과 UAC 분류가 섞이지 않는 합성 OCR 결과 검사를 추가한다.

### 8.3 native 파일 경로와 이벤트

`approve-receive`는 텍스트 입력 뒤 read_field 재확인을 유지한다. 경로 비교는 slash/case 정규화만으로 끝내지 않고 구성 시 safe owner path와 no-reparse를 검증한다. source/receive 경로를 실행 중 새로 정하지 않는다.

`save-picker-approved`는 경로 입력의 성공이 아니라 실제 native picker resolve다. `save-stream-closed`는 실제 writer.close promise 완료 뒤에만 기록한다. close reject면 정상 closed 이벤트를 만들지 않는다. C08은 B send dialog ID 대신 Viewer가 받은 offer ID와 비교한다.

추가 Node 검사: picker reject, createWritable reject, write reject, close reject, 한 promise 여러 관측 hook 호출, 원래 메서드 this binding 유지, 호출 횟수 1회. 민감 문자열을 synthetic token으로 넣고 serialized observation에 없는지 검사한다.

### 8.4 UI 실측 전 남겨야 할 결과

검사 결과를 `static-contract-verified`로만 기록한다. 실제 창 제목/AutomationId/locale/native picker/OCR 정확도가 확인되었다고 쓰지 않는다. B03 확인표에는 target 이름, 현재 owner, selector 일치 개수, capture hash/시각, 확인 결과만 남기며 PIN value는 수집하지 않는다.

## 9. A08 — C16 서비스 사망 후 audit 계약

### 9.1 확인한 코드와 아직 확정하면 안 되는 결론

`HostSessionSupervisor`의 정상 stop 작업은 `AuditKind.SessionEnded`를 기록한다. `RecoveryRuntime.Release()`는 Helper fence와 입력 up를 담당하며, 확인한 현재 구현에는 SessionEnded append가 없다. C16 predicate `END`는 SessionEnded 1개를 요구한다. 따라서 서비스 강제 종료 시 다른 실제 생산자가 없는지 먼저 추적해야 한다. 이 불일치를 expected 값으로 채우거나 audit predicate를 제거해 감추면 안 된다.

조사 명령은 다음처럼 로컬 검색으로 제한한다.

```powershell
rg -n 'SessionEnded|AuditKind.SessionEnded|recovery-bind' src server scripts
rg -n 'RecoveryScope|recovery.Start|Recovery.Start' src/RemoteAssist.Host.Service
```

audit가 중앙 signaling과 Host 양쪽에 존재하더라도 현재 `e2e-product-evidence.ps1`가 어느 저장소를 읽는지 확인한다. 다른 저장소의 사건을 같은 로컬 사건인 것처럼 합치지 않는다. 실제 계약에 맞는 생산자가 이미 있으면 source 연결만 고친다.

### 9.2 생산자가 없을 때의 변경 절차

이 경우는 **시험 도구 누락뿐 아니라 제품의 crash 종료 audit 계약 보완**이다. 먼저 `docs/architecture.md`, `docs/protocol.md`의 해당 감독/로컬 IPC 계약과 관측 계약 문서를 갱신한다. 서비스 정상 종료와 Recovery 사망 처리의 audit 책임을 명시한다. 제품 변경을 하지 않고 이 항목을 완료 처리할 수 없다.

구현 방향은 기존 Recovery 프로세스의 SYSTEM 권한과 실제 세션 바인딩을 재사용하는 것이다. 새 시험용 SYSTEM writer를 만들어 제품 audit를 대신 쓰지 않는다.

1. `RecoveryScope.Start`의 호출자에서 실제 SessionId와 ConnectionId를 확보한다. 현재 recovery-bind는 SessionId/PermissionGeneration/UserSid/HelperPid만 전달하므로 ConnectionId를 추가해야 한다. DTO와 송수신 양쪽을 함께 수정한다.
2. Recovery가 인증된 IPC bootstrap의 service PID에 대한 handle을 취득해 유지한다. PID 숫자만 저장하지 않는다. 입력 up 후 서비스 handle의 exit를 확인해 실제 서비스 사망인지 판단한다. pipe 종료만으로 서비스 사망으로 단정하지 않는다.
3. 정상 stop과 crash stop의 중복 종료 event를 막을 제품 API가 필요하다. `AuditJournal`은 현재 프로세스 내부 lock만 사용하므로 서로 다른 프로세스가 append/rotate하면 경쟁할 수 있다. 단순히 Recovery에서 기존 Append를 호출하는 것으로 끝내지 않는다.
4. 최소 제안은 `AppendSessionEndedOnce(session, connection)`이다. 서비스와 Recovery 모두 같은 API를 사용하고, 검증된 보호 audit 경로에서 같은 cross-process mutex 아래 기존 terminal event 확인→append를 수행한다. mutex는 SYSTEM/관리자만 접근하도록 보호하고 대기 상한을 둔다.
5. 기존 audit 파일은 current + 최대 3개 rotation, 각 4MiB다. 검색은 이 범위와 protected pending spool을 벗어나지 않는다. 동일 SessionId/ConnectionId/Kind=SessionEnded의 기존 event가 있으면 그 event를 반환한다. stale 다른 session을 재사용하지 않는다.
6. 기존 `Append`의 spool-first 동작을 검토한다. crash가 spool 저장과 main journal append 사이에 발생하면 같은 EventId를 복구해야 한다. 새 EventId로 두 번째 SessionEnded를 만들지 않는다. spool에서 발견한 기존 event를 journal에 보충할 때에도 동일 mutex·원본 EventId·원래 Utc를 유지한다.
7. terminal event는 PermissionGeneration=0, 실제 SessionId/ConnectionId, Result=Stopped 계약을 유지한다. 입력 복구 성공 여부와 세션 종료 사실은 다르므로 Recovery가 실패했다고 세션 종료 사실을 만들어 숨기지 않는다. 두 결과는 별도로 기록한다.
8. audit 쓰기를 fence/up 이전에 넣지 않는다. 입력 복귀의 3초 SLA를 audit I/O 때문에 지연시키지 않는다. up 이후 유한 timeout으로 기록하고 실패는 실제 audit 미확보로 남긴다.
9. mutex/저장 경로 검증 실패, torn journal, spool overflow는 audit FAIL이다. 오류를 무시하고 성공 event를 합성하지 않는다.

이 절은 제품 변경의 구현 방향까지 지정하지만 cross-process audit crash 원자성은 별도 리뷰가 필요하다. 기존 AuditJournal의 다른 writer/rotation/Reporter와 충돌하는지 검사한 뒤 완료 판단한다. 이 범위가 커지면 4~8시간 잠정 추정의 초과 사유로 별도 보고하며 조용히 대규모 감사 플랫폼을 만들지 않는다.

### 9.3 입력 없는 검사

production `OpenProtected()`를 실행하지 않고 임시 디렉터리와 경로 검증 delegate·fake time·fake process liveness를 주입한다. 기존 AuditJournal 테스트의 내부 생성자 패턴을 재사용한다.

- 정상 service end 뒤 recovery end → 동일 event 1개.
- recovery end 뒤 지연된 service end → 동일 event 1개.
- spool만 존재 → 동일 EventId로 journal 보충, 중앙 보고 중복 식별 유지.
- service handle alive/pipe disconnected → crash 전용 append 0.
- 다른 session/connection → 기존 event와 혼동 없음.
- mutex timeout/path ACL 검증 실패 → 성공 반환 없음.
- 입력 fence/up 호출 순서가 audit보다 앞임.
- C16 fake trace에서 wrong PID/revision, fault 500ms+1, recovery 3000ms+1, native `f12HeldBefore=false`, serviceRestarted=true는 모두 실패.

## 10. A09 — 신규 설정과 빌드 결과 만들기

### 10.1 설정 생성

신규 작은 로컬 도구 `scripts/e2e_case_config.py`를 제안한다. 입력은 검증된 환경 template + case ID + 새 runId다. 임의 command/action/expected override 필드를 받지 않는다. schema가 허용하지 않는 필드는 거부한다.

생성값은 현재 `read_config()`와 같아야 한다.

| 필드 | 생성 규칙 |
|---|---|
| caseId | C01~C14 또는 C16. C15 생성 거부 |
| runId | uuid4 hex. 과거 ID 및 기존 runDir와 충돌 거부 |
| runDir | 저장소 `.artifacts/e2e-<case>-<runId>` 절대 경로. 아직 없는 디렉터리 |
| expectedMask | runtime.MASKS에서 가져옴. 입력으로 덮어쓰지 않음 |
| a/b | 고정 주소와 machine 이름, 검증된 실제 SID/session. 세션을 추측해서 생성하지 않음 |
| signalingOrigin/viewerUrl | `https://192.168.0.61:25001`, 뒤 `/`. relay query 없음 |
| staging | 기존 허용 정규식에 맞는 B 사용자 하위 신규 경로. 원격 존재 여부는 Prepare 전 외부 검사 |
| candidateSha256 | 실제 candidate.json hash |
| caseInputs C07~09 | runtime의 고정 파일 경로 규칙으로 계산 |
| C04 trigger | 실제 서명 검증된 cmd.exe hash 입력. 다른 C에 임의 trigger 허용 안 함 |
| C04~06 recovery | 지정 operator와 run/case 결합 evidence 경로 |
| uiRules | 고정 기본값 또는 검증된 allowlist override |

SID/session/sourceEvidence가 미확보면 생성물이 `DRAFT_NOT_READY`임을 별도 보고한다. 가짜 값을 넣어 read_config를 통과시키지 않는다. Run은 지금처럼 증거가 모두 존재해야 한다. template에 비밀 내용 대신 기존 credential 파일 참조만 둔다.

### 10.2 빌드 wrapper 검증

`scripts/e2e-case-build-tools.ps1`는 product/UI를 실행하지 않으므로 신규 `.artifacts` 출력에서 빌드 가능하다. 먼저 PowerShell parse와 경로 검사를 하고 실행한다. 이미 있는 출력 경로를 덮지 않는다. 경로 reparse/외부 경로는 거부한다.

검사해야 할 실제 산출물: desktop-tool/E2eDesktop.exe 및 runtime 의존 파일, input-target/RemoteAssist.InputTarget.exe 및 의존 파일, `build-result.json`의 실제 두 경로. 결과 JSON만 있고 exe가 없으면 실패다. `--self-check`는 기능/COM 복원 검사가 아니라 초기 실행 진입 확인임을 명시한다.

제품 후보는 기존 `package-host-dev.ps1` 옵션을 실제 param 블록에서 확인한 뒤 사용한다. 문서에 없는 옵션을 추측하지 않는다. 현재 후보 source hash와 변경된 코드가 달라지면 재패키징한다. 기존 live_Release DTD를 임의 덮어쓰지 않는다.

## 11. A10 — 범위와 생성 문서 일치

1. `update-e2e-case-mappings.py:render`의 공통 문장 중 C15 collector를 모든 C의 현재 외부 조건처럼 쓰는 부분을 수정한다.
2. `live_blockers('C15')`는 이번 EXCLUDED를 설명한다. C15 과거 명세가 필요하면 별도 “향후 재포함 시 조건”으로 표시한다.
3. 기존 C15 CLI 차단을 유지하고, 직접 `prepare_case`/`package_campaign`을 호출하는 내부 경로에도 scope 확인을 넣을지 검토한다. `prerequisites`만 차단하더라도 그 앞에 외부 호출이 없어야 한다.
4. 다른 사례는 `ForceRelay=false` 및 viewer relay query 없음 검사를 유지한다. 실제 selected pair에 relay가 관측되면 `direct-path-assumption-not-met`로 현재 회차를 중단·정리하고 직접 연결 전제 불충족을 보고한다. selected pair가 아직 없으면 기존 연결 deadline 내에서 기다리며 임의로 direct라고 채우지 않는다. 자동으로 C15를 실행하거나 제품의 정상 TURN 기능을 저장소 전체에서 삭제하지 않는다. 해당 검사를 기존 연결 준비 단계에 넣어 standalone TURN 실증 단계를 추가하지 않는다.
5. 사례 wrapper를 삭제하지 않는다. C15 Abort/Collect는 과거 자원 처리용으로 유지한다.
6. 통합 구현계획 파일은 현재 작업 트리에 없는 것으로 확인했다. `rg --files`, git tracked 경로와 상태를 확인하고 사용자 변경으로 삭제된 파일이면 덮어 복구하지 않는다. R30 갱신을 수행하지 못하면 그 이유를 보고한다.

문서 생성 뒤 각 C의 source link가 존재하는지 검사한다. 신규 helper/test 링크를 추가할 때 아직 없는 파일이면 “신규 예정” 텍스트로만 적고 완료 링크로 제시하지 않는다.

## 12. A11 — 검증 명령과 실패 처리

다음은 현재 존재하는 입력 없는 검사다. 각각 종료 코드를 확인한다. PowerShell에서 여러 명령을 이어 실행한 뒤 마지막 exit code만 보면 앞 테스트 실패가 숨을 수 있다.

```powershell
python -B -X utf8 scripts/test_e2e_cases.py
python -B -X utf8 scripts/test_e2e_case_live.py
python -B -X utf8 scripts/test_e2e_case_scope.py
node --test tests/e2e/browser-observer.test.cjs
node --check scripts/e2e-browser-observer.cjs
node --check scripts/e2e-viewer.cjs
python -B -X utf8 scripts/update-e2e-case-mappings.py --check
git diff --check
```

신규 테스트 파일은 구현 후 같은 방식으로 추가한다. 없는데 실행 성공했다고 보고하지 않는다. Python 검사는 socket/subprocess/live transport를 패치해 호출되면 즉시 AssertionError가 나게 한다. COM copy 검사는 실제 clipboard API 접근 경계를 fake로 대체한다.

PowerShell은 `System.Management.Automation.Language.Parser.ParseFile`로 변경 파일과 e2e wrapper를 모두 검사한다. parser가 0개 error인지 확인하고 스크립트를 dot-source하여 “구문 검사”하지 않는다. dot-source는 부작용을 실행할 수 있다.

.NET은 제품 솔루션과 E2eDesktop/InputTarget을 빌드한다. 저장소 obj 쓰기가 제한되면 workspace/TEMP의 별도 props로 BaseIntermediateOutputPath/OutputPath를 옮기고 기존 obj/bin을 compile glob에서 제외한다. 권한 오류 때문에 user 파일을 지우거나 제품을 실행하지 않는다. restore/build 로그의 실제 오류·경고 개수를 기록한다.

코드 검토 체크: path traversal/reparse, arbitrary command 삽입, secret 로그, control 승인 없이 admin 실행, 잘못된 owner 종료, completionUnknown 재전송, catch-all Pending, null을 expected로 채움, 독립 guard 조기 종료, 첫 오류 덮기, 파일 hash/bytes 검증 누락이다.

## 13. A12 — 문서/결과 작성 규칙

각 작업 완료 기록은 아래 형식으로 남긴다.

```text
작업 ID:
상태: IMPLEMENTED_OFFLINE_VERIFIED / IMPLEMENTED_UNVERIFIED / BLOCKED / DEFERRED
변경 파일·함수:
실제 해결한 문제:
입력 없는 검사 명령·종료 코드·검사 수:
남은 실제 실증:
외부 조건:
실제 소요 시간(측정했다면):
```

최종 보고에서 최소한 다음을 분리한다.

- 구현하지 못한 코드/지원 형식.
- 코드가 있지만 실제 A/B에서 검증하지 않은 동작.
- 외부 담당자·환경·실측 증거 때문에 막힌 조건.
- 이번 범위에서 제외된 C15 및 후순위 범용 확장.

전체 완료라는 표현은 활성 15개 명세에 필요한 구현이 실제로 연결되고 관련 코드 검사가 통과했을 때만 사용하되, E2E 미실행이면 그 사실을 바로 함께 적는다. C11 baseline 일부를 버리거나 C16 audit 검사를 빼서 완료 수치를 맞추지 않는다.

## 14. B/C — 후속 실증에 넘길 정확한 자료

이번에는 아래 파일/목록을 준비만 한다. 실제 내용을 관측하지 않았으면 미확보로 남긴다.

| 준비물 | 반드시 담을 것 | 없을 때 |
|---|---|---|
| 환경 기록 | A/B 주소·DTD exe/config hash·SID/session/Default·브라우저 locale/version·포트·TLS 신원/체인 | 환경 미확인 BLOCKED |
| 후보 기록 | candidate.json SHA·현재 source SHA·바이너리 목록·설정 ForceRelay=false | 오래된 후보 사용 금지 |
| 사전 측정 | watchdog/SCM/UAC/정리/외부 복귀/TLS의 실제 producer·clock·artifact·hash·적용 범위 | Arm 금지 |
| UI 확인표 | target별 unique selector·현재 owner·capture 만료·OCR 분류 | 실행 중 탐색 금지, 해당 사례 BLOCKED |
| C11 증거 | 지원 baseline 전체의 메모리 복제·실제 flush·helper 종료 후 유지·10초 복원 | C11 준비 완료 아님 |
| C04~06 담당자 | operator 신원·콘솔 접근·READY/RETURNED run binding·같은 사용자 로그인 방법 | 해당 사례 BLOCKED |
| 회차 설정 | 새 runId/runDir/staging·고정 caseInputs·변경 불가 도구 hash·단계 상한 | 과거 회차 재사용 금지 |
| 중단 자료 | 기존 wrapper Abort 인수·소유 자원·현장 복귀 수단·미복귀 시 연락/보고 방식 | 실행 시작 금지 |

실제 실행 직전에는 사용자에게 목적·화면 점유 예상·첫 실패 중단·복귀 수단을 짧게 알린다. 회차 안에서 코드 수정/재빌드/selector 탐색/추가 시나리오/새 origin을 만들지 않는다. 정리 실패면 다음 사례를 실행하지 않는다.

## 15. 구현자가 작업을 끝내기 전 답해야 할 질문

1. C11은 어떤 medium을 독립 보존하며 어떤 medium에서 변경 전 차단되는가? 그 차단이 이번 baseline을 막는가?
2. A만 열렸거나 local runtime만 시작돼도 정리하는가? 오류가 나도 나머지 독립 정리를 시도하는가?
3. 지연된 정상 사건은 기다리고, 실제 위반은 뒤의 정상 사건으로 덮지 않는가?
4. screenOccupiedSeconds는 실제 측정인가, 상한인가? 결과 schema에 그 구분이 드러나는가?
5. 신규 sourceEvidence를 만드는 데 가짜 기존 sourceEvidence가 필요하지 않은가?
6. C16의 서비스 사망 뒤 SessionEnded를 실제로 쓰는 제품 프로세스는 무엇인가? 중복/유실을 어떻게 검사했는가?
7. 새로운 검사가 실제 desktop/clipboard/서비스/SSH를 만지지 않았다는 경계가 있는가?
8. C15는 제외 상태이며 다른 사례의 준비를 막지 않는가?
9. 문서의 구현 완료 링크가 실제 파일/함수를 가리키는가?
10. 입력 없는 검증과 향후 실증을 구분해 보고했는가?

어느 질문에도 근거를 제시하지 못하면 해당 항목을 미완료로 남긴다. 추측으로 체크박스를 채우지 않는다.
