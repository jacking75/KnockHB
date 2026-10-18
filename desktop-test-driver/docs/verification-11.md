# D01~D11 구현과 로컬 검증 증거

- [x] 독립 솔루션과 win-x64 패키지를 빌드하고 파일별 hash를 대조한다.
- [x] C# 계약·고장 주입 검사 18개 묶음을 통과한다.
- [x] loopback SSH/SFTP 검사 11개를 통과한다.
- [x] 게시된 Server로 inventory CLI 검증을 통과한다.

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
