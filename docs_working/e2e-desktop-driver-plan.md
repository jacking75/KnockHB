# DesktopTestDriver — 개발자용 다중 머신 데스크톱 시험 도구 구현 계획

**도구 이름: DesktopTestDriver.** RemoteAssist 제품과 별개로 만드는 개발자·CI 전용 시험 도구다. 일반 사용자에게 배포하거나 원격 지원 제품의 구성 요소로 제공하지 않는다.

**Server는 원격으로 조작하는 PC**에서 실행해 시험을 지휘하고 결과를 수집한다. **Client는 원격으로 조작될 시험 PC**에서 실행해 화면 관측·입력·정리를 수행한다. 한 Server가 여러 Client를 제어한다. 이 Server/Client 명칭은 시험 도구의 역할이며 RemoteAssist 제품의 시그널링 서버·Host·Viewer와 다르다.

## 구현 태스크 목록

이 목록은 독립 도구의 세부 진행 상태 원본이다. 완료할 때 구현 파일·검사 명령·증거 경로를 해당 항목에 덧붙인다. **[1PC]는 개발 PC 한 대의 코드·모의 환경에서 완료하고, [2PC]는 조작 PC의 Server와 별도 실제 시험 머신의 Client를 사용하는 실행 검증**이다. 1PC 결과를 2PC PASS로 승계하지 않는다. 실제 머신의 배치·입력·복귀 검증은 맨 뒤 D21~D23에 모아 한 번의 준비·배치 캠페인에서 순서대로 수행한다. [통합 구현계획](원격제어-구현계획.md)의 H01~H14는 RemoteAssist 연동 마일스톤이며 D 완료만으로 H 또는 V/F를 완료 처리하지 않는다.

### 1PC — 구현·계약·로컬 리허설

- [x] D01 [1PC] 독립 프로젝트 경계, 지원 OS/세션, 위임·권한·위협 모델과 명령 계약을 확정한다. [계약](../desktop-test-driver/docs/contracts-9.md)을 문서 리뷰했다.
- [x] D02 [1PC] 저장소 루트의 독립 디렉터리와 자체 빌드·패키징·버전 체계를 만든다. `desktop-test-driver/build.ps1 -Package`의 독립 빌드·win-x64 게시·0.1.0 파일별 hash manifest 생성과 대조가 통과했다.
- [x] D03 [1PC] 머신 inventory, 역할, 예약·충돌 방지, 배치와 구성 검증을 구현한다. `Inventory.cs`의 역할 검증·원자 예약·미정리 예약 유지·동시 경합 검사가 통과했다.
- [x] D04 [1PC] Client의 일반 권한 콘솔 실행부, 인증된 로컬 IPC, 세션 변경 감지를 구현한다. `Client`·`Client.Broker`·`Ipc.cs`를 구현했다. 실제 로컬 pipe의 정상 전달과 인증 거부 검사가 통과했다. 실제 시험 머신의 콘솔 기동은 D21에서 확인한다.
- [x] D05 [1PC] 화면·창·UIA·모니터·DPI·foreground 관측과 프레임 신원 검증을 구현한다. `WindowsDesktop.cs`에 GDI PNG·timeout UIA·프로세스 생성 시각/hash를 구현했다. 프레임 계약 모의 검사가 통과했으며 실제 시험 머신의 화면 실증은 D22에서 수행한다.
- [x] D06 [1PC] 키·마우스·휠·드래그·문자 입력과 소유 키/버튼 ledger를 구현한다. `Input.cs`·`WindowsInput`·`CommandEngine`의 Unicode·음수 원점·부분 주입·해제 실패 검사가 통과했다. 실제 OS 입력은 실행하지 않았다.
- [x] D07 [1PC] 프레임·창·세션 전제 조건, 제한된 wait, 멱등 명령과 오류 코드를 구현한다. stale·DPI·HWND 재사용·foreground·세션 검증, wait 중 stop 우선, 재전송 멱등성·순번 검사가 통과했다.
- [x] D08 [1PC] Server의 SSH 배치·전송·제어 CLI/API를 구현하고 모의 SSH 대상으로 검사한다. `Server`·`SshTransport`·`transport/ssh_transport.py`와 Python CLI를 구현했고 실제 loopback SSH/SFTP 대상의 host key pin·배치 hash·응답 유실·rollback·수집·제거 검사가 통과했다. 실제 시험 머신의 배치·기동·제거는 D21~D23 E2E에서 검증한다.
- [x] D09 [1PC] Server의 다중 Client 예약·barrier·deadline·전역 종료 정책을 모의 노드로 구현·검사한다. `Campaign.cs`의 barrier·최초 실패·단조 deadline·전체 stop·미정리 차단 검사가 통과했다.
- [x] D10 [1PC] Client의 독립 watchdog, 강제 stop, 입력 해제와 소유 자원 정리를 로컬 고장 주입으로 검증한다. 별도 watchdog과 가짜 입력 backend에서 Client/Watchdog 프로세스 각각 강제 종료·소유 PID/hash 정리 검사가 통과했다. 실제 시험 머신의 키 해제 실증은 D22에서 수행한다.
- [x] D11 [1PC] 복귀 등급·UAC/잠금/재부팅 지원 계약과 외부 콘솔 어댑터를 설계·모의 검증한다. `RecoveryGate`·`IRecoveryAdapter`·`ExternalConsoleAdapter`의 유한 호출·모의 복귀 검사가 통과했다. 모의 receipt는 실환경 위험 시험을 승인하지 않는다.
- [x] D12 [1PC] 회차별 권한 위임, 시험 범위 allowlist, 감사·비밀·증거 보관 정책을 구현한다. `Authorization.cs`의 P-256 소유자 서명·외부 신뢰 저장소·시나리오/명령/제품 권한 상한과 비밀 본문 없는 감사·증거 예산/보존 만료 검사가 통과했다. 실제 머신 키 등록은 D21 준비다.
- [x] D13 [1PC] 제품 없는 모의 창·모의 노드에서 기능·고장 주입 자체 검증을 완료한다. `Driver.Tests` 19개가 통과했다(`desktop-test-driver/artifacts/d12-d13-contracts.txt`). 모의 프레임/입력·실제 로컬 pipe·독립 프로세스 장애를 포함하며 실제 데스크톱 입력은 실행하지 않았다.
- [x] D14 [1PC] 범용 시나리오 정의와 플러그인/어댑터 계약, 결과 판정 엔진을 구현한다. `Scenarios.cs`·`CounterAdapter.cs`의 JSON 계획/해시·명시 등록·출처/후보 결합·첫 실패 종료·유한 정리와 허위 PASS 거부를 검사했다(`artifacts/d14-contracts.txt`).
- [x] D15 [1PC] RemoteAssist 어댑터의 실제 Tray 동의·거절·종료와 Viewer 조작 로직을 구현·로컬 검증한다. `TrayUi.cs`·`CdpViewer.cs`·`NodeUi.cs`에 UIA 관측→좌표 클릭, 요청 신원/권한 대조, 거절/닫기/만료, HTTPS 탭 고정과 CDP 실제 입력을 구현했다. 모의 UI/CDP 검사로 확인했으며 실제 제품 UI 실행은 독립 도구 수용 후 별도 H12 단계다(`artifacts/d15-contracts.txt`).
- [x] D16 [1PC] RemoteAssist 영상·입력·권한·파일·채팅·모니터·품질의 출처별 관측 로직을 구현·로컬 검증한다. `ReadOnlyProductEvidence.cs`·`ProductObservations.cs`·`MarkerWindow`에 실제 디코딩/표식·입력 파일/OS held 상태·파일 hash·채팅/감사·해상도/FPS 대조를 연결했다. 모의 증거의 변조·잘못된 출처·경로 탈출·키 고착 거부 검사가 통과했다(`artifacts/d16-contracts.txt`).
- [x] D17 [1PC] 설치/업데이트/rollback·서비스·UAC/SAS·TLS/TURN/NAT·장시간 시험 fixture와 복귀 절차를 작성·모의 검증한다. `fixtures/*.json` 5종과 `Fixtures.cs`에 환경/R2·유한 호출·최초 실패·독립 복원·2시간 최소 실측 기준을 정의했다. 정상/최초 실패/복원 실패/실환경 증거 부재의 모의 검사가 통과했다.
- [x] D18 [1PC] RemoteAssist V/F 매핑, 고정 실행 계획, 증거 묶음, 리포트와 수용 기준을 검토한다. `acceptance.json`의 V41/F12 총 53항목과 공통 회차 12개, `Acceptance.cs`의 private bundle/hash 검증·공개 요약·후보/실환경/모의 분리 게이트를 검사했다. 공통 회차만으로 각 V/F의 모든 하위 조건을 완료 처리하지 않는다.
- [x] D19 [1PC] 입력 없는 준비 검사와 모의 환경의 정리/복귀 리허설을 완료한다. `Preparation.cs`에서 후보 hash·세션/desktop/창·watchdog/복귀·증거 공간을 입력 없이 검사한다. 준비 성공/변조 실패·첫 실패 정리·복원 실패 리허설을 검사했고 모의 결과의 실환경 승격을 거부했다(`artifacts/d17-d19-contracts.txt`, 25개). 실제 두 PC 제품 PASS로 세지 않는다.
- [x] D20 [1PC] 다른 제품/시험에 사용할 예제와 독립 설치·제거·운영 문서를 완성한다. `CounterAdapter`·`scenarios/examples/counter.json`과 `docs/scenarios-12.md`·README에 소유자 등록/서명·독립 예제·어댑터/fixture·증거/중단/제거를 정리했다. 게시 CLI의 서명/예제/변조 거부 7개 검사와 포함 파일 manifest 검사가 통과했다.

