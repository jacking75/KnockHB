# 제품 검증 하네스와 장애 묶음

**현재 Arm과 invoke-product-local 직접 실행은 차단됐다.** 30분 재리뷰에서 전역 deadline/독립 watchdog·유한 SCM/정리·실제 복귀 증거가 부족함을 확인했다. PrepareOnly/Finish/Abort/Collect는 유지한다. [구현계획 R30](../../원격제어-구현계획.md)와 [보수적 리뷰](../../docs_working/e2e-30-minute-review-2026-10-08.md)의 R30-05~08이 끝나기 전에는 아래 과거 Arm 절차를 실행하지 않는다.

최신 기준은 전체 목표600초·절대 상한900초다. campaign-plan.json version1의1800초 예산은 폐기된 검토 자료이며 현재 실행 계획이 아니다. 구현계획 R30-02/03에서 새 예산과 검사기를 재설계/검증한다. readiness.template.json의 빈 값/false는 미확인이다. `node scripts/check-e2e-readiness.cjs tests/e2e/campaign-plan.json tests/e2e/readiness.template.json .artifacts/readiness-review.json`은 종전 파일 모델만 검사하고 version1에서는 항상 BLOCKED_BEFORE_START/exit2다. 이 결과는900초 준비 완료 증거가 아니다. 준비 양식을 true로 채워 runtime guard 구현을 대신하지 못한다. 입력/서비스/네트워크를 실행하지 않으며 제품 PASS를 판정하지 않는다. `node --test tests/e2e/readiness.test.cjs`는 종전 모델의 거부 회귀다.

다음 설명은 기존 하네스의 계약/남은 보완 근거다. generic features 선택과180초 TURN 시나리오는 현행30분 계획의 실행 지시가 아니다. 현재 범위/증거/시간은 campaign-plan.json과 보수적 리뷰의 고정 case를 따른다.

진행/판정은 루트 구현계획에서만 관리한다. 이 문서는 명령·준비·증거 계약이다. E 단계에서는 실행하지 않으며 Gate I 이후 V에서 준비·리허설한다. F는 두 물리 PC의 별도 최종 수용이다.

`powershell -NoProfile -File scripts/verify-local.ps1`은 비대화형 restore/build/C#/Go/JS를 deadline 안에서 실행하고 명령·종료 코드·시간·출력을 기록한다. `-Race`는 Windows C compiler 준비 후 별도로 추가한다. `-Native`는 FFmpeg synthetic encode/decode probe이며 브라우저 E2E 성공이 아니다. PowerShell의 ParseFile로 모든 준비/설치 스크립트 문법을 검사한다. 긴 부하는 `RA_LONG_LOAD_SECONDS=600` 또는 7200과 `go test -run TestLongLoad -v -timeout 125m ./internal/signaling`로 화면 점유 없이 실행한다. 기본 시험의 100룸/200연결 1초 shutdown 회귀는 10분/2시간 수용을 대신하지 않는다.

화면 묶음은 `scenarios.json`의 한 시나리오만 선택한다. `Prepare`는 새 후보 publish·파일 hash·계정/Session·도구 경로를 기록하며 화면/입력을 실행하지 않는다. 실제 signaling/coturn/Viewer·시험 창·인증서·desktop·전경·복귀 절차를 별도로 준비하고 계획을 리뷰한다. readiness JSON에는 `reviewed`, `stopRecoveryReviewed`, `operator`, `desktop`, `scenario`, `endpoint`를 넣는다. 입력 묶음에는 실제 `targetPid`, `targetImage`, `foregroundConfirmed`, `geometry`를 넣고 `RemoteAssist.InputTarget`의 현재 snapshot을 근거로 한다. 관리자 묶음에는 현장 `localRecoveryOperator`, `recoveryProcedure`가 필요하다. TLS 오류를 우회하지 않는다.

`Arm -RunDir <prepared> -Readiness <reviewed.json>`은 후보 hash를 재확인하고 관리자 설치 감독을 시작한다. 20초 안에 소유 Tray 창이 없으면 즉시 finish를 기록한다. 실제 Tray에서 사람이 승인한다. 앱에 자동 승인 경로는 없다. 준비 신호·서비스 시작은 기능 PASS가 아니다. 화면 점유는 기본 240초/최대300초이며 생성/빌드는 그 전에 끝낸다. Viewer/입력 대상은 별도 소유 세션으로 관리하고 동일 머신 Viewer 포커스에 따른 blur/reset을 먼저 확인한다. 코드/PIN 파일은 ignored artifact만 사용하며 로그 제출 전에 제거한다.

첫 실패·시간 초과·준비 신호 누락은 `Abort`로 종료하고 `Collect`에서 서비스·자식·배치 정리가 모두 true인지 확인한다. 새로운 실행 묶음은 새 Prepare만 허용한다. 제품 프로세스나 OS 상태가 남으면 일반 작업 복구부터 수행한다. 시험 창/브라우저/방화벽 규칙은 readiness에 기록한 소유자만 닫거나 복구한다. 현장 조작자는 UAC/잠금에서 Ctrl+Alt+Shift+F9와 물리 키보드 복귀를 준비한다.

관찰 파일은 배열이며 각 항목은 `id`, `result:PASS|FAIL|BLOCKED`, `actualSeconds`, `evidence`가 필수다. evidence에는 후보·대상/PID·승인 ID·영상/채널/OS 이벤트·단조 시각·정리 경로를 포함한다. `Collect -Observation <file>`은 실제 관찰이 없는 결과를 BLOCKED로 기록하며 미선택/미실행 시나리오를 그대로 남긴다. 수동 관찰은 사용자 서명/검토를 첨부한다. 전체 F 완료로 해석하지 않는다.

보안/장애 묶음은 기존 C# Input/File/Audit/Recovery/Admin/Deployment/Quality 회귀, Go framing/room/operations/generation, JS connection/features/files/runbooks 회귀와 연결한다. 디스크 full은 격리 VM의 작은 시험 볼륨으로만 유도하고 원본 사용자 파일은 보존한다. 업데이트 변조/다운그레이드/ZIP 탈출·symlink·길이·hash는 직접 회귀로 검사한다. 실제 전원 차단/rollback은 VM snapshot으로 V37을 수행한다. native hang/service crash는 `invoke-product-local.ps1`의 소유 서비스 PID/이미지 재검증 후 `crash-service` 신호로만 주입한다. 복원 예약 ACK 후 실제 down/변경 상태와 독립 Recovery up을 계측해야 PASS다. 일반 SendInput·shared mapping 단위 시험은 실제 UAC/키 복원 수용을 대신하지 않는다.

반복/누수는 고정 후보에서 start/stop100회와 2시간 묶음의 handle/GPU/managed heap/goroutine/queue 시계열을 보존하고 시작 전 기준과 비교한다. GC나 단일 shutdown 성공으로 누수 없음을 선언하지 않는다. 실행 환경이 없으면 해당 V/F를 BLOCKED로 남긴다.
