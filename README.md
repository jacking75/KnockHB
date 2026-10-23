# KnockHB / RemoteAssist

사내 PC 관리 운영 부서가 쓰는 Windows 원격 지원 도구다. 지원을 요청한 사람이 프로그램을 받아 관리자 권한으로 한 번 실행하면, 지원자가 고정 9자리 지원 ID로 접속해 화면·입력·관리자(UAC)·파일·클립보드·진단·원격 명령을 모두 쓴다. 대상자의 추가 승인은 없고 연결이 끊기면 자동으로 다시 접속한다.

구성: SYSTEM 서비스 + 일반 권한 WPF Tray/Helper(C# .NET 10), Go 시그널링 서버(TLS), 웹 Viewer(바닐라 JS). 두 PC는 STUN/TURN 없이 직접 P2P로 연결한다.

처음 보는 개발자는 [개발자 문서 안내](docs/README.md)부터 읽는다.

`docs_working/`은 개발 중 사용하는 임시 계획·조사·리뷰·작업 기록을 보관하는 디렉터리다.

| 문서 | 내용 |
|---|---|
| [아키텍처](docs/architecture.md) | 실행 구조, 허가·세션 수명, 권한 경계 |
| [기술 사양](docs/technical-specification.md) | 스택, 설정, 상한 |
| [주요 로직](docs/core-logic.md) | 코드 소유권 |
| [기능별 워크플로우](docs/workflows.md) | 시작·연결·기능·재접속 흐름 |
| [실행·검증 가이드](docs/development-guide.md) | 로컬 검사, 개발 설치, 실연결 시험 |
| [프로토콜](docs/protocol.md) | 와이어 포맷의 단일 기준 |