### 2PC — 맨 마지막 공동 캠페인

- [ ] D21 [2PC] Server가 SSH/SFTP로 시험 PC에 Client를 설치·기동하고 hash·세션·watchdog·제거/복귀 준비 상태를 확인한다.
- [ ] D22 [2PC] 제품 없이 Server↔Client 화면·입력·명령·중단·장애 복귀 E2E를 한 배치에서 검증한다.
- [ ] D23 [2PC] RemoteAssist가 없는 두 PC에서 도구 자체의 증거 수집·소유 Client/fixture/작업 제거·입력 해제·정상 desktop/SSH 복귀를 확인하고 독립 수용 보고서를 완성한다. 제품 연동·제품 V/F는 완료 조건에 포함하지 않는다.

작성일: 2026-10-18. 현재 단계: D01~D20 구현과 로컬·모의 검증 완료다. 도구는 0.2.0/프로토콜 v2이며 실제 시험 머신의 배치·입력·복귀는 뒤의 D21~D23 E2E에서 검증한다. [운영 문서](../desktop-test-driver/README-10.md)와 [검증 증거](../desktop-test-driver/docs/verification-11.md)를 참조한다.

사용자 요청에 따라 기존 E2E는 종료한 상태를 유지한다. 이전 회차의 서비스·설치·시험 작업 제거와 정상 화면 복귀는 기존 기록이다. 이번 구현에서는 제품 실행·실제 화면 입력·원격 PC 배치를 하지 않았다. 독립 도구의 D 태스크는 위 목록에서, RemoteAssist 연동의 H 태스크는 통합 구현계획에서 각각 추적하며 H/V/F를 완료로 승격하지 않는다.

## 1. 목표와 완료 기준

Server가 여러 Windows 시험 머신의 역할을 배치하고 각 Client를 통해 화면을 관측·조작하며, 실패 시 현장 인력 없이 중단·정리·복귀하게 한다. 첫 A/B 머신의 주소·계정은 inventory 예시일 뿐 설계에 고정하지 않는다. 소유자가 사전에 승인한 회차 한정 시험 위임을 실행 근거로 기록한다.

