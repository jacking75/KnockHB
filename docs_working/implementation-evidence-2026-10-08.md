# 구현 기반의 로컬 실행 증거 — 2026-10-08

## 13:44 KST 채팅·클립보드·진단 연결과 코드 리뷰

`1a71587` 뒤 구현에서 chat/clipboard/diagnostics 독립 권한을 Service→Tray→Helper→Viewer에 연결했다. 화면 없는 진단 승인은 video track·캡처·인코더를 만들지 않는 분기로 구현했고, data-ready도 monitor 없이 control/telemetry만 협상한다. 일반 사용자 진단은 CPU/메모리/고정 디스크/활성 인터페이스 누적 값과 이벤트 ID/시작 항목 이름/서비스 상태만 읽는다. 이벤트 본문·IP/MAC·명령줄은 수집하지 않는다. 항목 실패는 null/N/A, 지연은 2초 deadline 후 세션 종료이며 같은 지연 호출을 반복 생성하지 않는다.

보조 채널에는 양방향 세션·권한 generation·채널별 순번·소문자 GUID 요청 ID·큐/빈도/크기 제한을 적용했다. 클립보드는 명시적 읽기/전송만 수행하고 SHA-256·방향·ID·2초 응답을 검사한다. 코드 리뷰에서 Unicode JSON escaping의 크기 증가, hash 계산 뒤 취소, 수집 중 권한 철회, 이벤트 ID 16비트 절단, 서비스 객체 전체 Dispose, 로컬 ACK 처리와 clipboard 대기의 충돌을 점검·수정했다. IPC actor가 clipboard 응답을 기다리며 복원 ACK를 막지 않도록 별도 대기 객체를 사용한다.

전체 C# 빌드와 29개 회귀 그룹, Viewer 34개 검사, Go 전체 검사가 통과했다. 이후 추가한 JSON escaping 회귀와 UTC 검사는 다음 체크포인트에서 확인한다. 화면·입력·UAC·잠금·서비스 설치·브라우저를 사용하는 E2E는 추가 실행하지 않았다. 실제 WPF clipboard 왕복·새 UI·진단 전용 SDP/수신의 제품 실증은 마지막 짧은 시험 항목이며 E32/E33을 아직 완료 체크하지 않는다.

13:55~13:56 KST 비대화형 추가 검사에서는 전체 solution 경고/오류 0개, C# 30개 그룹·Viewer 35개 시험이 통과했다. E31의 파일 DTO/순번·size/hash 경계와 Windows x64의 handle-relative NTFS 저장 기반을 추가했다. 원래 rename 설계는 DeleteOnClose를 해제할 수 없으므로 같은 폴더 안에 덮어쓰기 없는 hard link를 확정하는 방식으로 기준 문서를 먼저 변경했다. 임시 파일만 OS DeleteOnClose로 지워지고 확정한 이름은 남는다. 실제 로컬 NTFS에서 0바이트·16387바이트 파일, 저장 충돌, hash 변조, 중복/유실 청크, 철회, 파일 write 공유 거부, 임시 링크 제거를 확인했다. 디스크 full은 주입된 Stream 오류이며 실제 디스크를 채우지 않았다. UI/브라우저/서비스를 실행하지 않았고, 파일 채널·양방향 UI·감사 통합·reparse 경합·강제 종료 실증은 아직 남아 E31을 미체크로 유지한다.

핸들 API 판단 근거는 [CreateFile](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilea), [FILE_DISPOSITION_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_disposition_info), [FILE_LINK_INFORMATION](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/ns-ntifs-_file_link_information)이다. 일반 파일 disposition 변경으로 DeleteOnClose를 취소할 수 없다는 조건을 반영했다.

## 12:40 KST 검증 방침 변경과 입력 통합 증거

