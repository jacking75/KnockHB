"""Resume only cleanup when a run's original controller has exited."""
import time
from e2e_case_runtime import read_json,new_json,get,require,stop_and_recover
from e2e_case_process import exists


def finish_existing(config,ctx,transport):
    from e2e_case_adapters import Dtd,Harness,CaseBackend
    from e2e_case_campaign import Campaign
    from e2e_case_live import LiveObserver
    if not (ctx.run_dir/'launch-once.json').exists():
        campaign=Campaign(config,ctx,transport)
        reply=campaign.remote('e2e-case-unprepare.ps1',ctx.clock()+10000000000)
        require(reply.get('status')=='completed','prepare-abort-recovery-incomplete')
        return {'caseId':ctx.case_id,'runId':ctx.run_id,'state':'PREPARATION_RETIRED_NOT_EXECUTED',
                'preparationCleanup':reply,'recovery':{'verified':False},'functionalActions':0}
    launch=read_json(ctx.run_dir/'launch-once.json')
    require(launch['runId']==ctx.run_id and launch['caseId']==ctx.case_id,'abort-owner')
    ctx.origin=launch['origin'];ctx.stopping=True
    if not (ctx.run_dir/'finish.json').exists():new_json(ctx.run_dir/'finish.json',{'id':ctx.run_id,'reason':'explicit-abort','tick':ctx.clock()})
    harness=Harness(str(ctx.run_dir/'campaign.json'),transport)
    try:harness.call('finish',ctx,min(ctx.end(900),ctx.clock()+10000000000))
    except Exception:pass # Preserve the stop marker and still attempt recovery.
    wait_end=min(ctx.end(900),ctx.clock()+60000000000)
    def saved_result():
        previous=read_json(ctx.run_dir/'result.json')
        require(previous.get('runId')==ctx.run_id and previous.get('caseId')==ctx.case_id,'abort-result-owner')
        return {'caseId':ctx.case_id,'runId':ctx.run_id,'state':'SAVED_RESULT_ONLY','previousResult':previous,'recovery':{'verified':False},
                'reason':'Past result is not a fresh independent return observation','functionalActions':0}
    while exists(launch['controllerPid']):
        if (ctx.run_dir/'result.json').exists():return saved_result()
        if ctx.clock()>=wait_end:
            return {'state':'FAIL_RECOVERY_REQUIRED','reason':'Original controller still owns recovery; no concurrent cleanup started'}
        time.sleep(.05)
    if (ctx.run_dir/'result.json').exists():return saved_result()
    dtd=Dtd(config['dtdExe'],config['dtdConfig'],transport)
    observer=LiveObserver(config,Campaign(config,ctx,transport),dtd,harness)
    backend=CaseBackend(config,dtd,harness,observer);observer.backend=backend
    load_errors=[]
    lifecycle=ctx.run_dir/'lifecycle.ndjson'
    if lifecycle.exists():
        from e2e_case_records import decode,sequence
        try:ctx.lifecycle=sequence(decode(lifecycle.read_bytes(),True),ctx.run_id)
        except Exception as e:load_errors.append('lifecycle:'+getattr(e,'code',type(e).__name__))
    actions=ctx.run_dir/'action-ledger.ndjson'
    if actions.exists():
        from e2e_case_records import decode
        try:
            ctx.ledger=decode(actions.read_bytes(),True)
            require(all(r.get('runId')==ctx.run_id and r.get('caseId')==ctx.case_id for r in ctx.ledger),'abort-action-ledger-owner')
            ctx.consumed={r['operation'] for r in ctx.ledger if r.get('status')=='intent'}
        except Exception as e:load_errors.append('action-ledger:'+getattr(e,'code',type(e).__name__))
    def load(path,apply):
        if not path.exists():return
        try:apply(read_json(path))
        except Exception as e:load_errors.append(path.name+':'+getattr(e,'code',type(e).__name__))
    for machine in ('pc-a','pc-b'):
        def session(value,m=machine):
            require(value['runId']==ctx.run_id,'abort-session-owner');dtd.sessions[m]=value['controlSessionId']
        load(ctx.run_dir/('dtd-'+machine+'.json'),session)
        load(ctx.run_dir/('baseline-'+machine+'.json'),lambda value,m=machine:observer.baselines.__setitem__(m,value))
    observer.runtime_started=(ctx.run_dir/'local-start.json').exists() or (ctx.run_dir/'local-controller-owner.json').exists()
    if any(r['resource'].startswith('runtime:') and r['state']=='intent' for r in ctx.lifecycle):observer.runtime_started=True
    for machine in ('pc-a','pc-b'):
        if machine not in dtd.sessions and any(r['resource']=='dtd:'+machine and r['state']=='intent' for r in ctx.lifecycle):
            load_errors.append('dtd:'+machine+':ownership-unknown')
    owners=ctx.run_dir/'runtime-owners.json'
    load(owners,lambda value:setattr(backend,'owners',value))
    for path in ctx.run_dir.glob('probe-*-owner.json'):
        load(path,lambda value,p=path:observer.desktop.launched.__setitem__(p.stem,(value['machine'],value['identity'])))
    controller=ctx.run_dir/'local-controller-owner.json'
    load(controller,lambda value:observer.desktop.launched.__setitem__('local-controller',('pc-a',value)))
    if ctx.case_id=='C11':
        load(ctx.run_dir/'clipboard-guard-owner.json',lambda value:setattr(observer.desktop,'clipboard',value))
        # Even a capture reply lost before owner persistence must get its
        # restore signal. Never start a replacement baseline guard.
        try:observer.desktop.signal('clipboard-restore',ctx,min(ctx.end(840),ctx.clock()+10000000000))
        except Exception as e:load_errors.append('clipboard-signal:'+getattr(e,'code',type(e).__name__))
    stop_and_recover(ctx,backend)
    if load_errors:ctx.recovery.update(verified=False,loadErrors=load_errors)
    result={'caseId':ctx.case_id,'runId':ctx.run_id,'state':'ABORTED_RECOVERED' if ctx.recovery.get('verified') else 'FAIL_RECOVERY_REQUIRED',
            'cleanup':ctx.cleanup,'recovery':ctx.recovery,'origin':ctx.origin,'functionalActions':0}
    path=ctx.run_dir/'abort-recovery.json'
    if not path.exists():new_json(path,result)
    return result
