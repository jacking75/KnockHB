# 와이어 포맷 (단일 기준)

> **이 문서가 기준이다.** C#(`RemoteAssist.Protocol`), Go(`server/signaling`), JS(`web/viewer`)는
> 각각 이 문서를 구현한 것이지 서로를 참조하지 않는다.
> **스키마 변경은 항상 이 문서를 먼저 고치고, 그 다음 세 곳의 코드를 맞춘다.**
>
> 버전: `v0.1`

> **구현 상태 (2026-10-07):** 1장의 시그널링은 구현되어 있다. 2~6장의 데이터채널과 기능 메시지는
> 설계 초안이며, 현재 Host/Viewer는 해당 채널을 생성하지 않는다. `InputMsg` DTO만 골격으로 존재한다.
> 입력 전이 이벤트의 신뢰성·좌표 공식(G02), 파일 청크 형식, 진단 정보 채널 배치는 구현 전에 확정해야 한다.
> 현재 실행 구조는 [아키텍처](architecture.md), 기능 흐름은 [워크플로우](workflows.md)를 참고한다.

## 공통 규약

- 인코딩은 **UTF-8 JSON**. 파일 청크만 바이너리다.
- 필드명은 **짧게, 소문자로** 고정한다. C#은 `[JsonPropertyName]` 으로 맞춘다(파스칼 케이스 자동 변환에 기대지 않는다).
- 모르는 필드는 **무시**한다. 새 필드 추가는 하위 호환으로 취급한다.
- 필드를 **제거하거나 의미를 바꾸는 것은 호환 파괴**다. `v1`로 올린다.

---

## 1. 시그널링 (Host/Viewer ↔ 서버, WebSocket)

엔드포인트: `ws://<server>:25001/ws`

```jsonc
{
  "type": "join",     // join | joined | peer-joined | peer-left | offer | answer | ice | bye | error
  "room": "418207935",
  "sdp":  "v=0\r\n...",              // offer / answer 일 때만
  "ice":  {                           // ice 일 때만
    "candidate": "candidate:1 1 udp ...",
    "sdpMid": "0",
    "sdpMLineIndex": 0
  }
}
```

| type | 방향 | 의미 |
|---|---|---|
| `join` | 클라 → 서버 | 룸 참가 요청. **연결 후 첫 메시지는 반드시 이것이어야 한다** |
| `joined` | 서버 → 참가자 | 참가 성공 ack. `peerPresent` 로 반대편이 이미 있는지 알려준다 |
| `peer-joined` | 서버 → 기존 참가자 | 반대편이 붙었다. **Host는 이때 offer를 만든다** |
| `peer-left` | 서버 → 남은 참가자 | 반대편이 끊겼다 |
| `offer` / `answer` | 클라 ↔ 클라 (서버 릴레이) | SDP 교환 |
| `ice` | 클라 ↔ 클라 (서버 릴레이) | ICE 후보 교환 |
| `bye` | 양방향 | 세션 종료 |
| `error` | 서버 → 클라 | 코드/PIN 오류, 만료, 정원 초과 |

`ice` 필드는 브라우저 `RTCIceCandidate` 의 필드명을 그대로 쓴다. 뷰어에서 변환할 일이 없게 하기 위한 것이다.

### join / joined

```jsonc
// Host — POST /session 으로 받은 hostToken 으로 host 슬롯을 점유한다
{ "type": "join", "room": "418207935", "role": "host",   "token": "..." }

// Viewer — 대역 외로 전달받은 PIN 으로 참가한다
{ "type": "join", "room": "418207935", "role": "viewer", "pin": "6042" }

// 서버 응답
{ "type": "joined", "room": "418207935", "role": "viewer", "peerPresent": true }
{ "type": "peer-joined", "role": "viewer" }
{ "type": "peer-left",   "role": "viewer" }
```

역할 슬롯(host/viewer)은 각각 하나뿐이다. 이미 점유된 역할로 참가하면 `slot-taken` 오류가 난다.

> **릴레이 시 인증 필드는 제거된다.** 서버는 `offer`/`answer`/`ice`/`bye` 를 반대편에 넘길 때
> `type`·`room`·`sdp`·`ice` 만 전달한다. `pin`·`token` 이 상대에게 새어 나가지 않게 하기 위한 것이다.

### error

```jsonc
{ "type": "error", "code": "bad-pin", "reason": "PIN이 올바르지 않습니다" }
```

| code | 의미 |
|---|---|
| `bad-request` | 첫 메시지가 join이 아니거나 형식 오류 |
| `not-found` | 없는 세션 코드 |
| `expired` | 만료된 세션 |
| `bad-pin` | PIN 불일치 |
| `bad-token` | host 토큰 불일치 |
| `bad-role` | role 이 host/viewer 가 아님 |
| `slot-taken` | 해당 역할이 이미 사용 중 |

### 세션 코드 발급 (REST)

```
POST /session
→ 200 { "code": "418207935", "pin": "6042", "hostToken": "...", "expiresIn": 600 }
```

코드는 일회성·단기 만료(기본 10분). PIN은 대역 외로 전달된다. 상세는 T3.1.

---

## 2. 데이터 채널

성격이 다른 트래픽을 나눠 신뢰성·순서 정책을 각각 최적화한다.

