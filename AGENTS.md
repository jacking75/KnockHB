# RemoteAssist — 프로젝트 가이드

사내 PC 관리 운영 부서용 Windows 원격 지원 도구. 개발자 문서는 [docs/README.md](docs/README.md)에서 시작한다.
구조·설계는 [아키텍처](docs/architecture.md)와 [기술 사양](docs/technical-specification.md),
와이어 포맷은 [protocol.md](docs/protocol.md), 실연결 진행 상태와 남은 과제는 [두 PC 실연결 시험](docs/live-e2e-test.md) §9를 기준으로 한다.
설계와 어긋나는 구현을 하기 전에 해당 기준 문서를 먼저 갱신한다.

## 제품 결정 (2026-10-23 사용자 확정, 모든 기능 결정에 우선)

- 지원자 PC와 대상 PC는 서로 신뢰한다. 지원을 요청한 사람이 프로그램을 받아 직접 실행한다. 신뢰 검증·본인 확인·승인 절차·방어적 제한 코드는 만들지 않는다. 사용 편의성을 우선한다.
- 대상자가 프로그램을 **관리자 권한으로 한 번** 실행하면 끝이다. 그 뒤 대상자는 아무것도 하지 않고, 지원자가 화면·입력·관리자(UAC)·파일·클립보드·진단·Runbook·원격 명령·모니터 전환을 모두 쓴다.
- 접속은 PIN 없는 **고정 9자리 지원 ID**로 한다. 끊기면 자동으로 다시 접속한다. 지원 허가는 서비스 재시작·업데이트 뒤에도 유지되고 재부팅·로그오프·사용자 변경·대상자의 즉시 종료에서 사라진다.
- 두 PC는 지정 IP로 항상 **직접 P2P** 연결된다. STUN/TURN·릴레이·연결 가능성 분기는 없다. 실제 연결 오류는 결함으로 조사한다.
- 대상자에게 보이는 접속 표시와 Tray의 즉시 종료 버튼은 유지한다.
- 터널링 서버는 TLS(HTTPS/WSS)를 유지한다. 사설망은 자체 서명 인증서 + Host 핀 고정(`RA_MODE=lan`)을 쓴다.

## 문서 저장 위치

- `docs/`: 아키텍처·기술 사양·프로토콜·개발자 가이드 등 지속적으로 유지하는 기준 문서.
- `docs_working/`: 개발 과정에서 임시로 만드는 리뷰·조사·분석·작업 메모·태스크 문서. 새 임시 개발 문서는 여기에 작성한다.
- `README.md` 파일에서는 `docs_working/`이 임시 계획·조사·리뷰·작업 기록을 보관하는 디렉터리라는 용도만 설명한다. 그 안의 개별 문서 이름·내용·링크는 언급하지 않는다.
- 진행 상태와 남은 과제는 `docs/live-e2e-test.md` §9 "현재 상태와 남은 과제"에서 관리한다. `working_log.md`는 만들지 않는다.
- 개발자용 시험 도구 DesktopTestDriver는 별도 저장소(`F:/github/DesktopTestDriver`)다. 기본 빌드·검증은 RemoteAssist 없이 자체 fixture로 완료한다.

## 실연결(E2E) 시험 — 필수 지침

### 고정 시험 환경

- A: `192.168.0.61` — 지원자 PC(웹 Viewer) / 터널링 서버.
- B: `192.168.0.74` — 대상 Host PC. SSH 사용자: `jacking75NoteBook`.
- SSH 인증은 기존 환경 설정과 등록된 호스트 키를 사용한다. 비밀번호·토큰·개인 키·환경 변수 전체를 출력하거나 문서에 복사하지 않는다.
- DesktopTestDriver 저장소: `F:/github/DesktopTestDriver`. 실행본 기준: `bin/live_Release`.

### 원칙

