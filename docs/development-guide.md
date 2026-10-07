# 실행·검증·문제 해결 가이드

[문서 안내](README.md) · [기술 사양](technical-specification.md) · [리뷰 태스크](../docs_working/code-review-task-risks-2026-10-07.md)

## 1. 준비물과 검증 범위

아래 명령은 저장소 루트에서 시작하는 Windows PowerShell 기준이다. 명령 예시는 재현 절차이며 이번 문서 작업에서 전체 실행에 성공했다는 기록은 아니다.

필요한 것은 .NET 10 SDK와 Windows targeting/desktop 구성요소, `go.mod`의 Go 1.27 요구를 충족하는 toolchain, WebRTC/H.264 지원 브라우저, 프로젝트 요구에 맞는 FFmpeg shared DLL이다. Docker는 TURN/컨테이너 검증 단계에서 필요하다. Viewer 자체에는 npm 설치·번들 빌드 단계가 없다. Node는 JS 문법 검사나 모의 테스트에만 선택적으로 쓴다.

```powershell
dotnet --info
go version
node --version
```

SDK/toolchain이 자동 다운로드되는 환경은 네트워크와 캐시 쓰기 권한이 필요하다. 과거 스택 문서에 설치 완료라고 적혀 있어도 현재 머신의 설치 상태와 같다고 가정하지 않는다. 현재 앱은 MySQL을 사용하지 않으므로 DB 서비스 준비는 필요 없다.

이 PoC는 동의 UI가 없고 연결되면 바로 화면을 보낸다. 개발 참여자가 공유할 화면을 알고 있는 통제된 환경에서 실행한다. 일반 사용자 배포 전에는 G01의 동의·권한·TLS·종료 조건을 충족해야 한다.

## 2. 의존성과 빌드

### C# 솔루션

```powershell
dotnet restore RemoteAssist.slnx
dotnet build RemoteAssist.slnx --no-restore
```

솔루션에는 Protocol/Core/Service/Tray와 두 진단 도구가 포함된다. 컴파일 성공은 FFmpeg DLL을 올바르게 확보했다거나 GPU 인코딩이 된다는 뜻이 아니다. 네이티브 라이브러리는 런타임 준비가 따로 필요하다.

### Go 서버

```powershell
Push-Location server/signaling
go mod download
go build ./...
go test ./...
go vet ./...
Pop-Location
```

각 명령의 종료 코드를 확인한다. toolchain 다운로드 실패 후 다음 명령이 성공했다고 테스트가 실행된 것으로 기록하지 않는다. `go test -race ./...`는 지원되는 C toolchain/환경을 준비한 뒤 추가 실행한다. race detector는 join/leave 같은 논리적 경쟁을 자동으로 모두 찾지 못한다.

### Viewer

```powershell
node --check web/viewer/app.js
```

문법 검사만 수행한다. 연결 순서·이전 callback·실제 디코딩을 검증하지 않는다.

## 3. FFmpeg 배치

요구 버전·허용 빌드·동적 링크 정책은 [FFmpeg 안내](../third_party/ffmpeg/README.md)를 따른다. DLL은 저장소에서 제외되어 있으므로 checkout 직후 없을 수 있다.

```text
third_party/
  ffmpeg/
    README.md
    bin/
      avcodec-*.dll
      avutil-*.dll
      ...선택한 shared 빌드의 나머지 의존 DLL
```

`avcodec` 하나만 복사하면 되는 것이 아니다. 고정 ABI에 맞는 전체 의존 DLL 세트가 필요하다. 빌드 출처·버전·해시·옵션을 기록하고 GPL 활성화/libx264 포함 여부를 프로젝트 정책에 맞게 확인한다. 검증할 때도 시스템 PATH의 다른 FFmpeg를 실수로 검사하지 말고 확보한 빌드의 실행 파일·메타데이터를 명시한다.

```powershell
Test-Path third_party/ffmpeg/bin
Get-ChildItem third_party/ffmpeg/bin -Filter '*.dll'
dotnet run --project tools/RemoteAssist.HostRun -- --encoder-check
```

인코더 도구의 현재 한계(R01/R17)를 함께 확인한다. 후보 목록 “사용 가능”은 등록 이름 확인에 가깝고, 실제 Configure/Encode 검사는 자동 선택 후보 하나에만 수행된다. 출력 프레임 수와 실패 로그를 확인해야 한다.

## 4. 로컬 화면 공유 PoC

### 터미널 A: 서버와 Viewer 파일 제공

