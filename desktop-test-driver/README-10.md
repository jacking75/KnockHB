# DesktopTestDriver 0.2.0

- [x] D01~D20의 독립 도구·시나리오·어댑터를 구현한다.
- [x] 제품과 실제 화면 입력을 사용하지 않는 로컬 계약·고장 주입·SSH/SFTP 모의 검사를 제공한다.
- [x] D08의 SSH 배치·전송·제어를 loopback 모의 대상으로 검증한다.

Windows 시험 전용 도구다. Server는 조작 PC, Client는 조작될 PC에 두고 Broker가 SSH 관리 경로와 일반 사용자 콘솔 사이를 연결한다. Watchdog은 Client와 별도 프로세스다. RemoteAssist의 솔루션·설정·IPC·바이너리에 의존하지 않는다. [계약과 위협 모델](docs/contracts-9.md), [구현·검증 증거](docs/verification-11.md), [전체 D 태스크](e2e-desktop-driver-plan.md) 순서로 읽는다.

현재는 개발용 0.2.0, 프로토콜 v2다. 필수 소유자 서명과 시나리오 hash가 추가되어 0.1.0/v1과 혼용하지 않는다. D01~D20은 구현과 로컬·모의 검증 범위다. D21은 SSH 설치, D22는 자체 fixture의 관측·입력·장애 검증, D23은 수집·제거·복귀와 독립 수용이다. **D21~D23은 RemoteAssist 없이 완료한다.** 제품 연동은 독립 수용 뒤 별도 H 단계다. [회차 위임·시나리오·어댑터 운영](docs/scenarios-12.md)에서 새 기능과 제품 없는 재사용 예제를 설명한다.

## 설치와 독립 빌드

Windows 10 2004 이상/Windows 11 x64와 .NET 10 Desktop Runtime이 필요하다. 빌드는 SDK 10.0.400을 사용한다. Server SSH 전송에는 Python 3.11 이상과 `transport/requirements.txt`의 Paramiko가 필요하다. 시스템 Python이나 제품의 의존성 디렉터리를 변경하지 않고 도구 전용 환경을 만든다.

```powershell
Set-Location C:/github_dev/KnockHB/desktop-test-driver
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r transport/requirements.txt
.\build.ps1 -Package
.\verify.ps1
```

`build.ps1 -Package`는 `artifacts/package-<고유ID>/`에 Client, Broker, Watchdog, Server와 SSH 전송부를 게시한다. 각 파일의 SHA-256·크기와 도구/프로토콜 버전을 `package-manifest.json`에 기록한다. 제품 빌드나 CI는 실행하지 않는다. 프레임워크 종속 패키지이므로 시험 PC에 Desktop Runtime이 먼저 있어야 한다. `verify.ps1`은 실제 데스크톱·서비스·제품을 조작하지 않으며, 실패한 검사에서 종료한다.

## inventory와 준비

기본 `DesktopTestDriver.slnx`, `build.ps1 -Package`, `verify.ps1`은 RemoteAssist 어댑터·제품별 fixture/V/F 계획을 빌드하거나 참조하지 않는다. 패키지 manifest의 profile은 core다. `verify.ps1`은 어댑터와 제품 소스가 없는 임시 독립 복사본에서도 빌드·계약 검사를 실행한다. 자체 `MarkerWindow`의 FixtureClick/FixtureText·fixture-input.json으로 실제 입력 결과를 관측한다.

독립 수용 이후 제품 연동을 준비할 때만 `build.ps1 -Package -RemoteAssist` 또는 `verify.ps1 -RemoteAssist`를 명시한다. 이 선택형 프로필은 별도 `DesktopTestDriver.RemoteAssist.slnx`와 모의 제품 어댑터 검사를 사용한다. 어댑터는 `adapters/remoteassist/`에 따로 게시하며 기본 Server에는 링크하지 않는다. 이 옵션도 실제 제품을 실행하지 않는다.

`inventory/example.json`을 비공개 작업 디렉터리에 복사해 수정한다. 예시 주소·키·계정은 실제 대상이 아니다. 머신 ID, 소유자, OS/GPU/모니터, 가능한 역할, 실제 콘솔 계정, 허용 설치 루트와 고정 SSH 호스트 키를 기록한다. 호스트 키는 별도 신뢰 경로로 확인하고 `hostKeyType`과 Base64 공개키 원문을 함께 고정한다. 자동 수집한 새 키를 무조건 신뢰하지 않는다.

