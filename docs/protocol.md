# 와이어 포맷 (단일 기준)

> **이 문서가 기준이다.** C#(`RemoteAssist.Protocol`), Go(`server/tunnel`), JS(`web/viewer`)는
> 각각 이 문서를 구현한 것이지 서로를 참조하지 않는다.
> **스키마 변경은 항상 이 문서를 먼저 고치고, 그 다음 세 곳의 코드를 맞춘다.**
>
> 버전: `v1`. 2026-10-23 상호 신뢰 규격으로 동의·승인 메시지를 없앴다.

## 터널링 서버

WebSocket 메시지는 정수 `v:1`이 필요하다. 누락·다른 버전·중복 JSON 필드·잘못된 타입·128KiB 초과·binary는 거부하고 연결을 끊는다. 미등록 필드도 거부한다. HTTP `POST /session` 본문에는 `v` 필드가 없다.

### 방 열기

`POST /session`은 선택적 본문 `{"code":"<9자리>"}`로 Host의 고정 지원 ID 방을 연다. 같은 코드의 방에 Host가 연결 중이면 409, 이전 방이 끝났거나 Host가 아직 참가하지 않은 방이면 교체한다. 코드가 없으면 서버가 난수 코드를 발급한다. 응답은 `{sessionId, code, hostToken, expiresIn}`다. sessionId·hostToken은 난수 24바이트의 padding 없는 URL-safe Base64다. code는 선행 0을 보존한 숫자 9자리 문자열이다. Host가 연결된 방은 만료되지 않고 Host가 나가면 끝난다. 발급 속도 제한은 없다.

### 참가

```json
{"v":1,"type":"join","room":"418207935","role":"host","token":"Host 전용 토큰"}
{"v":1,"type":"join","room":"418207935","role":"viewer","displayName":"PC 관리 담당"}
{"v":1,"type":"joined","sessionId":"sid","connectionId":"cid","generation":1,"role":"viewer","room":"418207935","peerPresent":true}
```

연결의 첫 메시지는 join이고 joined ACK가 첫 서버 메시지다(10초). Host는 hostToken으로 자기 방을 증명하고, Viewer는 지원 ID만으로 들어온다. `displayName`은 표시용이며 서버가 앞뒤 공백·제어 문자를 지우고 80 UTF-16 단위로 자른다(비면 "지원자"). 예약한 피어는 ACK 뒤에만 중계 대상이 된다. joined에 `iceServers`는 없다.

Viewer가 이미 있는 방에 다른 Viewer가 들어오면 서버는 기존 Viewer에 `error/replaced`를 보내고 끊는다. 새 Viewer는 `error/busy`를 받고 다시 접속한다. Host는 기존 Viewer의 peer-left로 세션을 끝내고 같은 ID로 다시 대기한다.

### 메시지

| 타입 | 발신 | 의미 |
|---|---|---|
| peer-joined / peer-left | 서버 | 상대 참가·이탈(peer-joined에 displayName, connectionId) |
| offer | Host | `sdp` 문자열, `generation`. 현재 세대 또는 정확히 다음 세대 |
| answer | Viewer | `sdp` 문자열, `generation`. 현재 세대 |
| ice | 양쪽 | `ice:{candidate,sdpMid?,sdpMLineIndex?}`, `generation`. 현재 세대 |
| ping / pong | 양쪽·서버 | 5초 간격, 15초 무응답 종료 |
| bye | 양쪽 | 종료 |
| error | 서버 | 참가·프로토콜 오류 |

오류 코드: `bad-request, not-found, expired, bad-token, bad-role, slot-taken, consumed, busy, replaced, capacity`. Viewer는 `replaced`를 뺀 오류에서 자동으로 다시 접속한다. 잘못된 역할의 offer/answer는 연결 종료다. SDP 96KiB, ICE 문자열 8KiB, 원격 후보 대기 256개가 상한이다. 서버는 token을 떼고 sessionId/generation을 찍어 중계한다.

