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
    proof=read_json(config['sourceEvidence'])
    require(proof.get('schema')=='remoteassist-e2e-readiness-v1','readiness-schema',exit_code=10)
    require(proof.get('caseId')==config['caseId'] and proof.get('candidateSha256')==config['candidateSha256'],
            'readiness-candidate',exit_code=10)
    hashes={row['sha256'] for row in frozen['files'] if row['path'] not in
            (str(Path(config['sourceEvidence']).resolve()),frozen['configPath'])}
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
    if config['caseId']=='C15':
        safe_path(get(config,'caseInputs.turnEvidence'),exists=True);get(config,'caseInputs.relayInstance')
    return proof


def verify_frozen(config,config_path,ctx):
    from e2e_case_runtime import freeze_inputs
    saved=read_json(ctx.run_dir/'frozen-inputs.json')
    current=freeze_inputs(config,config_path)
    require(saved==current,'prepared-inputs-changed',exit_code=10)
    prepared=read_json(ctx.run_dir/'prepared.json')
    require(prepared.get('state')=='READY_NOT_EXECUTED' and prepared.get('runId')==ctx.run_id,'prepare-not-ready',exit_code=10)
    require(digest(ctx.run_dir/'campaign.json')==prepared['campaignSha256'],'campaign-changed',exit_code=10)
    readiness(config,saved)
    ctx.candidate_sha256=config['candidateSha256'];ctx.tool_manifest_sha256=saved['toolManifestSha256']
    return saved
