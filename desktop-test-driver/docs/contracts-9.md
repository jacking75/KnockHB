# DesktopTestDriver v1 계약과 권한 경계

- [x] D01 독립 빌드, 실행 경계, 지원 환경, 위협 모델과 명령 계약을 확정한다.

지원 환경은 Windows 10 2004 이상과 Windows 11 x64, .NET SDK 10.0.400이다. Server와 Client는 제품 솔루션이나 제품 코드에 의존하지 않는다. Python 3.11 이상 CLI와 Paramiko 4.x 전송부는 Server의 교체 가능한 SSH 어댑터다. Broker, Client, Watchdog은 서로 다른 프로세스이며 공개 수신 포트를 만들지 않는다. 릴리스 버전은 0.1.0, 와이어 버전은 1로 시작한다.

Client는 이미 로그인한 동일 계정의 실제 콘솔 세션에서 일반 권한으로만 실행한다. SSH Session 0의 Broker는 해당 계정의 InteractiveToken 예약 작업으로 Client를 기동한다. 다른 계정, RDP, 관리자 창, 보안 desktop, UAC, SAS, 잠금, 재부팅 직접 제어는 거부한다. R2 외부 콘솔 어댑터의 검증된 복귀 경로가 없는 위험 시나리오는 BLOCKED다. 모의 복귀 성공을 R2 실증으로 승격하지 않는다.

## 위임과 위협 모델

회차 설정은 runId, machineId, role, targetSession, expectedDesktop, UTC 만료, 최대 실행 시간, 실행 파일 경로와 SHA-256 및 정확한 창 제목 allowlist를 가진다. 입력 가능 키, 최대 hold와 프레임 유효기간을 제한한다. 설정 파일의 SHA-256은 명령의 manifestHash와 일치해야 한다. D12의 소유자 서명·상세 감사·보존 정책은 후속 범위이며 현재 설정은 신뢰하는 장비 소유자가 사전에 작성해 배치하는 전제다.

다른 로컬 계정·잘못된 회차·재전송·오래된 화면·창 핸들 재사용·포커스 탈취·물리 입력 경합·프로세스 중단·연결 유실·부분 입력 실패를 위협으로 다룬다. 관리자/SYSTEM이 시험 계정을 장악한 경우는 이 도구의 격리 경계 밖이다. 회차 디렉터리는 현재 계정 전용 ACL이며 IPC는 CurrentUserOnly named pipe와 디렉터리의 무작위 256비트 비밀로 인증한다. 비밀은 인자나 응답에 넣지 않는다. SHA-256만으로 소유자 인증이 성립한다고 간주하지 않는다.

## 명령과 결과

JSON Lines, UTF-8, 한 메시지 최대 1MiB다. 필수 필드는 protocolVersion/runId/machineId/role/commandId/sequence/targetSession/expectedDesktop/deadline/manifestHash다. 입력에는 expectedWindow와 frameId가 추가로 필요하다. 창 신원은 HWND/PID/프로세스 생성 UTC/이미지 경로/SHA-256/제목/사각형으로 묶는다. 프레임에는 콘솔 세션·desktop·foreground·모니터·DPI·생성 단조 시각이 연결된다. 좌표는 프레임의 창 클라이언트 영역 안 픽셀 좌표다. 모니터 음수 원점과 DPI는 별도로 보고한다.

명령은 inspect/snapshot/focus/move/click/wheel/drag/key-down/key-up/chord/text/wait/release-all/stop과 heartbeat다. UIA는 읽기만 하며 별도 프로세스에 유한 timeout을 적용한다. Unicode 입력은 클립보드를 사용하지 않는다. wait는 window/foreground/frame-change 조건을 유한 deadline 안에서 관측한다. 명령 ID 재전송은 동일 요청 hash일 때 이전 결과만 반환한다. 내용 변경은 DuplicateMismatch, 순번 역행은 SequenceRejected다. heartbeat는 wait와 독립된 순번·멱등 캐시를 사용하며 재전송으로 lease를 연장하지 않는다. stop은 안전한 반복 정리를 허용한다. 실행 결과는 accepted/executed/verified를 분리하며 주입 성공만으로 애플리케이션 기능 PASS를 선언하지 않는다.

입력 직전과 각 조각에서 만료·세션·desktop·창 신원·foreground·프레임·배치를 검사한다. 키/버튼 down 전에 복원 ledger를 디스크에 기록한다. 이미 눌린 물리 키는 소유하지 않는다. 부분 실패·물리 입력·세션 변경·stop은 새 입력을 봉인하고 소유한 입력만 해제한다. Client와 별도 Watchdog은 같은 ledger 잠금을 사용한다. Watchdog은 실행부 PID/생성 시각, heartbeat/Server lease, hold 상한, 단조 deadline을 감시한다. Client 죽음/Watchdog 죽음 모두 fail closed다.

## 예약·SSH·복귀

Server는 머신별 배타 예약 파일을 원자 생성한다. 만료만으로 미정리 예약을 인수하지 않는다. stop은 모든 노드에 전파하고 모든 정리 결과를 받아야 예약을 해제한다. barrier 누락·실패·deadline은 전체를 종료한다. 모의 transport와 실제 pinned SSH/SFTP를 같은 API로 연결한다. 고정된 배치 동작 이외 임의 shell 명령 API는 제공하지 않는다. 입력 명령의 응답 유실은 자동 재시도하지 않는다.

패키지는 파일별 SHA-256과 버전 manifest를 가진다. 설치는 고유 staging 안에만 가능하고 기존 경로를 덮어쓰지 않는다. 제거는 정확한 회차/경로/manifest의 일치가 필요하다. 프로세스는 PID·생성 시각·경로·hash가 모두 일치할 때만 정리한다. 현재 자원 모델은 소유 프로세스와 회차 디렉터리·Interactive 예약 작업이다. 임의 서비스나 사용자 파일의 삭제는 지원하지 않는다.

R0은 입력 없는 검사만, R1은 별도 watchdog과 정상 desktop 복귀 실증을 요구한다. R2는 VM 콘솔/스냅샷 또는 KVM 화면·HID·전원 복구 실증을 추가 요구한다. 실제 R1/R2 증거가 없는 inventory는 입력을 허용하지 않는다. 모의 시험은 주입 backend와 외부 복귀 adapter를 가짜 구현으로 교체하며 실제 OS 입력을 만들지 않는다.
