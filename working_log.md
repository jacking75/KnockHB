# 작업 로그

## 2026-10-23 23:40 KST — 후속 4건: 커밋, 터널링 서버 이름 변경, 하이브리드 GPU 수정과 동작 3 재시험, 잔여 결함

- 1차 실연결 수정을 커밋했다(c336b51).
- "시그널링"을 "터널링 서버"로 바꿨다(07f00e1). 문서, Go 모듈 `server/tunnel`, `Host.Core/Tunnel`, 설정 키 `TunnelUri`, 스크립트, 배포 파일이 대상이다. Go·.NET·Viewer·PowerShell 테스트와 루프백 검사가 PASS다.
- 하이브리드 GPU: `CaptureDump --probe-dxgi`로 B에서 실측했다. 프로세스가 NVIDIA에 배정되면 출력이 NVIDIA 아래에 보이고 `DuplicateOutput`이 `0x887A0004`로 실패한다. 절전 GPU 지정 시 Intel 아래로 옮겨 가 성공한다. `prepare-host`가 AdminHelper에 SYSTEM 하이브 `GpuPreference=1;`을 등록하고 제거한다(3f5a197).
- 잔여 결함 수정(36d57bf)
  - Viewer 문자 입력: reset ACK 전에 누른 전송을 보관했다가 재개 때 보낸다.
  - 제거 스크립트: 지원 허가를 지운다.
  - preflight·prepare: Go 확인 위치, 5.1 실행, TLS 폴더 생성, `-PasswordEnv`.
  - 시험 문서: 새로고침 기대 결과를 실제 동작에 맞췄다.
- 동작 3 재시험 PASS. 관리자 모드, 관리자 창 조작, UAC 보안 데스크톱 표시·클릭, Ctrl+Alt+Del 보안 화면이 B(Intel UHD + GTX 1660 Ti)에서 된다. 점유 약 4분이다. 준비 중 `e2e-prepare` 원격 따옴표 결함과 `install-host-dev` 서비스 경로 따옴표 누락(unquoted service path)을 찾아 고쳤다. 정리까지 확인했다. 자세한 내용은 `docs_working/동작3-재시험-4.md`.

## 2026-10-23 23:20 KST — 두 PC 수동 실연결 테스트(수동-실연결-테스트-3) 실행

- 환경: A `192.168.0.61`(Chrome Viewer + 시그널링 lan 모드, 자체 서명 TLS), B `192.168.0.74`(Intel UHD + GTX 1660 Ti Max-Q, 드라이버 27.20.100.8190 / 32.0.16.1062). 두 PC 모두 pwsh가 없어 Windows PowerShell 5.1로 준비했고, B 설치는 비밀번호 SSH(환경 변수)로 했다.
- 화면 점유: 동작 1~5와 재개 확인은 약 6분(23:07~23:13, 서버 로그 기준). 그 전 23:02~23:03에는 Helper 재시작 루프 때문에 즉시 중단·원인 분석을 했다.
- 결과: 동작 1 PASS(1280×720 · 디코딩 30fps · 약 4Mbps · 경로 `192.168.0.74:27002` 직접 P2P). 동작 2 PASS(영문·숫자·한글 문자 입력·창 드래그·실제 키보드 빠른 입력·Ctrl+A). 동작 4 PASS(B 서비스 재시작 뒤 같은 지원 ID로 약 10초 안에 자동 재접속). 동작 5 PASS(즉시 종료 → 화면 끊김·허가 삭제·재접속 거부, 재실행+UAC 뒤 재개). **동작 3 FAIL**: AdminHelper의 `IDXGIOutput1.DuplicateOutput`이 `0x887A0004`(DXGI_ERROR_UNSUPPORTED)로 실패해 관리자 모드를 쓸 수 없다(하이브리드 GPU 추정, 미분석).
- 시험 중 고친 결함: `install-host-dev.ps1` 공백 경로 인자 미인용, `prepare-host.ps1` 방화벽 포트 목록을 문자열 하나로 전달, IPC 허용 목록에 `device`·`supporter`·`admin-monitor` 누락(UAC 승인 직후 서비스 종료), 관리자 헬퍼 준비 전 실패가 지원 세션 전체를 끊음(이제 관리자 거부 후 일반 모드 유지), 진단 기록에 HRESULT·Helper 종료 사유 추가. Host.Core 38/38 PASS.
- 남은 결함·문서 불일치: 하이브리드 GPU DXGI 캡처, 문자 입력 칸에서 포커스 이동 직후 첫 전송이 안내 없이 무시됨, Viewer 새로고침은 자동 재접속이 아니라 ID만 채움(문서 7장 기대 결과와 다름), `install-host-dev.ps1 -Action Remove`가 `initial-support.json`을 남김(문서 9장과 다름), `e2e-preflight.ps1` Go 버전 오판, `e2e-prepare.ps1`이 pwsh를 요구하고 TLS 디렉터리를 미리 만들지 않음. B에 예전 시험의 Helper 인바운드 Block 방화벽 규칙 2개가 있어 지웠다.
- 정리: B 개발 설치 제거(서비스·설치 폴더·방화벽 규칙·후보·허가 파일), A 서버 종료, TLS 키 삭제. B에 메모장 창(저장 안 한 시험 탭)이 남아 있다.

