# RemoteAssist — 프로젝트 가이드

동의 기반 Windows 원격 지원(트러블슈팅) 도구. 개발자 문서는 [docs/README.md](docs/README.md)에서 시작한다.
구조·설계는 [아키텍처](docs/architecture.md)와 [기술 사양](docs/technical-specification.md),
와이어 포맷은 [protocol.md](docs/protocol.md), 단계별 태스크는 [구현계획](원격제어-구현계획.md)을 기준으로 한다.
설계와 어긋나는 구현을 하기 전에 해당 기준 문서를 먼저 갱신한다.

## 문서 저장 위치

- `docs/`: 아키텍처·기술 사양·프로토콜·개발자 가이드 등 지속적으로 유지하는 기준 문서.
- `docs_working/`: 개발 과정에서 임시로 만드는 리뷰·조사·분석·작업 메모·태스크 리스크 문서. 앞으로 새 임시 개발 문서는 여기에 작성한다.
- 루트의 `원격제어-구현계획.md`는 기존 위치를 유지한다.
- 구현 계획·진행 상태는 `원격제어-구현계획.md` 하나에서 관리한다. E01~E41은 구현·로컬 검증, F01~F12는 최종 E2E이며, 기존 T는 대응표와 과거 기록으로 보존한다. 임시 문서에 별도 진행표를 복제하지 않는다.

## 설계 원칙 (모든 기능 결정에 우선)

1. **대상자 통제권 우선** — 명시적 동의 없이는 어떤 접속·조작도 성립하지 않는다. 대상자는 언제든 즉시 세션을 끊을 수 있다.
2. **최소 권한** — 세션에서 허용된 동작만 수행한다. 초기 버전에 무인(unattended) 상시 접속은 넣지 않는다.
3. **전 구간 암호화** — 시그널링은 TLS(WSS), 미디어·데이터는 WebRTC DTLS-SRTP. TURN 경유 시에도 릴레이가 페이로드를 복호화할 수 없어야 한다.
4. **감사 로그** — 세션 시작·종료, 접속 주체, 관리자 모드 진입/해제, 파일 전송을 기록한다.

편의 기능이 위 원칙과 충돌하면 원칙을 따른다.

## 아키텍처

현재 실행 경로는 **HostRun 콘솔 + Go 시그널링 + 웹 Viewer의 화면 공유 PoC**다.
Service/Tray는 골격이며 입력·동의·TURN은 미구현이다. 아래는 목표 책임 경계다.
시그널링(SDP/ICE)과 미디어를 분리하고, 미디어는 P2P 우선·TURN 폴백을 목표로 한다.

- **Host Agent** (대상 PC) — 서비스·사용자 UI 분리 + 사용자 세션 실행 헬퍼 설계
  - `Host.Service`: Session 0 · SYSTEM 권한. 세션·권한·헬퍼 수명 감독
  - `Host.Tray`: 사용자 세션 · 일반 권한. 동의 대화상자, 세션 코드/PIN 표시, 접속 중 배지, 종료 버튼
  - 사용자 세션의 캡처·입력 헬퍼와 인증된 로컬 IPC로 통신한다. 인코딩·WebRTC 실행 위치와 IPC 세부는 T2.4에서 확정한다
- **Viewer** (지원자) — 웹 SPA(브라우저 내장 WebRTC). 설치 불필요가 핵심 이점
- **Signaling + Relay** (클라우드) — 세션/룸 관리, SDP·ICE 중계, coturn(STUN/TURN), 감사 로그. 미디어 원본은 저장하지 않는다

### 왜 SYSTEM 서비스인가
관리자 권한 조작에는 일반 입력과 별도 승인·실행 경계가 필요하다. SYSTEM 서비스도 Session 0에서
사용자 세션으로 자동 이동하지 않는다. 서비스는 감독을 맡고 캡처·입력은 대상 사용자 세션의 실행
경로에서 처리하도록 설계한다. `SetThreadDesktop`만으로 Windows 세션 경계를 넘을 수 없다.
일반 제어 동의가 관리자 UI 조작 권한으로 확대되지 않도록 한다. 상세는 아키텍처 문서와 리뷰 G03을 따른다.

## 기술 스택 (영역별로 더 나은 언어를 쓴다)

Host는 C#, 시그널링 서버는 Go. .NET을 쓰는 곳은 모두 **.NET 10(LTS)** 로 통일한다.
Host가 C#인 이유는 WinRT(Windows.Graphics.Capture)·DXGI COM·`SendInput`·`OpenInputDesktop`·Windows 서비스·WPF를 모두 표준으로 다룰 수 있기 때문이고,
시그널링이 Go인 이유는 하는 일이 "다수 WebSocket을 붙들고 릴레이"뿐이라 goroutine 동시성과 단일 정적 바이너리 배포가 그대로 이점이 되기 때문이다.

