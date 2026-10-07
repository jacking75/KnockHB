# 기술 사양

[문서 안내](README.md) · [아키텍처](architecture.md) · [와이어 포맷](protocol.md) · [실행 가이드](development-guide.md)

이 문서는 **현재 코드의 고정값과 제약**을 설명한다. “목표”라고 표시한 항목은 아직 구현 또는 검증을 마치지 않았다. 패키지 버전은 저장소 선언값이며 이번 문서 작업에서 최신 버전 여부나 패키지 복원 성공을 보증하지 않는다.

## 1. 런타임과 프로젝트

| 항목 | 선언/선택 | 근거 |
|---|---|---|
| Host 언어 | C#, .NET 10 | Windows API·WPF·서비스 통합 |
| Windows TFM | `net10.0-windows10.0.19041.0` | `Directory.Build.props` |
| 지원 OS 선언 하한 | Windows 10 빌드 19041 | Windows 프로젝트의 `SupportedOSPlatformVersion` |
| 대상 배포 아키텍처 | Windows x64 | 네이티브 FFmpeg 동봉 정책; 플랫폼별 E2E는 별도 |
| Protocol TFM | `net10.0` | Windows 전용 API 없는 DTO 프로젝트 |
| C# 설정 | nullable, implicit usings, latest language, invariant globalization | 공통 props |
| 서비스 | .NET Generic Host + WindowsServices | 등록 이름 `RemoteAssist Host`, 현재 골격 |
| Tray | WPF, `asInvoker` | 현재 빈 MainWindow |
| 시그널링 | Go, `net/http`, `coder/websocket` | 독립 `go.mod`, `go 1.27` 선언 |
| Viewer | HTML/CSS/바닐라 JavaScript | ES module, 현재 package.json/Node 빌드 단계 없음 |
| DB | 없음 | 룸은 메모리 맵; MySQL을 사용하지 않음 |

시스템에 어떤 SDK가 설치되어 있는지는 이 표로 알 수 없다. 실제 개발 환경은 [실행 가이드](development-guide.md)의 명령으로 확인한다. 과거 환경 기록은 [stack.md](stack.md)에 있다.

### 고정 패키지

| 패키지 | 선언 버전 | 담당 |
|---|---|---|
| SIPSorcery | 10.0.16 | Host PeerConnection·RTP 송신 |
| SIPSorceryMedia.FFmpeg | 10.0.16 | H.264 인코딩 래퍼 |
| Vortice.Direct3D11 / Vortice.DXGI | 3.8.3 | GPU 자원·DXGI 인터롭 |
| Microsoft.Extensions.Logging.Abstractions | 10.0.11 | Core 로깅 인터페이스 |
| Microsoft.Extensions.Logging.Console | 10.0.11 | HostRun 콘솔 로그 |
| Microsoft.Extensions.Hosting / WindowsServices | 10.0.11 | 서비스 호스팅 |
| github.com/coder/websocket | v1.8.15 | Go WebSocket |

NuGet 버전은 [Directory.Packages.props](../Directory.Packages.props), Go 의존성은 [go.mod](../server/signaling/go.mod)와 `go.sum`이 기준이다. 대체 인코더를 도입하더라도 deprecated로 지정된 `SIPSorceryMedia.Encoders`를 다시 추가하지 않는 것이 프로젝트 정책이다.

## 2. Host 설정

### ScreenShareOptions

| 속성 | 기본값 | 현재 의미 |
|---|---|---|
| `SignalingUri` | `http://localhost:25001` | HTTP 발급과 WS 참가의 서버 원점 |
| `MonitorIndex` | `0` | 시작 시 캡처할 모니터 인덱스 |
| `TargetFps` | `30` | 캡처 루프 목표 간격과 RTP duration 계산에 사용 |
| `BitrateKbps` | `4000` | 인코더 초기 비트레이트; 실제 측정치 아님 |
| `PreferredEncoder` | `null` | null이면 기본 후보 순서, 지정하면 허용 후보 내 우선 배치 |
| `StunServers` | `stun:stun.l.google.com:19302` | Host의 STUN 목록 |

