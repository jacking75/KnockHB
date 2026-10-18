# D01~D20 구현과 로컬 검증 증거

- [x] 독립 솔루션과 win-x64 패키지를 빌드하고 파일별 hash를 대조한다.
- [x] C# 계약·고장 주입 검사 18개 묶음을 통과한다.
- [x] loopback SSH/SFTP 검사 11개를 통과한다.
- [x] 게시된 Server로 inventory CLI 검증을 통과한다.
- [x] D12~D20의 권한·시나리오·어댑터·fixture·준비/수용 판정과 독립 예제를 검증한다.

## 2026-10-19 독립성 교정 검사

사용자 지시에 따라 D21~D23을 제품 없는 도구 자체 수용으로 수정했다. 기본 솔루션과 Server에서 RemoteAssist 어댑터 참조를 제거하고 제품별 시나리오/fixture·검사를 선택형 프로필로 격리했다. 자체 MarkerWindow에 클릭·drag·wheel·키/문자·OS held 상태의 `fixture-input.json` 관측을 추가했다. 실제 창/입력 실증은 수행하지 않았다.

기본 `verify.ps1` 증거는 `artifacts/verify-cb5edda46df641dea38856f7fc4b104b/`다. 시스템 검증 summary의 종료 시각은 2026-10-19 00:26:29 KST다. 독립 빌드 경고/오류 0개, 기본 계약 검사 22개, loopback SSH/SFTP 11개와 게시 CLI 검사 7개가 통과했다. core 패키지는 파일 66개이며 RemoteAssist 관련 경로/어셈블리는 0개다. Server.deps.json에서도 제품 어댑터 참조가 없음을 검사했다.

`test_independent.py`는 src·자체 fixture·기본 테스트만 임시 디렉터리에 복사한다. RemoteAssist 제품 소스·어댑터 디렉터리·제품 계획이 없는 상태에서 독립 빌드와 계약 검사 22개가 통과했다. 선택형 모의 어댑터 프로필의 회귀 검사는 `artifacts/verify-931078d7528a40e899e92ea360fc35df/`에 별도로 보관하며 실제 제품을 실행하지 않았다. 기본/선택 프로필은 테스트 NuGet lock 파일도 분리했다.

이번 검증은 로컬 코드·모의 검사다. D21 SSH 실PC 배치, D22 실제 자체 fixture 입력/장애, D23 제거/복귀·독립 수용은 모두 미실행이다. RemoteAssist 제품의 준비나 결함을 이 태스크들의 선행 조건으로 삼지 않는다. 제품 연동 H/V/F는 독립 수용 뒤 별도 계획으로 남긴다.

## D12~D20 최초 검사 기록

시스템 명령과 검증 summary에서 확인한 종료 시각은 **2026-10-18 22:20:46 KST**다. `verify.ps1`에서 독립 Release 빌드 경고/오류 0개, Client/Broker/Watchdog/Server/MarkerWindow 게시, C# 계약·고장 주입 25개, loopback SSH/SFTP 11개, 게시 CLI 서명·예제·변조 거부 7개, inventory 검사와 V/F 계획 드리프트 검사가 통과했다. CI는 생성·변경·실행·진단하지 않았다.

증거는 `artifacts/verify-1d6fc989bc704153bbfba4d9b2da83e3/`의 build/contracts/ssh/scenario-cli/plans/cli.txt와 summary.json이다. 시험 패키지는 `artifacts/package-23a82dbc2fb247199e601a1cb0222daa/`, 0.2.0/프로토콜 v2, 파일 86개다. 각 파일의 크기/hash를 manifest와 대조했다. 문서의 이 결과 기록은 시험 뒤 추가했으며 시험 바이너리는 아래 hash로 식별한다.

- Server/Core.dll: `10cd9253be728bbd8946e3c3ae717ebe0e11836bca771394580eead89b8e4bf5`다.
- Server/RemoteAssist.Adapter.dll: `1a20df29d41a4450281c7f68ae7d528338cbc5a32bd21dda01e71d2f43ccc199`다.

