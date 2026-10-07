# 기술 스택 · 버전 고정

> **확인일 2026-08-24.** 아래 버전은 이날 실제로 조회·빌드해 확인한 값이다.
> 버전을 올릴 때는 이 문서와 `Directory.Packages.props`를 함께 고친다.

> **기록의 범위 (2026-10-07 보충):** 아래 설치·측정·배치 완료 표는 2026-08-24 환경의 기록이다.
> 현재 checkout의 도구 설치나 FFmpeg 바이너리 존재, 다른 GPU의 인코딩 성공을 보증하지 않는다.
> 현재 사양은 [기술 사양](technical-specification.md), 준비 절차는 [실행 가이드](development-guide.md)를 따른다.
> 특히 인코더별 “사용 가능”과 전체 DoD는 리뷰 R01/R17/G06에 따라 재검증해야 한다.

---

## 1. 언어 선택

영역마다 더 나은 쪽을 쓴다. 언어 통일보다 각 영역의 제약이 우선이다.

| 영역 | 선택 | 근거 |
|---|---|---|
| Host — 캡처·인코딩·입력·데스크톱 전환 | **C# / .NET 10** | WinRT(`Windows.Graphics.Capture`)를 CsWinRT로, DXGI/D3D11 COM을 `Vortice.Windows`로 바로 다룬다. `SendInput`·`OpenInputDesktop` P/Invoke, Windows 서비스 호스팅, Named Pipe가 모두 표준 제공. Go면 COM vtable을 손으로 짜거나 cgo 셰임이 필요해 **가장 어려운 부분이 더 어려워진다** |
| Host UI — 트레이·동의 창 | **C# / WPF** | Windows 데스크톱 UI 성숙도. Host 본체와 같은 언어라 IPC 계약을 공유하기 쉽다 |
| 시그널링 서버 | **Go** | 하는 일이 "다수 WebSocket을 오래 붙들고 릴레이"뿐. goroutine 동시성, 단일 정적 바이너리, 작은 컨테이너 이미지가 그대로 이점 |
| TURN | **coturn** | 언어 무관. 직접 구현하지 않는다 |
| Viewer | **웹 (바닐라 JS)** | 브라우저 내장 WebRTC. 지원자 측 설치 불필요가 최대 도입 이점 |

**두 언어를 쓰는 대가.** 공유 DTO를 코드로 나눌 수 없다. 와이어 포맷의 기준은 코드가 아니라
[`docs/protocol.md`](protocol.md) 문서 하나이며, C#·Go·JS가 각자 그것을 구현한다.
스키마 변경은 **항상 문서 → 양쪽 코드** 순서.

---

## 2. 개발 도구 (설치 확인됨)

| 도구 | 확인된 버전 | 비고 |
|---|---|---|
| .NET SDK | **10.0.400** | `Microsoft.WindowsDesktop.App 10.0.11` 동반 확인 (WPF 필요) |
| Go | **1.27.0** windows/amd64 | |
| Node.js / npm | **24.18.0** / 11.16.0 | 웹 뷰어용 |
| Docker | **29.0.1** | coturn·시그널링 컨테이너용 |
| Git | 2.52.0.windows.1 | |

> **미완료:** 테스트 머신 2대(대상 PC / 지원자 PC). LAN P2P·서비스 설치 검증에 필요하다. VM 2대로 대체 가능.

---

## 3. NuGet 패키지

버전은 `Directory.Packages.props` 에서 **중앙 관리**한다(CPM). 개별 csproj에 버전을 적지 않는다.

