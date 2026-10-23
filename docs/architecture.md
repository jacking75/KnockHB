# 아키텍처

신뢰 모델과 기능 범위는 [제품 계약](product-contract.md), 와이어 포맷은 [프로토콜](protocol.md)이 기준이다.

## 실행 경계

```mermaid
flowchart LR
 subgraph target["대상 Windows PC"]
  service["SYSTEM · Session 0 서비스\n허가·지원 ID·헬퍼 감독·감사"]
  tray["일반 권한 WPF Tray\n지원 ID·접속 표시·즉시 종료·채팅·파일 보내기·클립보드"]
  helper["대상 사용자 Helper\n캡처·인코딩·WebRTC·일반 입력"]
  admin["SYSTEM AdminHelper (사용자 세션)\n기본·보안 데스크톱 캡처·입력"]
  book["RunbookWorker\n일반 또는 SYSTEM"]
  service <-->|"인증 Named Pipe"| tray
  service <-->|"인증 Named Pipe"| helper
  service <-->|"설정·입력·모니터"| admin
  service <-->|"작업·결과"| book
  admin -->|"ACL 공유 프레임"| helper
 end
 go["Go 시그널링 · TLS/WSS\n지원 ID 방·SDP/ICE 중계"]
 viewer["지원자 웹 Viewer"]
 helper <-->|"HTTPS/WSS"| go
 viewer <-->|"HTTPS/WSS"| go
 helper <-->|"DTLS-SRTP/SCTP · 직접 P2P"| viewer
```

서비스는 Session 0에서 캡처하지 않는다. WTS로 확인한 단일 콘솔 Session/SID에 Tray와 Helper를 만든다. 사용자가 바뀌면 지원 허가를 없애고 모든 자식을 끝낸다.

## 지원 허가와 지원 ID

- 대상자가 `RemoteAssist.Update.exe`(정식: `support`, 개발: `support-dev`)를 관리자 권한으로 실행하면 `ProgramData/RemoteAssist/initial-support.json`에 허가를 쓴다. 허가는 콘솔 사용자 SID·Windows 세션·로그온 시각에 묶인다. 파일 ACL은 SYSTEM/Administrators만 허용한다.
- 서비스는 시작할 때마다 허가를 읽는다(`InitialSupportLaunch.Load`). 같은 로그온(사용자·세션·로그온 시각)이면 유지되므로 서비스 재시작·업데이트 뒤에도 지원이 이어진다. 재부팅·로그오프·사용자 변경이면 지운다.
- 대상자의 즉시 종료·Tray 종료·사용자 변경은 허가를 지운다(`StopSupport`).
- 지원 ID는 `ProgramData/RemoteAssist/device-id.txt`의 고정 9자리다. Helper는 매 세션 `POST /session {"code":<지원 ID>}`로 같은 방 번호를 연다.

## 세션 수명

1. 허가가 있고 Helper가 없으면 서비스가 Helper를 띄운다(`StartHelper`). Helper는 시그널링 방을 열고 지원자를 무기한 기다린다.
2. 지원자가 들어오면 Helper가 바로 전 권한(`PermissionGate.Grant`)으로 PeerConnection과 모든 기능 채널을 만든다. 승인 단계는 없다.
3. Viewer는 연결 직후 관리자 모드를 요청한다. 서비스가 AdminHelper를 띄우고, 이후 화면과 입력은 AdminHelper를 거친다(일반·상승 창·UAC·잠금 화면을 한 경로로 처리).
4. 지원자가 나가면 세션이 끝나고 서비스는 1초 뒤(시작 실패가 이어지면 5초 뒤) 새 Helper를 띄운다. 시그널링 서버가 내려가 있어도 재시도한다.

### 일반·관리자 이중 구조의 결정