저장소 루트의 독립 디렉터리 `desktop-test-driver/`에 자체 솔루션·빌드·배포·CLI·검사를 가진 별도 프로젝트를 만든다. `RemoteAssist.slnx`, `src/`, `tools/`, `scripts/`, `tests/e2e/`에 넣지 않는다. 제품 없이도 동작해야 하고, 다른 애플리케이션은 어댑터·시나리오를 추가해 사용한다. 제품 배포물에는 포함하지 않는다. RemoteAssist 어댑터는 승인 API·IPC·권한 상태를 직접 변경하지 않고 실제 동의 UI를 클릭한다. 이 결과는 위임받은 시험 조작이며 실제 사람이 동의 내용을 이해했다는 사용성 검증을 대체하지 않는다.

도구의 독립 수용 범위는 서로 다른 두 Windows 머신의 역할 예약·동시 제어, 자체 fixture 창 관측과 이동·클릭·드래그·휠·키·문자 입력, 장애 시 입력 해제·소유 자원 정리·독립 복귀와 최종 제거다. D21~D23에서는 RemoteAssist 설치본·서비스·시그널링·Viewer·InputTarget을 사용하지 않는다. RemoteAssist 어댑터는 제품의 동의·영상·원격 입력·대상자 종료에서 시작해 V01~V41/F01~F12의 기능과 환경을 충분히 검증할 수 있어야 한다. UAC·잠금·SAS·재부팅 후 현장 없는 복귀는 H13의 별도 실증이 끝나야 수용한다. 구현만으로 F01~F12가 통과한 것으로 처리하지 않는다.

### 독립 프로젝트 구조

    desktop-test-driver/
      README.md                 # 독립 설치·CLI·운영·제거
      DesktopTestDriver.slnx    # 제품 솔루션과 분리
      global.json               # 도구 SDK 고정
      src/Server/              # 조작 PC: 캠페인·머신 조정·CLI/API
      src/Client/              # 대상 PC: 실행부·콘솔 세션 관측·입력
      src/Client.Broker/       # 대상 PC: 인증 IPC·콘솔 기동
      src/Contracts/           # 버전 있는 명령·증거 DTO
      src/Client.Watchdog/     # 대상 PC: Server와 분리된 deadline·정리
      cli/python/              # Server 호출용 개발자·CI CLI
      adapters/remoteassist/   # 제품 전용 UI·로그·판정
      scenarios/examples/      # 범용 재사용 예제
      inventory/               # 비밀 없는 스키마·예제
      tests/                   # 계약·고장 주입·모의 UI
      docs/                    # 프로토콜·보안·복귀

Server와 Client는 각각 독립 실행·배포 단위다. Broker와 Watchdog은 Client 패키지에 포함하지만 Client 콘솔 실행부와 별도 프로세스로 동작한다. 도구 핵심은 RemoteAssist 네임스페이스·제품 DTO/IPC·제품 빌드 결과에 링크하지 않는다. 어댑터만 제품 UI와 로그 형식을 안다. 기존 SSH·Job·deadline 코드의 아이디어는 리뷰해 독립 디렉터리 안에 구현하거나 안정된 공개 인터페이스로 사용한다. 기존 저장소의 `scripts/`를 상대 경로로 호출하는 숨은 의존성을 두지 않는다. .NET 10 Windows Server/Client와 Python CLI를 기본 후보로 하며 정확한 버전·패키징은 D01/D02에서 확정한다.

## 2. 구성과 실행 경로

아래 A/B 경로는 Server 1대와 Client 1대의 예시다. 실제로는 Server가 inventory에서 viewer, host, relay, observer, recovery **제품 시험 역할**을 조합한다. 이 역할은 시험 도구의 Server/Client 배치와 별개다. 한 머신이 여러 제품 시험 역할을 맡을 수 있지만 실제 두 PC가 필요한 F 항목은 단일 PC로 대체하지 않는다. 병렬 캠페인은 서로 다른 예약 머신만 사용한다.

```text
개발자·CI / A의 DesktopTestDriver.Server
  ├─ 캠페인·여러 Client 예약·CLI·증거·Viewer 측 조작
  └─ SSH(호스트 키 고정) ── B의 DesktopTestDriver.Client 명령 중계
                              ├─ 인증된 로컬 IPC ── B 콘솔 세션 실행부
                              │                       └─ 화면 수집 / SendInput / 실제 제품 UI
                              └─ 독립 watchdog ── 시간 제한 / 입력 해제 / 소유 자원 정리

A의 실제 Chrome Viewer ── RemoteAssist HTTPS/WebRTC ── B의 제품 Service/Tray/Helper
```

- Client: 조작될 시험 PC에 배치한다. C#/.NET 10 Windows 일반 권한 콘솔 실행부가 화면·입력을 맡고 별도 Broker/Watchdog이 IPC·복귀를 맡는다. **Server가 SSH로 Client를 설치·기동·제거한다.** Session 0 SSH 프로세스에서 직접 화면 입력을 시도하지 않는다. 현재 콘솔의 계정·Session ID·desktop을 확인해 Interactive 작업으로 시작한다.
- Server: 조작하는 PC에 설치한다. 기존 Python/Paramiko의 자격증명 조회·호스트 키 고정 코드는 검토 후 교체 가능한 SSH transport로 옮긴다. 개발자·CI용 CLI는 구조화된 JSON 결과와 비공개 이미지 경로를 반환한다. Server는 Viewer가 실행되는 PC의 브라우저 조작도 맡는다. 임의 셸 문자열을 입력 명령에 섞지 않는다.
- 명령 전달: Server가 Client PC의 기존 SSH 관리 경로로 접속한다. Client 전용 공개 수신 포트는 열지 않는다. 사용자 전용 ACL의 로컬 IPC 및 회차별 비밀·만료 lease로 Client 명령 중계부와 콘솔 실행부를 결합한다. 비밀은 머신별 secret provider에서 읽고 특정 계정·환경변수 이름을 도구 계약에 고정하지 않는다. 암호를 명령 인자·문서·로그에 넣지 않는다.
- 화면: 제품 캡처·인코더·WebRTC와 다른 캡처 경로를 우선 검토한다. 일반 desktop의 GDI 기반 스냅샷을 후보로 삼고 검은 화면·DPI·다중 모니터를 H03에서 실측한다. 불가능한 표면은 지원 불가로 반환한다. 실시간 영상 스트리밍은 첫 버전에 필요하지 않다.
- UI Automation은 창·컨트롤·경계·토글 상태 관측에 사용한다. 사람처럼 입력하는 시나리오의 클릭은 실제 좌표와 SendInput으로 수행하며 InvokePattern이나 내부 API 성공을 클릭 성공으로 대체하지 않는다.
- 독립 수용은 도구에 포함한 `fixtures/MarkerWindow`의 FixtureClick/FixtureText와 `fixture-input.json`을 사용한다. RemoteAssist.InputTarget과 제품 입력 모듈은 사용하지 않는다. 제품 전용 관측 창은 향후 선택형 H 연동에서만 사용할 수 있다.

