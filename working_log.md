# 작업 로그

## 2026-10-23 10:38 KST — 현황 문서 1~6단계 구현(상호 신뢰 규격)

- STUN/TURN/relay 제거(시그널링·Host·Viewer·배포), 지원 ID 방의 Viewer 교체(`busy`/`replaced`)와 시그널링 `lan` 모드(TLS 유지, operator/감사 선택) 추가.
- 동의 창·건별 승인·Recovery·CaseTrace를 없애고 권한을 전 권한 고정으로 바꿨다. 연결 시 자동 관리자 모드, 관리자 모드 중 모니터 전환, 데스크톱·화면 구성 변경 자동 전환, 원격 PowerShell 명령을 추가했다.
- 지원 허가를 로그온 시각에 묶은 지속형으로 바꿔 서비스 재시작·업데이트 뒤에도 유지되게 했다. 개발 모드에서는 보호 경로·manifest 검사를 생략한다. 구 E2E 하네스를 정리하고 docs·AGENTS.md를 새 규격으로 갱신했다.
- 검증: 빌드 경고·오류 0, Host.Core 38·Go 전체·Viewer 58·PowerShell 계약 테스트 통과. 인코더 실측(이 PC): nvenc 81.4fps, mf 104.3fps, openh264 62.7fps. h264_amf profile 결함을 고쳤다. 두 PC 실연결은 실행하지 않았다(구현계획-1 T13~T15).