13:14 KST까지 추가 대화형 시험 없이 관리자 제품 경로를 구현했다. 최초 admin 토글은 요청 범위이며 30초 기한의 2차 로컬 승인과 최대 2분 lease가 실제 특권 Helper를 만든다. Helper는 네트워크 없이 shared BGRA와 고정 native input만 처리한다. 보안 desktop의 고정 종료 창·F9·30초 제한, Session/SID/monitor/세대 최종 검사, 서비스 SAS 정책 진단을 연결했다. 코드 리뷰에서 원격으로 2차 승인 창을 클릭할 위험을 발견해 해당 창을 열기 전에 입력을 중단·해제하고 응답 뒤 epoch를 증가시켰다. 이 경계의 allow/deny·stale epoch와 shared mapping 크기/세대/clear 회귀를 포함해 C# 28개 그룹과 Node 29개 시험이 통과했다. 전체 solution은 경고/오류 0개다. 실제 새 관리자 실행·Winlogon 종료 표시·Global mapping ACL·SAS 전환은 미실행이다. 이 상태를 E25/E26 완료로 처리하지 않는다.

사용자는 화면·입력 시험의 불편을 이유로 구현과 코드 리뷰를 우선하고 추가 E2E를 마지막으로 미루도록 지시했다. 모든 시험 브라우저·격리 입력 창·임시 제품 서비스와 보호 배치를 정리했다. 이후에는 비대화형 검사와 코드 리뷰를 수행한다. 아래는 방침 변경 전의 기록이며 추가 E2E의 실행 승인을 뜻하지 않는다.

제품 경로에 control/input/pointer 채널, reset ACK, 독립 SYSTEM up-only 복원 감독을 연결했다. `.artifacts/product-20261008-120820`에서 실제 Chrome→일반 Helper→소유 시험 창의 클릭 1회·F12 down/up 각각 1회와 Unicode `한글 검증` 수신을 확인했다. composition 이벤트는 합성했으므로 실제 IME 조작 전체의 수용 증거는 아니다. 원격 Ctrl down 수신과 foreground/held 상태를 확인한 뒤 Helper 강제 종료→Ctrl up/held=false/Helper 소멸은 287.2731ms였다.

`.artifacts/product-20261008-122658`에서 공유 테두리·모니터 안의 종료 창·제품 제어 상태를 확인했다. 동일 PC의 headed Viewer는 로컬 포커스 이동이 원격 reset을 발생시켜 보류 키 시험과 충돌했다. headless Viewer와 고정 격리 .NET WPF 시험 창으로 준비 완료를 확인한 뒤 서비스 강제 종료를 수행했다. 실제 서비스 종료 기록→Ctrl up은 137.0782ms, 마커 요청→복원 관찰은 298.8769ms였다. cleanup.json의 serviceAbsent/stageAbsent=true와 소유 제품 프로세스·시험 창의 종료를 확인했다.

앞선 `-122018` 서비스 종료 시도는 fault 시점의 CIM 조회가 측정에 포함돼 2초 제한을 넘었다. 복원은 관찰했으나 지연 수용 PASS로 처리하지 않는다. PowerShell WPF 시험 창의 문화권·파일 교체 오류도 발견했다. WPF 프로젝트에서 invariant globalization을 해제하고 snapshot 파일 교체 충돌을 재시도하는 .NET InputTarget으로 정리했다. 기존 PS 구현은 해당 도구 실행기로 교체했다. 실제 관리자 제품 경로·복원 lease 만료·종료 단축키·전체 입력 조합의 실증은 미실행이다.

코드 리뷰에서 이전 Recovery의 지연 종료 callback을 현재 Helper scope와 대조하도록 제한했고, IPC ACK 대기 상한을 원자적으로 적용했다. 포인터 캡처 실패 시 down을 보내지 않고 reset하며, Viewer 필수 모듈 누락을 서버 시작 전에 검사한다. 이 증거를 E21~E24 전체 완료나 F 통과로 승계하지 않는다.

## 후속 활성 콘솔 실증