| 패키지 | 고정 버전 | 용도 | 비고 |
|---|---|---|---|
| `SIPSorcery` | **10.0.16** | WebRTC PeerConnection·DataChannel·MediaTrack | `TargetFrameworks`에 **`net10.0` 명시 지원**. 실제 빌드로 확인 완료 |
| `SIPSorceryMedia.FFmpeg` | **10.0.16** | H.264 인코딩 | **LGPL-2.1-only.** 네이티브 FFmpeg 8.1 shared lib를 별도 동봉해야 한다 → [`third_party/ffmpeg/README.md`](../third_party/ffmpeg/README.md) |
| `Vortice.Direct3D11` | **3.8.3** | DXGI Desktop Duplication / D3D11 | `SharpDX`는 유지보수 종료라 쓰지 않는다 |
| `Vortice.DXGI` | **3.8.3** | 위와 동반 | |
| `Microsoft.Extensions.Hosting` | **10.0.11** | 서비스 호스팅 | |
| `Microsoft.Extensions.Hosting.WindowsServices` | **10.0.11** | Windows 서비스 등록 | |

### 쓰지 않기로 한 것

| 패키지 | 이유 |
|---|---|
| `SIPSorceryMedia.Encoders` | **deprecated** — "legacy이고 더 이상 유지보수하지 않음". 벤더가 FFmpeg를 대안으로 안내 |
| `SIPSorcery.VP8` | 순수 C# VP8이지만 벤더가 **"experimental이고 1080p 이상에서 성능이 나쁘다"** 고 명시. 화면 공유는 1080p 이상이 기본이라 부적합 |
| `SIPSorceryMedia.Windows` | **웹캠·마이크용**이다. 화면 캡처와 무관하므로 이름 때문에 혼동하지 말 것 |
| `SharpDX` | 유지보수 종료 |

---

## 4. Go 모듈

| 모듈 | 버전 | 용도 | 상태 |
|---|---|---|---|
| `github.com/coder/websocket` | **v1.8.15** | 시그널링 WebSocket | ✅ 적용됨 (T1.1). 유일한 외부 의존성 |
| `github.com/gorilla/websocket` | v1.5.3 | 대안 | 위가 안 맞을 때만 |

---

## 5. FFmpeg (동봉 대상)

경로 A(`SIPSorceryMedia.FFmpeg`)로 확정했으므로 아래 조건이 **전부** 따라붙는다.
자세한 내용과 준수 체크리스트는 [`third_party/ffmpeg/README.md`](../third_party/ffmpeg/README.md).

| 항목 | 값 |
|---|---|
| ABI | **FFmpeg 8.1** (`FFmpeg.AutoGen 8.1.0` 타깃). 어긋나면 PInvoke 로드 에러 |
| 빌드 | **LGPL 빌드만.** `--enable-gpl` 금지 |
| 금지 | **libx264 포함 빌드 금지** — GPL이라 제품 전체가 GPL 조건에 걸린다 |
| H.264 인코더 | `h264_nvenc` → `h264_qsv` → `h264_amf` → `h264_mf`, 없으면 `libopenh264` |
| 링크 | 동적 링크, 별도 DLL 유지. **단일 실행파일로 합치지 않는다** |

### 배치 완료 (2026-08-24)

| 항목 | 값 |
|---|---|
| 빌드 | `ffmpeg-n8.1-latest-win64-lgpl-shared` (BtbN/FFmpeg-Builds) |
| 버전 | `n8.1.2-44-g7c533d0f86-20260824` |
| 위치 | `third_party/ffmpeg/bin` (**137MB**, 7개 DLL. 커밋하지 않는다) |
| 검증 | `--enable-gpl` 없음, `--disable-libx264` 확인 |
| H.264 인코더 | `libopenh264`, `h264_nvenc`, `h264_qsv`, `h264_amf`, `h264_mf`, `h264_d3d12va`, `h264_vaapi`, `h264_vulkan` |

> **설치본 용량 주의.** DLL만 137MB다. 필요 없는 코덱을 뺀 최소 빌드를 T6.1에서 검토한다.
> 참고로 이 개발 머신에 `winget`으로 깔려 있던 FFmpeg는 `--enable-gpl --enable-libx264` + static 이라
> **동봉하면 안 되는 빌드의 전형**이었다. 시스템 FFmpeg에 의존하지 않는 이유가 이것이다.