- 실연결 시험은 사용자의 **명시적 재개 지시** 없이 실행하지 않는다. 준비가 끝나면 시험 여부를 먼저 묻는다.
- **충분한 실행 계획·준비·사전 리뷰 없이 시작하지 않는다.** 목적, 확인할 결과, 단계와 PASS/FAIL 판정, 필요한 증거, 중단·정리·복귀 절차를 먼저 문서화한다. 단일 PC의 제품 화면·입력·서비스·UAC·잠금 실증에도 같다.
- 후보 commit/바이너리 hash·설정·인증서/포트·실제 대상 창/좌표·로그 수집·소유 PID/경로를 미리 확인한다. 준비 실패를 실제 시험 반복으로 진단하지 않는다.
- 화면 점유 묶음은 짧게(기본 5분 이내) 설계하고 빌드·설치·원인 분석을 그 구간에 넣지 않는다. 실행 직전에 목적·예상 점유 시간·중단 조건을 사용자에게 알린다.
- 실패·신호 누락·시간 초과 시 즉시 중단하고 입력 해제·세션·브라우저·임시 서비스 정리를 수행한다. 화면을 점유한 채 코드 수정·환경 탐색·시나리오 추가를 하지 않는다.
- **원인과 준비가 바뀌지 않은 동일 시험의 반복을 금지한다.** 수집한 증거로 원인을 좁히고 수정·예상 결과·판정 절차를 리뷰한 뒤 영향받은 시나리오만 재실행한다.
- 실제 실행 시간, 확인한 결과/새 결함, 정리 결과를 기록한다. 준비 완료·프로세스 기동을 기능 PASS로 세지 않는다.

## 아키텍처

제품 경로는 **SYSTEM Service + 일반 권한 WPF Tray/Helper + (관리자 모드) SYSTEM AdminHelper + Go 터널링 서버 + 웹 Viewer**다. HostRun은 화면 전용 개발 진단 도구다.

- **Host.Service** (Session 0 · SYSTEM): 지원 허가 읽기/폐기, 고정 지원 ID, Tray/Helper/AdminHelper/RunbookWorker 감독, 자동 재대기, 감사(실패해도 계속).
- **Host.Tray** (사용자 세션 · 일반 권한): 지원 ID·접속 표시·즉시 종료·채팅·파일 보내기·클립보드 처리. 승인 창은 없다.
- **Host.Helper** (사용자 세션 · 일반 권한): 캡처·인코딩·WebRTC·일반 입력.
- **Host.AdminHelper** (사용자 세션 · SYSTEM): 연결되면 자동으로 쓰는 관리자 모드. 현재 입력 데스크톱(기본·UAC 보안 데스크톱·잠금 화면)을 캡처·입력하고 ACL 공유 mapping으로 프레임을 Helper에 넘긴다.
- **Viewer** (지원자): 웹 SPA(브라우저 내장 WebRTC). 설치 불필요.
- **터널링 서버** (Go, `server/tunnel`): 지원자 PC(A)가 여는 접속 지점이다. 대상 PC(B)는 설치 때 받은 A의 주소로 여기에 직접 접속한다. Viewer 페이지 제공·지원 ID 방·첫 SDP/ICE 교환·Viewer 교체를 맡고, 화면·입력 미디어는 거치지 않는다(두 PC 직접 P2P). 예전 이름은 "시그널링"이었다(2026-10-23 사용자 결정으로 변경).

SYSTEM 서비스도 Session 0에서 사용자 세션으로 자동 이동하지 않는다. 캡처·입력은 대상 사용자 세션의 헬퍼가 처리한다. `SetThreadDesktop`만으로 Windows 세션 경계를 넘을 수 없다.

## 기술 스택 (영역별로 더 나은 언어를 쓴다)

Host는 C#, 터널링 서버는 Go. .NET을 쓰는 곳은 모두 **.NET 10(LTS)** 로 통일한다.

| 영역 | 선택 |
|---|---|
| 런타임 | .NET 10 (LTS), Windows 10/11 x64 |
| WebRTC (Host) | `SIPSorcery` 10.0.x |
| 화면 캡처 | **WGC 기본 / DXGI 폴백**. 보안 데스크톱은 AdminHelper의 DXGI |
| 인코딩 | **`SIPSorceryMedia.FFmpeg`로 H.264**. `SIPSorceryMedia.Encoders`는 deprecated, 사용 금지 |
| FFmpeg | **8.1 ABI · LGPL 빌드만** 동봉(`third_party/ffmpeg/`). 시스템 PATH에 의존하지 않고 로드 경로를 명시 |
| H.264 인코더 | `h264_nvenc` → `h264_qsv` → `h264_amf` → `h264_mf`, 없으면 `libopenh264`. **`libx264`는 GPL이므로 금지** |
| 입력 주입 | Win32 `SendInput` (P/Invoke), SAS는 `SendSAS` |
| Host UI | WPF |
| 터널링 서버 | **Go** + `net/http` + `coder/websocket` (TCP 25001) |
| Viewer | 정적 HTML/CSS/바닐라 JS (Node.js 빌드 단계 없음) |