저장소 루트에서 다음을 실행한다. `RA_VIEWER_DIR`는 절대 경로로 계산하므로 이후 작업 디렉터리를 바꿔도 올바른 폴더를 가리킨다.

```powershell
$env:RA_LISTEN = '127.0.0.1:25001'
$env:RA_VIEWER_DIR = (Resolve-Path 'web/viewer').Path
Push-Location server/signaling
go run .
```

서버 중단은 Ctrl+C다. 중단 후 같은 터미널에서 `Pop-Location`으로 돌아온다. 환경 변수는 해당 터미널 프로세스와 자식 프로세스에 적용된다.

### 터미널 B: 상태와 화면 공유

```powershell
Invoke-WebRequest http://localhost:25001/healthz
dotnet run --project tools/RemoteAssist.CaptureDump -- --list
dotnet run --project tools/RemoteAssist.HostRun -- --signaling http://localhost:25001 --monitor 0 --fps 30 --bitrate 4000
```

HostRun이 출력한 코드/PIN을 준비한다. 브라우저에서 `http://localhost:25001/`을 열고 입력한다. 정적 파일을 `file://`로 직접 여는 방식은 ES module/Origin 정책 차이가 있으므로 위 HTTP 서빙 절차를 사용한다.

화면 표시 후 실제 선택된 인코더 로그, video의 해상도/FPS, 화면 내용이 맞는지 확인한다. 브라우저가 로컬 화면 안에 포함되어 무한 거울처럼 보이는 것은 루프백 화면 공유의 자연스러운 결과일 수 있다.

현재 Viewer 종료 후 HostRun은 Ctrl+C를 기다리며 남을 수 있고 동일 인스턴스의 재접속은 정상 복구되지 않을 수 있다. 재시험은 HostRun을 종료하고 새 코드로 시작하되, 이것을 R07/R08이 해결된 것으로 기록하지 않는다.

## 5. 캡처만 확인하기

```powershell
dotnet run --project tools/RemoteAssist.CaptureDump -- --backend wgc --monitor 0 --frames 5 --out .artifacts/capture-wgc
dotnet run --project tools/RemoteAssist.CaptureDump -- --backend dxgi --monitor 0 --frames 5 --out .artifacts/capture-dxgi
```

한 번에 하나씩 실행하고 저장된 PNG의 내용·방향·크기·모니터를 확인한다. 이 도구는 사용자 화면을 실제 파일로 저장하므로 진단 후 산출물의 민감정보 여부도 확인한다. 앱 미디어를 서버가 저장하지 않는다는 설명과 진단 도구의 명시적 PNG 저장은 별개다.

CaptureDump는 일부 프레임만 저장해도 성공 코드가 나올 수 있다. 요청한 수와 실제 파일 수를 직접 비교한다. 현재 WGC/DXGI는 같은 인덱스가 다른 출력을 가리킬 수 있으므로 각 PNG가 의도한 모니터인지 확인한다(R03).

## 6. 두 머신 검증 구성

실제 장비를 준비할 때는 [두 PC E2E 사전 준비 체크리스트](../docs_working/e2e-test-preparation.md)를 함께 따른다. PC별 설치 항목, SSH와 대화형 사용자 세션의 차이, 방화벽·로그 수집·준비 완료 조건을 정리했다.

최소 구성은 대상 PC A에 HostRun과 시그널링 서버를 두고 지원자 PC B에 브라우저만 두는 형태다. 시그널링은 별도 서버 C로 옮길 수도 있다.

1. A의 접근 가능한 LAN 주소에 `RA_LISTEN`을 바인딩한다. 예: `192.168.10.20:25001`이며 실제 주소로 바꾼다.
2. 허용된 개발 네트워크에서 TCP 25001과 WebRTC 통신의 접근 조건을 확인한다. 이 가이드는 방화벽 규칙을 자동 변경하지 않는다.
3. A의 HostRun을 `--signaling http://192.168.10.20:25001`로 시작한다.
4. B의 브라우저에서 `http://192.168.10.20:25001/`을 연다. B의 localhost는 A를 가리키지 않는다.
5. 참여자가 합의한 방식으로 코드/PIN을 전달하고 연결 결과를 기록한다.

HTTP LAN 구성은 개발용이다. 운영 시나리오의 TLS/WSS 통과로 간주하지 않는다. 로컬 WebRTC 포트 범위를 코드에서 아직 제한하지 않으므로 TCP 25001만 열면 영상까지 반드시 된다고 보장할 수 없다. 지원 포트 정책과 ICE 구성을 정리하는 작업은 별도로 필요하다.

## 7. 증상별 확인 순서

