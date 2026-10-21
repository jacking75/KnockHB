"""Frozen case packages and existing A/B harness integration.

Build/publish is a preparation operation. Run consumes existing files only.
No imports launch processes, connect to SSH, or touch the desktop.
"""
import json
import os
from pathlib import Path
import shutil
import sys
import time
import zipfile

from e2e_case_runtime import ROOT, CaseError, require, get, digest, safe_path, read_json, new_json


def inventory(directory):
    root=Path(directory)
    require(root.is_dir(),'package-directory-missing')
    rows=[]
    for path in sorted(root.rglob('*')):
        safe_path(str(path),root)
        if path.is_file():
            rows.append({'path':path.relative_to(root).as_posix(),'sha256':digest(path),'size':path.stat().st_size})
            require(len(rows)<=4096,'package-file-bound')
    require(bool(rows),'empty-package')
    return rows


def verify_candidate(config):
    root=Path(config['candidateDirectory'])
    candidate=read_json(root/'candidate.json')
    require(candidate.get('kind')=='UNSIGNED_DEVELOPMENT_NOT_RELEASE','candidate-kind')
    files=inventory(root/'payload')
    expected=sorted([{'path':r['path'].replace('\\','/'),'size':r['size'],'sha256':r['sha256'].lower()}
                     for r in candidate['files']],key=lambda r:r['path'])
    require(sorted(files,key=lambda r:r['path'])==expected,'candidate-payload-changed')
    settings=read_json(root/'payload/host.settings.json')
    require(settings.get('SignalingUri') in ('https://192.168.0.61:25001','https://192.168.0.61:25001/'),
            'candidate-signaling-origin')
    require(settings.get('ForceRelay',False) is (config['caseId']=='C15'),'candidate-relay-policy')
    return candidate


def package_campaign(config,ctx):
    require(ctx.case_id!='C15','turn-excluded-20261022',exit_code=10)
    verify_candidate(config)
    require(not (ctx.run_dir/'campaign.json').exists(),'campaign-replay')
    tools=[]
    for p in sorted((ROOT/'scripts').iterdir()):
        if p.is_file() and (p.name.startswith(('e2e-','e2e_case_')) or p.name=='install-host-dev.ps1'):
            tools.append({'path':'scripts/'+p.name,'sha256':digest(p)})
    archive=ctx.run_dir/'prepared.zip'
    with zipfile.ZipFile(archive,'x',compression=zipfile.ZIP_DEFLATED) as z:
        for entry in tools:z.write(ROOT/entry['path'],entry['path'])
        for key,target in (('candidateDirectory','candidate'),('desktopToolDirectory','desktop-tool'),('inputTargetDirectory','input-target')):
            source=Path(get(config,key))
            if key=='candidateDirectory':
                z.write(source/'candidate.json',target+'/candidate.json')
                for entry in inventory(source/'payload'):z.write(source/'payload'/entry['path'],target+'/payload/'+entry['path'])
            else:
                for entry in inventory(source):z.write(source/entry['path'],target+'/'+entry['path'])
        certificate=safe_path(get(config,'tls.certificate'),exists=True)
        z.write(certificate,'lan-test.cer')
        if ctx.case_id in ('C07','C08','C09'):
            z.writestr('inputs/ra-'+ctx.case_id.lower()+'.bin',bytes(range(256))*1024)
    (ctx.run_dir/'receive').mkdir()
    if ctx.case_id in ('C07','C08','C09'):
        (ctx.run_dir/'inputs').mkdir()
        with (ctx.run_dir/'inputs'/('ra-'+ctx.case_id.lower()+'.bin')).open('xb') as stream:stream.write(bytes(range(256))*1024)
    local=get(config,'local')
    local_files=[]
    for key in ('signaling','tlsCert','tlsKey','node','chrome','python'):
        p=safe_path(get(local,key),exists=True)
        local_files.append({'path':str(p),'sha256':digest(p)})
    for p in (ROOT/'web/viewer').rglob('*'):
        if p.is_file() and 'tests' not in p.parts:local_files.append({'path':str(p),'sha256':digest(p)})
    manifest={'version':4,'id':ctx.run_id,'caseId':ctx.case_id,'profile':'e2e-'+ctx.case_id,
        'expectedMask':config['expectedMask'],'state':'PREPARED_NOT_AUTHORIZED','staging':config['staging'],
        'localRun':str(ctx.run_dir),'candidateSha256':config['candidateSha256'],'archiveSha256':digest(archive),
        'tools':tools,'local':local,'localFiles':local_files,
        'identity':{'a':config['a'],'b':config['b']},
        'testTools':{'desktop-tool':inventory(config['desktopToolDirectory']),
                     'input-target':inventory(config['inputTargetDirectory'])},
        'tls':{'mode':'existing-chain','sha256':digest(certificate),'thumbprint':get(config,'tls.thumbprint')},
        'budget':{'totalSeconds':900,'targetSeconds':600,'activeSeconds':300,'cleanupSeconds':240,'recoverySeconds':300,'reportSeconds':60}}
    if ctx.case_id=='C04':manifest['uacTriggerSha256']=get(config,'caseInputs.uacTriggerSha256')
    new_json(ctx.run_dir/'campaign.json',manifest)
    shutil.copyfile(certificate,ctx.run_dir/'lan-test.cer')
    return manifest


