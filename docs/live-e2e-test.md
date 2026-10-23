# 두 PC 실연결(E2E) 시험 가이드

실제 두 Windows PC에서 제품 경로를 확인하는 방법이다. 단위 테스트·한 PC 루프백 검사(`scripts/loopback-check.ps1`)로 확인할 수 없는 것을 본다. 대상은 실제 화면 표시, 실제 입력 주입, UAC 보안 데스크톱, 두 PC 사이 성능이다. 2026-10-23~24에 이 방식으로 T14·T15를 수행했다. 결과는 `docs_working/`의 시험 기록에 있다.

시험 운영 원칙(사용자 재개 지시, 사전 계획, 짧은 화면 점유, 실패 시 즉시 중단·정리, 같은 시험 반복 금지)은 [AGENTS.md](../AGENTS.md)의 "실연결(E2E) 시험" 절이 기준이다. 이 문서는 그 원칙 아래의 **실행 방법**이다.

## 1. 구성과 역할

| 역할 | 장비 | 하는 일 |
|---|---|---|
| A 지원자 PC | `192.168.0.61` | 저장소, 빌드, 터널링 서버(TCP 25001), Chrome Viewer, 시험 진행(Claude Code 포함) |
| B 대상 PC | `192.168.0.74`, SSH 계정 `jacking75NoteBook` | 개발 설치, 사용자 세션의 Tray/Helper/AdminHelper |
| 사람 | A·B 앞 | 브라우저 인증서 예외 허용, 필요 시 B의 UAC 승인, 브라우저 저장 창 조작, 비상 시 B에서 즉시 종료 |

- 미디어·입력은 A와 B가 직접 P2P로 주고받는다. 터널링 서버는 A가 여는 접속 지점(Viewer 페이지 제공·첫 SDP/ICE 교환)일 뿐이다.
- 두 PC 모두 Windows PowerShell 5.1만으로 돈다. pwsh(PowerShell 7)는 필요 없다.

### 비밀 정보

- B SSH는 비밀번호 인증이다. 비밀번호는 A의 사용자 환경 변수에 두고, 스크립트에는 **변수 이름만** 넘긴다(`-PasswordEnv`). 값은 출력·문서·커밋에 남기지 않는다.
- 접속 정보 메모는 git 미추적 파일 `e2e-ssh.env`(`*.env` 무시)에 둔다. 호스트·계정·변수 이름만 적는다.
- `scripts/e2e-ssh.ps1`은 `SSH_ASKPASS` 도우미를 임시 폴더에 만든다. 도우미 안에도 변수 이름만 들어간다.

## 2. 준비 (화면 점유 없음)

```powershell
# 1) 준비 점검: .NET 10 SDK, Go(server/tunnel 기준 툴체인), Python cryptography, A LAN IP, 포트, B ping/SSH, 브라우저
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\e2e-preflight.ps1 -PasswordEnv <변수 이름>

# 2) 준비 자동화: TLS 인증서 → Host 후보 빌드 → B에 복사·검증·설치 → 터널링 서버 기동
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\e2e-prepare.ps1 -Install -StartTunnel -PasswordEnv <변수 이름>
```

`e2e-prepare.ps1`이 하는 일은 다음과 같다.
- `.artifacts\e2e-<시각>\`에 7일짜리 자체 서명 TLS 인증서를 만든다.
- 후보(`candidate.json`과 `payload\`)를 빌드한다.
- B의 `C:\ra-candidate`로 후보와 `install-host-dev.ps1`만 복사해 Validate와 Install을 실행한다.
- 터널링 서버를 고정 경로 `tunnel.exe`로 빌드해 새 창에서 띄운다. `go run`의 임시 exe는 실행할 때마다 방화벽 확인 창을 띄우므로 쓰지 않는다.

B 원격 명령은 `-EncodedCommand`로 보낸다. B의 SSH 기본 셸은 cmd이고, 5.1이 넘기는 따옴표는 cmd에서 깨진다.

### 준비 확인

- 터널링 서버: A와 B 양쪽에서 `curl -sk https://192.168.0.61:25001/healthz`가 `ok`이다.
- 서비스 경로: `HKLM\SYSTEM\CurrentControlSet\Services\RemoteAssistHost\ImagePath`가 따옴표로 감싼 경로다.
- 관리자 헬퍼 GPU 지정: `HKU\S-1-5-18\Software\Microsoft\DirectX\UserGpuPreferences`에 AdminHelper 경로가 `GpuPreference=1;`로 있다.

## 3. B에서 지원 시작

지원 시작은 대상자가 `RemoteAssist.Update.exe`를 관리자 권한으로 한 번 실행하는 것이다. 시험에서는 두 방법 중 하나를 쓴다.

- **사람이 UAC 승인**(제품 그대로): SSH로 B 사용자 세션에 일회성 작업을 등록해 `Start-Process 'C:\Program Files\RemoteAssist-Dev\RemoteAssist.Update.exe'`를 실행한다. B 화면에 뜬 UAC를 사람이 승인한다.
- **출력 확인용**: 같은 작업을 `-RunLevel Highest`로 등록하고, `cmd /c "...\RemoteAssist.Update.exe" > C:\ra-probe\update.txt 2>&1`로 실행한다. UAC 없이 관리자 권한으로 돌고, 성공하면 `DEVELOPMENT_SUPPORT_READY`가 남는다. 실패 사유도 남으므로 준비 문제를 진단할 때 이 방법을 쓴다.

일회성 작업 패턴(B에서 `-EncodedCommand`로 실행):

