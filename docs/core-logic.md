# 주요 로직과 자원 소유권

2026-10-08 현재 코드 기준이다. 지원 계약은 [제품 계약](product-contract.md), 메시지 기준은 [프로토콜](protocol.md), 실행은 [개발 가이드](development-guide.md)를 따른다.

## 코드를 읽는 순서

| 파일·영역 | 책임 |
|---|---|
| HostRun/Program.cs | CLI 검사·콘솔 동의·종료 코드·실제 인코더 진단 |
| ScreenShareOptions | 네이티브 자원 생성 전 범위·URI·허용 인코더 검사 |
| ScreenShareSession | 승인·협상 이벤트·캡처 스레드·종료 소유 |
| Security/PermissionGate | 독립 capability·세대·만료·종료 최종 상태 |
| SignalingClient/SignalingFraming | bounded UTF-8 수신·필수 버전·직렬 송신·heartbeat |
| Capture/WgcScreenCapture | 승인 모니터 WGC·resize·COM/D3D 소유 |
| Capture/DxgiScreenCapture | 같은 승인 모니터 DXGI·회전·커서·access lost |
| Media/FFmpegH264Encoder | 실제 Configure/출력 폴백·소유 스레드 인코딩 |
| Media/KeyframeRequests/H264ParameterSets | PLI/FIR 요청 병합·SPS/PPS/IDR 복구 |
| server/signaling/internal/room | 원자적 인증·예약·ready·동일 인스턴스 삭제 |
| server/signaling/internal/signaling | ACK·역할 검사·릴레이·lease·연결 종료 |
| web/viewer/connection.js | 시도별 WS/PC/timer/ICE 큐·callback 수명 |
| web/viewer/app.js | 현재 연결만 DOM 변경·오류 보존·PIN 폐기 |

## Host 수명

StartAsync는 한 번만 실행한다. 옵션과 모니터를 검사하고 코드 발급·joined ACK를 받는다. 시작 실패는 Completion의 실패 결과로 수렴한다. peer-joined는 상한 128인 직렬 이벤트 큐에서 처리한다. local consent가 없거나 거절되면 캡처 없이 종료한다. screen 이외 미구현 권한을 부여하지 않는다.

승인된 monitorInfo를 고정하고 PermissionGate에 sessionId/connectionId/lease를 묶는다. PeerConnection은 승인 뒤 생성하며 offer를 먼저 보내고 ICE를 큐에 넣는다. answer 전 원격 ICE 최대 256개를 보관한다. connected는 직렬 경로에서 캡처 스레드를 한 번 시작한다. 거절·시그널링 EOF·peer-left·오류·disconnected·deadline은 EndSession으로 수렴한다.

EndSession은 최종 송신과 같은 잠금에서 종료를 먼저 확정하고 권한을 폐기한다. lifetime 취소와 큐 종료, 첫 Completion 이유를 한 번만 설정한다. 종료와 heartbeat 처리는 지연된 동의/SDP 뒤에 기다리지 않는다. 상태 관찰자의 예외는 이미 폐기한 권한을 되살리지 못한다.

DisposeAsync는 캡처 스레드를 최대 3초 기다리고 시그널링 heartbeat와 수신/이벤트/pairing/협상 Task를 소유한다. 전체 managed 정리 목표는 5초다. 네이티브 스레드가 멈추지 않으면 그 스레드가 쓰는 인코더·PC를 먼저 해제하지 않고 timeout을 반환한다. 이때 프로세스 격리 종료를 할 E14 감독 경로는 아직 없다.

## 프레임과 인코더

캡처 스레드가 캡처·인코더의 생성/사용/폐기를 모두 맡는다. frame은 BGRA 크기/stride와 승인 monitor identity를 포함한다. 크기 변경 때 이전 화면을 폐기하고 인코더를 재설정하며 키프레임을 요청한다. 패딩 stride는 packed BGRA로 변환한다. 최종 송신 직전에 권한/종료를 다시 검사한다.

FFmpegLoader는 추적된 DLL hash를 검증해 같은 공급본만 로드한다. 인코더 factory는 후보별 별도 인스턴스를 만들고 실제 Configure/시험 출력을 통과해야 이름을 확정한다. 실패 인스턴스를 폐기한 뒤 다음 후보를 시도한다. native Configure/Encode 자체의 hang은 현재 스레드 timeout만으로 강제 중단할 수 없으며 향후 헬퍼 프로세스 감독으로 격리해야 한다.

RTCP callback은 video PLI/FIR 요청 bit만 기록한다. 인코더 소유 스레드가 요청을 합쳐 소비한다. 시험 출력의 SPS/PPS를 보존해 다음 IDR이 새 디코더에서도 복구되게 한다. SendVideo duration은 누락 프레임을 포함한 실제 송신 시간 간격으로 계산한다.

## 서버와 Viewer 경합

룸 잠금 순서는 manager→room이다. 조회·인증·만료·예약·일회성 소비를 원자화하며 network I/O는 잠금 밖에서 수행한다. joined 쓰기 성공 뒤 Activate한다. 승인 전에 peer를 ready로 공개하지 않는다. Leave는 룸을 종료하고 현재 map의 동일 인스턴스가 비었을 때만 제거한다. active room은 참가 코드 만료만으로 삭제하지 않는다.

ViewerConnection은 시도마다 ws/pc/timer/ICE/Promise chain을 소유한다. await 뒤 live/current 여부를 검사해 과거 시도의 track/close/stats가 새 UI를 변경하지 못하게 한다. 협상 deadline은 SDP await 전에 시작한다. pending 처리 큐는 128개이며 종료·lease 응답은 큐를 우회한다. end는 첫 오류 사유를 보존하고 CONNECTING 소켓도 닫는다. 기본 browser timer는 globalThis wrapper로 호출해 Illegal invocation을 피한다.

## 검증 한계

순수 회귀는 tests/RemoteAssist.Host.Core.Tests, Go tests, Viewer Node tests에서 재현한다. 실제 인코더 출력과 Chrome 오류 표시도 별도로 확인한다. 이는 사용자 세션 캡처·입력·SYSTEM/UAC·실제 TURN·두 PC 표시를 검증한 결과가 아니다. 테스트 증거와 차단 환경은 docs_working에 보관하며 완료 체크는 구현계획에서만 한다.