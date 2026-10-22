"""Freeze observed selectors before Prepare; no live UI access here."""
import argparse
import json
from pathlib import Path
from e2e_case_runtime import require,read_json,new_json,digest,safe_path,CaseError,equal
from e2e_case_evidence import identity,tool_hashes
from e2e_case_ui import rules

def targets(case):
    a={'name','code','pin','join-submit'}
    b={'HostStart','HostStop','PairingCode','PairingPin','ConsentApprove','ConsentScreen','ConsentControl','ConsentAdmin',
       'consent-file','consent-clipboard','consent-diagnostics','consent-runbook','consent-chat'}
    extra={
        'C01':({'screen'},set()),'C02':(set(),{'ConsentDeny'}),
        'C03':({'screen','remote-text','send-text','blur-window'},{'fixture'}),
        'C04':({'screen','admin-request','admin-revoke'},{'AdminConsentScope','AdminConsentApprove'}),
        'C05':({'screen','admin-request','admin-sas'},{'AdminConsentScope','AdminConsentApprove'}),
        'C06':({'admin-request'},{'AdminConsentScope','AdminConsentApprove'}),
        'C07':({'file-source','file-dialog-path','file-dialog-approve','file-send'},{'receive-dialog-path','receive-dialog-approve'}),
        'C08':({'file-accept','receive-dialog-path','receive-dialog-approve'},{'file-send-local','file-dialog-path','file-dialog-approve'}),
        'C09':({'file-source','file-dialog-path','file-dialog-approve','file-send'},{'file-deny'}),
        'C10':({'chat-text','chat-send'},{'chat-text','chat-send','chat-history'}),
        'C11':({'clipboard-text','clipboard-send','clipboard-get'},set()),'C12':(set(),set()),
        'C13':({'runbook-choice','runbook-detail','runbook-network-summary','runbook-summary','runbook-send'},{'runbook-summary-text','runbook-approve'}),
        'C14':({'screen','runbook-choice','runbook-detail','runbook-network-summary','runbook-summary','runbook-send','runbook-cancel'},{'fixture'}),
        'C16':({'screen'},{'fixture'})}
    require(case in extra,'ui-case-scope');x,y=extra[case]
    return {'pc-a':a|x,'pc-b':b|y}

def validate(config,proof,hashes=None):
    if proof.get('schema')=='remoteassist-c01-runtime-ui-v1':
        require(config['caseId']=='C01' and config['expectedMask']==1 and proof.get('caseId')=='C01','runtime-ui-scope')
        require(proof.get('candidateSha256')==config['candidateSha256'] and equal(proof.get('identity'),identity(config)) and
                proof.get('toolHashes')==(tool_hashes(config) if hashes is None else hashes) and proof.get('rules')==rules(config.get('uiRules',{})),
                'runtime-ui-binding')
        require(proof.get('mode')=='bind-actual-owner-and-unique-element-before-each-action','runtime-ui-mode')
        return proof
    require(proof.get('schema')=='remoteassist-e2e-ui-v1' and proof.get('mode')=='witnessed','ui-evidence-schema')
    require(proof.get('caseId')==config['caseId'] and proof.get('candidateSha256')==config['candidateSha256'] and
            equal(proof.get('identity'),identity(config)) and proof.get('toolHashes')==(tool_hashes(config) if hashes is None else hashes),'ui-evidence-binding')
    selectors=rules(config.get('uiRules',{}));require(proof.get('rules')==selectors,'ui-selectors-changed')
    require(isinstance(proof.get('locale'),str) and proof['locale'] and isinstance(proof.get('browserVersion'),str) and proof['browserVersion'],'ui-locale-version')
    rows=proof.get('targets',[]);expected={(m,t) for m,ts in targets(config['caseId']).items() for t in ts}
    require(len(rows)==len(expected) and {(r['machine'],r['target']) for r in rows}==expected,'ui-target-coverage')
    for row in rows:
        require(row.get('matchCount')==1 and type(row['matchCount']) is int and row.get('enabled') is True and row.get('foregroundVerified') is True,'ui-not-unique-enabled-foreground')
        owner=row.get('owner',{});require(type(owner.get('pid')) is int and owner['pid']>0 and owner.get('createdAt') and owner.get('imagePath'),'ui-owner-incomplete')
        capture=safe_path(row['capture'],exists=True)
        require(capture.stat().st_size<=16*1048576 and digest(capture)==row['captureSha256'],'ui-capture-changed')
        require(row.get('ownerRole')==selectors[row['machine']][row['target']]['ownerRole'],'ui-owner-role')
    return proof

def main(argv=None):
    p=argparse.ArgumentParser();p.add_argument('--config',required=True);p.add_argument('--observations',required=True);p.add_argument('--output',required=True);a=p.parse_args(argv)
    try:
        c=read_json(a.config);proof=validate(c,read_json(a.observations));new_json(safe_path(a.output),proof)
        print(json.dumps({'state':'SELECTORS_FROZEN_NOT_ARMED','functionalActions':0}));return 0
    except (CaseError,OSError,KeyError,TypeError) as e:
        print(json.dumps({'state':'NOT_READY','code':getattr(e,'code',type(e).__name__)}));return 10

if __name__=='__main__':raise SystemExit(main())
