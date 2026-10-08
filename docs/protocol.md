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

데이터 JSON 공통 필드는 `v`, `t`, `sessionId`, `generation`, `permissionGeneration`, `id`(요청 ID), `seq`(양의 단조 입력 순번)다. 기능 채널은 `input` reliable ordered(key/button/wheel/text/reset), `pointer` reliable ordered(move 최신 1건 합치기), `control` reliable ordered, `file` reliable ordered, `chat` reliable ordered, `telemetry` reliable ordered로 결정한다. 서로 다른 채널의 순서는 가정하지 않으며 button/wheel은 최종 좌표를 함께 보낸다. 모든 입력은 승인된 monitor identity와 `topologyGeneration`을 포함한다. 이전 순번/세대는 폐기한다.

권한 이름 순서는 `screen,control,admin,file,clipboard,diagnostics,runbook,chat`이며 승인한 항목만 넣는다. 빈 승인·중복·미지 권한·control 없는 admin·screen 없는 control을 거부한다. control은 모든 승인 세션에 만들고, control 권한에 input/pointer, file에 file, chat 또는 clipboard에 chat, diagnostics에 telemetry를 추가한다. data-ready의 모니터는 screen 승인 때만 포함한다. diagnostics만 승인한 경우 SDP에 video track을 넣거나 캡처·인코더를 만들지 않는다. 데이터 채널의 생존 확인은 승인한 기능 집합의 현재 generation을 검사한다.

chat 채널의 `chat`은 1~4096 UTF-16 문자의 text를 전달하며 최대 5건/초다. clipboard는 별도 권한이며 `clipboard-get`, `clipboard-set`, `clipboard-text`, `clipboard-applied`, `clipboard-rejected`를 쓴다. set/text의 text는 UTF-8 16KiB·UTF-16 8192 이하, `hash`는 UTF-8 SHA-256 소문자 hex다. JSON escaping 후에는 로컬 IPC envelope 공간을 포함해 보조 메시지 자체를 30KiB 이하로 제한한다. clipboard-get은 본문 없이 GUID N id로 요청하고 응답은 같은 id를 사용한다. clipboard-applied는 hash만, rejected는 고정 code만 포함한다. 같은 id를 재실행하지 않으며 마지막 256개를 기억한다. clipboard는 최대 2건/초이고 자동 양방향 복사를 하지 않는다. 브라우저의 명시적 텍스트 입력·복사 버튼을 사용한다. 본문은 감사·진단에 남기지 않는다.

보조 기능 요청 ID는 GUID N 소문자 32자리로 고정한다. 대소문자 변형으로 replay 검사를 우회하지 못한다.

telemetry는 diagnostics 승인 뒤 2초 주기로 `telemetry`를 보낸다. 공통 envelope와 `snapshot`을 포함한다. snapshot 필드는 `utc,cpuPercent,memory,disks,network,errors,startup,services`이며 null 필드도 생략하지 않는다. memory는 `totalBytes,availableBytes`, disks는 `name,totalBytes,freeBytes`, network는 `receivedBytes,sentBytes`, errors는 `provider,eventId,utc,level`, services는 `name,state`다. eventId는 이벤트 resource ID의 상위 severity 2비트를 제외한 값이다. 모든 정수는 JavaScript safe integer 범위이며 network는 활성 인터페이스의 누적 합계로 가상 인터페이스 때문에 중복될 수 있다. 계정·호스트 이름·IP/MAC·명령줄·파일 경로·이벤트 본문을 포함하지 않는다. 접근 불가 항목은 null이며 UI에서 N/A와 마지막 수신 시간을 표시한다. provider는 2초 상한, 전체 JSON은 32KiB, 목록은 각 128개/이벤트 20개 이하로 제한한다. 지연 provider를 병렬로 계속 쌓지 않는다.