| 증상 | 먼저 확인할 것 | 관련 코드/태스크 |
|---|---|---|
| SDK/모듈 다운로드 실패 | 요구 버전, 네트워크, 프록시, 캐시 쓰기 권한 | G06 |
| `/healthz` 접속 실패 | RA_LISTEN, 프로세스 실행, 대상 주소 | `main.go` |
| Viewer 페이지 404 | RA_VIEWER_DIR 절대 경로와 index.html | `main.go` |
| WS 참가 거절 | Origin, 코드 만료, PIN, 슬롯 점유 | `server.go`, R16 |
| 참가 때 예상치 못한 응답 | 동시 join ACK 순서 | R05 |
| 연결 중 상태에서 진행 안 됨 | offer/answer와 ICE 순서, 직접 경로 가능 여부 | R06/G04 |
| connected인데 검은 화면 | 캡처/인코더 예외, 네이티브 DLL, 실제 출력 | R01/R07 |
| 다른 모니터가 보임 | backend별 monitor index | R03 |
| 크기 변경 후 깨짐/정지 | 프레임 포맷과 인코더 크기 | R02 |
| 일시 단절 후 화면 안 돌아옴 | 취소된 CTS와 남은 캡처 스레드 참조 | R08 |
| 접속 연타 후 이상 동작 | 이전 소켓·PC callback의 전역 상태 접근 | R09 |
| 종료가 오래 걸림 | 캡처 Join, WS close 응답, 작업 소유권 | R11 |
| Service/Tray 실행해도 공유 없음 | 현재 두 프로젝트는 골격 | T2.4 |

PIN/Host 토큰이 들어 있는 로그를 그대로 공유하지 않는다. 현재 Core의 PIN 로그 기록은 R15에서 제거할 대상이다.

## 8. 자동 검증과 E2E를 나누는 기준

| 한 머신에서 결정적으로 검증할 것 | 실제 환경이 필요한 것 |
|---|---|
| JSON 필드·UTF-8 분할·잘못된 메시지 | 실제 브라우저 SDP/H.264 상호운용 |
| 룸 join/leave/sweep 순서 경쟁 | NAT·방화벽·TURN 경로 |
| 가짜 캡처/인코더의 예외·포맷 변경 | GPU/드라이버별 캡처와 인코더 초기화 |
| Viewer 늦은 이벤트와 teardown | 두 머신 영상 표시·지연·장기 실행 |
| 권한 상태 머신·철회 우선순위 | Windows 서비스·세션·UAC·SAS |
| 좌표 계산·입력 상태 복구 | 실제 DPI·회전·다중 모니터 입력 |

다음 매트릭스는 앞으로 남길 검증 기록의 틀이다. 미구현 기능의 행은 해당 Phase에서 실행한다.

| 축 | 필수 경우 |
|---|---|
| 머신 | 서로 다른 2대, 다른 GPU, HW 인코더 없는 VM |
| 네트워크 | 같은 LAN, 다른 NAT, 직접 연결 차단 후 TURN, 일시 단절/복구 |
| 화면 | 가로/세로, 해상도 변경, 음수 좌표 모니터, 다른 DPI |
| 권한 | 일반 앱, 승격 앱, UAC, 잠금화면, 콘솔/RDP/사용자 전환 |
| 보안 | 미승인, 일부 기능 승인, 철회, 코드 만료/재사용, 인증 반복 실패 |
| 장애 | 서버 종료, 캡처/인코더 실패, 모니터 제거, 응답 없는 피어 |

각 실행에는 commit, OS/드라이버/GPU, 브라우저, FFmpeg·인코더, 네트워크·선택 candidate pair, 재현 절차, 결과를 기록한다. 성능 수치는 FPS·지연·비트레이트의 측정 위치와 시간을 함께 남긴다. 설정값이나 추정값을 실측으로 쓰지 않는다.

## 9. 이번 문서 기준의 확인 한계

2026-10-07 리뷰에서는 Viewer JS 문법 검사와 일부 모의 이벤트 검증을 수행했다. C# 빌드는 자산 파일 부재 및 NuGet 네트워크 제한, Go 테스트는 요구 toolchain 다운로드 제한으로 완료하지 못했다. 네이티브 FFmpeg 바이너리와 두 머신 E2E도 이번 리뷰에서 확인하지 않았다.

따라서 이 문서의 실행 절차는 코드에서 도출한 절차다. 전체 환경 준비와 실제 실행 결과는 별도 기록해야 한다. 문서 링크·다이어그램 검증과 앱의 기능 검증을 같은 것으로 취급하지 않는다.
