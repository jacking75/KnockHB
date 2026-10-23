# 두 PC 실연결(E2E) 시험 가이드

실제 두 Windows PC에서 제품 경로를 확인하는 방법이다. 단위 테스트·한 PC 루프백 검사(`scripts/loopback-check.ps1`)로 확인할 수 없는 것을 본다. 대상은 실제 화면 표시, 실제 입력 주입, UAC 보안 데스크톱, 두 PC 사이 성능이다. 2026-10-23~24에 이 방식으로 수행한 결과와 남은 과제는 §9에 있다.

시험 운영 원칙(사용자 재개 지시, 사전 계획, 짧은 화면 점유, 실패 시 즉시 중단·정리, 같은 시험 반복 금지)은 [AGENTS.md](../AGENTS.md)의 "실연결(E2E) 시험" 절이 기준이다. 이 문서는 그 원칙 아래의 **실행 방법**이다.

## 1. 구성과 역할

| 역할 | 장비 | 하는 일 |
|---|---|---|
| A 지원자 PC | `<A_IP>` | 저장소, 빌드, 터널링 서버(TCP 25001), Chrome Viewer, 시험 진행 |
| B 대상 PC | `<B_IP>`, SSH 계정 `<B_USER>` | 개발 설치, 사용자 세션의 Tray/Helper/AdminHelper |
| 사람 | A·B 앞 | 브라우저 인증서 예외 허용, 필요 시 B의 UAC 승인, 브라우저 저장 창 조작, 비상 시 B에서 즉시 종료 |

- 미디어·입력은 A와 B가 직접 P2P로 주고받는다. 터널링 서버는 A가 여는 접속 지점(Viewer 페이지 제공·첫 SDP/ICE 교환)일 뿐이다.
- 준비 스크립트는 Windows PowerShell 5.1을 지원한다. PowerShell 7은 필수가 아니다. A에는 .NET SDK·Go·Python과 `cryptography`, OpenSSH 클라이언트가 필요하고 B에는 원격 설치에 사용할 OpenSSH 서버와 관리자 권한 세션이 필요하다.
- `<A_IP>`, `<B_IP>`, `<B_USER>`는 시험 환경 값으로 바꾼다. 준비 스크립트에는 기존 개발 환경 기본값이 남아 있으므로 `-HostIp`, `-TargetHost`, `-TargetUser`를 반드시 명시한다.

### 비밀 정보

- 기존 SSH 설정과 등록된 호스트 키를 사용한다. 키 인증이면 `-PasswordEnv`를 생략한다. 비밀번호 인증이 필요한 환경에서만 기존 환경 변수의 **이름**을 `-PasswordEnv`로 넘긴다. 비밀번호·토큰·개인 키·환경 변수 전체는 출력·문서·커밋에 남기지 않는다.
- 접속 정보 메모는 git 미추적 파일 `e2e-ssh.env`(`*.env` 무시)에 둔다. 호스트·계정·변수 이름만 적는다.
- `scripts/e2e-ssh.ps1`은 `SSH_ASKPASS` 도우미를 임시 폴더에 만든다. 도우미 안에도 변수 이름만 들어간다.

## 2. 사전 점검과 설치 준비

실행 전에 후보 commit·바이너리 hash·인증서/포트·로그 경로·소유 PID/경로를 기록한다. `-Install`은 B의 서비스·설치 파일·OS 설정을 변경하며 `-TargetDir`의 기존 디렉터리를 삭제하고 다시 만든다. 시험 전용 새 경로를 지정한다. `-StartTunnel`은 A에 서버 창을 열며 방화벽 확인이 필요할 수 있으므로 화면 점유가 전혀 없는 작업으로 취급하지 않는다.

```powershell
$hostIp = '<A_IP>'
$targetHost = '<B_IP>'
$targetUser = '<B_USER>'
$targetDir = 'C:/ra-candidate' # 시험 전용 경로인지 확인한다

# 1) 준비 점검: .NET 10 SDK, Go(server/tunnel 기준 툴체인), Python cryptography, A LAN IP, 포트, B ping/SSH, 브라우저
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\e2e-preflight.ps1 -HostIp $hostIp -TargetHost $targetHost -TargetUser $targetUser

# 2) 준비 자동화: TLS 인증서 → Host 후보 빌드 → B에 복사·검증·설치 → 터널링 서버 기동
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\e2e-prepare.ps1 -HostIp $hostIp -TargetHost $targetHost -TargetUser $targetUser -TargetDir $targetDir -Install -StartTunnel
```