ICE candidate 문자열은 브라우저 형식 `candidate:` 접두사를 포함한다. Host는 SIPSorcery native 값에 정확히 한 번 붙인다. Viewer는 `candidate`/`sdpMid`/`sdpMLineIndex`만 복사한다.

### 기한

Viewer는 joined 뒤 20초 안에 offer가 없으면 끊고 다시 접속한다. offer 수신 뒤 WebRTC 협상은 30초다. Host는 지원자가 들어오면 바로 offer를 만든다.

### H.264 수신 level

Host offer는 `packetization-mode=1;profile-level-id=42e034;level-asymmetry-allowed=1`이고, answer의 level을 Viewer가 받을 수 있는 상한으로 쓴다(3.1 → 1280×720, 4.x → 1920×1080, 5.x → 그 이상). Chrome은 실제 디코딩 능력과 관계없이 answer에 3.1(`42e01f`)을 적으므로, Viewer는 브라우저에는 원래 answer를 적용하고 터널링 서버로 보내는 사본에서만 `level-asymmetry-allowed=1`인 H264 fmtp의 level이 4.2(`2a`)보다 낮으면 4.2로 올린다. 이는 Host에 1080p60 수신 상한을 알리는 정책이며 실제 디코딩·표시 성능을 보장하지 않는다.

### 전송 재구성 세대

STUN/TURN 서버는 쓰지 않는다. 양쪽은 빈 ICE 서버 목록으로 host 후보만 교환한다. 연결 generation은 1부터 시작한다. 일시 `disconnected`/`failed`가 2초 이어지면 Host가 같은 터널링 서버에서 다음 generation offer를 보낸다(ICE 재시작에 해당). 5초 간격·연속 최대 3회·각 30초다. PeerConnection을 다시 만들면 데이터 채널 순번·inputEpoch는 새 generation에서 1부터 시작하고, 캡처·입력·진행 중 파일·Runbook을 멈췄다가 재개한다. 관리자 모드는 유지된다. 터널링 서버 상실·지원자 이탈은 재구성하지 않고 세션을 끝낸다.

## 데이터 채널 공통

데이터 JSON 공통 필드는 `v, t, sessionId, generation, permissionGeneration, id, seq`다. `permissionGeneration`은 항상 1이다(전 권한 고정, 필드는 호환을 위해 남긴다). `seq`는 채널·방향마다 독립적인 양의 단조 순번이다. JSON은 UTF-8 32KiB·깊이 32·중복 필드 거부다.

권한과 채널은 고정이다.

- permissions: `screen,control,admin,file,clipboard,diagnostics,runbook,chat`
- channels: `control,input,pointer,file,chat,telemetry,runbook` (모두 reliable ordered)

Viewer는 이 밖의 채널·중복 채널을 받으면 세션 오류로 끊는다.

## control

Host의 첫 `data-ready`는 `channels`, `permissions`(위 고정 목록)와 `monitor:{identity,topologyGeneration,x,y,width,height}`를 포함한다. Viewer는 500ms마다 `data-ping`을 보내고 같은 id의 `data-pong`을 받는다. 2초 동안 pong이 없으면 끊는다. 채널 닫힘·바인딩 오류·256KiB bufferedAmount 초과는 세션 종료다.

| t | 방향 | 필드 |
|---|---|---|
| monitor-list | V→H, H→V | 응답에 `monitors`(최대 16개) |
| monitor-select | V→H | `monitorIdentity` |
| monitor-state | H→V | `monitor`(증가한 topologyGeneration), `inputEpoch` |
| quality-report | V→H | `sample:{rttMs,lossPercent,decodeMs,receivedKbps}` 최대 1건/초 |
| quality-mode | V→H | `mode: auto\|detail\|motion` |
| quality-state | H→V | `quality:{fps,bitrateKbps,longEdge,shortEdge,mode}` |
| admin-request | V→H | GUID N id만 |
| admin-state | H→V | `state: pending\|denied\|active`, active에 `adminGeneration`, denied/active에 `inputEpoch` |
| admin-sas | V→H | `adminGeneration` |
| admin-revoke | V→H | `adminGeneration`. 세션 종료 |
| input-reset-ack | H→V | reset 입력과 같은 id, `inputEpoch` |

