# KnockHB / RemoteAssist

대상자의 동의를 기반으로 Windows PC를 지원하기 위한 원격 지원 도구다. 현재는 C# HostRun과 Go 시그널링 서버, 웹 Viewer를 연결하는 **화면 공유 PoC**를 구현했다. 원격 입력·동의 UI·서비스 연동·TURN은 후속 구현 대상이다.

처음 보는 개발자는 [개발자 문서 안내](docs/README.md)부터 읽는다.

| 문서 | 내용 |
|---|---|
| [아키텍처](docs/architecture.md) | 현재 실행 구조, 프로젝트 의존성, 목표 권한 경계 |
| [기술 사양](docs/technical-specification.md) | 스택, 설정, API, 미디어·배포 제약 |
| [주요 로직](docs/core-logic.md) | 코드 탐색, 세션·캡처·인코딩·릴레이 로직 |
| [기능별 워크플로우](docs/workflows.md) | 현재 기능과 예정 기능의 Mermaid 흐름도 |
| [실행·검증 가이드](docs/development-guide.md) | 로컬 PoC 실행, 두 머신 검증, 문제 해결 |
| [프로토콜](docs/protocol.md) | 와이어 포맷의 단일 기준 |
| [구현계획](원격제어-구현계획.md) | 단계별 태스크와 완료 조건 |
| [리뷰·태스크 리스크](docs_working/code-review-task-risks-2026-10-07.md) | 발견된 결함, 수정 방법, 회귀 검증 |