HostRun의 옵션은 `--signaling`, `--monitor`, `--fps`, `--bitrate`, `--encoder`, `--encoder-check`다. `--encoder`는 이름과 달리 **엄격한 단일 후보 강제가 아니다.** 선택 실패 시 다른 허용 후보를 계속 시도하며, `libx264`처럼 목록에 없는 값은 목록에 추가하지 않는다. 실제 초기화 단계 실패는 아직 다음 후보로 이어지지 않는다(R01).

공개 옵션의 유효 범위 검사는 미흡하다. 특히 FPS 0은 사용하면 안 되며, 명시적 범위 검증을 R10에서 추가할 예정이다. 기본값 30fps/4000kbps를 성능 보증이나 네트워크 사용량 실측으로 인용하지 않는다.

### CaptureDump 옵션

| 옵션 | 기본값 | 설명 |
|---|---|---|
| `--list`, `-l` | false | 공통 모니터 목록과 WGC 지원 여부 출력 |
| `--monitor`, `-m` | 0 | 대상 모니터 |
| `--frames`, `-n` | 3 | 저장할 PNG 개수 |
| `--out`, `-o` | `capture-dump` | PNG 출력 디렉터리 |
| `--backend`, `-b` | `auto` | `wgc`, `dxgi`, 자동 선택 |

HostRun에는 캡처 backend CLI 옵션이 없다. backend별 문제를 분리하려면 CaptureDump를 사용한다. 두 도구는 시작 시 프로세스 DPI awareness를 설정한다.

## 3. 시그널링 API와 세션 자격정보

| 메서드·경로 | 기능 | 현재 응답/행동 |
|---|---|---|
| `GET /healthz` | 프로세스 HTTP 상태 확인 | `200`, 본문 `ok` |
| `POST /session` | 룸 생성 | `200`, code/pin/hostToken/expiresIn |
| `GET /ws` | WebSocket upgrade | 첫 JSON 메시지가 반드시 join |
| `GET /` 등 | 선택적 Viewer 정적 서빙 | `RA_VIEWER_DIR`가 설정된 경우 |

`/healthz`는 GPU, FFmpeg, 브라우저 연결, TURN을 검사하지 않는다. health 응답만으로 화면 공유 전체가 정상이라고 판정하지 않는다.

### 자격정보

| 필드 | 생성 방식/형태 | 소비자 |
|---|---|---|
| `code` | `crypto/rand` 기반 9자리 숫자 문자열 | Host·Viewer가 같은 룸 선택 |
| `pin` | 난수 4자리 문자열 | Viewer 인증 |
| `hostToken` | 난수 24바이트의 URL-safe Base64, padding 없음 | Host 인증 |
| `expiresIn` | 설정 TTL의 초 단위 정수 | 유효기간 안내 |

코드 충돌은 최대 10회 재생성한다. PIN/token 비교에는 `subtle.ConstantTimeCompare`를 사용한다. Host/Viewer 슬롯은 각각 하나다. 코드·PIN은 숫자가 아닌 문자열로 취급해야 선행 0이 사라지지 않는다.

현재 없는 보장은 인증된 사용자 계정, 발급 rate limit, PIN 실패 횟수 제한, 코드 일회성 소비, 지원자 신원 확인이다. 자격정보를 알고 있다는 것과 대상자가 화면 공유를 승인했다는 것은 다른 조건이다.

### 메시지 계약

| 메시지 | 주 사용 방향 | 역할 |
|---|---|---|
| `join` / `joined` | 클라이언트→서버 / 서버→참가자 | 인증·슬롯 점유 / 참가 결과 |
| `peer-joined` / `peer-left` | 서버→남은 피어 | 반대편 입장·이탈 알림 |
| `offer` / `answer` | 의도상 Host→Viewer / Viewer→Host | SDP 교환 |
| `ice` | 양방향 | 후보 교환 |
| `bye` | 양방향 | 명시적 종료 메시지 |
| `error` | 서버→클라이언트 | 오류 code/reason |

현재 서버는 릴레이 type을 허용하지만 offer/answer의 역할별 방향·협상 상태를 엄격히 검사하지 않는다. 릴레이 시 인증 필드를 빼고 `type`, 서버가 정한 `room`, `sdp`, `ice`만 전달한다. 정확한 JSON과 오류 코드 목록은 [protocol.md](protocol.md) 하나에서 관리한다.

## 4. 서버 설정과 시간 제한