모니터 전환: Host는 입력을 멈추고 눌린 키를 뗀 뒤 새 모니터로 캡처(관리자 모드면 AdminHelper)를 옮기고, `monitor-state`에 새 topologyGeneration과 새 `inputEpoch`를 보낸다. Viewer는 이 epoch부터 입력을 보낸다. 관리자 모드 중에도 전환한다(관리자 모드 시작 중에만 막는다). 해상도·배치가 바뀌거나 모니터가 빠지면 Host가 같은 모니터(없으면 주 모니터)로 스스로 다시 맞추고 같은 방식으로 알린다. 이때 id는 Host가 만든 값이다.

관리자 모드: Viewer는 입력 채널이 준비되면 세션당 한 번 `admin-request`를 자동으로 보낸다(실패하면 지원자가 버튼으로 다시 요청). Host는 입력을 멈추고 서비스에 AdminHelper를 요청한다. 준비되면 `admin-state/active`와 새 inputEpoch를 보낸다. 실패하면 `denied`로 일반 모드를 계속한다. 대상자 승인 단계는 없다.

품질: 자동 정책은 5초 이상 유지하며 혼잡 때 낮추고 15초 안정 뒤 단계적으로 회복한다. 수치는 유한한 0 이상의 값이며 RTT/decode 10000ms, loss 100%, bitrate 100000kbps가 상한이다.

## input / pointer

공통 필드 외에 `monitorIdentity`, `topologyGeneration`, `inputEpoch`가 필요하다. 동작은 `t`와 `a`로 구분한다.

| 채널 | t / a | 동작 필드 |
|---|---|---|
| pointer | `mouse` / `move` | `x`, `y` (0~1) |
| input | `mouse` / `down`, `up` | `x`, `y`, `btn: left\|right\|middle`, `pointerBarrier` |
| input | `mouse` / `wheel` | `x`, `y`, `d` (-12000~12000), `pointerBarrier` |
| input | `key` / `down`, `up` | `vk` (Windows 가상 키 1~254), 선택적 `mods` |
| input | `key` / `text` | `text` (1~4096 UTF-16 문자, NUL 금지), 선택적 `mods` |
| input | `reset` / `all` | 증가시킨 `inputEpoch` |

`mods`는 중복 없는 `ctrl`, `alt`, `shift`, `win` 배열이다. `pointerBarrier`는 마지막으로 보낸 move 순번이며 move 전이면 0이다.

- 입력 `seq`는 `input`과 `pointer` 채널마다 독립적으로 증가한다.
- 모든 입력은 `inputEpoch`, monitor identity, `topologyGeneration`을 포함한다. 초기 epoch는 1이다. `reset/all`은 epoch를 정확히 1 올리고 원격 키·버튼을 뗀다. Host는 `input-reset-ack`로 응답하며 Viewer는 ACK 전 새 입력을 보내지 않는다(1초 timeout은 연결 종료).
- 관리자 모드 전환·모니터 전환 뒤에는 Host가 알린 epoch를 쓴다. Viewer reset ACK가 대기 중이면 같은 epoch도 허용하고, 그 reset의 늦은 ACK는 무시한다(최근 16개).
- pointer는 move만 보내며 최신 1건만 유지한다(2KiB bufferedAmount 상한). button/wheel은 최종 좌표와 `pointerBarrier`를 함께 보낸다. Host는 barrier 이하의 늦은 move를 버린다.
- 좌표는 video contain 영역의 0~1이다. Host 픽셀은 `monitorOrigin + round(normalized * (monitorSize-1))`, SendInput absolute는 `round((pixel-virtualOrigin)*65535/(virtualSize-1))`와 VIRTUALDESK다. blur/숨김/연결 상실은 reset이다. IME 조합 중 물리 문자 key는 보내지 않고 compositionend에서 확정 문자열을 1회 보낸다.

## file

