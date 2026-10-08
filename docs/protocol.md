# 와이어 포맷 (단일 기준)

> **이 문서가 기준이다.** C#(`RemoteAssist.Protocol`), Go(`server/signaling`), JS(`web/viewer`)는
> 각각 이 문서를 구현한 것이지 서로를 참조하지 않는다.
> **스키마 변경은 항상 이 문서를 먼저 고치고, 그 다음 세 곳의 코드를 맞춘다.**
>
> 버전: `v1` (2026-10-08, 호환 파괴 변경). v0.1 클라이언트는 거부한다.

## v1 변경 계약

인증된 joined에만 `iceServers:[{urls:[...],username,credential}]`을 포함한다. coturn REST HMAC-SHA1의 username은 `만료Unix초:sessionId:connectionId`이며 유효기간은 10분이다. 공유 secret은 서버의 보호된 secret 파일에만 둔다. 장시간 credential 갱신·rotation·실제 coturn allocation 검증은 E28/E29가 완료되어야 지원된다고 표시한다. 현재 자격증명 만료로 기존 allocation이 즉시 철회된다고 가정하지 않는다.

시그널링의 모든 메시지는 정수 `v:1`이 필요하다. 누락·다른 버전·중복 JSON 필드·잘못된 타입·128KiB 초과·binary는 거부하고 종료한다. 연결 첫 메시지는 join이며 joined ACK가 항상 첫 서버 메시지다. 룸 예약은 ACK 성공 뒤 ready가 되며 먼저 예약한 미준비 peer로 릴레이하지 않는다. 일회성 Viewer 소비는 인증/예약과 함께 원자화한다. 인증 실패는 PIN 5회 뒤 잠근다. 인증 실패와 거절 후에는 새 세션을 발급한다. Host와 Viewer 재참가로 기존 승인을 복구하지 않는다.

`POST /session` 응답은 기존 필드와 `sessionId`(비밀이 아닌 난수 감사 ID)를 포함한다. `joined`는 `sessionId`, 서버가 부여한 `connectionId`, `generation:1`을 포함한다. 클라이언트는 다른 세션/세대 메시지가 섞이면 실패로 종료하며 종료한 연결은 재활용하지 않는다. `join.displayName`은 앞뒤 공백과 제어 문자가 없는 1~80 UTF-16 code unit의 자기 신고 이름이다. Host의 `peer-joined`에는 이 이름과 connectionId가 전달되며 신원 검증으로 표현하지 않는다.

`ping`/`pong`은 5초마다 교환하고 15초 무응답이면 종료한다. `consent`/`consent-pending`/`consent-denied`는 Host에서 Viewer로만 중계한다. `consent`에는 `permissions`, 증가하는 `permissionGeneration`을 넣는다. Viewer는 이를 통지로만 쓰고 Host의 실제 권한 객체는 로컬 승인으로만 변경한다. `offer`는 Host 전용, `answer`는 Viewer 전용이다. 잘못된 역할의 보안 명령은 종료한다. SDP 96KiB, ICE 문자열 8KiB, 후보 대기 256개가 상한이다. 시그널링 상실·peer-left는 기존 승인 폐기와 전체 세션 종료다.

데이터 JSON 공통 필드는 `v`, `t`, `sessionId`, `generation`, `permissionGeneration`, `id`(요청 ID), `seq`(양의 단조 입력 순번)다. 기능 채널은 `input` reliable ordered(key/button/wheel/text/reset), `pointer` unreliable ordered(maxRetransmits=0, move), `control` reliable ordered, `file` reliable ordered, `chat` reliable ordered, `telemetry` reliable ordered로 결정한다. 서로 다른 채널의 순서는 가정하지 않으며 button/wheel은 최종 좌표를 함께 보낸다. 모든 입력은 승인된 monitor identity와 `topologyGeneration`을 포함한다. 이전 순번/세대는 폐기한다.

좌표는 video의 실제 contain 영역에서만 0~1로 계산하고 letterbox는 입력 대상에서 제외한다. Host 픽셀은 `monitorOrigin + round(normalized * (monitorSize-1))`다. SendInput absolute는 `round((pixel-virtualOrigin)*65535/(virtualSize-1))`와 VIRTUALDESK 플래그를 쓴다. blur/숨김/철회/연결 상실은 reset으로 원격 키·버튼을 해제한다. IME 조합 중 물리 문자 key는 보내지 않고 compositionend에서 확정 문자열을 1회 보낸다.

파일 청크 header는 32바이트다. ASCII RAFT 4바이트, 버전 uint16 LE, reserved uint16=0, RFC4122 순서 transfer UUID 16바이트, sequence uint32 LE, payload length uint32 LE 다음에 최대 16384바이트를 붙인다. offer는 UUID/name/size/sha256을 포함하며 별도 수락 전 본문 전송은 금지한다. diagnostics는 telemetry에만 보낸다. clipboard는 별도 권한과 방향 승인, 64KiB 한도, UUID/hash echo 방지를 적용한다. Runbook은 설치된 ID/version/hash/검증 인자에 로컬 건별 승인을 묶고 원격 임의 스크립트는 거부한다. 미구현 채널/기능은 supported capabilities에 광고하지 않는다.

v0.1 클라이언트는 v1에 참가할 수 없다. 과거 초안의 unreliable key/button과 좌표 공식은 폐기했다.

## 현재 구현 범위

ICE의 candidate 문자열은 브라우저 RTCIceCandidate 형식인 `candidate:` 접두사를 포함한다. SIPSorcery의 native candidate 값은 접두사를 포함하지 않을 수 있으므로 Host의 송신 adapter에서 정확히 한 번 붙인다. sdpMLineIndex는 0 이상의 미디어 순서다. empty/end-of-candidates는 실제 후보로 전달하지 않는다.

