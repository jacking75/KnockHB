"""Maintain implementation links, not acceptance status. --patch prints a patch.

Reads all 16 plans, never runs a case or contacts DesktopTestDriver.
"""
import argparse
import ast
from pathlib import Path
import re
import sys

from e2e_case_steps import CASES
from e2e_case_runtime import live_blockers
from e2e_case_source_contracts import contract

ROOT=Path(__file__).resolve().parent.parent
START='<!-- case-implementation-20261021:start -->'
END='<!-- case-implementation-20261021:end -->'
TESTS={
 'C01':'test_c01_stop_boundary_video_and_preconsent',
 'C02':'test_c02_cumulative_frame_leak_and_real_deny',
 'C03':'test_c03_unicode_wheel_reset',
 'C04':'test_c04_privileged_boundary_and_local_recovery_not_product_success',
 'C05':'test_c05_screen_kind',
 'C06':'test_c06_wrong_sid_or_old_permission',
 'C07':'test_c07_c08_chunk_hash_and_close','C08':'test_c07_c08_chunk_hash_and_close',
 'C09':'test_c09_deny_not_timeout_or_bytes','C10':'test_c10_ui_not_just_sent_frame',
 'C11':'test_c11_restore_and_response_deadline','C12':'test_c12_missing_capture_and_null_telemetry',
 'C13':'test_c13_worker_count_privilege_and_installed_hash','C14':'test_c14_ack_before_resume',
 'C15':'test_c15_selected_pair_not_candidate_presence','C16':'test_c16_fault_identity_order_and_tool_up'}