| 영역 | 선택 |
|---|---|
| 런타임 | .NET 10 (LTS), Windows 10/11 x64 |
| WebRTC (Host) | `SIPSorcery` 10.0.x — **.NET 10 명시 지원**(csproj `TargetFrameworks`에 `net10.0`) |
| 화면 캡처 | **WGC 기본 / DXGI 폴백** — 하이브리드 GPU(Intel+NVIDIA)에서 DXGI 복제가 `DXGI_ERROR_UNSUPPORTED`로 실패함을 실측 확인. WGC는 dirty rect를 주지 않는다 |
| 인코딩 | **`SIPSorceryMedia.FFmpeg`로 H.264** (확정). VP8 임시 단계 없음. `SIPSorceryMedia.Encoders`는 deprecated, 사용 금지 |
| FFmpeg | **8.1 ABI · LGPL 빌드만** 동봉(`third_party/ffmpeg/`). 시스템 PATH에 의존하지 않고 로드 경로를 명시 |
| H.264 인코더 | `h264_nvenc` → `h264_qsv` → `h264_amf` → `h264_mf`, 없으면 `libopenh264`. **`libx264`는 GPL이므로 금지** — 기본값에 맡기지 말고 명시 지정 |
| 입력 주입 | Win32 `SendInput` (P/Invoke), SAS는 `SendSAS` |
| Host UI | WPF |
| 시그널링 | **Go** + `net/http` + `coder/websocket` (TCP 25001) |
| TURN | coturn (docker) — 직접 구현하지 않는다 |
| Viewer | 정적 HTML/CSS/바닐라 JS (현재 Node.js 빌드 단계 없음) |

## 프로젝트 구조

```
RemoteAssist.slnx
├─ src/
│  ├─ RemoteAssist.Host.Service/   # Windows 서비스 골격, 목표는 세션·권한 감독
│  ├─ RemoteAssist.Host.Tray/      # 트레이 UI(WPF): 동의·상태
│  ├─ RemoteAssist.Host.Core/      # 공통 로직(캡처/인코딩/입력/프로토콜)
│  └─ RemoteAssist.Protocol/       # 데이터채널 메시지 C# DTO
├─ server/signaling/               # 시그널링 서버 (Go 모듈, 별도 빌드)
├─ web/viewer/                     # 지원자 웹 뷰어(정적 SPA)
├─ deploy/
│  ├─ coturn/                      # coturn 설정·docker-compose
│  └─ signaling/                   # 시그널링 Dockerfile·compose
└─ docs/
   ├─ stack.md                     # 언어 선택·라이브러리 버전
   └─ protocol.md                  # 와이어 포맷 단일 기준
```

`RemoteAssist.slnx`에는 C# 프로젝트와 진단 도구만 넣는다. Go 모듈은 `go build ./...`로 따로 빌드한다.
실제 화면 공유 실행은 `tools/RemoteAssist.HostRun`, 캡처 확인은 `tools/RemoteAssist.CaptureDump`다.

## 데이터 채널 프로토콜

아래는 목표 채널 구성이다. 현재 Host/Viewer는 데이터채널을 생성하지 않는다.
입력 key/button 전이의 신뢰성과 좌표 규약은 리뷰 G02에 따라 구현 전에 확정한다.

언어가 둘이므로 **공유 DTO를 코드로 나눌 수 없다.** 와이어 포맷의 기준은 `docs/protocol.md` 하나이고,
C# 레코드(`RemoteAssist.Protocol`)·Go 구조체·JS 객체가 각각 그것을 구현한다. 스키마 변경은 **항상 문서 → 양쪽 코드** 순서로 한다.

| 채널 | 신뢰성/순서 | 용도 |
|---|---|---|
| `input` | unreliable · ordered | 마우스·키보드 (최신성 우선) |
| `control` | reliable · ordered | 세션 제어, 모니터 전환, 품질 조절 |
| `file` | reliable · ordered | 파일 전송(≈16KB 청크, 백프레셔 적용) |
| `chat` | reliable · ordered | 텍스트 채팅, 시스템 정보 |

- **좌표는 항상 0~1 정규화**해서 보내고 Host가 실제 픽셀로 환산한다. 뷰어 창 크기·DPI에 의존하지 않는다.
- 파일 전송: `file-offer` → `file-accept` → 순번 청크 → `file-complete`(체크섬 검증).

```jsonc
{ "t": "mouse", "a": "move", "x": 0.4213, "y": 0.6187, "mon": 0 }
{ "t": "key", "a": "down", "vk": 65, "mods": ["ctrl"] }
```

