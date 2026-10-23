# KnockHB / RemoteAssist

사내 PC 관리 운영 부서용 Windows 원격 지원 도구다. 저장소 이름은 KnockHB, 제품·네임스페이스는 RemoteAssist다.

지원이 필요한 사람이 프로그램을 받아 **관리자 권한으로 한 번** 실행하면 끝이다. 그 뒤 지원자가 고정 9자리 지원 ID로 접속해 모든 기능을 쓴다. 대상자의 추가 승인·PIN은 없고, 끊기면 같은 지원 ID로 자동 재접속한다. 대상자 화면에는 접속 표시와 즉시 종료 버튼이 항상 남는다.

## 기능

- 화면 보기(1080p H.264, 하드웨어 인코더 우선)와 마우스·키보드·한글 입력
- 관리자 모드: UAC 보안 데스크톱·잠금 화면·관리자 창 조작, Ctrl+Alt+Del
- 파일 주고받기, 텍스트 클립보드 양방향, 채팅
- 시스템 진단, 등록된 Runbook, 원격 PowerShell 명령(사용자 또는 SYSTEM 권한)
- 다중 모니터 전환, 적응 품질

## 구성

```
대상 PC                                              지원자 PC
┌───────────────────────────────────────────┐        ┌──────────────────────┐
│ Host.Service (SYSTEM, Session 0)          │        │ 터널링 서버 (Go, TLS) │
│  └ 사용자 세션: Tray(WPF) · Helper         │◀──────▶│  Viewer 페이지 제공·  │
│                 · AdminHelper(SYSTEM)     │ HTTPS  │  첫 SDP/ICE 교환      │
│                 · RunbookWorker           │  /WSS  ├──────────────────────┤
│                                           │◀══════▶│ 웹 Viewer (브라우저)  │
└───────────────────────────────────────────┘ 직접 P2P└──────────────────────┘
                                         (화면·입력·파일, DTLS-SRTP/SCTP)
```

- 미디어와 데이터는 두 PC 사이 **직접 P2P**로만 흐른다. STUN/TURN·릴레이는 없다.
- 터널링 서버는 지원자 PC가 여는 접속 지점이다. 대상 PC는 설치 때 받은 주소로 여기에 접속한다.

| 영역 | 기술 |
|---|---|
| Host | C# .NET 10, WPF, SIPSorcery(WebRTC), FFmpeg 8.1 LGPL(H.264), WGC/DXGI 캡처, `SendInput` |
| 터널링 서버 | Go, `net/http`, `coder/websocket` (TCP 25001) |
| Viewer | 정적 HTML/CSS/바닐라 JS, 브라우저 내장 WebRTC (설치 불필요) |

## 빠른 시작

준비물: Windows 10/11 x64, .NET 10 SDK(WindowsDesktop 포함), Go 1.27, Node 22 이상, Python(`cryptography`, TLS 시험 인증서용).

```powershell
# FFmpeg LGPL DLL 배치 (저장소에 커밋하지 않는다)
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/prepare-ffmpeg.ps1

# 빌드와 테스트
dotnet restore RemoteAssist.slnx --locked-mode
dotnet build RemoteAssist.slnx --no-restore
dotnet run --project tests/RemoteAssist.Host.Core.Tests --no-build
cd server/tunnel; go vet ./...; go test ./...; cd ../..
node --test web/viewer/tests/*.test.js

# 한 PC 통합 검사 (실제 터널링 서버 + Host 세션 + 가짜 Viewer)
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/loopback-check.ps1
```

- 두 PC 실연결 시험은 [두 PC 실연결 시험](docs/live-e2e-test.md)을 따른다.

## 저장소 구조

| 경로 | 내용 |
|---|---|
| `src/` | Host.Service · Host.Tray · Host.Helper · Host.AdminHelper · Host.RunbookWorker · Host.Core · Protocol · Update |
| `server/tunnel/` | 터널링 서버(Go 모듈, 별도 빌드) |
| `web/viewer/` | 지원자 웹 Viewer |
| `tests/` | Host.Core 회귀 테스트, 설치·준비 스크립트 계약 테스트, 공유 fixture |
| `tools/` | 개발용 진단 도구(HostRun, CaptureDump, LoopbackCheck 등). 배포본에 들어가지 않는다 |
| `scripts/` | 빌드·패키징·개발 설치·실연결 준비/측정 스크립트 |
| `deploy/` | 설치·준비 스크립트, Runbook, 터널링 서버 컨테이너 |
| `third_party/ffmpeg/` | FFmpeg 공급 기준·고지(바이너리는 커밋하지 않는다) |
| `docs/` | 기준 문서 |
| `docs_working/` | 개발 중 임시 계획·조사·리뷰를 두는 디렉터리(필요할 때만 만든다) |

## 문서

[개발자 문서 안내](docs/README.md)에서 시작한다.

| 문서 | 내용 |
|---|---|
| [제품 계약](docs/product-contract.md) | 신뢰 모델, 기능 범위, 수용 기준 |
| [아키텍처](docs/architecture.md) | 프로세스 구조, 허가·세션 수명, 권한 경계 |
| [기술 사양](docs/technical-specification.md) | 설정, 상한, 의존성 |
| [프로토콜](docs/protocol.md) | 와이어 포맷의 단일 기준 |
| [워크플로우](docs/workflows.md) | 시작·연결·기능·재접속 흐름 |
| [최초 관리자 실행](docs/startup-preparation.md) | 지원 허가, 방화벽·SAS·GPU 준비 |
| [두 PC 실연결 시험](docs/live-e2e-test.md) | E2E 방법, 현재 상태, 남은 과제 |
| [설치/업데이트](deploy/installation.md) | 서명·보호 설치·롤백 |
| [터널링 서버 운영](deploy/tunnel/README.md) | TLS·감사·컨테이너 |

## 상태 (2026-10-24)

두 PC 실연결에서 화면, 입력, 관리자/UAC, 재접속, 즉시 종료, 원격 명령, Runbook, 클립보드, 채팅, 파일 양방향, 1080p 30fps 송신을 확인했다.
- 입력-표시 지연은 기준(p95 150ms)에 못 미쳐 보류 중이다.
- 모니터 전환은 아직 실연결로 확인하지 못했다.

자세한 내용은 [현재 상태와 남은 과제](docs/live-e2e-test.md#9-현재-상태와-남은-과제)에 있다.

## 라이선스·주의

- FFmpeg는 LGPL 빌드만 동적 링크로 동봉한다. GPL 구성 요소(`libx264` 등)는 쓰지 않는다. 고지는 `third_party/ffmpeg/`에 있다.
- 원격 제어 도구는 오남용 소지가 크다. 사내 배포 통제, 접속 표시·즉시 종료 유지, 감사 기록을 전제로 하며 배포 전 조직 보안 정책과 관련 법규를 검토한다.