### 제품 서비스·Tray의 실제 통합 시험

로컬 checkpoint `893565e` 이후 변경을 포함한 `.artifacts/product-20261008-105827` 시험에서 보호된 92개 파일 배치와 `RemoteAssistHost` SYSTEM 서비스→Session 1 일반 권한 WPF Tray/Helper의 인증 연결을 확인했다. 사용자 UI의 시작 버튼으로만 일회성 코드/PIN을 만들었다. 첫 실행 실패는 PowerShell UTF-8 BOM 설정 파서와 SYSTEM 프로세스 조회 권한 문제였고 각각 회귀 시험과 SCM 기반 서비스 신원 검증으로 수정했다. 클라이언트는 커널 pipe PID/Session, SCM 서비스 PID/LocalSystem/고정 보호 경로, nonce/역할을 확인한다. 일반 사용자 권한을 승격하거나 SYSTEM 프로세스 조회 권한을 넓히지 않았다.

브라우저 참가 후 실제 대상자 UI의 로컬 동의 대기 상태와 videoWidth=0을 확인했다. 거부 버튼을 누르자 영상은 0으로 유지됐고 UI 코드/PIN이 지워져 새 코드 발급 버튼이 활성화됐다. 새 코드로 `화면 보기`만 승인한 뒤 Chrome에서 1920×1012, readyState=4, 실제 decoded 증가와 P2P 경로를 확인했다. 표시 26fps의 한 시점 통계는 60초 성능 수용으로 처리하지 않는다.

UIAutomation에서 실제 종료 버튼의 IsOffscreen=false와 창 내부 배치를 확인했다. 즉시 종료 버튼 호출부터 소유 Helper 프로세스 소멸까지 185.0825ms였고 브라우저 영상은 0으로 해제됐다. 새로 승인한 세션에서 Tray 강제 종료부터 Helper 소멸까지 228.4242ms였다. cleanup.json의 serviceAbsent/stageAbsent=true와 브라우저 연결 종료를 확인했다. 원본 화면·코드/PIN·부트스트랩은 ignored artifact에만 둔다.

C# 로컬 회귀 21개 그룹이 통과했다. 실제 Windows pipe의 PID/Session 조회, UTF-8 단편·순번 재생·잘린 프레임·nonce 불일치와 동의 actor가 막힌 동안 3회의 heartbeat/긴급 종료가 처리되는 시험을 포함한다. 권한별 후속 기능·UI 만료·전체 장애 주입이 아직 남아 있으므로 E14~E17/E24 전체 완료로 처리하지 않는다.

### 경계와 미디어 기반 실증

후속 제품 시험 `.artifacts/product-20261008-111114`에서는 동의 만료와 동의 창 닫기를 검사했다. 모두 영상 크기 0, 코드/PIN 해제, 새 코드 발급 가능 상태로 종료됐다. 승인 뒤 UAC를 열었을 때 일반 권한 화면 공유가 종료돼 보안 데스크톱의 송신을 차단했다. 주입 전후의 native Default input desktop 확인과 active 콘솔/protocol/SID 검사를 추가했다. 서비스와 배치는 cleanup.json에서 제거를 확인했다.

입력 기반은 필수 inputEpoch·채널별 독립 순번·reset barrier·정규 좌표 변환·권한 실행 게이트·성공한 key/button/Unicode prefix 추적으로 보완했다. `.artifacts/boundary-20261008-113109`에서 이 제품 InputSession/WindowsInputBackend를 실제 일반/SYSTEM 소유 시험 창에 적용했다. 두 경로 모두 mouseSent=5/keySent=2, 실제 clicks=1/keys=1, success=true를 확인했다. 클릭 전이마다 자체 최종 위치를 적용하므로 기존 primitive probe의 mouseSent=3과 구분한다. 시험 서비스/배치는 제거됐다. 브라우저 데이터채널과 강제 종료 시 키 복원 감독은 아직 연결하지 않았으며 이 결과를 E23 완료로 처리하지 않는다.

