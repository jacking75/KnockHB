# 30분 E2E 착수 전 보수적 리뷰

진행 상태와 준비 태스크는 [구현계획 2.1장](../원격제어-구현계획.md)에서만 관리한다. 기준 전달 commit은 f440dfccc219a6a918453b3224f486fc03815678이다. 이번 작업은 코드/문서 검토와 입력 없는 검사이며 E2E 실행 기록이 아니다. 현재 **NOT READY / ARM BLOCKED**다.

## 발견한 부족과 착수 차단 조건

| 발견 | 코드/문서 근거 | 필요한 보완과 준비 완료 증거 |
|---|---|---|
| 전체 회차의 clock이 없다 | product-e2e-harness의 Seconds는 단일 실행만 제한한다 | campaign ID 한 개와 단조 시작 tick을 소유 controller가 고정한다. 새 프로세스/시나리오/Prepare가 시작 tick을 바꾸지 못해야 한다 |
| UAC 승인이 무한 대기할 수 있다 | Start-Process -Verb RunAs가 전역 timer 밖이다 | 관리자 감독과 설치 준비를 실행 전에 끝낸다. 동의와 UAC 조작은 시험 시간에 포함한다. 호출이 멎어도 독립 watchdog이 기능 작업/정리를 시작하는 고장 주입 증거가 필요하다 |
| 설치/서비스 시작 뒤에야 clock이 시작된다 | invoke-product-local의 stage copy/New-Service/Start-Service 다음 Stopwatch다 | copy/install/publish는 사전에 완료한다. 첫 시험 동작 전에 clock을 시작한다. 작업 시작 후 발생한 복사/설치/대기 시간을 제외하지 않는다 |
| stop/delete가 bounded가 아니다 | finally의 Stop-Service/sc.exe delete는 deadline이 없다 | 소유 service image/PID/start-time 확인 후 각 명령의 10초 상한·독립 프로세스 종료를 구현한다. 전체 정리는 300초에 제한한다 |
| owner 확인이 장애 주입에만 있다 | cleanup은 이름으로 Stop-Service/delete한다 | 정리 직전에 서비스 경로·PID/시작 시각·Job/launch ID를 다시 확인한다. 다른 서비스/재사용 PID는 건드리지 않고 FAIL_RECOVERY_REQUIRED로 전환한다 |
| 입력 복원이 무조건 확보됐다고 볼 수 없다 | Recovery는 Job 밖이며 5초 이후 stage를 남긴다 | 일반 종료와 crash에서 OS 실제 held 상태/Recovery ACK와 완료를 먼저 관측한다. 키가 남으면 다음 단계 금지, 물리 키보드/VM 외부 복귀로 전환한다 |
| 정리가 모든 자원을 덮지 않는다 | cleanup.json은 service/stage/자식 세 항목이다 | Viewer/시험 창·시그널링/coturn의 소유 상태·방화벽/네트워크·모니터/desktop·스케줄/잠금·키 상태를 각각 기록한다. 기존 사용자의 프로세스/서비스는 유지한다 |
| 급히 지운 경로가 reparse로 바뀔 수 있다 | stage 접두사 검사는 생성 전이며 재귀 Remove 직전 재검사가 없다 | 정리 전 최종 절대 경로·관리자 ACL·reparse 없는 트리/소유 marker를 확인한다. 검사 실패 시 그 트리를 삭제하지 않고 외부 복귀한다 |
| 실제 Tray/관측의 신선도와 소유가 약하다 | MainWindowHandle와 readiness의 boolean을 쓴다 | SID/Session/desktop·실제 owner PID/start-time/image hash·현재 window handle·snapshot monotonic tick·foreground·geometry를 런타임 확인한다. 사전 boolean으로 클릭 허용하지 않는다 |
| InputTarget에 고정 화면 위치가 있다 | Left=3000/Top=450 | 사전 snapshot을 승인 모니터의 실제 bounds에 대조한다. 범위 밖이면 계획 단계에서 배치/관측 도구를 고치고 실행하지 않는다 |
| 모든 키/버튼 해제를 현재 observer로 증명할 수 없다 | InputTarget은 Ctrl/F12와 일부 이벤트만 집계한다 | down/up 이벤트와 모든 주입 키/버튼의 실제 OS held 상태·reset epoch/ACK를 기록할 observer가 필요하다. Ctrl=false 하나로 '키 상태0'을 판정하지 않는다 |
| TURN 갱신 대기 시간과 시나리오가 맞지 않는다 | RefreshLoop는 8분, 기존 transport-recovery는180초다 | 실제 session/refresh 시작 tick을 기록하고 8분 갱신 전후를 관측한다. 아래 7~12분 전용 창으로 예약하고 처음부터 TURN 사용 조건을 준비한다 |
| TURN 중 재승인/파일 작업이 충돌할 수 있다 | 새 transport가 feature 작업을 취소하고 승인 중 fail-closed한다 | 갱신 예상 시각 전 파일/로컬 승인/Runbook을 끝낸다. 기존 세대 종료·새 DTLS/SCTP·실제 새 allocation·세션 권한을 확인한다 |
| TTL 만료/양쪽 credential 증거를 자동으로 얻지 못한다 | 실제 allocation/expiry는 기존 artifact에 없다 | 서버와 양쪽 selected pair/credential expiry의 비밀 없는 관측을 사전에 설치한다. product TTL을 임의 축소하거나 공개 test bypass로 대신하지 않는다 |
| features는 선택 문구여서 누락될 수 있다 | 기존 scenarios의 '또는'/'한 기능 묶음'이다 | 파일 두 방향/거부/중단·채팅/clipboard·모니터·Runbook 등 case ID를 고정한다. 회차의 PASS와 전체 F PASS를 분리한다 |
| 다른 시나리오의 수동 관찰도 수용된다 | Collect는 전체 prepared.scenarios의 ID만 검사한다 | 이번 campaign의 선택 case/subcase ID와 후보·시간 범위·증거 hash가 일치해야 한다. 중복/범위 밖/추측 actualSeconds는 거부한다 |
| 2분 관리자 lease를 무시한 묶음은 끝낼 수 없다 | AdminAuthorization/동의 UI의120초, secure desktop30초다 | UAC/SAS/잠금 각각의 실제 단계와 복귀를 미리 측정한다. 180초 창에 전부 들어간다는 측정이 없으면 일부 선택 후 전체 F05는 미완료로 남긴다 |
| 일반/보안 desktop 종료 단축키가 다르다 | NativeTrayIcon F10, SecureStopWindow F9 | 일반 Ctrl+Alt+Shift+F10, 보안 Ctrl+Alt+Shift+F9을 구분한다. 등록 성공/충돌과 현장 종료 버튼을 확인한다. 잠금에서 일반 Tray hotkey를 믿지 않는다 |
| 장시간/다중 환경을30분에 넣을 수 없다 | product-contract: 2시간/100회/10분100룸, F02/F07/F11/F12 | 같은 후보의 사전 증거로만 재사용한다. 없으면 전체 수용은 BLOCKED, 기준을 삭제하거나 짧은 smoke로 승격하지 않는다 |
| 로그/시계/공간 준비가 부족하다 | observation은 수동 text이고 deadline/exit 보장이 없다 | 실행 전 저장소 용량·flush/read 권한·관측 동작·각 PC clock 오차를 확인한다. 단조 시간으로 지연을 측정하고 개인정보가 없는 결과를 미리 정한 경로에 수집한다 |