### 2.1 SSH를 통한 시험 PC Client 설치·제거

D21에서 Server가 아래 1~5단계를 한 번 수행하고 같은 두 PC 배치를 D22/D23의 후속 회차에 재사용한다. 6단계의 실제 제거는 D23 종료 또는 앞 단계의 실패 정리에서 수행한다. 설치는 독립 fixture 시험 동작 전에 끝내며, 시험 대상 PC에 사람이 가서 Client 설치 버튼을 누르는 절차를 요구하지 않는다. 시험 PC의 SSH 서비스·인증·콘솔 계정·관리 권한은 사전에 inventory와 위임 manifest로 확인한다.

1. 1PC 단계에서 Client/Broker/Watchdog을 하나의 버전 고정 패키지로 만들고 파일별 hash·도구 버전·허용 경로·제거 목록을 manifest에 기록한다. Server는 패키지와 설치/제거 명령을 리뷰하고 원격 PC의 기존 상태를 읽기 전용으로 수집한다.
2. Server가 고정된 SSH 호스트 키와 승인된 자격증명으로 대상 PC에 연결해 SFTP로 회차 전용 staging 경로에 패키지를 전송한다. 경로는 inventory가 허용한 절대 경로로 한정하고 ACL·사용 가능 공간·기존 설치와의 충돌을 확인한다. 전송 뒤 대상 PC에서 hash를 다시 계산해 manifest와 대조한다.
3. 원격 설치 명령은 인자화된 고정 동작만 실행하고 실행 파일·설정·회차 ID를 소유 ledger에 등록한다. SSH가 연 Session 0에서 입력을 주입하지 않는다. Broker가 확인한 사용자 콘솔 Session ID/계정/desktop에 별도의 Interactive 실행부를 기동한다.
4. Client와 독립 watchdog의 PID·생성 시각·실행 경로·hash, IPC 인증, heartbeat, 화면 관측 가능 여부와 실제 일반 desktop을 입력 없이 확인한다. Server는 설치 응답만으로 준비 완료를 선언하지 않는다.
5. 설치·기동·검증의 각 호출에 timeout을 둔다. 실패하면 D22/D23을 시작하지 않고 설치한 소유 자원만 역순 제거한 뒤 이전 PC 상태와 SSH 접속 가능 여부를 확인한다. 재전송/재설치는 원인·준비 변경을 리뷰한 새 배치에서만 한다.
6. 캠페인 종료 시 Server가 SSH로 Client 중단·입력 해제·작업/프로세스/파일 제거를 요청한다. SSH가 끊겨도 Client watchdog이 입력을 해제하고, 독립 복귀 경로가 잔여 상태를 확인한다. 사용자 데이터와 기존 SSH 서비스는 제거하지 않는다.

F01의 깨끗한 설치 조건은 **RemoteAssist 제품이 없는 상태**로 판정한다. 별도 시험 도구 Client의 파일·작업·권한 변경은 baseline에 기록해 제품 설치 결과와 구분하고, 캠페인 종료 때 제거한다.

### 2.2 다중 머신 예약과 무인 회차

1. inventory에 머신 ID, 물리/VM, 소유자, OS, GPU, 모니터, 가능한 역할, 관리 주소, SSH 호스트 키 지문, 콘솔 접근 방식, 독립 복귀 수단을 기록한다. 비밀 값은 기록하지 않는다.
2. Server가 역할별 머신을 예약한다. 예약에는 run ID, 후보·설정 hash, 시작/만료 시각, 소유 자원 범위를 포함한다. 같은 머신의 동시 화면 점유를 막고 미정리 예약은 복귀 확인 전 재사용하지 않는다.
3. 각 Client가 사용자·Session ID·desktop·foreground·실제 창·화면 배치·TLS·포트·설치 상태를 **입력 없이** 확인한다. Viewer 브라우저와 Host 제품의 준비 신호를 별도로 확인한다. 준비 실패는 회차 시작이 아니다.
4. 전체 준비 완료 뒤 Server가 단일 캠페인 ID와 종료 시각을 발급한다. Client별 독립 watchdog은 자체 단조 시계와 lease로 deadline을 지킨다. 머신 간 wall clock 차이는 로그 상관에만 사용한다.
5. barrier는 단계마다 필요한 머신/증거를 기다린다. 한 머신의 실패·사용자 긴급 중단·deadline은 전체 회차를 중단한다. Server는 모든 Client에 stop을 보내고, 통신 실패 시에도 각 Client watchdog이 입력을 해제한다.
6. 각 Client는 소유 프로세스·작업·임시 서비스·키/버튼과 기존 상태를 ledger에 기록한다. 정리는 소유 항목만 역순으로 수행한다. Server 재시작 뒤에도 이전 run의 미정리 상태를 먼저 처리한다.
7. 증거에는 machine ID·역할·run ID·후보/도구/설정 hash·단조/UTC 시각·출처를 담는다. 한 머신의 정리/복귀 증거가 없으면 전체 PASS를 금지한다.

장시간 부하와 화면 점유 시험은 별도 큐와 시간/복귀 정책을 사용한다. 새로운 제품 기능은 범용 Client에 넣지 않고 RemoteAssist 어댑터에 추가한다. 다른 애플리케이션을 시험할 때는 해당 어댑터만 교체한다.

### 2.3 회차 위임과 접근 제한

장비 소유자가 캠페인 전에 대상 머신·계정·기간·시나리오·허용 창·요청자 신원·제품 권한 상한·중단/복귀 경로를 승인한 **회차 한정 위임 manifest**를 작성한다. Server와 Client는 동일 hash를 검증한다. 만료·대상 불일치·권한 초과·세션 변경 시 중단한다. 위임 없이 관측·입력을 시작하지 않는다.

