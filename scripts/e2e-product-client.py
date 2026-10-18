"""Private SSH evidence transport for the single frozen LAN campaign."""
import json,subprocess,sys,time
from pathlib import Path
root=Path(__file__).resolve().parent.parent
action=sys.argv[1]
if action not in ('arm','status','observe','finish','collect'):raise SystemExit('Unsupported action')
stage=(root/'.artifacts/product-stage.txt').read_text(encoding='utf-8').strip()
local=Path((root/'.artifacts/product-local-run.txt').read_text(encoding='utf-8').strip())
if (root/'.artifacts').resolve() not in local.resolve().parents:raise SystemExit('Local campaign scope mismatch')
if action=='arm':
    start=json.loads((local/'local-start.json').read_text(encoding='utf-8-sig'))
    # Windows perf_counter uses the same machine-wide QPC source as .NET Stopwatch.
    elapsed=time.perf_counter()-start['tick']/start['frequency']
    if not 0<=elapsed<=20 or not (local/'viewer-ready.json').exists() or (local/'local-error.json').exists():
        (local/'finish.json').write_text('{}',encoding='utf-8')
        raise SystemExit('Start window/readiness missed; local stop requested, B not armed')
destination=root/'.artifacts'/('product-'+action+'-'+str(time.time_ns())+'.json')
result=subprocess.run([sys.executable,str(root/'scripts/e2e-ssh-command.py'),'--script',str(root/('scripts/e2e-product-'+action+'.ps1')),'--output',str(destination),'--timeout','25','--staging',stage],capture_output=True,text=True,timeout=35)
if result.returncode:
    print(json.dumps({'state':'COMMAND_FAILED','action':action,'artifact':str(destination)}));sys.exit(1)
record=json.loads(destination.read_text(encoding='utf-8'))
if action=='status':
    data=json.loads(record['stdout'])
    pairing=data.pop('pairing.json',None)
    if pairing:
        temporary=local/'pairing.tmp';temporary.write_text(json.dumps(pairing),encoding='utf-8');temporary.replace(local/'pairing.json')
    print(json.dumps({'state':data,'pairingRetrieved':bool(pairing),'artifact':str(destination)},ensure_ascii=False))
else:
    print(json.dumps({'action':action,'output':record['stdout'].strip(),'artifact':str(destination)},ensure_ascii=False))
if action=='finish':(local/'finish.json').write_text('{}',encoding='utf-8')