추가 C# 회귀는 24개 그룹, Node 회귀는 21개가 통과했다. pointer가 먼저 도착해도 reliable button을 버리지 않는 순서, reset 이전 pointer 폐기, 음수 원점/세로 모니터, 거부·철회·partial Unicode down의 up 복원, blocked actor와 무관한 IPC 중단 감독을 포함한다. 전체 solution 빌드는 경고/오류 0개였다.

후속 작업의 기준 HEAD는 로컬 commit `96a621e530b813e6537d1d8bd036f007e5e316c3`이며 아래 결과는 그 위의 변경을 포함한다. 09:28 KST부터 processSession=consoleSession=1, Default input desktop, 실제 DXGI 출력과 두 모니터를 확인했다. 이전 세션의 환경 차단은 이 시점에 해소됐다.

WGC 4096×2160와 DXGI 세로 2160×3840의 PNG를 각각 2장 얻어 방향을 확인했다. 화면 원본은 ignored `.artifacts/local-console-20261008`에만 두며 저장소에 추가하지 않는다. 실제 HostRun 승인→Chrome 협상에서 native ICE 문자열의 `candidate:` 접두사가 빠져 Chrome이 OperationError를 반환하는 결함을 발견했다. 와이어 정규화와 실제 native fixture를 추가한 뒤 P2P 영상 수신이 성공했다. 4K 송신·표시는 30fps에 못 미쳐 성능 완료로 처리하지 않았다. 기본 출력 상한과 재사용 swscale/BGRA 버퍼를 추가한 뒤 1920×1012 실제 수신을 확인했으나 60초 평균 기준은 후속 측정 대상이다.

E05 일회성 서비스 증거는 `.artifacts/boundary-20261008-094624`, `-094930`, `-095330`, `-095649`, `-095956`, `-100112`다. 매 실행에서 고유 Program Files 경로를 SYSTEM/Admin 쓰기·Users 읽기 ACL로 보호하고 배치 파일의 manifest hash를 확인했다. Session 0 SYSTEM 서비스가 WTS 일반 토큰으로 Session 1 비관리자 헬퍼를, 자기 토큰의 세션 ID를 제한해 Session 1 SYSTEM 헬퍼를 실행했다. 실제 pipe client PID를 생성한 자식과 대조했다. 일반·SYSTEM 전용 창에서 각각 SendInput mouse 3/key 2와 WM_LBUTTONDOWN 1/WM_KEYDOWN 1을 확인했다.

UAC·잠금·재로그인·SAS 호출 중 Winlogon/Default 전환과 4096×2160 DXGI 프레임을 수신했다. 픽셀 원본은 저장하지 않고 크기·해시만 기록했다. 접근 0x81의 보안 데스크톱 입력은 error 5로 실패했다. 0x1FF에서는 3픽셀 이동이 실제 관찰되고 복원 입력도 1건 성공했다. 마지막 `-100112` 시험은 UIAccess 토큰 변경 없이 같은 결과를 반환했다. 창 버튼을 누르거나 비밀번호를 주입하지 않았다. SAS는 기존 SoftwareSASGeneration=1 정책에서 서비스 호출 뒤 Winlogon 전환을 관찰했으며 OS 정책은 수정하지 않았다. 모든 완료된 시험의 cleanup.json에서 serviceAbsent/stageAbsent=true를 확인했다. 다른 계정이 없다는 사용자 답에 따라 실제 계정 전환은 후속으로 남겼다.