RemoteAssist 실제 동의 UI는 그대로 둔다. Client가 위임 범위의 명시적 시나리오에서 UI를 클릭할 수 있지만 제품에 자동 승인 옵션·동의 우회 IPC·상시 무인 접속을 넣지 않는다. 이를 시험 위임으로 기록하고 사람의 동의 문구 이해 검증과 구분한다. 일반 업무 PC, 운영 장비, 위임 밖 요청은 자동 클릭하지 않는다. 대상자의 로컬 중단은 Server보다 우선한다.

SSH 호스트 키를 고정하고 비밀은 OS 자격증명 저장소나 승인된 기존 경로에서 회차에만 읽는다. 명령 인자·문서·로그에 넣지 않는다. Client Broker와 콘솔 실행부의 IPC는 사용자 전용 ACL, 회차 비밀, 짧은 lease로 인증한다. Client의 새 공개 수신 포트는 기본으로 열지 않는다. 각 명령·위임·중단을 감사 로그에 연결한다.

## 3. 명령 계약

| 명령 | 입력과 사후 조건 |
|---|---|
| `inspect` / `snapshot` | Session/desktop/foreground/PID·생성 시각/모니터·DPI/프레임 ID·관측 시각/스크린샷 반환 |
| `focus` | 허용된 창의 정확한 신원 검증 후 포커스 시도. 실제 foreground 확인 실패 시 중단 |
| `move` / `click` / `wheel` / `drag` | 최신 프레임과 대상 창에 묶은 좌표, 버튼, 유한 거리·단계·시간. 동작 후 화면·입력 관측 |
| `key-down` / `key-up` / `chord` | 허용 키와 최대 hold 시간. Client가 주입한 키만 소유 ledger에 기록 |
| `text` | 길이 제한된 Unicode 입력. 물리 키/IME 경로와 구분해 결과에 표기. 제품 클립보드를 몰래 사용하지 않음 |
| `wait` | 특정 창/상태/프레임 변화와 유한 timeout. 고정 sleep으로 준비를 가정하지 않음 |
| `release-all` / `stop` | 멱등 입력 해제·새 입력 차단·회차 종료. 새 명령보다 우선 처리 |

요청 필수 필드는 `protocolVersion`, `runId`, `machineId`, `role`, `commandId`, `sequence`, `targetSession`, `expectedDesktop`, `deadline`, `expectedWindow`, `frameId`, `manifestHash`다. 일반 조회에는 대상 창을 생략할 수 있으나 입력에는 필수다. `expectedWindow`에는 HWND만 두지 않고 PID·생성 시각·이미지 식별을 포함한다. 응답은 접수·실행·사후 검증을 구분하고 `observedAt`, `duration`, `errorCode`, `evidence`를 포함한다. 오류 코드는 만료, 위임 불일치, 예약 충돌, 세션/desktop 변경, stale 프레임, foreground 불일치, 주입 실패, 관측 불가, 정리 미완료를 구별한다.

같은 commandId의 재전송은 이전 결과만 반환한다. 응답 유실로 실행 여부가 불명확하면 클릭을 재전송하지 않고 관측 후 실패 처리한다. 큐 길이·본문·이미지 크기·명령 실행 시간을 제한한다. 좌표는 virtual desktop의 실제 픽셀과 모니터 배치를 기준으로 환산하고 음수 원점·DPI·회전·화면 변경을 검증한다. 오래된 프레임, 포커스 변경, 대상 창 교체, 세션/desktop 변경이면 주입하지 않는다.

## 4. 선택형 제품 연동 — 독립 도구 수용 이후

이 절의 RemoteAssist 동의·영상·제품 입력·V/F는 후속 H 단계의 참고 설계다. 기본 도구의 기능 검사와 D21~D23 수용 조건에는 포함하지 않는다.

1. B의 Client가 무해한 입력 관측 창을 준비하고 실제 Tray에서 지원 시작을 조작한다.
2. A의 실제 Viewer가 코드/PIN으로 연결한다. B의 Client는 실제 동의 창과 요청자·요청 권한을 확인한다.
3. 계획에 있는 권한만 실제 체크박스·승인 버튼으로 선택한다. 거절·닫기·만료 시나리오도 각각 독립 회차로 수행한다. 화면 또는 요청 신원이 예상과 다르면 중단한다.
4. 제품 원격 입력 검증 구간에서는 **B Client의 입력을 봉인하고 관측만 허용**한다. A Viewer에 넣은 마우스·키보드가 제품 경로를 거쳐 B InputTarget에 도달했는지 검증한다. B에 직접 넣은 입력은 제품 입력 PASS 증거로 사용하지 않는다.
5. 대상자 종료 검증 구간에서만 B Client 입력을 다시 허용하고 실제 종료 버튼을 클릭한다. Viewer·Host 양쪽의 송신/입력 차단 및 상태를 관측한다.
6. 기능 성공과 별개로 모든 입력 해제, 제품·시험 자원 정리, 일반 desktop 복귀를 확인한다. 각 이벤트에 `driver-local` 또는 `product-remote` 출처를 붙여 혼동을 막는다.

승인 전 영상 0, 권한별 허용/거부, 실제 디코딩 프레임 증가, InputTarget의 실제 이벤트, 종료 후 새 입력 차단을 각각 판정한다. 명령 접수·SendInput 반환·프로세스 시작만으로 PASS하지 않는다. 임의 바탕화면 캡처 대신 시험 창의 회차별 시각 표식을 대조해 잘못된 화면도 탐지한다.

### 4.1 RemoteAssist 어댑터의 기능 범위

범용 도구는 화면·입력·예약·복귀를 제공하고, RemoteAssist 어댑터는 실제 제품을 블랙박스로 조작한다. 제품 내부 상태 확인에는 읽기 전용 로그·프로세스·네트워크·입력 관측을 사용한다. 기능별 실제 사후 증거가 없는 조작은 PASS가 아니다.

