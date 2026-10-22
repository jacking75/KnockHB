"""One observed DesktopTestDriver action at a time; no input retry."""
import argparse, json, os, re, subprocess, time, uuid
from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
RUN_ID=os.environ.get('REMOTEASSIST_E2E_RUN_ID','')
if not re.fullmatch(r'\d{8}-\d{6}',RUN_ID):
    raise RuntimeError('Set REMOTEASSIST_E2E_RUN_ID to a fresh yyyyMMdd-HHmmss run')
OUT=ROOT/'.artifacts/remote-pc-e2e'/RUN_ID
EXE='F:/github/DesktopTestDriver/bin/live_Release/Controller/Controller.exe'
CONFIG=str(ROOT/'.artifacts/dtd-e2e-20261020/Controller.json')

def remaining():
    state=json.loads((OUT/'run.json').read_text(encoding='utf-8'))
    if state['runId']!=RUN_ID or (OUT/'failed.json').exists():
        raise RuntimeError('Run failed or identity mismatch; cleanup required')
    left=state['activeEnd']-time.monotonic()
    if left<=0: raise RuntimeError('Active deadline expired; cleanup required')
    return left

def cli(*args,cleanup=False):
    limit=15 if cleanup else min(15,remaining())
    p=subprocess.run([EXE,'--config',CONFIG,*args],capture_output=True,encoding='utf-8-sig',timeout=limit)
    result=json.loads(p.stdout)
    if result['status']!='completed':
        (OUT/f'failure-{uuid.uuid4().hex}.json').write_text(json.dumps(scrub(result),ensure_ascii=False,indent=2),encoding='utf-8')
        raise RuntimeError('DesktopTestDriver: '+str(result.get('error',{}).get('code','failed')))
    return result

def session(side,mode='control'):
    f=OUT/f'session-{side}-{mode}.json'
    if f.exists(): return json.loads(f.read_text())['data']['controlSessionId']
    r=cli('--session-open','--machine','pc-'+side,'--mode',mode,'--idle-seconds','900')
    f.write_text(json.dumps(r),encoding='utf-8');return r['data']['controlSessionId']

def scrub(result):
    copy=json.loads(json.dumps(result))
    for e in copy.get('data',{}).get('elements',[]):
        if e.get('automationId') in ('PairingCode','PairingPin'):
            e['name']='<redacted>';e['value']={'available':False,'text':None}
    return copy

def action(side,op,args,mode='control',private=False):
    remaining()
    rid=uuid.uuid4().hex
    request={'apiVersion':1,'requestId':rid,'controlSessionId':session(side,mode),'operation':op,'arguments':args}
    path=OUT/f'request-{rid}.json'
    path.write_text(json.dumps(request,ensure_ascii=False),encoding='utf-8')
    try: r=cli('--request',str(path))
    except Exception:
        (OUT/'failed.json').write_text(json.dumps({'requestId':rid,'operation':op}),encoding='utf-8')
        raise
    finally:
        if private: path.unlink(missing_ok=True)
    (OUT/f'response-{rid}.json').write_text(json.dumps(scrub(r),ensure_ascii=False,indent=2),encoding='utf-8')
    return r

def prepare():
    OUT.mkdir(parents=True,exist_ok=False)
    # Prepare does not start the product budget. Arm once immediately before B launcher.
    (OUT/'prepared.json').write_text(json.dumps({'runId':RUN_ID}),encoding='utf-8')
    (OUT/'stop-b.ps1').write_text(
        "$ErrorActionPreference='Stop'\n"
        f"$base='C:/Users/jacking75NoteBook/RemoteAssist-Test/{RUN_ID}'\n"
        "if(Test-Path \"$base/start.json\"){[IO.File]::WriteAllText(\"$base/stop\",'stop')}\n",
        encoding='utf-8')

def arm():
    with (OUT/'run.json').open('x',encoding='utf-8') as f:
        start=time.monotonic()
        json.dump({'runId':RUN_ID,'start':start,'activeEnd':start+300,
                   'cleanupEnd':start+600,'absoluteEnd':start+900},f)
    import sys
    worker=subprocess.Popen([sys.executable,str(ROOT/'scripts/manual_e2e_watch.py')],
                            creationflags=subprocess.CREATE_NO_WINDOW,
                            stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
    (OUT/'watchdog.json').write_text(json.dumps({'pid':worker.pid}),encoding='utf-8')
    end=time.monotonic()+5
    while not (OUT/'watchdog-ready.json').exists():
        if worker.poll() is not None or time.monotonic()>=end:
            (OUT/'failed.json').write_text('{"reason":"WatchdogNotReady"}',encoding='utf-8')
            raise RuntimeError('Independent watchdog not ready; do not launch product')
        time.sleep(.05)

def close_sessions():
    errors=[]
    for side,mode in (('a','control'),('b','control'),('a','observe'),('b','observe')):
        f=OUT/f'session-{side}-{mode}.json'
        if not f.exists(): continue
        try:
            sid=json.loads(f.read_text())['data']['controlSessionId']
            result=cli('--session-close',sid,cleanup=True)
            (OUT/('closed-'+f.name)).write_text(json.dumps(result),encoding='utf-8')
        except Exception as e: errors.append(str(e))
    if errors: raise RuntimeError('; '.join(errors))

if __name__=='__main__':
    import sys
    if len(sys.argv)==2 and sys.argv[1] in ('prepare','arm','close'):
        {'prepare':prepare,'arm':arm,'close':close_sessions}[sys.argv[1]]()
        sys.exit(0)
    p=argparse.ArgumentParser();p.add_argument('side',choices=['a','b']);p.add_argument('operation');p.add_argument('arguments',nargs='?',default='{}');p.add_argument('--mode',default='control');a=p.parse_args()
    print(json.dumps(scrub(action(a.side,a.operation,json.loads(a.arguments),a.mode)),ensure_ascii=False))
