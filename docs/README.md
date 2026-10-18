# RemoteAssist 개발자 문서

동의 기반 Windows 원격 지원 도구다. 저장소 이름은 KnockHB이고 제품/네임스페이스는 RemoteAssist다. 제품 경로는 보호 설치의 SYSTEM 서비스→일반 권한 WPF Tray/Helper→웹 Viewer다. HostRun의 콘솔 화면 승인은 개발 진단용이다.

구현·코드 리뷰와 실행 수용을 구분한다. 상태는 [통합 구현계획](../docs_working/원격제어-구현계획.md) 하나에서 관리한다. 기존 단일 PC 실증은 [과거 실행 증거](../docs_working/implementation-evidence-2026-10-08.md)이며 새 후보의 V/F 통과를 대신하지 않는다. 실제 TLS/TURN 서버·정식 서명 인증서·깨끗한 VM·다른 GPU/두 물리 PC의 검증은 해당 V/F에서 수행한다.

30분 E2E 재리뷰에서 E39/E41을 다시 열었으며 현재 Arm/제품 시험 직접 실행은 차단됐다. [리뷰와 준비 계약](../docs_working/e2e-30-minute-review-2026-10-08.md)의 전역 시간·정리·관측·현장 복귀 증거가 갖춰진 뒤에만 착수한다.

| 문서 | 기준 |
|---|---|
| [제품 계약](product-contract.md) | 지원 대상·위협 모델·권한·수용값 |
| [아키텍처](architecture.md) | 프로세스·Session/SID·감독·수명 |
| [기술 사양](technical-specification.md) | 고정 의존성·설정·상한 |
| [프로토콜](protocol.md) | 유일한 와이어 필드/역할/세대 기준 |
| [주요 로직](core-logic.md) | 코드 소유권·실행 게이트·종료 |
| [워크플로우](workflows.md) | 동의·미디어·입력·보조 기능·복구 |
| [개발 가이드](development-guide.md) | 로컬 검사·실행 준비·증거·정리 |
| [설치/업데이트](../deploy/installation.md) | 서명·보호 설치·transaction/rollback |
| [시그널링 운영](../deploy/signaling/README.md) | TLS·키·감사·metrics·컨테이너 |
| [TURN 운영](../deploy/coturn/README.md) | 인증·허용 목적지·키 교체 |
| [검증 하네스](../tests/e2e/README.md) | 준비·실행·중단·수집 계약 |

screen/control/admin/file/clipboard/diagnostics/runbook/chat은 독립 동의다. control은 screen이 필요하고 admin은 별도의 현장 2차 승인 뒤에만 활성화한다. 파일·모니터 전환·Runbook에는 건별 대상자 승인도 필요하다. 대상자는 상시 표시와 즉시 종료를 제공받으며 권한 철회는 감사 실패보다 우선한다.

코드/PIN은 일회성 pairing이며 입력 이름은 자기 신고로 표시한다. Go 서버는 SDP/ICE와 감사 metadata를 처리하고 화면 원본을 저장하지 않는다. 미디어·데이터는 P2P 우선 DTLS-SRTP/SCTP, TURN은 암호화된 패킷만 중계한다. 관리자/SAS는 승인된 대상 Session의 별도 SYSTEM helper가 처리한다. 진단만 승인한 세션에는 영상 트랙이 없다.

지원 범위는 Windows 10 22H2/11 x64의 단일 콘솔 사용자와 최신 안정 Chrome/Edge다. RDP/다른 사용자로 승계하지 않는다. 사용자 세션을 완전히 장악한 악성코드나 악성 관리자로부터 신뢰 UI를 보장하지 않는다. 무인 접속·임의 원격 셸·AI 자동 조치·기업 RBAC/HA는 1차 범위에 없다.

지속 기준 문서는 docs/, 임시 리뷰/분석은 docs_working/에 둔다. working_log.md는 사용하지 않는다. 스키마 변경은 protocol→C#/Go/JS 순서이며 구현 상태는 다른 문서에 진행표로 복제하지 않는다. CI는 사용자 명시 요청 전 작업하지 않는다. [스택 이력](stack.md)의 과거 버전/측정은 현재의 보증으로 읽지 않는다.