| 범위 | 검사한 결함과 한계 |
|---|---|
| D12 | 소유자 P-256 서명·미등록/변조/위임 범위·시나리오 hash와 비밀 본문 없는 감사, 감사 용량 실패 시 입력 차단/긴급 정리, 보존 만료 뒤 복구 ledger 보존을 검사했다. 실제 시험 머신 키 등록은 미실행이다. |
| D13/D14 | 모의 창·노드·실제 로컬 pipe·독립 가짜 프로세스 고장, 첫 실패 종료, source/후보/회차 결합과 잘못된 판정·NaN·미정리 PASS 거부를 검사했다. 새 프로세스 main module 조회 경합에는 최대 500ms의 신원 확인 상한을 추가했다. |
| D15 | 현재 제품 소스의 실제 UI ID/문구를 대조하고 모의 UI/CDP로 승인/거절/닫기/만료·요청/창 교체·권한 상한·문자/마우스 이벤트·letterbox·탭 이동 뒤 release를 검사했다. 실제 Tray/Chrome을 열거나 클릭하지 않았다. |
| D16 | 독립 InputTarget의 신원/freshness/event/OS held 상태·문자 hash, 영상 카운터/FPS/표식·파일 hash/경로 탈출·채팅 일치 횟수와 회차별 색 표식을 검사했다. 실제 디코더/GDI/SendInput·제품 파일 전송은 미실행이다. |
| D17 | fixture 5종의 정상·최초 실패·복원 실패·실환경 복귀 증거 부재를 모의 검증했다. 실제 설치/서비스/UAC/SAS/잠금/TURN/NAT/rollback/2시간 부하는 미실행이다. 장비별 실제 backend는 해당 환경의 D21~D23 준비가 필요하다. |
| D18/D19 | V41/F12 총 53항목과 공통 계획 12개를 대조하고 bundle 변조·후보 변경·단일 PC/모의의 수용 승격을 거부했다. 입력 없는 준비·정리/복귀 리허설은 모의 계약 검증이며 실제 ReadyForRealExecution을 선언하지 않았다. |
| D20 | 게시 파일만으로 owner-init→서명→독립 CounterAdapter 실행→bundle 재검증→서명 변조 거부를 검사했다. 시험용 개인키는 종료 때 삭제했다. 설치·제거 자체의 실PC 검증은 D21~D23이다. |

실제 화면 점유·제품 실행·원격 머신 설치·관리자 프롬프트·잠금은 수행하지 않았다. H10 제품 Helper 조기 종료 원인과 H/V/F·R30의 실제 수용 게이트는 별도다. 공통 회차 성공만으로 V/F의 모든 하위 조건을 통과 처리하지 않는다. [운영 문서](scenarios-12.md)와 [D 상태 원본](../../docs_working/e2e-desktop-driver-plan.md)을 따른다.

## D01~D11 초기 검사 기록

검사 종료 시각은 시스템 시계 기준 2026-10-18 19:48:23 KST다. `verify.ps1`은 제품과 실제 데스크톱 입력을 실행하지 않았다. 이 문서는 실행 결과와 검증 한계를 기록하며 상세 D 상태는 [구현계획](../../docs_working/e2e-desktop-driver-plan.md)의 목록을 원본으로 삼는다.

## 재현 명령과 로컬 산출물

```powershell
Set-Location C:/github_dev/KnockHB/desktop-test-driver
.\verify.ps1
```

최종 검사 증거는 `artifacts/verify-e2c9a411f4084337961e6bf2f537bf62/`의 `build.txt`, `contracts.txt`, `ssh.txt`, `cli.txt`, `summary.json`에 있다. `artifacts/`는 머신 한정 산출물로 git에서 제외한다. 코드를 받은 다른 머신에서는 같은 명령으로 새로운 고유 경로의 증거를 만든다.

게시 패키지는 `artifacts/package-0425a9924b23415bac35666250d3a356/`이며, 파일 46개의 hash manifest를 대조했다. `package-manifest.json`의 SHA-256은 `8b55d41adb92927b473cb2e572ff6356c1cafac4da200b17e0f73f605b1c6028`이다. 패키지는 SDK 10.0.400, 도구 0.1.0, 프로토콜 v1, .NET 10 Desktop Runtime에 종속되는 win-x64 게시물이다. 빌드와 네 프로젝트 게시 모두 경고·오류 0개다.

## 무엇을 확인했는가

