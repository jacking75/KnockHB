# 작업 로그

## 2026-10-23 14:10 KST — A 머신용 E2E 준비 스크립트

- 이 머신은 A가 아니고 B에 닿지 않아(실제 테스트는 다른 A 머신에서) 준비물만 만들었다. `scripts/e2e-preflight.ps1`(읽기 전용 준비 점검: 도구·A LAN IP·B ping/SSH·포트)과 `scripts/e2e-prepare.ps1`(TLS 인증서 + Host 후보 빌드 + B에 SSH 설치 + 시그널링 기동)을 추가했다. 둘 다 ParseFile 문법 검사 통과, preflight는 이 PC에서 NOT READY로 올바르게 보고(B 미도달·A의 192.168.0.x 없음).
- `수동-실연결-테스트-3.md`에 "A 머신에서 실행"과 두 스크립트 빠른 준비 절을 추가했다.

## 2026-10-23 11:01 KST — 한 PC 루프백 통합 검사와 실연결 시험 계획

- 두 PC가 없는 환경에서 할 수 있는 범위를 수행했다. `scripts/loopback-check.ps1`로 실제 Go 시그널링 + 실제 ScreenShareSession + C# 가짜 Viewer(입력은 기록만, 화면 읽기만)를 붙여 연결·7개 채널·입력 경로·관리자 요청(승인 창 없음)·모니터 전환·원격 명령·채팅·Viewer 교체(busy/replaced)·재접속을 21/21 PASS로 확인했다(연결 607ms, data-ready 834ms, nvenc RTP 수신).
- 실연결 시험 계획서 `docs_working/실연결-시험-계획-2.md`(T13) 작성. 실제 SendInput·UAC 캡처·두 PC·브라우저 Viewer는 T14/T15로 남겼다.

## 2026-10-23 10:38 KST — 현황 문서 1~6단계 구현(상호 신뢰 규격)

- STUN/TURN/relay 제거(시그널링·Host·Viewer·배포), 지원 ID 방의 Viewer 교체(`busy`/`replaced`)와 시그널링 `lan` 모드(TLS 유지, operator/감사 선택) 추가.
- 동의 창·건별 승인·Recovery·CaseTrace를 없애고 권한을 전 권한 고정으로 바꿨다. 연결 시 자동 관리자 모드, 관리자 모드 중 모니터 전환, 데스크톱·화면 구성 변경 자동 전환, 원격 PowerShell 명령을 추가했다.
- 지원 허가를 로그온 시각에 묶은 지속형으로 바꿔 서비스 재시작·업데이트 뒤에도 유지되게 했다. 개발 모드에서는 보호 경로·manifest 검사를 생략한다. 구 E2E 하네스를 정리하고 docs·AGENTS.md를 새 규격으로 갱신했다.
- 검증: 빌드 경고·오류 0, Host.Core 38·Go 전체·Viewer 58·PowerShell 계약 테스트 통과. 인코더 실측(이 PC): nvenc 81.4fps, mf 104.3fps, openh264 62.7fps. h264_amf profile 결함을 고쳤다. 두 PC 실연결은 실행하지 않았다(구현계획-1 T13~T15).