| 채널 | 신뢰성 / 순서 | 페이로드 | 용도 |
|---|---|---|---|
| `input` | **unreliable** · ordered | JSON | 마우스·키보드 (최신성 우선, 재전송 무의미) |
| `control` | reliable · ordered | JSON | 세션 제어, 모니터 전환, 품질 조절, 상태 |
| `file` | reliable · ordered | JSON + 바이너리 | 파일 전송 |
| `chat` | reliable · ordered | JSON | 텍스트 채팅, 시스템 정보 |

---

## 3. `input` 채널

### 좌표 규약 (중요)

좌표는 **항상 0~1 정규화**해서 보내고, Host가 대상 모니터 픽셀로 환산한다.
뷰어 창 크기·DPI 배율에 영향받지 않게 하기 위한 것이다. **픽셀 좌표를 그대로 보내지 않는다.**

- 뷰어: `x = e.offsetX / rect.width`
- Host: `절대좌표 = (monitor.X + x * monitor.W) * 65535 / virtualW` (SendInput 절대좌표 환산)

### 마우스

```jsonc
{ "t": "mouse", "a": "move",  "x": 0.4213, "y": 0.6187, "mon": 0 }
{ "t": "mouse", "a": "down",  "x": 0.4213, "y": 0.6187, "mon": 0, "btn": "left" }
{ "t": "mouse", "a": "up",    "x": 0.4213, "y": 0.6187, "mon": 0, "btn": "left" }
{ "t": "mouse", "a": "wheel", "x": 0.4213, "y": 0.6187, "mon": 0, "d": -120 }
```

| 필드 | 값 |
|---|---|
| `a` | `move` \| `down` \| `up` \| `wheel` |
| `btn` | `left` \| `right` \| `middle` |
| `d` | 휠 델타 (120 단위) |
| `mon` | 모니터 인덱스 (0부터) |

### 키보드

```jsonc
{ "t": "key", "a": "down", "vk": 65, "mods": ["ctrl"] }
{ "t": "key", "a": "up",   "vk": 65 }
{ "t": "key", "a": "text", "text": "안녕" }
```

| 필드 | 값 |
|---|---|
| `a` | `down` \| `up` \| `text` |
| `vk` | Windows 가상 키코드 |
| `text` | 유니코드 문자열 (IME·특수문자용, `KEYEVENTF_UNICODE`로 주입) |
| `mods` | `ctrl` \| `alt` \| `shift` \| `win` 의 배열 |

> **Ctrl+Alt+Del (SAS)** 은 일반 주입이 막힌다. `input` 채널로 보내지 않고
> `control` 채널의 `sas` 명령으로 요청해 서비스가 `SendSAS` API로 처리한다.

---

## 4. `control` 채널

```jsonc
{ "t": "monitor",  "index": 1 }                       // 캡처 모니터 전환
{ "t": "quality",  "mode": "auto" }                   // auto | high | low
{ "t": "sas" }                                        // Ctrl+Alt+Del 요청
{ "t": "admin",    "a": "request" }                   // 관리자 모드 요청 (동의 게이트로)
{ "t": "admin",    "a": "granted" | "denied" }
{ "t": "consent",  "screen": true, "control": true, "admin": false }
{ "t": "stats",    "fps": 30, "encoder": "h264_nvenc", "path": "p2p" }
```

`stats.encoder` 는 실제 선택된 FFmpeg 인코더 이름이다. 뷰어 상태바에 노출한다
(성능 확인용이자, **libx264가 아님을 현장에서 확인하는 수단**이다 — T1.3b).

`stats.path` 는 `p2p` \| `relay`.

---

## 5. `file` 채널

```jsonc
{ "t": "file-offer",    "id": "f1", "name": "log.zip", "size": 1048576, "hash": "sha256:..." }
{ "t": "file-accept",   "id": "f1" }
{ "t": "file-reject",   "id": "f1", "reason": "거부됨" }
// 이후 바이너리 청크 스트림 (16KB 안팎, 순번 포함)
{ "t": "file-complete", "id": "f1", "hash": "sha256:..." }
{ "t": "file-error",    "id": "f1", "reason": "체크섬 불일치" }
```

- 청크 크기는 **16KB 안팎**.
- 채널 버퍼가 임계치를 넘으면 송신을 잠시 멈춘다(**백프레셔**).
- 완료 시 **체크섬 검증**은 필수다.

---

## 6. `chat` 채널

```jsonc
{ "t": "msg",     "from": "viewer", "text": "재부팅해 주세요", "ts": 1756000000 }
{ "t": "sysinfo", "cpu": 12.4, "memPct": 68, "disk": [ { "d": "C:", "freeGb": 42 } ] }
```

`sysinfo` 의 상세 항목은 T5.6(시스템 상태 대시보드)에서 확정한다.

---

## 7. 변경 이력

| 버전 | 날짜 | 내용 |
|---|---|---|
| `v0` | 2026-08-24 | 초안. Phase 1 착수 전 기준 |
| `v0.1` | 2026-08-24 | T1.1 구현에 맞춰 `join`/`joined`/`peer-left`·오류 코드·릴레이 시 인증 필드 제거 규칙 명시 |
