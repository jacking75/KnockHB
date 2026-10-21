"""Local-only assembly of witnessed, hash-bound preflight measurements.

No shell, DTD, SCM, SSH or clipboard call is made by either CLI operation.
Hashes establish integrity, not authenticity: B's authorized operator retains
the original producer records. Offline/synthetic records cannot arm a case.
"""
import argparse
import json
from pathlib import Path
from e2e_case_runtime import (CaseError,require,get,read_json,new_json,digest,safe_path,numeric,tool_inventory,read_config,equal,OLD_RUN)

CHECKS=('monotonicDeadline','independentWatchdog','boundedScm','boundedUac','cleanup','externalReturn','tlsIdentity')
PRODUCERS={name:'e2e-readiness-fixture-v1:'+name for name in CHECKS}
CAPS={'activeSeconds':300,'cleanupSeconds':240,'recoverySeconds':300,'totalSeconds':900}

def identity(config):return {key:{field:get(config,key+'.'+field) for field in ('sid','session')} for key in ('a','b')}
def tool_hashes(config):return sorted({r['sha256'] for r in tool_inventory(config)})

def validate_measurements(config,index,hashes=None):
    require(config['caseId']!='C15','turn-excluded-20261022',exit_code=10)
    hashes=tool_hashes(config) if hashes is None else sorted(hashes)
    require(index.get('schema')=='remoteassist-e2e-measurements-v1','measurements-schema')
    require(index.get('candidateSha256')==config['candidateSha256'],'measurements-candidate')
    require(equal(index.get('toolHashes'),hashes) and equal(index.get('identity'),identity(config)),'measurements-environment')
    require(config['caseId'] in index.get('caseIds',[]) and set(index['caseIds'])<=set('C%02d'%i for i in (*range(1,15),16)), 'measurements-case-scope')
    require(len(set(index['caseIds']))==len(index['caseIds']),'measurements-duplicate-case')
    rows=get(index,'measurements');require(isinstance(rows,list) and len(rows)<=32,'measurements-bound')
    checks={};records={}
    for row in rows:
        name=get(row,'check');require(name in CHECKS and name not in checks,'measurement-check')
        require(row.get('producer')==PRODUCERS[name] and row.get('mode')=='witnessed' and row.get('outcome')=='PASS','measurement-not-witnessed')
        path=safe_path(get(row,'artifact'),exists=True);require(digest(path)==get(row,'sha256'),'measurement-artifact-changed')
        record=read_json(path)
        require(record.get('schema')=='remoteassist-e2e-measurement-artifact-v1' and record.get('check')==name,'measurement-artifact-schema')
        for key in ('producer','mode','outcome','runId','startTick','endTick','frequency'):
            require(type(record.get(key)) is type(row.get(key)) and record.get(key)==row.get(key),'measurement-provenance',key)
        import re
        require(re.fullmatch('[a-f0-9]{32}',str(row.get('runId',''))) is not None and row['runId']!=OLD_RUN,'measurement-run')
        require(record.get('candidateSha256')==config['candidateSha256'] and record.get('toolHashes')==hashes and
                equal(record.get('identity'),identity(config)) and equal(record.get('caseIds'),index['caseIds']),'measurement-binding')
        a=numeric(get(row,'startTick'));b=numeric(get(row,'endTick'));f=numeric(get(row,'frequency'))
        require(type(a) is int and type(b) is int and type(f) is int and f>0 and 0<=a<b,'measurement-clock')
        require(record.get('resourcesAbsent') is True and record.get('recoveryVerified') is True and
                record.get('operatorWitnessed') is True,'measurement-recovery')
        sources=record.get('sourceEvidence',[])
        require(isinstance(sources,list) and 1<=len(sources)<=64,'measurement-source-provenance')
        for source in sources:
            original=safe_path(get(source,'artifact'),exists=True)
            require(digest(original)==get(source,'sha256'),'measurement-original-changed')
        checks[name]={'verified':True,'artifact':str(path),'sha256':digest(path),'producer':row['producer']}
        records[name]=record
    missing=[name for name in CHECKS if name not in checks]
    require(not missing,'missing-measurements',','.join(missing),10)
    # One actual end-to-end fixture clock supplies all phase boundaries.
    terminal=records['externalReturn'];ticks=get(terminal,'phaseTicks');f=get(terminal,'frequency')
    names=('origin','activeEnd','cleanupEnd','recoveryEnd','reportEnd')
    values=[numeric(get(ticks,n)) for n in names]
    require(all(type(v) is int for v in values) and all(a<b for a,b in zip(values,values[1:])), 'measurement-phase-order')
    require(values[0]==terminal['startTick'] and values[-1]==terminal['endTick'],'measurement-terminal-clock')
    budgets=dict(zip(CAPS,[(values[1]-values[0])/f,(values[2]-values[1])/f,(values[3]-values[2])/f,(values[4]-values[0])/f]))
    require(all(0<v<=CAPS[k] for k,v in budgets.items()) and (values[2]-values[0])/f<=540 and
            (values[3]-values[0])/f<=840,'measurement-phase-budget')
    return checks,budgets

def assemble(config,index,output):
    checks,budgets=validate_measurements(config,index)
    proof={'schema':'remoteassist-e2e-readiness-v1','caseId':config['caseId'],'candidateSha256':config['candidateSha256'],
           'identity':identity(config),'toolHashes':tool_hashes(config),'checks':checks,'measuredBudgets':budgets,
           'measurements':index}
    new_json(safe_path(output),proof);return proof

def inspect(config):
    missing=[]
    path=Path(config['sourceEvidence'])
    if not path.is_file():missing.extend(CHECKS)
    else:
        try:
            proof=read_json(path);validate_measurements(config,get(proof,'measurements'))
        except CaseError as e:missing.append(e.code)
    return {'state':'NOT_READY' if missing else 'EVIDENCE_ASSEMBLED_NOT_ARMED','missing':missing,'functionalActions':0}

def main(argv=None):
    p=argparse.ArgumentParser();p.add_argument('--action',choices=('inspect','assemble'),required=True)
    p.add_argument('--config',required=True);p.add_argument('--measurements');p.add_argument('--output');a=p.parse_args(argv)
    try:
        raw=read_json(a.config);config=read_config(a.config,raw['caseId'],raw['runDir'],require_evidence=False)
        if a.action=='inspect':result=inspect(config)
        else:
            require(a.measurements and a.output,'measurement-paths-required')
            assemble(config,read_json(a.measurements),a.output);result={'state':'EVIDENCE_ASSEMBLED_NOT_ARMED','functionalActions':0}
        print(json.dumps(result));return 10 if result['state']=='NOT_READY' else 0
    except (CaseError,OSError,KeyError,TypeError) as e:
        print(json.dumps({'state':'NOT_READY','code':getattr(e,'code',type(e).__name__),'missing':list(CHECKS),'functionalActions':0}));return 10

if __name__=='__main__':raise SystemExit(main())
