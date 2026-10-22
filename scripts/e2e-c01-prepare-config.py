"""Create a fresh C01 config from actual local files and read-only DTD status."""
import json,sys,subprocess,hashlib,shutil
from pathlib import Path
from e2e_case_config import generate
from e2e_case_runtime import ROOT,read_json,new_json,digest,read_config
from e2e_case_evidence import tool_hashes,identity
from e2e_case_ui import rules

output=Path(sys.argv[1]).resolve()
template={
 'candidateDirectory':str(ROOT/'.artifacts/c01-candidate-20261022-2155'),
 'desktopToolDirectory':str(ROOT/'.artifacts/c01-tools-20261022-2154/desktop-tool'),
 'inputTargetDirectory':str(ROOT/'.artifacts/c01-tools-20261022-2154/input-target'),
 'dtdConfig':str(ROOT/'.artifacts/dtd-e2e-20261020/Controller.json'),
 'a':{'sid':'S-1-5-21-3302815104-1998419199-3994616790-1001','session':2},
 'b':{'sid':'S-1-5-21-2995394558-58149160-2761003625-1001','session':2},
 'tls':{'certificate':str(ROOT/'.artifacts/lan-tls-20261018/lan-test.cer'),'thumbprint':'DF89155EC756C0B2D4EFA3BB11738689EDDD74AB','mode':'pinned-leaf'},
 'local':{'signaling':str(ROOT/'.artifacts/c01-readiness-20261022-2158/signaling.exe'),'node':'C:/Program Files/nodejs/node.exe',
          'python':sys.executable,'chrome':'C:/Program Files/Google/Chrome/Application/chrome.exe',
          'tlsCert':str(ROOT/'.artifacts/lan-tls-20261018/server-cert.pem'),'tlsKey':str(ROOT/'.artifacts/lan-tls-20261018/server-key.pem')},
 'uiRules':{}
}
rows=generate(template,str(output),('C01',));path=Path(rows[0]['config']);config=read_json(path)
sources={}
def add(name,p):sources[name]={'path':str(Path(p).resolve()),'sha256':digest(p)}
for side in ('a','b'):
 r=subprocess.run([config['dtdExe'],'--config',config['dtdConfig'],'--status','--machine','pc-'+side],capture_output=True,text=True,encoding='utf-8',timeout=10,check=True)
 p=output/('dtd-'+side+'.json');new_json(p,json.loads(r.stdout));add('dtd'+side.upper(),p)
probe=Path(config['desktopToolDirectory'])/'E2eDesktop.exe'
r=subprocess.run([str(probe),'--tls-check',config['tls']['certificate'],digest(config['tls']['certificate']),'192.168.0.61'],capture_output=True,text=True,encoding='utf-8',timeout=12,check=True)
p=output/'tls-a.json';new_json(p,json.loads(r.stdout));add('tlsA',p)
add('remote',ROOT/'.artifacts/c01-readiness-20261022-2158/b-readiness.json')
add('watchdog',ROOT/'.artifacts/c01-watchdog-20261022-2158/controller/cleanup.json')
add('watchdogResult',ROOT/'.artifacts/c01-watchdog-20261022-2158/results.json')
config=read_config(str(path),'C01',config['runDir'],require_evidence=False)
hashes=tool_hashes(config)
proof={'schema':'remoteassist-c01-dtd-readiness-v1','candidateSha256':config['candidateSha256'],'identity':identity(config),
       'toolHashes':hashes,'fixtureRunId':'4576f4c262fa4adca72c7287957d4f80','sources':sources,
       'fixtureTools':[{'path':str(ROOT/p),'sha256':digest(ROOT/p)} for p in ('scripts/e2e-native.cs','scripts/e2e-campaign.ps1','scripts/e2e-service-guard.ps1','tests/e2e/campaign-fixture.ps1','tests/e2e/scm-fixture.ps1')],
       'recoveryMode':'DTD Default snapshot plus independent SSH owned-resource inspection; no human witness claimed'}
from e2e_case_c01_readiness import validate
validate(config,proof)
new_json(config['sourceEvidence'],proof)
new_json(config['uiEvidence'],{'schema':'remoteassist-c01-runtime-ui-v1','caseId':'C01','candidateSha256':config['candidateSha256'],
         'identity':identity(config),'toolHashes':hashes,'rules':rules(config['uiRules']),'mode':'bind-actual-owner-and-unique-element-before-each-action'})
print(json.dumps({'config':str(path),'runDir':config['runDir'],'runId':config['runId'],'state':'LOCAL_EVIDENCE_BOUND_NOT_PREPARED'}))