`e2e-prepare.ps1`이 하는 일은 다음과 같다.
- `.artifacts\e2e-<시각>\`에 7일짜리 자체 서명 TLS 인증서를 만든다.
- 후보(`candidate.json`과 `payload\`)를 빌드한다.
- B의 `-TargetDir`(위 예시는 `C:\ra-candidate`)로 후보와 `install-host-dev.ps1`만 복사해 Validate와 Install을 실행한다.
- 터널링 서버를 고정 경로 `tunnel.exe`로 빌드해 새 창에서 띄운다. `go run`의 임시 exe는 실행할 때마다 방화벽 확인 창을 띄우므로 쓰지 않는다.

B 원격 명령은 `-EncodedCommand`로 보낸다. Windows OpenSSH 기본 셸이 cmd인 환경에서도 명령 인용을 유지하기 위해서다.

### 준비 확인

- 터널링 서버: A와 B 양쪽에서 `curl.exe -sk https://<A_IP>:25001/healthz`가 `ok`이다. 이 명령의 `-k`는 생존 확인용이며 인증서 검증 성공을 뜻하지 않는다. Host의 DER 핀과 서버 인증서는 별도로 대조한다.
- 서비스 경로: `HKLM\SYSTEM\CurrentControlSet\Services\RemoteAssistHost\ImagePath`가 따옴표로 감싼 경로다.
- 관리자 헬퍼 GPU 지정: `HKU\S-1-5-18\Software\Microsoft\DirectX\UserGpuPreferences`에 AdminHelper 경로가 `GpuPreference=1;`로 있다.

## 3. B에서 지원 시작

지원 시작은 대상자가 `RemoteAssist.Update.exe`를 관리자 권한으로 한 번 실행하는 것이다. 시험에서는 두 방법 중 하나를 쓴다.

- **사람이 UAC 승인**(제품 그대로): SSH로 B 사용자 세션에 일회성 작업을 등록해 `Start-Process 'C:\Program Files\RemoteAssist-Dev\RemoteAssist.Update.exe'`를 실행한다. B 화면에 뜬 UAC를 사람이 승인한다.
- **출력 확인용**: 같은 작업을 `-RunLevel Highest`로 등록하고, `cmd /c "...\RemoteAssist.Update.exe" > C:\ra-probe\update.txt 2>&1`로 실행한다. UAC 없이 관리자 권한으로 돌고, 성공하면 `DEVELOPMENT_SUPPORT_READY`가 남는다. 실패 사유도 남으므로 준비 문제를 진단할 때 이 방법을 쓴다.

일회성 작업 패턴(B에서 `-EncodedCommand`로 실행, `C:\ra-probe`는 미리 준비한다):

```powershell
$a=New-ScheduledTaskAction -Execute 'cmd.exe' -Argument '/c "C:\Program Files\RemoteAssist-Dev\RemoteAssist.Update.exe" > C:\ra-probe\update.txt 2>&1'
$p=New-ScheduledTaskPrincipal -UserId '<B_USER>' -LogonType Interactive -RunLevel Highest   # 사람 승인 방식이면 Limited
Register-ScheduledTask -TaskName 'RA-E2E-Launch' -Action $a -Principal $p -Force | Out-Null
Start-ScheduledTask -TaskName 'RA-E2E-Launch'   # 끝나면 Unregister-ScheduledTask로 지운다
```

확인 위치:
- 서비스 상태 기록: `C:\Program Files\RemoteAssist-Dev\service-status.ndjson`. `tray-ready`·`helper-launched`가 남는다. `helper-stopped`에는 종료 사유(`detail`)가 함께 남는다.
- 지원 ID: `C:\ProgramData\RemoteAssist\device-id.txt`.
- 터널링 서버 로그: Host `참가` 줄.

## 4. A에서 Viewer 접속

1. Chrome에서 `https://<A_IP>:25001/`을 연다. 자체 서명 인증서 경고는 **사람이** "고급 → 이동"으로 넘긴다. 인증서를 새로 만들 때마다 다시 넘겨야 한다. 이 경고 화면은 브라우저 자동화가 붙지 못한다.
2. 지원 ID와 이름을 넣고 **접속**을 누른다. 마지막 값이 미리 채워진다.
3. 상단의 `해상도 · 디코딩 fps · 수신 kbps · 경로: <B IP>:<포트>`와 "관리자 모드 · UAC·잠금 화면과 관리자 창도 조작할 수 있다."를 확인한다.