시그널링 v1은 구현했다. 데이터채널은 위 계약과 C# 입력 파서·파일 청크 header까지만 존재한다. Host/Viewer는 데이터채널을 개설하지 않는다. 기능별 진행 상태는 루트 구현계획에서만 관리한다.

## 참가와 승인 예시

```json
{"v":1,"type":"join","room":"418207935","role":"host","token":"Host 전용 토큰"}
{"v":1,"type":"join","room":"418207935","role":"viewer","pin":"6042","displayName":"지원자 이름"}
{"v":1,"type":"joined","sessionId":"sid","connectionId":"cid","generation":1,"role":"viewer","peerPresent":true}
{"v":1,"type":"consent","sessionId":"sid","generation":1,"permissions":["screen"],"permissionGeneration":1}
```

joined만으로 캡처하지 않는다. Host는 peer-joined를 받으면 로컬 승인 callback을 실행한다. HostRun은 화면 보기만 승인하며 다른 기능은 거부한다. 이름은 자기 신고이며 신원 인증이 아니다. 대역 외 통화 등으로 확인한다. 동의와 협상 deadline은 각각 30초다.

## 서버 메시지 방향

| 타입 | 발신 | 의미 |
|---|---|---|
| consent-pending/consent/consent-denied | Host | 로컬 승인 상태 통지 |
| offer | Host | 승인 뒤 SDP offer |
| answer | Viewer | SDP answer |
| ice | 양쪽 | candidate/sdpMid/sdpMLineIndex |
| ping/pong | 양쪽·서버 | 생존 확인 |
| bye | 양쪽 | 종료 |
| peer-joined/peer-left | 서버 | 상대 참가·이탈 |
| error | 서버 | 참가·프로토콜 오류 |

서버는 PIN/token을 제거하고 자신의 sessionId/generation을 찍어 중계한다. Host는 offer를 먼저 큐에 넣고 로컬 ICE를 그 뒤 보낸다. 양쪽은 원격 SDP 적용 전 ICE를 보관한다. 직렬 처리 큐 상한은 각각 128개다. 종료와 heartbeat 응답은 지연된 SDP 작업 뒤에 대기하지 않는다. ICE disconnected도 현재는 종료하며 E29 복구는 미구현이다.

오류 코드는 bad-request/not-found/expired/bad-pin/bad-token/bad-role/slot-taken/consumed/locked/capacity다. 일반 close가 기존 참가 오류 표시를 덮지 않는다. 코드·PIN은 숫자 문자열이며 선행 0을 보존한다. sessionId와 hostToken은 각각 난수 24바이트의 padding 없는 URL-safe Base64다.

## 보조 기능 확장 원칙

파일 header 검증은 본문 길이·reserved·transfer UUID·최대 16384바이트를 검사한다. 순번·상한·백프레셔·임시 파일·최종 체크섬·취소 및 부분 파일 제거는 E31에서 구현한다. 보조 기능의 요청·응답·오류 DTO는 E31~E34에서 문서를 먼저 확장한다. 아직 존재하지 않는 요청을 현재 지원 기능으로 광고하지 않는다.

## 변경 이력

## 로컬 IPC v1 (Windows Host 전용)

시그널링과 별개의 인증된 Named Pipe다. 서비스가 생성한 고유 pipe 이름·32-byte nonce·서비스 PID·자식 역할을 보호된 고정 바이너리의 시작 인자로 전달한다. 서버 ACL은 SYSTEM과 대상 SID만 허용하고 실제 client PID/Session ID/SID를 서비스가 생성한 자식과 대조한다. 클라이언트도 커널의 server PID를 전달받은 서비스 PID와 대조한다. 외부 네트워크 pipe client와 다른 PID는 거부한다. nonce는 로그에 쓰지 않으며 상호 hello에만 사용한다.

프레임은 uint32 LE 길이와 그 길이의 UTF-8 JSON이다. JSON 최대 32768바이트·깊이 32, 중복/미지/버전 없는 필드는 거부한다. envelope는 `v=1`, `t`, `seq`(방향별 1부터 단조 증가), `id`(요청 correlation ID), 선택적인 `nonce`(hello만), `body`다. hello의 body에는 `role`만 두며 예상 역할을 정확히 확인한다. 순번 재생·간격 누락·잘린 프레임은 연결 종료다. 쓰기 대기는 최대 128개이며 넘치면 종료한다. heartbeat는 500ms마다 전송하고 읽기 lease는 1초다. bootstrap hello는 10초 deadline을 적용한다.

허용 타입은 `hello`, `heartbeat`, `config`, `start`, `stop`, `credentials`, `state`, `consent-request`, `consent`, `admin-request`, `admin-state`, `admin-revoke`, `input`, `monitor`, `file-request`, `file-response`, `clipboard-request`, `clipboard-response`, `runbook-request`, `runbook-response`, `error`다. 역할별 방향과 요청의 session/peer/permission generation은 서비스 감독이 추가로 검사한다. 세션 승인은 `consent-request`의 현재 correlation ID와 일치하는 로컬 UI 응답만 소비한다. 임의 권한 JSON·Viewer 패킷을 이 경로의 로컬 승인으로 역직렬화하지 않는다. `body`가 credentials인 경우 코드/PIN은 로컬 표시용이며 일반 진단 로그에서 제외한다.

| 버전 | 날짜 | 내용 |
|---|---|---|
| v0 | 2026-08-24 | 초안 |
| v0.1 | 2026-08-24 | PoC 참가·오류·인증 필드 제거 |
| v1 | 2026-10-08 | 필수 버전·ACK 원자성·일회성 참가·동의·세대·heartbeat·입력 채널 분리·좌표·파일 header |