| 환경 변수 | 기본값 | 의미 |
|---|---|---|
| `RA_LISTEN` | `:25001` | HTTP 리슨 주소. 기본은 loopback 전용이 아님 |
| `RA_SESSION_TTL` | `10m` | 코드 참가 유효기간 |
| `RA_ALLOWED_ORIGINS` | 없음 | WS 허용 Origin 패턴 목록, 쉼표 구분. 기본 동일 호스트 검사 |
| `RA_VIEWER_DIR` | 없음 | 정적 Viewer 파일 디렉터리 |

TTL 문자열 파싱 실패 시 기본값을 사용한다. 0 이하 값은 Server 생성 시 10분으로 보정한다. 잘못된 설정을 항상 startup error로 처리하는 구현은 아니다.

| 제한 | 값 | 비고 |
|---|---|---|
| HTTP 헤더 읽기 | 10초 | HTTP Server 설정 |
| 첫 join 대기 | 10초 | WS 접속 후 첫 메시지 |
| WS 메시지 읽기 크기 | 128KiB | Go 서버 |
| WS 쓰기 | 5초 | 쓰기 락 획득 후 설정되는 timeout |
| 이탈 통지 | 3초 | 별도 context |
| 만료 청소 주기 | 1분 | 빈 만료 룸 제거 |
| HTTP shutdown | 10초 | 열린 WS 전체 종료 보증과는 별개 |

참가 TTL과 지원 작업 최대 시간을 구분한다. 설계 의도는 연결된 지원 작업을 TTL만으로 끊지 않는 것이다. 현재 `Get`과 `Sweep`의 활성 룸 처리 차이와 참가/삭제 경쟁은 R12에 남아 있다.

## 5. 영상 사양

| 항목 | 현재 값/구현 |
|---|---|
| 입력 화면 | 시작 시 선택한 단일 모니터 |
| 픽셀 형식 | BGRA, 4바이트/픽셀 |
| 캡처 선택 | WGC IsSupported가 true면 WGC, false면 DXGI |
| 캡처 대기 | 세션 루프에서 최대 5ms 요청 |
| 정적 화면 | 새 프레임이 없으면 직전 프레임 재인코딩 |
| WGC 변경 영역 | 제공하지 않음; `DirtyRectCount=-1` |
| DXGI 변경 영역 | 로깅용으로 수집; 부분 화면 인코딩에는 미사용 |
| 코덱 | H.264 단일 트랙, SendOnly |
| Host SDP 포맷 설정 | payload type 102, clock rate 90000, `packetization-mode=1` |
| RTP duration | `90000 / TargetFps` 정수값 |
| 전송 | SIPSorcery `SendVideo` |
| 표시 | 브라우저 `<video autoplay playsinline muted>`, `object-fit: contain` |

WGC 초기화 자체가 실패했을 때 DXGI로 다시 시도하는 일반적인 런타임 폴백은 없다. 해상도 변경, DXGI 회전·커서, 서로 다른 모니터 번호 체계는 R02~R04의 보완 대상이다. H.264 profile/level 정합성과 실제 송신 간격 대비 RTP 시간의 영향은 G04의 검증 대상이다.

### 인코더 선택 정책

| 순서 | 후보 | 옵션 |
|---|---|---|
| 1 | `h264_nvenc` | preset p1, tune ull, rc cbr, delay 0, zerolatency 1 |
| 2 | `h264_qsv` | preset veryfast, low_power 1, async_depth 1 |
| 3 | `h264_amf` | usage ultralowlatency, quality speed |
| 4 | `h264_mf` | hw_encoding true, low_latency true |
| 5 | `libopenh264` | allow_skip_frames 1 |

이 표는 코드가 전달하는 옵션이다. 모든 환경에서 옵션이 수락되고 실제 인코딩에 성공했다는 뜻은 아니다. `SetBitrate`를 초기화 전에 호출하며, 초기화와 프레임 출력까지 성공해야 인코더 사용 가능으로 판정해야 한다.

### FFmpeg 배치와 로드

프로젝트 정책은 FFmpeg 8.1 ABI의 LGPL shared 빌드를 별도 DLL로 배치하고 `libx264`를 선택하지 않는 것이다. `Host.Service`는 `PublishSingleFile=false`를 선언한다. 배포 고지·소스 제공 등은 [FFmpeg 안내](../third_party/ffmpeg/README.md)의 체크리스트를 따른다.