E11 `tools/RemoteAssist.MediaProbe`는 synthetic 1080p와 실제 ICE/DTLS-SRTP/SRTCP 피어를 사용했다. SDP에 없는 receiver SSRC의 bare PLI가 전달되지 않는 것을 확인했다. 현재 송신 video SSRC를 포함한 인증된 RR로 receiver SSRC를 한 번 결합한 뒤 PLI/FIR가 전달됐고, NVENC/libopenh264 모두 해당 요청으로 만든 출력이 fresh decoder에서 복구됐다. 수신 10프레임의 RTP timestamp는 0,3000,…,27000이었다. RFC 5104 FIR의 실제 8-byte FCI는 고정 SDK의 private raw-report 메서드를 테스트 도구에서만 사용해 SRTCP 보호 후 보냈다. 제품 코드는 이 reflection 경로를 사용하지 않는다. 최초 RR 이전에는 시작 IDR와 2초 복구 IDR를 사용한다.

전체 빌드 중 실행 중인 시험 Host가 DLL을 잠근 한 번의 실패가 있었으며 정상 세션 종료 뒤 재빌드했다. 최종 재빌드는 경고 0/오류 0, C# 회귀 18묶음과 libopenh264 native 미디어/PLI/FIR 검사가 통과했다. 과거 자동 검사·mock 통과를 제품 IPC/관리자 승인/철회 완료로 확대하지 않는다.

추가 로컬 전체 명령은 `.artifacts/local-20261008-100958/result.json`에 보존했다. .NET locked restore/build, C# 18묶음, Go build/test/vet, 명시적 실행 파일 생성, Node 19개, native 후보/피어 검사가 모두 exit 0이었다. NVENC/MF/libopenh264 synthetic 출력 처리량은 각각 161.5/142.2/96.6fps였으며 브라우저 표시 FPS가 아니다. 이후 IPC·설치 ACL 회귀를 추가해 C# 20묶음이 통과했다. 실제 unbuffered Windows Named Pipe에서 양쪽 hello 쓰기 대기가 교착하는 문제를 재현하고 동시에 읽고 쓰도록 수정했다. 정상/잘못된 nonce·분할 한글/emoji·순번 재생·잘린/과대 프레임을 검사했다. 서비스용 native endpoint·프로세스 감독은 빌드 단계이며 E14/E15 전체 완료로 표시하지 않는다.

libopenh264 실제 캡처→Chrome P2P 경로의 60.0009초 측정은 `.artifacts/sw-browser-media-20261008-101628.json`이다. 실제 수신 크기는 1920×1012, totalVideoFrames 증가 1536, droppedVideoFrames 증가 354, requestVideoFrameCallback 1031회(17.183fps)였다. 실제 표시 30fps 기준에는 미달하며 E35에서 캡처/인코딩/브라우저 배압을 개선한다. 이 시험을 두 PC 수용 시험 또는 성능 PASS로 바꾸지 않는다.

기준 HEAD는 `c4f0fbc80658404022f21c9f8a5e0489ff1e2c41`이며 아래 검증은 그 위 작업트리 변경을 대상으로 했다. 최종 변경 commit은 Git 이력으로 식별한다. 별도 진행표는 만들지 않으며 완료 판정은 루트 구현계획에서만 관리한다. 최종 두 PC F01~F12는 사용자 지시에 따라 이번 세션에서 실행하지 않았다. CI도 실행·변경하지 않았다.

## 환경과 차단 원인

Windows x64/.NET SDK 10.0.400/WindowsDesktop 10.0.11을 사용했다. Windows Go launcher는 1.26.1이며 go.mod 요구에 따라 실제 빌드 toolchain은 Go 1.27.0이었다. Node는 22.17.1이다. Docker CLI와 Windows CGO C compiler는 없다. race 검사는 WSL Ubuntu Go 1.27.0/gcc에서 별도로 했다.

`dotnet run --project tools/RemoteAssist.SessionProbe`의 읽기 전용 결과는 processSession=1, consoleSession=3, elevated=false, threadDesktop=Default, inputDesktop=null, desktopError=5다. WGC supported=true이나 DXGI outputs=[]이며 현재 열거된 WinDisc 모니터는 3840×2160이다.