```powershell
$a=New-ScheduledTaskAction -Execute 'cmd.exe' -Argument '/c "C:\Program Files\RemoteAssist-Dev\RemoteAssist.Update.exe" > C:\ra-probe\update.txt 2>&1'
$p=New-ScheduledTaskPrincipal -UserId 'jacking75NoteBook' -LogonType Interactive -RunLevel Highest   # 사람 승인 방식이면 Limited
Register-ScheduledTask -TaskName 'RA-E2E-Launch' -Action $a -Principal $p -Force | Out-Null
Start-ScheduledTask -TaskName 'RA-E2E-Launch'   # 끝나면 Unregister-ScheduledTask로 지운다
```

확인 위치:
- 서비스 상태 기록: `C:\Program Files\RemoteAssist-Dev\service-status.ndjson`. `tray-ready`·`helper-launched`가 남는다. `helper-stopped`에는 종료 사유(`detail`)가 함께 남는다.
- 지원 ID: `C:\ProgramData\RemoteAssist\device-id.txt`.
- 터널링 서버 로그: Host `참가` 줄.

## 4. A에서 Viewer 접속

1. Chrome에서 `https://192.168.0.61:25001/`을 연다. 자체 서명 인증서 경고는 **사람이** "고급 → 이동"으로 넘긴다. 인증서를 새로 만들 때마다 다시 넘겨야 한다. 이 경고 화면은 브라우저 자동화가 붙지 못한다.
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
- 파일 제안은 **수락 30초** 안에 받아야 한다(protocol.md). Viewer 저장은 브라우저의 Windows 저장 창(`showSaveFilePicker`)이라 자동화가 조작하지 못한다.
- 그래서 A에서 파일을 **선택까지만** 해 두고, 사람이 "열기 → 받은 파일 저장 → 저장 위치 지정"을 한 번에 한다. 저장 위치(예: 다운로드)를 미리 정해 알려 준다.
- 판정은 A에 저장된 파일의 크기와 SHA256으로 한다.

## 6. 성능 측정 (T15)

B에 측정 대상 창을 띄운다. `scripts/e2e-motion-target.ps1`을 B로 복사한 뒤 SSH 일회성 작업(`Limited`, `-Sta`)으로 실행한다. 정해진 시간이 지나면 창이 스스로 닫힌다.

- `-Mode motion`: 막대가 계속 움직여 매 프레임 화면이 바뀐다. 정지 화면에서는 캡처 프레임이 거의 나오지 않으므로 FPS 측정에 필요하다.
- `-Mode latency`: 키를 누를 때마다 창 전체가 흑/백으로 반전된다.

### FPS (60초)

`scripts/browser-media-measure.js`와 같은 방식이다. Viewer 페이지에서 60초 동안 아래 값을 모은다.
- `getVideoPlaybackQuality()`의 `totalVideoFrames`(디코딩)와 `droppedVideoFrames`
- `requestVideoFrameCallback` 횟수(표시)

판정은 1920×1080에서 평균 30fps 이상이다. 표시 횟수는 A 화면 재생 빈도에 묶인다. `requestAnimationFrame` 횟수로 A의 실제 갱신률을 먼저 확인한다.

### 입력-표시 지연

1. latency 창을 Viewer 영상에서 한 번 클릭해 B 포커스를 준다.
2. 창 안쪽 영역을 영상 좌표로 구한다. 페이지 좌표 → `getBoundingClientRect`와 `object-fit: contain` 여백 보정 → `videoWidth/videoHeight`.
3. `scripts/browser-latency-measure.js`의 `measureInputToDisplay(region, 40)`를 실행한다. 약 30초 걸리고 결과는 `window.__lat`에 남는다.
4. 판정은 p95 150ms 이하다. 40회 모두 검출됐는지(`missed=0`), 흑/백이 번갈아 바뀌었는지 확인한다.

이 측정에는 Viewer 입력, 전송, 주입, 화면 갱신, 캡처, 인코딩, 전송, 디코딩, 표시가 모두 들어 있다. A 화면이 느리면(예: 30Hz) 표시 대기가 더해지므로 결과와 함께 A 갱신률을 적는다.

## 7. 정리 (매 시험 끝, 중단 때도)

1. Viewer 탭을 닫는다.
2. B 시험 창과 작업을 끝낸다. `RA-E2E-*` 작업을 Stop·Unregister한다.
3. B에서 `C:\ra-candidate\install-host-dev.ps1 -Action Remove`를 실행한다.
   - 지우는 것: 서비스, 설치 폴더, 방화벽 규칙, 관리자 헬퍼 GPU 지정, SAS 준비, 지원 허가.
   - 남는 것: 지원 ID·감사 기록(`C:\ProgramData\RemoteAssist`).
4. B 복사본과 시험 파일을 지운다: `C:\ra-candidate`, `C:\ra-probe`, 보낸 시험 파일.
5. 시험 데이터까지 완전히 지울 때는 아래도 지운다.
   - `C:\ProgramData\RemoteAssist*`
   - `%LOCALAPPDATA%\RemoteAssist`(B 사용자)
   - `C:\Windows\System32\config\systemprofile\AppData\Local\RemoteAssist`
   - `%USERPROFILE%\Downloads\RemoteAssist`
6. A에서 터널링 서버(`.artifacts\e2e-*\tunnel.exe`)를 끄고, `tls\` 키·`host-candidate\`·받은 시험 파일을 지운다.
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

## 9. 기록

시험마다 `docs_working/`에 계획과 결과를 남긴다.
- 계획: 목적, 단계, PASS/FAIL 판정, 중단·정리 절차를 시험 전에 적는다.
- 결과: 실제 시각, 화면 점유 시간, B GPU·드라이버, 단계별 결과·수치, 새 결함, 정리 결과를 적는다.

진행 상태는 `docs_working/구현계획-1.md`의 T14·T15, 요약은 `working_log.md`에 적는다. 준비 완료나 프로세스 기동은 기능 PASS로 세지 않는다.
