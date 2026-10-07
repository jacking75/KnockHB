# 엄격 코드 리뷰 및 태스크 리스크 — 2026-10-07

## 1. 판정과 범위

현재 코드를 **다중 머신 검증 또는 배포를 통과한 구현으로 볼 수 없다.** Phase 1 화면 공유 PoC에도 연결 순서, 종료, 인코더 초기화, 화면 크기 변경 관련 결함이 있다. 아래 R01~R18을 수정 태스크로 등록한다. Phase 2 이후 미구현 기능과 설계 위험은 G01~G06으로 구분한다.

- 검토 기준 커밋: `ceab45aeefcd36e9b79c1a8f6e9da33caceb856f`. 검토 시작 시 추적 파일 변경 없음.
- 범위: `src/`, `server/signaling/`, `web/viewer/`, `tools/`, `deploy/`, 설계서·구현계획·프로토콜·스택 문서.
- 실제 동작 경로: `HostRun → ScreenShareSession → WGC/DXGI → FFmpeg → WebRTC → Viewer`. Service는 대기 루프, Tray는 빈 창이며 입력 주입·IPC·동의 UI는 아직 없다.
- 이번 산출물은 리뷰와 수정 설계다. 제품 코드 수정이나 수정 완료 판정은 하지 않았다.
- 후속 문서 이관: 기존 HTML 설계서는 [개발자 문서](../docs/README.md)로 대체했다. 아래 구 설계서에 대한 지적은 검토 당시의 기록이며, 문서 이관만으로 코드·실환경 검증 태스크를 완료 처리하지 않는다.
- 정적 리뷰와 모의 이벤트 검증으로 실제 GPU·Windows 세션·브라우저·NAT 조합의 E2E를 대체할 수 없다. 단일 머신에서 제거 가능한 결함부터 제거하고, 남은 실환경 검증을 별도 게이트로 유지한다.

### 증거와 우선순위

`정적 확인`은 해당 조건에서 코드 경로가 잘못됨을 확인했다는 뜻이다. 실환경에서 실제 장애가 발생했다는 뜻은 아니다. `모의 재현`은 원본 JS를 읽어 가짜 DOM/WebSocket/PeerConnection으로 이벤트 순서를 강제한 결과다. 네이티브 미디어 동작을 검증하지 않는다.

- **P1:** 연결 실패·화면 오류·세션 통제 상실·정보 노출. 후속 기능 통합 전 우선 수정한다.
- **P2:** 특정 환경 기능 오류·진단 오류·검증 신뢰성 결함. 해당 기능 완료 판정 전 수정한다.
- **배포 차단:** 아직 구현되지 않은 동의·인증·암호화 요구사항. PoC의 기존 버그와 혼동하지 않는다.

모든 태스크 상태는 **미착수**, 담당자는 **미지정**이다. 체크는 구현과 해당 회귀 검증이 모두 끝난 뒤 변경한다. 아래 줄 번호는 기준 커밋 기준이다.

## 2. 수정 태스크 목록

| 상태 | ID | 우선순위 | 문제 | 연결 태스크 | 증거 |
|---|---|---|---|---|---|
| [ ] | R01 | P1 | 실제 인코더 초기화 실패 시 HW→SW 폴백 없음 | T1.3b | 정적 확인, 의존성 버전 대조 필요 |
| [ ] | R02 | P1 | 해상도 변경 후 캡처·인코더·버퍼 크기 불일치 | T1.2/T1.3/T1.4 | 정적 확인 |
| [ ] | R03 | P1 | WGC와 DXGI의 같은 인덱스가 다른 모니터를 가리킴 | T1.2/T5.5 | 정적 확인 |
| [ ] | R04 | P2 | DXGI 회전 화면·별도 커서 처리 누락 | T1.2 | 정적 확인, 공식 API 대조 |
| [ ] | R05 | P1 | 동시 참가 시 `joined`보다 다른 알림이 먼저 도착 가능 | T1.1/T1.4 | 정적 경쟁 순서 확인 |
| [ ] | R06 | P1 | offer 이전 ICE 후보 유실 | T1.4/T1.5 | 정적 확인 + 모의 재현 |
| [ ] | R07 | P1 | 시그널링·미디어 오류가 세션 종료로 수렴하지 않음 | T1.4/T1.5/T3.3 | 정적 확인 + Viewer 모의 재현 |
| [ ] | R08 | P1 | 일시 `disconnected` 한 번으로 영구 송신 중단 | T1.4/T4.3 | 정적 확인 |
| [ ] | R09 | P1 | Viewer 이전 연결·비동기 작업이 새 연결을 오염시킴 | T1.5 | 모의 재현 |
| [ ] | R10 | P1 | `--fps 0`이 캡처 스레드의 보호 범위 밖에서 실패 | T1.4 | 정적 확인 |
| [ ] | R11 | P2 | 종료 대기 상한·작업 소유권 보장 없음 | T1.4/T3.3 | 정적 확인 |
| [ ] | R12 | P1 | 룸 조회·참가·삭제가 원자적이지 않음 | T1.1/T3.1 | 정적 경쟁 순서 확인 |
| [ ] | R13 | P2 | `forceKeyframe` 인자 무시 | T1.3/T1.4 | 정적 확인 |
| [ ] | R14 | P2 | WebSocket 분할 수신 시 UTF-8 문자 손상 | T1.4 | 동일 디코딩 방식 재현 |
| [ ] | R15 | P1 | 일반 로그에 세션 코드와 PIN 동시 기록 | T1.4/T3.5 | 정적 확인 |
| [ ] | R16 | P2 | PIN·만료 오류가 일반 연결 종료 문구로 덮임 | T1.5 | 모의 재현 |
| [ ] | R17 | P2 | 인코더 검증 도구가 미검증·실패를 성공으로 표시 | T1.3b | 정적 확인 |
| [ ] | R18 | P2 | 인증 테스트의 무작위 fixture 충돌 | T1.1 | 정적 확인 |