`FFmpegLoader`의 탐색 순서는 다음과 같다.

1. 호출자가 명시한 `libraryPath`가 있으면 해당 디렉터리.
2. 실행 파일 디렉터리의 `ffmpeg`.
3. 실행 파일 디렉터리의 `runtimes/win-x64/native`.
4. 실행 파일 위치에서 최대 8단계 상위까지의 `third_party/ffmpeg/bin`.

자동 탐색은 `avcodec-*.dll` 존재로 후보를 찾는다. 파일명 탐색 자체가 ABI·전체 DLL 세트·빌드 라이선스 검증은 아니다. 프로젝트 로더는 시스템 PATH를 찾는 fallback을 구현하지 않았으며, 찾지 못하면 실패한다. 현재 checkout에는 동봉 바이너리가 없을 수 있으므로 과거 “배치 완료” 기록과 구분한다.

## 6. 데이터채널과 향후 기능

**현재 PeerConnection에는 비디오 트랙만 추가한다. 아래 데이터채널은 생성하지 않는다.**

| 채널 | 기존 프로토콜 초안 | 용도와 선행 검토 |
|---|---|---|
| `input` | unreliable, ordered | 마우스·키보드; key/button 전이의 신뢰성 재설계 필요(G02) |
| `control` | reliable, ordered | 동의·관리자 요청·품질·모니터·통계 |
| `file` | reliable, ordered | 약 16KB 청크, 수락·체크섬·백프레셔 |
| `chat` | reliable, ordered | 채팅·클립보드·시스템 정보 초안 |

기존 계획의 대시보드는 control 채널을 언급하지만 protocol 예시는 chat에 sysinfo를 둔다. 구현 전 채널 배치와 스키마를 `protocol.md`에서 확정해야 한다. 이 문서에서 임의로 다른 와이어 계약을 확정하지 않는다.

## 7. 네트워크·배포

| 대상 | 현재 설정 | 주의점 |
|---|---|---|
| 시그널링 | TCP 25001, HTTP/WS | 운영 TLS termination은 미구현 |
| 외부 STUN | `stun.l.google.com:19302` | 외부 서비스의 목적지 포트이며 앱 로컬 할당 포트가 아님 |
| 로컬 WebRTC 소켓 | 라이브러리/브라우저가 할당 | 코드에 UDP 허용 대역 강제 설정 없음 |
| coturn listening | 27001 | compose는 UDP 전용을 강제하지 않음; TCP 노출도 검토 필요 |
| coturn relay 범위 | 27100~27199 | 현재 앱의 TURN 통합 없음 |
| 개발용 허용 대역 | TCP 25001~25199, UDP 27001~27199 | 구성 변경 및 테스트 listener 할당 시 준수 |

coturn compose는 `network_mode: host`, `coturn/coturn:latest`, 개발용 고정 자격증명을 사용한다. OS/Docker host networking 조건, TURN TCP/TLS listening 포트, 운영 자격증명과 이미지 고정은 배포 전에 확정해야 한다. 현재 compose가 프로젝트 포트 정책 전체를 충족한다고 가정하지 않는다.

시그널링 Dockerfile은 `golang:1.27`에서 Linux 정적 바이너리를 빌드해 distroless nonroot 이미지에 넣는다. 최종 이미지에는 Viewer 정적 파일이나 TLS 프록시가 포함되지 않는다. 외부 배포용 웹 서빙·HTTPS·TURN·인증·설치본은 후속 태스크다.

## 8. 검증 수준

이 저장소에는 Go signaling 테스트가 있고 C# 및 Viewer 전용 자동 테스트 프로젝트는 아직 없다. 이전 기록의 루프백 영상 표시와 인코딩 처리량은 당시 환경의 관측값이다. 최신 환경·다중 머신·TURN·UAC 지원을 증명하지 않는다.

검증 명령과 실제 머신 체크리스트는 [실행·검증 가이드](development-guide.md)에 있다. 공개 API나 프로토콜을 바꿀 때는 [리뷰·태스크 리스크](../docs_working/code-review-task-risks-2026-10-07.md)의 회귀 조건을 함께 적용한다.