일반 Helper(사용자 토큰)는 UIPI 때문에 상승 창·보안 데스크톱에 입력할 수 없다. 대상자가 이미 관리자 실행으로 허가했으므로, 연결되면 항상 관리자 모드로 들어가 SYSTEM AdminHelper가 현재 입력 데스크톱을 따라가며 캡처·입력한다. WebRTC·인코딩은 일반 Helper에 남긴다(네트워크·코덱을 SYSTEM 프로세스에 두지 않기 위해). 관리자 헬퍼 시작이 실패하면 일반 모드로 계속하고 Viewer는 다시 시도할 수 있다. 두 PC·서로 다른 GPU에서의 실측은 실연결 검증(구현계획 7단계)에서 확인한다.

## 소유권과 종료

- `ScreenShareSession`이 직렬 이벤트 큐·시그널링 수신·heartbeat·연결 generation·캡처 스레드를 소유한다. 캡처·스케일러·인코더는 캡처 스레드 하나에서 만들고 쓰고 버린다. 오래된 PeerConnection·채널 callback은 generation 검사로 버린다.
- 서비스의 Job이 Helper·AdminHelper·RunbookWorker를 소유한다. 헬퍼가 멈추면 Job 종료로 정리한다. 입력은 Helper 안의 `InputSession`이 눌린 키·버튼을 추적해 정지·일시 중단 때 떼고, AdminHelper는 데스크톱을 떠날 때 자기가 누른 키를 뗀다.
- `session.gate` 파일 잠금은 지원자가 연결된 동안만 잡는다. 업데이트 설치는 연결이 없을 때 진행한다.
- 감사 기록(`AuditJournal`)은 지원을 막지 않는다. 기록 실패는 `service-status.ndjson`에 남긴다.

## 미디어와 입력

- 모니터 identity/geometry를 세션에 묶는다. WGC 우선, 초기화 실패 때 같은 모니터의 DXGI를 쓴다. 해상도·배치가 바뀌면 같은 모니터(없으면 주 모니터)로 자동으로 다시 맞춘다.
- 관리자 모드 중에도 모니터를 바꿀 수 있다. Helper가 입력 좌표 기준을 옮기고 AdminHelper에 `admin-monitor`로 새 모니터를 알린다.
- 관리자 모드가 아닐 때 보안 데스크톱(UAC·잠금)이 뜨면 세션을 끊지 않고 마지막 화면을 유지한다.
- FFmpeg 8.1 LGPL DLL만 hash 검사 후 고정 경로에서 로드한다. 후보는 nvenc → qsv → amf → mf → openh264 순서이며 libx264는 금지다.
- 입력은 input/pointer 채널을 분리하고 순번·inputEpoch·barrier로 오래된 입력을 거부한다. 좌표는 0~1 정규화다.

## 보조 기능

- 파일: 한 번에 한 건, 16KiB 청크, credit 배압, SHA-256, NTFS handle 기반 원자 확정. 수신 파일은 대상 사용자 다운로드 폴더 아래 `RemoteAssist`에 저장하며 기존 파일을 덮어쓰지 않는다.
- 클립보드·채팅·진단은 각자 상한을 둔 채널이다.
- Runbook: 등록 작업(고정 source/hash)과 원격 PowerShell 명령. 서비스가 일반 또는 SYSTEM worker Job으로 실행하며 20초·출력 상한이 있다.

## 연결 복구

일시 ICE 단절이 2초 이어지면 Host가 같은 시그널링 위에서 다음 generation offer로 전송을 다시 만든다(최대 3회). 시그널링이 끊기거나 지원자가 나가면 세션을 끝내고 Host가 같은 지원 ID로 다시 대기한다.

## 설치·업데이트

정식 설치는 Program Files/RemoteAssist/versions의 보호 ACL 경로다. Authenticode 서명 Host, RSA-PSS 서명 manifest/ZIP 검증, pending transaction → 서비스 image 전환 → ready → commit 순서이며 실패하면 직전 버전으로 rollback한다. 개발 설치(Program Files/RemoteAssist-Dev)와 빌드 출력 실행은 개발 모드로 보고 보호 경로 ACL·릴리스 manifest 검사를 생략한다. 정식 설치 검사는 배포 단계에서 다시 검토한다.
