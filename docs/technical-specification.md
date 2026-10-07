# 기술 사양

2026-10-08 현재 코드 기준이다. 제품 지원 범위·시간·성능 수용값은 [제품 계약](product-contract.md), 와이어 필드는 [프로토콜](protocol.md)을 따른다. 진행 상태는 [구현계획](../원격제어-구현계획.md)에서만 관리한다.

## 런타임과 의존성

.NET SDK는 global.json의 10.0.400으로 고정한다. Protocol은 net10.0, Windows 프로젝트는 net10.0-windows10.0.19041.0이다. 제품 지원 OS는 Windows 10 22H2/11 x64이며 TFM 하한과 실제 지원 선언을 구분한다. SIPSorcery와 SIPSorceryMedia.FFmpeg는 10.0.16, Vortice는 3.8.3, Microsoft.Extensions 패키지는 10.0.11이다. NuGet은 packages.lock.json과 locked restore로 재현한다. Go 모듈은 Go 1.27과 coder/websocket v1.8.15를 요구한다. Viewer는 정적 ES module이며 Node 빌드는 없다. package.json은 Node 회귀 테스트 용도다.

## Host 옵션과 동의

| 옵션 | 기본값·범위 |
|---|---|
| SignalingUri | http://localhost:25001; 운영 HTTPS |
| AllowLoopbackHttp | false; HostRun --dev-loopback으로 명시 |
| TargetFps | 30; 1~60 |
| BitrateKbps | 4000; 100~50000 |
| MonitorIndex | 0; 시작 시 존재하는 공통 모니터 인덱스 |
| PreferredEncoder | null은 후보 순서; 지정은 단일 후보 강제 |
| StunServers | 빈 목록; 구성한 stun/stuns만 허용 |
| ForceRelay | false; --relay는 TURN 필요 |

HostRun CLI는 --signaling/--dev-loopback/--monitor/--fps/--bitrate/--encoder/--relay/--encoder-check/--out이다. 중복·미지원·누락·비숫자 값은 실패한다. RequestLocalConsent callback이 없으면 거절한다. 현재 콘솔의 '화면 승인' 입력만 screen을 부여한다. 캡처와 PeerConnection은 그 뒤 만든다. 코드/PIN은 로컬 콘솔에만 표시하며 일반 ILogger에 기록하지 않는다.

CaptureDump는 --backend auto/wgc/dxgi, --monitor, --frames 1~1000, --out, --list, --verbose를 지원한다. 기존 PNG를 덮어쓰지 않는다. 요청보다 적은 PNG는 실패하며 전체 대기는 60초로 제한한다. SessionProbe는 세션 ID·desktop·승격 여부·모니터·DXGI 출력만 읽으며 프로세스 승격이나 입력 주입을 수행하지 않는다.

## 서버 설정

| 환경 변수 | 기본값·정책 |
|---|---|
| RA_MODE | development 또는 production |
| RA_LISTEN | 127.0.0.1:25001; TCP 25001~25199 |
| RA_SESSION_TTL | 10m; 30s~30m |
| RA_VIEWER_DIR | 선택적 정적 자산 디렉터리; 운영 필수 |
| RA_ALLOWED_ORIGINS | 동일 호스트 기본; wildcard 금지 |
| RA_TLS_CERT/RA_TLS_KEY | 운영 필수; 직접 TLS 최소 1.2 |
| RA_TURN_URLS | turn/turns URL 목록; 운영 필수 |
| RA_TURN_SECRET_FILE | 최소 32바이트 secret 파일; 운영 필수 |

개발 평문 listener는 loopback만 허용한다. 운영은 인증서/키를 시작 전에 검사한다. Viewer는 index.html/app.js/connection.js/app.css/favicon.svg만 제공하며 디렉터리 목록·임의 파일을 제공하지 않는다. CSP·frame 차단·nosniff·referrer 정책을 적용하며 TLS 응답에는 HSTS를 추가한다.

GET /healthz는 HTTP 프로세스 상태다. GET /readyz는 현재 서버 종료 상태만 검사한다. TURN allocation이나 외부 도달성 검사로 해석하지 않는다. POST /session은 IP당 분당 10회, limiter map 최대 4096개, 룸 최대 1000개, 연결 및 upgrade 예약 합계 최대 2000개다. 설정 가능한 조직별 제한과 운영 관측은 E38에서 확장해야 한다.

join은 10초, 시그널링 read lease는 15초, heartbeat는 5초, 쓰기는 5초, 상대 이탈 통지는 3초다. HTTP 종료 deadline은 5초이며 별도로 hijacked WebSocket을 추적해 닫는다. 룸은 메모리 저장이며 서버 재시작 뒤 승인을 복원하지 않는다.

## 캡처·영상

승인 모니터는 시작 시 handle+deviceName으로 고정한다. WGC/DXGI에서 같은 공통 모니터를 확인하며 backend 열거 순서로 재선택하지 않는다. BGRA 프레임은 실제 width/height/stride/monitorIdentity/generation을 포함한다. 크기·stride·최대 256MiB 버퍼를 검사한다. WGC resize는 이전 프레임을 폐기하고 RT device를 재사용한다. COM 초기화 횟수는 소유 스레드에서 균형을 맞춘다. DXGI는 texture 크기와 회전, 컬러·monochrome·masked cursor를 반영한다. access lost는 오래된 화면을 계속 보내지 않고 실패로 종료한다. 실제 회전·해상도 변경 실증은 아직 남아 있다.

FFmpeg는 8.1 ABI shared DLL 7개이며 supply.json의 불변 release archive와 SHA-256으로 준비한다. Loader는 내장 libraries.json의 각 DLL hash를 검증한 뒤 명시 경로만 로드한다. PATH fallback은 없다. buildconf의 GPL/nonfree/libx264 활성화를 거부한다. 실제 공급본은 enable-version3이므로 LGPLv3 고지를 제공한다. 설치본의 전체 외부 라이브러리 대응 소스·배포 의무 검토는 미완료다.

H.264 후보 순서는 nvenc/qsv/amf/mf/openh264다. 후보별 Configure와 실제 시험 출력이 성공해야 선택한다. 실패 후보는 폐기하고 다음 후보를 시도한다. --encoder 지정 실패는 다른 후보 성공으로 숨기지 않는다. BGRA 크기는 짝수여야 하며 인코더는 한 소유 스레드에서만 사용한다. forceKeyframe과 RTCP video PLI/FIR은 요청을 합쳐 소유 스레드에서 실행한다. 시험 출력에서 소모한 SPS/PPS를 캐시해 IDR에 필요한 파라미터 세트를 넣는다.

RTP video clock은 90kHz이며 직전 실제 송신 간격을 duration으로 전달한다. 인코더 검사는 1080p 실제 출력 60/60·모든 프레임 디코딩·30fps 이상·fresh decoder 강제 키프레임 복구를 검사한다. 이 처리량은 캡처/네트워크/브라우저 표시 FPS가 아니다.

## 미완료 제품 경로

Service/Tray는 골격이다. 사용자 세션 헬퍼·인증 IPC·원격 입력·관리자/UAC/SAS·파일·채팅·클립보드·진단·Runbook·적응형 품질·설치/업데이트는 제품 경로에 연결되지 않았다. TURN credential 발급과 양쪽 설정 전달은 존재하지만 coturn allocation·갱신·rotation·복구를 검증하지 않았다. 지원 기능을 확대하기 전에 선행 권한·철회 계약과 로컬 실증을 충족해야 한다.