## 동의 모델 — 구현 시 반드시 지킬 것

- 세션 코드는 **일회성·단기 만료**, 대역 외로 전달되는 **PIN 동반**, 실패 횟수 제한.
- 동의 대화상자에서 **화면 보기 / 원격 제어 / 관리자 권한을 각각 독립 토글**로 승인받는다.
  관리자 권한 부여는 화면 보기·제어와 별개의 승인 항목이며 별도 게이트를 한 번 더 거친다.
- 세션 중에는 화면 테두리·트레이 배지로 접속 중임을 **상시 표시**하고, 원클릭 즉시 종료를 제공한다.

## 개발 로드맵

리스크가 큰 것(캡처·인코딩·입력·권한)을 앞에 배치했다. 각 Phase는 앞 Phase 위에 쌓인다.

| Phase | 목표 | 완료 기준 |
|---|---|---|
| 1 | 시그널링 + 화면 보기 (PoC) | 웹 뷰어에 대상 화면이 뜬다 |
| 2 | 원격 입력 + 서비스(SYSTEM) 골격 | 화면을 마우스·키보드로 조작한다 |
| 3 | 동의·보안 모델 | 코드/PIN·동의·암호화·즉시 종료·감사 로그 완성 |
| 4 | NAT 통과·릴레이 | 방화벽 뒤에서도 P2P↔TURN 폴백으로 연결된다 |
| 5 | 관리자 모드 + 보조 + 차별화 1차 | UAC/SAS/잠금화면 조작, 파일·채팅·다중 모니터, 상태 대시보드·Runbook |
| 6 | 배포·안정화 | 서명된 설치본, 자동 업데이트, 적응형 품질 |

## 차별화 방향 (기본기만으로는 기존 도구를 못 이긴다)

목적 특화 축 세 가지: **진단**(시스템 상태 대시보드·원격 탐색기), **자동화/AI**(Runbook 스크립트, AI 진단 어시스턴트), **보안·거버넌스**(self-hosted 릴레이, RBAC·승인 워크플로).
1차 착수는 **시스템 상태 대시보드 + Runbook**부터. AI 어시스턴트의 실행형 조치는 항상 human-in-the-loop.
차별화 기능을 넣을 때마다 위 권한·동의·보안 모델을 위반하지 않는지 함께 검토한다.

## 주요 리스크

| 리스크 | 대응 |
|---|---|
| **Session 0에서 화면 캡처 불가** — WGC·DXGI 모두 사용자 세션 필요 | 서비스가 사용자 세션에 캡처 헬퍼를 띄우고 IPC로 프레임 수신 (T2.4에서 설계) |
| 데스크톱 전환(UAC) 구현 난도 | 별도 최소 예제로 먼저 검증 후 통합 |
| **FFmpeg GPL 오염** — libx264 포함 빌드 동봉 시 제품 전체가 GPL 조건 | LGPL 빌드만 동봉 + 인코더 명시 지정. 선택된 인코더가 libx264가 아님을 로그로 확인 |
| LGPL 준수 — 동적 링크·별도 DLL 유지(단일 파일 병합 금지), 고지·소스 제공 | 설치 패키지 설계에 반영. 부담이 크면 Media Foundation 직접 구현으로 전환 — `IVideoEncoder` 추상화가 그 보험 |
| FFmpeg ABI 버전 불일치 | 8.1 고정, 동봉 경로 명시 로드 |
| H.264 HW 인코더 부재·드라이버 편차 | HW 우선 + `libopenh264` SW 폴백, GPU 다른 머신 2대 이상 + VM에서 검증 |
| C#·Go 간 스키마 드리프트 | 기준은 `docs/protocol.md` 하나. 통합 테스트에 왕복 파싱 케이스 포함 |
| C# H.264 HW 인코딩 파이프라인 | FFmpeg H.264로 구현. 초기화 실패 폴백·키프레임·포맷 변경은 리뷰 R01/R02/R13에서 보완 |
| 백신·SmartScreen 오탐 | 코드 서명, 벤더 화이트리스트, 조기 스캔 테스트 |
| TURN 대역폭 비용 | P2P 성공률 모니터링, 리전별 coturn 배치 |
| 오남용·사회공학 | 동의 화면에 접속자 신원 강조, 대역 외 코드, 배포 통제 |

## 컨벤션

- 개발용 포트는 TCP `25001~25199`, UDP `27001~27199` 범위에서만 할당한다. 시그널링은 TCP `25001`.
- 성능 수치(지연·FPS·비트레이트)는 추정하지 말고 실측값을 기록한다.
- 원격 제어 도구는 오남용 소지가 크다. 배포·운영 시 조직 보안 정책과 관련 법규(개인정보·정보통신망) 검토를 병행한다.