## 실행 전에 끝낼 작업

1. controller/Host/Viewer의 역할과 별도 관리 연결을 정한다. 시험 NAT/TURN 경로를 바꿔도 관리·로그 수집·중단 경로가 살아 있어야 한다. SSH 성공은 제품 PASS가 아니다.
2. candidate commit/tree, signed installer manifest, 각 exe/DLL/FFmpeg/Viewer/settings/source bundle hash를 고정한다. 모든 검사/실증 증거의 후보 일치·환경 일치·경계 영향을 리뷰한다. 이후 코드/설정 변경은 READY를 폐기한다.
3. 서명 설치/업데이트/rollback·실제 native encoder/decode·지원 GPU·2시간/100회/100룸 부하·race·공격 회귀를 화면 없는 별도 사전 검증으로 끝낸다. 해당 준비가 없는 상태에서30분 E2E부터 켜지 않는다.
4. 빈 시험 폴더·합성 UTF-8/비BMP 텍스트·1MiB 고정 파일 두 개와 SHA256·일반/승격 시험 창·모니터 복귀값·고정 Runbook ID/hash/영향·폐쇄 로그 경로를 준비한다. 업무 앱/개인 파일/실제 암호 입력은 시험 대상으로 쓰지 않는다.
5. Viewer observer는 실제 decodedFrames·selected candidate pair 양끝·채널/세대/inputEpoch·reset ACK·geometry·monotonic event를 수집한다. InputTarget observer의 부족은 코드를 보완한 뒤 사전 증거로 확인한다. 브라우저 automation의 focus/blur 영향도 이 단계에서 확인한다.
6. 현장 조작자와 대체 조작자를 정하고 정상/보안 desktop의 중단·물리 키보드·VM console/검증된 snapshot 복귀를 준비한다. controller가 죽거나 네트워크가 끊겨도 독립 watchdog/현장 경로가 동작해야 한다.
7. R30-05의 입력 없는 고장 주입 회귀를 먼저 통과한다. OS 복귀/키 해제는 격리 환경에서 별도 준비 실증한다. 단위/fake 검사로 그 실증을 대신하지 않는다.
8. 아래 각 case의 단계 명령·관찰 selector/실제 snapshot·expected event·deadline·cleanup owner를 채운다. 준비 양식의 빈 값/UNVERIFIED와 미완료 R30가 하나라도 있으면 시작하지 않는다.

