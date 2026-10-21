"""Bounded live source assembly. Values come from observed records, never fixtures.

Only observation polling is allowed here. Feature actions are dispatched elsewhere.
"""
import hashlib
import re
import time
from pathlib import Path
from e2e_case_runtime import require,get,new_json,digest,CaseError,safe_path
from e2e_case_observations import derive,Pending,last


def window_rows(o,ctx,deadline):
    return get(o.dtd.request('pc-b','windows',{},ctx,deadline,False),'data.windows')


def text_field(o,target,ctx,deadline):
    # Only explicitly selected synthetic chat or installed Runbook UI is read.
    require(target in ('chat-history','runbook-summary-text'),'public-field-selector')
    return o.backend.read_field('pc-b',target,ctx,deadline)


def observation_window(o,ctx,deadline,key):
    if key in o.last:return o.last[key]
    started=ctx.clock();samples=[];first=len(o.events(ctx))
    if key=='negativeWindow':first=o.last.get('negative-start-index',first)
    while ctx.clock()-started<5_000_000_000:
        ctx.remaining(deadline);samples.append(o.browser(ctx))
        require(not samples[-1]['inputEnabled'],'input-enabled-in-negative-window')
        time.sleep(min(.05,ctx.remaining(deadline)))
    events=o.events(ctx)[first:]
    product=o.product(ctx,deadline)
    result={'framesMax':max(x['frames'] for x in samples),'observedWindowMs':5000,
        'nativeCount':sum(r['metadata']['count'] for r in product['trace'] if r['kind']=='native-input'),
        'newOffers':sum(r.get('message',{}).get('type')=='offer' for r in events),
        'newDataReady':sum(r.get('message',{}).get('t')=='data-ready' for r in events),
        'newInputPermissions':sum('control' in r.get('message',{}).get('permissions',[]) for r in events)}
    o.last[key]=result
    return result


def receive_files(o,ctx,product):
    root=ctx.run_dir/'receive'
    files=list(root.iterdir());require(len(files)<=64,'receive-file-bound')
    for path in files:
        safe_path(str(path),ctx.run_dir,True);require(path.stat().st_size<=1048576,'receive-size-bound')
    rows=[{'name':p.name,'size':p.stat().st_size,'sha256':digest(p)} for p in files]
    remote=get(product['product-inventory.json'],'receive')
    require(isinstance(remote,list),'receive-inventory')
    return rows,remote


