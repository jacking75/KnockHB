"""C01-C16 execution primitives. Import and Validate perform no external action.

Execution and observation dispatch are code-owned; configuration cannot inject
an action or replace a missing observation with an expected value.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import re
import time
import uuid
import sys
from dataclasses import dataclass, field

# The CLI and imported adapters must share the same exception/context classes.
if __name__=='__main__':sys.modules['e2e_case_runtime']=sys.modules[__name__]

ROOT = Path(__file__).resolve().parent.parent
LIMIT = 1048576
OLD_RUN = '6a2f55ebaee74a4099177a271ea0b88c'
MASKS = (1, 0, 3, 7, 7, 7, 8, 8, 8, 128, 16, 32, 64, 67, 1, 3)
CHANNELS = (('control',), (), ('control', 'input', 'pointer'),
            ('control', 'input', 'pointer'), ('control', 'input', 'pointer'),
            ('control', 'input', 'pointer'), ('control', 'file'),
            ('control', 'file'), ('control', 'file'), ('control', 'chat'),
            ('control', 'chat'), ('control', 'telemetry'), ('control', 'runbook'),
            ('control', 'input', 'pointer', 'runbook'), ('control',),
            ('control', 'input', 'pointer'))
FILE_HASH = '2312394bd99545d9de131c24efb781e765ac1aec243f2ed9347597a793a415e9'


class CaseError(Exception):
    def __init__(self, code, detail, exit_code=20):
        super().__init__(detail)
        self.code, self.detail, self.exit_code = code, detail, exit_code


def require(condition, code, detail='', exit_code=20):
    if not condition:
        raise CaseError(code, detail or code, exit_code)


def digest(path):
    h = hashlib.sha256()
    with Path(path).open('rb') as f:
        for block in iter(lambda: f.read(65536), b''):
            h.update(block)
    return h.hexdigest()


def safe_path(path, parent=None, exists=False):
    p = Path(path)
    require(p.is_absolute(), 'absolute-path-required', exit_code=40)
    # Inspect lexical ancestors before resolve, which would hide a junction.
    for item in (p, *p.parents):
        if item.exists():
            require(not item.is_symlink() and not (getattr(item.stat(), 'st_file_attributes', 0) & 1024),
                    'reparse-path', exit_code=40)
    p = p.resolve()
    if parent is not None:
        require(p.is_relative_to(Path(parent).resolve()) and p != Path(parent).resolve(),
                'path-outside-owner', exit_code=40)
    if exists:
        require(p.is_file(), 'missing-file', str(p), 10)
    return p


def read_json(path):
    p = safe_path(path, exists=True)
    with p.open('rb') as f:
        raw = f.read(LIMIT + 1)
    require(len(raw) <= LIMIT, 'json-size-bound', exit_code=40)
    try:
        def unique(pairs):
            out = {}
            for key, value in pairs:
                require(key not in out, 'duplicate-json-key', exit_code=40)
                out[key] = value
            return out
        return json.loads(raw.decode('utf-8-sig'), object_pairs_hook=unique,
                          parse_constant=lambda _: (_ for _ in ()).throw(ValueError()))
    except (UnicodeError, ValueError) as e:
        raise CaseError('invalid-json', type(e).__name__, 40) from e


def new_json(path, value):
    raw = json.dumps(value, ensure_ascii=False, allow_nan=False, indent=2).encode('utf-8')
    require(len(raw) <= LIMIT, 'json-size-bound')
    with Path(path).open('xb') as f:
        f.write(raw)
        f.flush()
        os.fsync(f.fileno())


def get(value, path):
    for key in path.split('.'):
        if not isinstance(value, dict) or key not in value or value[key] is None:
            raise CaseError('observation-unavailable', path)
        value = value[key]
    return value


def numeric(value):
    require(type(value) in (int, float) and math.isfinite(value), 'invalid-number')
    return value


def elapsed(record, start, end, limit):
    # Only ticks belonging to this single source record may be compared.
    frequency = numeric(get(record, 'frequency'))
    require(frequency > 0, 'invalid-frequency')
    a, b = numeric(get(record, start)), numeric(get(record, end))
    require(0 <= b-a <= limit*frequency/1000, 'latency-bound', start + '/' + end)


def equal(actual, expected):
    # Python's True == 1 must not make malformed observations pass.
    if type(actual) is not type(expected):return False
    if isinstance(actual,dict):return actual.keys()==expected.keys() and all(equal(actual[k],expected[k]) for k in actual)
    if isinstance(actual,(list,tuple)):return len(actual)==len(expected) and all(equal(a,b) for a,b in zip(actual,expected))
    return actual==expected


def assert_fields(data, fields):
    for path, expected in fields.items():
        require(equal(get(data, path), expected), 'assertion-failed', path)


def select_unique(items, rule, owner=None):
    require(isinstance(rule, dict) and rule, 'selector-empty')
    require(set(rule) <= {'automationId', 'name', 'controlType', 'title', 'readOnly'},
            'selector-field-unsupported')
    matches = [x for x in items if all(equal(x.get(k), v) for k, v in rule.items())]
    require(len(matches) == 1, 'selector-not-unique')
    x = matches[0]
    if owner is not None:
        for key in ('pid', 'imagePath', 'createdAt'):
            require(get(x, key) == get(owner, key), 'owner-mismatch', key)
    require(x.get('enabled', True) is True, 'element-disabled')
    return x


def bind_observation(record, ctx, step, source):
    assert_fields(record, {'runId': ctx.run_id, 'caseId': ctx.case_id,
                          'stepId': step, 'source': source, 'available': True})
    require(get(record, 'machine') in ('pc-a', 'pc-b'), 'observation-machine')
    require(type(get(record, 'tick')) is int and type(get(record, 'frequency')) is int
            and record['frequency'] > 0, 'observation-clock')
    require('sessionId' in record and record['sessionId'] == ctx.session_id, 'observation-session')
    require('connectionGeneration' in record and record['connectionGeneration'] == ctx.generation, 'observation-generation')
    require(record['tick'] >= 0 and isinstance(get(record, 'payload'), dict), 'observation-schema')
    return record['payload']


@dataclass(frozen=True)
class Step:
    name: str
    seconds: float
    actions: tuple
    checks: tuple
    evidence: tuple = ()


@dataclass
class Context:
    case_id: str
    run_id: str
    run_dir: Path
    clock: object = time.perf_counter_ns
    origin: int | None = None
    session_id: str | None = None
    generation: int | None = None
    ledger: list = field(default_factory=list)
    assertions: list = field(default_factory=list)
    done: list = field(default_factory=list)
    consumed: set = field(default_factory=set)
    first_failure: dict | None = None
    active_step: str = ''
    cleanup: dict = field(default_factory=lambda: {'resourcesAbsent': False})
    recovery: dict = field(default_factory=lambda: {'verified': False})
    evidence: list = field(default_factory=list)
    execution_mode: str = 'live'
    candidate_sha256: str | None = None
    tool_manifest_sha256: str | None = None
    screen_occupied_seconds: float | None = None
    screen_start_tick: int | None = None
    screen_end_tick: int | None = None
    phase_ticks: dict = field(default_factory=dict)
    lifecycle: list = field(default_factory=list)
    tool_calls: dict = field(default_factory=dict)
    stopping: bool = False
    observation_after: int = 0

    def screen_start(self):
        if self.screen_start_tick is None:self.screen_start_tick=self.clock()

    def tool_call(self,kind):
        phase='recovery' if 'recoveryStart' in self.phase_ticks else 'cleanup' if self.stopping else 'active'
        caps={'dtd':(320,96,64),'ssh':(160,64,48),'gateway':(640,192,128)}
        require(kind in caps,'tool-kind')
        key=phase+':'+kind;row=self.tool_calls.setdefault(key,{'count':0,'observedSeconds':0,'maximumSeconds':0})
        require(row['count']<caps[kind][('active','cleanup','recovery').index(phase)],'tool-call-budget-exceeded',key)
        row['count']+=1;return row

    def resource(self, name, state, owner_file=None):
        require(name in ('dtd:pc-a','dtd:pc-b','runtime:local','runtime:remote','clipboard:pc-b','prepared:remote'), 'lifecycle-resource')
        require(state in ('intent','acquired','released','unknown'), 'lifecycle-state')
        if owner_file is not None:require(Path(owner_file).name==owner_file,'lifecycle-owner-file')
        path=self.run_dir/'lifecycle.ndjson'
        if not self.lifecycle and path.exists():
            from e2e_case_records import decode,sequence
            with path.open('rb') as stream:self.lifecycle=sequence(decode(stream.read(LIMIT+1),True),self.run_id)
        row=dict(runId=self.run_id,seq=len(self.lifecycle)+1,resource=name,state=state,
                 ownerFile=owner_file,tick=self.clock(),frequency=1000000000)
        with path.open('ab') as f:
            f.write(json.dumps(row).encode()+b'\n');f.flush();os.fsync(f.fileno())
        self.lifecycle.append(row)

    def begin(self):
        require(self.origin is None, 'origin-already-set', exit_code=40)
        origin = self.clock()
        require(type(origin) is int and origin>=0,'origin-clock')
        new_json(self.run_dir/'launch-once.json', {'runId': self.run_id, 'caseId': self.case_id,
                    'origin': origin, 'frequency': 1000000000,'controllerPid':os.getpid(),
                    'controllerImage':str(Path(sys.executable).resolve())})
        self.origin = origin

    def end(self, seconds):
        require(self.origin is not None, 'origin-missing')
        return self.origin + int(seconds*1000000000)

    def remaining(self, deadline, cap=5):
        now=self.clock()
        require(type(deadline) is int and type(now) is int and (self.origin is None or now>=self.origin),'deadline-clock')
        require(type(cap) in (int,float) and math.isfinite(cap) and cap>0,'deadline-cap')
        remaining = min(cap, (deadline-now)/1000000000)
        require(remaining > 0, 'deadline-expired')
        return remaining

    def recovery_deadline(self, phase_seconds, allowance):
        """Keep the original SLA; overdue safety work gets at most 10 seconds.

        This is a call budget, never a new run origin or feature-action grant.
        """
        require(self.stopping and 0 < allowance <= 180, 'recovery-budget-only')
        now=self.clock()
        deadline=min(self.end(900),self.end(phase_seconds),now+int(allowance*1e9))
        return deadline if deadline>now else now+int(min(allowance,10)*1e9)

    def log(self, operation, status, request_id=None):
        require(len(self.ledger) < 4096, 'ledger-bound')
        row = dict(caseId=self.case_id, runId=self.run_id, stepId=self.active_step,
                   requestId=request_id, operation=operation, tick=self.clock(), status=status)
        self.ledger.append(row)
        with (self.run_dir/'action-ledger.ndjson').open('ab') as f:
            f.write(json.dumps(row).encode('utf-8')+b'\n')
            f.flush()
            os.fsync(f.fileno())

    def once(self, key, call):
        require(key not in self.consumed, 'duplicate-action', key)
        self.consumed.add(key)  # Consume BEFORE dispatch, including lost responses.
        request_id = uuid.uuid4().hex
        self.log(key, 'intent', request_id)
        try:
            result = call(request_id)
            require(result.get('status') == 'completed', 'completion-unknown-or-failed', key)
            self.log(key, 'completed', request_id)
            return result
        except Exception:
            self.log(key, 'failed-or-unknown', request_id)
            raise


def stop_and_recover(ctx, backend):
    """Every recovery branch is attempted; first functional failure is immutable."""
    ctx.stopping=True
    ctx.phase_ticks['cleanupStart']=ctx.clock()
    finish = ctx.run_dir/'finish.json'
    errors = []
    try:
        if not finish.exists():
            new_json(finish, {'id': ctx.run_id, 'reason': 'case-stop', 'tick': ctx.clock()})
        if ctx.case_id in ('C04','C05','C06') and not (ctx.run_dir/'external-recovery-request.json').exists():
            new_json(ctx.run_dir/'external-recovery-request.json',{'runId':ctx.run_id,'caseId':ctx.case_id,
                'reason':'verify-default-and-same-user','deadlineTick':ctx.end(840),'frequency':1000000000})
    except Exception as e:
        errors.append('local-stop:'+type(e).__name__)
    # A stop marker precedes any SSH call. Recovery never replays a feature action.
    for operation in ('finish-remote', 'release-owned-input', 'stop-owned-runtime',
                      'restore-case-state', 'inspect-independent', 'retire-and-close'):
        try:
            if operation=='inspect-independent':
                ctx.phase_ticks['cleanupEnd']=ctx.clock();ctx.phase_ticks['recoveryStart']=ctx.clock()
                if ctx.clock()>ctx.end(540):errors.append('cleanup-phase-deadline')
            if operation == 'retire-and-close' and not ctx.recovery.get('verified'):
                errors.append('guards-retained-unverified-recovery')
                continue
            # Expired runs still attempt finite safety recovery; do not reset origin.
            allowance={'stop-owned-runtime':180,'inspect-independent':60,'retire-and-close':45,
                       'restore-case-state':30,'release-owned-input':20}.get(operation,10)
            end = ctx.recovery_deadline(840 if operation in ('inspect-independent','retire-and-close') else 540,allowance)
            data = backend.recover(operation, ctx, end)
            require(data.get('status') == 'completed', 'recovery-action-failed', operation)
            if operation == 'inspect-independent':
                ctx.cleanup = {'resourcesAbsent': data.get('resourcesAbsent') is True}
                ctx.recovery = {'verified': data.get('verified') is True}
        except Exception as e:
            errors.append(operation + ':' + (e.code if isinstance(e, CaseError) else type(e).__name__))
    if ctx.clock()>ctx.end(840):errors.append('recovery-phase-deadline')
    if errors:
        ctx.recovery['verified'] = False
        try:backend.recover('close-owned-sessions',ctx,ctx.recovery_deadline(840,10))
        except Exception as e:errors.append('close-owned-sessions:'+type(e).__name__)
    ctx.recovery['errors'] = errors
    ctx.phase_ticks['recoveryEnd']=ctx.clock()


def execute_steps(ctx, backend, steps):
    """Used by offline fault injection and the live dispatcher; no retry loop."""
    try:
        ctx.begin()
        backend.begin(ctx, ctx.end(300))
        for index, step in enumerate(steps, 1):
            ctx.active_step = f'{index:02d}'
            deadline = min(ctx.end(300), ctx.clock()+int(step.seconds*1000000000))
            ctx.remaining(deadline)
            backend.check_guards(ctx, deadline)
            for action in step.actions:
                ctx.remaining(deadline)
                require(not (ctx.run_dir/'finish.json').exists(),'external-stop-requested')
                backend.action(action, ctx, deadline)
                ctx.observation_after=time.time_ns()
            for source, predicate in step.checks:
                record = backend.observe(source, ctx, deadline)
                payload = bind_observation(record, ctx, ctx.active_step, source)
                predicate(payload)
                ctx.assertions.append({'id': source, 'stepId': ctx.active_step, 'status': 'PASS',
                    'expected': {'predicate': source}, 'actual': {'verified': True},
                    'evidenceRefs': list(step.evidence)})
                ctx.remaining(deadline)
            backend.collect(step.evidence, ctx, deadline)
            ctx.done.append(step.name)
    except Exception as e:
        ctx.first_failure = {'stepId': ctx.active_step or 'begin',
                             'code': e.code if isinstance(e, CaseError) else type(e).__name__}
    finally:
        if ctx.origin is not None:
            ctx.phase_ticks['productEnd']=ctx.clock()
            stop_and_recover(ctx, backend)
    return write_result(ctx, steps)


def write_result(ctx, steps):
    now = ctx.clock()
    recovery_ok = ctx.recovery.get('verified') is True and ctx.cleanup.get('resourcesAbsent') is True
    evidence_ok = True
    if ctx.execution_mode == 'live':
        required = {name for step in steps for name in step.evidence}
        actual = {e['relativePath'] for e in ctx.evidence}
        evidence_ok = bool(required) and required <= actual
        for entry in ctx.evidence:
            try:
                p = safe_path(str(ctx.run_dir/entry['relativePath']),ctx.run_dir,True)
                evidence_ok &= digest(p)==entry['sha256'] and p.stat().st_size==entry['bytes']
            except Exception:
                evidence_ok = False
        evidence_ok &= all(isinstance(h,str) and re.fullmatch('[a-f0-9]{64}',h)
                           for h in (ctx.candidate_sha256,ctx.tool_manifest_sha256))
        evidence_ok &= (ctx.screen_start_tick is not None and ctx.screen_end_tick is not None
                        and ctx.origin is not None and ctx.origin<=ctx.screen_start_tick<=ctx.screen_end_tick<=now)
    functional = not ctx.first_failure and len(ctx.done) == len(steps) and bool(ctx.assertions) and evidence_ok
    if not evidence_ok and not ctx.first_failure:
        ctx.first_failure={'stepId':'report','code':'evidence-integrity-or-runtime-metadata-missing'}
    within = ctx.origin is not None and now <= ctx.end(900)
    status = ('PASS' if functional and recovery_ok and within else
              'FAIL' if recovery_ok and within else 'FAIL_RECOVERY_REQUIRED')
    resources={r['resource']:r for r in ctx.lifecycle}
    result = dict(caseId=ctx.case_id, runId=ctx.run_id, result=status,
                  executionMode=ctx.execution_mode,
                  firstFailureStep=ctx.first_failure, startTick=ctx.origin, endTick=now,
                  frequency=1000000000, actualSeconds=(now-ctx.origin)/1e9 if ctx.origin is not None else None,
                  screenOccupiedSeconds=ctx.screen_occupied_seconds,
                  screenMeasurement='bounded' if ctx.screen_start_tick is not None and ctx.screen_end_tick is not None else 'unknown',
                  screenBoundStartTick=ctx.screen_start_tick,screenBoundEndTick=ctx.screen_end_tick,
                  screenOccupiedUpperBoundSeconds=(ctx.screen_end_tick-ctx.screen_start_tick)/1e9
                    if ctx.screen_start_tick is not None and ctx.screen_end_tick is not None else None,
                  phaseTicks=ctx.phase_ticks,
                  toolCalls=ctx.tool_calls,
                  productActionSeconds=(ctx.phase_ticks['productEnd']-ctx.phase_ticks['productStart'])/1e9
                    if 'productStart' in ctx.phase_ticks and 'productEnd' in ctx.phase_ticks else None,
                  cleanupRecoverySeconds=(ctx.phase_ticks['recoveryEnd']-ctx.phase_ticks['cleanupStart'])/1e9
                    if 'recoveryEnd' in ctx.phase_ticks else None,
                  candidateSha256=ctx.candidate_sha256,toolManifestSha256=ctx.tool_manifest_sha256,
                  previousRun=None,assertions=ctx.assertions,evidence=ctx.evidence,
                  cleanup=ctx.cleanup, recovery=ctx.recovery,
                  unresolvedOwnedResources=[{'resource':key,'state':r['state'],'ownerFile':r.get('ownerFile')}
                    for key,r in resources.items() if r['state']!='released'],
                  unexecutedSteps=[s.name for s in steps if s.name not in ctx.done])
    new_json(ctx.run_dir/'result.json', result)
    return result


def read_config(path, case_id, run_dir, require_evidence=True):
    c = read_json(path)
    require(set(c)<={'caseId','runId','runDir','expectedMask','a','b','signalingOrigin','viewerUrl','staging','dtdExe',
                    'candidateDirectory','candidateSha256','sourceEvidence','dtdConfig','tls','uiRules','uiEvidence','observations',
                    'recovery','desktopToolDirectory','inputTargetDirectory','local','caseInputs'},'config-unknown-field',exit_code=40)
    require(c.get('observations')=={},'config-observation-override',exit_code=40)
    require(re.fullmatch(r'C(0[1-9]|1[0-6])', case_id) is not None, 'case-id', exit_code=40)
    i = int(case_id[1:])-1
    require(c.get('caseId') == case_id and equal(c.get('expectedMask'), MASKS[i]), 'case-mask', exit_code=40)
    rid = c.get('runId', '')
    require(re.fullmatch('[a-f0-9]{32}', rid) is not None and rid != OLD_RUN, 'run-id', exit_code=40)
    actual = safe_path(run_dir, ROOT/'.artifacts')
    require(actual == (ROOT/'.artifacts'/f'e2e-{case_id}-{rid}').resolve()
            and actual == safe_path(c.get('runDir', '')), 'run-directory', exit_code=40)
    assert_fields(c, {'a.machine': 'pc-a', 'a.address': '192.168.0.61',
                      'b.machine': 'pc-b', 'b.address': '192.168.0.74', 'b.sshUser': 'jacking75NoteBook',
                      'signalingOrigin': 'https://192.168.0.61:25001'})
    expected_url = c['signalingOrigin'] + ('/?relay=1' if case_id == 'C15' else '/')
    require(c.get('viewerUrl') == expected_url, 'viewer-url', exit_code=40)
    require(re.fullmatch(r'C:/Users/jacking75NoteBook/RemoteAssist-E2E-[0-9]+', c.get('staging', '')),
            'staging-path', exit_code=40)
    require(c.get('dtdExe') == 'F:/github/DesktopTestDriver/bin/live_Release/Controller/Controller.exe',
            'dtd-path', exit_code=40)
    for key in ('candidateDirectory', 'candidateSha256', 'sourceEvidence', 'dtdConfig',
                'tls', 'uiRules', 'observations', 'recovery'):
        require(c.get(key) is not None, 'config-field-required', key, 40)
    require(re.fullmatch('[a-fA-F0-9]{64}', c['candidateSha256']), 'candidate-hash', exit_code=40)
    candidate = safe_path(str(Path(c['candidateDirectory'])/'candidate.json'), exists=True)
    require(digest(candidate) == c['candidateSha256'].lower(), 'candidate-changed', exit_code=10)
    for key in ('dtdExe', 'dtdConfig') + (('sourceEvidence',) if require_evidence else ()):
        safe_path(c[key], exists=True)
    from e2e_case_ui import rules
    c['uiRules']=rules(c['uiRules'])
    for side in ('a','b'):
        require(re.fullmatch(r'S-1-5-21-\d+-\d+-\d+-\d+',str(c[side].get('sid',''))) and
                type(c[side].get('session')) is int and c[side]['session']>0,'interactive-identity-required',exit_code=10)
    if case_id=='C04':require(re.fullmatch('[a-fA-F0-9]{64}',str(c.get('caseInputs',{}).get('uacTriggerSha256',''))),'uac-trigger-required',exit_code=10)
    if case_id in ('C07','C08','C09'):
        filename='ra-'+case_id.lower()+'.bin'
        source=(c['staging']+'/tools/inputs/'+filename if case_id=='C08' else str(actual/'inputs'/filename))
        receive=(str(actual/'receive'/filename) if case_id=='C08' else c['staging']+'/observer/receive')
        normalize=lambda value:value.replace('\\','/').casefold()
        require(normalize(get(c,'caseInputs.sourcePath'))==normalize(source) and
                normalize(get(c,'caseInputs.receivePath'))==normalize(receive),'fixed-file-path-required',exit_code=40)
    return c


def live_blockers(case_id):
    # External readiness is distinct from implemented execution branches.
    common = ['candidate/tool hashes and measured phase budgets require prior evidence',
              'independent watchdog, cleanup and external return require witnessed validation',
              'A/B existing-chain TLS identity and desktop identity require preparation']
    extra = {
        'C04': ['designated on-site/VM recovery operator and signed Windows UAC trigger'],
        'C05': ['designated on-site/VM recovery operator'],
        'C06': ['designated same-user login operator'],
        'C15': ['EXCLUDED: TURN excluded by user on 2026-10-22'],
    }
    return common + extra.get(case_id,[])


def tool_inventory(config):
    """Hash actual existing inputs only, never fabricate future runtime identity."""
    paths = [Path(config['candidateDirectory'])/'candidate.json',Path(config['dtdExe']),Path(config['dtdConfig'])]
    paths += [p for p in (ROOT/'scripts').iterdir() if p.is_file() and p.name.startswith(('e2e-','e2e_case_'))]
    paths += list((ROOT/'scripts/e2e-desktop').glob('*.cs'))
    paths += [ROOT/'tests/e2e'/name for name in ('campaign-fixture.ps1','scm-fixture.ps1','ScmFixture.cs')]
    paths.append(ROOT/'tests/e2e/scm-fixture/ScmFixture.csproj')
    for directory in (ROOT/'.artifacts/browser/node_modules/playwright-core',ROOT/'.artifacts/python-ssh'):
        if directory.is_dir():paths += [p for p in directory.rglob('*') if p.is_file() and '__pycache__' not in p.parts]
    for key in ('desktopToolDirectory','inputTargetDirectory'):
        if key in config:paths += [p for p in Path(config[key]).rglob('*') if p.is_file()]
    if 'desktopToolDirectory' in config:
        build=Path(config['desktopToolDirectory']).parent/'build-result.json'
        paths.append(build)
        paths += [p for p in (build.parent/'scm-fixture').rglob('*') if p.is_file()]
    if 'local' in config:
        paths += [Path(get(config,'local.'+name)) for name in ('signaling','tlsCert','tlsKey','node','chrome','python')]
    if 'tls' in config:paths.append(Path(get(config,'tls.certificate')))
    paths += [ROOT/'scripts'/('e2e-'+config['caseId']+'.ps1'),ROOT/'scripts/e2e-case-command.ps1']
    for name in ('e2e-run-contract.ps1','e2e-native.cs','e2e-product-client.py',
                 'e2e-ssh-command.py','e2e-local-session.ps1','e2e-product-guard.ps1'):
        paths.append(ROOT/'scripts'/name)
    entries=[]
    for path in sorted(set(paths)):
        p=safe_path(str(path),exists=True)
        entries.append({'path':str(p),'sha256':digest(p),'bytes':p.stat().st_size})
    return entries


def freeze_inputs(config,config_path):
    entries=tool_inventory(config)
    for path in (config_path,config['sourceEvidence'],config.get('uiEvidence'),config.get('caseInputs',{}).get('clipboardEvidence')):
        if path is None:continue
        p=safe_path(str(path),exists=True)
        entries.append({'path':str(p),'sha256':digest(p),'bytes':p.stat().st_size})
    entries=sorted({r['path']:r for r in entries}.values(),key=lambda r:r['path'])
    encoded=json.dumps(entries,sort_keys=True,separators=(',',':')).encode('utf-8')
    recovery_bytes=json.dumps(config,ensure_ascii=False,allow_nan=False,indent=2).encode('utf-8')
    return {'caseId':config['caseId'],'runId':config['runId'],'configPath':str(Path(config_path).resolve()),
            'configSha256':digest(config_path),'toolManifestSha256':hashlib.sha256(encoded).hexdigest(),
            'recoveryConfigSha256':hashlib.sha256(recovery_bytes).hexdigest(),
            'files':entries}


def prepare_case(config, config_path, ctx, dtd):
    require(ctx.case_id!='C15','turn-excluded-20261022',exit_code=10)
    from e2e_case_campaign import package_campaign,Campaign
    from e2e_case_readiness import readiness
    require(not ctx.run_dir.exists(),'prepare-replay',exit_code=40)
    ctx.run_dir.mkdir(parents=True)
    frozen=freeze_inputs(config,config_path)
    new_json(ctx.run_dir/'recovery-config.json',config)
    require(digest(ctx.run_dir/'recovery-config.json')==frozen['recoveryConfigSha256'],'recovery-config-write')
    new_json(ctx.run_dir/'frozen-inputs.json',frozen)
    blockers=[]
    try:readiness(config,frozen)
    except CaseError as e:blockers.append(e.code)
    # No session-open, launch, certificate change or product bootstrap here.
    statuses={}
    end=ctx.clock()+15000000000
    for machine in ('pc-a','pc-b'):
        try:
            reply=dtd.status(machine,ctx,end)
            data=get(reply,'data')
            assert_fields(data,{'machineId':machine,'apiVersion':1,'desktop':'Default','inputAvailable':True})
            expected_session=get(config,'a.session' if machine=='pc-a' else 'b.session')
            require(get(data,'interactiveSessionId')==expected_session,'interactive-session-changed')
            require({'windows','controls','focus','click','type-text','release-input'} <= set(get(data,'supportedOperations')),
                    'dtd-operations-missing')
            # DTD currently supplies no SID; do not infer it from session number.
            statuses[machine]={'status':'read-only-status-verified','session':data['interactiveSessionId'],
                               'desktop':data['desktop'],'sidVerifiedAtRun':True}
        except Exception as e:
            statuses[machine]={'status':'unverified','error':e.code if isinstance(e,CaseError) else type(e).__name__}
            blockers.append(machine+': read-only identity probe failed')
    campaign_hash=None
    if not blockers:
        try:
            package_campaign(config,ctx)
            campaign=Campaign(config,ctx,dtd.transport)
            campaign.deploy(ctx.clock()+180000000000)
            campaign_hash=campaign.sha
        except Exception as e:blockers.append(e.code if isinstance(e,CaseError) else type(e).__name__)
    result={'caseId':ctx.case_id,'runId':ctx.run_id,'state':'BLOCKED' if blockers else 'READY_NOT_EXECUTED',
            'configSha256':frozen['configSha256'],'toolManifestSha256':frozen['toolManifestSha256'],
            'campaignSha256':campaign_hash,'identity':statuses,'blockers':blockers,'functionalActions':0}
    if (ctx.run_dir/'prepare-failure.json').exists():
        result['failure']=read_json(ctx.run_dir/'prepare-failure.json')
        if result['failure']['cleanup'].get('status')!='completed':result['state']='FAIL_RECOVERY_REQUIRED'
    new_json(ctx.run_dir/'prepared.json',result)
    return result


def collect_saved(ctx):
    """Rehash saved evidence; never promote a past cleanup record to current recovery."""
    result_path=ctx.run_dir/'result.json'
    if not result_path.exists():
        return {'caseId':ctx.case_id,'runId':ctx.run_id,'state':'BLOCKED',
                'reason':'No completed execution record','functionalActions':0}
    saved=read_json(result_path)
    assert_fields(saved,{'caseId':ctx.case_id,'runId':ctx.run_id})
    checked=[]
    for entry in saved.get('evidence',[]):
        p=safe_path(str(ctx.run_dir/entry['relativePath']),ctx.run_dir,True)
        checked.append({'relativePath':entry['relativePath'],
                        'hashMatches':digest(p)==entry['sha256'] and p.stat().st_size==entry['bytes']})
    return {'caseId':ctx.case_id,'runId':ctx.run_id,'state':'COLLECTED_SAVED_EVIDENCE_ONLY',
            'previousResult':saved['result'],'evidence':checked,'recoveryVerified':False,
            'reason':'Saved evidence is not a fresh desktop recovery observation','functionalActions':0}


def abort_case(ctx, transport):
    launch=read_json(ctx.run_dir/'launch-once.json')
    assert_fields(launch,{'caseId':ctx.case_id,'runId':ctx.run_id})
    ctx.origin=get(launch,'origin')
    # Never create a fresh origin or use a legacy launch/manifest.
    if not (ctx.run_dir/'finish.json').exists():
        new_json(ctx.run_dir/'finish.json',{'id':ctx.run_id,'reason':'explicit-abort','tick':ctx.clock()})
    error=None
    try:
        from e2e_case_adapters import Harness
        h=Harness(str(ctx.run_dir/'campaign.json'),transport)
        h.call('finish',ctx,ctx.clock()+10000000000)
    except Exception as e:
        error=e.code if isinstance(e,CaseError) else type(e).__name__
    return {'caseId':ctx.case_id,'runId':ctx.run_id,'state':'FAIL_RECOVERY_REQUIRED',
            'reason':'Independent guards perform recovery; use the original runner result for recovery proof',
            'remoteStopError':error,'functionalActions':0}


def main(argv=None):
    p = argparse.ArgumentParser()
    p.add_argument('--case', required=True, choices=[f'C{i:02d}' for i in range(1,17)])
    p.add_argument('--action', required=True, choices=['Validate','Prepare','Run','Abort','Collect'])
    p.add_argument('--run-dir', required=True)
    p.add_argument('--config', required=True)
    p.add_argument('--execute', action='store_true')
    a = p.parse_args(argv)
    try:
        require(a.execute == (a.action == 'Run'), 'execute-only-for-run', exit_code=40)
        # Scope decision 2026-10-22: retain recovery for historical runs, but
        # never prepare or launch the excluded TURN case, even with old config.
        if a.case == 'C15' and a.action in ('Validate', 'Prepare', 'Run'):
            print(json.dumps({'caseId': a.case, 'action': a.action,
                              'state': 'EXCLUDED', 'reason': 'TURN excluded by user on 2026-10-22; direct NAT connectivity assumed',
                              'functionalActions': 0}))
            return 10
        if a.action=='Abort':
            from e2e_case_adapters import OwnedGateway
            from e2e_case_recovery import abort_existing
            result=abort_existing(a.case,a.run_dir,a.config,OwnedGateway());print(json.dumps(result))
            if result.get('state')=='PREPARATION_RETIRED_NOT_EXECUTED':return 10
            return 20 if result.get('state')=='ABORTED_RECOVERED' else 30
        c = read_config(a.config, a.case, a.run_dir)
        from e2e_case_steps import CASES
        require(a.case in CASES, 'case-not-implemented', exit_code=10)
        blockers = live_blockers(a.case)
        ctx=Context(a.case,c['runId'],Path(a.run_dir))
        if a.action=='Prepare':
            from e2e_case_adapters import Dtd, OwnedGateway
            result=prepare_case(c,a.config,ctx,Dtd(c['dtdExe'],c['dtdConfig'],OwnedGateway()))
            print(json.dumps(result));return 0 if result['state']=='READY_NOT_EXECUTED' else 30 if result['state']=='FAIL_RECOVERY_REQUIRED' else 10
        if a.action=='Collect':
            print(json.dumps(collect_saved(ctx)));return 10
        if a.action=='Run':
            from e2e_case_readiness import verify_frozen
            from e2e_case_adapters import OwnedGateway,Dtd,Harness,CaseBackend
            from e2e_case_campaign import Campaign
            from e2e_case_live import LiveObserver
            verify_frozen(c,a.config,ctx)
            gateway=OwnedGateway();dtd=Dtd(c['dtdExe'],c['dtdConfig'],gateway)
            harness=Harness(str(ctx.run_dir/'campaign.json'),gateway)
            observer=LiveObserver(c,Campaign(c,ctx,gateway),dtd,harness)
            result=execute_steps(ctx,CaseBackend(c,dtd,harness,observer),CASES[a.case])
            print(json.dumps(result));return {'PASS':0,'FAIL':20,'FAIL_RECOVERY_REQUIRED':30}[result['result']]
        print(json.dumps({'action': a.action, 'caseId': a.case, 'runId': c['runId'],
                          'state': 'LOCAL_CONFIG_VALID_NOT_READY', 'blockers': blockers,
                          'functionalActions': 0}))
        return 0
    except CaseError as e:
        if e.exit_code==20:
            e.exit_code=30 if 'ctx' in locals() and ctx.origin is not None else 40
        print(json.dumps({'state': 'BLOCKED' if e.exit_code == 10 else 'ERROR',
                          'code': e.code, 'detail': e.detail, 'functionalActions': 0}))
        return e.exit_code
    except (KeyError,TypeError,AttributeError,ValueError,OSError) as e:
        print(json.dumps({'state':'ERROR','code':'invalid-config-or-file','detail':type(e).__name__,
                          'functionalActions':0}))
        return 40


if __name__ == '__main__':
    raise SystemExit(main())