Viewer는 연결되면 자동으로 관리자 모드를 요청한다. 관리자 모드를 시작하지 못하면 "일반 권한 창만 조작한다"로 계속된다. 그때는 SYSTEM 진단 로그 `C:\Windows\System32\config\systemprofile\AppData\Local\RemoteAssist\diagnostic.ndjson`의 `hresult`를 본다.

## 5. 기능 시험 절차와 판정

| 항목 | 방법 | PASS 기준 |
|---|---|---|
| 화면 | 접속 | 1920×1080 표시, 경로가 B IP 직접 |
| 입력 | B에 메모장을 띄우고(SSH 작업), 영상에서 클릭·키 입력·창 드래그. 한글은 "문자 입력" 칸 | 메모장에 그대로 입력되고 창이 움직인다 |
| 관리자 창 | B에 관리자 명령 프롬프트를 띄우고 A에서 창 X 클릭 | 닫힌다(일반 권한 입력은 UIPI로 불가) |
| UAC | SSH 작업으로 B 사용자 세션에서 `Start-Process cmd.exe -Verb RunAs`를 실행한다. **B 사람은 누르지 않는다** | 보안 데스크톱 동의 화면이 A에 보이고, A에서 "예" 클릭이 반영된다 |
| Ctrl+Alt+Del | 상단 버튼 | 보안 화면이 보이고, A에서 "취소"로 복귀한다 |
| 재접속 | B에서 `Restart-Service RemoteAssistHost` | 같은 지원 ID로 약 10초 안에 자동 재접속한다. 새로고침은 자동 재접속이 아니라 ID만 채운다 |
| 즉시 종료 | B Tray **즉시 종료** | 화면이 끊기고, `initial-support.json`이 사라지고, 재접속이 거부된다. 다시 관리자 실행하면 재개된다 |
| 원격 명령 | `whoami`를 SYSTEM 체크 켜고/끄고 실행 | `nt authority\system` / B 사용자 |
| Runbook | "네트워크 어댑터 개수 조회" | `completed · 종료 코드 0` |
| 클립보드 | A→B 전송 후 B 사용자 세션에서 `Get-Clipboard`(SSH 작업, `-Sta`)로 확인. B→A는 같은 작업으로 `Set-Clipboard` 후 Viewer "대상 PC에서 읽기" | 값이 일치한다 |
| 채팅 | Viewer → B Tray, B Tray 입력란을 원격 조작해 → Viewer | 양쪽에 표시된다 |
| 파일 A→B | Viewer 파일 선택(브라우저 자동화는 file input에 업로드), "대상 PC로 보내기" | B `Downloads\RemoteAssist`에 같은 SHA256 파일 |
| 파일 B→A | 아래 "B→A 파일" 참고 | A에 받은 파일의 SHA256이 원본과 같다 |
| 모니터 전환 | B에 모니터 2개가 있어야 한다 | 목록·전환·좌표가 맞다 |

### B→A 파일

- Tray "지원자에게 파일 보내기" 창은 A에서 원격으로 조작할 수 있다.
- 파일 제안은 **수락 30초** 안에 받아야 한다([프로토콜](protocol.md)). File System Access API를 쓰는 Viewer 저장 창(`showSaveFilePicker`)은 페이지 DOM 자동화로 조작할 수 없다. 이 API가 없는 브라우저는 8MiB까지 메모리 수신 후 다운로드한다.
- 그래서 A에서 파일을 **선택까지만** 해 두고, 사람이 "열기 → 받은 파일 저장 → 저장 위치 지정"을 한 번에 한다. 저장 위치(예: 다운로드)를 미리 정해 알려 준다.
- 판정은 A에 저장된 파일의 크기와 SHA256으로 한다.

## 6. 성능 측정

B에 측정 대상 창을 띄운다. `scripts/e2e-motion-target.ps1`을 B로 복사한 뒤 SSH 일회성 작업(`Limited`, `-Sta`)으로 실행한다. 정해진 시간이 지나면 창이 스스로 닫힌다.

- `-Mode motion`: 막대가 계속 움직여 매 프레임 화면이 바뀐다. 정지 화면에서는 캡처 프레임이 거의 나오지 않으므로 FPS 측정에 필요하다.
- `-Mode latency`: 키를 누를 때마다 창 전체가 흑/백으로 반전된다.

### FPS (60초)