`secretKind`는 `password-env` 또는 `keyfile-env`다. `secretReference`에는 승인된 자격증명을 공급할 환경변수 이름만 넣는다. 값은 문서·명령 인자·JSON 요청에 넣지 않는다. Client의 새 네트워크 포트는 열지 않는다. .NET API의 `Inventory.Validate`와 Server의 다음 명령은 입력 없이 구성을 검사한다.

```powershell
& ./artifacts/package-<ID>/Server/Server.exe validate ./private/inventory.json
```

로그인한 콘솔 사용자는 SSH 계정과 같아야 한다. SSH의 Session 0에서 화면을 조작하지 않고, Broker가 그 계정의 Limited/Interactive 예약 작업으로 Client를 기동한다. 관리 권한이 필요하거나 예약 작업 생성이 거부되는 환경은 준비 실패로 처리한다. UAC 해제나 자동 로그온을 설정하지 않는다.

회차 설정은 [run 템플릿](inventory/run.template.json)을 사용한다. 실제 콘솔 Session ID와 회차/머신/역할, UTC 만료 시각을 기입한다. 처음에는 R0/`allowInput:false`로 관측만 준비한다. 입력은 R1 실증 증거와 실행 파일 절대 경로·SHA-256·정확한 창 제목 allowlist를 준비한 뒤 사용한다. R2 실증이 없으면 UAC·SAS·잠금·재부팅은 BLOCKED다. 템플릿의 만료 시각은 사용 전에 현재 회차에 맞게 바꾼다.

## CLI와 API

Server는 JSON 결과를 표준 출력에, 비밀을 제외한 오류 코드만 표준 오류에 쓴다. PowerShell에서 한글 JSON을 파이프로 보낼 때는 `$OutputEncoding=[Console]::OutputEncoding=[Text.UTF8Encoding]::new()`로 UTF-8을 명시한다. Python 래퍼는 같은 Server 인자를 전달하고 stdin의 JSON 한 줄을 전달한다.

```text
Server.exe validate INVENTORY
Server.exe reserve INVENTORY RESERVATIONS RUN ASSIGNMENTS CANDIDATE_HASH SETTINGS_HASH EXPIRY
Server.exe ssh INVENTORY MACHINE ROLE RUN PYTHON_EXE SSH_ADAPTER OPERATION
Server.exe owner-init NEW_PRIVATE_DIRECTORY KEY_ID
Server.exe authorize UNSIGNED_CONFIG PRIVATE_KEY NEW_SIGNED_CONFIG
Server.exe plan-check PLAN
Server.exe catalog-check CATALOG
Server.exe simulate COUNTER_PLAN SIGNED_CONFIG NEW_BUNDLE
Server.exe bundle-check BUNDLE
Server.exe purge-evidence STOPPED_RUN_STATE
```

`ASSIGNMENTS`는 `[{"machineId":"lab-host","role":"host"}]` 형식이다. 예약은 머신 단위로 충돌을 막고 후보·설정 hash·기간·역할을 보존한다. 만료됐어도 정리 확인 없이 예약을 재사용하지 않는다. SDK의 `Reservations.Complete(machine,run,cleanupVerified)`는 전체 정리 결과가 확인된 뒤 호출한다.

SSH operation은 `install/status/send/stop/collect/remove`만 허용한다. 각 호출은 신규 연결에서 호스트 키를 먼저 검증한 뒤 인증한다. 임의 셸 명령은 받지 않는다. `install`의 stdin은 `{"packagePath":"절대 패키지 경로","configPath":"절대 회차 설정 경로"}`다. 고유한 `InstallRoot/runId`만 생성하며 기존 회차를 덮어쓰지 않는다. 전송 뒤 원격에서 파일 hash를 다시 검사하고 Client를 기동한다. 반환의 `prepared:true`는 준비 요청 결과이며 기능 PASS나 준비 완료가 아니다.

`status`는 Client/Watchdog의 PID·생성 시각·실행 경로·hash와 manifest hash를 반환한다. 이어 `send`로 `inspect`를 보내 실제 세션·desktop·창·모니터 상태를 확인한다. `send`의 stdin은 계약 문서의 Command JSON이다. 실제 명령을 보낼 때는 별도 heartbeat를 지속해야 하며 기본 lease는 3초다. heartbeat는 독립 순번 스트림이고 동일 commandId의 재전송으로 lease를 갱신하지 않는다. 프로세스별 연속 SSH 연결이 느린 환경은 지원 준비 실패로 처리하고 lease를 무제한으로 늘리지 않는다.