## 프로젝트 구조

```
RemoteAssist.slnx
├─ src/
│  ├─ RemoteAssist.Host.Service/      # Windows 서비스: 허가·지원 ID·헬퍼 감독·감사
│  ├─ RemoteAssist.Host.Tray/         # 트레이 UI(WPF): 지원 ID·상태·즉시 종료
│  ├─ RemoteAssist.Host.Helper/       # 사용자 세션 헬퍼: 캡처·인코딩·WebRTC
│  ├─ RemoteAssist.Host.AdminHelper/  # SYSTEM 관리자 헬퍼: 보안 데스크톱 캡처·입력
│  ├─ RemoteAssist.Host.RunbookWorker/# Runbook·원격 명령 실행
│  ├─ RemoteAssist.Host.Core/         # 공통 로직(캡처/인코딩/입력/세션)
│  ├─ RemoteAssist.Protocol/          # 데이터채널 메시지 C# DTO
│  └─ RemoteAssist.Update/            # 관리자 실행 진입점·설치/업데이트
├─ server/tunnel/                  # 터널링 서버 (Go 모듈, 별도 빌드)
├─ web/viewer/                        # 지원자 웹 뷰어(정적 SPA)
├─ deploy/                            # 설치 스크립트, tunnel 컨테이너, runbooks
└─ docs/                              # 기준 문서 (protocol.md가 와이어 포맷 단일 기준)
```

`RemoteAssist.slnx`에는 C# 프로젝트와 진단 도구만 넣는다. Go 모듈은 `server/tunnel`에서 `go build ./...`로 따로 빌드한다.

## 데이터 채널 프로토콜

와이어 포맷의 기준은 `docs/protocol.md` 하나이고 C#·Go·JS가 각각 그것을 구현한다. 스키마 변경은 **항상 문서 → 양쪽 코드** 순서로 한다.

| 채널 | 신뢰성/순서 | 용도 |
|---|---|---|
| `control` | reliable · ordered | 세션 제어, 모니터 전환, 품질, 관리자 모드 |
| `input` | reliable · ordered | key/button/wheel/text/reset 전이 |
| `pointer` | reliable · ordered · move 최신 값 합치기 | 마우스 move |
| `file` | reliable · ordered | 파일 전송(16KiB 청크, 백프레셔) |
| `chat` | reliable · ordered | 채팅, 클립보드 |
| `telemetry` | reliable · ordered | 시스템 진단 |
| `runbook` | reliable · ordered | 등록 작업, 원격 PowerShell 명령 |

- 지원자는 항상 전 권한을 받는다. 권한·채널 목록은 고정이다.
- **좌표는 항상 0~1 정규화**해서 보내고 Host가 실제 픽셀로 환산한다.

## 주요 리스크

| 리스크 | 대응 |
|---|---|
| Session 0에서 화면 캡처 불가 | 서비스가 사용자 세션 Helper/AdminHelper를 감독한다 |
| 보안 데스크톱(UAC) 캡처의 GPU별 차이 | AdminHelper DXGI 경로. 실연결 검증에서 GPU가 다른 두 PC로 실측 |
| FFmpeg GPL 오염 | LGPL 빌드만 동봉 + 인코더 명시 지정 |
| LGPL 준수 | 동적 링크·별도 DLL 유지, 고지·소스 제공 |
| H.264 HW 인코더 부재·드라이버 편차 | HW 우선 + `libopenh264` SW 폴백. `--encoder-check`로 장치별 검증 |
| C#·Go 간 스키마 드리프트 | 기준은 `docs/protocol.md` 하나. 공유 fixture(`tests/fixtures`) 왕복 테스트 |
| 백신·SmartScreen 오탐 | 코드 서명, 벤더 화이트리스트 |
| 원격 제어 도구 오남용 | 사내 배포 통제, 접속 표시·즉시 종료 유지, 감사 기록 |

## 컨벤션

- 개발용 포트는 TCP `25001~25199`, UDP `27001~27199` 범위에서만 할당한다. 터널링 서버는 TCP `25001`, Host WebRTC는 UDP `27002~27099`.
- 성능 수치(지연·FPS·비트레이트)는 추정하지 말고 실측값을 기록한다.
- 원격 제어 도구는 오남용 소지가 크다. 배포·운영 시 조직 보안 정책과 관련 법규(개인정보·정보통신망) 검토를 병행한다.
