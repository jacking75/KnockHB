# 실행·로컬 검증 가이드

2026-10-08 기준이다. 완료 상태는 [구현계획](../원격제어-구현계획.md), 지원·수용 기준은 [제품 계약](product-contract.md)을 따른다. F01~F12 최종 두 PC 검증은 별도 세션에서 수행한다. 아래는 개발자 로컬 명령이며 제품 설치·운영 검증을 대신하지 않는다.

## 준비와 빌드

Windows x64, global.json의 .NET SDK 10.0.400, WindowsDesktop 구성요소, Go 1.27 toolchain, Node 22 이상과 WebRTC/H.264 브라우저가 필요하다. Go toolchain 자동 다운로드를 쓰면 네트워크와 캐시 권한이 필요하다. Viewer의 npm 설치·번들 단계는 없다.

```powershell
dotnet --info
go version
node --version
dotnet restore RemoteAssist.slnx --locked-mode
dotnet build RemoteAssist.slnx --no-restore
```

루트에서 로컬 회귀 전체를 실행하고 명령·종료 코드를 보존한다. 기존 Output 디렉터리는 덮어쓰지 않는다. CI를 생성하거나 실행하지 않는다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify-local.ps1
```

개별 실행은 다음과 같다.

```powershell
dotnet run --project tests/RemoteAssist.Host.Core.Tests
node --test web/viewer/tests/*.test.js
Push-Location server/signaling
go build ./...
go test ./...
go vet ./...
Pop-Location
```

Go race는 지원되는 C toolchain에서 go test -race ./...로 실행한다. 이번 머신에서는 Windows CGO toolchain이 없어 WSL Ubuntu의 Go 1.27/gcc에서 실행했다. WSL race 성공은 Windows 캡처·입력·SYSTEM 동작을 검증하지 않는다.

## 고정 FFmpeg 준비

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/prepare-ffmpeg.ps1
```

기존 third_party/ffmpeg/bin이 있으면 거부한다. 검증된 기존 공급본을 지우지 말고 -Destination으로 새 준비 경로를 선택해 비교한다. URL/SHA/ABI/DLL hash/빌드 옵션은 [공급 안내](../third_party/ffmpeg/README.md)를 따른다. 임의 PATH의 FFmpeg를 사용하지 않는다.

```powershell
dotnet run --project tools/RemoteAssist.HostRun -- --encoder-check
```

--encoder h264_nvenc 같은 지정은 그 후보만 검사한다. UNSUPPORTED·출력 0·디코딩 누락·처리량 미달·키프레임 복구 실패는 구분해서 보고한다. 기존 결과를 덮어쓰지 않으며 기본 결과는 .artifacts/encoders/날짜UTC에 남긴다. --out으로 새 디렉터리를 지정할 수 있다.

## 먼저 Windows 세션 확인

```powershell
dotnet run --project tools/RemoteAssist.SessionProbe
```

processSession과 consoleSession이 다르거나 inputDesktop이 null/desktopError=5이면 캡처·입력 실증을 하지 못하는 환경이다. 일반 대화형 사용자 세션에서 도구를 실행해야 한다. 이 진단은 자동 승격·세션 이동을 수행하지 않는다. E05의 SYSTEM/UAC/SAS 실증은 추가로 승인된 관리자·현장 사용자 환경이 필요하다.

## 로컬 화면 보기

터미널 A에서 루트 기준으로 다음을 실행한다.

```powershell
$env:RA_MODE = 'development'
$env:RA_LISTEN = '127.0.0.1:25001'
$env:RA_VIEWER_DIR = (Resolve-Path 'web/viewer').Path
Push-Location server/signaling
go run .
```

터미널 B에서 실제 사용자 desktop을 확인하고 실행한다.

```powershell
Invoke-WebRequest http://localhost:25001/healthz
dotnet run --project tools/RemoteAssist.HostRun -- --dev-loopback --signaling http://localhost:25001 --monitor 0 --fps 30 --bitrate 4000
```

브라우저에서 http://localhost:25001/을 열어 자기 신고 이름·코드·PIN을 입력한다. 대상자가 Host 콘솔의 지원자와 모니터를 확인하고 '화면 승인'을 입력해야 캡처한다. Ctrl+C로 종료한다. 거절·실패·상대 종료는 HostRun 결과와 종료 코드로 전달된다. 같은 코드로 재참가하지 말고 새 세션을 시작한다. 이는 Tray 동의 UI가 완성된 제품 경로가 아니다.

기존 평문 LAN 예시는 폐기했다. 다른 PC에서 접근하는 운영 경로는 RA_MODE=production, 실제 RA_TLS_CERT/RA_TLS_KEY, RA_TURN_URLS/RA_TURN_SECRET_FILE과 Viewer 자산을 준비해 HTTPS/WSS로 실행한다. 인증서 검사를 우회하는 옵션은 제공하지 않는다. 외부 TLS/TURN·두 PC 준비는 [준비 문서](../docs_working/e2e-test-preparation.md)를 따른다.

## 캡처 진단과 실제 브라우저 회귀

```powershell
dotnet run --project tools/RemoteAssist.CaptureDump -- --backend wgc --monitor 0 --frames 5 --out .artifacts/capture-wgc
dotnet run --project tools/RemoteAssist.CaptureDump -- --backend dxgi --monitor 0 --frames 5 --out .artifacts/capture-dxgi
```

PNG 내용·모니터·방향·해상도를 확인한다. 요청한 개수 미달은 실패다. --verbose는 stack trace를 추가한다. 캡처 파일에는 민감 화면이 있을 수 있으므로 저장소에 올리지 않는다. 동일 --out 결과를 덮어쓰지 않는다.

로컬 서버를 시작한 뒤 설치된 Playwright CLI로 독립 브라우저 세션을 연다.

```powershell
playwright-cli -s=knockhb open http://127.0.0.1:25001/ --browser chrome
playwright-cli -s=knockhb run-code --filename scripts/browser-smoke.js
playwright-cli -s=knockhb close
```

smoke는 없는 코드·잘못된 PIN·오류 보존·PIN 폐기·session UI 숨김·pageerror 0을 검사한다. 시험용 룸은 인증된 Host 슬롯으로 종료한다. 캡처·미디어·원격 입력을 시험하지 않는다.

## 현재 차단과 검증 한계

이번 자동화 세션은 processSession=1, consoleSession=3, inputDesktop 접근 거부였다. WGC monitor item 생성 실패와 DXGI output 부재로 실제 캡처가 차단됐다. 순수 C#/Go/Node 테스트와 synthetic 인코더 성공을 E05/E12/E13 또는 실제 두 PC 성공으로 바꾸지 않는다. 현재 증거는 [로컬 실행 기록](../docs_working/implementation-evidence-2026-10-08.md)에 있다.