pointer의 기존 maxRetransmits=0 목표는 고정 SDK의 SCTP partial reliability 구현 한계 때문에 변경했다. SIPSorcery 10.0.16의 DCEP 생성은 maxRetransmits>0만 분기하고 수신 채널 설정은 TODO이며 SCTP association에 FORWARD-TSN 처리가 없다. 지원을 가정하는 대신 reliable pointer의 송신/수신에서 최신 1건만 유지하고 2KiB bufferedAmount 상한을 적용한다. 입력 생존 lease가 만료되면 held 상태를 해제하고 세션을 중단해 지연된 조작이 복구 뒤 실행되지 않게 한다. 근거는 고정 [RTCDataChannel](https://github.com/sipsorcery-org/sipsorcery/blob/4ca86773993a875706d8a7a4c1e0108e3a885ebf/src/SIPSorcery/net/WebRTC/RTCDataChannel.cs), [SctpAssociation](https://github.com/sipsorcery-org/sipsorcery/blob/4ca86773993a875706d8a7a4c1e0108e3a885ebf/src/SIPSorcery/net/SCTP/SctpAssociation.cs) 소스다. partial reliability는 지원하는 transport로 교체하고 손실 시험을 통과하기 전에는 광고하지 않는다.

좌표는 video의 실제 contain 영역에서만 0~1로 계산하고 letterbox는 입력 대상에서 제외한다. Host 픽셀은 `monitorOrigin + round(normalized * (monitorSize-1))`다. SendInput absolute는 `round((pixel-virtualOrigin)*65535/(virtualSize-1))`와 VIRTUALDESK 플래그를 쓴다. blur/숨김/철회/연결 상실은 reset으로 원격 키·버튼을 해제한다. IME 조합 중 물리 문자 key는 보내지 않고 compositionend에서 확정 문자열을 1회 보낸다.

입력 `seq`는 `input`과 `pointer` 채널마다 독립적으로 증가한다. 두 채널을 하나의 순번으로 비교하면 최신 pointer가 먼저 도착한 뒤 신뢰 전이 key/button이 버려지는 결함이 생기므로 금지한다. 입력은 필수 양의 `inputEpoch`를 포함한다. 초기 epoch는 1이다. reliable `reset/all`은 다음 epoch로 정확히 1 증가하며 원격 키·버튼을 해제한다. 다른 입력은 현재 epoch와 일치해야 한다. reset 이전의 지연 pointer는 폐기하고, 새 epoch의 pointer가 reset보다 먼저 도착해도 폐기한다. reliable 순번은 reset 후에도 계속 증가한다. 이전 모니터/권한/연결 세대의 입력은 어떤 채널에서도 실행하지 않는다.

reliable 마우스 button/wheel 전이는 필수 `pointerBarrier`(0 이상의 safe integer)를 포함한다. Viewer는 대기 pointer를 먼저 flush하고 당시 송신한 pointer 순번을 barrier로 넣는다. Host는 전이의 최종 좌표를 적용한 뒤 barrier 이하의 지연 move를 폐기한다. flush가 배압으로 보류됐으면 대기 move는 폐기한다. 따라서 독립 채널의 오래된 move가 버튼 좌표를 되돌리지 않는다. reset 해제에 성공하면 Host는 control 채널에서 같은 ID의 `input-reset-ack`와 `inputEpoch`를 응답한다. Viewer는 ACK 전 새 입력을 보내지 않으며 1초 ACK timeout은 전체 연결 종료다.

파일 청크 header는 32바이트다. ASCII RAFT 4바이트, 버전 uint16 LE, reserved uint16=0, RFC4122 순서 transfer UUID 16바이트, sequence uint32 LE, payload length uint32 LE 다음에 최대 16384바이트를 붙인다. offer는 UUID/name/size/sha256을 포함하며 별도 수락 전 본문 전송은 금지한다. diagnostics는 telemetry에만 보낸다. clipboard는 위의 별도 권한·크기 한도·UUID/hash 응답 결합을 적용한다. Runbook은 설치된 ID/version/hash/검증 인자에 로컬 건별 승인을 묶고 원격 임의 스크립트는 거부한다. 미구현 채널/기능은 supported capabilities에 광고하지 않는다.

### 파일 전송 상세

file 채널의 JSON은 공통 envelope와 `file-offer`(name,size,hash), `file-accept`, `file-reject`(code), `file-complete`(size,hash), `file-received`(size,hash), `file-cancel`(code)다. id는 소문자 GUID N이며 binary header의 transfer UUID와 일치한다. name은 Windows의 안전한 파일명 한 개이며 경로·ADS·장치명·끝의 공백/마침표를 금지한다. size는 0~256MiB 정수, hash는 전체 파일 SHA-256 소문자 hex다. 청크 seq는 0부터 연속 증가하며 길이는 1~16384바이트다. 빈 파일은 청크 없이 complete로 진행한다. JSON의 seq는 채널 방향별 독립 순번이다. file-reject/cancel code는 `declined,busy,timeout,io-error,hash-mismatch,cancelled,too-large`만 허용한다.

수신자는 저장한 누적 byte count인 size와 다음 청크 순번인 nextChunk를 포함한 `file-progress`를 4개 청크마다 또는 마지막 청크 뒤 보낸다. 중간 청크는 항상 16384바이트이며 마지막 청크만 작을 수 있다. sender는 수신 확인되지 않은 청크를 16개 이내로 제한하고 credit를 10초 안에 받지 못하면 중단한다. nextChunk는 증가해야 하며 보낸 청크 수를 넘거나 대응 size가 다르면 거부한다. 이것은 SCTP 자체 수신 ACK와 별개로 브라우저 파일 쓰기의 완료를 반영한다.

종료한 최근 256개 전송의 취소·해당 size/hash의 늦은 receipt와 이미 전송 중이던 binary는 저장·재실행 없이 폐기한다. 이전 offer/accept/complete 재생으로 전송을 재개하지 않는다.

동시에 진행하는 전송은 방향을 합쳐 1개이며 최근 256개 ID를 재사용하지 않는다. 개별 수락은 30초 기한, 수락 후 무진행 10초·전체 10분 상한이다. sender는 bufferedAmount 64KiB에서 대기하고 256KiB 이상이면 중단한다. receiver 큐는 최대 32개 청크이며 늦거나 중복·누락·다른 UUID·size 초과 청크를 즉시 거부한다. complete를 검증해 저장을 확정한 뒤 file-received를 보낸다. offer와 accept 이전 본문은 허용하지 않는다. 취소/철회/오류는 저장을 확정하지 않으며 임시 파일을 삭제한다. 재연결에 전송을 승계하지 않는다.

파일 수락·송신 파일 선택·Runbook 승인 UI를 열기 전에 Host가 원격 입력을 중단하고 보유 키를 해제한다. control의 `local-approval`은 kind=`file|runbook`, state=`pending|resumed`를 보낸다. 요청 ID는 UI correlation ID이며 pending에는 inputEpoch가 없고, resumed에는 control 승인 때 증가한 inputEpoch를 포함한다. control 권한이 없으면 resumed에도 inputEpoch가 없다. Viewer는 pending부터 입력과 관리자 요청을 막고 resumed에서 로컬 보유 상태와 포인터 순번을 비운다. 관리자 승인과 다른 로컬 승인 UI를 겹치지 않으며 관리자 활성 상태에서 파일 선택/수락은 거부한다. 사용자 응답은 30초 제한이고 취소·세션 종료 뒤 늦은 UI 응답으로 파일을 실행하지 않는다.

파일 감사 IPC는 `file-audit`→`file-audit-ack`다. body는 `id,sessionId,permissionGeneration,phase,direction,size,outcome`이며 phase=`started|finished`, direction=`to-host|from-host`, outcome=`allowed|denied|completed|failed|stopped`만 허용한다. started는 allowed, finished는 나머지 결과다. 서비스의 보호 저널 기록·flush ACK 전에 offer/accept/본문을 보내지 않는다. 완료 감사는 저장 확정/상대 receipt 뒤 기록한다. 감사 실패는 세션을 종료하며 이미 확정한 정상 파일을 임의 삭제하지 않는다. SIPSorcery 10.0.16에는 bufferedamountlow 이벤트가 구현되지 않아 Host는 10ms 간격으로 bounded bufferedAmount를 확인하며 10초 무진행 제한을 함께 적용한다.

로컬 승인과 Viewer reset이 경합하면 resumed/관리자 응답의 Host epoch는 Viewer 현재 값+1이어야 한다. Viewer reset ACK가 아직 대기 중이면 Host가 그 reset을 처리하기 전에 로컬 phase를 전환했을 수 있으므로 현재 값과 같은 epoch도 허용한다. 이때 reset 대기를 폐기하고 마지막 16개 폐기 reset ID/epoch와 정확히 맞는 늦은 ACK만 무시한다. 미지 ACK·다른 epoch는 거부한다. pending 로컬 승인 중 reset ACK의 1초 타임아웃을 중단하며 control lease는 계속 확인한다. 새 phase 뒤의 이전 epoch 입력은 Host가 폐기한다.

Host의 파일 I/O는 일반 사용자 Helper에서만 수행한다. 로컬 선택 경로를 Viewer에게 전달하지 않는다. 1차 저장 대상은 로컬 고정 NTFS 볼륨이다. 선택 폴더의 조상을 handle-relative NtCreateFile로 열어 reparse를 거부하고 rename/delete 공유를 막는다. 임시 파일은 CreateNew·DeleteOnClose로 만들며 hash/size 검증과 flush 후 같은 폴더 안에 handle 기반 NTFS hard link를 원자적으로 생성해 확정한다. 최종 이름이 존재하면 실패한다. 임시 이름은 정상 종료와 프로세스 강제 종료 때 OS가 삭제하고, 확정 이름만 남는다. 기존 파일 자동 덮어쓰기·실행·SYSTEM 쓰기를 금지한다. Viewer는 사용자 gesture로 선택한 File System Access write stream을 우선 쓰고 검증 후 close/실패 시 abort한다. API가 없으면 8MiB 이하만 메모리 수신·명시적 다운로드로 제한하며 큰 파일은 거부한다. upload hash는 작은 청크로 계산하고 전체 파일을 무제한 arrayBuffer로 적재하지 않는다. 감사는 transfer ID·방향·크기·결과만 기록하고 파일 이름/경로/내용은 기록하지 않는다.

v0.1 클라이언트는 v1에 참가할 수 없다. 과거 초안의 unreliable key/button과 좌표 공식은 폐기했다.

## 현재 구현 범위

ICE의 candidate 문자열은 브라우저 RTCIceCandidate 형식인 `candidate:` 접두사를 포함한다. SIPSorcery의 native candidate 값은 접두사를 포함하지 않을 수 있으므로 Host의 송신 adapter에서 정확히 한 번 붙인다. sdpMLineIndex는 0 이상의 미디어 순서다. empty/end-of-candidates는 실제 후보로 전달하지 않는다.

시그널링 v1과 승인 후 control 데이터 채널을 구현했다. 보호 설치의 제품 서비스에서 실제 Chrome의 data-ready→data-ping→data-pong을 확인했다. 일반 제어 승인 시 Host가 input/pointer도 생성하며 복원 감독이 준비돼야 이를 광고한다. 다른 기능은 아직 광고하지 않는다. 기능별 진행 상태는 루트 구현계획에서만 관리한다.

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

### 데이터 제어 채널 시작 계약

Host가 승인 후 `control` reliable ordered 채널을 offer에 포함해 만든다. Viewer는 같은 label의 채널 한 개만 받아들인다. 미지원/중복 채널은 세션 오류다. Host의 첫 `data-ready`는 데이터 공통 envelope와 `channels`, `permissions`, `monitor:{identity,topologyGeneration,x,y,width,height}`를 포함한다. 화면 전용은 `channels:["control"]`, `permissions:["screen"]`이다. 일반 제어는 복원 감독 준비 후 `channels:["control","input","pointer"]`, `permissions:["screen","control"]`이다. 모든 input 채널이 open이고 일치하는 준비 통지가 도착하기 전에는 Viewer가 입력을 보내지 않는다. Viewer는 500ms data-ping을 보내며 바인딩과 순번을 검사한 ping만 Host의 입력 lease를 갱신한다. 2초 동안 pong이 없으면 연결을 종료한다. 후속 file 등의 실제 실행·철회가 연결된 뒤 추가한다.

초기 관리자 요청 범위를 승인했으면 permissions는 `screen,control,admin` 순서이며 채널 구성은 일반 제어와 같다. `admin-request`는 GUID N 형식 id와 공통 envelope만 보낸다. Host는 입력을 중단하고 기존 키를 해제한 뒤 2차 동의 창을 요청한다. 지원자가 원격으로 자기 승인 창을 조작할 수 없어야 한다. Host의 같은 ID `admin-state`는 `state:pending|denied|active`를 응답한다. active에만 양의 `adminGeneration`을 포함한다. denied/active에는 Host가 증가시킨 `inputEpoch`를 넣는다. 승인 중 대기한 이전 epoch 입력은 실행하지 않는다. Viewer는 local held 상태·pending pointer를 지우고 새 epoch를 사용한다. Viewer는 active 통지를 받기 전 특권 기능을 표시하지 않는다. `admin-sas`와 `admin-revoke`는 현재 adminGeneration을 포함하며, revoke는 지원 전체 종료다. Host의 로컬 승인·privileged 준비 ACK만 active를 만들 수 있다.

control JSON은 UTF-8 32KiB 상한과 중복 필드 거부를 적용한다. `seq`는 채널의 송신 방향마다 독립적인 양의 단조 순번이다. 세션/연결/권한 세대가 맞아야 처리한다. `data-ping` 요청에는 같은 `id`의 `data-pong`을 보내며 remote consent/grant를 이 경로에서 받지 않는다. 채널 닫힘·타입/바인딩 오류·256KiB 송신 bufferedAmount 상한 초과는 기존 권한을 폐기하고 세션을 끝낸다.

파일 header 검증은 본문 길이·reserved·transfer UUID·최대 16384바이트를 검사한다. 순번·상한·백프레셔·임시 파일·최종 체크섬·취소 및 부분 파일 제거는 E31에서 구현한다. 보조 기능의 요청·응답·오류 DTO는 E31~E34에서 문서를 먼저 확장한다. 아직 존재하지 않는 요청을 현재 지원 기능으로 광고하지 않는다.

## 변경 이력

## 로컬 IPC v1 (Windows Host 전용)

관리자 IPC는 `admin-request`/`admin-response`의 현재 request ID와 service가 결합한 sessionId/connectionId/permissionGeneration을 사용한다. 초기 admin은 요청 허용 범위이며 `AdminAuthorization`의 2차 로컬 승인이 실제 lease를 만든다. `admin-config`는 해당 승인·target SID·monitor·mapping·output 상한만 전달하고, `admin-ready` 이후 일반 Helper에 `admin-state`를 보낸다. `admin-input`은 같은 adminGeneration의 검증된 InputStroke 최대 64개를 받아 `admin-input-ack`로 실제 성공 prefix 수를 응답한다. `admin-lease`는 검증된 data-ping에서만 갱신한다. `admin-revoke`는 긴급 중단으로 dispatch backlog를 우회한다. `admin-sas`는 현재 control+관리자 승인에서만 서비스가 정책을 진단하고 호출한다. Recovery의 `recovery-admin`은 서비스가 시작한 현재 특권 Helper의 PID를 ACK 전에 결합하고 모든 관련 Helper를 차단한 뒤 up을 복원한다.

특권 프레임 mapping은 서비스가 생성하는 고유 `Global\\RemoteAssist-<GUID N>` 이름을 인증 IPC로만 전달한다. 슬롯 두 개는 각각 header 64바이트 + payload 16MiB다. header는 little-endian commit(0), permission generation(8), topology generation(16), admin generation(24), width(32), height(36), stride(40), payload bytes(44), secure desktop flag(48), reserved 0(52), monotonic timestamp ms(56)다. commit은 프레임 순번×2이며 쓰는 동안 홀수다. 단일 SYSTEM writer가 payload를 복사한 뒤 even commit을 publish한다. reader는 승인된 세 세대와 geometry/stride/크기를 확인하고 복사 전후 commit이 같은 최신 슬롯만 소비한다. 아직 쓰는 슬롯·변조·잘린 크기·이전 세대는 송신하지 않는다. 관리자 해제 전에 SYSTEM이 두 슬롯을 지운다. 파일이나 로그로 프레임을 저장하지 않는다.

복원 전용 IPC는 `recovery-bind`(sessionId, permissionGeneration, userSid, helperPid), `recovery-reserve`(동일 sessionId/permissionGeneration, revision, keys, buttons, unicode), `recovery-ack`(같은 correlation ID), `recovery-lease`를 추가한다. 예약은 실제 down 전에 ACK하며 성공한 up 뒤에만 예약 집합을 줄인다. revision은 1부터 정확히 증가한다. keys는 1~254의 VK, buttons는 left/right/middle, unicode는 1~65535의 UTF-16 code unit이다. 중복·초과·다른 세션·늦은 세대는 거부하고 종료한다. 이 메시지는 인증된 서비스↔Recovery와 서비스↔일반 Helper 사이에서만 사용하며 Viewer가 로컬 승인·복원 예약을 직접 보낼 수 없다. Recovery에는 다른 실행 명령이 없다. 복원 예약과 Unicode 값은 진단·감사 로그에 저장하지 않는다.

시그널링과 별개의 인증된 Named Pipe다. 서비스가 생성한 고유 pipe 이름·32-byte nonce·서비스 PID·자식 역할을 보호된 고정 바이너리의 시작 인자로 전달한다. 서버 ACL은 SYSTEM과 대상 SID만 허용하고 실제 client PID/Session ID/SID를 서비스가 생성한 자식과 대조한다. 클라이언트도 커널의 server PID를 전달받은 서비스 PID와 대조한다. 외부 네트워크 pipe client와 다른 PID는 거부한다. nonce는 로그에 쓰지 않으며 상호 hello에만 사용한다.

프레임은 uint32 LE 길이와 그 길이의 UTF-8 JSON이다. JSON 최대 32768바이트·깊이 32, 중복/미지/버전 없는 필드는 거부한다. envelope는 `v=1`, `t`, `seq`(방향별 1부터 단조 증가), `id`(요청 correlation ID), 선택적인 `nonce`(hello만), `body`다. hello의 body에는 `role`만 두며 예상 역할을 정확히 확인한다. 순번 재생·간격 누락·잘린 프레임은 연결 종료다. 쓰기 대기는 최대 128개이며 넘치면 종료한다. heartbeat는 500ms마다 전송하고 읽기 lease는 1초다. bootstrap hello는 10초 deadline을 적용한다.

허용 타입은 `hello`, `heartbeat`, `config`, `start`, `stop`, `credentials`, `state`, `consent-request`, `consent`, `admin-request`, `admin-state`, `admin-revoke`, `input`, `monitor`, `file-request`, `file-response`, `clipboard-request`, `clipboard-response`, `runbook-request`, `runbook-response`, `error`다. 역할별 방향과 요청의 session/peer/permission generation은 서비스 감독이 추가로 검사한다. 세션 승인은 `consent-request`의 현재 correlation ID와 일치하는 로컬 UI 응답만 소비한다. 임의 권한 JSON·Viewer 패킷을 이 경로의 로컬 승인으로 역직렬화하지 않는다. `body`가 credentials인 경우 코드/PIN은 로컬 표시용이며 일반 진단 로그에서 제외한다.

| 버전 | 날짜 | 내용 |
|---|---|---|
| v0 | 2026-08-24 | 초안 |
| v0.1 | 2026-08-24 | PoC 참가·오류·인증 필드 제거 |
| v1 | 2026-10-08 | 필수 버전·ACK 원자성·일회성 참가·동의·세대·heartbeat·입력 채널 분리·좌표·파일 header |
