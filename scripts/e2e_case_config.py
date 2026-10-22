"""Generate new local drafts for the active 15 cases. Never prepares/runs them."""
import argparse
from copy import deepcopy
import json
from pathlib import Path
import uuid
from e2e_case_runtime import ROOT,MASKS,require,read_json,new_json,digest,safe_path,CaseError,read_config

ACTIVE=tuple('C%02d'%i for i in (*range(1,15),16))
FIELDS={'candidateDirectory','desktopToolDirectory','inputTargetDirectory','dtdExe','dtdConfig','tls','local','a','b','uiRules','recovery','uacTriggerSha256'}

def generate(template,output,cases=ACTIVE):
    require(bool(cases) and all(case in ACTIVE for case in cases),'turn-excluded-20261022',exit_code=10)
    require(set(template)<=FIELDS,'unknown-template-field')
    allowed={'a':{'machine','address','sid','session'},'b':{'machine','address','sid','session','sshUser'},
             'tls':{'certificate','thumbprint','mode'},'local':{'signaling','node','python','chrome','tlsCert','tlsKey'},'recovery':{'operator'}}
    for key,fields in allowed.items():require(isinstance(template.get(key,{}),dict) and set(template.get(key,{}))<=fields,'unknown-template-nested-field')
    root=safe_path(output,ROOT/'.artifacts');require(not root.exists(),'config-output-replay');root.mkdir(parents=True)
    candidate=safe_path(str(Path(template['candidateDirectory'])/'candidate.json'),exists=True)
    results=[]
    for case in cases:
        require(case in ACTIVE,'turn-excluded-20261022',exit_code=10)
        rid=uuid.uuid4().hex;run=safe_path(str(ROOT/'.artifacts'/('e2e-'+case+'-'+rid)))
        require(not run.exists(),'run-collision')
        c={k:deepcopy(v) for k,v in template.items() if k not in ('uacTriggerSha256','recovery')}
        c['a']={'machine':'pc-a','address':'192.168.0.61',**c.get('a',{})}
        c['b']={'machine':'pc-b','address':'192.168.0.74','sshUser':'jacking75NoteBook',**c.get('b',{})}
        c.setdefault('dtdExe','F:/github/DesktopTestDriver/bin/live_Release/Controller/Controller.exe')
        c.update(caseId=case,runId=rid,runDir=str(run),expectedMask=MASKS[int(case[1:])-1],
                 staging='C:/Users/jacking75NoteBook/RemoteAssist-E2E-'+str(int(rid,16)),
                 candidateSha256=digest(candidate),sourceEvidence=str(root/(case+'-readiness.json')),
                 uiEvidence=str(root/(case+'-ui.json')),signalingOrigin='https://192.168.0.61:25001',
                 viewerUrl='https://192.168.0.61:25001/',observations={},uiRules=template.get('uiRules',{}),recovery={},caseInputs={})
        if case in ('C04','C05','C06'):
            c['recovery']={**template.get('recovery',{}),'operatorEvidence':str(root/(case+'-operator.json'))}
        if case=='C04' and template.get('uacTriggerSha256'):c['caseInputs']['uacTriggerSha256']=template['uacTriggerSha256']
        if case=='C11':c['caseInputs']['clipboardEvidence']=str(root/(case+'-clipboard-lifetime.json'))
        if case in ('C07','C08','C09'):
            filename='ra-'+case.lower()+'.bin'
            c['caseInputs']={'sourcePath':c['staging']+'/tools/inputs/'+filename if case=='C08' else str(run/'inputs'/filename),
                            'receivePath':str(run/'receive'/filename) if case=='C08' else c['staging']+'/observer/receive'}
        path=root/(case+'-config.json');new_json(path,c)
        missing=[]
        for field in ('dtdConfig','tls','local'):
            if not c.get(field):missing.append(field)
        for key in ('a','b'):
            for field in ('sid','session'):
                if field not in c.get(key,{}):missing.append(key+'.'+field)
        try:read_config(str(path),case,str(run),require_evidence=False)
        except (CaseError,OSError,KeyError,TypeError) as e:missing.append(getattr(e,'code',type(e).__name__))
        missing+=['sourceEvidence','uiEvidence']
        if not Path(c['dtdExe']).is_file():missing.append('dtdExe')
        if case=='C04' and not c['caseInputs'].get('uacTriggerSha256'):missing.append('caseInputs.uacTriggerSha256')
        if case in ('C04','C05','C06') and not c['recovery'].get('operator'):missing.append('recovery.operator')
        if case in ('C04','C05','C06'):missing.append('recovery.operatorEvidence')
        if case=='C11':missing.append('caseInputs.clipboardEvidence')
        entry={'caseId':case,'config':str(path),'configSha256':digest(path),'runId':rid,'state':'DRAFT_NOT_READY','missing':missing}
        commands={action:['powershell.exe','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',str(ROOT/'scripts'/('e2e-'+case+'.ps1')),
                          '-Action',action,'-RunDir',str(run),'-Config',str(path)]+(['-Execute'] if action=='Run' else [])
                  for action in ('Validate','Prepare','Run','Abort','Collect')}
        from e2e_case_steps import CASES
        entry['handoff']={'commands':commands,'steps':[{'name':s.name,'seconds':s.seconds,'sources':[n for n,_ in s.checks],'evidence':list(s.evidence)} for s in CASES[case]],
            'operator':c['recovery'].get('operator'),'firstFailureStops':True,'budgets':{'screenUpperBoundSeconds':300,'targetTotalSeconds':600,'cleanupEndSeconds':540,'recoveryEndSeconds':840,'absoluteEndSeconds':900},
            'plannedResources':{'runDir':str(run),'staging':c['staging'],'task':'RemoteAssist-E2E-Product-'+rid,'watchdog':'RemoteAssist-E2E-Watchdog-'+rid},
            'warning':'NOT_READY: commands are a handoff, not execution authorization; independent return must be witnessed'}
        results.append(entry)
    new_json(root/'draft-report.json',{'state':'DRAFT_NOT_READY','cases':results,'functionalActions':0})
    fixture_id=uuid.uuid4().hex;fixture=root/('readiness-fixture-'+fixture_id)
    context={'schema':'remoteassist-e2e-fixture-context-v1','runId':fixture_id,'directory':str(fixture),'caseIds':list(cases),
        'identity':{side:{k:v for k,v in template.get(side,{}).items() if k in ('sid','session')} for side in ('a','b')},
        'authorization':{'scope':'product-independent-readiness-fixture','runId':fixture_id,'operator':None,'consoleAvailable':False},
        'operatorEvidence':str(fixture/'operator.json'),
        'sources':{**{k:str(fixture/'a/controller/cleanup.json') for k in ('monotonicDeadline','independentWatchdog','cleanup')},
                   'boundedScm':str(fixture/'raw-b/service-cleanup.json'),'boundedUac':str(fixture/'raw-b/uac.json'),
                   'tlsIdentity':str(fixture/'tls-bundle.json')},
        'snapshots':{side:str(fixture/('snapshot-'+side+'.json')) for side in ('a','b')},
        'absenceEvidence':{side:str(fixture/('absence-'+side+'.json')) for side in ('a','b')},
        'ownedProcessFiles':{'a':[str(fixture/p) for p in ('a/controller/start.json','a/controller/watchdog.json','a/controller/child.json','tls/owner.json')],
                             'b':[str(fixture/p) for p in ('raw-b/service-start.json','raw-b/service-watchdog.json')]}}
    new_json(root/'fixture-context-draft.json',context)
    return results

def main(argv=None):
    p=argparse.ArgumentParser();p.add_argument('--template',required=True);p.add_argument('--output',required=True);p.add_argument('--case',choices=ACTIVE);a=p.parse_args(argv)
    try:
        rows=generate(read_json(a.template),a.output,(a.case,) if a.case else ACTIVE)
        print(json.dumps({'state':'DRAFT_NOT_READY','cases':rows,'functionalActions':0}));return 10
    except (CaseError,OSError,KeyError,TypeError) as e:
        print(json.dumps({'state':'ERROR','code':getattr(e,'code',type(e).__name__)}));return 40

if __name__=='__main__':raise SystemExit(main())
