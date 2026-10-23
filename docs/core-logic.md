# 주요 로직과 코드 읽기

진행 상태는 [구현계획](../docs_working/구현계획-1.md)에서 확인한다. 이 문서는 현재 코드의 책임과 소유권을 설명한다.

| 코드 | 책임 |
|---|---|
| Host.Service/HostSessionSupervisor | 콘솔 Session/SID·지원 허가 읽기/폐기·Tray/Helper/AdminHelper/Runbook Job 감독·자동 재대기·감사(실패해도 계속) |
| Deployment/InitialSupportLaunch, DeviceId | 관리자 실행 허가(로그온 시각·사용자·세션 결합)와 고정 지원 ID |
| Deployment/InstallationState, StartupPreparation | 정식/개발 모드 구분, 보호 경로·manifest 검사(정식만), 방화벽 준비 검증 |
| Windows/SessionProcess, ProtectedInstallation | WTS 실행·고정 역할/이미지·토큰·Job |
| Ipc/LocalIpcEndpoint, IpcPeerSession | 로컬 ACL·PID/SID/Session/nonce 인증·크기/순번·heartbeat |
| Security/PermissionGate, AdminAuthorization | 연결된 지원자에게 전 권한 부여·세션 종료 뒤 차단, 관리자 세대 |
| ScreenShareSession | 직렬 이벤트·generation·PeerConnection/채널·캡처 스레드·모니터 전환·데스크톱 전환·전송 재구성·멱등 종료 |
| Capture/WgcScreenCapture, DxgiScreenCapture | 같은 모니터 identity·BGRA/stride·geometry·rotation/cursor |
| Media/BgraFrameScaler, FFmpegH264Encoder | 재사용 버퍼·짝수 출력·후보별 실제 init/SPS·키프레임 |
| Media/H264Contract, FrameRtpClock, H264RtpPayloads | SDP profile/level·단조 90kHz·NAL/FU-A |
| Input/InputSequenceGuard, InputDataSession, InputSession | 채널별 순번/barrier/epoch·move 합치기·눌린 키 추적과 해제·일시 중단/재개 |
| Security/AdminDesktopRuntime, AdminFrameSource, Service/AdminScope | 현재 입력 데스크톱 추적 캡처·입력, 모니터 재바인딩, 공유 프레임 |
| Files/FileTransferSession, DownloadFolder | 한 건·credit·SHA256·NTFS handle 확정·다운로드 폴더 자동 저장 |
| Media/FeatureDataChannel, Diagnostics | chat/clipboard/진단 한도 |
| Runbooks, Media/RunbookDataSession, Service/RunbookScope | 등록 작업·원격 명령·worker Job·20초/출력 상한 |
| Security/AuditJournal, Service/AuditReporter | 폐쇄 metadata 저널·선택적 서버 보고 |
| Host.Update, deploy/install.ps1 | 관리자 실행 진입점(support/support-dev)·서명 설치/업데이트·rollback |
| Go room/tunnel | 지원 ID 방·ACK 원자성·역할·재협상 세대·Viewer 교체·상한·TLS |
| Viewer connection/input/features/files/runbooks/app | 연결 객체·자동 관리자 모드·자동 재접속·좌표·파일·원격 명령 UI |

캡처 스레드는 모니터·품질 변경을 프레임 경계에서 적용한다. 일반 모드 모니터 전환은 캡처 스레드가 새 캡처를 만들고 첫 프레임을 보낸 뒤 완료되고, 관리자 모드 전환은 AdminHelper가 다음 데스크톱 루프에서 새 모니터를 연다. 두 경우 모두 입력을 멈췄다가 새 epoch로 재개한다.

EndSession은 최종 송신과 직렬화해 권한·큐를 먼저 폐기하고 Completion 원인을 한 번 확정한다. Dispose는 native thread/task 종료를 기한 안에서 기다리며 실행 중인 native 객체를 먼저 해제하지 않는다. 서비스는 헬퍼 crash/hang·IPC 상실에서 Job을 끝내고 허가가 있으면 다시 대기한다.

로그는 고정 kind/ID/결과 metadata만 쓴다. 예외 본문·token·클립보드·파일 경로·스크립트 출력은 감사에 없다.
