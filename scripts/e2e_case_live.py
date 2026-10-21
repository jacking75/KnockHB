"""Live adapters for the closed case runner. Only Run --execute constructs these."""
import base64
import json
from pathlib import Path
import time
from e2e_case_runtime import (CaseError,require,get,new_json,read_json,digest,bind_observation,
                              MASKS,CHANNELS,ROOT)
from e2e_case_desktop import DesktopSources


class LiveObserver:
    def __init__(self,config,campaign,dtd,harness):
        self.config,self.campaign,self.dtd,self.harness=config,campaign,dtd,harness
        self.desktop=DesktopSources(config,campaign,dtd)
        self.backend=None
        self.baselines={}
        self.raw={}
        self.last={}
        self.runtime_started=False
        self.fixture=None

    def read(self,source,ctx,deadline):
        from e2e_case_sources import read
        return read(self,source,ctx,deadline)

    def after_join(self,ctx,deadline):
        from e2e_case_sources import observation_window
        while not any(r.get('message',{}).get('type',r.get('message',{}).get('t'))=='consent-pending' for r in self.events(ctx)):
            time.sleep(min(.05,ctx.remaining(deadline)))
        if ctx.case_id!='C01':observation_window(self,ctx,deadline,'preConsent')

    def after_grant(self,ctx,deadline):
        while True:
            ready=[r['message'] for r in self.events(ctx) if r.get('message',{}).get('t')=='data-ready']
            if ready:break
            time.sleep(min(.05,ctx.remaining(deadline)))
        product=self.product(ctx,deadline)
        initialized=[r for r in product['trace'] if r['kind']=='input-backend-ready']
        if initialized:self.last['initialInputEpoch']=initialized[-1]['metadata']['epoch']
        if self.fixture is not None:
            rule,_,owner=self.backend.binding('pc-b','fixture')
            window=self.dtd.window('pc-b',rule,owner,ctx,deadline)
            self.dtd.focus('pc-b',window,rule,owner,ctx,deadline,key='fixture:focus-after-grant')

    def book_consent(self,ctx,deadline):
        import re
        from e2e_case_sources import text_field,assemble
        from e2e_case_observations import derive
        text=text_field(self,'runbook-summary-text',ctx,deadline)
        hashes=re.findall(r'SHA256 ([a-f0-9]{64})',text)
        require(len(hashes)==1 and 'detail=summary' in text,'book-dialog-summary')
        s=assemble(self,'preview',ctx,deadline)
        data=derive(ctx.case_id,'preview',s)
        data.update(dialogHash=hashes[0],workerBeforeCount=sum(r['kind']=='runbook-start' for r in s['trace']))
        self.last['book-consent']=data
        return data

    def wait_local(self,name,ctx,deadline):
        while ctx.clock()<deadline:
            for error in ('local-error.json','local-watchdog-error.json','viewer-error.json'):
                if (ctx.run_dir/error).exists():raise CaseError('runtime-error',error)
            p=ctx.run_dir/name
            if p.exists():return read_json(p)
            time.sleep(min(.02,ctx.remaining(deadline)))
        raise CaseError('runtime-ready-timeout',name)

    def harness_data(self,action,ctx,deadline):
        reply=self.harness.call(action,ctx,deadline)
        record=read_json(get(reply,'artifact'))
        require(record['exitCode']==0,'harness-source-failed')
        return json.loads(record['stdout'])

    def begin(self,backend,ctx,deadline):
        self.backend=backend
        self.campaign.authorize()
        for machine in ('pc-a','pc-b'):
            self.dtd.open(machine,ctx,deadline)
            actual=self.desktop.snapshot(machine,ctx,deadline)
            expected=self.config['a' if machine=='pc-a' else 'b']
            require(actual.get('available') is True and actual['desktop']=='Default' and
                    actual['session']==expected['session'] and actual['activeSessionId']==expected['session'] and actual['sid']==expected['sid'] and
                    actual['heldVirtualKeys']==[],'baseline-desktop-identity-or-held')
            self.baselines[machine]=actual
            new_json(ctx.run_dir/('baseline-'+machine+'.json'),actual)

    def start_runtimes(self,ctx,deadline,relay=False):
        require(not self.runtime_started and relay==(ctx.case_id=='C15'),'runtime-start-replay-or-policy')
        self.runtime_started=True
        launch=self.campaign.start_local(deadline)
        response=self.dtd.request('pc-a','launch',launch,ctx,deadline,key='runtime:local-controller')
        new_json(ctx.run_dir/'local-controller-owner.json',get(response,'data'))
        self.desktop.launched['local-controller']=('pc-a',get(response,'data'))
        self.wait_local('viewer-ready.json',ctx,deadline)
        self.campaign.authorize_remote(deadline)
        self.campaign.arm(deadline)
        while True:
            state=self.harness_data('status',ctx,deadline)
            if state.get('product-error.json'):raise CaseError('product-start-failed','product-error.json')
            if state.get('product-running.json'):break
            time.sleep(min(.05,ctx.remaining(deadline)))
        self.raw['runtime']=state
        viewer=self.wait_local('viewer-owner.json',ctx,deadline)
        require(viewer['runId']==ctx.run_id,'viewer-owner-run')
        for machine,role,image,pid in (
            ('pc-a','viewer',get(self.config,'local.chrome'),viewer['pid']),
            ('pc-b','tray','C:\\Program Files\\RemoteAssist-Dev\\RemoteAssist.Host.Tray.exe',None)):
            while True:
                rows=get(self.dtd.request(machine,'windows',{},ctx,deadline,False),'data.windows')
                matches=[r for r in rows if r['imagePath'].lower().replace('/','\\')==image.lower().replace('/','\\') and
                         (pid is None or r['pid']==pid)]
                identities={r['pid'] for r in matches}
                require(len(identities)<=1,'runtime-window-owner-ambiguous')
                if len(identities)==1:
                    row=matches[0];self.backend.owners.setdefault(machine,{})[role]={k:row[k] for k in ('pid','createdAt','imagePath')};break
                time.sleep(min(.05,ctx.remaining(deadline)))
        self.wait_local('browser-observation.json',ctx,deadline)
        self.baselines['browser']=self.browser(ctx)
        new_json(ctx.run_dir/'runtime-owners.json',self.backend.owners)
        if ctx.case_id in ('C01','C15'):self.start_owned_fixture('input-target',ctx,deadline)
        if ctx.case_id=='C03':
            directory=ctx.run_dir/'a-fixture';directory.mkdir()
            response=self.dtd.request('pc-a','launch',{'executable':str(Path(self.config['inputTargetDirectory'])/'RemoteAssist.InputTarget.exe'),
                'arguments':['--output',str(directory),'--seconds',str(max(30,int(ctx.remaining(ctx.end(300),300))))],
                'lifetime':'session'},ctx,deadline,key='fixture:blur-window')
            self.last['blur-fixture']=get(response,'data')
            self.backend.owners.setdefault('pc-a',{})['fixture']={k:get(response,'data')[k] for k in ('pid','createdAt','imagePath')}
            new_json(ctx.run_dir/'blur-fixture-owner.json',get(response,'data'))
        if ctx.case_id in ('C04','C05','C06'):
            self.last['session-watch']=self.desktop.launch_probe('pc-b','session-watch',ctx,deadline,
                lease_deadline=ctx.end(300))

    def browser(self,ctx):
        value=read_json(ctx.run_dir/'browser-observation.json')
        require(value['runId']==ctx.run_id and not value['overflow'],'browser-source-invalid')
        self.raw['browser']=value
        if value['selected']:self.last['selected-pairs']=value['selected']
        return value

    def events(self,ctx):
        p=ctx.run_dir/'browser-metadata.ndjson'
        require(p.exists() and p.stat().st_size<=1048576,'browser-events-unavailable')
        lines=p.read_text(encoding='utf-8').splitlines()
        rows=[json.loads(line) for line in lines if line]
        require(all(r.get('runId')==ctx.run_id for r in rows),'browser-events-owner')
        require([r['seq'] for r in rows]==list(range(1,len(rows)+1)),'browser-events-gap')
        self.raw['browser-events']=rows
        for r in rows:
            m=r.get('message',{})
            if m.get('sessionId'):
                if ctx.session_id is not None:require(ctx.session_id==m['sessionId'],'unexpected-session-change')
                ctx.session_id=m['sessionId']
                if 'generation' in m:
                    if ctx.generation is not None:require(ctx.generation==m['generation'],'unexpected-generation-change')
                    ctx.generation=m['generation']
        return rows

    def product(self,ctx,deadline):
        files=self.harness_data('evidence',ctx,deadline)
        data={}
        for name,encoded in files.items():
            require(Path(name).name==name,'product-artifact-name')
            raw=base64.b64decode(encoded,validate=True)
            require(len(raw)<=1048576,'product-evidence-bound')
            if name.endswith('.ndjson'):
                lines=raw.decode('utf-8-sig').splitlines()
                # A writer may have an incomplete last line; it is unavailable,
                # never silently treated as a complete observation.
                if raw and not raw.endswith((b'\n',b'\r')):
                    from e2e_case_observations import Pending
                    raise Pending(name)
                data[name]=[json.loads(line) for line in lines if line]
            else:data[name]=json.loads(raw.decode('utf-8-sig'))
        trace=[]
        for name,rows in data.items():
            if name.startswith('trace-'):
                require([r['sequence'] for r in rows]==list(range(1,len(rows)+1)),'product-trace-gap')
                require(all(r['runId']==ctx.run_id for r in rows),'product-trace-owner')
                trace.extend(rows)
        data['trace']=sorted(trace,key=lambda r:r['tick'])
        self.raw['product']=data
        return data

    def consent_states(self,ctx,deadline):
        names=['ConsentScreen','ConsentControl','ConsentAdmin','consent-file','consent-clipboard',
               'consent-diagnostics','consent-runbook','consent-chat']
        rule,_,owner=self.backend.binding('pc-b','ConsentApprove')
        window=self.dtd.window('pc-b',rule,owner,ctx,deadline)
        return self.desktop.toggles(owner,window['title'],names,ctx,deadline)

    def admin_consent(self,ctx,deadline):
        rule,_,owner=self.backend.binding('pc-b','AdminConsentApprove')
        window=self.dtd.window('pc-b',rule,owner,ctx,deadline)
        toggles=self.desktop.toggles(owner,window['title'],['AdminConsentScope'],ctx,deadline)
        data=self.browser(ctx)
        return {'pending':data['controlState']=='admin-state','inputEnabled':data['inputEnabled'],
                'productHeld':self.held(ctx,deadline)['productHeld'],'ownerMatched':True,
                'scopeChecked':toggles['AdminConsentScope']}

    def held(self,ctx,deadline):
        product=self.product(ctx,deadline)
        acks=[r for r in product['trace'] if r['kind'] in ('reservation-ack','input-backend-ready') and r['sessionId']==ctx.session_id]
        require(bool(acks),'product-held-observation-unavailable')
        actual=self.desktop.snapshot('pc-b',ctx,deadline)
        return {'productHeld':acks[-1]['metadata']['heldCount'],'osHeld':len(actual['heldVirtualKeys']),
                'observationValid':actual['available'] is True}

    def start_owned_fixture(self,kind,ctx,deadline):
        reply=self.desktop.start_fixture(kind,ctx,deadline)
        self.fixture=get(reply,'data')
        self.backend.owners.setdefault('pc-b',{})['fixture']={k:self.fixture[k] for k in ('pid','createdAt','imagePath')}
        before=self.desktop.await_file('pc-b','input-target.json',ctx,deadline)
        require(before['pid']==self.fixture['pid'] and before['observationValid'],'fixture-owner')
        self.baselines['fixture']=before
        return before

    def capture_clipboard(self,ctx,deadline):
        self.last['clipboard-baseline']=self.desktop.capture_clipboard(ctx,deadline)
        return self.last['clipboard-baseline']

    def restore_clipboard(self,ctx,deadline):
        self.last['clipboard-restored']=self.desktop.restore_clipboard(ctx,deadline)
        return self.last['clipboard-restored']

    def trigger_uac(self,ctx,deadline):
        self.operator_ready(ctx,deadline)
        self.last['uac-trigger']=self.desktop.launch_probe('pc-b','uac-trigger',ctx,deadline,wait=False,
            lease_deadline=min(ctx.end(300),ctx.clock()+30000000000),triggerSha256=get(self.config,'caseInputs.uacTriggerSha256'))

    def arm_fault(self,ctx,deadline):
        require(self.fixture is not None,'fault-fixture-missing')
        status=self.harness_data('status',ctx,deadline)
        services=[p for p in get(status['product-running.json'],'ownedProcesses')
                  if p['image'].endswith('RemoteAssist.Host.Service.exe')]
        require(len(services)==1,'fault-service-not-unique')
        self.last['fault-armed']=self.desktop.arm_fault(services[0],self.fixture,ctx,deadline)
        return self.last['fault-armed']

    def video_geometry(self,ctx,deadline):
        rule,element_rule,owner=self.backend.binding('pc-a','screen')
        window=self.dtd.window('pc-a',rule,owner,ctx,deadline)
        element=self.dtd.element('pc-a',window,element_rule,ctx,deadline)
        capture=self.dtd.capture('pc-a',window,ctx.run_dir/('geometry-'+__import__('uuid').uuid4().hex+'.png'),ctx,deadline)
        actual=self.desktop.await_file('pc-b','input-target.json',ctx,deadline)
        require(self.fixture is not None and actual['pid']==self.fixture['pid'] and actual['observationValid'],'geometry-fixture-owner')
        product=self.product(ctx,deadline)
        starts=[r for r in product['trace'] if r['kind']=='capture-start' and r['sessionId']==ctx.session_id]
        require(len(starts)==1,'capture-monitor-unavailable-or-restarted')
        monitor=starts[0]['metadata'];bounds=element['bounds']
        # DTD API1 PublicElement.Bounds is already window-client-relative.
        viewport={k:bounds[k] for k in ('x','y','width','height')}
        b=self.baselines['fixture']['bounds']
        # Endpoints derived from the observed fixture client bounds. No desktop
        # resolution, guessed monitor origin or preselected screen coordinates.
        start=[b['left']+(b['right']-b['left'])*.3,b['top']+(b['bottom']-b['top'])*.85]
        end=[b['left']+(b['right']-b['left'])*.7,start[1]]
        result={'windowId':window['windowId'],'monitor':monitor,'viewport':viewport,
            'capture':{'valid':True,'captureId':capture['captureId']},'validUntilTick':ctx.clock()+1000000000,
            'targets':{'fixture-button':[actual['button']['x'],actual['button']['y']]},
            'wheelPoint':[actual['textBox']['x'],actual['textBox']['y']],
            'drag':{'start':start,'end':end,'steps':5}}
        self.last['geometry']=result
        self.raw['geometry-capture']=capture
        return result

    def uac_cancel_position(self,window,ctx,deadline):
        require(ctx.case_id=='C04','uac-target-case')
        path=ctx.run_dir/('uac-target-'+__import__('uuid').uuid4().hex+'.png')
        capture=self.dtd.capture('pc-a',window,path,ctx,deadline)
        result=self.desktop.launch_probe('pc-a','ocr-cancel',ctx,deadline,
            image=str(path),imageSha256=capture['sha256'])
        require(result['available'] and result['uacSceneMatched'],'uac-target-unconfirmed')
        require(result['width']==capture['width'] and result['height']==capture['height'],'uac-capture-size')
        self.last['uac-ocr']=result
        return {'x':round(result['x']),'y':round(result['y']),'captureId':capture['captureId']}

    def security_scene(self,ctx,deadline):
        rule,_,owner=self.backend.binding('pc-a','screen')
        window=self.dtd.window('pc-a',rule,owner,ctx,deadline)
        path=ctx.run_dir/'security-screen.png'
        capture=self.dtd.capture('pc-a',window,path,ctx,deadline)
        return self.desktop.launch_probe('pc-a','ocr-security',ctx,deadline,image=str(path),imageSha256=capture['sha256'])

    def operator_ready(self,ctx,deadline):
        # A designated human/VM operator is an explicit external requirement,
        # bound to this run before any secure desktop action. No credentials.
        record=read_json(get(self.config,'recovery.operatorEvidence'))
        require(record.get('runId')==ctx.run_id and record.get('caseId')==ctx.case_id and
                record.get('operator')==get(self.config,'recovery.operator') and
                record.get('state')=='READY' and record.get('consoleAvailable') is True,'operator-not-ready')
        self.last['operator-ready']=record
        return record

    def operator_return(self,ctx,deadline):
        require('operator-ready' in self.last,'operator-not-prearmed')
        while ctx.clock()<deadline:
            record=read_json(get(self.config,'recovery.operatorEvidence'))
            if record.get('state')=='RETURNED':
                require(record.get('runId')==ctx.run_id and record.get('operator')==get(self.config,'recovery.operator'),'operator-return-owner')
                actual=self.desktop.snapshot('pc-b',ctx,deadline)
                baseline=self.baselines['pc-b']
                require(actual['sid']==baseline['sid'] and actual['session']==baseline['session'] and
                        actual['activeSessionId']==baseline['session'] and actual['desktop']=='Default','operator-return-desktop')
                self.last['operator-return']={'returned':True,'designatedOperator':True,'actual':actual}
                return self.last['operator-return']
            time.sleep(min(.1,ctx.remaining(deadline)))
        raise CaseError('operator-return-timeout','same-user login not witnessed')

    def no_resume(self,ctx,deadline,milliseconds):
        require(milliseconds==5000,'fixed-observation-window')
        from e2e_case_sources import observation_window
        return observation_window(self,ctx,deadline,'negativeWindow')

    def guard_identity(self,ctx,deadline):
        # Before runtime startup the driver/baseline and C11 private guard are
        # governed by their own finite requests. Product phases require both
        # independent harness watchdog readiness records and no error record.
        require(not (ctx.run_dir/'finish.json').exists(),'external-stop-requested')
        if not self.runtime_started:return
        self.wait_local('local-watchdog.json',ctx,deadline)
        status=self.harness_data('status',ctx,deadline)
        require(not status.get('product-error.json') and not status.get('product-watchdog-error.json') and not status.get('product-recovery-required.json'),'product-guard-failed')
        require(status.get('_watchdogAlive') is True,'product-watchdog-missing')
        wd=read_json(ctx.run_dir/'local-watchdog.json')
        identity={'pid':wd['pid'],'start':wd['start'],'image':self.campaign.start_local(deadline)['executable']}
        from e2e_case_process import alive
        require(alive(identity),'local-watchdog-lost')

    def stop_runtimes(self,ctx,deadline):
        if not self.runtime_started:return {'status':'completed'}
        while ctx.clock()<deadline:
            local=ctx.run_dir/'local-cleanup.json'
            status=self.harness_data('status',ctx,deadline)
            if local.exists() and status.get('product-cleanup.json'):
                self.raw['cleanup']={'a':read_json(local),'b':status['product-cleanup.json']}
                return {'status':'completed'}
            time.sleep(min(.05,ctx.remaining(deadline)))
        raise CaseError('runtime-cleanup-timeout','owned runtime cleanup incomplete')

    def restore_case_state(self,ctx,deadline):
        if ctx.case_id=='C11':
            self.desktop.signal('clipboard-restore',ctx,deadline)
            if self.desktop.clipboard is not None:self.restore_clipboard(ctx,deadline)
        self.desktop.signal('target-finish',ctx,deadline)
        self.desktop.signal('fault-stop',ctx,deadline)
        blur=ctx.run_dir/'blur-fixture-owner.json'
        if blur.exists():self.dtd.request('pc-a','terminate-owned',{'processIds':[read_json(blur)['processId']]},ctx,deadline,key='cleanup:blur-fixture')
        return {'status':'completed'}

    def independent_recovery(self,ctx,deadline):
        remote=self.harness_data('collect',ctx,deadline)
        require(remote.get('id')==ctx.run_id and remote.get('resourcesAbsent') is True,'remote-resources-remain')
        proof={}
        for machine in ('pc-a','pc-b'):
            if machine=='pc-b' and ctx.case_id in ('C04','C05','C06'):
                status=get(self.dtd.status(machine,ctx,deadline),'data')
                if status.get('desktop')!='Default' or status.get('inputAvailable') is not True:
                    # The designated operator watches external-recovery-request;
                    # no password entry or automatic consent is implemented.
                    if 'operator-ready' not in self.last:
                        record=read_json(get(self.config,'recovery.operatorEvidence'))
                        require(record.get('runId')==ctx.run_id and record.get('operator')==get(self.config,'recovery.operator'),'recovery-operator-owner')
                        self.last['operator-ready']=record
                    self.operator_return(ctx,deadline)
            data=self.desktop.snapshot(machine,ctx,deadline);baseline=self.baselines[machine]
            require(data['desktop']=='Default' and data['sid']==baseline['sid'] and data['session']==baseline['session'] and
                    data['activeSessionId']==baseline['session'] and data['heldVirtualKeys']==[],'independent-return-failed')
            proof[machine]=data
        local=self.raw['cleanup']['a']
        require(local['id']==ctx.run_id and local['ownedProcesses']==[] and local['listeners']==0 and not local['errors'],'local-resources-remain')
        owners=read_json(ctx.run_dir/'local-owned.json')['processes']
        if 'viewer-owner.json' in [p.name for p in ctx.run_dir.iterdir()]:
            viewer=self.backend.owners['pc-a']['viewer'];owners.append(viewer)
        current=self.desktop.launch_probe('pc-a','inspect-owned',ctx,deadline,owners=owners)
        require(current['resourcesAbsent'] is True,'independent-owned-processes-remain')
        self.last['recovery']=proof
        ctx.screen_occupied_seconds=(ctx.clock()-ctx.origin)/1e9
        return {'status':'completed','resourcesAbsent':True,'verified':True}

    def retire(self,ctx,deadline):
        # Obtain a fresh B proof immediately before retirement; old cleanup JSON
        # is never treated as independent current desktop recovery.
        b=self.desktop.snapshot('pc-b',ctx,deadline)
        self.desktop.bridge('retire',ctx,deadline,name=b['_sourceName'])
        proof=ctx.run_dir/'local-recovery-verified.json'
        if not proof.exists():new_json(proof,{'id':ctx.run_id,'tick':ctx.clock(),'recovery':self.last['recovery']})
        from e2e_case_process import alive
        wd=read_json(ctx.run_dir/'local-watchdog.json')
        identity={'pid':wd['pid'],'start':wd['start'],'image':self.campaign.start_local(deadline)['executable']}
        while alive(identity):time.sleep(min(.05,ctx.remaining(deadline)))
        self.desktop.stop_probes(ctx,deadline)
        for machine in ('pc-a','pc-b'):self.dtd.close(machine,ctx,deadline)
        return {'status':'completed'}

    def materialize(self,files,ctx,deadline):
        for name in files:
            path=ctx.run_dir/name
            if path.exists():continue
            if name.endswith('.png'):
                rule,_,owner=self.backend.binding('pc-a','screen')
                window=self.dtd.window('pc-a',rule,owner,ctx,deadline)
                self.dtd.capture('pc-a',window,path,ctx,deadline)
            elif name in ('audit.ndjson','service-status.ndjson'):
                rows=self.raw.get('product',{}).get(name)
                require(rows is not None,'required-product-evidence-missing',name)
                with path.open('xb') as f:
                    for row in rows:f.write(json.dumps(row,ensure_ascii=False).encode('utf-8')+b'\n')
            elif name.endswith('.ndjson'):
                rows=self.raw.get('browser-events',[])+self.raw.get('product',{}).get('trace',[])
                require(bool(rows),'required-event-evidence-missing',name)
                with path.open('xb') as f:
                    for row in rows:f.write(json.dumps(row,ensure_ascii=False).encode('utf-8')+b'\n')
            else:
                require('observation' in self.last,'observation-evidence-missing')
                new_json(path,self.last['observation'])
