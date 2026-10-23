# 기능별 워크플로우

실제 두 PC 동작 확인은 [두 PC 실연결 시험](live-e2e-test.md)으로 판정하고 결과는 그 문서 §9에 남긴다. 아래는 구현된 경로의 처리 순서다.

## 시작

1. 요청자가 프로그램을 받아 `RemoteAssist.Update.exe`를 관리자 권한으로 실행한다(정식 설치는 시작 메뉴 **RemoteAssist Support**).
2. 런처가 지원 허가를 쓰고 방화벽·SAS 준비를 마친 뒤 서비스를 (재)시작한다.
3. 서비스가 콘솔 사용자에 Tray를 띄우고 지원 ID를 표시한다. 허가가 있으므로 바로 Helper를 띄워 지원 ID 방을 연다.

## 연결

1. 지원자가 Viewer에서 지원 ID(9자리)를 입력한다. 이름은 선택이며 대상 PC Tray에 표시만 한다.
2. 서버가 Viewer를 방에 넣고 Host에 peer-joined를 보낸다. Host는 바로 전 권한으로 PeerConnection과 모든 채널을 만들어 offer를 보낸다.
3. 연결되면 Viewer가 자동으로 관리자 모드를 요청한다. 서비스가 AdminHelper를 띄우고 화면·입력 경로를 넘긴다.
4. Viewer 상단에 연결 상태·경로·재접속 횟수가 표시된다.

## 화면·입력·모니터·품질

캡처(WGC/DXGI 또는 AdminHelper) → scale → H.264 → RTP/SRTP. Viewer는 해상도·디코딩 FPS·수신 kbps를 표시하고 1초마다 품질 통계를 보낸다. 지원자는 모니터 목록에서 다른 모니터로 바꾸거나 품질 모드(자동/글자 우선/움직임 우선)를 고른다. 관리자 모드 중에도 모니터를 바꿀 수 있다.

입력은 영상의 여백을 뺀 0..1 좌표다. Host는 identity/topology/epoch/sequence/barrier를 검사해 주입한다. blur·reset·모니터/관리자 전환 때 눌린 키를 떼고 새 epoch로 재개한다.

## 관리자·보안 데스크톱

AdminHelper는 현재 입력 데스크톱을 따라간다. UAC 동의 창·잠금 화면이 뜨면 그 데스크톱을 캡처·조작하고, 사라지면 기본 데스크톱으로 돌아온다. 지원자는 Ctrl+Alt+Del 버튼으로 SAS를 보낸다(서비스 SAS 정책 `SoftwareSASGeneration`은 최초 실행에서 준비한다). 관리자 헬퍼가 실패하면 일반 모드로 계속하며, 이때 보안 데스크톱이 뜨면 세션을 끊지 않고 마지막 화면을 유지한다.

## 보조 기능

- 파일: 지원자가 보낸 파일은 대상 사용자 `다운로드\RemoteAssist`에 저장된다. 대상자는 Tray에서 파일을 골라 보낼 수 있고, 지원자는 저장 위치를 골라 받는다.
- 원격 명령: 지원자가 PowerShell 명령을 입력하고 관리자(SYSTEM) 또는 사용자 권한을 고른다. 결과(stdout/stderr/종료 코드)가 20초 안에 돌아온다.
- 등록 Runbook·채팅·클립보드·진단은 각 패널에서 쓴다.

## 끊김·재접속·종료

- 일시 ICE 단절: 입력·영상을 멈추고 2초 뒤 Host가 다음 generation으로 전송을 다시 만든다(최대 3회).
- 지원자 이탈·터널링 서버 상실: 세션이 끝나고 Host가 1초 뒤 같은 지원 ID로 다시 대기한다. Viewer는 2초부터 최대 10초 간격으로 다시 접속한다.
- 다른 Viewer가 같은 ID로 접속: 새 Viewer가 이어받고 이전 Viewer는 재접속하지 않는다.
- 대상자 즉시 종료(Tray 버튼·Ctrl+Alt+Shift+F10·Tray 창 닫기): 지원을 끝내고 허가를 지운다. 다시 지원받으려면 관리자 권한으로 실행한다.
- 재부팅·로그오프·사용자 변경: 허가가 무효가 된다.

## 업데이트

업데이트는 HTTPS/PSS/ZIP 검증 → 지원자가 없을 때 session.gate 획득 → 보호 staging → pending transaction → 서비스 image 전환 → ready → commit이다. 지원 허가는 유지되므로 서비스 재시작 뒤 같은 지원 ID로 다시 대기한다. 실패하면 직전 버전으로 rollback한다.