| 검증 묶음 | 어댑터·fixture가 제공할 기능 | PASS에 필요한 별도 증거 |
|---|---|---|
| 시작·연결·동의 | 설치/서비스/Tray 시작, 코드/PIN 전달, 실제 Viewer 접속, 요청자·권한 확인, 승인/거절/만료/종료 UI | 승인 전 영상 0, 권한별 결과, 대상자 종료 뒤 차단, 감사 로그 |
| 영상·입력 | Host 시험 표식 창, Viewer 영상, 해상도/모니터 전환, 마우스·키·휠·문자·reset | 실제 디코딩 프레임·화면 표식 일치, Host 입력 관측 창의 실제 이벤트와 키 해제 |
| 권한·보안 | 보기/제어/관리자 별도 승인, 권한 초과, 만료·재연결·경합, TLS/WSS·DTLS-SRTP·잘못된 인증서/Origin | 금지 동작 차단, 권한 축소·종료, 실제 암호화/인증 실패와 감사 기록 |
| 보조 기능 | 파일 offer/accept/청크/체크섬, 채팅, 시스템 상태, Runbook과 허용 진단 | 수신 파일 hash, 메시지·상태 일치, 실행 권한 경계 |
| 네트워크·품질 | LAN P2P, NAT/TURN fallback, 연결 단절/복원, 대역폭·FPS·비트레이트·다중 GPU | ICE 선택 경로·TURN allocation·실측 지표·영상 연속성 |
| 설치·운영 | 깨끗한 VM 설치, 서비스 감독, 서명·업데이트·rollback·제거, 다른 계정·다중 세션, 반복·부하 | 설치본/서명/hash, 버전 복구, 소유 자원 정리, 성능/누수 실측 |
| 특권·복귀 | UAC/SAS/잠금/재부팅과 별도 관리자 동의 | 독립 콘솔 복귀·지원 가능한 중단 경로와 실제 권한 게이트 |

최종 F01~F12는 **모두 실제 두 PC 이상에서 판정**한다. 아래 표는 선택형 제품 연동의 기능 목록이며 실제 PASS는 독립 D21~D23 수용 후 별도 H12~H14 캠페인에서 판정한다. 이 표의 환경이나 제품 결함은 독립 도구 수용의 선행 조건이 아니다. F02의 추가 GPU/VM, F05의 독립 콘솔, F07의 NAT/TURN, F11의 장시간 부하, F12의 rollback VM은 두 기본 PC 외 환경이 더 필요할 수 있다. 정확한 PASS 수치·조건은 [통합 구현계획](원격제어-구현계획.md)의 F 표를 따른다.

| F | 필요한 도구 기능·fixture | 필수 환경/복귀 조건 |
|---|---|---|
| F01 | 두 PC 설치 상태·코드/PIN·동의 UI·TLS·첫 영상·정상 제거의 순서/증거 수집 | 깨끗한 두 PC와 서명 후보 |
| F02 | 역할 교환, GPU/인코더·WGC/DXGI 관측, 회전·resize·모니터 제거와 프레임 대조 | 다른 GPU와 HW 없는 VM, 화면 배치 복원 |
| F03 | Viewer 입력 주입과 Host InputTarget 관측의 출처 분리, DPI/letterbox/IME·키 해제 검사 | 정확한 창·좌표·foreground |
| F04 | 거절·부분 승인·철회·PIN 제한·코드 재사용·지원자 교체·긴급 종료 자동화 | 각 회차의 독립 위임·요청자 신원 |
| F05 | 별도 관리자 승인, UAC/SAS/잠금 상태 관측과 권한 해제 | R2 외부 콘솔·중단·복귀 실증 |
| F06 | 양방향 파일 hash·경로·전송 취소, 채팅·클립보드 순환 관측 | 샌드박스 파일/경로와 수집 후 제거 |
| F07 | LAN/NAT/TURN 토폴로지 선택, UDP 차단·TCP/TLS fallback, ICE pair 양끝 수집 | 격리 네트워크와 방화벽 원복 |
| F08 | 진단 전용 동의, Runbook 미리보기/승인/거절/취소, 실행 hash·감사 대조 | 등록된 무해한 시험 Runbook |
| F09 | 제품 각 프로세스/서버 실패·blackhole·로그오프/사용자 전환·RDP 조건 관측 | 머신별 독립 watchdog, 세션 복귀 |
| F10 | 인증·IPC·프로토콜 음성 fixture와 로그/웹/파일 입력 검사 | 격리 환경, 비밀 마스킹, 무단 입력 차단 |
| F11 | 장시간/동시 룸·손실/저대역·파일+영상 부하와 자원/지연/FPS 수집 | 비점유 장시간 캠페인·별도 정리 예산 |
| F12 | 이전 설치본·서명 업데이트·변조 실패·rollback·재시작·제거 상태 대조 | 스냅샷 가능한 깨끗한 VM과 버전 복구 |

각 묶음을 하나의 15분 회차에 몰아넣지 않는다. V01~V41/F01~F12의 각 하위 조건에 시나리오 ID와 증거를 매핑한다. 제품 없는 도구 자체 검증, 실제 두 PC 기능 검증, 다른 GPU/VM/NAT/서명/부하 검증은 별도 환경과 시간 기준을 가진다. 미실행·환경 미확보는 NOT RUN/BLOCKED로 남긴다. H10의 기존 Helper 조기 종료 결함은 제품 코드에서 별도로 수정하며 도구 구현만으로 해결됐다고 보지 않는다.

## 5. 실패·권한·복귀

inventory에 머신별 복귀 등급을 기록하고 시나리오가 요구하는 등급을 준비 검사에서 강제한다.

| 등급 | 확보할 수단 | 허용 시험 |
|---|---|---|
| R0 | SSH와 일반 콘솔 Client만 확인 | 입력 없는 준비 검사 |
| R1 | 독립 watchdog, 실제 입력 해제·서비스/창 정리·일반 desktop 복귀 실증 | 일반 desktop의 짧은 무인 시험 |
| R2 | 제품/OS 세션과 독립된 VM 콘솔·스냅샷 또는 화면 관측·USB HID가 가능한 하드웨어 KVM과 전원 복구 실증 | UAC·잠금·SAS·재부팅·OS hang 위험 시험 |