JSON: `file-offer`(name,size,hash), `file-accept`, `file-reject`(code), `file-progress`(size,nextChunk), `file-complete`(size,hash), `file-received`(size,hash), `file-cancel`(code). id는 소문자 GUID N이며 binary header의 transfer UUID와 같다. name은 경로·ADS·장치명·끝 공백/마침표가 없는 Windows 파일명, size는 0~256MiB, hash는 SHA-256 소문자 hex다. code는 `declined,busy,timeout,io-error,hash-mismatch,cancelled,too-large`.

청크 header 32바이트: ASCII `RAFT` 4, 버전 uint16 LE, reserved uint16 0, RFC4122 순서 UUID 16, sequence uint32 LE, length uint32 LE, 뒤에 최대 16384바이트. 수신자는 4청크마다·마지막 뒤 `file-progress`를 보내고 송신자는 미확인 16청크 이내로 보낸다. 동시 전송 1건, 수락 30초·무진행 10초·전체 10분.

지원자 → 대상: Host는 확인 없이 대상 사용자 다운로드 폴더 아래 `RemoteAssist`에 저장하며 기존 파일을 덮어쓰지 않는다. 대상 → 지원자: Tray에서 고른 파일을 보내며, Viewer는 저장 위치를 고른 뒤 수락한다(File System Access API가 없으면 8MiB까지 메모리 수신 후 다운로드).

파일 감사 IPC는 `file-audit`→`file-audit-ack`이며 body는 `id,sessionId,permissionGeneration,phase(started|finished),direction(to-host|from-host),size,outcome(allowed|denied|completed|failed|stopped)`다. 파일 이름·경로·내용은 감사하지 않는다.

## chat / clipboard

chat 채널의 `chat`은 1~4096 UTF-16 문자, 최대 5건/초다. clipboard는 `clipboard-get`, `clipboard-set`, `clipboard-text`, `clipboard-applied`, `clipboard-rejected`(code: `clipboard-busy|too-large|unavailable`). set/text의 text는 UTF-16 8192·UTF-8 16KiB 이하, `hash`는 UTF-8 SHA-256 소문자 hex다. `clipboard-applied`에도 적용한 텍스트의 `hash`를 넣는다. 최대 2건/초, 응답 2초. 자동 동기화는 없다.

## telemetry

2초 주기 `telemetry`의 `snapshot:{utc,cpuPercent,memory,disks,network,errors,startup,services}`(null도 생략하지 않음). memory `totalBytes,availableBytes`, disks `name,totalBytes,freeBytes`, network `receivedBytes,sentBytes`, errors `provider,eventId,utc,level`, services `name,state`. 계정·호스트 이름·IP/MAC·명령줄·파일 경로는 넣지 않는다. 목록 각 128개, 이벤트 20개. 진단 수집 오류·2초 기한 초과에서는 해당 수집 루프만 중단한다. 화면·입력·파일 세션은 유지되므로 마지막 진단값을 새 측정으로 취급하지 않는다.

## runbook

Host의 `runbook-catalog`는 `books:[{bookId,bookVersion,hash,title,impact,requiresAdmin,source}]`(최대 16개, 등록 작업)를 보낸다. Viewer의 `runbook-request`는 두 가지다.

- 등록 작업: `bookId`(network-summary|clear-dns-cache), `bookVersion:1`, `hash`(카탈로그 source hash), `args:{detail:"summary"|"counts"}`.
- 원격 명령: `bookId:"command"`, `bookVersion:1`, `hash`(script의 CRLF→LF 정규화 UTF-8 SHA-256), `args:{script,admin:"true"|"false"}`. script는 1~8192자 PowerShell이며 admin이면 SYSTEM, 아니면 대상 사용자 권한으로 실행한다. hash는 전송 무결성 확인이고 내용 제한은 없다.

