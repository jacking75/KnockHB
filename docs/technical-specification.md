# 기술 사양

[제품 계약](product-contract.md)·[프로토콜](protocol.md)이 지원/수용값과 와이어 기준이다. 실행 완료는 [구현계획](../원격제어-구현계획.md)에서만 판정한다.

.NET SDK 10.0.400, Protocol net10.0, Windows 프로젝트 net10.0-windows10.0.19041.0이다. NuGet은 packages.lock.json과 locked restore, Go는 go.mod/go.sum으로 고정한다. SIPSorcery/SIPSorceryMedia.FFmpeg 10.0.16, Vortice 3.8.3, Microsoft.Extensions 10.0.11을 사용한다. Go 1.27/coder-websocket v1.8.15, Viewer는 정적 ES module이고 Node는 회귀용이다. 검증 runner는 Windows PowerShell5.1/PowerShell7을 지원하며 배포 패키징은 PowerShell7을 요구한다.

## 설정과 상한

| Host 설정 | 기본/검사 |
|---|---|
| SignalingUri, AllowLoopbackHttp | http://localhost:25001, false; 운영 HTTPS, 개발은 명시 loopback만 |
| TargetFps / BitrateKbps | 30 / 4000; 1..60 / 100..50000 |
| MonitorIndex | 0; 시작 시 현재 목록 범위 |
| PreferredEncoder | null은 허용 후보 순서, 명시는 단일 후보 엄격 실패 |
| StunServers / ForceRelay | 빈 목록 / false; stun/stuns 검사, relay에는 실제 TURN 필요 |
| MaxOutputLongEdge / ShortEdge | 1920 /1080, 최대3840/2160, 짝수 축소·확대 없음 |

HostRun은 엄격 CLI와 실패 exit를 제공하고 콘솔 화면 승인만 허용한다. CaptureDump는 같은 모니터 backend·PNG/프레임 수·60초 deadline·기존 결과 보존을 적용한다. SessionProbe는 Session/desktop/승격·모니터 정보를 읽을 뿐 입력을 주입하지 않는다. 개발 진단 성공은 제품 경로 수용이 아니다.

| 서버 환경 | 정책 |
|---|---|
| RA_MODE, RA_LISTEN | development/production, 기본127.0.0.1:25001; TCP25001..25199 |
| RA_SESSION_TTL | 기본10m, 30s..30m |
| RA_VIEWER_DIR | 운영 필수; 허용된 정적 파일만 |
| RA_ALLOWED_ORIGINS | 정확한 host:port 목록, wildcard 금지 |
| RA_TLS_CERT/KEY | 운영 필수, 직접 TLS1.2 이상 |
| RA_TURN_URLS/SECRET_FILE | 운영 필수, peer별10분 credential, 보호32..128 base64url 키 |
| RA_OPERATOR_SECRET_FILE / RA_AUDIT_DIR | 운영 node 인증/감사 디렉터리 필수 |

운영은 TLS/key/Viewer/TURN/node/audit를 시작 전에 검사한다. 개발 평문은 loopback만 허용한다. static allowlist에는 index/app/style/connection/input/geometry/features/files/sha256/runbooks/favicon만 있다. CSP/frame 차단/nosniff/referrer/HSTS를 적용한다. /healthz는 생존, /readyz는 시작 오류·키·감사 장애·종료 상태이며 실제 allocation PASS가 아니다. /metrics·/audit는 node bearer 인증이다.

발급은 IP당10회/분, limiter4096, 룸1000, 연결+upgrade예약2000이다. join10초, signaling heartbeat5초/read lease15초, 쓰기5초, peer 이탈3초, HTTP shutdown5초다. 시그널링128KiB/SDP96KiB/ICE8KiB/대기256, JSON depth32와 중복/미지 필드를 거부한다. metadata 감사 event4096bytes, spool4096, journal4MiB×4/7일이다.

## 미디어·데이터

Host UDP27002..27099, coturn TCP25002/25003, relay UDP27100..27199다. 브라우저 동적 소스 포트는 고정할 수 없다. 목적지 allowlist와 실제 TLS/방화벽 정책은 배포 자료를 따른다.

모니터 handle/deviceName/geometry·frame generation·BGRA/stride/최대256MiB를 검사한다. WGC resize는 이전 프레임을 폐기하고 같은 RT device를 재사용한다. DXGI는 rotation/커서와 부분 COM 생성 정리를 처리한다. 바뀐 물리 descriptor는 새 동의·capture/scaler/encoder 재구성·IDR/ACK가 필요하다. 제거 시 종료한다.

FFmpeg8.1 ABI의 7개 shared DLL은 고정 archive/DLL SHA256 이후 명시 경로에서 로드한다. GPL/nonfree/libx264와 PATH fallback을 금지한다. 현재 공급본은 LGPLv3이며 source recipe/80개 dependency pin과 전체 소스/고지 준비 도구가 있다. 실제 대응 소스 공개·재빌드·배포 법적 검토는 V02/V36이다.

H264는 constrained baseline/mode1, 명시 profile-level-id 제안과 실제 SPS를 검사한다. answer level3.1..5.2의 보수 한도로 출력/FPS/bitrate를 제한한다. 후보 nvenc→qsv→amf→mf→openh264는 실제 Configure/시험 SPS 출력 후에만 선택한다. PLI/FIR는 인증된 해당 video stream에 결합해 소유 인코더 스레드에 합친 요청으로 전달하며 시작/재개/2초 주기 IDR도 보낸다.

RTP는 actual monotonic90kHz sample time, 1200byte NAL/FU-A/마지막 marker다. encoder probe는 synthetic1080p60프레임·모두 decode·처리량30fps·fresh decoder IDR 복구를 별도 검사한다. 이것은 실제 캡처/브라우저 FPS 수용이 아니다. 적응 품질은 통계1초/5초 유지/15초 연속 안정 정책과 재사용 scaler를 사용한다.

input/pointer/control/file/chat/telemetry/runbook은 reliable ordered다. move는 최신1건 합치며 전이는 보존한다. 입력 identity/topology/epoch/sequence/barrier와 일반/특권 최종 권한 검사를 사용한다. 독립 Recovery는 down 전에 예약 ACK한다. peer generation마다 transport/채널 순번을 새로 만들며 permission generation은 같은 승인을 유지한다.

파일은 최대256MiB/청크16KiB/credit16/buffer한도·한 건·건별 수락·NTFS handle/충돌·SHA256·30초 승인/10초 idle/10분 전체 기한이다. chat4096자, clipboard8192자/UTF8 16KiB와 hash/2초 요청이다. 진단 provider와 목록에는 bounded timeout/길이/오류가 있다. Runbook은 고정 두 작업·인자 enum·동시1/최근ID256·승인28초/worker20초/출력4096자/Job 제한이다.

실제 media lease는 활성 후15초 첫 전송, 이후3초 정지로 helper Job을 종료한다. Viewer decodedFrames 정지는15초이며 monitor 승인/복구 중에는 감시를 중단한다. local stop은 이 타이머를 기다리지 않는다. managed 정리5초, 무응답 native는 서비스가 소유 프로세스로 격리한다. 설치/업데이트·운영·하네스의 상세는 deploy/와 tests/e2e 문서를 따른다.
