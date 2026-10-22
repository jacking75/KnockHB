"""Real desktop observation bridge; DTD still owns every interactive launch/input."""
import base64
import hashlib
import json
import os
from pathlib import Path, PureWindowsPath
import time
import uuid
from e2e_case_runtime import require,get,new_json,read_json,CaseError


class DesktopSources:
    def __init__(self,config,campaign,dtd):
        self.config,self.campaign,self.dtd=config,campaign,dtd
        self.remote_info=None
        self.launched={}
        self.clipboard=None

    def bridge(self,operation,ctx,deadline,**fields):
        name='case-request-'+uuid.uuid4().hex+'.json'
        request=ctx.run_dir/name
        new_json(request,{'runId':ctx.run_id,'caseId':ctx.case_id,'operation':operation,**fields})
        try:
            return self.campaign.remote('e2e-case-bridge.ps1',deadline,
                ((request,self.config['staging']+'/'+name),),request=name)
        finally:request.unlink(missing_ok=True)

    def clock(self,machine,ctx,deadline):
        if machine=='pc-a':
            import ctypes
            tick=ctypes.c_longlong();frequency=ctypes.c_longlong()
            require(ctypes.windll.kernel32.QueryPerformanceCounter(ctypes.byref(tick)) and
                    ctypes.windll.kernel32.QueryPerformanceFrequency(ctypes.byref(frequency)),'local-qpc-unavailable')
            return {'tick':tick.value,'frequency':frequency.value,'root':str(ctx.run_dir),'output':str(ctx.run_dir)}
        response=self.bridge('clock',ctx,deadline)
        require(response['id']==ctx.run_id,'bridge-clock-owner')
        self.remote_info=response
        return response

    def read(self,machine,name,ctx,deadline):
        require(Path(name).name==name,'source-name')
        if machine=='pc-a':
            p=ctx.run_dir/name
            if not p.exists():return None
            if name.endswith('.ndjson'):
                from e2e_case_records import decode
                with p.open('rb') as f:return decode(f.read(1048577),True)
            return read_json(p)
        result=self.bridge('read',ctx,deadline,name=name)
        if not result['available']:return None
        raw=base64.b64decode(result['body'],validate=True)
        require(hashlib.sha256(raw).hexdigest()==result['sha256'].lower(),'source-hash-mismatch')
        require(len(raw)<=1048576,'source-size-bound')
        from e2e_case_records import decode
        return decode(raw,name.endswith('.ndjson'))

    def await_file(self,machine,name,ctx,deadline):
        while ctx.clock()<deadline:
            data=self.read(machine,name,ctx,deadline)
            if data is not None:return data
            error=self.read(machine,name+'.error',ctx,deadline)
            if error is not None:raise CaseError('desktop-probe-failed',get(error,'error'))
            time.sleep(min(.02,ctx.remaining(deadline)))
        raise CaseError('desktop-source-timeout',name)

    def launch_probe(self,machine,operation,ctx,deadline,wait=True,lease_deadline=None,**fields):
        require(not ctx.stopping or operation in ('snapshot','inspect-owned'),'recovery-probe-not-read-only')
        sample=self.clock(machine,ctx,deadline)
        require(type(sample.get('tick')) is int and type(sample.get('frequency')) is int and sample['tick']>=0 and sample['frequency']>0,'desktop-clock-domain')
        receipt=ctx.clock()
        # B clock sampled before receipt; grant cannot extend A's deadline.
        end=sample['tick']+int(max(0,((lease_deadline or deadline)-receipt)/1e9)*sample['frequency'])
        name='probe-'+uuid.uuid4().hex
        output=name+'-result.json'
        root=sample['output']
        identity=get(self.config,'a' if machine=='pc-a' else 'b')
        request={'runId':ctx.run_id,'caseId':ctx.case_id,'operation':operation,'root':root,
            'output':str(PureWindowsPath(root)/output),'deadlineTick':end,
            'session':get(identity,'session'),'sid':get(identity,'sid'),**fields}
        if machine=='pc-b':
            response=self.bridge('write-request',ctx,deadline,name=name+'.json',probe=request)
            path=response['path']
            exe=str(PureWindowsPath(sample['root'])/'desktop-tool/E2eDesktop.exe')
        else:
            path=str(ctx.run_dir/(name+'.json'));new_json(path,request)
            exe=str(Path(self.config['desktopToolDirectory'])/'E2eDesktop.exe')
        if operation=='fault-guard':
            require(machine=='pc-b' and ctx.case_id=='C16','fault-machine')
            self.bridge('start-fault',ctx,deadline,name=name+'.json')
            return {'output':output,'requestName':name+'.json'}
        response=self.dtd.request(machine,'launch',{'executable':exe,'arguments':['--request',path],
            'lifetime':'keep' if operation=='clipboard-guard' else 'session'},ctx,deadline,key=ctx.active_step+':probe:'+name)
        self.launched[name]=(machine,get(response,'data'))
        new_json(ctx.run_dir/(name+'-owner.json'),{'machine':machine,'identity':get(response,'data')})
        if wait:return self.await_file(machine,output,ctx,deadline)
        return {'output':output,'requestName':name+'.json','process':get(response,'data')}

    def snapshot(self,machine,ctx,deadline):
        launched=self.launch_probe(machine,'snapshot',ctx,deadline,wait=False)
        data=self.await_file(machine,launched['output'],ctx,deadline)
        require(data.get('runId')==ctx.run_id and data.get('caseId')==ctx.case_id,'snapshot-run-owner')
        data['_sourceName']=launched['output']
        return data

    def toggles(self,owner,window_title,names,ctx,deadline):
        data=self.launch_probe('pc-b','uia',ctx,deadline,owner=owner,windowTitle=window_title,automationIds=names)
        require(data['available'] is True,'uia-unavailable')
        require(data.get('ownerPid')==owner['pid'] and all(e.get('enabled') is True for e in data['elements']),'uia-owner-or-disabled')
        states={e['automationId']:e['toggle'] for e in data['elements']}
        require(set(states)==set(names) and all(type(v) is bool for v in states.values()),'toggle-unavailable')
        return states

    def capture_clipboard(self,ctx,deadline):
        require(ctx.case_id=='C11' and self.clipboard is None,'clipboard-owner-replay')
        ctx.resource('clipboard:pc-b','intent')
        if self.remote_info is None:self.clock('pc-b',ctx,deadline)
        root=PureWindowsPath(self.remote_info['output'])
        # Independent restore deadline is the existing cleanup phase end, never
        # a duration renewed by a subsequent command/session/terminal.
        launched=self.launch_probe('pc-b','clipboard-guard',ctx,deadline,wait=False,lease_deadline=ctx.end(540),
            restoreSignal=str(root/'clipboard-restore'),restoredOutput=str(root/'clipboard-restored.json'))
        self.clipboard=launched
        data=self.await_file('pc-b',launched['output'],ctx,deadline)
        require(data.get('runId')==ctx.run_id and data.get('caseId')==ctx.case_id and data.get('state')=='captured'
                and data.get('pid')==launched['process']['pid'] and data.get('captured') is True,'clipboard-baseline-owner')
        self.clipboard['baseline']=data
        new_json(ctx.run_dir/'clipboard-guard-owner.json',self.clipboard)
        ctx.resource('clipboard:pc-b','acquired','clipboard-guard-owner.json')
        return data

    def restore_clipboard(self,ctx,deadline):
        if self.clipboard is None:return {'status':'completed','changed':False}
        self.bridge('signal',ctx,deadline,name='clipboard-restore')
        data=self.await_file('pc-b','clipboard-restored.json',ctx,deadline)
        base=get(self.clipboard,'baseline')
        require(all(data.get(k)==base.get(k) for k in ('runId','caseId','pid','createdAt','frequency')) and
                data.get('state')=='restored' and data.get('comparisonVerified') is True and data.get('flushCompleted') is True,
                'clipboard-restore-owner')
        from e2e_case_runtime import elapsed
        elapsed(data,'startedTick','tick',10000)
        require(data.get('baselineRestored') is True and data.get('withinLimit') is True,'clipboard-restore-unverified')
        ctx.resource('clipboard:pc-b','released')
        return {'status':'completed',**data}

    def start_fixture(self,kind,ctx,deadline):
        require(kind=='input-target','fixture-kind')
        sample=self.clock('pc-b',ctx,deadline)
        return self.dtd.request('pc-b','launch',{
            'executable':str(PureWindowsPath(sample['root'])/'input-target/RemoteAssist.InputTarget.exe'),
            'arguments':['--output',sample['output'],'--seconds',str(max(30,min(300,int(ctx.remaining(ctx.end(300),300))))),'--marker',ctx.run_id],
            'lifetime':'session'},ctx,deadline,key='fixture:input-target')

    def signal(self,name,ctx,deadline):
        return self.bridge('signal',ctx,deadline,name=name)

    def arm_fault(self,service,fixture,ctx,deadline):
        sample=self.clock('pc-b',ctx,deadline)
        root=PureWindowsPath(sample['output'])
        record=self.launch_probe('pc-b','fault-guard',ctx,deadline,wait=False,
            lease_deadline=min(ctx.end(300),ctx.clock()+20000000000),
            devOwner='C:/Program Files/RemoteAssist-Dev/dev-owner.json',service=service,fixture=fixture,
            fixtureObservation=str(root/'input-target.json'),firedOutput=str(root/'fault-fired.json'),
            stopSignal=str(root/'fault-stop'))
        return self.await_file('pc-b',record['output'],ctx,deadline)

    def stop_probes(self,ctx,deadline):
        for machine in ('pc-a','pc-b'):
            ids=[get(identity,'processId') for m,identity in self.launched.values() if m==machine]
            if ids:self.dtd.request(machine,'terminate-owned',{'processIds':ids},ctx,deadline,key='cleanup:probes:'+machine)
        return {'status':'completed'}