`scripts/browser-media-measure.js`와 같은 방식이다. Viewer 페이지에서 60초 동안 아래 값을 모은다.
- `getVideoPlaybackQuality()`의 `totalVideoFrames`와 `droppedVideoFrames` 증가량. 이 값만으로 Host 송신 FPS를 판정하지 않는다
- `requestVideoFrameCallback` 횟수(표시)

송신·표시 판정은 각각 1920×1080에서 평균 30fps 이상이다. 송신은 같은 측정 구간의 Host 송신 프레임 계수 또는 WebRTC outbound 통계를 별도로 수집한다. 표시 횟수는 A 화면 재생 빈도에 묶인다. `requestAnimationFrame` 횟수로 A의 실제 갱신률을 먼저 확인한다.

### 입력-표시 지연

1. latency 창을 Viewer 영상에서 한 번 클릭해 B 포커스를 준다.
2. 창 안쪽 영역을 영상 좌표로 구한다. 페이지 좌표 → `getBoundingClientRect`와 `object-fit: contain` 여백 보정 → `videoWidth/videoHeight`.
3. `scripts/browser-latency-measure.js`의 `measureInputToDisplay(region, 40)`를 실행한다. 약 30초 걸리고 결과는 `window.__lat`에 남는다.
4. 판정은 p95 150ms 이하다. 40회 모두 검출됐는지(`missed=0`), 흑/백이 번갈아 바뀌었는지 확인한다.

이 스크립트는 Viewer의 합성 키 이벤트 발생부터 밝기가 반전된 영상 프레임의 `expectedDisplayTime`까지 측정한다. 전송·주입·화면 갱신·캡처·인코딩·디코딩을 포함하지만 물리 키보드 지연이나 패널의 실제 발광 시각을 계측한 값은 아니다. 프레임 콜백이 오지 않으면 대기가 끝나지 않을 수 있으므로 시험 묶음에 별도 시간 제한을 두고 신호 누락 시 중단한다. A 화면이 느리면(예: 30Hz) 표시 대기가 더해지므로 결과와 함께 A 갱신률을 적는다.

## 7. 정리 (매 시험 끝, 중단 때도)

설치 폴더와 시험 파일을 지우기 전에 로그·후보 hash·측정 결과 등 필요한 증거를 별도 경로에 수집한다. 아래 경로는 §2의 예시이며 실제 `-TargetDir`와 계획에 기록한 경로를 사용한다.

1. Viewer가 응답하면 입력 reset을 보내고 탭을 닫는다. 연결 종료 뒤 눌린 키·버튼이 해제됐는지 확인한다.
2. 기록한 PID/경로의 B 시험 창과 이번 실행에서 만든 작업만 Stop·Unregister한다. 기존 `RA-E2E-*` 작업을 일괄 삭제하지 않는다.
3. B에서 `C:\ra-candidate\install-host-dev.ps1 -Action Remove`를 실행한다.
   - 지우는 것: 서비스, 설치 폴더, 방화벽 규칙, 관리자 헬퍼 GPU 지정, SAS 준비, 지원 허가.
   - 남는 것: 지원 ID·감사 기록(`C:\ProgramData\RemoteAssist`).