`CaptureDump --backend wgc --frames 2 --verbose`는 monitor capture item 생성의 E_INVALIDARG로 실패했다. DXGI는 해당 모니터에 대응하는 output을 찾지 못했다. 자동화 세션을 실제 로그인 콘솔 캡처·입력 환경으로 볼 수 없다. 이 결과는 제품 캡처 성공도, F 최종 E2E 실패도 아닌 E05 선행 실증의 환경 차단이다. SYSTEM/UAC/SAS/사용자 전환 실증도 실행하지 못했다. 미구현 UI를 추가해 통과로 덮지 않았다.

## 재현 가능한 로컬 회귀

`powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify-local.ps1`을 실행했다. `.artifacts/local-20261008-015158/result.json`과 명령별 txt에 종료 코드를 보존했다. locked restore, .NET build, C# 회귀, Go build/test/vet, Node 회귀가 모두 exit 0이었다. .NET 빌드는 경고 0/오류 0이다.

빌드 산출물이 없는 detached checkout에 staged source patch를 적용해 locked restore와 빌드를 다시 수행했다. 최초 실행에서 PATH에 Node가 두 개 있을 때 명령 경로를 배열로 다루는 검증 스크립트 오류를 발견해 첫 실제 application을 선택하도록 고쳤다. 수정 후 `.artifacts/repro-results/local-20261008-015642/result.json`의 모든 check가 exit 0이었다. 최초 빌드도 경고 0/오류 0이다. NuGet/Go 다운로드 캐시는 사용했으므로 인터넷 없는 최초 복원을 시험한 것은 아니다.

HostRun의 FPS 0/음수/비숫자, bitrate 0, monitor -1, libx264, 누락/unknown/중복 옵션 9가지와 CaptureDump의 frames 0/1001, unknown backend, monitor -1을 실제 CLI로 실행해 모두 비정상 종료를 확인했다. 강제 h264_qsv 검사는 UNSUPPORTED와 비정상 종료를 반환해 다른 인코더의 성공으로 숨기지 않았다. 일부 PNG 출력·네이티브 hang·전체 Host 수명 장애 주입은 미실행이다.

C# 회귀 15개 묶음은 모든 UTF-8 분할 경계, binary/oversize/중복/버전 거부, 승인 전·만료·종료·교체 피어 권한 거부, 잘못된 옵션, 프레임 stride/회전, 공통 fixture, 잘못된 FFmpeg 공급본, 후보 실패 정리·HW/SW 폴백·강제 후보, PLI/FIR 병합, SPS/PPS 보존, cursor mask/clipping, 입력 binding, 파일 header UUID 순서, 멱등 종료를 검사한다. OS 캡처/입력 mock을 OS 성공으로 세지 않는다.

Node 회귀 19개는 ICE→offer, 승인 없는 offer, 중복 offer, 이전 callback, SDP 중 취소, 지연 stats, pending SDP deadline, bounded 큐, 오류 보존, heartbeat, selected pair의 양쪽 relay와 공통 fixture를 검사한다.

Go 회귀는 zero entropy/시계 fixture, 예약·ACK barrier, join/leave/sweep, 일회성 참가·PIN 잠금, 잘못된 메시지·binary·UTF-8·역할 거부, 무응답 lease와 잔여 룸 제거, hijacked socket shutdown, 발급 상한·Origin, TURN HMAC, Unicode 이름과 잘못된 설정을 검사한다. `go test -race ./...`는 WSL에서 모든 패키지가 통과했다. Windows native 또는 실제 coturn 검증이 아니다.

## 실제 H.264 출력 검사

`dotnet run --project tools/RemoteAssist.HostRun -- --encoder-check`는 캡처·네트워크 없이 synthetic 1920×1080 BGRA 60프레임을 처리했다. `.artifacts/encoders/20261007T164140Z/report.json`에 기록했다. 처리량 타이머에는 인코딩·출력 수집이 포함되며 디코딩 비용은 제외했다.