## 2026-10-23 14:10 KST — A 머신용 E2E 준비 스크립트

- 이 머신은 A가 아니고 B에 닿지 않아(실제 테스트는 다른 A 머신에서) 준비물만 만들었다. `scripts/e2e-preflight.ps1`(읽기 전용 준비 점검: 도구·A LAN IP·B ping/SSH·포트)과 `scripts/e2e-prepare.ps1`(TLS 인증서 + Host 후보 빌드 + B에 SSH 설치 + 시그널링 기동)을 추가했다. 둘 다 ParseFile 문법 검사 통과, preflight는 이 PC에서 NOT READY로 올바르게 보고(B 미도달·A의 192.168.0.x 없음).
- `수동-실연결-테스트-3.md`에 "A 머신에서 실행"과 두 스크립트 빠른 준비 절을 추가했다.

## 2026-10-23 11:01 KST — 한 PC 루프백 통합 검사와 실연결 시험 계획

- 두 PC가 없는 환경에서 할 수 있는 범위를 수행했다. `scripts/loopback-check.ps1`로 실제 Go 시그널링 + 실제 ScreenShareSession + C# 가짜 Viewer(입력은 기록만, 화면 읽기만)를 붙여 연결·7개 채널·입력 경로·관리자 요청(승인 창 없음)·모니터 전환·원격 명령·채팅·Viewer 교체(busy/replaced)·재접속을 21/21 PASS로 확인했다(연결 607ms, data-ready 834ms, nvenc RTP 수신).
- 실연결 시험 계획서 `docs_working/실연결-시험-계획-2.md`(T13) 작성. 실제 SendInput·UAC 캡처·두 PC·브라우저 Viewer는 T14/T15로 남겼다.

## 2026-10-23 10:38 KST — 현황 문서 1~6단계 구현(상호 신뢰 규격)

- STUN/TURN/relay 제거(시그널링·Host·Viewer·배포), 지원 ID 방의 Viewer 교체(`busy`/`replaced`)와 시그널링 `lan` 모드(TLS 유지, operator/감사 선택) 추가.
- 동의 창·건별 승인·Recovery·CaseTrace를 없애고 권한을 전 권한 고정으로 바꿨다. 연결 시 자동 관리자 모드, 관리자 모드 중 모니터 전환, 데스크톱·화면 구성 변경 자동 전환, 원격 PowerShell 명령을 추가했다.
- 지원 허가를 로그온 시각에 묶은 지속형으로 바꿔 서비스 재시작·업데이트 뒤에도 유지되게 했다. 개발 모드에서는 보호 경로·manifest 검사를 생략한다. 구 E2E 하네스를 정리하고 docs·AGENTS.md를 새 규격으로 갱신했다.
- 검증: 빌드 경고·오류 0, Host.Core 38·Go 전체·Viewer 58·PowerShell 계약 테스트 통과. 인코더 실측(이 PC): nvenc 81.4fps, mf 104.3fps, openh264 62.7fps. h264_amf profile 결함을 고쳤다. 두 PC 실연결은 실행하지 않았다(구현계획-1 T13~T15).
