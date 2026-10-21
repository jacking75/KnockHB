"""Convert fixed product-independent fixture outputs into measurement artifacts.

This local reader never executes the fixtures. Their execution is a separate B
authorization; missing native output cannot be replaced with verified=true.
"""
import argparse
import json
from pathlib import Path
from e2e_case_runtime import require,read_json,new_json,digest,safe_path,CaseError,get
from e2e_case_evidence import CHECKS,PRODUCERS,identity,tool_hashes,validate_measurements

def collect(config,context_path,output):
    context=read_json(context_path)
    require(context.get('schema')=='remoteassist-e2e-fixture-context-v1','fixture-context')
    rid=get(context,'runId');require(get(context,'identity')==identity(config),'fixture-identity')
    authorization=get(context,'authorization')
    require(authorization.get('scope')=='product-independent-readiness-fixture' and authorization.get('runId')==rid and
            authorization.get('operator') and authorization.get('consoleAvailable') is True,'fixture-authorization')
    root=safe_path(get(context,'directory'));proof_root=safe_path(output)
    require(not proof_root.exists(),'fixture-evidence-replay');proof_root.mkdir(parents=True)
    phases={name:read_json(root/('phase-'+name+'.json')) for name in ('Start','ActiveEnd','CleanupEnd','Return','Finish')}
    start=phases['Start'];require(start.get('contextSha256','').lower()==digest(context_path),'fixture-context-changed')
    require(all(r.get('runId')==rid and r.get('frequency')==start['frequency'] for r in phases.values()),'fixture-phase-owner')
    ticks=dict(zip(('origin','activeEnd','cleanupEnd','recoveryEnd','reportEnd'),[phases[n]['tick'] for n in phases]))
    sources=get(context,'sources');require(set(sources)==set(CHECKS)-{'externalReturn'},'fixture-source-coverage')
    raw={name:read_json(safe_path(path,exists=True)) for name,path in sources.items()}
    def bounded(row,limit):
        return all(type(row.get(k)) is int for k in ('startedTick','tick','frequency')) and row['frequency']>0 and 0<=row['startedTick']<row['tick'] and row['tick']-row['startedTick']<=limit*row['frequency']
    # Existing campaign fixture: real independent watchdog/controller-loss cleanup.
    for name in ('monotonicDeadline','independentWatchdog','cleanup'):
        row=raw[name]
        require(row.get('campaign')==rid and row.get('state')=='FIXTURE_CLEAN' and row.get('frequency')==start['frequency'] and
                config['a']['address'] in row.get('addresses',[]) and start['tick']<row.get('tick',0)<=phases['ActiveEnd']['tick'],'fixture-campaign-cleanup')
        if name=='independentWatchdog':require(row.get('by')=='Watchdog','fixture-independent-writer')
    scm=raw['boundedScm'];require(scm.get('state')=='PASS' and scm.get('serviceAbsent') is True and scm.get('processAbsent') is True and scm.get('campaign')==rid and
                               bounded(scm,60),'fixture-scm')
    from e2e_case_campaign import inventory
    expected=sorted((r['path'],r['sha256']) for r in inventory(Path(config['desktopToolDirectory']).parent/'scm-fixture'))
    require(sorted((r['name'],r['sha256'].lower()) for r in scm.get('fixtureFiles',[]))==expected,'fixture-scm-build-changed')
    uac=raw['boundedUac'];require(uac.get('runId')==rid and uac.get('cancelled') is True and uac.get('unexpectedApproval') is False and
                                bounded(uac,30),'fixture-uac')
    bundle=raw['tlsIdentity'];require(bundle.get('schema')=='remoteassist-e2e-tls-bundle-v1','fixture-tls-bundle')
    probes={};tls_paths=[]
    for item in get(bundle,'probes'):
        scope=get(item,'scope');require(scope in ('a-user','b-user','b-system') and scope not in probes,'fixture-tls-scope')
        path=safe_path(get(item,'artifact'),exists=True);require(digest(path)==get(item,'sha256'),'fixture-tls-changed')
        row=read_json(path);side='a' if scope=='a-user' else 'b';sid='S-1-5-18' if scope=='b-system' else config[side]['sid']
        require(row.get('status')=='completed' and row.get('chainValid') is True and row.get('storeMutated') is False and
                row.get('endpointProbed') is True and row.get('hostnameValid') is True and row.get('peerCertificateMatched') is True and
                row.get('certificateSha256')==digest(config['tls']['certificate']) and row.get('sid')==sid and
                config[side]['address'] in row.get('addresses',[]),'fixture-tls')
        probes[scope]=row;tls_paths.append(str(path))
    require(set(probes)=={'a-user','b-user','b-system'},'fixture-tls-coverage')
    operator=read_json(context['operatorEvidence'])
    require(operator.get('runId')==rid and operator.get('operator')==authorization['operator'] and operator.get('state')=='RETURNED','fixture-operator')
    for side in ('a','b'):
        snap=read_json(context['snapshots'][side]);expected=config[side]
        require(snap.get('runId')==rid and snap.get('available') is True and snap.get('desktop')=='Default' and
                snap.get('sid')==expected['sid'] and snap.get('session')==expected['session'] and snap.get('activeSessionId')==expected['session'] and
                snap.get('heldVirtualKeys')==[],'fixture-snapshot')
        absence=read_json(context['absenceEvidence'][side])
        require(absence.get('runId')==rid and absence.get('resourcesAbsent') is True and absence.get('remaining')==[] and
                absence.get('changed')==[],'fixture-resource-inspection')
        if side=='a':minimum=phases['CleanupEnd']['tick'];frequency=start['frequency']
        else:
            require(scm['frequency']==uac['frequency']==probes['b-user']['frequency']==probes['b-system']['frequency'],'fixture-b-clock-domain')
            minimum=max(scm['tick'],uac['tick'],probes['b-user']['tick'],probes['b-system']['tick']);frequency=scm['frequency']
        require(snap.get('frequency')==absence.get('frequency')==frequency and snap.get('tick',-1)>=minimum and
                absence.get('tick',-1)>=minimum,'fixture-return-stale')
        owners=[]
        for path in context['ownedProcessFiles'][side]:
            record=read_json(path);record=record.get('identity',record)
            require(type(record.get('pid')) is int and record['pid']>0,'fixture-owner-record')
            owners.append(record['pid'])
        require(owners and set(owners)==set(absence.get('inspectedPids',[])),'fixture-inspected-owner-set')
    hashes=tool_hashes(config);case_ids=get(context,'caseIds')
    source_paths=[context_path,*sources.values(),*context['snapshots'].values(),*context['absenceEvidence'].values(),context['operatorEvidence']]
    source_paths += tls_paths
    source_paths += [path for paths in context['ownedProcessFiles'].values() for path in paths]
    source_paths += [str(root/('phase-'+name+'.json')) for name in phases]
    evidence=[{'artifact':str(safe_path(str(path),exists=True)),'sha256':digest(path)} for path in source_paths]
    index={'schema':'remoteassist-e2e-measurements-v1','candidateSha256':config['candidateSha256'],'caseIds':case_ids,
           'toolHashes':hashes,'identity':identity(config),'measurements':[]}
    for check in CHECKS:
        # The same measured end-to-end interval is retained for all boundaries;
        # separate small probe durations never masquerade as a total measurement.
        record={'schema':'remoteassist-e2e-measurement-artifact-v1','check':check,'producer':PRODUCERS[check],
            'mode':'witnessed','outcome':'PASS','runId':rid,'startTick':start['tick'],'endTick':phases['Finish']['tick'],
            'frequency':start['frequency'],'candidateSha256':config['candidateSha256'],'toolHashes':hashes,
            'identity':identity(config),'caseIds':case_ids,'phaseTicks':ticks,'resourcesAbsent':True,'recoveryVerified':True,
            'operatorWitnessed':True,'sourceEvidence':evidence,'contextSha256':digest(context_path)}
        path=proof_root/(check+'.json');new_json(path,record)
        index['measurements'].append({k:record[k] for k in ('check','producer','mode','outcome','runId','startTick','endTick','frequency') }|
                                     {'artifact':str(path),'sha256':digest(path)})
    validate_measurements(config,index);new_json(proof_root/'measurements.json',index);return index

def main(argv=None):
    p=argparse.ArgumentParser();p.add_argument('--config',required=True);p.add_argument('--context',required=True);p.add_argument('--output',required=True);a=p.parse_args(argv)
    try:collect(read_json(a.config),a.context,a.output);print(json.dumps({'state':'MEASURED_NOT_ARMED','functionalActions':0}));return 0
    except (CaseError,OSError,KeyError,TypeError,ValueError) as e:
        print(json.dumps({'state':'NOT_READY','code':getattr(e,'code',type(e).__name__)}));return 10

if __name__=='__main__':raise SystemExit(main())
