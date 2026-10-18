# 회차 위임·시나리오·어댑터 운영

- [x] D12 회차 소유자 서명·명령/창/제품 권한과 비공개 증거 정책을 구현한다.
- [x] D14~D18 범용 실행·제품 어댑터·fixture·V/F 대응과 판정을 구현한다.
- [x] D19 모의 준비·실패·정리/복귀를 검증하고 실환경 승격을 차단한다.
- [x] D20 게시된 독립 예제·설치/제거·운영 절차를 검증한다.

이 문서는 0.2.0/v2 운영 기준이다. 설치는 [README](../README-10.md), 상세 권한 경계는 [계약](contracts-9.md), 실행 결과는 [검증 증거](verification-11.md)에서 확인한다. 실제 머신 시험은 [D21~D23](../../docs_working/e2e-desktop-driver-plan.md)에 남아 있다. 이 문서의 명령 중 `simulate`, `plan-check`, `catalog-check`는 제품이나 OS 입력을 실행하지 않는다.

## 소유자 등록과 회차 승인

소유자는 배치와 분리된 전용 디렉터리에서 키를 만든다. 디렉터리는 새 경로여야 하며 현재 사용자 전용 ACL이 적용된다. 개인키 파일의 내용은 출력하거나 패키지·inventory·git에 넣지 않는다.

```powershell
$server = 'C:/DesktopTestDriverPackage/Server/Server.exe'
& $server owner-init C:/DesktopTestDriverPrivate lab-owner
$env:DTD_TRUST_STORE = 'C:/DesktopTestDriverPrivate/owners.json'
& $server plan-check C:/DesktopTestDriverPackage/scenarios/examples/counter.json
```

`owners.json`에는 owner, keyId와 P-256 공개키만 있다. 공개키는 별도 신뢰 경로로 확인해 Server와 해당 시험 머신의 콘솔 계정에 등록한다. Server는 서로 다른 머신 소유자 키를 모두 등록할 수 있다. Client는 서명 소유자와 현재 Windows 계정도 대조한다. 배치에서 도착한 키를 자동 등록하지 않는다. 실제 Interactive 작업에는 `$env:`의 일시 설정이 전달된다고 가정하지 않고 해당 시험 계정의 사용자 환경 또는 승인된 실행 환경에 `DTD_TRUST_STORE`를 사전 등록한 뒤 입력 없이 확인한다. 개인키는 서명할 소유자 PC에만 둔다.

[run.template.json](../inventory/run.template.json)을 비공개 디렉터리에 복사한다. 다음 값을 검토해 채운다.

- 회차/머신/역할·실제 콘솔 Session·Default desktop·15분 이내 UTC 만료다.
- owner는 콘솔 Windows 계정, requester는 실제 동의 UI에 나타날 예상 이름이다. 이 이름은 제품의 자기 신고 신원을 대체하지 않는다.
- scenarios와 scenarioHashes에는 승인할 계획 ID와 `plan-check`가 반환한 정확한 hash를 함께 넣는다. 계획 수정 시 새 서명이 필요하다.
- actions는 실제 필요한 명령만, productPermissions는 screen/control/admin/file/chat/clipboard/diagnostics/runbook 중 상한만 지정한다.
- windows에는 실행 파일 절대 경로·SHA-256·정확한 창 제목을 지정한다. allowedKeys와 hold/frame 상한도 검토한다.
- observationFiles에는 읽을 파일의 논리 ID·로컬 절대 경로·최대 크기·json/ndjson/file 유형만 넣는다. file 유형은 본문 대신 SHA-256/크기를 반환한다. 재분석 지점과 UNC는 거부한다.
- evidence는 images 허용 여부, 최대 바이트와 보존 시간을 지정한다. 처음에는 R0/allowInput:false로 시작한다.

```powershell
& $server authorize C:/DesktopTestDriverPrivate/unsigned.json `
    C:/DesktopTestDriverPrivate/owner-private.pem `
    C:/DesktopTestDriverPrivate/signed.json
```

입력에는 실제 R1 복귀 증거가 필요하다. UAC·잠금·SAS·rollback은 R2 독립 콘솔 증거를 추가 요구한다. 서명은 대상·기간·범위를 한정한 시험 위임이며 제품의 실제 동의 UI를 제거하지 않는다. 소유자가 서명한 범위 안에서 개발자 도구가 동의 UI를 클릭한다. 제품에 자동 승인 옵션·승인 IPC 우회를 추가하지 않는다.

## 다른 시험에 재사용하는 독립 예제

