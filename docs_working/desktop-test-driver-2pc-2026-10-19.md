# DesktopTestDriver 2PC 실행 계획 — 2026-10-19

**현재 상태: 사용자 승인 후 실행 준비.** 자동 승인 검토가 처음에는 대상 PC의 공개 신뢰 저장소·사용자 `DTD_TRUST_STORE` 환경변수 설정 및 임시 Interactive 예약 작업 생성/실행을 차단했다. 사용자가 이 원격 작업을 명시 승인했고, 환경변수는 다음 시험을 위해 유지하라고 지시했다. 대상 계정 전용 서명 키·공개 신뢰 저장소도 함께 유지한다. 시험 회차의 Client·fixture·예약 작업과 임시 서명용 바이너리는 종료 시 제거한다.

## 목적과 범위

- 조작 PC `F:\github\KnockHB`의 Server에서 `192.168.0.74`의 Client를 SSH로 배치한다.
- D21의 패키지 hash, 콘솔 Session 1, Client/Watchdog, 읽기 전용 화면 관측, 중단·수집·소유 자원 제거를 한 회차로 확인한다.
- D22의 실제 입력 및 장애 주입은 **실제 R1 해제·복귀 증거가 확보된 경우에만** 별도 짧은 회차로 진행한다. 현재 그 증거는 없다. RemoteAssist 제품은 사용하지 않는다.
- 첫 실패에서 그 회차의 기능 실행을 멈추고 정리한다. 같은 회차의 재설치·명령 재전송·코드 수정·시나리오 추가를 하지 않는다.

## 사전 확인 결과

첫 로컬 `verify.ps1`에서 독립 복사본의 동시 heartbeat 파일 교체가 Windows 공유 경합으로 실패했다. `RunStore.Write`에 최대 4회의 짧은 재시도와 임시 파일 정리를 추가한 뒤 고정 SDK 10.0.400으로 전체 검증을 다시 실행했다. 두 번째 실행은 빌드 경고/오류 0개, 기본 계약 28개, SSH/SFTP 모의 11개, CLI 9개, 제품 없는 독립 복사본 28개가 통과했다. 증거는 `desktop-test-driver/artifacts/verify-638c603f529b496bbfff284f7bd44141/`에 있다. 이 검증에는 실제 화면 입력이나 2PC 배치가 포함되지 않는다.

| 항목 | 확인값 |
|---|---|
| 대상 | `192.168.0.74`, `DESKTOP-6Q1OG37`, Windows 11 Pro |
| SSH | TCP 22 열림. 기존 `known_hosts`의 3개 공개키와 live key 모두 일치. Paramiko 4.0.0 준비 |
| 계정·콘솔 | `DESKTOP-6Q1OG37\jacking75NoteBook`, 활성 console Session 1, SSH 관리자 토큰. 암호는 `jacking75NoteBookPW` 환경변수에서만 읽음 |
| 대상 런타임·공간 | `Microsoft.WindowsDesktop.App 10.0.11`, C: 여유 약 713GB |
| 대상 기존 상태 | `C:\DesktopTestDriver` 없음, `DesktopTestDriver-*` 예약 작업 없음, SSH 서비스 실행 중 |
| 빌드 | 작업공간 내부 SDK 10.0.400으로 core 독립 검증 28개, SSH 모의 11개, CLI 9개, 독립 소스 복사 28개 통과 |
| 후보 패키지 | `desktop-test-driver/artifacts/package-b81583a32caf41d9bf5a63e36da3d47d`, manifest SHA-256 `c306449d7bde16a5fd5bd465bcecf8a9f3467b82199685e2169e20fdf530ba73`, core 파일 67개 |
| Inventory·증거 | `desktop-test-driver/private/inventory-74.json`의 R0 inventory가 Server `validate` 통과. `desktop-test-driver/private/evidence/`는 로컬 현재 계정만 허용하는 ACL로 준비 |
| 복귀 등급 | R0 관측만 준비 가능. R1 실제 입력 해제·정상 desktop 복귀 실증은 없음. R2도 없음 |

## D21 실행 전 완료 조건

