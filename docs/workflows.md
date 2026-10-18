# 기능별 워크플로우

실행 성공 여부는 [구현계획](../docs_working/원격제어-구현계획.md)의 V/F에서 판정한다. 아래는 구현된 경로의 처리 순서다.

## 생성·동의·연결

1. 보호된 서비스가 console Session/SID를 확인하고 일반 Tray를 시작한다.
2. 대상자가 지원 시작을 누르면 session.gate를 얻고 일반 Helper를 생성한다. 코드/PIN은 로컬 UI에만 표시한다.
3. Viewer가 별도 경로로 받은 코드/PIN과 자기 신고 이름으로 한 번 참가한다. 서버는 예약/소비를 원자화하고 joined ACK 뒤에만 peer를 ready로 공개한다.
4. Tray는 지원자 검증 수준·모니터·독립 기능 토글을 보여준다. 기본 미승인이며 거절/닫기/timeout은 승인되지 않는다.
5. 로컬 승인·필수 감사 후에만 PeerConnection/승인 트랙과 기능 채널을 만든다. data-ready가 권한/채널/geometry를 확인한다. diagnostics만 승인하면 영상은 없다.

## 화면·입력·모니터·품질

캡처는 WGC/DXGI의 같은 모니터를 사용하며 BGRA→scale→허용 H.264 후보→SPS/SDP 한도 검사→RTP/SRTP 순서다. actual sample time을 90kHz로 반영한다. Viewer는 decodedFrames/FPS/bitrate와 selected pair의 양쪽 P2P/relay를 표시한다. 품질 설정값과 실제 통계를 구분한다.

Viewer input은 승인 영상의 여백을 제외해 0..1로 변환한다. Host는 identity/topology/epoch/sequence/barrier·현재 권한을 검사한다. down은 독립 Recovery 예약 ACK 뒤에만 주입하고 성공한 up 뒤 예약을 줄인다. blur/reset·건별 로컬 UI·권한 변경 시 key/button을 해제하고 새 epoch ACK 전에는 입력하지 않는다.

모니터 변경/geometry 변경은 새 descriptor를 대상자가 건별 승인해야 한다. 캡처 스레드가 이전 프레임을 지우고 새 capture/encoder/IDR→geometry ACK→입력 재개로 처리한다. 대상 제거·거부·실패는 종료한다. 자동 품질은 5초 유지·혼잡 하향·15초 연속 안정 후 상승하며 통계 누락을 안정으로 세지 않는다.

## 관리자·보안 desktop

초기 admin capability는 요청 허용이다. 요청 시 일반 입력을 중단하고 Tray에서 별도 만료 승인을 받는다. 서비스는 승인 바인딩의 대상 Session/SYSTEM AdminHelper를 Job으로 실행한다. 기본/보안 desktop 프레임은 ACL 공유 mapping을 거쳐 일반 Helper의 인코더로 전달한다. batch 입력/SAS는 현재 admin generation과 lease를 검사한다. 로컬 철회·승인 만료·helper/desktop 복귀 오류는 전체 지원을 종료한다. UAC 정책을 끄거나 자격증명을 저장하지 않는다.

## 보조 기능

파일 offer는 본문 전 건별 수락/저장 위치 선택과 durable 감사가 필요하다. Host 송신도 로컬 파일 선택이다. 한 건의 16KiB 청크에 순번/길이/credit을 적용하고 SHA256을 완료 전에 검사한다. 거부·취소·timeout·철회는 부분 산출물을 지우며 원본을 보존한다.

chat은 동의한 text만 송수신한다. clipboard get/set은 별도 권한·명시 동작·로컬 UI actor·hash/길이/빈도로 검사한다. 내용은 감사하지 않는다. 진단은 읽기 전용 provider의 bounded snapshot이며 오류/stale를 표시한다.

Runbook은 등록 ID/version/source hash·허용 인자만 요청한다. Tray가 실제 전문/영향/권한을 보여 건별 승인하면 일반/SYSTEM worker Job을 실행한다. 20초·출력4096자·동시1개 한도와 취소가 있으며 감사는 ID/hash/결과만 남긴다. 이미 수행한 변경은 취소로 되돌아가지 않을 수 있다.

## 복구·종료·업데이트

일시 ICE 단절은 같은 인증 피어/로컬 승인에 한해 입력/영상 중단→peer별 새 TURN credential→다음 generation PC/DTLS/SCTP→새 data-ready/IDR로 복구한다. 최대3회/각30초이고 credential은 8분마다 갱신한다. 승인 UI/관리자 중 복구·시그널링 상실·다른 피어는 종료다.

로컬 즉시 종료는 새 송신/입력을 먼저 차단하고 Job/독립 up 복원을 실행한다. 실제 영상 lease가 멈춰도 서비스가 헬퍼를 종료한다. Viewer 디코딩 정지는 별도로 종료한다. 감사 실패가 종료를 막지 않는다.

업데이트는 HTTPS/PSS/ZIP 검증→유휴 gate→보호 staging→pending transaction→SCM image 전환→45초 ready→commit이다. 실패/중간 종료는 직전 정상 버전으로 rollback하며 최고 승인 버전을 낮추지 않는다. 이전 동의는 재사용하지 않는다. 실제 설치·부하·다른 NAT/두 PC는 준비된 V/F 묶음으로 별도 검증한다.