## 3. 버그별 수정 방법과 완료 조건

### R01 — 인코더 초기화 실패 시 폴백하지 않는다

**근거:** [FFmpegH264Encoder.cs](../src/RemoteAssist.Host.Core/Media/FFmpegH264Encoder.cs) 64~84, 90~101, 142~152행. 생성자의 후보 탐색은 `SetCodec` 반환값으로 끝나고 실제 `InitialiseEncoder`는 이후 `Configure`에서 한 번만 호출한다.

**발생 조건·영향:** 앞 후보 선택은 성공했지만 GPU·드라이버·지원 해상도 문제로 초기화가 실패하면 다음 후보를 시도하지 않는다. 라이브러리에 NVENC가 포함된 것과 현재 PC에서 NVENC 세션을 열 수 있는 것은 다르다. NVIDIA 없는 머신/VM에서 SW 폴백이 필요한 경로를 막는다. 실제 초기화 실패 이후 재시도가 없다는 점은 저장소 코드로 확정된다. `SetCodec`이 이름 조회만 수행하는 동작은 [상류 소스](https://raw.githubusercontent.com/sipsorcery-org/SIPSorceryMedia.FFmpeg/master/src/FFmpegVideoEncoder.cs)에서도 확인되지만, 이는 `master`이며 고정 패키지 10.0.16의 소스와 동일하다고 단정하지 않는다.

**구현:** 후보 선택을 `Configure`까지 포함한 트랜잭션으로 옮긴다. 후보마다 별도 인코더를 생성하고 실제 크기·FPS·비트레이트로 초기화 및 제한된 시험 인코딩을 수행한다. 실패한 후보는 완전히 정리하고 다음 후보를 시도한다. 실제 성공 후에만 `EncoderName`을 확정한다. 허용 목록 밖 인코더는 시도하지 않는다. 패키지 초기화 실패 후 같은 인스턴스를 재사용하지 않는다.

**완료 조건:** 첫 HW 후보의 초기화 예외를 주입해 다음 후보의 프레임 출력까지 확인한다. HW 후보 모두 실패→`libopenh264` 성공, 모든 후보 실패→명시적 세션 실패를 확인한다. 고정 DLL/패키지에서 이 경로를 검증한 뒤 GPU가 다른 머신과 VM 검증을 추가한다.

### R02 — 해상도 변경이 인코딩 파이프라인에 전달되지 않는다

**근거:** [WgcScreenCapture.cs](../src/RemoteAssist.Host.Core/Capture/WgcScreenCapture.cs) 152~165행은 버퍼와 풀만 변경하고 `Monitor`는 갱신하지 않는다. [ScreenShareSession.cs](../src/RemoteAssist.Host.Core/ScreenShareSession.cs) 259, 271~280행은 최초 한 번 설정 후 `frame.Bgra`만 보관한다. [FFmpegH264Encoder.cs](../src/RemoteAssist.Host.Core/Media/FFmpegH264Encoder.cs) 158~174행은 고정 `_width/_height`로 배열을 전달하며 길이를 검사하지 않는다. DXGI도 [DxgiScreenCapture.cs](../src/RemoteAssist.Host.Core/Capture/DxgiScreenCapture.cs) 171~175, 197~215행에서 AccessLost 후 복제 객체만 재생성한다.

**발생 조건·영향:** 공유 중 해상도 변경 후 새 크기의 데이터를 옛 크기로 인코딩한다. 작아진 배열을 큰 영상으로 전달하는 경우 네이티브 경계 밖 읽기 위험이 있다. 네이티브 크래시·정보 노출까지 실제 재현한 것은 아니다. DXGI는 기존 크기의 staging에 새 크기의 texture를 복사하게 된다.

**구현:** 크기·stride·모니터 식별자를 포함한 프레임 포맷 변경 이벤트를 정의한다. 변경 시 이전 프레임 캐시를 버리고 캡처 자원과 인코더를 같은 포맷으로 재생성한 뒤 새 키프레임부터 송신한다. 0 크기·오버플로·BGRA 길이·stride를 네이티브 호출 전에 검사한다. DXGI는 출력 설명과 texture description을 다시 조회한다.

**완료 조건:** 1920×1080→1280×720→1920×1080, stride 패딩, 잘린 버퍼를 검증한다. 가짜 프레임 공급자로 작은 버퍼가 네이티브 인코더에 도달하지 않음을 먼저 검증하고 실제 디스플레이 변경으로 화면과 좌표 일치를 확인한다.

### R03 — 캡처 백엔드마다 모니터 번호의 의미가 다르다

**근거:** [Monitors.cs](../src/RemoteAssist.Host.Core/Capture/Monitors.cs) 40~44행은 주 모니터 우선 정렬한다. WGC는 `Monitors.Get`을 사용하지만 [DxgiScreenCapture.cs](../src/RemoteAssist.Host.Core/Capture/DxgiScreenCapture.cs) 97~108행은 어댑터/출력 열거 순번을 사용한다. HostRun과 CaptureDump의 사용자 안내 목록은 `Monitors.Enumerate`다.

**발생 조건·영향:** 두 열거 순서가 다른 다중 모니터 머신에서 `--monitor 0` 또는 같은 설정을 사용해도 다른 화면이 공유된다. 사용자 선택과 다른 모니터 노출이며, 향후 입력 좌표가 다른 화면으로 향할 위험도 있다.

**구현:** 공통 목록에서 선택한 HMONITOR/장치 식별자를 캡처 팩토리에 넘기고 DXGI 출력의 `Description.Monitor`와 일치시킨다. 핫플러그로 대상이 사라지면 다른 화면으로 자동 대체하지 말고 중단·재선택한다.

**완료 조건:** 공통 목록 `[A,B]`, DXGI 열거 `[B,A]`인 모의 구성에서 두 백엔드가 같은 대상을 선택한다. 실제 2개 모니터에서 주 모니터 변경·음수 좌표 배치를 확인한다.

### R04 — DXGI가 회전과 별도 포인터를 처리하지 않는다

**근거:** [DxgiScreenCapture.cs](../src/RemoteAssist.Host.Core/Capture/DxgiScreenCapture.cs) 149~162, 211~235행. 데스크톱 논리 폭·높이로 staging을 만들고 texture를 그대로 복사하며 회전·포인터 모양 합성이 없다.

**발생 조건·영향:** 90/270도 화면은 복제 surface와 데스크톱 크기의 축이 다를 수 있어 복사가 잘못되고, 180도 화면은 방향이 틀어진다. 커서가 별도 하드웨어 plane인 환경에서는 화면에 커서가 나타나지 않는다. 해당 처리는 [Microsoft Desktop Duplication 문서](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/desktop-dup-api)가 요구한다.

**구현:** 원본 texture 크기로 staging을 만들고 DXGI 회전 값에 따라 BGRA를 정방향으로 변환한다. 별도 포인터 표시 여부·모양·위치를 처리하되 이미 프레임에 포함된 커서는 중복 합성하지 않는다.

**완료 조건:** 0/90/180/270도 기준 이미지 비교, 모서리 좌표 일치, 별도 커서 visible/hidden 및 모양 변경을 검증한다. WGC 성공만으로 DXGI 완료를 판정하지 않는다.

### R05 — 참가 ACK보다 `peer-joined` 또는 릴레이가 먼저 갈 수 있다

**근거:** [server.go](../server/signaling/internal/signaling/server.go) 219~235행에서 `rm.Join`으로 피어를 공개한 뒤 자신의 `joined`를 보낸다. 다른 핸들러는 공개된 피어에 곧바로 쓸 수 있다. [SignalingClient.cs](../src/RemoteAssist.Host.Core/Signaling/SignalingClient.cs) 70~79행은 첫 수신이 `joined`가 아니면 참가 실패로 처리한다.

**경쟁 순서:** Host가 슬롯에 등록된 직후 ACK 전송 전에 멈춘다. Viewer가 등록되고 자신의 ACK를 받은 다음 Host에 `peer-joined`를 보낸다. Host가 이 알림을 첫 메시지로 받으면 정상 인증에도 연결이 실패한다. 소켓 쓰기 락은 바이트 전송을 직렬화할 뿐 프로토콜 순서를 보장하지 않는다.

**구현:** 룸별 이벤트 처리를 직렬화하거나 슬롯에 `joining/ready` 상태를 둔다. ACK를 해당 연결의 송신 큐에 먼저 넣고 이후 알림·릴레이를 허용한다. ACK 실패 시 예약 슬롯을 롤백한다. 네트워크 I/O 동안 전역 룸 락을 보유하지 않는다. Host의 협상 생성도 하나의 상태 전이 경로로 직렬화한다.

**완료 조건:** barrier로 Host의 슬롯 예약과 ACK 사이를 멈춘 뒤 Viewer를 참가시켜도 각 연결의 첫 메시지는 `joined`다. 동시 참가 시 offer와 PeerConnection은 정확히 한 번 생성된다. 단순 반복 횟수에 기대지 않는 결정적 테스트가 필요하다.

### R06 — offer보다 먼저 도착한 ICE 후보가 사라진다

**근거:** [ScreenShareSession.cs](../src/RemoteAssist.Host.Core/ScreenShareSession.cs) 147~155, 173~187행은 로컬 SDP 설정 중 발생한 ICE를 즉시 비동기 송신한다. offer 선송신을 보장하지 않는다. [app.js](../web/viewer/app.js) 90~98행은 `pc`가 없으면 후보를 버린다.

**발생 조건·영향:** `ice → offer` 순서에서는 유효한 후보가 적용되지 않는다. 후보 하나가 유일한 연결 경로인 환경에서 연결에 실패할 수 있다. 원본 JS 모의 재현에서 후보를 먼저 주고 offer를 처리해도 `addIceCandidate` 호출은 0회였다. 실제 SIPSorcery에서 이 순서의 발생 빈도는 측정하지 않았다.

**구현:** Host는 offer를 송신 큐에 넣기 전 로컬 ICE를 보관한다. Viewer도 해당 세션의 원격 SDP 준비 전 ICE를 제한된 큐에 보관하고 적용 후 순서대로 비운다. 종료·새 협상 시 이전 큐를 폐기한다. 후보 개수·크기·협상 세대 검증을 추가한다.

**완료 조건:** `ice→offer`, 여러 후보→offer, 종료 후 늦은 ICE, 재협상 세대 변경을 검증한다. **`setRemoteDescription` 호출 후 pending 동안 `addIceCandidate`를 호출하는 것 자체는 버그로 단정하지 않는다.** 브라우저의 operation chain을 생략한 mock은 거짓 실패를 만들 수 있다. [W3C WebRTC 규약](https://www.w3.org/TR/webrtc/#dom-rtcpeerconnection-addicecandidate)을 기준으로 테스트한다.

### R07 — 치명적 오류와 시그널링 종료가 세션을 정리하지 않는다

**근거:** [ScreenShareSession.cs](../src/RemoteAssist.Host.Core/ScreenShareSession.cs) 93, 127~134행은 수신 EOF/예외 시 루프만 종료한다. 296~305행의 캡처 오류도 로그·자원 해제만 한다. [app.js](../web/viewer/app.js) 68~70행은 PeerConnection이 존재하면 WebSocket 종료에도 아무 상태 변경을 하지 않는다. HostRun은 세션 실패와 무관하게 Ctrl+C만 기다린다.

**발생 조건·영향:** 시그널링 서버만 끊어지고 P2P는 살아 있으면 Host의 화면 송신이 계속될 수 있다. 반대로 캡처·인코더 초기화가 실패하면 PeerConnection은 연결된 채 화면만 나오지 않는다. 상대 종료 통지와 장애 상태가 실제 미디어 수명과 어긋난다.

**구현:** 단일 `EndSessionAsync(reason)`과 완료/실패 결과를 노출한다. EOF·치명적 프로토콜 오류·미디어 오류·로컬 종료를 이 경로로 모은다. 현 단계에는 시그널링 상실 시 송신을 중단하는 정책을 명문화한다. 향후 P2P 지속 정책을 도입하려면 별도 인증된 종료 경로·동의 상태 유지·사용자 표시부터 설계한다. 지원되는 API라는 이유만으로 WGC 초기화 성공을 가정하지 말고, 안전한 실패에 한해 동일 모니터 DXGI 폴백 또는 명시적 종료를 수행한다.

**완료 조건:** 수신 EOF, JSON 오류, 캡처 생성 예외, 인코더 초기화 예외를 각각 주입한다. 모든 경우 송신/연결/호출자 결과가 일관되게 종료되고, Viewer가 무기한 연결 화면에 남지 않는다.

### R08 — 일시 단절을 처리하면서 세션 전체 취소 토큰을 영구 취소한다

**근거:** [ScreenShareSession.cs](../src/RemoteAssist.Host.Core/ScreenShareSession.cs) 190~202, 225~240행. `disconnected`도 `StopStreaming()`을 호출하고 이는 `_cts.Cancel()`이다. 이후 `connected`가 와도 `_captureThread`는 null로 되돌아가지 않는다.

**발생 조건·영향:** 네트워크 일시 단절→복구 시 기존 PC가 다시 연결되어도 캡처가 재시작하지 않는다. 시그널링 루프도 취소되어 후속 제어를 처리하지 못한다. 자동 재연결 미구현과 별개로 현재 상태 처리 자체가 되돌릴 수 없다.

**구현:** 세션 수명 토큰과 스트리밍 작업 토큰을 분리하고 상태 전이를 직렬화한다. `disconnected`는 제한 시간의 복구 대기 또는 명시적 완전 종료 중 하나로 처리한다. 복구를 지원한다면 캡처 작업 종료를 기다린 뒤 새 작업·토큰으로 재개한다. 이미 종료된 세션의 callback은 generation 검사로 무시한다.

**완료 조건:** `connected→disconnected→connected`, timeout→failed, 로컬 종료와 복구 이벤트 동시 발생을 검증한다. 재개 정책이면 중복 스레드 없이 복구되고, 종료 정책이면 양쪽에 종료가 표시된다.

### R09 — Viewer의 전역 `ws/pc`가 이전 이벤트와 섞인다

**근거:** [app.js](../web/viewer/app.js) 56~73, 125~160, 199~204행. 연속 접속을 막지 않고 새 연결로 전역 변수를 교체한다. 기존 callback과 `await` 후속 코드도 같은 전역 변수를 참조한다. `teardown`은 OPEN 소켓만 닫는다.

**모의 재현:** 접속을 두 번 누른 뒤 첫 소켓의 `onopen`을 실행하면 두 번째 소켓에 `send`하여 `socket not OPEN`이 발생했다. offer 처리의 첫 await 직후 teardown을 실행하면 `Cannot read properties of null (reading 'createAnswer')`가 발생했다. 이전 소켓의 `peer-left`도 새 세션을 종료시킬 수 있는 구조다.

**구현:** 연결 시도별 객체에 소켓·PC·타이머·generation을 소유시킨다. callback은 해당 인스턴스를 캡처하고 매 await 뒤 현재 세션 여부를 검사한다. 재접속은 이전 시도를 취소·정리한 뒤 수행한다. CONNECTING 소켓도 종료 대상에 넣고 중복 offer 정책을 정한다. JSON 파싱·협상·통계 수집 Promise의 예외를 처리한다.

**완료 조건:** 연속 submit, CONNECTING 중 취소, SDP 대기 중 종료, 이전 연결의 늦은 close/message/track 이벤트를 결정적 mock으로 검증한다. 새 세션이 보존되고 unhandled rejection과 남은 연결이 없어야 한다.

### R10 — 잘못된 FPS가 캡처 스레드에서 처리되지 않은 예외를 만든다

**근거:** [HostRun/Program.cs](../tools/RemoteAssist.HostRun/Program.cs) 30행에서 0도 정수로 허용한다. [ScreenShareSession.cs](../src/RemoteAssist.Host.Core/ScreenShareSession.cs) 248~252행은 `TimeSpan.FromMilliseconds(1000.0 / TargetFps)`와 정수 나눗셈을 `try` 밖에서 수행한다.

**발생 조건·영향:** `--fps 0`으로 시작하고 Viewer가 연결되면 캡처 스레드 시작 직후 실패한다. 이는 정상적인 세션 오류 반환 경로 밖이며 프로세스 종료로 이어질 수 있다. 음수·과대 FPS와 비트레이트·모니터 값도 공개 API 경계에서 검증하지 않는다.

**구현:** `ScreenShareOptions`를 네트워크 연결·스레드 생성 전에 검증한다. 지원 FPS·비트레이트 범위와 모니터 존재 여부를 명시한다. CLI의 잘못된 입력을 기본값으로 조용히 바꾸지 말고 오류와 비정상 종료 코드로 반환한다. 스레드 진입점 전체에 예외 경계를 둔다.

**완료 조건:** 0/음수/과대/비숫자 설정이 캡처 스레드를 만들기 전에 거절된다. 정상 경계값은 통과한다. 오류에 의해 호스트 프로세스가 예기치 않게 죽지 않는다.

### R11 — 종료 경로에 전체 시간 상한이 없다

**근거:** [SignalingClient.cs](../src/RemoteAssist.Host.Core/Signaling/SignalingClient.cs) 129~145행은 `CloseAsync(..., CancellationToken.None)`을 기다린다. [ScreenShareSession.cs](../src/RemoteAssist.Host.Core/ScreenShareSession.cs) 309~326행은 캡처 Join의 성공 여부를 무시한 뒤 PC와 CTS를 정리한다. ICE 송신 Task도 추적하지 않는다.

**발생 조건·영향:** close 응답을 보내지 않는 서버와 연결된 상태에서 정리하면 호출자의 종료가 지연될 수 있다. 캡처가 3초 안에 끝나지 않으면 살아 있는 작업과 공유 자원 폐기가 겹친다. 3초씩 기다리는 코드가 있다고 전체 종료가 3초로 제한되지는 않는다. [ClientWebSocket.CloseAsync 계약](https://learn.microsoft.com/en-us/dotnet/api/system.net.websockets.clientwebsocket.closeasync?view=net-10.0)도 완료 시점을 연결 종료로 정의한다.

**구현:** 미디어 송신 허용을 먼저 차단하고 전체 정리 deadline을 적용한다. 소켓 graceful close는 짧은 토큰으로 제한한 뒤 Abort/Dispose한다. 캡처·송신 작업의 소유권과 완료를 추적하고 종료를 멱등적으로 만든다. 네이티브 호출의 무한 정지는 관리 스레드 강제 중단으로 해결하지 말고 헬퍼 프로세스 격리와 감독 종료 대상으로 설계한다.

**완료 조건:** close 미응답 서버, 지연된 인코더, 진행 중 ICE 송신, 중복 Dispose를 검증한다. T3.3의 1초 송신 차단 목표와 백그라운드 자원 회수 시간을 구분해 기록한다.

### R12 — 룸 참가와 삭제의 원자성이 깨진다

**근거:** [room.go](../server/signaling/internal/room/room.go) 208~227행의 `Get`/`Remove`와 `Room.Join`은 서로 다른 연산이다. [server.go](../server/signaling/internal/signaling/server.go) 273~283행의 `Leave→Empty→Remove`도 분리되어 있다.

**경쟁 순서·영향:** 마지막 피어 이탈 후 `Empty()==true` 판정과 `Remove` 사이에 새 피어가 참가하면 비어 있지 않은 룸을 삭제한다. 먼저 `Get`한 포인터가 나중에 삭제된 룸으로 참가하는 경로도 있다. 새 피어는 참가 성공을 받지만 이후 상대는 해당 코드를 찾지 못한다. 또한 `Get`은 만료 시 활성 피어 유무와 관계없이 삭제하지만 `Sweep`은 활성 룸을 보존하여 정책이 다르다. 이때 기존 연결이 즉시 끊기는 것은 아니며 맵 밖에서 살아 있게 된다.

**구현:** 관리자 API에서 조회·만료 판정·인증·슬롯 예약을 원자적으로 수행한다. 삭제는 같은 룸 인스턴스인지 확인하고 비어 있음/종료 상태를 같은 임계구역에서 판정한다. 잠금 순서는 manager→room으로 통일하고 네트워크 쓰기는 락 밖에서 처리한다. 참가 코드 만료와 활성 세션 수명을 별도 상태로 분리한다.

**완료 조건:** barrier 기반 `join vs 마지막 leave`, `join vs sweep`, 활성 룸 만료 후 제3자 조회를 검증한다. 맵에 없는 룸에 신규 참가 성공이 발생하지 않아야 한다. `go test -race` 통과에 더해 이 논리 경쟁 테스트가 필요하다.

### R13 — 키프레임 강제 요청을 버린다

**근거:** [IVideoEncoder.cs](../src/RemoteAssist.Host.Core/Media/IVideoEncoder.cs)의 `forceKeyframe` 계약과 달리 [FFmpegH264Encoder.cs](../src/RemoteAssist.Host.Core/Media/FFmpegH264Encoder.cs) 158~176행은 인자를 읽지 않는다. [ScreenShareSession.cs](../src/RemoteAssist.Host.Core/ScreenShareSession.cs) 280행도 항상 false를 전달하고 RTCP 키프레임 요청 연결이 없다.

**영향:** 호출자가 true를 전달해도 강제 키프레임을 보장하지 않는다. 손실 복구·재개·포맷 변경 시 디코더 복구가 주기적 키프레임에 의존한다. GOP 길이나 실제 복구 지연을 이번 검토에서 측정하지 않았다.

**구현:** 고정 패키지 API를 확인해 true 요청을 실제 키프레임 요청으로 전달한다. RTCP PLI/FIR은 encoder 소유 스레드로 전달하고 중복 요청을 합친다. 다른 스레드에서 직접 인코더를 호출하지 않는다.

**완료 조건:** 정상 P-frame 뒤 강제 요청을 넣고 출력 비트스트림에서 복구 가능한 키프레임 및 필요한 파라미터 세트를 확인한다. 이후 실제 패킷 손실→복구 검증을 수행한다.

### R14 — 분할된 UTF-8을 조각마다 디코딩한다

**근거:** [SignalingClient.cs](../src/RemoteAssist.Host.Core/Signaling/SignalingClient.cs) 99~126행. Receive 호출별 `Encoding.UTF8.GetString` 후 문자열을 합친다.

**재현·영향:** `가`의 UTF-8 3바이트를 1+2바이트로 나눠 같은 방식으로 디코딩한 결과 원문과 일치하지 않았다. WebSocket 메시지/수신 버퍼 경계는 문자 경계가 아니다. 한글 reason이나 향후 비ASCII 필드가 손상될 수 있다.

**구현:** 전체 메시지 바이트를 제한된 버퍼에 모은 뒤 한 번 역직렬화하거나 상태 유지 Decoder를 사용한다. Text 타입만 받고 총 메시지 상한을 서버의 128KiB 정책과 맞춘다. 무제한 문자열 누적을 제거한다.

**완료 조건:** 한글·4바이트 문자를 모든 바이트 경계에서 분할해도 동일하게 복원된다. 상한 초과·Binary·잘못된 JSON은 명시적으로 종료하며 메모리가 무한 증가하지 않는다.

### R15 — PIN이 일반 로그에 남는다

**근거:** [ScreenShareSession.cs](../src/RemoteAssist.Host.Core/ScreenShareSession.cs) 68행의 `LogInformation("세션 코드 {Code} / PIN {Pin}", ...)`.

**영향:** 코드와 PIN이 같은 로그 레코드에 남는다. 향후 서비스 로깅·진단 로그 수집에 이 코드를 재사용하면 로그 접근만으로 세션 참가 정보가 노출된다. 사용자가 전달하도록 HostRun 콘솔 UI에 표시하는 것과 범용 로그에 저장하는 것은 구분해야 한다.

**구현:** 일반 로그에서 PIN·hostToken·TURN 비밀정보를 제거한다. 세션 추적은 비밀정보가 아닌 별도 식별자를 사용한다. UI 표시와 감사 로그의 책임을 나눈다.

**완료 조건:** 로그 수집기를 붙여 세션 생성·인증 실패·예외 경로를 실행하고 발급된 PIN/token 값이 로그 이벤트에 없는지 검증한다.

### R16 — 구체적인 접속 실패 이유가 지워진다

**근거:** [app.js](../web/viewer/app.js) 105~108행은 오류를 표시한 뒤 close한다. 68~70행은 `pc`가 없으면 일반 종료 문구를 다시 표시한다.

**모의 재현:** `bad-pin` 처리 후 close 이벤트를 실행하면 `PIN이 올바르지 않습니다.`가 `시그널링 연결이 끊어졌습니다.`로 바뀌었다. 사용자가 PIN·만료 문제를 네트워크 문제로 오인한다.

**구현:** 연결 시도에 종료 원인을 보관하고 이미 확정된 원인을 onclose가 덮지 않게 한다. R09의 이전 연결 이벤트 차단과 함께 적용한다.

**완료 조건:** bad-pin/expired/slot-taken, 사용자 종료, 순수 네트워크 종료 후 최종 안내가 각 원인에 맞게 유지된다.

### R17 — 인코더 검사 도구가 성공을 과대 판정한다

**근거:** [HostRun/Program.cs](../tools/RemoteAssist.HostRun/Program.cs) 116~132행의 인코더별 검사는 생성과 이름 비교만 한다. 실제 Configure/Encode는 자동 선택 후보 하나에만 수행한다. 172~184행은 FPS 미달을 경고만 하고 성공 코드 0을 반환하며, 출력 프레임 0개도 별도 실패 판정하지 않는다.

**영향:** “5종 모두 사용 가능”은 5종 모두 실제 인코딩 성공이라는 증거가 아니다. 출력 없는 빠른 함수 호출도 처리량 성공으로 보일 수 있다. 기존 T1.3b 완료 근거의 범위를 좁혀야 한다.

**구현:** 후보별 등록 여부/초기화/출력/디코딩 성공을 구분한다. 강제 검사에서는 다른 인코더로 폴백한 결과를 해당 후보의 성공으로 세지 않는다. 출력 0, 초기화 실패, DoD 미달은 명시적 비정상 종료 코드로 반환한다. 인코딩 처리량은 입력 반복 횟수와 실제 출력 프레임 수를 구분한다.

**완료 조건:** 출력 0인 가짜 인코더와 성능 미달 결과가 반드시 검사 실패가 된다. GPU별 실제 출력 검증 후에만 스택 문서의 사용 가능 기록을 갱신한다. CaptureDump의 일부 프레임만 저장하고 성공하는 정책도 요청 수 충족 여부와 구분한다.

### R18 — 잘못된 PIN 테스트가 가끔 올바른 PIN을 사용한다

**근거:** [server_test.go](../server/signaling/internal/signaling/server_test.go) 234~235행. 틀린 PIN을 `0000`, 없는 코드를 `000000000`으로 고정하지만 실제 생성기는 이 값을 생성할 수 있다.

**영향:** 정상 코드에서도 PIN이 우연히 같으면 인증 실패 테스트가 실패한다. 전부 0인 코드가 발급되면 없는 코드 테스트도 실패한다. 재시도로 가리면 실제 회귀와 구분하기 어렵다.

**구현:** 실제 발급 값의 첫 숫자를 변경해 반드시 다른 PIN/코드를 만든다. 코드의 경우 테스트 서버에 다른 룸이 없는 전제를 명시하거나 존재하지 않는 값을 조회해 정한다. 랜덤 생성기/시계는 테스트에서 주입 가능하게 한다.

**완료 조건:** 실제 PIN과 코드를 전부 0으로 고정한 fixture에서도 오류 시나리오가 결정적으로 통과한다. 무작위 반복 실행만으로 확인하지 않는다.

## 4. 미구현·설계·검증 리스크

### G01 — 동의·즉시 종료·인증·TLS는 배포 차단 조건이다

- **연결:** T3.1~T3.5. **상태:** 미구현, 배포 차단.
- **근거:** `ScreenShareSession` 주석과 `connected→StartStreaming` 직접 호출, 빈 Tray, `room.Authorize`의 실패 제한 TODO, `main.go`의 기본 `:25001` HTTP 리슨. 코드 일회성 소비 상태가 없어 슬롯이 비면 같은 PIN으로 재참가 가능하다. 이는 이미 계획된 미완성 기능이며 Phase 1 구현 버그 수에 합산하지 않는다.
- **대응:** 화면/입력/관리자 각각 Host 측 동의 게이트, 철회 우선 처리, 코드 소비·실패 제한·발급 및 연결 수 제한, 비밀정보 없는 감사 로그, HTTPS/WSS 배포 경로를 구현한다. 기본 개발 모드는 loopback으로 제한하고 LAN PoC는 참여자 동의와 명시적 개발 설정을 사용한다. 운영 모드는 안전 조건이 없으면 시작을 거부한다.
- **완료 조건:** 동의 전 송신 0, 거부/철회 뒤 재사용·늦은 callback 차단, 실패 횟수 제한, 재사용 코드 거절, 대상자 종료 후 1초 이내 송신 차단, 실제 TLS 검증이 필요하다. UI의 `E2E 암호화` 표시는 시그널링 인증·TLS 완료 증거가 아니다.

### G02 — 입력 채널 신뢰성과 좌표 공식이 구현 전에 수정되어야 한다

- **연결:** T2.1~T2.3. **상태:** 프로토콜 설계 결함, 입력 구현 전 차단.
- **근거:** [protocol.md](../docs/protocol.md) 101행은 key/button up까지 unreliable 채널에 묶는다. 115~116행의 좌표 예시는 영상 여백과 가상 데스크톱 음수 원점을 처리하지 않는다. 입력 구현은 아직 없으므로 실행 버그가 아니다.
- **대응:** move는 최신 값 우선, key/button 전이는 신뢰성 보장 또는 상태 snapshot/ACK 복구로 분리한다. 채널 간 이벤트 순서와 button 이벤트의 최종 좌표 적용도 정의한다. blur·종료·권한 철회 시 누른 키/버튼을 해제한다. Viewer는 실제 영상 표시 영역을 기준으로 좌표를 계산하고 여백 입력을 제외한다. Host는 `(pixelX - virtualLeft)` 및 `(pixelY - virtualTop)`로 가상 화면 원점을 보정하고 정규화 범위를 제한한다. `MOUSEEVENTF_VIRTUALDESK` 정책과 양끝 픽셀 매핑을 정의한다.
- **완료 조건:** key-up/button-up 손실·중복·지연, 음수 X/Y 모니터, DPI 차이, letterbox, 영상 모서리를 검증한다. `docs/protocol.md → C#/JS 구현` 순서를 지킨다.

### G03 — SYSTEM 서비스의 사용자 세션 접근 설계를 먼저 확정해야 한다

- **연결:** T2.4/T5.1/T5.2. **상태:** 아키텍처 선행 리스크.
- **근거:** 삭제 전 구 HTML 설계서의 `WTSGetActiveConsoleSessionId + OpenInputDesktop/SetThreadDesktop` 설명만으로는 프로세스의 Windows 세션이 바뀌지 않는다. [SetThreadDesktop](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setthreaddesktop)은 현재 프로세스 window station에 속한 desktop을 요구하고, 각 사용자 세션에는 별도의 [WinSta0](https://learn.microsoft.com/en-us/windows/win32/winstation/window-stations)가 있다. 후속 [아키텍처 문서](../docs/architecture.md)는 사용자 세션 실행 경계를 명시한다. 실제 헬퍼·IPC·권한 구조 확정과 검증은 아직 남아 있다.
- **대응:** 서비스는 세션·권한 감독을 맡고 캡처/입력 실행 위치를 대상 사용자 세션의 헬퍼로 명시한다. 일반 제어는 일반 권한 경로를 사용하고 관리자 동의 후에만 필요한 특권 헬퍼 경로를 연다. SYSTEM 헬퍼를 상시 사용하며 관리자 UI에 무조건 입력할 수 있게 만드는 구조를 피한다. IPC ACL·상대 프로세스/세션 신원·재연결 인증·권한 변경·로그오프/사용자 전환 종료를 설계한다. UAC/잠금화면은 별도 최소 예제로 검증한다.
- **완료 조건:** 일반 앱/승격 앱/UAC/잠금화면과 콘솔/RDP/사용자 전환을 나눠 검증한다. 잘못된 세션 및 관리자 동의 없는 승격 입력은 차단되어야 한다. 설계서와 계획서를 먼저 일치시킨 뒤 구현한다.

### G04 — TURN 및 미디어 상호운용성은 아직 검증되지 않았다

- **연결:** T1.3b/T4.1~T4.3/T6.3. **상태:** 실환경 검증 대기.
- **근거:** Host/Viewer ICE 설정은 STUN만 포함한다. coturn compose만으로 앱의 TURN 폴백이 구현되는 것은 아니다. Viewer 경로 표시는 local candidate만 검사하므로 향후 Host 쪽만 relay인 경우 오표시할 수 있다. H.264 SDP는 `packetization-mode=1`만 지정하며 실제 SPS와 profile/level 정합성은 확인하지 않았다. RTP duration은 실제 송신 간격과 무관하게 `90000 / TargetFps`로 고정한다.
- **대응:** 세션별 TURN 자격증명과 양쪽 ICE 설정, relay 강제 검증, 선택된 candidate pair의 양끝 타입 판정을 구현한다. 실제 고정 라이브러리의 RTP timestamp 사용 방식과 캡처 지연/프레임 생략 영향을 대조한다. 인코더별 SPS·SDP·브라우저 지원 조합을 확인한다. profile 불일치나 누적 지연이 이미 발생했다고 단정하지 않는다.
- **완료 조건:** 서로 다른 네트워크, 직접 UDP 차단, relay 강제, 일시 단절/복구, Chrome/Edge 및 지원 대상 브라우저, HW/SW 인코더별 검증 결과를 남긴다. coturn 개발 자격증명 교체와 TCP/TLS 폴백 포트도 프로젝트 허용 대역에 맞춰 명시한다.

### G05 — 네이티브 자원 및 장애 감시의 실측이 필요하다

- **연결:** T1.2/T1.4/T6.4. **상태:** 정적 위험, 누수량·복구 시간 미측정.
- **근거:** WGC/DXGI 생성 도중 예외의 명시적 롤백이 없다. WGC resize의 `QueryInterface<IDXGIDevice>()` 임시 래퍼는 Dispose되지 않는다. `WgcInterop.EnsureComInitialized`는 성공한 `CoInitializeEx` 호출과 대응하는 해제를 명시하지 않는다. WGC의 캡처 대상 종료와 단순 정적 화면을 구분하는 상태도 없다. Go는 참가 이후 heartbeat/lease가 없고 HTTP shutdown과 열린 WebSocket 종료를 연결하는 별도 관리가 없다.
- **대응:** 생성 중 자원 소유권과 실패 롤백, COM 초기화/해제 규칙, WGC 대상 종료 이벤트, 무응답 소켓 heartbeat·세션 lease·서버 shutdown 연결 정리를 설계한다. 캡처 중단 상태에서 마지막 화면을 새 화면처럼 계속 재전송하지 않도록 상태를 분리한다.
- **완료 조건:** 반복 시작/실패/resize/종료 후 native handle·GPU 메모리·goroutine 수를 실측한다. FIN/RST 없는 네트워크 blackhole과 서버 종료에서 제한 시간 안에 상태가 정리되는지 확인한다. GC가 있으므로 누수가 없다고 판정하거나, 반대로 측정 없이 영구 누수량을 주장하지 않는다.

### G06 — 완료 표시와 검증 환경 기록이 실제 증거보다 강하다

- **연결:** T0.1/T0.2/T1.3b/T1.4/T6.4. **상태:** 검증·문서 정합성 태스크.
- **근거:** T1.3b 본문 DoD는 서로 다른 GPU 2대 이상과 VM을 요구하고 T1.4는 LAN의 두 머신을 요구한다. 체크리스트는 완료지만 스택 문서는 루프백만 수행했다고 기록한다. FFmpeg 배치도 체크리스트 상단 미완료/Phase 1 완료가 동시에 남아 있다. 이 작업 트리의 `third_party/ffmpeg`에는 README만 있다. 현재 확인된 SDK 10.0.301, 로컬 Go 1.24.1, Node 23.10.0은 과거 문서의 도구 버전과 다르다. 과거 다른 환경에서의 측정 자체를 무효라고 단정하지 않는다.
- **대응:** 루프백 PoC 성공과 전체 DoD 통과를 별도 기록한다. FFmpeg 확보 경로·해시·ABI·허용 빌드 검증 절차, SDK/toolchain 준비, 자동 테스트 실행법을 재현 가능하게 만든다. 설계서/가이드의 남은 VP8·Media Foundation 우선 문구도 확정된 FFmpeg H.264 선택에 맞게 정리한다.
- **완료 조건:** 깨끗한 checkout에서 의존성 준비→빌드→단일 머신 자동 검증을 재현한다. 고정 패키지 버전의 소스/API를 확인한다. 이후 2대 머신·GPU·VM DoD 증거를 붙인 뒤 기존 완료 상태를 재판정한다.

## 5. 이번 검증 결과

| 확인 | 결과 | 한계 |
|---|---|---|
| `node --check web/viewer/app.js` | 통과 | 문법 검사이며 동작 보증 아님 |
| 원본 Viewer JS + Node VM 모의 이벤트 | R06/R09/R16 및 WebSocket 종료 시 PC 잔존 확인 | 브라우저/WebRTC 구현을 대체하지 않음 |
| .NET UTF-8 분할 디코딩 | `가`를 1+2바이트로 나누면 원문 복원 실패 | 동일 수신 디코딩 방식만 검증 |
| `dotnet build RemoteAssist.slnx --no-restore` | 실행 실패: project.assets.json 없음 | 컴파일 성공/실패를 판정할 수 없음 |
| workspace 내부 NuGet 경로로 restore | 실행 실패: `NU1301`, api.nuget.org 소켓 접근 제한 | 의존성 복원 후 빌드 재실행 필요 |
| `go test ./...`, `go vet ./...` | 도구 체인 준비 단계 실패 | 처음에는 외부 캐시 쓰기 거절 |
| workspace 내부 Go 캐시로 `go test ./...` 재시도 | Go 1.27 다운로드 네트워크 제한 | 로컬 Go 1.24.1로 go.mod 요구 버전 충족 불가 |
| 두 머신 E2E / 네이티브 캡처·인코딩 / Go race | 미실행 | 완료 근거로 사용할 수 없음 |

모의 이벤트에서 얻은 출력은 다음과 같다. `socket not OPEN`은 fake WebSocket의 예외 문구다.

```text
repeated submit: socket not OPEN
error overwritten: PIN이 올바르지 않습니다. => 시그널링 연결이 끊어졌습니다.
teardown during offer: Cannot read properties of null (reading 'createAnswer')
signaling close retains peer: true
ICE before offer: addIceCandidate calls = 0
fragmented UTF8 roundtrip matches: False
```

참고: 초기 mock에서 관찰한 “setRemoteDescription pending 중 ICE 추가 실패”는 브라우저 operation chain을 재현하지 않아 확정 결함에서 제외했다. R06은 **offer 이전 후보 유실**만을 대상으로 한다.

## 6. 실행 순서 및 리뷰 종료 기준

1. **검증 기반 정비:** R17/R18/G06. 실패를 성공으로 표시하는 검증 도구와 무작위 fixture를 먼저 수정한다.
2. **연결 상태와 종료 통합:** R05~R09/R11/R12/R16. 시그널링·PeerConnection·미디어 작업에 공통 상태/소유권을 정의하고 결정적 경쟁 테스트를 만든다.
3. **미디어 안정화:** R01~R04/R10/R13. 네이티브 진입 전 포맷/설정을 검증하고 실제 초기화·복구·키프레임 동작을 확인한다.
4. **수신·로그 경계 정비:** R14/R15. 프로토콜·보안 테스트에 포함한다.
5. **후속 기능 선행 조건:** G02/G03을 설계 문서에 먼저 반영한다. 배포 전에는 G01을 반드시 해소한다.
6. **실환경 검증:** G04/G05 및 원래 T1.3b/T1.4 DoD를 실제 머신으로 수행한다.

단일 머신 자동 검증에 우선 추가할 것은 룸 동시 참가/삭제, Host의 오류·상태 전이, Viewer의 오래된 이벤트, 프레임 포맷 변경, 네이티브 호출 전 크기 검증이다. 무작위 sleep 대신 barrier·수동 완료 Promise·가짜 시계로 실패 순서를 고정한다. Go는 race detector와 논리 경쟁 테스트를 함께 실행한다.

실환경 기록에는 commit, OS, GPU/드라이버, 브라우저, FFmpeg/인코더, 모니터 배치, 선택 ICE 경로, 재현 절차, 결과를 남긴다. 모든 항목은 **코드 수정 + 관련 자동 검증 + 필요한 실환경 증거**가 있을 때만 완료로 바꾼다. 리뷰 문서 작성 자체는 버그 해결이나 E2E 통과가 아니다.