## 고정된 22분 기능 창

다음은 최대 예약 시간이며 자동 실행이나 측정된 소요 시간이 아니다. 사전 증거로 확인된 variant만 동결하고 검사기가 시간표/증거 누락을 검사한다. 실패는 다음 case로 넘기지 않는다. 테스트 방법을 실행 중 검색하거나 즉흥적으로 추가하지 않는다.

| 경과 | case / V-F | 정확한 관찰·증거·판정 |
|---|---|---|
| 0~2분 | consent-view / V16,V19,V24,F01,F04 | 준비된 두 역할·코드/PIN·Host identity 확인→일반 Tray에서 직접 부분 승인→승인 전 actual media frame0/미승인 동작0→승인 후 올바른 영상 decodedFrames 증가·상시 표시를 관찰한다. 단계별30초/첫 영상15초, 증거 없으면 중단한다. main session 시작 tick을 기록하고 이후 TURN 창까지 유지한다 |
| 2~5분 | input / V21~23,F03 | snapshot≤1초·foreground/모니터 bounds 확인→시험 창에서 절대 좌표 모서리/drag/wheel/한글·비BMP·조합키를 정해진 event로 확인→blur/reset ACK≤1초와 실제 주입 키/버튼 held0. observer에 없는 항목은 PASS를 기록하지 않는다 |
| 5~7분 | file-chat-clipboard / V30~32,F06 | 준비한1MiB 파일 각 방향의 건별 승인→해시/길이 비교→다른 transfer 거부/취소 후 partial0→고정 채팅/clipboard text1회와 loop0. 단계별30초; consent/IO 지연으로420초를 넘길 것 같으면 중단한다 |
| 7~12분 | turn-refresh / V27~29,F07 | 작업/동의 pending0 확인→실제8분 timer의 현재 session tick에서 예약한 refresh를 관찰→양끝 selected pair relay와 새 allocation/expiry·generation 증가·구세대 차단·실제 decode 재개. 복구 단계30초, 총재협상90초 상한. timer가 이 창에 들어오지 않으면 준비 오류로 BLOCKED, TTL 변경/즉석 재접속 금지. 다른 NAT/TCP/TLS variant는 사전 증거 없으면 전체F07 미완료 |
| 12~14분 | monitor-quality / V30,V35,F02,F03,F11 | 사전 확인한 두 monitor identity/geometry로 switch 로컬 승인→capture/IDR/geometry ACK→새epoch 실제 target/event 확인→계측된 품질 sample을 기록한다. 평균FPS/p95는 사전 실측과 연결하며 설정값으로 통과시키지 않는다. 실제 두 모니터 없으면 착수 gate에서 BLOCKED |
| 14~17분 | diagnostic-runbook / V33,V34,F08 | 등록 hash/전문/영향 확인→해당ID 일반 Runbook approve→20초 이내 결과·감사 연관/출력 상한→다음 고정ID 요청 거절 또는취소 후worker0. diagnostics-only/관리자 Runbook variant는 같은후보의 사전 증거를 연결하거나 F08 미완료다 |
| 17~20분 | admin-desktop / V25,V26,F05 | 일반 제어에서 특권 접근 거부→별도현장 승인→미리 동결한 UAC/SAS/잠금 variant의 실제desktop/입력 관찰→F9/현장 버튼철회→일반 desktop복귀/held0. 보안desktop30초·관리자120초를 지킨다. 미측정 복귀·미선택 variant는 PASS가 아니다 |
| 20~22분 | owner-fault-stop / V22,V24,V40,F04,F09 | 사전결정한 단일 owned helper/service 장애를 PID/image/start-time 재검증 뒤 주입→up-only Recovery·새입력/새송신1초내차단·재동의없는 재개0→잔여권한0. 끝나면 기능검사를종료한다. Tray/server/user-switch/RDP 모든variant는 사전증거 없으면F09 미완료다 |