| 후보 | 실제 출력/디코딩 | 처리량 | 강제 키프레임 fresh decoder 복구 | 판정 |
|---|---|---|---|---|
| h264_nvenc | 60/60 · 60/60 | 139.5fps | 성공 | PASS |
| h264_qsv | 초기화 실패 | 측정 없음 | 미실행 | UNSUPPORTED |
| h264_amf | AMF runtime 부재 | 측정 없음 | 미실행 | UNSUPPORTED |
| h264_mf | 60/60 · 60/60 | 112.8fps | 성공 | PASS |
| libopenh264 | 60/60 · 60/60 | 72.8fps | 성공 | PASS |

이 값은 이 PC의 인코더 처리량이며 캡처/브라우저 표시 FPS·다른 GPU·VM·두 PC 성공을 뜻하지 않는다. 시험 첫 출력의 SPS/PPS 소모로 강제 IDR 복구가 실패하는 문제를 실제 디코더로 발견해 캐시/재삽입으로 수정했다. ForceKeyFrame/SetCodec와 RTCP feedback 계약은 NuGet 10.0.16 repository commit `4ca86773993a875706d8a7a4c1e0108e3a885ebf`의 API/소스를 대조했다.

## 실제 Chrome 검사

Go 서버를 loopback 25001에서 Viewer 자산과 함께 실행하고 `playwright-cli -s=knockhb run-code --filename scripts/browser-smoke.js`를 실행했다. 없는 코드 오류와 실제 발급한 시험 룸의 잘못된 PIN을 검사했다. 최종 결과는 status='PIN이 올바르지 않다.', hidden=true, pageerror=[]였다. 시험 룸은 인증된 Host 슬롯으로 종료했다. 승인·캡처·미디어는 실행하지 않았다.

실제 브라우저에서 bare clearInterval을 객체 메서드로 호출할 때 Illegal invocation으로 UI 종료가 중단되는 문제를 발견했다. globalThis wrapper로 수정한 뒤 재검사했다. Node fake만으로 이 API receiver 문제를 확인했다고 표현하지 않는다.

## 공급·배포 한계와 다음 환경

불변 FFmpeg release archive SHA와 DLL hash를 검증해 준비했다. native Loader는 누락·대체 DLL을 거부한다. 실제 enable-version3을 확인해 LGPLv3/GPLv3 참조 전문과 상류 LICENSE.md를 보관했다. 전체 동봉 외부 라이브러리 대응 소스·설치본 라이선스 검토는 아직 남아 있다.

수정한 준비 스크립트를 다시 실행해 `.artifacts/ffmpeg-trusted-final`에 archive/DLL manifest 검증과 notices 복사를 완료했다. 기존 공급본과 성공한 인코더/브라우저 증거는 덮어쓰지 않았다.

다음 E05 실증은 활성 Windows 콘솔에서 .NET 도구를 실행하고 별도 승인된 관리자·현장 사용자 환경을 확보해야 한다. Service/Tray/IPC/입력/관리자 경로는 아직 구현하지 않았으며, TURN allocation/갱신/rotation·파일/보조 기능·서명 설치·업데이트도 미완료다. Gate A와 전체 구현 완료를 선언하지 않는다.


## RTCP 상류 계약 추가 확인

고정 SIPSorcery RTPSession.GetMediaStream(RTCPCompoundPacket)은 단독 feedback을 SenderSSRC로 찾으며 MediaSSRC 또는 FIR FCI로 대체 매칭하지 않는다. 알려지지 않은 receiver SSRC의 reduced-size PLI/FIR이 OnReceiveReport에 전달된다고 단정할 수 없다. 현재 callback adapter와 키프레임 병합의 단위 계약은 통과했지만 실제 WebRTC 수신·대체 경로 검증이 필요해 E11을 미완료로 되돌렸다. synthetic ForceKeyFrame 성공을 이 전달 경로 성공으로 세지 않는다.
