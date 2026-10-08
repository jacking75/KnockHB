# 주요 로직과 코드 읽기

수용 상태는 [구현계획](../원격제어-구현계획.md)에서 확인한다. 이 문서는 현재 코드의 책임/소유권을 설명한다.

| 코드 | 책임과 최종 실행 게이트 |
|---|---|
| Host.Service/HostSessionSupervisor | 단일 console Session/SID·Tray/Helper/Job·동의/권한·감사·session.gate·실제 media lease |
| Windows/SessionProcess, ProtectedInstallation | WTS 실행·고정 역할/이미지·최소 token/handle·Job·보호 트리 검사 |
| Ipc/LocalIpcEndpoint, IpcPeerSession | 로컬 ACL·PID/SID/Session/nonce 인증·크기/순번·bounded actor·heartbeat |
| Security/PermissionGate, LocalApprovalGate, AdminAuthorization | 기능/피어/세대/유효기간·철회 우선·단일 건별 승인·2차 관리자 승인 |
| ScreenShareSession | 직렬 이벤트·generation·PeerConnection/채널·media thread·완료 결과·멱등 종료 |
| Capture/WgcScreenCapture, DxgiScreenCapture | 같은 승인 identity·BGRA/stride·geometry·rotation/cursor·COM/native rollback |
| Media/BgraFrameScaler, FFmpegH264Encoder | 재사용 버퍼·짝수 출력·후보별 실제 init/SPS·키프레임·native thread 소유권 |
| Media/H264Contract, FrameRtpClock, H264RtpPayloads | SDP/profile/level/peer limit·단조90kHz·NAL/FU-A marker |
| Input/InputSequenceGuard, InputDataSession, InputSession | 채널별 순번/barrier/epoch·합쳐진 move·reserved down/복원 ACK·최종 SendInput |
| Service/RecoveryScope, Host.Recovery | helper 밖의 제한된 up 복원·예약 revision·일반 사용자 실행 |
| Service/AdminScope, Host.AdminHelper | 대상 Session/SYSTEM·admin lease·인증 batch/SAS·세대가 묶인 공유 프레임 |
| Files/FileTransferSession, SafeFileReceiver | 건별 선택/수락·credit·한 건·SHA256·안전한 NTFS handle/rename·부분 파일 정리 |
| Media/FeatureDataChannel, Diagnostics | 독립 chat/clipboard/diagnostics·한도/취소·화면 없는 진단 |
| Runbooks, Media/RunbookDataSession, Service/RunbookScope | 등록 source/hash·고정 인자·별도 승인/Job·20초/출력 상한 |
| Security/AuditJournal, Service/AuditReporter | 폐쇄 metadata·보호 spool·durable ACK·bounded 회전/재시도·종료 우선 |
| Deployment, Host.Update, deploy/install.ps1 | PSS/ZIP·ACL·유휴 gate·단조 버전·transaction/ready/rollback/bootstrap |
| Go room/signaling | ACK ready·일회성 pairing·역할·재협상 세대·상한·TLS·감사·metrics |
| Viewer connection/input/features/files/runbooks | 연결 객체·await 후 소유권·reset/좌표·독립 권한·스트리밍 파일/hash·UI textContent |

캡처 스레드는 새 topology/품질을 경계에서 적용한다. 바뀐 모니터는 입력 suspend→Tray 건별 승인→캡처 재생성→IDR→monitor-state→새 inputEpoch로 재개한다. 제거·새 승인 실패는 전체 종료다. native buffer는 캡처 스레드만 폐기하며 공유 특권 프레임은 복사 전후 commit/세대를 검사한다.

연결 객체와 permission snapshot은 별도 수명이다. TURN 재협상에서는 캡처를 중단하고 기존 PC/feature/file/Runbook 작업을 정리한 뒤 새 generation을 만든다. 이전 PC·채널·async hash 완료는 현재 세션에 적용하지 않는다. 입력 복원 revision은 모니터 전환에서도 감소시키지 않는다.

EndSession은 최종 송신과 직렬화해 권한·로컬 승인·큐를 먼저 폐기하고 Completion 원인을 한 번 확정한다. Dispose는 native thread/task 종료를 deadline으로 기다리며 실행 중 native 객체를 먼저 해제하지 않는다. 서비스는 헬퍼 crash/hang·UI/lease 상실에서 Job을 종료한다. Recovery 예약은 down 전에 ACK해 부분 SendInput 성공이나 crash에서도 up 복원 후보가 남도록 한다.

로그는 고정 kind/ID/결과 등 metadata를 사용한다. 예외 본문·PIN·token·TURN/node 키·클립보드·파일 경로·스크립트 출력은 감사에 없다. 원본 화면/사용자 파일을 시험 정리 대상으로 삼지 않는다.