R1에서는 Server·Client 콘솔 실행부·SSH 중 하나가 죽어도 Client watchdog이 새 입력을 차단하고 소유 키·버튼을 해제해야 한다. R2에서는 실제 잠금·보안 desktop·OS hang 뒤 독립 경로로 상태 관측과 복귀가 가능한지 실증한다. 장비나 권한이 없으면 해당 시나리오는 BLOCKED다.

- 제품과 별도인 Client watchdog 및 명령 lease를 둔다. SSH/Server/Client 콘솔 실행부 소실, heartbeat 만료, deadline, 화면 잠금 시 새 입력을 차단하고 소유 키·버튼을 해제한다. 단순 Job 종료만으로 키 해제가 된다고 가정하지 않는다. 독립 복원 프로세스와 고장 주입 실증이 필요하다.
- 사용자 실제 입력과 경쟁하면 시험을 중단한다. 정상 업무 PC를 자동 잠그거나 사용자의 키 입력을 차단하지 않는다. 허용된 시험 창 이외 조작은 정책으로 거부한다. 시험 상태 배지·로컬 긴급 중단·A의 stop을 제공한다.
- 일반 권한 Client 콘솔 실행부가 승격 창/보안 desktop을 조작한다고 가정하지 않는다. H13에서 별도 최소 권한 broker의 가능성과 기존 Windows 경계를 검토한다. UAC 비활성화·비밀번호 저장·자동 로그온을 해결책으로 삼지 않는다.
- **현장 없는 전체 복귀**에는 제품·OS 사용자 세션과 독립된 경로가 필요하다. 물리 시험 머신은 화면 캡처와 USB HID가 가능한 하드웨어 KVM, VM은 하이퍼바이저 콘솔/스냅샷을 후보로 비교한다. 머신별 확보 여부·비용·설치 작업은 미확정이며 이 계획만으로 확보됐다고 기록하지 않는다. 구매/장치 설치는 별도 결정이다.
- H13 전에는 잠금·UAC·SAS·재부팅 시험을 시작하지 않는다. 일반 desktop이 사라지면 FAIL/BLOCKED로 멈춘다. OS hang/전원 단절까지 소프트웨어 watchdog으로 15분 복귀를 보장하지 않는다.
- 소유 PID·생성 시각·경로·hash·회차 ID로만 제거한다. 재시작 시 미정리 회차를 먼저 복구하며 타 프로세스·기존 인증서·사용자 파일을 지우지 않는다. 정상 종료 뒤 자동 재접속/자동 승인하지 않는다.

## 6. 관측 개선과 기존 실패 처리

r5는 사용자의 승인 응답 후 영상 timeout이 발생했고 Helper 조기 종료 기록만 남았다. 새 조작 도구가 이 제품 결함을 해결한다고 간주하지 않는다. H10에서 Helper 종료 코드·예외 유형·실행 단계, Tray 동의 결과·요청 ID, 시그널링/ICE/인코더/캡처 상태를 비밀 제외한 동일 회차 로그로 연결한다. 승인 이벤트 누락·Helper 종료·협상 실패를 발견하면 즉시 종료하고 전체 영상 timeout까지 기다리지 않는다.

한 회차 증거는 후보/설정/도구 hash, A/B 세션·desktop, 단조 시각, 명령과 결과, 동작 전후 이미지, 제품 로그, 정리 ledger, 복귀 결과를 포함한다. PIN·비밀번호·클립보드·일반 업무 화면은 공개 보고서에 넣지 않는다. 이미지·원본 로그는 사용자 전용 ACL 아래 보관하며 보존 기간/삭제를 명시한다. 스크린샷은 기본 시험 창으로 제한하고 전체 화면은 필요한 관측에만 비공개로 수집한다.

## 7. 구현 순서와 태스크별 완료 조건

아래 H 항목은 RemoteAssist 연동 마일스톤과의 대응표다. 세부 구현 진행은 이 문서 상단의 D 목록에서, 제품 통합/검증 진행은 통합 구현계획의 H/V/F에서 관리한다.

| ID | 선행 | 산출물 및 완료 조건 |
|---|---|---|
| H01 | 없음 | D01/D12 계약·경계·위임·권한·오류 코드 리뷰. 계획 작성 자체는 구현 완료가 아님 |
| H02 | H01 | D02/D04 독립 Server/Client 프로젝트와 Client 사용자 콘솔 실행부·IPC. 제품 없이 실행·종료 가능 |
| H03 | H02 | D05 스냅샷·창/UIA·DPI/모니터 좌표. 실제 시험 창/프레임 확인 |
| H04 | H03 | D06 키·마우스·드래그·휠·문자와 입력 ledger. 실제 이벤트·해제 확인 |
| H05 | H02 | D03/D08/D09 다중 Client SSH 중계·CLI·예약·barrier·명령 멱등성을 모의 노드로 구현·검사. 실제 SSH 설치는 D21 |
| H06 | H04,H05 | D07 프레임/창/foreground 전제 조건과 조건부 wait, stale/포커스 탈취/부분 주입 차단 |
| H07 | H04,H05 | D10/D11 독립 Client watchdog·긴급 stop·복귀 ledger의 로컬 고장 주입. 실제 두 PC 장애 복귀는 D22 |
| H08 | H06,H07 | D13 제품 없는 한 PC 모의 환경 자체 검증. 독립 실제 머신 실증은 D22에서 수행하며 제품 H12와 구분 |
| H09 | H08 | D14/D15 실제 Tray 동의/거절/종료 어댑터. 요청 신원/토글/결과 검증 |
| H10 | H08 | 기존 Helper 실패 진단·관측 보완 및 제품 원인 수정·영향 회귀. 원인 미확정이면 제품 재시험 차단 |
| H11 | H09,H10 | D16/D18 Viewer 입력/대상자 입력 분리와 RemoteAssist V/F별 출처·판정·시나리오 고정 |
| H12 | H11 및 독립 D23 수용 | 별도 제품 캠페인에서 제한된 RemoteAssist 일반 화면 E2E. D23과 분리하며 첫 실패 종료·복귀 보고 |
| H13 | H07 | 독립 도구 수용 뒤 별도 R2 캠페인에서 제품 UAC/잠금/SAS/재부팅 복귀 실증. 미확보는 해당 제품 시험만 BLOCKED |
| H14 | H12,H13 | 선택형 제품 캠페인의 V/F 결과를 연결해 제품 수용 검토. 독립 D23 결과와 구분하며 미실행 장시간/NAT/서명은 잔여로 유지 |