---

## 6. 타깃 프레임워크 정책

`Directory.Build.props` 의 `$(WindowsTfm)` 하나로 관리한다.

| 프로젝트 | TFM | 이유 |
|---|---|---|
| `RemoteAssist.Protocol` | `net10.0` | 플랫폼 중립. Windows 전용 API를 넣지 않는다 |
| `RemoteAssist.Host.Core` | `net10.0-windows10.0.19041.0` | WinRT·DXGI |
| `RemoteAssist.Host.Service` | `net10.0-windows10.0.19041.0` | 서비스 + 캡처·입력 |
| `RemoteAssist.Host.Tray` | `net10.0-windows10.0.19041.0` | WPF |

`SupportedOSPlatformVersion` 은 **10.0.19041.0**(Windows 10 2004). WGC의 `Direct3D11CaptureFramePool.CreateFreeThreaded` 가 1903+ 를 요구하고, 그보다 낮은 Windows 10은 이미 지원 종료라 하한을 여기로 잡았다.

> 솔루션 파일은 .NET 10 기본값인 **`RemoteAssist.slnx`**(새 XML 형식)다. `.sln`이 아니다.

---

## 7. 포트 할당

전역 개발 포트 대역을 지킨다 — **TCP 25001~25199 / UDP 27001~27199**.

| 용도 | 포트 |
|---|---|
| 시그널링 서버 (HTTP/WebSocket) | TCP **25001** |
| coturn STUN/TURN listening | UDP **27001** |
| coturn 릴레이 포트 범위 | UDP **27100~27199** |

---

## 8. 검증 상태

| 항목 | 결과 |
|---|---|
| `dotnet build RemoteAssist.slnx` | ✅ 성공 — 경고 0, 오류 0 |
| SIPSorcery 10.0.16 on `net10.0-windows` | ✅ 복원·컴파일 확인 (.NET 10 지원 실증) |
| `go build ./...` (`server/signaling`) | ✅ 성공 |
| `go vet ./...` · `gofmt -l` | ✅ 지적사항 없음 |
| `go test ./...` | ✅ 10개 통과 (시그널링 릴레이·룸 정리·인증·고루틴 누수) |
| 시그널링 서버 실동작 | ✅ TCP 25001에서 `/healthz`·`POST /session` 확인 |
| 화면 캡처 (WGC) | ✅ 1920x1080 PNG 덤프 확인 (`tools/RemoteAssist.CaptureDump`) |
| 화면 캡처 (DXGI) | ❌ **이 개발 머신에서 미지원** — 하이브리드 GPU(Intel UHD + NVIDIA GTX 1660 Ti Max-Q)에서 `DXGI_ERROR_UNSUPPORTED`. 폴백 경로로만 유지하며, 단일 GPU 머신에서 별도 검증 필요 |
| H.264 인코딩 | ✅ 1080p **97.8fps**, 출력 지연 0프레임 (`h264_nvenc`). 5개 인코더 모두 사용 가능, libx264는 선택 불가 |
| WebRTC 종단간 | ✅ Host → 브라우저 뷰어 1920x1080 표시, 경로 **P2P**, ~17fps / 1.2~3.2Mbps |
| 파이프라인 처리량 | ⚠️ 목표 20fps 대비 **실측 ~17fps**. 인코더는 97fps를 내므로 병목은 BGRA→I420 변환과 프레임당 8MB CPU 복사다. T6.3에서 다룬다 |
| 다중 머신 검증 | ❌ **미실시** — 지금까지 전부 한 대에서 루프백. LAN P2P·NAT 통과는 T0.1의 머신 2대 확보 후 |
| `go test -race` | ⚠️ **미실행** — 이 머신에 gcc가 없어 cgo가 안 된다. CI(리눅스) 또는 golang 도커 이미지에서 돌려야 한다 |