4. B 복사본과 시험 파일을 지운다: `C:\ra-candidate`, `C:\ra-probe`, 보낸 시험 파일.
5. 시험 데이터까지 지울 때는 `C:\ProgramData\RemoteAssist`, B 사용자와 SYSTEM의 `AppData\Local\RemoteAssist`, B의 `Downloads\RemoteAssist`에서 이번 시험이 만든 파일만 지운다. 기존 지원 ID·감사 기록·사용자 파일은 전체 초기화가 계획에 포함된 경우에만 삭제한다. 재귀 삭제 전 절대 경로가 기록한 시험 대상과 일치하는지 확인한다.
6. A에서 기록한 PID/실행 경로의 터널링 서버를 끄고, `tls\` 키·`host-candidate\`·받은 시험 파일을 지운다.
7. 확인 항목(B): 서비스 없음, 설치 폴더 없음, 허가 없음, `RemoteAssist.Startup.v1` 방화벽 규칙 0개, GPU 지정 없음, `RemoteAssist*` 프로세스 0개.

## 8. 알려진 함정

| 증상 | 원인·대처 |
|---|---|
| preflight Go FAIL | 저장소 루트의 `go`는 오래된 버전일 수 있다. `server/tunnel`에서 `GOTOOLCHAIN=auto`로 go.mod 버전을 쓰므로 그 폴더 기준으로 본다 |
| 설치 거부 "Existing block rule for RemoteAssist" | 예전에 방화벽 확인 창을 닫아 생긴 `TCP/UDP Query User{…}…helper.exe` Block 규칙이다. 해당 규칙만 지운다 |
| `RemoteAssist.Update.exe` "Service does not belong…" | 서비스 `ImagePath` 따옴표 누락. 고쳐진 `install-host-dev.ps1`로 다시 설치한다 |
| 관리자 모드 거부, `0x887A0004` | 하이브리드 GPU에서 DXGI 복제 불가. GPU 지정(`GpuPreference=1;`)이 등록됐는지 본다. `CaptureDump --probe-dxgi`로 어댑터·출력 배치를 확인한다 |
| 화면이 1280×720 | Viewer가 H264 수신 level 4.2를 알리는지 확인한다(protocol.md "H.264 수신 level") |
| 브라우저 자동화의 빠른 타이핑에서 글자가 빠짐, Ctrl 조합이 안 됨 | 자동화 도구가 수정자 키를 따로 누르지 않는다. 한 글자씩 보내거나 사람 키보드로 확인한다 |
| "문자 입력"이 안 들어감 | 영상 포커스를 잃으면 입력 초기화 ACK를 기다린다. 그 사이 누른 전송은 보관했다가 보낸다. 값이 남아 있으면 다시 누른다 |
| B→A 파일이 0바이트·버튼 무반응 | 저장 중 세션이 끊긴 경우다. 진단 실패는 이제 세션을 끊지 않는다. 다시 접속하면 Viewer 파일 영역은 초기화된다 |
| 표시 FPS가 낮음 | A 모니터 재생 빈도·Chrome 갱신률(rAF)을 먼저 확인한다 |

## 9. 현재 상태와 남은 과제

시험 전 계획(목적·단계·PASS/FAIL 판정·중단·정리 절차)은 `docs_working/`에 임시로 쓰고, 끝나면 결과를 이 절에 반영한다. 준비 완료나 프로세스 기동은 기능 PASS로 세지 않는다.

### 2026-10-23~24 실측 (A RTX 4060·30Hz 모니터, B Intel UHD + GTX 1660 Ti Max-Q, 관리자 모드)

| 항목 | 결과 |
|---|---|
| 화면 | PASS: 1920×1080, 디코딩 30fps, 약 4Mbps, 직접 P2P |
| 입력 | PASS: 영문·숫자, 한글 문자 입력, 창 드래그, 실제 키보드 빠른 입력, Ctrl+A |
| 관리자 창·UAC·Ctrl+Alt+Del | PASS: 하이브리드 GPU에 AdminHelper 절전 GPU 지정 후 |
| 재접속 | PASS: 서비스 재시작 뒤 같은 ID로 약 10초 |
| 즉시 종료·재개 | PASS |
| 원격 명령(SYSTEM/사용자)·Runbook | PASS |
| 클립보드·채팅 | PASS: 양방향 |
| 파일 | PASS: A→B 1MiB, B→A 512KiB, SHA256 일치(B→A는 진단 실패가 세션을 끊던 결함을 고친 뒤) |
| 송신 FPS(60초) | PASS: 1800프레임 = 30fps |
| 표시 FPS | 수용 판정 보류: 당시 A 모니터 30Hz. 60Hz 이상 환경에서 재측정 예정 |
| 입력-표시 지연 | FAIL: p50 188 / p95 306ms(기준 p95 ≤ 150ms). 보류 |
| 모니터 전환 | 미시험: 2모니터 B 없음 |

위 결과는 당시 시험 기록이며 현재 HEAD의 재검증 결과가 아니다. 이 요약에는 후보 commit·바이너리 hash·원시 측정 로그가 연결되어 있지 않으므로, 후속 시험은 해당 식별자와 증거 위치·실제 시작/종료 시각·정리 결과를 함께 기록한다.

### 남은 과제

- 입력-표시 지연: 사용자 결정으로 보류했다. 다시 다룰 때는 단계별 계측부터 한다. 후보는 관리자 모드 2단 캡처 주기(AdminHelper 30fps → Helper 30fps), 서비스 경유 입력 IPC, Viewer 지터 버퍼, A 화면 30Hz다.
- 표시 FPS: 60Hz 이상 A 화면에서 다시 잰다.
- 모니터 전환: 2모니터 대상 PC에서 확인한다.
- 저장 순간 진단 수집이 기한을 넘긴 원인: 미확인이다. 세션은 더 이상 끊기지 않는다.
