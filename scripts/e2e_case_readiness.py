"""Preparation gates; claims without hash-bound evidence never arm a run."""
import re
from pathlib import Path
from e2e_case_runtime import get,require,read_json,digest,safe_path,CaseError


def prerequisites(config):
    require(config.get('caseId') != 'C15', 'turn-excluded-20261022', exit_code=10)
    for key in ('desktopToolDirectory','inputTargetDirectory','local','a.sid','a.session','b.sid','b.session'):
        get(config,key)
    require(re.fullmatch(r'S-1-5-21-\d+-\d+-\d+-\d+',get(config,'a.sid')) and
            re.fullmatch(r'S-1-5-21-\d+-\d+-\d+-\d+',get(config,'b.sid')),'interactive-sid-required')
    for root,name in (('desktopToolDirectory','E2eDesktop.exe'),('inputTargetDirectory','RemoteAssist.InputTarget.exe')):
        safe_path(str(Path(config[root])/name),exists=True)
    desktop=Path(config['desktopToolDirectory']);target=Path(config['inputTargetDirectory'])
    require(desktop.name=='desktop-tool' and target.name=='input-target' and desktop.parent==target.parent,'tool-build-layout')
    from e2e_case_artifacts import verify as verify_artifacts
    verify_artifacts(config['candidateDirectory'],str(desktop.parent))
    for name in ('signaling','node','python','chrome','tlsCert','tlsKey'):
        safe_path(get(config,'local.'+name),exists=True)
    from e2e_case_runtime import ROOT
    for directory in (ROOT/'.artifacts/browser/node_modules/playwright-core',ROOT/'.artifacts/python-ssh'):
        require(directory.is_dir(),'prepared-dependency-missing',exit_code=10)
    candidate=read_json(Path(config['candidateDirectory'])/'candidate.json')
    sources=candidate.get('sources',[])
    require(any(row['path']=='src/RemoteAssist.Host.Core/Diagnostics/CaseTrace.cs' for row in sources),
            'candidate-missing-current-observers',exit_code=10)
    for row in sources:
        path=safe_path(str(ROOT/row['path']),ROOT,True)
        require(digest(path)==row['sha256'].lower(),'candidate-source-changed',exit_code=10)
    return True


def readiness(config,frozen):
    prerequisites(config)
    from e2e_case_ui_evidence import validate as validate_ui
    validate_ui(config,read_json(get(config,'uiEvidence')))
    proof=read_json(config['sourceEvidence'])
    from e2e_case_evidence import validate_measurements,identity
    checks_expected,budgets_expected=validate_measurements(config,get(proof,'measurements'))
    require(proof.get('identity')==identity(config) and proof.get('checks')==checks_expected and
            proof.get('measuredBudgets')==budgets_expected,'readiness-provenance',exit_code=10)
    require(proof.get('schema')=='remoteassist-e2e-readiness-v1','readiness-schema',exit_code=10)
    require(proof.get('caseId')==config['caseId'] and proof.get('candidateSha256')==config['candidateSha256'],
            'readiness-candidate',exit_code=10)
    evidence_paths={str(Path(path).resolve()) for path in (config['sourceEvidence'],config['uiEvidence'],
                    config.get('caseInputs',{}).get('clipboardEvidence')) if path is not None}
    evidence_paths.add(frozen['configPath'])
    hashes={row['sha256'] for row in frozen['files'] if row['path'] not in evidence_paths}
    require(set(proof.get('toolHashes',[]))==hashes,'readiness-tools-changed',exit_code=10)
    checks=proof.get('checks',{})
    for name in ('monotonicDeadline','independentWatchdog','boundedScm','boundedUac','cleanup','externalReturn','tlsIdentity'):
        item=get(checks,name)
        require(item.get('verified') is True,'readiness-unverified',name,10)
        artifact=safe_path(get(item,'artifact'),exists=True)
        require(digest(artifact)==get(item,'sha256'),'readiness-artifact-changed',name,10)
    budgets=get(proof,'measuredBudgets')
    require(0<get(budgets,'activeSeconds')<=300 and 0<get(budgets,'cleanupSeconds')<=240 and
            0<get(budgets,'recoverySeconds')<=300 and 0<get(budgets,'totalSeconds')<=900 and
            sum(budgets[k] for k in ('activeSeconds','cleanupSeconds','recoverySeconds'))<=budgets['totalSeconds'],
            'unverified-phase-budget',exit_code=10)
    if config['caseId'] in ('C04','C05','C06'):
        safe_path(get(config,'recovery.operatorEvidence'),exists=True);get(config,'recovery.operator')
    if config['caseId']=='C11':
        lifetime=read_json(get(config,'caseInputs.clipboardEvidence'))
        require(lifetime.get('schema')=='remoteassist-clipboard-lifetime-v1' and lifetime.get('caseId')=='C11' and
                lifetime.get('candidateSha256')==config['candidateSha256'] and
                lifetime.get('executableSha256')==digest(Path(config['desktopToolDirectory'])/'E2eDesktop.exe') and
                lifetime.get('assemblySha256')==digest(Path(config['desktopToolDirectory'])/'E2eDesktop.dll') and
                lifetime.get('sid')==config['b']['sid'] and lifetime.get('session')==config['b']['session'] and
                all(lifetime.get(k) is True for k in ('available','baselineRestored','flushCompleted','helperExited','helperExitSurvived','comparisonVerified')),
                'clipboard-lifetime-not-witnessed',exit_code=10)
    if config['caseId']=='C15':
        safe_path(get(config,'caseInputs.turnEvidence'),exists=True);get(config,'caseInputs.relayInstance')
    return proof


def verify_frozen(config,config_path,ctx):
    from e2e_case_runtime import freeze_inputs
    require(not (ctx.run_dir/'finish.json').exists() and not (ctx.run_dir/'launch-once.json').exists(),
            'prepared-run-stopped-or-consumed',exit_code=10)
    saved=read_json(ctx.run_dir/'frozen-inputs.json')
    current=freeze_inputs(config,config_path)
    require(saved==current,'prepared-inputs-changed',exit_code=10)
    require(digest(ctx.run_dir/'recovery-config.json')==get(saved,'recoveryConfigSha256'),
            'prepared-recovery-config-changed',exit_code=10)
    prepared=read_json(ctx.run_dir/'prepared.json')
    require(prepared.get('state')=='READY_NOT_EXECUTED' and prepared.get('runId')==ctx.run_id,'prepare-not-ready',exit_code=10)
    require(digest(ctx.run_dir/'campaign.json')==prepared['campaignSha256'],'campaign-changed',exit_code=10)
    readiness(config,saved)
    ctx.candidate_sha256=config['candidateSha256'];ctx.tool_manifest_sha256=saved['toolManifestSha256']
    return saved
