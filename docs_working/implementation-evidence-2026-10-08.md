# 구현 기반의 로컬 실행 증거 — 2026-10-08

## 후속 활성 콘솔 실증

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