def render(case):
    source=(ROOT/'scripts/e2e_case_steps.py').read_text(encoding='utf-8')
    lines={}
    for node in ast.walk(ast.parse(source)):
        if isinstance(node,ast.Call) and isinstance(node.func,ast.Name) and node.func.id=='S':
            lines[node.args[0].value]=node.lineno
    rows=[]
    for i,step in enumerate(CASES[case],1):
        actions=', '.join('`'+str(a[0])+'`' for a in step.actions) or '읽기 관측'
        observations=', '.join('`'+s+'`' for s,_ in step.checks)
        evidence=', '.join('`'+s+'`' for s in step.evidence)
        rows.append(f'| {i:02d} / {step.name} | [CASES["{case}"][{i-1}]](../scripts/e2e_case_steps.py#L{lines[step.name]}) | {actions} → {observations} | {step.seconds:g}초 | {evidence} |')
    blockers='\n'.join('- '+b for b in live_blockers(case))
    meta=contract(case,'all')
    scope='C15는 이번 범위 EXCLUDED다. 아래 코드는 향후 재포함 시 보존 명세다.' if case=='C15' else '현재 대상은 C01~C14·C16의 15개다. C15/TURN collector는 이 사례의 외부 조건이 아니다.'
    return f'''{START}
## 11. 2026-10-21 코드 연결·입력 없는 검증 결과

{scope}

**실행 구현 연결 / 실제 E2E 미실행이다.** 진입점·고정 단계·실제 source adapter·bootstrap·정리·독립 복귀를 연결했다. 아래 코드는 4절의 파일 미생성 상태와 이전 부분 구현 기록을 갱신한다. 입력 없는 검증 통과를 기능 PASS나 현장 실행 준비 완료로 세지 않는다.

- 진입점: [e2e-{case}.ps1](../scripts/e2e-{case}.ps1). `Validate`는 로컬 설정 검사다. `Prepare`는 사전 증거 검사·파일 동결·DTD status·신규 v4 패키지 배치·비기동 준비를 수행한다. 증거/외부 조건이 부족하면 `BLOCKED/10`이다. 이번 작업에서는 Prepare와 Run을 호출하지 않았다.
- 공통 실행: [e2e_case_runtime.py](../scripts/e2e_case_runtime.py)의 `Context.begin/once`, `execute_steps`, `stop_and_recover`, `write_result`. 문서의 `Invoke-{case}-...` 함수들은 아래 `CASES`의 불변 `Step` 정의와 공통 executor로 구현 위치를 변경했다. 순서/상한/관측 predicate/증거 목록은 코드 상수다.
- 실제 호출: [e2e_case_adapters.py](../scripts/e2e_case_adapters.py)의 `Dtd`, `CaseBackend.action`, `Harness.call`, `OwnedGateway`. 기존 `e2e-native.cs`의 Job과 `e2e-product-client.py`의 소유 manifest transport를 재사용한다. selector 0/복수개·owner 불일치·미확정 요청은 실패하며 임의 명령 입력 경로는 없다.
- 실제 관측: [e2e_case_live.py](../scripts/e2e_case_live.py)의 `LiveObserver`, [e2e_case_sources.py](../scripts/e2e_case_sources.py)의 `assemble/read`, [e2e_case_observations.py](../scripts/e2e_case_observations.py)의 `derive`. 브라우저 메타데이터, 현재 제품 trace/audit, DTD 소유 창, OS/fixture 증거를 조합한다. 누락을 기대값으로 대체하지 않는다.
- `Run -Execute`는 `verify_frozen`과 `readiness`를 통과한 회차만 `execute_steps`에 연결한다. `Abort`는 먼저 stop을 기록하고 기존 실행기의 복구를 기다리며, 실행기 종료가 확인된 경우에만 `finish_existing`이 동일 origin/소유 session으로 복구한다. `Collect`는 저장 증거 재검사이며 현재 데스크톱 복귀 증명이 아니다.
- 부속 구현: [e2e_case_campaign.py](../scripts/e2e_case_campaign.py)의 `package_campaign/Campaign`, [e2e_case_desktop.py](../scripts/e2e_case_desktop.py)의 `DesktopSources`, [E2eDesktop Program.cs](../scripts/e2e-desktop/Program.cs), [ClipboardSnapshot.cs](../scripts/e2e-desktop/ClipboardSnapshot.cs), [e2e_case_recovery.py](../scripts/e2e_case_recovery.py)의 `finish_existing`. 기존 하네스·DesktopTestDriver API1을 재사용한다.

| 계획 단계/이름 | 실제 정의 | 고정 조작 → 관측/predicate | 단계 상한 | 수집할 증거 |
|---|---|---|---|---|
{chr(10).join(rows)}

위 모든 단계의 **순서·각 단계 첫 실패 중단**은 `RuntimeTests.test_all_case_step_orders_and_first_failure_at_every_step`에서 실제 executor를 기록용 sink로 실행해 확인했다. 전체 16개 정상 순서와 103개 실패 지점, 합계119개 경로다. 이 검사는 live 관측 source나 UI의 실행 성공을 검증하지 않는다. 이 C의 주요 판정 벡터는 `PredicateTests.{TESTS[case]}`로 검증했다. 각 step의 모든 live 필드를 검증한 것으로 확대하지 않는다.

검증은 [test_e2e_cases.py](../scripts/test_e2e_cases.py)의 37개, [test_e2e_case_live.py](../scripts/test_e2e_case_live.py)의 13개, [브라우저 observer 테스트](../tests/e2e/browser-observer.test.cjs)의 5개를 통과했다. 실제 executor의 119개 경로와 live source의 run/sequence/partial record/ACK 인과관계/외부 중단/후보 hash 계약을 검증했다. 제품 전체·E2eDesktop 컴파일과 PowerShell/Python/JavaScript 구문 검사 결과는 [전체 검토·결과](e2e-case-implementation-review-20261021.md)에 연결했다. 테스트는 실제 DTD/SSH/제품/SCM/GUI/UAC/잠금을 호출하지 않았다.

### 11.1 이 C에 남은 미구현·미검증·외부 자원

{blockers}

고정 조작 opcode에 남은 stub/임의 명령 콜백은 없다. C03 fixture/포커스 이탈 창, C11 STA OLE 복원 helper, C16 SYSTEM 단발 소유 프로세스 fault trigger도 구현했다. **실제 A/B 통합·UI selector·OCR·TLS 체인·종단 시간·복구 시간은 미검증이다.** C11은 완전 복제할 수 없는 OLE medium을 만나면 변경 전에 중단한다. C04~C06의 현장 담당자는 외부 자원이다. C15 collector는 향후 재포함 시 조건이며 이번 범위에서 제외한다. 소스 필드가 없거나 실제 제품이 기대 audit/복구 증거를 만들지 않으면 FAIL이며 PASS로 보정하지 않는다. 시간표는 코드 상한이고 실측 예산 증거가 없으면 Arm하지 않는다.

기존 heartbeat Save/Read는 공유 잠금으로 경로조회/open/replace 전체를 직렬화했다. 입력 없는 경쟁 검사에서3572회 읽기를 통과했지만 원래 C01 RuntimeException과 동일 원인이라고 단정하지 않는다. watchdog 예외는 phase/role/정리 여부/예외형 체인/HResult/파일/줄을 남기며 비밀이 들어갈 수 있는 메시지 원문을 출력하지 않는다. SYSTEM/SSH와 사용자 세션 사이 전체 통합 복구는 여전히 미검증이다.
### 11.2 현재 source 필드·생산자·비동기 경계

각 단계 source는 위 단계 표와 `derive(case, source)`의 실제 분기에 연결된다. 7.1절의 표준 관측 필드와 아래 사례별 원본 필드 집합을 함께 사용한다.

| 항목 | 현재 계약 |
|---|---|
| 원본 필드/생산자 | `{meta['raw']}` |
| 정상 도착을 기다리는 경계 | {meta['arrival']} |
| 회차·세대·상관 결합 | {meta['binding']} |
| tick 단위 | {meta['clock']} |
| 부분 기록 | NDJSON 마지막 newline 미도착만 Pending. 완성 행의 오류·중복 key·다른 run·중복/역순 sequence는 즉시 FAIL이다 |
| 관측 상한 | 원래 step deadline과 origin을 유지한다. 조작 재전송과 새 timeout은 없다. 허가/순서/ID/실제 SLA 위반은 즉시 FAIL이다 |

추가 회귀는 [잔여 작업 검사](../scripts/test_e2e_case_remaining.py), [OLE 메모리 검사](../tests/RemoteAssist.E2e.Clipboard.Tests/Program.cs), [감사 경쟁 검사](../tests/RemoteAssist.Host.Core.Tests/AuditRegression.cs)에 연결한다. 최신 검사 수·hash·진입점은 [잔여 작업 완료 기록](e2e-remaining-work-20261022.md)에서 확인한다. B/C 실전 결과와 별개다.
{END}'''


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--check',action='store_true');parser.add_argument('--patch',action='store_true');parser.add_argument('--case',choices=list(CASES))
    args=parser.parse_args()
    changed=[];patches=['*** Begin Patch']
    for case in CASES:
        if args.case and case!=args.case:continue
        files=list((ROOT/'docs_working').glob(f'e2e-{case}-*-20261021.md'))
        if len(files)!=1:raise RuntimeError('Expected one plan: '+case)
        p=files[0];old=p.read_text(encoding='utf-8-sig');block=render(case)
        if START in old:
            a=old.index(START);b=old.index(END,a)+len(END)
            new=old[:a]+block+old[b:]
            before=old[a:b]
        else:
            new=old.rstrip()+'\n\n'+block+'\n';before=None
        if new==old:continue
        changed.append(case)
        if args.patch:
            patches += ['*** Update File: '+p.as_posix(),'@@']
            if before is None:
                last=old.rstrip().splitlines()[-1]
                patches += [' '+last,'+']
                patches += ['+'+line for line in block.splitlines()]
            else:
                patches += ['-'+line for line in before.splitlines()]
                patches += ['+'+line for line in block.splitlines()]
        elif not args.check:p.write_text(new,encoding='utf-8')
    if args.patch:
        patches.append('*** End Patch');print('\n'.join(patches))
    elif args.check:
        print('PASS 16 implementation mappings' if not changed else 'STALE '+','.join(changed))
        return int(bool(changed))
    return 0


if __name__=='__main__':raise SystemExit(main())