모든 도구 신규 파일은 `desktop-test-driver/` 아래에 둔다. H01~H08은 주로 D01~D13의 1PC 작업, H09~H11은 D14~D20의 1PC 준비 작업이다. 독립 두 PC 실행은 D21~D23으로 끝낸다. 이후 준비된 제품을 시험할 때만 선택형 H12~H14 캠페인을 별도로 시작한다. 제품 코드 변경은 진단/실제 결함 수정으로 제한하며 시험 자동 승인 스위치는 추가하지 않는다.

## 8. 독립 도구 검증 단계와 시간 계획

2026-10-19 사용자 지시로 독립 수용과 RemoteAssist 제품 시험을 분리했다. **D21~D23은 RemoteAssist가 없는 환경에서 완결한다.** 제품 바이너리·서비스·Go 시그널링·웹 Viewer·제품 InputTarget·제품 인증서/TURN·H10 결함 해결을 준비 조건으로 삼지 않는다. D15~D18에서 작성한 제품 어댑터·fixture·V/F 대응표는 보존하되 선택형 후속 자료로 둔다. 기본 솔루션·패키지·verify는 제품 어댑터를 참조하지 않는다.

| 단계 | 태스크 | 실제 수용 증거 |
|---|---|---|
| 독립 구현·모의 검사 | D01~D20 | 제품 없는 계약·위임·프레임/입력·예약·watchdog·복귀 검사와 자체 시나리오다. D15~D18의 제품 전용 코드는 선택 확장으로 격리한다. |
| SSH 설치·준비 | D21 | 고정된 Client/Broker/Watchdog과 자체 fixture만 설치하고 hash·콘솔 Session·인증 IPC·독립 watchdog·일반 desktop·중단/제거 경로를 입력 없이 확인한다. |
| 도구 기능·장애 | D22 | Server가 자체 fixture를 관측·조작하고 클릭/drag/wheel/키/Unicode 결과, 중복/만료/오래된 프레임·포커스 변경 차단, 통신/실행부/watchdog 실패 때 해제·복귀를 확인한다. |
| 수집·제거·독립 수용 | D23 | 비공개 증거와 공개 요약을 검증하고 소유 Client/fixture/작업/파일만 제거한 뒤 기존 SSH·사용자 파일·정상 desktop과 빈 ledger를 확인한다. |
| 선택형 제품 연동 | H09~H14 및 제품 V/F | D23 완료와 별도 사용자 착수 지시 뒤 제품별 준비·후보·동의·복귀 계획으로 수행한다. 독립 수용의 일부가 아니다. |

### 8.1 두 PC 독립 캠페인 순서

1. 입력을 주입하기 전에 회차 위임, core 패키지/설정 hash, 실제 콘솔 계정/Session/desktop, SSH host key, 증거 ACL/공간, 소유 자원과 복구 수단을 확인한다. 미확보 항목은 BLOCKED다. 준비 실패를 실제 입력 시험으로 진단하지 않는다.
2. D21에서 Server가 SSH로 Client를 한 번 배치한다. 자체 `MarkerWindow.exe`의 실행 파일 hash·회차별 제목·PID/생성 시각·실제 창/컨트롤 좌표를 확인한다. 제품 실행 파일이나 브라우저는 기동하지 않는다.
3. D22에서 관측→최신 frame→허용된 입력→fixture-input.json의 실제 이벤트/결과/키 해제를 순서대로 확인한다. 권한 밖 창·만료/중복·DPI/foreground/Session 변경과 장애 시나리오는 미리 정한 별도 짧은 회차로 나눈다. 첫 실패에서 캠페인을 끝내고 정리한다.
4. D23에서 SSH 수집과 bundle/hash·비밀 제외를 확인한다. configHash/packageHash/소유 신원이 일치하는 자원만 제거하고 정상 desktop·기존 SSH·사용자 데이터·빈 입력 ledger·잔여 소유 프로세스/작업 없음을 확인한다. 정리 실패 시 예약을 유지한다.
5. 결과는 도구 기능 PASS, 정리/복귀 PASS, 미실행·차단 항목으로 나눠 보고한다. 실제 실행이 없으면 D21~D23을 체크하지 않는다. RemoteAssist의 정상 동작이나 제품 V/F 결과는 독립 수용 판정에 넣지 않는다.

### 8.2 시간·중단·복귀

각 화면 점유 회차는 첫 fixture 시험 동작부터 결과 전달까지 목표 600초, 절대 상한 900초다. 기능 입력은 기본 300초 이내다. 검증 전 예산은 기동/준비 신호 90초, 정상 fixture 관측/입력 90초, 고정 장애/해제 120초, 수집/제거/복귀 180초, 보고 120초로 합계 600초다. D19 리허설과 실제 관측으로 예산을 확정한 뒤 Arm한다. 남은 300초는 복구 여유이며 시나리오 추가에 사용하지 않는다.

전역 단조 deadline·독립 watchdog·유한 정리 호출·소유 자원과 일반 desktop 복귀를 사전 검증한다. 실패·준비 신호 누락·시간 초과 시 즉시 새 입력을 봉인하고 소유 입력/자원을 정리한다. 같은 회차에서 재시도·코드 수정·재빌드·환경 탐색을 하지 않는다. 원인과 준비가 바뀐 새 계획에서 영향받은 항목만 재실행한다.

UAC·SAS·잠금·재부팅·OS hang·제품 TURN/NAT·서명 업데이트·2시간 부하는 독립 일반 desktop 수용의 필수 조건이 아니다. 필요하면 R2 또는 전용 자원을 갖춘 별도 확장 캠페인으로 수행하며 해당 미실행을 기본 도구 실패로 취급하지 않는다.
