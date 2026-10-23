# 기술 사양

[제품 계약](product-contract.md)·[프로토콜](protocol.md)이 지원/수용값과 와이어 기준이다. 진행 상태는 [실연결 현재 상태](live-e2e-test.md#9-현재-상태와-남은-과제)에서 관리한다.

.NET SDK 10.0.400, Protocol net10.0, Windows 프로젝트 net10.0-windows10.0.19041.0이다. NuGet은 packages.lock.json과 locked restore, Go는 go.mod/go.sum으로 고정한다. SIPSorcery/SIPSorceryMedia.FFmpeg 10.0.16, Vortice 3.8.3, Microsoft.Extensions 10.0.11, Go 1.27/coder-websocket v1.8.15를 사용한다. Viewer는 정적 ES module이고 Node는 회귀 테스트용이다.

## Host 설정 (`host.settings.json`)

| 항목 | 기본/검사 |
|---|---|
| TunnelUri | 기본 http://localhost:25001. loopback 밖은 HTTPS 필수 |
| AllowLoopbackHttp | false. true면 loopback http만 허용 |
| TunnelCertificateBase64 | 선택. 지정하면 그 DER 인증서와 정확히 일치하는 서버만 신뢰(자체 서명 핀 고정). 머신 인증서 저장소는 바꾸지 않는다 |
| TargetFps / BitrateKbps | 30 / 4000; 1..60 / 100..50000 |
| MonitorIndex | 0; 시작 시 현재 목록 범위 |
| PreferredEncoder | null은 허용 후보 순서, 명시는 단일 후보 엄격 실패 |
| MaxOutputLongEdge / ShortEdge | 1920 / 1080, 최대 3840/2160, 짝수 축소·확대 없음 |
| StunServers | 무시한다. 이미 배포된 설정에 남아 있어도 읽기만 한다 |

알 수 없는 필드·중복 필드는 거부한다. 지원 ID(`DeviceCode`)는 설정이 아니라 `ProgramData/RemoteAssist/device-id.txt`에서 읽는다.

## 터널링 서버 환경

| 환경 변수 | 정책 |
|---|---|
| RA_MODE | `development`(loopback 평문만), `lan`(사설 IP 또는 0.0.0.0, TLS 필수, 자체 서명 가능), `production`(TLS 필수) |
| RA_LISTEN | 기본 127.0.0.1:25001; TCP 25001..25199 |
| RA_TLS_CERT / RA_TLS_KEY | lan·production 필수 |
| RA_SESSION_TTL | 난수 코드 방의 대기 기간. 기본 10m, 30s..30m. 고정 지원 ID 방은 Host가 연결된 동안 만료되지 않는다 |
| RA_VIEWER_DIR | 선택. 허용 정적 파일만 제공 |
| RA_ALLOWED_ORIGINS | 선택. 정확한 host:port 목록, wildcard 금지. 비우면 같은 호스트만 |
| RA_OPERATOR_SECRET_FILE / RA_AUDIT_DIR | 선택. 지정하면 시작 때 검사하고 /metrics·/audit를 연다 |

static allowlist: index/app/css/connection/input/geometry/features/files/sha256/runbooks/favicon. CSP·frame 차단·nosniff·referrer·HSTS를 적용한다. /healthz는 생존, /readyz는 시작 오류·감사 장애·종료 상태다.

룸 1000, 연결+upgrade 예약 2000, join 10초, heartbeat 5초/read lease 15초, 쓰기 5초, peer 이탈 통지 3초, HTTP shutdown 5초. 터널링 서버 128KiB/SDP 96KiB/ICE 8KiB, JSON depth 32. 감사 event 4096bytes, journal 4MiB×4/7일.

## 미디어·데이터

Host WebRTC는 UDP 27002..27099의 host 후보만 쓴다. STUN/TURN은 없다. 방화벽 규칙은 Helper 실행 파일과 이 포트 범위, 터널링 서버 TCP(443, 설정 포트)에 한정한다.

캡처는 모니터 handle/deviceName/geometry·BGRA/stride를 검사한다. WGC 우선, 초기화 실패 때 같은 모니터의 DXGI를 한 번 쓴다. 보안 데스크톱은 AdminHelper의 DXGI 경로로 캡처한다. 화면 구성이 바뀌면 같은 모니터(없으면 주 모니터)로 자동으로 다시 맞춘다.

FFmpeg 8.1 ABI의 7개 shared DLL은 고정 SHA256 검사 후 명시 경로에서 로드한다. GPL/nonfree/libx264와 PATH fallback을 금지한다. H.264는 constrained baseline/mode1이며 실제 SPS의 profile/level을 검사한다. 후보 nvenc → qsv → amf → mf → openh264는 실제 Configure·시험 출력 뒤에만 선택한다. h264_amf에는 `constrained_baseline` profile 값을 쓴다(`baseline` 값이 없다). PLI/FIR와 시작·재개·2초 주기 IDR을 보낸다. RTP는 actual monotonic 90kHz sample time, 1200byte NAL/FU-A다.

파일은 최대 256MiB/청크 16KiB/credit 16/한 건·SHA256·NTFS handle 확정, 수락 30초/무진행 10초/전체 10분. chat 4096자, clipboard 8192자/UTF-8 16KiB와 hash, 2초 응답. 진단 provider 2초 상한. Runbook은 등록 두 작업 + 원격 명령(8192자), 동시 1개, worker 20초, stdout 8192자·stderr 4096자, Job 자식 2개·메모리 제한.

## 개발 진단 도구

HostRun은 화면만 보내는 개발 진단이다(입력·관리자·파일·Runbook은 서비스 설치본에서만 동작). `--encoder-check`는 화면·네트워크 없이 합성 1080p 60프레임으로 후보별 출력·디코딩·처리량(30fps 이상)·IDR 복구를 검사한다. CaptureDump·SessionProcess 진단·BoundaryProbe·InputTarget은 실제 화면/입력을 쓰므로 계획을 세운 뒤 실행한다.
