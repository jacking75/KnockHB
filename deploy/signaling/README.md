# 시그널링 운영 배포

Go 자체 TLS를 사용한다. 프록시 헤더와 다른 upstream을 신뢰하지 않는다. TCP 25001의 HTTPS/WSS만 노출하고 Viewer Origin은 scheme이 아닌 `host:port`의 정확한 목록으로 설정한다. 직접 HTTP 개발 모드는 loopback만 허용한다.

`scripts/prepare-signaling.ps1`은 검토한 Go 1.27.0·distroless 이미지 digest, 실제 인증서, TURN/node 키를 받아 보호된 배포 자료를 만든다. 이미지 digest는 배포자가 registry에서 확인해 고정하며 floating tag를 자동 선택하지 않는다. 생성한 `.env`와 `private/`, `audit/`를 저장소의 `deploy/signaling/`에 비공개로 배치하고 이 디렉터리에서 compose를 실행한다. 별도 staging에 있는 compose는 저장소로 옮긴 뒤 사용한다. Linux 소유자는 컨테이너 UID 65532, private/audit 디렉터리는 0700, secret/key는 0600으로 제한한다. coturn에는 같은 TURN 키를 별도 보호 배치한다. secret을 CLI·로그·Git에 넣지 않는다.

운영 시작 전 인증서 이름/기간/체인, 두 TURN URL, 실제 공개 IP/허용 피어, 포트/방화벽, audit 디스크 여유 공간을 확인한다. `/healthz`는 프로세스 생존만 의미한다. `/readyz`는 시작 검사·TURN 키·감사 저장 장애·shutdown 상태를 확인한다. 실제 allocation/미디어 연결은 V27~V29/F07에서 별도 판정한다.

`GET /metrics`와 `POST /audit`는 보호된 node bearer credential을 요구한다. metrics는 룸·연결·참가 대기·goroutine·readiness·거부 누적 수를 제공한다. 감사 본문은 폐쇄 metadata이며 fsync 후 ACK한다. 같은 ID의 다른 내용은 거부한다. Host는 `scripts/configure-audit.ps1`로 HTTPS /audit와 보호 credential을 설정한다. 미보고 spool이 4096개에 도달하면 새 감사 작업을 거부하고 종료/철회는 계속 허용한다. 경보는 ready=0, pending/room 상한 접근, audit HTTP 503, spool 증가, 디스크 부족에 연결한다.

정상 종료는 WS peer 통지/종료와 5초 HTTP deadline을 사용한다. 컨테이너 grace는 15초다. 재시작하면 이전 pairing·승인은 폐기되고 대상자가 새로 승인한다. 키 교체는 기존 세션 종료→Host 감사 재전송 상태 확인→두 TURN/시그널링 키 보호 교체→ready 및 allocation 재확인 순서다. node credential 교체 시 Host 서비스를 유휴 상태에서 재시작한다. journal은 4MiB×4/7일이며 미보고 spool은 회전과 분리한다. 백업에 비밀 키를 공개 포함하지 않는다.

이 구성 작성은 배포 실행·TLS 신뢰·부하·HA 수용 증거가 아니다. 단일 메모리 룸 서버만 지원하며 여러 replica에 분산하지 않는다.
