# 인증된 TURN 배포

`coturn/coturn:4.18.0-r0`의 공식 [설정](https://github.com/coturn/coturn/blob/docker/4.18.0-r0/examples/etc/turnserver.conf)을 기준으로 한다. 실제 image digest는 V27 후보 manifest에 고정한다. `latest`와 개발용 고정 계정은 사용하지 않는다.

`scripts/prepare-turn.ps1`에 보호된 secret 파일·realm·외부 IPv4·허용 피어 IP·TLS 인증서/키를 전달하면 별도 private staging을 만든다. 생성 파일은 Git에서 제외한다. Linux 전송 후 소유자와 읽기 권한을 coturn 실행 UID로 제한하며 다른 사용자의 읽기·쓰기를 금지한다. 생성 중 기존 디렉터리를 덮어쓰지 않는다. secret 내용을 명령 인자·로그에 넣지 않는다.

TCP 25002는 TURN/TCP, TCP 25003은 TURN/TLS다. TURN/UDP도 listener 25002를 사용하므로 프로젝트 UDP 대역에 맞추기 위해 운영에서는 TCP/TLS URL만 광고한다. UDP relay allocation은 27100~27199다. 직접 Host UDP는 27002~27099이며 브라우저가 자체 선택한 로컬 ephemeral 포트는 웹 API에서 고정할 수 없다. 방화벽에 모든 UDP 포트를 공개하지 않는다. 직접 UDP까지 전역 포트 고정을 요구하는 네트워크에서는 TURN/TCP/TLS를 사용한다.

목적지는 기본 전체 거부 후 명시한 IP만 허용한다. `allowed-peer-ip` 우선순위를 적용하므로 내부망·loopback·multicast는 승인 목록에 넣지 않는다. 격리된 V27 로컬 시험에 필요한 주소만 별도 후보 설정에 기록한다. 공개 서버는 허용된 지원 PC/relay 목적지 목록을 운영자가 관리한다. quota와 lifetime이 무제한 allocation을 방지한다.

시그널링의 `RA_TURN_URLS=turn:relay.example:25002?transport=tcp,turns:relay.example:25003?transport=tcp`, `RA_TURN_SECRET_FILE`을 같은 키로 설정한다. 서버는 발급 시 파일을 다시 읽고 무효 파일이면 발급/readiness를 거부한다. 키 교체는 coturn과 signaling 설정을 조율하며 신규 키 배치→양쪽 refresh/reallocation→구 키 만료/제거 순서다. 단일 static key의 무중단 중첩이 필요하면 coturn의 보호된 TURN secret DB를 사용한다. 현재 compose의 단일 키 교체 중 allocation 실패는 세션 종료로 처리하며 자동 재승인하지 않는다. 만료만으로 기존 allocation이 즉시 폐기된다고 주장하지 않는다.

실제 allocation·TLS/TCP·갱신·키 교체·목적지 거부는 V27~V29에서 확인한다. 설정 생성이나 후보 수집을 relay PASS로 기록하지 않는다.