class Campaign:
    def __init__(self,config,ctx,transport):
        self.config,self.ctx,self.transport=config,ctx,transport
        self.path=ctx.run_dir/'campaign.json'
        self.manifest=read_json(self.path)
        self.sha=digest(self.path)
        require(self.manifest['id']==ctx.run_id and self.manifest['caseId']==ctx.case_id,'campaign-owner')

    def remote(self,script,deadline,uploads=(),create=False,request=None):
        ctx=self.ctx
        ctx.tool_call('ssh')
        require(digest(self.path)==self.sha,'campaign-changed')
        output=ctx.run_dir/('ssh-'+__import__('uuid').uuid4().hex+'.json')
        seconds=ctx.remaining(deadline,180 if ctx.origin is None else 10)
        args=[str(ROOT/'scripts/e2e-ssh-command.py'),'--script',str(ROOT/'scripts'/script),
              '--staging',self.config['staging'],'--output',str(output),'--timeout',str(max(1,int(seconds)))]
        if create:args+=['--create-staging']
        else:args+=['--campaign',str(self.path)]
        if request:args+=['--case-request',request]
        for source,destination in uploads:args+=['--upload',str(source),destination]
        self.transport(sys.executable,args,seconds,ctx)
        record=read_json(output)
        require(record.get('exitCode')==0,'remote-command-failed')
        return json.loads(record['stdout'])

    def deploy(self,deadline):
        require(self.ctx.case_id!='C15','turn-excluded-20261022',exit_code=10)
        staging=self.config['staging']
        self.ctx.resource('prepared:remote','intent')
        try:
            deployed=self.remote('e2e-case-deploy.ps1',deadline,(
                (self.ctx.run_dir/'prepared.zip',staging+'/prepared.zip'),
                (self.path,staging+'/campaign.json')),create=True)
            self.ctx.resource('prepared:remote','acquired','campaign.json')
            preflight=self.remote('e2e-case-preflight.ps1',deadline)
        except Exception as primary:
            cleanup={'state':'FAIL_RECOVERY_REQUIRED','resources':['prepared:remote']}
            try:
                cleanup=self.remote('e2e-case-unprepare.ps1',min(deadline,self.ctx.clock()+10000000000))
                require(cleanup.get('status')=='completed','unprepare-incomplete')
                self.ctx.resource('prepared:remote','released')
            except Exception as error:
                cleanup['error']=error.code if isinstance(error,CaseError) else type(error).__name__
            new_json(self.ctx.run_dir/'prepare-failure.json',{
                'primary':primary.code if isinstance(primary,CaseError) else type(primary).__name__,'cleanup':cleanup})
            raise
        new_json(self.ctx.run_dir/'remote-preflight.json',preflight)
        return deployed

    def authorize(self):
        ctx=self.ctx
        auth={'id':ctx.run_id,'manifestSha256':self.sha,'scope':self.manifest['profile'],
              'requestedBy':'user-after-readiness-report'}
        # This function is called ONLY by Run --execute after all Prepare gates.
        new_json(ctx.run_dir/'execution-authorization.json',auth)

    def start_local(self,deadline):
        # DTD launches this controller with lifetime=keep; its independently
        # started watchdog survives a DTD session close/controller loss.
        return {'executable':str(Path(os.environ['SystemRoot'])/'System32/WindowsPowerShell/v1.0/powershell.exe'),
                'arguments':['-NoProfile','-NonInteractive','-WindowStyle','Hidden','-ExecutionPolicy','Bypass','-File',
                    str(ROOT/'scripts/e2e-local-session.ps1'),'-RunDir',str(self.ctx.run_dir),'-ManifestSha256',self.sha],
                'lifetime':'keep'}

    def authorize_remote(self,deadline):
        return self.remote('e2e-case-authorize.ps1',deadline,((self.ctx.run_dir/'execution-authorization.json',
            self.config['staging']+'/execution-authorization.json'),))

    def arm(self,deadline):
        ctx=self.ctx
        return self.transport(sys.executable,[str(ROOT/'scripts/e2e-product-client.py'),'arm',
            '--campaign',str(self.path)],ctx.remaining(deadline,10),ctx)
