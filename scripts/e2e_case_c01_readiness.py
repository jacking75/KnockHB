"""C01-only readiness from real DTD/SSH and independent cleanup observations.

No fabricated UAC/operator/UI observations. UI ownership is bound by the live
runner before each action. This does not apply to secure-desktop/input cases.
"""
import json
from pathlib import Path
from e2e_case_runtime import require,read_json,digest,get,safe_path,equal,ROOT
from e2e_case_evidence import identity,tool_hashes

def validate(config,proof):
    require(config['caseId']=='C01' and config['expectedMask']==1,'c01-readiness-scope',exit_code=10)
    require(proof.get('schema')=='remoteassist-c01-dtd-readiness-v1','c01-readiness-schema',exit_code=10)
    require(proof.get('candidateSha256')==config['candidateSha256'] and equal(proof.get('identity'),identity(config)) and
            proof.get('toolHashes')==tool_hashes(config),'c01-readiness-binding',exit_code=10)
    sources={}
    for name,entry in proof['sources'].items():
        path=safe_path(entry['path'],ROOT/'.artifacts',True)
        require(digest(path)==entry['sha256'],'c01-source-changed',name,10)
        sources[name]=read_json(path)
    for entry in proof['fixtureTools']:
        require(digest(safe_path(entry['path'],ROOT,True))==entry['sha256'],'c01-fixture-code-changed',exit_code=10)
    a=sources['watchdog'];ar=sources['watchdogResult']
    require(a['state']=='FIXTURE_CLEAN' and a['by']=='Watchdog' and a['campaign']==proof['fixtureRunId'] and
            ar['status']=='PASS' and ar['fault']=='controller' and 0<ar['seconds']<=12,'c01-watchdog',exit_code=10)
    remote=sources['remote'];require(remote['exitCode']==0,'c01-ssh-readiness',exit_code=10)
    b=json.loads(remote['stdout']);s=b['scm']
    require(b['state']=='READINESS_CHECKED_NOT_PRODUCT_PASS' and b['taskAbsent'] is True and s['state']=='PASS' and
            s['by']=='Watchdog' and s['campaign']==proof['fixtureRunId'] and s['serviceAbsent'] is True and s['processAbsent'] is True and
            b['results']['status']=='PASS' and 0<b['results']['seconds']<=65,'c01-scm-cleanup',exit_code=10)
    from e2e_case_campaign import inventory
    expected=sorted((r['path'],r['sha256']) for r in inventory(Path(config['desktopToolDirectory']).parent/'scm-fixture'))
    require(sorted((r['name'],r['sha256'].lower()) for r in s['fixtureFiles'])==expected,'c01-scm-tool-changed',exit_code=10)
    for tls in (sources['tlsA'],b['tls']):
        require(tls['status']=='completed' and tls['chainValid'] is True and tls['hostnameValid'] is True and tls['storeMutated'] is False and
                tls['certificateSha256']==digest(config['tls']['certificate']),'c01-tls',exit_code=10)
    for side in ('a','b'):
        status=sources['dtd'+side.upper()]
        require(status['status']=='completed','c01-dtd-unavailable',exit_code=10)
        data=status['data'];require(data['machineId']=='pc-'+side and data['desktop']=='Default' and data['inputAvailable'] is True and
            data['interactiveSessionId']==config[side]['session'],'c01-dtd-identity',exit_code=10)
        require(not any(s['state'] in ('active','closing','blocked') for s in data.get('sessions',[])),'c01-dtd-session-busy',exit_code=10)
    settings=read_json(Path(config['candidateDirectory'])/'payload/host.settings.json')
    import base64
    require(base64.b64decode(settings.get('SignalingCertificateBase64',''),validate=True)==Path(config['tls']['certificate']).read_bytes() and
            config['tls'].get('mode')=='pinned-leaf','c01-candidate-tls-binding',exit_code=10)
    return proof
