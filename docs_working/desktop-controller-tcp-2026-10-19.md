# DesktopTestDriver 직접 TCP 연결 검증

사용자 요구: 개발용 일반 TCP, TLS/HTTPS/연결 인증 없음. SSH는 배포/기동에 사용하고 이후 Controller가 Client를 직접 제어한다.

## 실행 전 계획

- 범위 C03: 192.168.0.74에 core Client 배포, 실제 일반 사용자 Session 1에서 실행, 192.168.0.61:25071 상시 Controller로 접속, TCP inspect/heartbeat/stop/cleanup 확인. 입력·앱 실행은 이번 회차에서 제외한다. C04의 실제 메모장/버튼 조작 PASS로 처리하지 않는다.
- 후보: package-2a329f0eef50441c8859c8dc37d3c73f. 독립 C# 32개, SSH/SFTP 모의 11개, CLI 9개 통과. 독립 복사본의 기대 검사 개수를 28→32로 수정한 뒤 독립 복사본 32개도 통과했다.
- 사전 상태: 대상 Windows 11, 콘솔 Session 1 Active, .NET Desktop Runtime 10.0.11, 시험 예약 작업 없음. 절전으로 SSH 사전 점검이 한 번 실패했고 사용자 확인으로 절전 해제 후 접속을 확인했다. 실제 시험 실패/재시도는 아니다.
- 로컬 Controller는 새 전용 디렉터리 private/controller-tcp에서 상시 실행한다. 방화벽 규칙은 지정 Server.exe, TCP 25071, RemoteAddress 192.168.0.74만 허용한다. 관리자 권한 부재로 첫 설정이 거부된 뒤 Windows UAC 승인으로 규칙을 만들었다.
- 준비 완료 조건: 정확한 패키지 hash, 실제 listener PID/경로, 방화벽 필터, 대상에서 TCP 포트 접근, SSH 상태를 확인한다. run은 unsigned R0, allowInput=false, images=false, windows=[], 만료 10분, 독립 watchdog 최대 120초다.
- 실행 도구: tests/run_connection_smoke.py. 설치는 1회만 수행하고 prepared를 PASS로 세지 않는다. TCP ready 목록의 실제 PID/경로/hash와 inspect 결과의 세션/desktop/비상승/모니터를 확인한다. heartbeat는 상시 Controller가 별도 순번으로 자동 송신한다.
- 1회 전체 상한 120초, 설치 호출 85초, 이후 TCP 관측 8초, 정리 시간을 남긴다. 정상 예상 30초 이내, 화면 입력/포커스 점유 0초. 준비 신호 누락·첫 명령 실패 시 추가 시험을 하지 않고 stop/수집/소유 자원 정리한다. Client는 3초 연결 lease와 별도 watchdog으로 독립 중단한다.
- 정상 종료는 TCP stop 응답과 TCP cleanup receipt로 확인한다. SSH는 종료 후 증거 파일 수집과 배포 파일/예약 작업 제거에만 사용한다. Controller 프로세스와 지정 방화벽 규칙은 다음 개발 요청을 위해 유지한다. 실제 결과는 private/evidence의 회차 report 및 이 문서에 기록한다.

## 결과

1차 회차 `tcp-20261019133230`: 실제 Client PID 5972, Watchdog PID 15960, Session 1/Default의 TCP 연결을 확인했다. 시험 Python 래퍼가 JSON Lines의 끝 개행을 보내지 않아 inspect가 로컬 CLI에서 InvalidRequest로 거부됐다. 첫 실패에서 시험을 중단했다. TCP stop은 accepted/executed/verified=true, clean=true를 반환했고 TCP cleanup receipt와 정상 desktop/빈 ledger를 확인했다. 배포 파일/예약 작업 제거 및 SSH 복귀를 완료했다. 자동 report는 FAIL로 보존하고 추가 정리 증거를 `private/evidence/tcp-connect-20261019/final-cleanup.json`에 기록했다.

수정/재검증: 시험 runner와 범용 Python 래퍼의 stdin에 개행을 추가했다. 실제 Python 래퍼→게시된 Server CLI→상시 Controller→TCP 모의 Client 통합 검사를 추가했고 inspect/heartbeat/stop 통과를 확인했다. C# 바이너리 변경은 없다. 원인이 확정됐고 전달 코드가 수정됐으므로 동일한 입력 없는 접속/inspect 회차만 새 run으로 재검증한다. 시간/중단/정리 기준은 위와 같다. C04 실제 메모장 입력/버튼 클릭은 아직 검증하지 않았다.
