"""Resume only cleanup when a run's original controller has exited."""
import time
from e2e_case_runtime import read_json,new_json,get,require,stop_and_recover
from e2e_case_process import exists


def finish_existing(config,ctx,transport):
    from e2e_case_adapters import Dtd,Harness,CaseBackend
    from e2e_case_campaign import Campaign
    from e2e_case_live import LiveObserver
    launch=read_json(ctx.run_dir/'launch-once.json')
    require(launch['runId']==ctx.run_id and launch['caseId']==ctx.case_id,'abort-owner')
    ctx.origin=launch['origin'];ctx.stopping=True
    if not (ctx.run_dir/'finish.json').exists():new_json(ctx.run_dir/'finish.json',{'id':ctx.run_id,'reason':'explicit-abort','tick':ctx.clock()})
    harness=Harness(str(ctx.run_dir/'campaign.json'),transport)
    try:harness.call('finish',ctx,ctx.clock()+10000000000)
    except Exception:pass # Preserve the stop marker and still attempt recovery.
    wait_end=ctx.clock()+60000000000
    while exists(launch['controllerPid']):
        if (ctx.run_dir/'result.json').exists():return read_json(ctx.run_dir/'result.json')
        if ctx.clock()>=wait_end:
            return {'state':'FAIL_RECOVERY_REQUIRED','reason':'Original controller still owns recovery; no concurrent cleanup started'}
        time.sleep(.05)
    if (ctx.run_dir/'result.json').exists():return read_json(ctx.run_dir/'result.json')
    dtd=Dtd(config['dtdExe'],config['dtdConfig'],transport)
    observer=LiveObserver(config,Campaign(config,ctx,transport),dtd,harness)
    backend=CaseBackend(config,dtd,harness,observer);observer.backend=backend
    for machine in ('pc-a','pc-b'):
        session=read_json(ctx.run_dir/('dtd-'+machine+'.json'))
        require(session['runId']==ctx.run_id,'abort-session-owner')
        dtd.sessions[machine]=session['controlSessionId']
        observer.baselines[machine]=read_json(ctx.run_dir/('baseline-'+machine+'.json'))
    observer.runtime_started=(ctx.run_dir/'local-start.json').exists()
    owners=ctx.run_dir/'runtime-owners.json'
    if owners.exists():backend.owners=read_json(owners)
    for path in ctx.run_dir.glob('probe-*-owner.json'):
        record=read_json(path);observer.desktop.launched[path.stem]=(record['machine'],record['identity'])
    controller=ctx.run_dir/'local-controller-owner.json'
    if controller.exists():observer.desktop.launched['local-controller']=('pc-a',read_json(controller))
    if ctx.case_id=='C11':observer.desktop.clipboard={'recoveredOwner':True}
    stop_and_recover(ctx,backend)
    result={'caseId':ctx.case_id,'runId':ctx.run_id,'state':'ABORTED_RECOVERED' if ctx.recovery.get('verified') else 'FAIL_RECOVERY_REQUIRED',
            'cleanup':ctx.cleanup,'recovery':ctx.recovery,'origin':ctx.origin,'functionalActions':0}
    path=ctx.run_dir/'abort-recovery.json'
    if not path.exists():new_json(path,result)
    return result
