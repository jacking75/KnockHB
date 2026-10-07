# 기능별 워크플로우

2026-10-08 현재 흐름과 미구현 제품 흐름을 구분한다. [제품 계약](product-contract.md)·[프로토콜](protocol.md)이 기준이며 완료 상태는 [구현계획](../원격제어-구현계획.md)에서만 관리한다.

## 현재 화면 보기

1. 대상자가 HostRun을 시작한다. 옵션·URI·모니터를 먼저 검사하고 코드/PIN을 발급받는다.
2. 대상자가 코드/PIN을 지원자에게 별도 경로로 전달한다. 일반 로그에는 두 값을 기록하지 않는다.
3. 지원자는 자기 신고 이름과 코드/PIN으로 Viewer에 참가한다. 서버는 원자적으로 인증·1회 소비·슬롯 예약하고 joined ACK 뒤 peer를 공개한다.
4. Host가 지원자 이름과 승인 모니터를 로컬 콘솔에 표시한다. 대상자가 30초 안에 '화면 승인'을 입력해야 한다. 거절·timeout·연결 상실이면 캡처 없이 종료한다.
5. Host가 screen 권한을 로컬 객체에 부여하고 consent를 통지한다. 승인 뒤 PC/offer를 만들며 Viewer가 answer를 보낸다. SDP 이전 ICE는 제한된 큐에 보관한다.
6. connected 뒤 승인 모니터를 캡처하고 실제 초기화·출력을 통과한 H.264 인코더를 선택한다. 크기 변경과 PLI/FIR은 강제 키프레임 복구를 요청한다.
7. Viewer는 영상과 실제 수신 통계·selected candidate pair 경로를 표시한다. 통계 조회 실패는 처리하며 지난 연결의 callback은 새 UI를 변경하지 못한다.
8. 대상자 Ctrl+C, Viewer 종료, peer 이탈, WS 무응답, 협상 오류는 권한 폐기·새 송신 차단·자원 정리로 수렴한다. 첫 종료 사유를 보존하며 같은 코드로 승인을 복구하지 않는다.

현재 동의는 콘솔 화면 보기 전용이다. 상시 표시·Tray 동의·원클릭 종료의 제품 UX는 미구현이며 최종 제품 경로로 배포하지 않는다.

## 현재 진단 도구

SessionProbe는 process/console session ID, Default/input desktop, 승격 여부, WGC 지원 여부와 DXGI 출력을 읽는다. 프로세스 승격·입력 주입·다른 사용자 세션 이동은 수행하지 않는다.

CaptureDump는 승인한 공통 모니터 identity를 WGC/DXGI에 전달해 PNG를 저장한다. 요청한 개수를 모두 저장해야 성공이다. 기존 파일을 덮어쓰지 않는다. 실패는 원인과 비정상 종료 코드다. 저장 PNG는 사용자가 명시적으로 요청한 로컬 진단 산출물이며 민감 화면을 저장소에 커밋하지 않는다.

HostRun --encoder-check는 캡처·시그널링 없이 synthetic 1080p BGRA를 처리한다. 후보마다 Configure/실제 출력/전체 디코딩/fresh decoder 강제 키프레임 복구/출력 처리량을 확인한다. 강제 후보 실패를 폴백 성공으로 숨기지 않는다. 하드웨어 없는 후보는 UNSUPPORTED로 분리한다. 네트워크 표시 FPS와 구분한다.

## 제품 경로 — 아직 미구현

Service는 Session 0에서 사용자 세션 헬퍼를 감독하고 Tray는 일반 사용자 승인 UI를 맡을 목표다. 헬퍼의 실제 실행 계정/session/desktop, 인증 IPC·ACL·승인 세대·민감 버퍼 수명은 E05 실증 뒤 확정한다. 서비스가 Session 0에서 사용자 화면을 직접 캡처한다고 가정하지 않는다.

일반 입력은 reliable key/button/text/reset과 별도 최신 pointer를 사용한다. 실제 video contain 영역·monitor identity·topology generation·단조 seq를 검증한다. 화면 보기+제어 승인 없이는 주입하지 않는다. blur/숨김/철회/헬퍼 종료 때 눌린 키를 해제한다. E21~E23에서 채널·브라우저 입력·SendInput을 연결한다.

관리자 권한은 독립 동의와 추가 현장 승인이 필요하다. UAC/SAS/잠금화면은 E05에서 실증한 backend와 대상자 중단 경로를 전제로 구현한다. 일반 동의를 SYSTEM 임의 명령 실행 권한으로 바꾸지 않는다.

TURN은 인증된 joined에 단기 credential을 전달하고 양쪽 PC에 설정을 넣는 단계까지 존재한다. 실제 coturn TLS/allocation·장시간 갱신/rotation·복구 시험은 미완료다. 현재 ICE disconnected는 종료하며 자동 복구를 지원한다고 표현하지 않는다.

파일은 기능 승인과 건별 수락·저장 위치 선택·임시 파일·체크섬 완료를 요구한다. clipboard는 방향 승인과 echo 방지, diagnostics는 별도 읽기 전용 동의, Runbook은 설치된 ID/version/hash와 인자에 대한 로컬 건별 승인을 요구한다. 해당 기능은 아직 UI·채널·실행 경로가 없다.

서명 설치·업데이트는 정식 서명과 검증/롤백/진행 중 세션 보호를 요구한다. 스켈레톤 서비스 빌드나 개발 인증서를 정식 설치 후보로 선언하지 않는다. 최종 F01~F12는 사용자의 지시에 따라 별도 세션에서 수행한다.