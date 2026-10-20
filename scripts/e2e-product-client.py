"""Explicit frozen-campaign SSH transport. No legacy stage/run pointer and no GUI input."""
import argparse,hashlib,json,subprocess,sys,time
from pathlib import Path
root=Path(__file__).resolve().parent.parent
p=argparse.ArgumentParser()
p.add_argument('action',choices=('arm','status','observe','finish','collect','evidence'))
p.add_argument('--campaign',required=True)
a=p.parse_args()
manifest_path=Path(a.campaign).resolve()
if (root/'.artifacts').resolve() not in manifest_path.parents:raise SystemExit('Workspace artifact campaign required')
m=json.loads(manifest_path.read_text(encoding='utf-8-sig'))
local=Path(m['localRun']).resolve()
if (root/'.artifacts').resolve() not in local.parents:raise SystemExit('Campaign local scope mismatch')
grant=[]
if a.action=='arm':
    auth=json.loads((local/'execution-authorization.json').read_text(encoding='utf-8-sig'))
    if auth!={'id':m['id'],'manifestSha256':hashlib.sha256(manifest_path.read_bytes()).hexdigest(),'scope':m['profile'],'requestedBy':'user-after-readiness-report'}:
        raise SystemExit('Separate execution instruction required')
    start=json.loads((local/'local-start.json').read_text(encoding='utf-8-sig'))
    if start['id']!=m['id'] or not (local/'viewer-ready.json').exists() or (local/'local-error.json').exists():
        raise SystemExit('Current local readiness missing')
    clock_file=local/('remote-clock-'+str(time.time_ns())+'.json')
    probe=subprocess.run([sys.executable,str(root/'scripts/e2e-ssh-command.py'),'--script',str(root/'scripts/e2e-product-status.ps1'),'--campaign',str(manifest_path),'--staging',m['staging'],'--output',str(clock_file),'--timeout','10'],capture_output=True,timeout=20)
    if probe.returncode:raise SystemExit('Remote clock not confirmed; do not repeat arm')
    clock=json.loads(json.loads(clock_file.read_text(encoding='utf-8'))['stdout'])['_clock']
    receipt=time.perf_counter()
    # B sampled before A received this response. Adding A remaining at receipt gives
    # a conservative B deadline; neither UTC offsets nor a fresh B duration are used.
    active_ms=(start['activeEnd']/start['frequency']-receipt)*1000
    deadline_ms=(start['deadline']/start['frequency']-60-receipt)*1000
    if clock['id']!=m['id'] or not 0<active_ms<deadline_ms or deadline_ms-active_ms<539999:
        raise SystemExit('Global execution/recovery reserve exhausted')
    grant=['--remote-deadline',str(clock['tick']+int(active_ms*clock['frequency']/1000)+540*clock['frequency']),
           '--remote-active-end',str(clock['tick']+int(active_ms*clock['frequency']/1000))]
if a.action=='finish':
    # Local stop cannot depend on a successful SSH roundtrip.
    try:
        with (local/'finish.json').open('x',encoding='utf-8') as f:json.dump({'id':m['id'],'reason':'requested-stop'},f)
    except FileExistsError:pass
budget=25
start_file=local/'local-start.json'
if start_file.exists():
    start=json.loads(start_file.read_text(encoding='utf-8-sig'))
    if start['id']!=m['id']:raise SystemExit('Campaign clock mismatch')
    remaining=(start['deadline']/start['frequency']-time.perf_counter())
    if remaining<=0 and a.action not in ('finish','collect'):raise SystemExit('Global deadline expired')
    # Overdue recovery still runs, but it is never reported as meeting the deadline.
    budget=max(1,min(budget,int(remaining))) if remaining>0 else 10
out=local/(a.action+'-'+str(time.time_ns())+'.json')
try:
    result=subprocess.run([sys.executable,str(root/'scripts/e2e-ssh-command.py'),'--script',str(root/('scripts/e2e-product-'+a.action+'.ps1')),'--campaign',str(manifest_path),'--staging',m['staging'],'--output',str(out),'--timeout',str(budget)]+grant,capture_output=True,text=True,encoding='utf-8',timeout=budget+15)
except subprocess.TimeoutExpired:
    print(json.dumps({'state':'COMPLETION_UNKNOWN','action':a.action,'artifact':str(out)}));raise SystemExit(2)
if result.returncode:
    print(json.dumps({'state':'COMMAND_FAILED_OR_UNKNOWN','action':a.action,'artifact':str(out)}));raise SystemExit(1)
record=json.loads(out.read_text(encoding='utf-8'))
data=json.loads(record['stdout']) if a.action in ('status','collect','evidence') else None
if a.action=='status':
    pairing=data.pop('pairing.json',None)
    if pairing:
        temporary=local/'pairing.tmp';temporary.write_text(json.dumps(pairing),encoding='utf-8');temporary.replace(local/'pairing.json')
# Pairing, evidence bodies and remote diagnostic stderr stay in private artifacts.
print(json.dumps({'state':'COLLECTED_NOT_PRODUCT_PASS','action':a.action,'id':m['id'],'artifact':str(out),'pairingRetrieved':bool(pairing) if a.action=='status' else False}))
