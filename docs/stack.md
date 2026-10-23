# 기술 스택 · 버전 고정

> 버전을 올릴 때는 이 문서와 `Directory.Packages.props`를 함께 고친다. 현재 설정·상한은 [기술 사양](technical-specification.md)이 기준이다.

---

## 1. 언어 선택

영역마다 더 나은 쪽을 쓴다. 언어 통일보다 각 영역의 제약이 우선이다.

| 영역 | 선택 | 근거 |
|---|---|---|
| Host — 캡처·인코딩·입력·데스크톱 전환 | **C# / .NET 10** | WinRT(`Windows.Graphics.Capture`)를 CsWinRT로, DXGI/D3D11 COM을 `Vortice.Windows`로 바로 다룬다. `SendInput`·`OpenInputDesktop` P/Invoke, Windows 서비스 호스팅, Named Pipe가 모두 표준 제공. Go면 COM vtable을 손으로 짜거나 cgo 셰임이 필요해 **가장 어려운 부분이 더 어려워진다** |
| Host UI — 트레이·상태 창 | **C# / WPF** | Windows 데스크톱 UI 성숙도. Host 본체와 같은 언어라 IPC 계약을 공유하기 쉽다 |
| 터널링 서버 | **Go** | 하는 일이 "다수 WebSocket을 오래 붙들고 릴레이"뿐. goroutine 동시성, 단일 정적 바이너리, 작은 컨테이너 이미지가 그대로 이점 |
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
| Docker | **29.0.1** | 터널링 서버 컨테이너용 |
| Git | 2.52.0.windows.1 | |


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
| `github.com/coder/websocket` | **v1.8.15** | 터널링 서버 WebSocket | ✅ 적용됨 (T1.1). 유일한 외부 의존성 |

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

### 동봉 빌드

| 항목 | 값 |
|---|---|
| 빌드 | `ffmpeg-n8.1-latest-win64-lgpl-shared` (BtbN/FFmpeg-Builds) |
| 버전 | `n8.1.2-44-g7c533d0f86-20260824` |
| 위치 | `third_party/ffmpeg/bin` (**137MB**, 7개 DLL. 커밋하지 않는다) |
| 검증 | `--enable-gpl` 없음, `--disable-libx264` 확인 |
| H.264 인코더 | `libopenh264`, `h264_nvenc`, `h264_qsv`, `h264_amf`, `h264_mf`, `h264_d3d12va`, `h264_vaapi`, `h264_vulkan` |

> 시스템 PATH의 FFmpeg(대개 GPL·libx264 포함 빌드)는 쓰지 않는다. 동봉 DLL만 명시 경로에서 로드한다.

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
| 터널링 서버 (HTTP/WebSocket) | TCP **25001** |
| Host WebRTC (직접 P2P, host 후보만) | UDP **27002~27099** |

STUN/TURN 서버는 쓰지 않는다. 두 PC는 지정 IP로 항상 직접 연결된다.