이 표에는 역할 교환/모든 GPU·NAT·모든관리자variant·2시간부하·서명업데이트전원차단을 동시에 실행하지 않는다. 해당 F 하위 조건의 사전 증거를 목록으로 연결한다. **일부 관찰 성공만으로 전체 F01~F12/Gate B를 체크하지 않는다.** 전체 회차를 PASS라고 부르려면 고정 범위의 모든 case와 정리가 통과해야 하며 전체제품수용은 별도다.

## 전역 deadline과 정리의 구현 계약 — R30-05 미구현

clock은 첫 제품 시험 동작 전에 시작하고 22분 기능 종료·27분 정상정리·29분외부복귀·30분결과닫기를 monotonic tick으로 강제한다. 시스템 UTC 변경/재연결/자식 재시작으로 연장하지 않는다. child는 부모의 남은시간보다 긴 timeout을 받을 수 없다. 준비/관측 controller와 별도 watchdog이 서로의 lease와 campaign ID를 검사한다. 감시 프로세스가 죽으면 owner가 기능 동작을 더 실행하지 않는다.

watchdog은 제품 동의/권한을 승인하지 않는다. 정리 요청은 idempotent이며 stop/cleanup ledger를 원자적으로 쓴다. 제어권을 먼저 차단한 뒤 소유 service/Job/입력복원/시험창/Viewer/방화벽/설정 순서의 복귀를 수행한다. Recovery는 실제 up 확인 전 임의 kill하지 않는다. 정상 정리가 실패하면 사전에 검증한 현장/VM console로 복귀한다. 이름만으로 전역 taskkill/다른서비스삭제/재귀삭제하지 않는다.

필수 입력 없는 회귀: UAC 호출 무응답, SCM start/stop/delete 무응답, controller/watchdog각각죽음, child끝나지않음, UTC앞/뒤변경, 같은campaign재Arm, 동일PID재사용, 서비스image교체, stagejunction삽입, 파일hash변경, 증거쓰기실패, observation중복/누락/다른case/후보불일치다. fake monotonic clock으로22/27/29/30분 경계를 검증하고 소유 child 종료/유한exit를 확인한다. 실제키/보안desktop/VM복귀는 이후격리준비실증이필요하다.

## 결과와 중단

첫 결함이면 즉시 abort reason/case/tick을 기록하고 남은 case를 NOT RUN으로 남긴다. 증거수집과 종료를 먼저 수행한다. 수집하지 못한 로그는 누락으로 판정하고 화면을 붙잡은 채 기다리지 않는다. 종료결과는 `PASS_SELECTED_SCOPE`, `FAIL`, `BLOCKED_BEFORE_START`, `FAIL_RECOVERY_REQUIRED`로 구분한다. 정리 불완전이면 PASS 금지다. 30분 이내의 결과파일과 사용자보고는 항상 남기며, 외부복귀가 불가능한 환경은 처음부터 착수하지 않는다.

사전 gate가 BLOCKED면 UI/서비스/UAC/입력을 시작하지 않고 빠진준비와담당작업만보고한다. 실패후같은30분 안에수정하거나새시나리오를시작하지않는다. 수정과준비검증은회차를닫은뒤 별도작업이며 다음E2E는 별도요청/일정으로만수행한다.