def assemble(o,source,ctx,deadline):
    if ctx.observation_after and (ctx.run_dir/'browser-observation.json').stat().st_mtime_ns<ctx.observation_after:
        raise Pending('post-action-browser-snapshot')
    b=o.browser(ctx);events=o.events(ctx);p=o.product(ctx,deadline);trace=p['trace']
    messages=[r['message'] for r in events if 'message' in r]
    connection=[m['connectionId'] for m in messages if m.get('connectionId')]
    require(len(set(connection))<=1,'connection-changed')
    inventory=get(p['product-inventory.json'],'processes')
    def processes(suffix):return [r for r in inventory if r['image'].endswith(suffix)]
    s={'browser':b,'events':events,'trace':trace,'last':o.last,'context':ctx,
       'audit':p.get('audit.ndjson',[]),'service':p.get('service-status.ndjson',[]),
       'connectionId':connection[0] if connection else None,'baselineOS':o.baselines['pc-b'],
       'preConsent':o.last.get('preConsent'),'ownersMatched':bool(o.backend.owners.get('pc-b',{}).get('tray')),
       'helperPids':processes('RemoteAssist.Host.Helper.exe'),'adminPids':processes('RemoteAssist.Host.AdminHelper.exe'),
       'workerPids':processes('RemoteAssist.Host.RunbookWorker.exe'),
       'aReady':bool(o.raw.get('runtime')) and (ctx.run_dir/'viewer-ready.json').exists(),
       'bReady':bool(o.raw.get('runtime',{}).get('product-running.json'))}
    s['serviceIdentityMatched']=bool(processes('RemoteAssist.Host.Service.exe'))
    if source in ('runtime','ready'):
        o.guard_identity(ctx,deadline);s['guardsReady']=True
    if source=='pre-consent':s['preConsent']=observation_window(o,ctx,deadline,'preConsent')
    if (ctx.case_id=='C02' and source=='denied') or (ctx.case_id=='C06' and source=='end'):
        if 'negativeWindow' not in o.last:
            observation_window(o,ctx,deadline,'negativeWindow')
            return assemble(o,source,ctx,deadline)
        s['negativeWindow']=observation_window(o,ctx,deadline,'negativeWindow')
    if source in ('return','held','baseline','admin-pending','admin-active','admin','compare','telemetry') or (ctx.case_id=='C14' and source=='click'):
        # Secure desktop observations use the SYSTEM capture trace, never a
        # normal-desktop GetAsyncKeyState result masquerading as secure input.
        s['os']=o.desktop.snapshot('pc-b',ctx,deadline)
    if o.fixture is not None:
        f=o.desktop.await_file('pc-b','input-target.json',ctx,deadline)
        require(f['pid']==o.fixture['pid'] and f['observationValid'],'fixture-source-owner')
        s.update(fixture=f,fixtureBaseline=o.baselines['fixture'])
        if source=='drag':
            points=f['dragPoints'];geometry=o.last['geometry']['drag']
            near=lambda p,q:abs(p['x']-q[0])<=8 and abs(p['y']-q[1])<=8
            s['dragPathMatched']=len(points)>=2 and near(points[0],geometry['start']) and near(points[-1],geometry['end'])
            rows=[r for r in f['events'] if r['kind']=='button' and r['tick']>o.last['drag-start-tick']]
            s['dragTransitions']=[r['action'] for r in rows]
    if ctx.case_id in ('C01','C02','C13','C14','C15'):
        windows=window_rows(o,ctx,deadline);owner=o.backend.owners['pc-b']['tray']
        owned=[r for r in windows if all(r[k]==owner[k] for k in ('pid','imagePath','createdAt'))]
        s['indicatorActive']=all(any(r['title']==title for r in owned) for title in ('RemoteAssist 공유 표시','RemoteAssist 공유 테두리'))
        s['consentWindowPresent']=any(r['title']==get(o.config,'uiRules.pc-b.ConsentApprove.window.title') for r in owned)
        if ctx.case_id in ('C13','C14'):
            s['bookDialogPresent']=any(r['title']==get(o.config,'uiRules.pc-b.runbook-approve.window.title') for r in owned)
    if ctx.case_id in ('C04','C05','C06'):
        approvals=[r for r in trace if r['kind']=='admin-consent-ui']
        states=[m for m in messages if m.get('t')=='admin-state']
        s['adminLocalApprovalMatched']=len(approvals)==1 and approvals[0]['metadata']['approved'] and bool(states)
        s['cancelledByViewer']=o.last.get('viewer-cancel-count',0)==1
        if ctx.case_id=='C04' and source=='return':
            trigger=o.desktop.await_file('pc-b',o.last['uac-trigger']['output'],ctx,deadline)
            s['cancelledByViewer'] &= trigger['cancelled'] and not trigger['unexpectedApproval']
        if source=='uac':
            rule,_,owner=o.backend.binding('pc-a','screen')
            w=o.dtd.window('pc-a',rule,owner,ctx,deadline)
            o.uac_cancel_position(w,ctx,deadline)
            s['uacSceneMatched']=o.last['uac-ocr']['uacSceneMatched']
        if source=='screen':s['securityScene']=o.security_scene(ctx,deadline)
        if source=='locked':
            s['sessionStates']=o.desktop.await_file('pc-b','session-events.ndjson',ctx,deadline)
            locked=[r for r in s['sessionStates'] if r['locked']]
            if not locked:raise Pending('wts-lock')
            o.last['lock-tick']=locked[0]['tick']
        if ctx.case_id=='C04' and source=='end':
            # A closed mapping must be witnessed by the privileged producer's
            # disposal record; process absence alone cannot prove this.
            s['adminMappingValid']=last(trace,'admin-mapping-closed')['metadata']['mappingOpen']
    starts=[r for r in trace if r['kind']=='session-runtime-start']
    s['producerRestartCount']=max(0,len(starts)-1)
    if ctx.case_id=='C06' and 'lock-tick' in o.last:
        s['producerRestartCount']+=sum(r['kind']=='capture-start' and r['tick']>o.last['lock-tick'] for r in trace)
    if ctx.case_id in ('C07','C08','C09'):
        a,r=receive_files(o,ctx,p);s.update(aReceive={'empty':not a},bReceive={'empty':not r})
        sourcepath=ctx.run_dir/'inputs'/('ra-'+ctx.case_id.lower()+'.bin')
        if source=='source':
            s['sourceFile']={'name':sourcepath.name,'size':sourcepath.stat().st_size,'sha256':digest(sourcepath)}
            if ctx.case_id!='C08':require(b['fileSource']=={'name':sourcepath.name,'size':sourcepath.stat().st_size},'selected-file-mismatch')
            else:
                offer=last([m for m in messages if m.get('t')=='file-offer'],'file-offer','t')
                require(offer['name']==sourcepath.name and offer['hash']==digest(sourcepath),'selected-b-file-mismatch')
        offers=[m for m in messages if m.get('t')=='file-offer']
        if offers:s['operationId']=offers[-1]['id']
        dialogs=[r['metadata'] for r in trace if r['kind']=='file-dialog-open']
        if ctx.case_id=='C08' and offers:s['fileDialogId']=b['fileOfferId']
        elif dialogs:s['fileDialogId']=dialogs[-1]['id']
        accepts=[r for r in events if r.get('message',{}).get('t')=='file-accept']
        if accepts:s['acceptTick']=accepts[0]['tick']
        if source=='accept':
            decision=last(trace,'file-consent-ui')['metadata']
            s['fileAcceptedLocally']=decision['approved'];s['receivePathMatched']=decision['pathHash']==o.last['receive-path-hash']
        if source=='save':s['receivePathMatched']=o.last['receive-path-confirmed']
        if source=='final':
            files=a if ctx.case_id=='C08' else r
            finals=[x for x in files if x['name']=='ra-'+ctx.case_id.lower()+'.bin']
            partial=[x for x in files if x not in finals]
            s['receivedFile']={'finalCount':len(finals),'partialCount':len(partial),'newFinalFiles':len(finals),'partialFiles':len(partial)}
            if len(finals)==1:s['receivedFile'].update(length=finals[0]['size'],sha256=finals[0]['sha256'])
    if ctx.case_id=='C10':
        if source=='b-chat':
            lines=text_field(o,'chat-history',ctx,deadline).splitlines()
            expected='지원자: RA-한글😀'
            s['bChat']={'remoteLineEquals':expected in lines,'remoteLineCount':sum(x==expected for x in lines),'unicodeValid':expected in lines}
        s['aChatLineCount']=b['chatBLineCount'];s['chatRenderMode']=b['chatRenderMode']
    if ctx.case_id=='C11':
        s['testClipboardHash']=hashlib.sha256('RA-CLIP😀'.encode()).hexdigest()
        if source=='os':s['clipboardOs']=o.desktop.launch_probe('pc-b','clipboard-compare',ctx,deadline)
    if ctx.case_id in ('C13','C14'):
        s['installedBookHash']=get(p['installed-book.json'],'hash');s['previewBookHash']=b['bookPreviewHash']
        requests=[m for m in messages if m.get('t')=='runbook-request']
        if requests:s['operationId']=requests[-1]['id']
        s['initialInputEpoch']=o.last.get('initialInputEpoch')
        s.update(o.last.get('book-consent',{}))
        if 'book-consent' in o.last:s['dialogBookHash']=o.last['book-consent']['dialogHash']
        if 'book-consent' in o.last:s['dialogBookHash']=o.last['book-consent']['dialogHash']
    if ctx.case_id in ('C01','C15') and source=='video':
        capture=last(trace,'capture-start')['metadata'];marker=s['fixture']['marker']
        probe=ctx.run_dir/'video-marker-probe.json'
        if not probe.exists():new_json(probe,{'runId':ctx.run_id,**marker,'monitor':capture})
        samples=o.last.setdefault('videoSamples',[])
        if not samples or b['tick']>samples[-1]['tick']:
            samples.append({'tick':b['tick'],'frames':b['frames'],'width':b['videoWidth'],'height':b['videoHeight'],
                'bytes':sum(x['bytesReceived'] for x in b['selected'])})
        if len(samples)<2 or b['markerMatched'] is None or samples[-2]['frames']<3 or samples[-1]['frames']<=samples[-2]['frames']:raise Pending('video-progress')
        ready=last([m for m in messages if m.get('t')=='data-ready'],'data-ready','t')['monitor']
        s.update(videoSamples=samples,videoMarkerMatched=b['markerMatched'],
            videoMonitorMatched=ready['identity']==capture['monitor'] and ready['width']==capture['width'] and ready['height']==capture['height'],
            videoBytesBefore=samples[0]['bytes'],videoBytesAfter=samples[-1]['bytes'])
    if ctx.case_id=='C15':
        from e2e_case_turn import observe_turn
        observed={**b,'selected':o.last.get('selected-pairs',[])} if source=='end' else b
        s['turn']=observe_turn(o.config,ctx,observed,trace,source)
    if ctx.case_id=='C16':
        s['serviceIdentityMatched']=bool(processes('RemoteAssist.Host.Service.exe'))
        if source in ('down','fault','recovery-order'):
            s['fault']=o.desktop.await_file('pc-b','fault-fired.json',ctx,deadline)
        if source=='recovery-order':
            native=last(trace,'recovery-native-up')['metadata']
            s['toolUpCausedRecovery']=not native['f12HeldBefore']
        s['serviceRestarted']=len({r['pid'] for r in trace if r['kind']=='service-settings'})>1
    return s


def read(o,source,ctx,deadline):
    while True:
        ctx.remaining(deadline)
        try:
            if ctx.case_id=='C11' and source=='baseline':payload=o.last['clipboard-baseline']
            else:
                s=assemble(o,source,ctx,deadline)
                terminal=source in ('audit','end','final') and ctx.case_id not in ('C03',)
                if terminal and not any(r.get('Kind')==3 and r.get('SessionId')==ctx.session_id for r in s['audit']):raise Pending('session-end-audit')
                payload=derive(ctx.case_id,source,s)
            record={'runId':ctx.run_id,'caseId':ctx.case_id,'stepId':ctx.active_step,'source':source,
                'available':True,'machine':'pc-a','tick':ctx.clock(),'frequency':1_000_000_000,
                'sessionId':ctx.session_id,'connectionGeneration':ctx.generation,'payload':payload}
            o.last['observation']=record
            return record
        except Pending:
            time.sleep(min(.05,ctx.remaining(deadline)))