1. 대상 시험 계정에서 `owner-init`으로 서명 키를 만들고 개인키는 대상에만 둔다. 공개 신뢰 목록만 Server에 복사한다. 대상에서 `authorize`로 회차를 서명한다. Server의 현재 계정과 대상 계정이 다르므로 Server에서 대상 소유자 서명을 가장하지 않는다. 회차 밖 신뢰 디렉터리는 대상 계정 전용 ACL로 만들고, 임시로 배치한 서명용 Server 바이너리는 사용 후 제거한다. 대상의 `DTD_TRUST_STORE`가 Broker와 **새 Interactive 예약 작업의 Client**에서 모두 보이는지 입력 없는 임시 작업으로 확인한다. 사용자 지시에 따라 신뢰 디렉터리·사용자 환경변수는 유지한다.
2. `R0`, `allowInput:false`, `observer`, Session 1, `Default`, 10분 이내 만료 설정을 서명하고 로컬 Server에서 inventory/config 서명·hash를 검사한다. 대상 설치 루트는 새 `C:\DesktopTestDriver`만 사용한다.
3. Python SSH orchestration의 설치→즉시 heartbeat→status/inspect→stop→collect→remove 순서를 코드 리뷰한다. 모든 SSH 호출은 host key pin, 5~10초 단일 호출 제한이며 설치는 120초 이내다. 중단/수집/제거 호출은 소유 회차 ID/hash만 사용한다.
4. 원격 정상 desktop, 연결, 예약 작업 부재, 증거 공간, 로컬 package hash가 다시 확인돼야 Arm한다. 준비 신호가 빠지면 설치도 하지 않는다.

## 한 회차의 판정과 시간

전역 단조 deadline은 첫 설치 호출부터 **목표 10분, 절대 15분**이다. 화면 점유 구간은 읽기 전용 `inspect`만 수행하며 5분 이내다. 설치·전송은 화면 점유 전에 끝낸다. 실동작 직전 사용자에게 예상 점유와 중단 조건을 알린다.

1. `install` 단 한 번: 새 run ID 하위에만 전송·검증하고 `prepared:true`를 확인한다. **이 응답만으로 PASS 처리하지 않는다.** 설치 120초 초과·hash/콘솔 불일치 시 즉시 중단한다.
2. 설치 응답 직후 별도 heartbeat를 3초 lease보다 빠르게 유지한다. `status`에서 Client/Watchdog PID·생성 시각·경로·hash와 설정 hash를 확인한다.
3. `inspect`에서 Session 1·`Default`·비상승 일반 콘솔·모니터를 확인한다. 화면 픽셀·좌표·입력은 사용하지 않는다. 관측 불가면 FAIL이다.
4. `stop`에서 `clean:true`, 빈 입력/프로세스 ledger와 정상 desktop을 확인한다. `collect`로 감사·status·cleanup 원본과 hash를 비공개 디렉터리에 수집한다.
5. config/package hash로 `remove`를 호출하고 해당 run 하위만 제거됐는지 확인한다. 기존 SSH 서비스·사용자 파일·다른 예약 작업은 유지돼야 한다. SSH 재접속과 활성 console 확인을 마지막에 한다.

PASS는 실제 두 PC의 상태·관측·정리 증거가 모두 있을 때만 부여한다. 첫 오류·deadline·사용자 물리 입력/세션 변경 때 새 명령을 봉인하고 `stop` 및 소유 자원 정리로 간다. 정리 실패는 미정리로 보고하고 추가 회차를 시작하지 않는다.

## D22/D23 잔여 기준

R1은 별도 watchdog의 **실제** 입력 해제, fixture/작업 정리와 일반 desktop 복귀를 독립 증거로 증명해야 한다. 모의 receipt, 필드만 맞춘 JSON, R0의 빈 키 ledger는 R1이 아니다. 증거가 없으면 D22 입력·장애 회차를 `BLOCKED`로 남긴다. D23의 전체 독립 수용은 D22와 최종 제거·복귀가 모두 끝난 뒤에만 판정한다. UAC·잠금·SAS·재부팅은 R2가 없어 실행하지 않는다.

## 22:06 KST — 배포·기동·통신 확인

대상 192.168.0.74, 회차 dtd-r0-20261019125655, 실행 19.22초. 전송/hash 검사와 Client PID 2788, Watchdog PID 17736의 Session 1/Default 기동을 확인했다. 통신 검사는 RuntimeError로 FAIL이며 inspect/heartbeat 성공 증거가 없다. 상세 예외 단계가 기록되지 않아 원인은 미확정이다. stop/수집/remove 성공, clean/normalDesktop=true, 빈 ledger, SSH 재접속과 예약 작업 0개를 확인했다. 신뢰 저장소/환경변수는 유지했다. 재시험하지 않았고 D21 완료로 처리하지 않는다. 증거: desktop-test-driver/private/evidence/connect-20261019-2205/.
