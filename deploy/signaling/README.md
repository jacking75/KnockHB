# 시그널링 운영 배포

Go 자체 TLS를 사용한다. 프록시 헤더와 다른 upstream을 신뢰하지 않는다. TCP 25001의 HTTPS/WSS만 노출한다. 사설망은 `RA_MODE=lan`(TLS 필수, 자체 서명 가능), 공개 운영은 `RA_MODE=production`을 쓴다. 평문 HTTP 개발 모드는 loopback만 허용한다. Viewer Origin을 제한하려면 `RA_ALLOWED_ORIGINS`에 정확한 `host:port` 목록을 넣는다(비우면 같은 호스트만).

STUN/TURN 서버는 쓰지 않는다. 지원자 PC와 대상 PC는 지정 IP로 직접 P2P 연결한다.

`scripts/prepare-signaling.ps1`은 검토한 Go 1.27.0·distroless 이미지 digest, 실제 인증서, node 키를 받아 보호된 배포 자료를 만든다. 생성한 `.env`와 `private/`, `audit/`를 저장소의 `deploy/signaling/`에 비공개로 배치하고 이 디렉터리에서 compose를 실행한다. Linux 소유자는 컨테이너 UID 65532, private/audit 디렉터리는 0700, secret/key는 0600으로 제한한다. secret을 CLI·로그·Git에 넣지 않는다.

`/healthz`는 프로세스 생존만 의미한다. `/readyz`는 시작 검사·감사 저장 장애·shutdown 상태를 확인한다. `RA_OPERATOR_SECRET_FILE`·`RA_AUDIT_DIR`은 선택이며, 지정하면 `GET /metrics`와 `POST /audit`가 node bearer credential로 열린다. metrics는 룸·연결·참가 대기·goroutine·readiness·거부 누적 수를 제공한다. 감사 본문은 폐쇄 metadata이며 fsync 후 ACK한다. Host는 `scripts/configure-audit.ps1`로 HTTPS /audit와 보호 credential을 설정할 수 있다.

정상 종료는 WS peer 통지/종료와 5초 HTTP deadline을 사용한다. 컨테이너 grace는 15초다. 서버를 재시작하면 연결 중인 지원은 끊기고, 대상 PC가 같은 지원 ID로 다시 대기하며 Viewer가 자동으로 다시 접속한다. 단일 메모리 룸 서버만 지원하며 여러 replica에 분산하지 않는다.
