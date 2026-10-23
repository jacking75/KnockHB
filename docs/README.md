# RemoteAssist 개발자 문서

사내 PC 관리 운영 부서가 쓰는 Windows 원격 지원 도구다. 저장소 이름은 KnockHB이고 제품/네임스페이스는 RemoteAssist다.

지원자 PC와 대상 PC는 서로 신뢰하는 관계다(2026-10-23 사용자 결정). 지원을 요청한 사람이 프로그램을 받아 관리자 권한으로 한 번 실행하면, 그 뒤 대상자는 아무것도 하지 않는다. 지원자가 고정 9자리 지원 ID로 접속해 화면·입력·관리자(UAC)·파일·클립보드·진단·Runbook·원격 명령·모니터 전환을 모두 쓴다. 승인 창·PIN·건별 승인은 없다. 연결이 끊기면 같은 지원 ID로 자동 재접속한다.

제품 경로는 SYSTEM 서비스 → 일반 권한 WPF Tray/Helper → (관리자 모드) SYSTEM AdminHelper → Go 터널링 서버 → 웹 Viewer다. 미디어·데이터는 두 PC 사이 직접 P2P(DTLS-SRTP/SCTP)로만 흐른다. STUN/TURN 서버는 없다.

| 문서 | 기준 |
|---|---|
| [제품 계약](product-contract.md) | 지원 대상·신뢰 모델·기능 범위·수용값 |
| [아키텍처](architecture.md) | 프로세스·Session/SID·감독·수명 |
| [기술 사양](technical-specification.md) | 고정 의존성·설정·상한 |
| [프로토콜](protocol.md) | 유일한 와이어 필드/역할/세대 기준 |
| [주요 로직](core-logic.md) | 코드 소유권·종료 |
| [워크플로우](workflows.md) | 시작·연결·미디어·입력·관리자·보조 기능·복구 |
| [최초 관리자 실행](startup-preparation.md) | 지원 허가와 OS 준비 |
| [개발 가이드](development-guide.md) | 로컬 검사·개발 설치·실연결 시험 |
| [두 PC 실연결 시험](live-e2e-test.md) | E2E 준비·실행·판정·성능 측정·정리 |
| [스택](stack.md) | 언어·라이브러리·포트 |
| [설치/업데이트](../deploy/installation.md) | 서명·보호 설치·transaction/rollback |
| [터널링 서버 운영](../deploy/tunnel/README.md) | TLS·감사·metrics·컨테이너 |

지속 기준 문서는 `docs/`에 둔다. `docs_working/`은 개발 중 사용하는 임시 계획·조사·리뷰·작업 기록을 보관하는 디렉터리다. 스키마 변경은 protocol → C#/Go/JS 순서다. 구현됨(빌드·단위 테스트 통과)과 실제 두 PC 동작 확인은 구분한다.