inspect 응답에는 관측 정보와 allowlist 창별 메타데이터 frame이 있다. 이 frame으로 정확한 창에 focus하고 snapshot을 얻는다. 입력은 그 frame ID와 같은 expectedWindow를 명시한다. 창 클라이언트 영역의 픽셀 좌표를 사용하며 음수 모니터 원점은 Client에서 더한다. 실제 대상 창의 관측 없이 좌표를 추정하지 않는다. snapshot은 창 영역의 PNG와 UIA 읽기 결과를 반환한다. 검은 화면·접근 불가·UIA timeout은 관측 실패다.

`CommandEngine`·`IClientNode`·`Campaign`·`ScenarioRunner`는 C# API다. Campaign은 여러 Client 실행, 단계별 barrier, 전체 단조 deadline과 최초 실패 시 모든 노드의 stop을 제공한다. API 사용자는 머신 예약과 위임을 먼저 확인하고, 정상 종료 때도 `await campaign.Stop()`의 모든 노드 정리 결과를 확인해야 한다. 범용 엔진은 명시 등록한 `IScenarioAdapter`만 호출한다. `RemoteAssist.Adapter.dll`은 제품 코드 참조 없이 실제 UI·CDP와 읽기 전용 관측을 연결한다. `simulate` CLI는 제품 없는 CounterAdapter만 등록한다. 입력 응답의 `executed:true`는 OS 주입 접수이고 `verified:false`다. 실제 시험 앱이 관측한 결과로 기능 PASS를 별도 판정한다.

## 중단·수집·제거

`stop`은 새 입력을 먼저 차단한다. SSH가 끊겨도 콘솔 Watchdog이 단조 deadline·Server lease·Client heartbeat·키 hold 상한에 따라 소유 입력을 해제한다. Watchdog이 죽으면 Client도 입력을 차단하고 해제한다. Client/Broker/Watchdog이 모두 죽거나 OS가 멈춘 경우에는 R2 외부 콘솔 복귀가 필요하다. 시험 Client에 직접 물리 입력이 들어오면 경합으로 중단하므로 전용 시험 머신에서만 사용한다.

`collect`의 stdin은 `{"outputDirectory":"새 비공개 빈 디렉터리"}`다. 수집 파일에는 창 이미지와 민감한 관측 정보가 있을 수 있으므로 현재 사용자 전용 ACL을 미리 설정한다. `secret` 파일은 전송하지 않는다. `remove` 전에 수집을 끝내고 `{"configHash":"회차 파일 SHA256","packageHash":"패키지 manifest SHA256"}`를 전달한다. 이 두 hash는 배치 receipt와 일치해야 한다. 정상 desktop 복귀와 빈 ledger, Client/Watchdog 종료를 확인한 뒤 정확히 소유한 작업·회차 디렉터리만 제거한다. 기존 SSH 서비스·사용자 파일은 변경하지 않는다.

배치 준비가 실패하면 같은 요청의 소유 표식을 검증해 가능한 범위에서 rollback한다. 통신 유실 등으로 정리가 확인되지 않으면 완료 여부 불명으로 응답한다. 예약을 유지하고 독립 콘솔로 잔여 상태를 확인한다. 동일 배치를 자동 재전송하거나 임의 경로를 강제 삭제하지 않는다. 모든 화면 점유 실검증은 별도 사전 계획과 10분 목표/15분 절대 상한을 적용한다.

외부 복귀는 `IRecoveryAdapter`를 구현하거나 `ExternalConsoleAdapter`로 hash가 고정된 VM/KVM 실행 파일을 연결한다. 실행 파일은 JSON probe/recover를 받고 RecoveryReceipt 한 줄을 반환해야 한다. 호출은 최대 5초로 제한하고, 실증 증거 파일/hash 및 정상 desktop·입력 해제·정리·독립성에 관한 receipt를 `RecoveryGate.Require`로 검증한다. 모의 receipt의 `simulated:true`는 실제 R1/R2 준비 증거로 인정하지 않는다.