동시 실행 1개, 실행 20초, 응답 55초. `runbook-cancel`은 같은 id로 취소한다. 결과 `runbook-result`의 `result:{state,exitCode,stdout,stderr,remainingImpact}`, state는 `denied,completed,failed,cancelled,timed-out`, stdout 8192자·stderr 4096자·합계 UTF-8 24KiB다. 원격 명령은 stderr를 그대로 돌려주고, 등록 작업은 엔진 오류 본문을 숨긴다. 감사에는 id·bookId·hash·결과만 남긴다.

## 로컬 IPC v1 (Windows Host 전용)

인증된 Named Pipe다. 서비스가 고유 pipe 이름·32바이트 nonce·서비스 PID·자식 역할을 고정 실행 파일의 시작 인자로 넘긴다. 서버 ACL은 SYSTEM과 대상 SID만 허용하고 client PID/Session/SID/이미지를 서비스가 만든 자식과 대조한다. 프레임은 uint32 LE 길이 + UTF-8 JSON(최대 32768바이트·깊이 32)이고 envelope는 `v=1, t, seq, id, nonce(hello만), body`다. heartbeat 500ms, 읽기 lease 1초, hello 10초.

| 경로 | 타입 |
|---|---|
| 서비스→Tray | `device{code,granted}`, `state`, `supporter{displayName}`, `update-status`, `chat`, `clipboard-request`, `file-state` |
| Tray→서비스 | `stop`, `chat{text}`, `file-send{path}`, `file-cancel`, `clipboard-response` |
| 서비스→Helper | `config`, `start`, `stop`, `admin-state`, `admin-input-ack`, `clipboard-response`, `file-audit-ack`, `runbook-result`, `file-send`, `file-cancel`, `chat` |
| Helper→서비스 | `credentials`, `supporter`, `state`, `chat`, `clipboard-request`, `file-state`, `file-audit`, `runbook-request`, `runbook-cancel`, `admin-request`, `admin-sas`, `admin-monitor`, `admin-input` |
| 서비스↔AdminHelper | `admin-config`, `admin-monitor`, `admin-input`→`admin-input-ack`, `admin-ready`, `stop` |
| 서비스↔RunbookWorker | `runbook-config`→`runbook-result` |

`admin-input`은 같은 adminGeneration의 InputStroke 최대 64개를 받고 `admin-input-ack`로 실제 성공 개수를 응답한다. 이전 모니터 좌표의 입력은 0으로 응답하고 버린다.

특권 프레임 mapping은 서비스가 만드는 고유 `Global\RemoteAssist-<GUID N>` 이름을 IPC로만 전달한다. 슬롯 두 개는 각각 header 64바이트 + payload 16MiB다. header는 little-endian commit(0), permission generation(8), topology generation(16, 항상 1), admin generation(24), width(32), height(36), stride(40), payload bytes(44), secure desktop flag(48), reserved(52), monotonic timestamp ms(56)다. commit은 프레임 순번×2이며 쓰는 동안 홀수다. reader는 복사 전후 commit이 같은 최신 슬롯만 쓴다. 1초보다 오래된 프레임은 버린다.

## Host 감사 보고

Host 서비스 감사 spool은 선택적으로 `POST /audit`(HTTPS, node bearer credential)로 보고한다. 이벤트는 EventId(32자리 소문자 GUID), Utc, Kind/Result(숫자 enum), SessionId/ConnectionId(최대 64 ASCII), PermissionGeneration, 선택적 Operation(Id,BookId,Hash,Direction,Size)의 폐쇄 필드만 허용한다. BookId는 `network-summary, clear-dns-cache, command`다. 서버는 fsync 뒤 204로 ACK하고 같은 EventId의 같은 내용은 중복 ACK한다. 감사 실패는 지원을 막지 않는다.

| 버전 | 날짜 | 내용 |
|---|---|---|
| v1 | 2026-10-08 | 필수 버전·ACK 원자성·세대·heartbeat·입력 채널 분리·좌표·파일 header |
| v1 | 2026-10-23 | 상호 신뢰: consent/승인/PIN·TURN 메시지 삭제, 고정 권한/채널, Viewer 교체(busy/replaced), monitor-state inputEpoch, 원격 명령 |