[CounterAdapter](../src/Core/CounterAdapter.cs)는 제품·화면·입력·SSH가 없는 두 번째 시험 대상이다. 위 템플릿의 role을 observer, scenario를 counter-sample, 복귀를 R0으로 두고 해당 예제 계획 hash로 서명한 뒤 실행한다.

```powershell
& $server simulate C:/DesktopTestDriverPackage/scenarios/examples/counter.json `
    C:/DesktopTestDriverPrivate/signed.json C:/DesktopTestDriverPrivate/counter-bundle
& $server bundle-check C:/DesktopTestDriverPrivate/counter-bundle
```

실행은 sample-observer의 count가 1인지 검사하고 정리한다. 공개 결과에 simulated:true와 제품 수용 NOT RUN을 남긴다. `simulate`는 CounterAdapter만 등록하므로 RemoteAssist 계획을 넣으면 거부한다. 다른 앱에는 `IScenarioAdapter`의 Prepare/Execute/Cleanup을 구현하고 `AdapterRegistry.Register`로 명시 등록한다. 범용 Client에 제품 코드를 넣지 않는다.

```csharp
var registry = new AdapterRegistry();
registry.Register(new CounterAdapter());
var runner = new ScenarioRunner(registry);
var report = await runner.Run(plan, signedContext, cancellation);
EvidenceBundle.Write(newPrivateDirectory, plan, report);
```

실제 UI 앱 어댑터는 `NodeUi`의 정확한 창/frame 관측과 `UiSelector`의 유일한 UIA ID/이름을 사용한다. `Click`은 관측한 컨트롤 중앙 좌표를 Client의 frame-guarded SendInput으로 전달한다. UIA InvokePattern이나 추측 좌표를 쓰지 않는다. 실행 명령의 action-receipt는 접수 증거다. 기능 PASS에는 별도 애플리케이션 관측 assertion을 둔다.

## RemoteAssist 연결과 관측

게시 패키지의 `Server/RemoteAssist.Adapter.dll`은 제품 소스/DTO/IPC에 참조하지 않는다. 준비된 두 `SshTransport`를 각각 `NodeUi`에 연결하고 host의 `TrayUi`, 전용 시험 브라우저의 `CdpViewer`, `ReadOnlyProductEvidence`, `TrayPairingSecrets`로 `RemoteAssistAdapter`를 구성해 등록한다. 실제 실행에는 PreparationReport를 반환하는 preparation callback도 필수다. `Preparation.Observe`는 해당 머신의 pinned SSH Broker에서 Client/Watchdog 생존을 확인하며 Server의 로컬 PID 표와 대조하지 않는다. 관측과 실행에는 같은 NodeUi 인스턴스를 사용해 명령 순번을 유지한다. 복귀 callback은 양쪽의 ledger·소유 자원·정상 desktop과 외부 복귀 receipt를 확인해야 한다. true를 고정 반환해 실제 준비를 통과시키지 않는다.

CDP는 시험 전용 Chrome의 loopback TCP 25002 같은 허용 포트와 정확한 HTTPS Viewer URL만 연결한다. 자동으로 브라우저를 시작하거나 인증서 오류를 무시하지 않는다. 이미 준비한 전용 탭에서 `CdpConnection.Connect(endpoint, expectedUrl, token)`을 호출한다. 위치/크기/가림·URL을 읽어 확인한 뒤 브라우저의 실제 mouse/key/insertText 이벤트를 사용한다. 제품 함수 호출, DOM 값 직접 변경이나 권한 상태 주입은 하지 않는다. 소유 키/버튼은 실패·탭 이동 뒤에도 release한다. 브라우저 종료 등 CDP 자체가 사라지면 제품 reset/Host watchdog과 독립 복귀 증거가 필요하며 정리 실패로 남긴다.

Tray는 HostStart/HostStop/ConsentScreen/ConsentControl/ConsentAdmin/ConsentApprove/ConsentDeny ID와 실제 나머지 토글 이름을 사용한다. 접속 요청자·모니터 문구와 창 신원이 매 클릭 전에 일치해야 한다. `ReadRequest`의 요청 ID는 창 신원 hash로 만든 UI 관측 ID다. 제품 내부 IPC requestId라고 주장하지 않는다. 제품 감사의 SessionId/ConnectionId는 별도로 수집해 대조한다. 요청 창 교체·중복 컨트롤·권한 불일치·관측 실패는 중단한다. expire 동작은 클릭하지 않고 이후 관측 단계에서 실제 만료를 확인한다.

제품 원격 입력 구간 앞에서 `seal-host`를 실행한다. Client는 소유 입력을 해제하고 입력/포커스 변경을 봉인한다. `viewer-pointer`, `viewer-key`, `viewer-text`는 봉인 중에만 허용한다. 종료 단계 `target-end`에서만 위임된 계획 ID로 봉인을 풀고 실제 HostStop을 클릭한다. Server의 heartbeat는 입력/wait와 독립적으로 계속되며 실패하면 이후 조작을 거부한다.

| 출처 | 판정에 사용하는 실제 관측 |
|---|---|
| browser-decoder | Chrome webkitDecodedFrameCount/droppedVideoFrames 차이와 단조 시간의 실제 FPS, 디코딩한 영상의 16칸 색 표식, 실제 영상 크기다. 디코딩 카운터가 없는 브라우저는 진행 증거를 통과시키지 않는다. |
| host-input-target | 읽기 전용 InputTarget JSON의 PID/이미지/Session/HWND/startTick/UTC, 이벤트 down/up, OS heldVirtualKeys, 문자 hash와 클릭/drag/wheel 변화다. |
| received-file | 허용된 양쪽 파일의 실제 SHA-256과 예상 fixture hash다. 수락/거절·방향·경로/취소는 별도 감사와 fixture 조건도 요구한다. |
| viewer-chat | 실제 표시 문자열의 예상 fixture 일치 횟수와 hash다. 양방향/클립보드/loop는 각각 별도 회차 조건이다. |
| product-audit | 실제 읽기 전용 감사 파일의 SessionId/ConnectionId와 Kind/Result다. 현재 제품 wire의 enum 숫자를 사용한다. |

`MarkerWindow.exe RUN_ID PRIVATE_OUTPUT SECONDS`는 회차 hash로 만든 16칸 색 표식을 표시하고 실제 screenRegion과 프로세스 신원을 기록하는 fixture다. 최대 300초 뒤 닫힌다. D21/D22에서 소유 자원으로 준비한 뒤 사용한다. screenRegion을 준비된 제품 캡처 영역·실제 영상 크기로 환산해 `EvidenceBindings.MarkerRegion`을 확정하며 좌표를 추정하지 않는다. 압축 색 오차는 채널별 40까지 허용한다. 현재 D01~D20 검사에서는 이 창을 띄우지 않았다.

## 고정 회차·fixture·수용 기준

[acceptance.json](../adapters/remoteassist/acceptance.json)은 V01~V41/F01~F12 전체 53항목과 원본 요구문, 연결할 공통 회차·환경·필수 하위 조건을 가진다. [ra-f01~ra-f12](../scenarios/remoteassist/ra-f01.json)는 준비·동의·관측·종료의 공통 회차다. 저장소에서 `python adapters/remoteassist/generate_plans.py --check`로 기준 문서와 드리프트를 확인한다. 이 생성기는 제품을 실행하지 않는다.

공통 회차는 각 F 전체의 자동 실행 대본이 아니다. 예를 들어 F03의 공통 입력 성공은 IME·blur·모든 DPI 조합을 완료하지 않으며 F06의 hash 관측만으로 파일 수락 UI 전체를 완료하지 않는다. 각 V/F 요구문의 모든 하위 조건은 해당 고정 후보의 기존 계약 검사 또는 별도 짧은 fixture 회차에 연결해 검토한다. `AcceptanceGate`는 하위 조건 증거가 빠지면 NotRun, 환경 부재는 Blocked, 모의 보고서·다른 후보는 미실행으로 남긴다. F에는 서로 다른 실제 두 머신 증거가 필요하다. 이 단계의 catalog는 전부 NotRun으로 시작한다.

| fixture | 준비와 중단·복귀 기준 |
|---|---|
| install-update-rollback | 깨끗한 VM·서명된 구/신 설치본·독립 콘솔을 요구한다. 서명/hash→구버전→업데이트→변조 거부→rollback→후보 설치/제거를 나누고 baseline snapshot으로 복귀한다. |
| service-failure | PID·생성 시각·경로/hash를 확인한 소유 제품 컴포넌트만 장애를 주입한다. Helper 종료 단계/코드/유형과 입력 해제·무동의 재개 없음이 필요하다. |
| secure-desktop | 별도 관리자 동의와 R2 실증이 필요하다. UAC→SAS→잠금은 각각 독립 짧은 회차로 나누며 독립 콘솔로 상태 관측과 복귀를 확인한다. |
| tls-turn-nat | 격리 네트워크·신뢰 시험 TLS·coturn을 준비한다. 인증서/Origin 부정 경로, 실제 ICE pair 양끝, TURN allocation·만료·세대 갱신을 기록한다. 8분 lifecycle을 짧은 smoke로 대신하지 않는다. |
| long-load | 별도 비점유 환경에서 최소 실제 7200초·100회와 합의한 부하 기준을 검증한다. 메모리/handle/GPU/queue·FPS·지연을 수집한다. 즉시 반환한 fixture receipt는 실제 장시간 PASS가 아니다. |

`IFixtureBackend`는 준비한 VM/KVM/네트워크/제품 설치 실행기에 맞춰 연결하는 계약이다. 저장소의 SimulatedFixtureBackend는 실행 순서와 실패 복귀만 검사한다. 실제 장비용 vendor backend와 실증 자원이 없으면 실환경 fixture를 실행하지 않는다. fixture JSON은 임의 shell 명령이나 관리자 인증 비밀을 포함하지 않는다. 전체 deadline·독립 watchdog·외부 복귀는 실제 실행 전에 별도로 확인한다. 제품 회차는 목표 600초/절대 900초, 화면 기능 점유는 최대 300초다. 장시간 fixture는 별도 최대 7800초 비점유 캠페인이다.

## 준비·증거·실패·제거

`Preparation.Check`는 signed context, 고정 파일 hash, 관측된 Session/desktop/window, Client/Watchdog 신원, 복귀 receipt와 증거 디스크 공간을 읽기 전용으로 확인한다. 모의 검사는 ContractsReady만 반환하며 ReadyForRealExecution은 false다. 준비 성공·프로세스 기동을 기능 PASS로 세지 않는다. 제품 Helper 조기 종료 원인과 H10이 해결됐다는 주장은 하지 않으며 D23 전에 해당 제품 게이트도 따로 충족해야 한다.

첫 실패·준비 누락·deadline에는 새 동작을 멈추고 브라우저 키/버튼 해제→양쪽 노드 stop→소유 자원 정리→정상 desktop/독립 복귀 확인→증거/결과 전달을 수행한다. feature 토큰이 취소돼도 정리에는 남은 전역 예산의 독립된 최대 5초를 쓴다. 정리 실패는 Fail이며 예약을 유지한다. 같은 회차 안의 수정·재빌드·재시도는 하지 않는다.

원본 보고서와 UI 이미지·artifact는 현재 계정 전용 ACL 디렉터리에 보관한다. `EvidenceBundle`은 계획·보고서·공개 요약의 파일별 hash와 크기를 기록하고 재검증한다. 공개 요약에는 ID/hash/판정과 모의 여부만 있으며 PIN·개인키·입력 문자열·채팅·화면·원본 adapter facts는 빠진다. `report.json`/`plan.json`은 공개 요약이 아니므로 자동 공개하지 않는다. 정책 기간이 끝난 비공개 bundle은 소유자가 정확한 bundle 경로를 확인해 제거한다.

도구 제거는 [README의 중단·수집·제거](../README-10.md#중단수집제거)를 따른다. stop/정상 desktop/빈 ledger와 배치 receipt의 configHash/packageHash 확인 뒤 소유 작업·프로세스·회차 파일만 제거한다. 기존 SSH·사용자 파일·제품 설치본을 도구 제거에 포함하지 않는다. 복귀 미확인 예약을 만료만으로 풀지 않는다. 키를 폐기할 때는 해당 공개키 신뢰 등록도 제거하며, 개인키 백업과 서명된 회차의 보존 여부를 소유자가 정한다.

## 문제 해결

- AuthenticationFailed는 외부 신뢰 경로·계정/keyId·P-256 공개키·서명 뒤 설정 변경을 입력 없이 확인한다.
- DelegationMismatch는 회차/역할/설정 hash·계획 hash·명령·제품 권한·Host 봉인을 확인한다. allowlist를 무조건 넓히지 않는다.
- ObservationUnavailable은 실제 UI ID/유일성·가림·로그 freshness·InputTarget overflow·표식/영상 크기를 확인한다. 접수 성공으로 PASS를 대체하지 않는다.
- TransportLost는 SSH host key/계정·기본 3초 lease 안의 heartbeat 가능 여부를 확인한다. 응답 유실 명령을 자동 재전송하지 않는다.
- CleanupIncomplete 또는 Fail은 먼저 독립 복귀 경로로 정상 작업 상태를 복구한다. 원인과 준비 변경을 리뷰한 새 회차 전에는 재시험하지 않는다.
