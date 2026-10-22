"""Concrete DTD, file, audit and existing-harness adapters.

No connection is made on import. Live entrypoints require frozen preparation
evidence and the explicit Run --execute action.
"""
import base64
import json
import math
from pathlib import Path
import subprocess
import sys
import time
import uuid

from e2e_case_runtime import (CaseError, ROOT, LIMIT, require, get, digest, read_json,
                            new_json, safe_path, select_unique)


class Dtd:
    def __init__(self, exe, config, transport):
        self.exe, self.config, self.transport = exe, config, transport
        self.sessions, self.windows = {}, {}

    def cli(self, args, ctx, deadline):
        ctx.tool_call('dtd')
        return self.transport(self.exe, ['--config',self.config,*args],
                              ctx.remaining(deadline), ctx)

    def status(self, machine, ctx, deadline):
        require(machine in ('pc-a','pc-b'), 'machine')
        reply = self.cli(['--status','--machine',machine],ctx,deadline)
        require(get(reply,'status')=='completed', 'dtd-status-failed')
        return reply

    def open(self, machine, ctx, deadline):
        require(machine in ('pc-a','pc-b') and machine not in self.sessions, 'dtd-session-replay')
        ctx.resource('dtd:'+machine,'intent')
        result = ctx.once('session-open:'+machine, lambda _: self.cli(
            ['--session-open','--machine',machine,'--mode','control','--idle-seconds','900'],ctx,deadline))
        sid = get(result,'controlSessionId')
        # Record immediately so cleanup can close a session even if identity validation fails.
        self.sessions[machine] = sid
        new_json(ctx.run_dir/('dtd-'+machine+'.json'), {'runId':ctx.run_id,'controlSessionId':sid})
        ctx.resource('dtd:'+machine,'acquired','dtd-'+machine+'.json')
        return result

    def request(self, machine, operation, arguments, ctx, deadline, mutation=True, key=None):
        require(machine in self.sessions,'dtd-session-missing')
        if mutation and not ctx.stopping:require(not (ctx.run_dir/'finish.json').exists(),'external-stop-requested')
        if mutation and operation in ('focus','click','type-text','chord','key','drag','wheel'):ctx.screen_start()
        def send(request_id):
            envelope = {'apiVersion':1,'requestId':request_id,'controlSessionId':self.sessions[machine],
                        'operation':operation,'timeoutMilliseconds':max(1,math.floor(ctx.remaining(deadline)*1000)),
                        'arguments':arguments}
            request_path = ctx.run_dir/('request-'+request_id+'.json')
            new_json(request_path,envelope)
            try:
                try:
                    response = self.cli(['--request',str(request_path)],ctx,deadline)
                except CaseError as e:
                    if e.code!='completion-unknown':raise
                    response = self.cli(['--request-status',self.sessions[machine],request_id],ctx,deadline)
                require(response.get('requestId')==request_id and
                        response.get('controlSessionId')==self.sessions[machine],'dtd-response-binding')
                if response.get('status') in ('running','completionUnknown'):
                    # Query ORIGINAL request once. Never submit the mutation again.
                    response = self.cli(['--request-status',self.sessions[machine],request_id],ctx,deadline)
                    require(response.get('requestId')==request_id and
                            response.get('controlSessionId')==self.sessions[machine],'dtd-response-binding')
                require(response.get('status')=='completed','dtd-not-completed')
                return response
            finally:
                # Request text can contain pairing or synthetic input. It is not evidence.
                request_path.unlink(missing_ok=True)
        if mutation:
            action_key=key or ctx.active_step+':'+machine+':'+operation
            if ctx.stopping and action_key in ctx.consumed:
                original=[r['requestId'] for r in ctx.ledger if r['operation']==action_key and r['status']=='intent']
                require(len(original)==1,'cleanup-original-request-unknown')
                reply=self.cli(['--request-status',self.sessions[machine],original[0]],ctx,deadline)
                require(reply.get('requestId')==original[0] and reply.get('controlSessionId')==self.sessions[machine] and
                        reply.get('status')=='completed','cleanup-original-request-unverified')
                return reply
            return ctx.once(action_key,send)
        return send(uuid.uuid4().hex)

    def window(self, machine, rule, owner, ctx, deadline):
        while True:
            data = self.request(machine,'windows',{},ctx,deadline,False)
            rows=get(data,'data.windows')
            if any(all(x.get(k)==v for k,v in rule.items()) for x in rows):return select_unique(rows,rule,owner)
            time.sleep(min(.05,ctx.remaining(deadline)))

    def element(self, machine, window, rule, ctx, deadline):
        while True:
            reply = self.request(machine,'controls',{'windowId':get(window,'windowId')},ctx,deadline,False)
            rows=get(reply,'data.elements')
            if any(all(x.get(k)==v for k,v in rule.items()) for x in rows):return select_unique(rows,rule)
            time.sleep(min(.05,ctx.remaining(deadline)))

    def capture(self,machine,window,output,ctx,deadline):
        path=safe_path(str(output),ctx.run_dir)
        require(not path.exists(),'capture-output-replay')
        result=self.request(machine,'capture',{'windowId':window['windowId'],'output':str(path)},ctx,deadline,False)
        data=get(result,'data')
        require(get(data,'windowId')==window['windowId'] and get(data,'sha256')==digest(path) and
                get(data,'bytes')==path.stat().st_size,'capture-integrity')
        return data

    def focus(self, machine, window, rule, owner, ctx, deadline, key):
        self.request(machine,'focus',{'windowId':window['windowId']},ctx,deadline,key=key)
        current = self.window(machine,rule,owner,ctx,deadline)
        require(current['windowId']==window['windowId'] and get(current,'foreground') is True,
                'foreground-not-confirmed')

    def click(self, machine, window_rule, element_rule, owner, ctx, deadline, key):
        window = self.window(machine,window_rule,owner,ctx,deadline)
        self.focus(machine,window,window_rule,owner,ctx,deadline,key+':focus')
        element = self.element(machine,window,element_rule,ctx,deadline)
        return self.request(machine,'click',{'windowId':window['windowId'],
                            'elementId':get(element,'elementId')},ctx,deadline,key=key)

    def type_text(self, machine, window_rule, element_rule, owner, value, ctx, deadline, key, replace=False, empty_confirmed=False):
        require(1 <= len(value.encode('utf-16-le'))//2 <= 4096,'text-length')
        self.click(machine,window_rule,element_rule,owner,ctx,deadline,key+':focus-field')
        window = self.window(machine,window_rule,owner,ctx,deadline)
        # Replacement is explicitly allowed only for fixed form/path fields.
        element = self.element(machine,window,element_rule,ctx,deadline)
        available=get(element,'value.available') is True
        require(available or empty_confirmed,'field-value-unavailable')
        if available and get(element,'value.text') and replace:
            self.request(machine,'chord',{'windowId':window['windowId'],'keys':['CTRL','A']},ctx,deadline,key=key+':select-all')
            self.request(machine,'key',{'windowId':window['windowId'],'key':'BACKSPACE','action':'press'},ctx,deadline,key=key+':clear')
            element=self.element(machine,window,element_rule,ctx,deadline)
        require((get(element,'value.available') is True and get(element,'value.text')=='') or empty_confirmed,'field-not-empty')
        return self.request(machine,'type-text',{'windowId':window['windowId'],'text':value},ctx,deadline,key=key)

    def release(self, machine, ctx, deadline, cleanup=False):
        result = self.request(machine,'release-input',{},ctx,deadline,key=('cleanup' if cleanup else ctx.active_step)+':release:'+machine)
        require(get(result,'inputReleased') is True,'dtd-input-not-released')
        return result

    def close(self, machine, ctx, deadline):
        if machine not in self.sessions:return {'status':'completed'}
        result = ctx.once('cleanup:close:'+machine,lambda _:self.cli(
            ['--session-close',self.sessions[machine]],ctx,deadline))
        require(get(result,'inputReleased') is True and get(result,'data.applicationsStopped') is True,
                'dtd-session-not-closed')
        ctx.resource('dtd:'+machine,'released')
        return result


def map_video_point(point, monitor, viewport, capture):
    """Map B physical coordinates into A video client pixels with letterboxing."""
    for obj in (monitor,viewport):
        require(all(type(obj.get(k)) in (int,float) and math.isfinite(obj[k]) for k in ('x','y','width','height')),
                'geometry-fields')
        require(obj['width']>0 and obj['height']>0,'geometry-size')
    require(capture.get('valid') is True and bool(capture.get('captureId')),'stale-capture')
    require(isinstance(point,(tuple,list)) and len(point)==2 and
            all(type(v) in (int,float) and math.isfinite(v) for v in point),'point-coordinate')
    x,y = point
    require(monitor['x'] <= x < monitor['x']+monitor['width'] and
            monitor['y'] <= y < monitor['y']+monitor['height'],'point-outside-monitor')
    scale = min(viewport['width']/monitor['width'],viewport['height']/monitor['height'])
    px = viewport['x']+(viewport['width']-monitor['width']*scale)/2+(x-monitor['x'])*scale
    py = viewport['y']+(viewport['height']-monitor['height']*scale)/2+(y-monitor['y'])*scale
    return {'x':round(px),'y':round(py),'captureId':capture['captureId']}


def ocr_point(result,capture,viewport):
    x,y=result['x'],result['y']
    require(all(type(v) in (int,float) and math.isfinite(v) for v in (x,y)),'ocr-coordinate')
    require(0<=x<capture['width'] and 0<=y<capture['height'] and
            viewport['x']<=x<viewport['x']+viewport['width'] and viewport['y']<=y<viewport['y']+viewport['height'],'ocr-outside-video')
    return {'x':round(x),'y':round(y),'captureId':capture['captureId']}

def inspect_file(directory, filename, owner_root):
    folder = safe_path(directory,owner_root)
    require(Path(filename).name==filename,'filename')
    files = list(folder.iterdir())
    require(len(files)<=64,'file-inventory-bound')
    for p in files:
        safe_path(str(p),owner_root,exists=True)
    final = folder/filename
    require(final.is_file(),'final-file-missing')
    return {'length':final.stat().st_size,'sha256':digest(final),
            'finalCount':sum(p.name==filename for p in files),
            'partialCount':sum(p.name!=filename for p in files)}


def read_audit(path, session, connection, generation, operation_id=None):
    p = safe_path(path,exists=True)
    require(p.stat().st_size<=LIMIT,'audit-size-bound')
    rows=[]
    for line in p.read_text(encoding='utf-8-sig').splitlines():
        row=json.loads(line)
        if (row.get('SessionId'),row.get('ConnectionId')) != (session,connection):
            continue
        require(row.get('PermissionGeneration') == (0 if row.get('Kind') in (2,3) else generation),
                'audit-generation')
        if row.get('Kind') in (7,8,9,10) and get(row,'Operation.Id') != operation_id:
            continue
        rows.append(row)
    return {'rows':rows,'SessionId':session,'ConnectionId':connection,
            'PermissionGeneration':generation,'operationId':operation_id}


def collect_artifact(path, ctx, source):
    p=safe_path(path,ctx.run_dir,exists=True)
    require(p.stat().st_size<=(16*LIMIT if p.suffix=='.png' else LIMIT),'evidence-size-bound')
    record={'relativePath':p.relative_to(ctx.run_dir).as_posix(),'sha256':digest(p),
            'bytes':p.stat().st_size,'source':source}
    ctx.evidence.append(record)
    return record


class Harness:
    """Reuse frozen-manifest transport, never the blocked legacy Arm path."""
    def __init__(self, manifest, transport):
        self.path=safe_path(manifest,ROOT/'.artifacts',True)
        self.manifest=read_json(self.path)
        self.hash=digest(self.path)
        self.transport=transport

    def call(self, action, ctx, deadline):
        require(action in ('status','finish','collect','evidence'),'harness-action')
        require(digest(self.path)==self.hash and get(self.manifest,'id')==ctx.run_id,'manifest-owner')
        require(Path(get(self.manifest,'localRun')).resolve()==ctx.run_dir.resolve(),'manifest-directory')
        if action=='finish' and not (ctx.run_dir/'finish.json').exists():
            new_json(ctx.run_dir/'finish.json',{'id':ctx.run_id,'reason':'case-stop'})
        args=[str(ROOT/'scripts/e2e-product-client.py'),action,'--campaign',str(self.path)]
        if ctx.stopping:args.append('--recovery-only')
        return self.transport(sys.executable,args,ctx.remaining(deadline,10),ctx)


class OwnedGateway:
    """All descendants are contained in existing E2eOwnedCommand's Windows Job."""
    def __call__(self, exe, arguments, seconds, ctx):
        require(0 < seconds <= (180 if ctx.origin is None else 10),'gateway-phase-budget')
        metric=ctx.tool_call('gateway');started=ctx.clock()
        # PowerShell startup/Add-Type/Job disposal share the caller's budget.
        # The old seconds+2 outer wait silently extended every phase boundary.
        child_seconds=seconds-min(2,seconds/2)
        request_id=uuid.uuid4().hex
        request=ctx.run_dir/('command-'+request_id+'.json')
        result=ctx.run_dir/('command-result-'+request_id+'.json')
        new_json(request,{'executable':str(safe_path(exe,exists=True)), 'arguments':arguments,
                          'timeoutSeconds':child_seconds,'output':str(result)})
        shell=str(Path(__import__('os').environ['SystemRoot'])/'System32/WindowsPowerShell/v1.0/powershell.exe')
        try:
            # The outer process owns the Job handle; terminating it closes the Job.
            completed=subprocess.run([shell,'-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass',
                '-File',str(ROOT/'scripts/e2e-case-command.ps1'),'-Request',str(request),
                '-Python',sys.executable,'-Milliseconds',str(max(1,math.floor(child_seconds*1000)))],
                stdin=subprocess.DEVNULL,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,
                timeout=seconds,creationflags=subprocess.CREATE_NO_WINDOW)
            require(completed.returncode==0,'owned-command-failed')
            record=read_json(result)
            response=json.loads(base64.b64decode(record['stdout']).decode('utf-8-sig'))
            code=record.get('exitCode')
            require(code==0 or (code==2 and response.get('status')=='completionUnknown') or
                    (code==3 and response.get('status')=='running'),'child-command-failed')
            return response
        except subprocess.TimeoutExpired as e:
            raise CaseError('completion-unknown','owned gateway timeout') from e
        finally:
            elapsed=(ctx.clock()-started)/1e9
            metric['observedSeconds']+=elapsed;metric['maximumSeconds']=max(metric['maximumSeconds'],elapsed)
            request.unlink(missing_ok=True)
            result.unlink(missing_ok=True)


class CaseBackend:
    """Closed action dispatcher with no configurable command/callback execution.

    uiRules are selectors only, never executable commands. owners and geometry
    must come from runtime observations, never future PID/HWND values in config.
    Bootstrap, observation and recovery use the code-owned LiveObserver.
    """
    def __init__(self, config, dtd, harness, observer):
        self.config, self.dtd, self.harness, self.observer = config,dtd,harness,observer
        self.owners={}
        self.geometry={}
        self.private_pairing=None

    def binding(self,machine,target):
        require(machine in ('pc-a','pc-b'),'machine')
        rule=get(self.config,'uiRules.'+machine+'.'+target)
        owner=get(self.owners,machine+'.'+get(rule,'ownerRole'))
        return get(rule,'window'),get(rule,'element'),owner

    def ui_click(self,machine,target,ctx,deadline,key):
        window,element,owner=self.binding(machine,target)
        return self.dtd.click(machine,window,element,owner,ctx,deadline,key)

    def ui_text(self,machine,target,value,ctx,deadline,key):
        window,element,owner=self.binding(machine,target)
        return self.dtd.type_text(machine,window,element,owner,value,ctx,deadline,key,
            replace=target in ('name','file-dialog-path','receive-dialog-path'),
            empty_confirmed=(target=='pin' and self.observer.browser(ctx).get('pinEmpty') is True))

    def video_window(self,ctx,deadline,key):
        rule,_,owner=self.binding('pc-a','screen')
        window=self.dtd.window('pc-a',rule,owner,ctx,deadline)
        self.dtd.focus('pc-a',window,rule,owner,ctx,deadline,key+':focus')
        return window

    def read_field(self,machine,target,ctx,deadline):
        rule,element_rule,owner=self.binding(machine,target)
        window=self.dtd.window(machine,rule,owner,ctx,deadline)
        element=self.dtd.element(machine,window,element_rule,ctx,deadline)
        value = (get(element,'value.text') if get(element,'value.available') is True else
                 get(element,'name') if target in ('PairingCode','PairingPin') else None)
        require(isinstance(value,str),'private-field-type')
        return value

    def issue_pairing(self,ctx,deadline):
        require(self.private_pairing is None,'pairing-replay')
        self.ui_click('pc-b','HostStart',ctx,deadline,ctx.active_step+':host-start')
        import re
        while True:
            code=self.read_field('pc-b','PairingCode',ctx,deadline).replace('-','')
            pin=self.read_field('pc-b','PairingPin',ctx,deadline).removeprefix('PIN ')
            if re.fullmatch('[0-9]{9}',code) and re.fullmatch('[0-9]{4}',pin):break
            time.sleep(min(.05,ctx.remaining(deadline)))
        self.private_pairing=(code,pin)
        self.observer.last['pairing-valid']=True
        self.observer.last['host-start-count']=self.observer.last.get('host-start-count',0)+1

    def join_viewer(self,ctx,deadline):
        require(self.private_pairing is not None,'pairing-missing')
        code,pin=self.private_pairing
        try:
            for target,value in (('name','RA-'+ctx.case_id+'-'+ctx.run_id[:8]),('code',code),('pin',pin)):
                self.ui_text('pc-a',target,value,ctx,deadline,ctx.active_step+':join:'+target)
            self.ui_click('pc-a','join-submit',ctx,deadline,ctx.active_step+':join-submit')
            self.observer.after_join(ctx,deadline)
        finally:
            self.private_pairing=None

    def grant(self,mask,ctx,deadline):
        from e2e_case_runtime import MASKS
        require(mask==MASKS[int(ctx.case_id[1:])-1],'fixed-consent-mask')
        require(mask!=0,'deny-case-must-not-approve')
        controls=((1,'ConsentScreen'),(2,'ConsentControl'),(4,'ConsentAdmin'),
                  (8,'consent-file'),(16,'consent-clipboard'),(32,'consent-diagnostics'),
                  (64,'consent-runbook'),(128,'consent-chat'))
        # API1 lacks ToggleState; the owned UIA helper observes the actual states.
        # No click can occur until all eight actual states have been observed.
        states=self.observer.consent_states(ctx,deadline)
        require(set(states)=={name for _,name in controls} and all(type(v) is bool for v in states.values()),
                'consent-toggle-observation-missing')
        for bit,name in controls:
            expected=bool(mask&bit)
            if states[name]!=expected:
                self.ui_click('pc-b',name,ctx,deadline,ctx.active_step+':toggle:'+name)
        after=self.observer.consent_states(ctx,deadline)
        require(after=={name:bool(mask&bit) for bit,name in controls},'consent-toggle-mismatch')
        self.ui_click('pc-b','ConsentApprove',ctx,deadline,ctx.active_step+':consent-approve')
        self.observer.after_grant(ctx,deadline)

    def approve_admin(self,ctx,deadline,request):
        from e2e_case_runtime import assert_fields
        if request:
            self.ui_click('pc-a','admin-request',ctx,deadline,ctx.active_step+':admin-request')
        state=self.observer.admin_consent(ctx,deadline)
        assert_fields(state,{'pending':True,'inputEnabled':False,'productHeld':0,'ownerMatched':True})
        require(type(get(state,'scopeChecked')) is bool,'admin-scope-unavailable')
        if not state['scopeChecked']:
            self.ui_click('pc-b','AdminConsentScope',ctx,deadline,ctx.active_step+':admin-scope')
        checked=self.observer.admin_consent(ctx,deadline)
        assert_fields(checked,{'scopeChecked':True,'pending':True,'ownerMatched':True})
        self.ui_click('pc-b','AdminConsentApprove',ctx,deadline,ctx.active_step+':admin-approve')

    def action(self,action,ctx,deadline):
        require(not ctx.stopping,'feature-action-during-recovery',exit_code=40)
        require(ctx.case_id!='C15','turn-excluded-20261022',exit_code=10)
        op,*args=action
        if ctx.case_id=='C11' and op not in ('capture-clipboard-baseline','restore-clipboard-baseline'):
            baseline=self.observer.last.get('clipboard-baseline',{})
            require(baseline.get('captured') is True and baseline.get('restorable') is True and
                    baseline.get('runId')==ctx.run_id,'clipboard-baseline-not-ready')
        key=ctx.active_step+':'+op+':'+':'.join(str(x) for x in args[:2])
        if op in ('start-runtimes','start-relay-runtimes'):
            return self.observer.start_runtimes(ctx,deadline,relay=op=='start-relay-runtimes')
        if op=='capture-clipboard-baseline':return self.observer.capture_clipboard(ctx,deadline)
        if op=='restore-clipboard-baseline':return self.observer.restore_clipboard(ctx,deadline)
        if op=='trigger-uac-once':return self.observer.trigger_uac(ctx,deadline)
        if op=='arm-owned-fault':return self.observer.arm_fault(ctx,deadline)
        if op=='issue-pairing':return self.issue_pairing(ctx,deadline)
        if op=='join-viewer':return self.join_viewer(ctx,deadline)
        if op=='grant':return self.grant(args[0],ctx,deadline)
        if op=='open-session':
            # The runtime bootstrap must bind actual owners before pairing/UI.
            self.action(('start-runtimes',),ctx,deadline)
            if args[1]!='none':self.observer.start_owned_fixture(args[1],ctx,deadline)
            self.issue_pairing(ctx,deadline)
            self.join_viewer(ctx,deadline)
            if args[0]:self.grant(args[0],ctx,deadline)
            return
        if op in ('approve-admin-local','approve-admin-session'):
            return self.approve_admin(ctx,deadline,op=='approve-admin-session')
        if op=='select-book':
            require(args==['network-summary','summary'],'fixed-book')
            for target in ('runbook-choice','runbook-network-summary','runbook-detail','runbook-summary'):
                self.ui_click('pc-a',target,ctx,deadline,key+':'+target)
            return
        if op=='click':
            if args==['pc-a','admin-sas']:self.observer.operator_ready(ctx,deadline)
            if args==['pc-b','ConsentDeny']:self.observer.last['negative-start-index']=len(self.observer.events(ctx))
            result=self.ui_click(args[0],args[1],ctx,deadline,key)
            if args[1]=='file-deny':self.observer.last['file-deny-count']=self.observer.last.get('file-deny-count',0)+1
            return result
        if op=='text':
            return self.ui_text(*args,ctx,deadline,key)
        if op=='host-stop':
            return self.ui_click('pc-b','HostStop',ctx,deadline,key)
        if op=='release-input':
            return self.dtd.release(args[0],ctx,deadline)
        if op=='focus':
            machine,target=args
            if target=='blur-window':self.observer.last['blur-sequence']=max((r['seq'] for r in self.observer.events(ctx)),default=0)+1
            rule,_,owner=self.binding(machine,target)
            window=self.dtd.window(machine,rule,owner,ctx,deadline)
            return self.dtd.focus(machine,window,rule,owner,ctx,deadline,key)
        if op in ('video-key','video-key-pair'):
            window=self.video_window(ctx,deadline,key)
            require(args[0] in ('F12','ESC'),'case-key')
            # One Agent key press owns both native transitions. CLI/PowerShell
            # startup is not the native 1500ms held-key lease/SLA clock.
            transitions=('press',) if op=='video-key-pair' else (args[1],)
            for transition in transitions:
                require(transition in ('press','down','up'),'key-transition')
                self.dtd.request('pc-a','key',{'windowId':window['windowId'],'key':args[0],
                                 'action':transition},ctx,deadline,key=key+':'+transition)
            if args[0]=='ESC':self.observer.last['viewer-cancel-count']=self.observer.last.get('viewer-cancel-count',0)+1
            return
        if op in ('video-click','video-wheel','video-drag'):
            window=self.video_window(ctx,deadline,key)
            if op=='video-click' and args==['uac-cancel']:
                position=self.observer.uac_cancel_position(window,ctx,deadline)
                result=self.dtd.request('pc-a','click',{'windowId':window['windowId'],'position':position,'button':'left'},ctx,deadline,key=key)
                self.observer.last['viewer-cancel-count']=self.observer.last.get('viewer-cancel-count',0)+1
                return result
            geometry=self.observer.video_geometry(ctx,deadline)
            require(get(geometry,'windowId')==window['windowId'],'video-window-changed')
            capture=get(geometry,'capture')
            require(ctx.clock()<=get(geometry,'validUntilTick'),'video-geometry-expired')
            def point(value):
                return map_video_point(value,get(geometry,'monitor'),get(geometry,'viewport'),capture)
            if op=='video-click':
                pos=point(get(geometry,'targets.'+args[0]))
                operation='click';arguments={'windowId':window['windowId'],'position':pos,'button':'left'}
            elif op=='video-wheel':
                require(args==[120],'wheel-delta')
                operation='wheel';arguments={'windowId':window['windowId'],'position':point(get(geometry,'wheelPoint')),'delta':120}
            else:
                drag=get(geometry,'drag')
                self.observer.last['drag-start-tick']=get(geometry,'fixtureTick')
                a,b=point(get(drag,'start')),point(get(drag,'end'))
                operation='drag';arguments={'windowId':window['windowId'],'captureId':capture['captureId'],
                    'start':{'x':a['x'],'y':a['y']},'end':{'x':b['x'],'y':b['y']},'button':'left','steps':get(drag,'steps')}
            return self.dtd.request('pc-a',operation,arguments,ctx,deadline,key=key)
        if op=='lock-local-once':
            require(ctx.case_id=='C06','lock-case')
            self.observer.operator_ready(ctx,deadline)
            self.observer.last['negative-start-index']=len(self.observer.events(ctx))
            rule,_,owner=self.binding('pc-b','HostStop')
            window=self.dtd.window('pc-b',rule,owner,ctx,deadline)
            self.dtd.focus('pc-b',window,rule,owner,ctx,deadline,key+':focus')
            result=self.dtd.request('pc-b','chord',{'windowId':window['windowId'],'keys':['WIN','L']},ctx,deadline,key=key)
            self.observer.last['lock-count']=self.observer.last.get('lock-count',0)+1
            return result
        if op=='choose-file':
            machine,name=args
            require(name in ('ra-c07.bin','ra-c08.bin','ra-c09.bin'),'fixed-file-name')
            path=get(self.config,'caseInputs.sourcePath')
            require(Path(path).name==name,'source-file-name')
            self.ui_click(machine,'file-source' if machine=='pc-a' else 'file-send-local',ctx,deadline,key+':open')
            self.ui_text(machine,'file-dialog-path',path,ctx,deadline,key+':path')
            result=self.ui_click(machine,'file-dialog-approve',ctx,deadline,key+':approve')
            self.observer.last['file-selection-count']=self.observer.last.get('file-selection-count',0)+1
            return result
        if op=='approve-receive':
            machine=args[0]
            if machine=='pc-a':self.ui_click(machine,'file-accept',ctx,deadline,key+':open')
            self.ui_text(machine,'receive-dialog-path',get(self.config,'caseInputs.receivePath'),ctx,deadline,key+':path')
            selected=self.read_field(machine,'receive-dialog-path',ctx,deadline)
            expected=get(self.config,'caseInputs.receivePath')
            require(selected.casefold().replace('/','\\')==expected.casefold().replace('/','\\'),'receive-path-mismatch')
            import hashlib
            self.observer.last['receive-path-confirmed']=True
            self.observer.last['receive-path-hash']=hashlib.sha256(expected.replace('/','\\').upper().encode('utf-8')).hexdigest()
            result=self.ui_click(machine,'receive-dialog-approve',ctx,deadline,key+':approve')
            self.observer.last['receive-approve-count']=self.observer.last.get('receive-approve-count',0)+1
            return result
        if op=='approve-book-local':
            from e2e_case_steps import BOOK, same, E
            data=self.observer.book_consent(ctx,deadline)
            BOOK(data);same('installedHash','dialogHash')(data);E(workerBeforeCount=0)(data)
            result=self.ui_click('pc-b','runbook-approve',ctx,deadline,key)
            self.observer.last['book-approve-count']=self.observer.last.get('book-approve-count',0)+1
            return result
        if op=='observe-no-resume':
            return self.observer.no_resume(ctx,deadline,args[0])
        if op=='await-operator-login':
            return self.observer.operator_return(ctx,deadline)
        # No shell, eval, arbitrary callback, generic click or alternate scenario fallback.
        raise CaseError('unknown-case-action',op,40)

    def observe(self,source,ctx,deadline):
        return self.observer.read(source,ctx,deadline)

    def collect(self,files,ctx,deadline):
        self.observer.materialize(files,ctx,deadline)
        for name in files:
            ctx.remaining(deadline)
            require(Path(name).name==name,'artifact-name')
            collect_artifact(str(ctx.run_dir/name),ctx,'case-observer')

    def recover(self,operation,ctx,deadline):
        if operation=='close-owned-sessions':
            errors=[]
            for machine in tuple(self.dtd.sessions):
                if 'cleanup:close:'+machine in ctx.consumed:continue
                try:self.dtd.close(machine,ctx,deadline)
                except Exception:errors.append(machine)
            require(not errors,'session-close-incomplete')
            return {'status':'completed'}
        if operation=='finish-remote':
            self.harness.call('finish',ctx,deadline)
            return {'status':'completed'}
        if operation=='release-owned-input':
            failures=[]
            for machine in tuple(self.dtd.sessions):
                try:self.dtd.release(machine,ctx,deadline,cleanup=True)
                except Exception:failures.append(machine)
            require(not failures,'release-incomplete')
            return {'status':'completed'}
        if operation=='inspect-independent':
            return self.observer.independent_recovery(ctx,deadline)
        if operation=='retire-and-close':
            require(ctx.recovery.get('verified') is True,'recovery-not-confirmed')
            return self.observer.retire(ctx,deadline)
        if operation=='stop-owned-runtime':return self.observer.stop_runtimes(ctx,deadline)
        if operation=='restore-case-state':return self.observer.restore_case_state(ctx,deadline)
        raise CaseError('unknown-recovery-operation',operation)

    def begin(self,ctx,deadline):
        return self.observer.begin(self,ctx,deadline)

    def check_guards(self,ctx,deadline):
        return self.observer.guard_identity(ctx,deadline)