| 범위 | 실제 검사와 잡아내는 결함 |
|---|---|
| D01/D02 | 별도 솔루션 빌드·게시 CLI 실행·파일별 hash 대조로 제품 솔루션의 숨은 빌드 의존성과 누락·변조된 패키지를 확인한다. |
| D03/D09 | 두 예약자의 실제 파일 생성 경합, 중복 머신/역할 검증, 만료·미정리 예약 유지, 소유 회차가 다른 해제 거부로 이중 예약과 잘못된 인수를 확인한다. |
| D04 | 실제 CurrentUserOnly named pipe의 인증 정상/거부, 회차 디렉터리 재사용 거부와 JSON 중복·미지 필드 거부를 확인한다. |
| D05/D07 | 가짜 desktop의 stale frame, DPI 변경, HWND/PID 교체, 창 이동, foreground 탈취, 세션·desktop 변경을 입력 전에 차단한다. 배경 창 메타데이터 frame과 focus, wait 조건도 확인한다. |
| D06 | 실제 SendInput 대신 가짜 backend로 이동·클릭·드래그·휠·키·chord·Unicode 및 음수 원점을 확인한다. down 실패 전 영속 ledger 기록, up 실패 항목 보존, 멱등 해제, 물리 키와 allowlist 거부를 확인한다. |
| D07 | 동일 명령 재전송이 입력을 두 번 만들지 않고 변경된 재전송·순번 역행을 거부하는지 확인한다. wait 중 stop과 heartbeat가 대기열에 갇히지 않고 heartbeat 재전송이 lease를 연장하지 않는지 확인한다. |
| D08 | TCP 25150~25198 중 사용 가능한 loopback 포트에서 실제 Paramiko SSH/SFTP 서버와 통신한다. 원격 OS 동작은 모의 구현이다. host key 변경을 인증 전에 거부하고, 실제 SFTP 전송·hash 검증·배치 실패 rollback·기존 회차 보존·응답 유실 시 무재시도를 확인한다. 수집은 secret을 제외하고 제거는 receipt를 먼저 검사하며 인접 파일을 보존한다. |
| D09 | 모의 두 노드의 barrier, 누락 증거 timeout, 한 노드 실패, 전체 deadline 및 전체 stop·부분 정리 실패를 확인한다. |
| D10 | 별도 로컬 프로세스를 가짜 Client/Watchdog으로 실행하고 각각 강제 종료한다. 살아남은 독립 경로가 디스크 ledger를 읽어 가짜 키 up을 기록하고 ledger를 비우는 것을 확인한다. 소유 프로세스 hash가 다르면 종료하지 않는다. |
| D11 | VM/KVM adapter 계약, 모의 복귀 receipt의 실환경 승격 거부, 고정 실행 파일 hash 검증, 실제 별도 모의 adapter 프로세스의 JSON 응답과 timeout 종료를 확인한다. |

C# 검사 18개 묶음, Python 검사 11개가 모두 통과했고 게시된 Server의 `validate inventory/example.json`은 `valid:true, machines:1`을 반환했다. 주입 명령은 접수 증거와 기능 판정을 분리하며 `verified:false`를 반환한다.

## 후속 실제 머신 E2E

D01~D11의 완료 범위는 구현과 로컬·모의 검증이다. D08은 SSH 배치·전송·제어 CLI/API 구현과 loopback SSH/SFTP 검사로 완료한다. 실제 시험 머신의 설치·콘솔 기동·관측·입력·복귀·제거는 뒤의 D21~D23 E2E에서 검증한다.

실제 GDI 화면·UIA·DPI 및 SendInput, Interactive 예약 작업의 콘솔 기동, 물리 입력 경합·키 복원, 다른 계정/RDP와 독립 복귀는 실제 머신 E2E의 검증 대상이다. UAC·SAS·잠금·재부팅·OS hang은 실제 R2 복귀 실증 뒤 수행한다. D12~D23과 RemoteAssist H/V/F는 각 단계의 완료 조건에 따라 별도로 판정한다.

코드 리뷰에서는 timeout·메시지 상한, 제품 참조 부재, 범위 밖 경로/재분석 지점 거부, 미정리 예약 유지, 부분 주입 ledger 보존, 소유 PID·생성 시각·이미지 hash 확인, 인증 비밀의 출력·수집 제외를 확인했다. 실제 Windows 동작과 보안 수용이 끝났다는 의미는 아니다.
