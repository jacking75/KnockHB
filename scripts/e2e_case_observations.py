"""Pure reductions of captured browser, product, OS and fixture observations.

Missing facts are never filled with expected values. Pending means an event has
not arrived yet; an observed contradiction is left to the case's strict predicate.
"""
from e2e_case_runtime import require,get,MASKS,CHANNELS,FILE_HASH,CaseError


class Pending(CaseError):
    def __init__(self,source):super().__init__('observation-pending',source)


def last(rows,kind,field='kind'):
    matches=[r for r in rows if r.get(field)==kind]
    if not matches:raise Pending(kind)
    return matches[-1]

def invariants(case,browser,events,trace,complete=True):
    """Arrived violations fail before any Pending/audit wait can hide them."""
    index=int(case[1:])-1
    channels=[r['label'] for r in events if r.get('kind')=='channel-created']
    require(len(channels)==len(set(channels)) and set(channels)<=set(CHANNELS[index]),'unauthorized-or-duplicate-channel')
    consent=[r for r in trace if r['kind']=='consent-ui']
    if consent:
        require(len(consent)==1 and consent[0]['metadata']['mask']==MASKS[index],'unexpected-consent-mask')
        if 'tick' in consent[0]:
            require(all(r['tick']>=consent[0]['tick'] for r in trace if r['kind'] in ('native-input','capture-start','runbook-start')),'producer-before-consent')
    elif complete:
        require(not channels and not browser.get('frames',0) and not any(r['kind'] in ('native-input','capture-start','runbook-start') for r in trace),'activity-before-consent')
    if case!='C15':
        pairs=browser.get('selected',[])+[r['metadata'] for r in trace if r['kind']=='selected-pair']
        require(all('relay' not in (p.get('localType'),p.get('remoteType')) for p in pairs),'direct-path-assumption-not-met')
    def msgs(t):return [r for r in events if r.get('message',{}).get('t',r.get('message',{}).get('type'))==t]
    if case in ('C02','C12'):
        require(not browser.get('frames',0) and not any(r['kind'] in ('native-input','capture-start') for r in trace),'forbidden-video-or-input')
    if case=='C02':require(not msgs('data-ready') and not msgs('offer'),'unapproved-session-start')
    if case in ('C07','C08','C09'):
        require(not any(r['kind']=='save-stream-error' for r in events),'native-save-failed')
        binary=[r for r in events if r['kind'].endswith('-binary')]
        accepts=msgs('file-accept');rejects=msgs('file-reject')
        offers=msgs('file-offer')
        require(all(len(msgs(kind))<=1 for kind in ('file-offer','file-complete','file-received')),'duplicate-file-terminal')
        require(len(accepts)<=1 and len(rejects)<=1,'duplicate-file-decision')
        require(not binary or accepts and binary[0]['tick']>=accepts[0]['tick'],'file-before-accept')
        require(not binary or len(offers)==1 and all(r.get('id')==offers[0]['message']['id'] for r in binary),'file-packet-id')
        require(all(r.get('validHeader') is True and type(r.get('chunkSeq')) is int and type(r.get('length')) is int and
                    r.get('chunkSeq')==i and r.get('length')==16384 for i,r in enumerate(binary)), 'file-chunk-order-or-size')
        if rejects:require(not accepts and not binary and not msgs('file-complete') and not msgs('file-received'),'file-after-deny')
    if case in ('C13','C14'):
        workers=[r for r in trace if r['kind']=='runbook-start']
        approvals=[r for r in trace if r['kind']=='runbook-consent-ui' and r['metadata'].get('approved')]
        require(len(workers)<=1 and (case!='C14' or not workers),'unexpected-worker')
        if workers:
            require(len(approvals)==1 and approvals[0]['tick']<=workers[0]['tick'] and
                    approvals[0]['metadata'].get('id')==workers[0]['metadata'].get('id'),'worker-before-approval')

def await_completion(case,source,s):
    """Explicit normal arrival gates, never a catch-all around predicates."""
    b=s['browser'];events=s['events'];trace=s['trace']
    def has(t):return any(r.get('message',{}).get('t',r.get('message',{}).get('type'))==t for r in events)
    if source in ('session','consent','baseline','catalog') and case!='C02':
        if not has('data-ready'):raise Pending('data-ready')
    if source in ('end','final','revoked','denied') and case not in ('C03',):
        if not b.get('joinVisible',False):raise Pending('viewer-end')
    if source=='final' and case in ('C07','C08') and s['receivedFile']['finalCount']==0:raise Pending('final-file')
    if source=='save' and any(r['kind']=='save-stream-error' for r in events):require(False,'save-stream-error')
    if source in ('request','pending') and case in ('C13','C14') and not s['bookDialogPresent']:raise Pending('book-dialog')
    if case in ('C13','C14'):
        for worker in (r['metadata'] for r in trace if r['kind']=='runbook-start'):
            require(worker['hash']==s['installedBookHash'] and worker['userSid']==s['baselineOS']['sid'] and
                    worker['session']==s['baselineOS']['session'] and worker['isSystem'] is False,'worker-binding-or-privilege')
    if source=='admin-pending' and not any(r.get('message',{}).get('state')=='pending' for r in events):raise Pending('admin-pending')
    if source in ('click','drag','wheel','key','text') and case in ('C03','C14'):
        f=s['fixture'];base=s['fixtureBaseline']
        if source=='click' and f['clicks']==base['clicks']:raise Pending('target-click')
    if case=='C10':
        if source=='a-chat' and b['chatBLineCount']==0:raise Pending('a-chat-ui')
        if source=='b-chat' and s['bChat']['remoteLineCount']==0:raise Pending('b-chat-ui')
        if source in ('a-sent','b-sent') and not has('chat'):raise Pending('chat-message')


def derive(case,source,s):
    b=s['browser'];events=s['events'];trace=s['trace'];local=s['last'];ctx=s['context']
    def messages(t,direction=None):
        return [r for r in events if r.get('message',{}).get('t',r.get('message',{}).get('type'))==t and
                (direction is None or r['kind']==direction)]
    def message(t,direction=None):
        rows=messages(t,direction)
        if not rows:raise Pending(t)
        return rows[-1]
    def trows(kind):return [r for r in trace if r['kind']==kind and r.get('sessionId') in (None,ctx.session_id)]
    def tlast(kind):return last(trows(kind),kind)
    def audit():
        rows=[r for r in s['audit'] if r.get('SessionId')==ctx.session_id and r.get('ConnectionId')==s['connectionId']]
        return {'rows':rows,'SessionId':ctx.session_id,'ConnectionId':s['connectionId'],
                'PermissionGeneration':0 if case=='C02' else 1,'operationId':s.get('operationId')}
    def session():
        ui=tlast('consent-ui')['metadata']
        require(ui['mask']==MASKS[int(case[1:])-1],'unexpected-consent-mask')
        data=message('data-ready')['message']
        channels=[r['label'] for r in events if r['kind']=='channel-created']
        require(set(channels)<=set(CHANNELS[int(case[1:])-1]),'unauthorized-channel')
        if set(channels)!=set(CHANNELS[int(case[1:])-1]):raise Pending('allowed-channels')
        channels=sorted(channels,key=CHANNELS[int(case[1:])-1].index)
        return {'mask':ui['mask'],'channels':channels,'beforeConsentFrames':s['preConsent']['framesMax'],
                'beforeConsentInputCount':s['preConsent']['nativeCount'],'ownerMatched':s['ownersMatched'],
                'consentMatched':ui['mask']==sum({'screen':1,'control':2,'admin':4,'file':8,'clipboard':16,
                    'diagnostics':32,'runbook':64,'chat':128}[p] for p in data['permissions'])}
    def held():
        rows=trows('reservation-ack')+trows('input-backend-ready')
        if not rows:raise Pending('held')
        newest=max(rows,key=lambda r:r['tick'])
        return {'productHeld':newest['metadata']['heldCount'],'osHeld':len(s['os']['heldVirtualKeys']),
                'observationValid':s['os']['available']}
    def returned():
        os=s['os'];base=s['baselineOS']
        return {'desktop':os['desktop'],'session':os['activeSessionId'],'baselineSession':base['session'],'sid':os['sid'],'baselineSid':base['sid'],
                'osHeld':len(os['heldVirtualKeys'])}
    def file_offer():
        row=message('file-offer');m=row['message'];
        return {'name':m['name'],'size':m['size'],'hash':m['hash'],'offerId':m['id'],
                'dialogId':s['fileDialogId'],'binaryBeforeAccept':sum(r.get('length',r.get('bytes',0)) for r in events
                    if r['kind'].endswith('-binary') and r['tick']<s.get('acceptTick',float('inf')))}
    def book():
        catalog=message('runbook-catalog')['message']['books']
        books=[x for x in catalog if x['bookId']=='network-summary']
        require(len(books)==1,'book-not-unique')
        current=books[0]
        return {'bookId':current['bookId'],'bookVersion':current['bookVersion'],'requiresAdmin':current['requiresAdmin'],
                'detail':b['bookDetail'],'installedHash':s['installedBookHash'],'catalogHash':current['hash'],
                'previewHash':s['previewBookHash']}
    if source=='audit':return audit()
    if source in ('runtime','ready'):
        return ({'allocationSourceReady':s['turn']['sourceReady'],'guardsReady':s['guardsReady']} if case=='C15' else
                {'aReady':s['aReady'],'bReady':s['bReady'],'guardsReady':s['guardsReady']})
    if case=='C01':
        if source=='pairing':return {'privateBindingValid':local['pairing-valid'],'hostStartCount':local['host-start-count']}
        if source=='join':return {'consentPending':bool(messages('consent-pending')),'joinCount':len(messages('join','signal-sent'))}
        if source=='pre-consent':return {'framesMax':s['preConsent']['framesMax'],'inputEnabled':b['inputEnabled'],
            'inputChannels':[r['label'] for r in events if r['kind']=='channel-created' and r['label'] in ('input','pointer')],
            'videoWidth':b['videoWidth'],'observedWindowMs':s['preConsent']['observedWindowMs']}
        if source=='consent':return session()
        if source=='stop':
            stop=last(s['service'],'local-stop-observation')['observation'];action=tlast('stop-ui')['metadata']
            return {**stop,'actionRequestId':action['requestId'],'frequency':action['frequency']}
        if source=='end':return {**audit(),'joinVisible':b['joinVisible'],'srcObjectPresent':b['srcObjectPresent'],
            'indicatorActive':s['indicatorActive'],'helperExited':not s['helperPids']}
    if case=='C02':
        if source=='pending':return {'pending':bool(messages('consent-pending')),'validPairing':local['pairing-valid']}
        if source=='baseline':return {'approvedMask':sum(r['metadata']['mask'] for r in trows('consent-ui')),
            'framesMax':b['frames'],'inputEnabled':b['inputEnabled'],'privilegedStartCount':len(trows('admin-desktop'))}
        if source=='deny':
            ui=tlast('consent-ui')['metadata']
            return {'deniedClicks':sum(r['metadata']['decision']=='denied' for r in trows('consent-ui')),'approvedMask':ui['mask']}
        if source=='denied':return {'totalFrames':s['negativeWindow']['framesMax'],'nativeCount':sum(r['metadata']['count'] for r in trows('native-input')),
            'featureChannelsCreated':[r['label'] for r in events if r['kind']=='channel-created'],
            'joinVisible':b['joinVisible'],'consentWindowPresent':s['consentWindowPresent'],
            'newOfferCount':s['negativeWindow']['newOffers'],'newDataReadyCount':s['negativeWindow']['newDataReady'],
            'observedWindowMs':s['negativeWindow']['observedWindowMs']}
    if case in ('C03','C14','C16'):
        f=s['fixture'];baseline=s['fixtureBaseline']
        if source=='click':return {'clickDelta':f['clicks']-baseline['clicks'],'textFocused':f['textFocused'],**(held() if case=='C14' else {})}
        if source=='baseline':return {**session(),**held(),'recoveryBound':bool(trows('recovery-bound')),
            'foreground':f['foreground'],'ownerHandlesBound':s['serviceIdentityMatched'],
            **(dict(**book(),workerStartCount=len(trows('runbook-start')),inputReady=b['inputReady']=='true') if case=='C14' else {})}
    if case=='C03':
        if source=='text':
            if s['fixture']['text']=='':raise Pending('target-text')
            return {'text':s['fixture']['text']}
        if source=='key' and (f['f12Down']-baseline['f12Down']<1 or f['f12Up']-baseline['f12Up']<1):raise Pending('target-key-pair')
        if source=='wheel' and f['wheel']==baseline['wheel']:raise Pending('target-wheel')
        if source=='drag' and f['dragCount']==baseline['dragCount']:raise Pending('target-drag')
        if source=='key':return {'f12DownDelta':f['f12Down']-baseline['f12Down'],'f12UpDelta':f['f12Up']-baseline['f12Up'],
            'defaultPrevented':last(events,'f12-keydown')['defaultPrevented']}
        if source=='wheel':return {'wheelDeltaSum':f['wheelDeltaSum']-baseline['wheelDeltaSum'],'wheelCount':f['wheel']-baseline['wheel'],'observationValid':f['observationValid']}
        if source=='drag':return {'pathMatched':s['dragPathMatched'],'buttonTransitions':s['dragTransitions'],
            'dragCount':f['dragCount']-baseline['dragCount'],'observationValid':f['observationValid']}
        if source=='reset':
            sent=message('reset','dc-sent');ack=message('input-reset-ack','dc-received')
            return {'requestId':sent['message']['id'],'ackId':ack['message']['id'],'epoch':sent['message']['inputEpoch'],
                'ackEpoch':ack['message']['inputEpoch'],'requestedTick':sent['tick'],'ackTick':ack['tick'],'frequency':1000,
                'resetCount':len([r for r in messages('reset','dc-sent') if r['seq']>=local['blur-sequence']])}
        if source=='held':return held()
    if case in ('C04','C05','C06'):
        admin=messages('admin-state','dc-received')
        active=bool(admin and admin[-1]['message'].get('state')=='active' and not b['joinVisible'])
        if source=='admin-before':return {'privilegedStartCount':len(trows('admin-desktop')),'nativeInputCount':sum(r['metadata']['count'] for r in trows('native-input'))}
        if source=='admin-pending':return {'pending':bool(admin and admin[-1]['message'].get('state')=='pending'),
            'inputEnabled':b['inputEnabled'],'productHeld':held()['productHeld']}
        if source in ('admin-active','admin'):
            row=message('admin-state','dc-received')['message']
            if row.get('state') in ('pending','inactive'):raise Pending('admin-approval')
            return {'active':active,'localApprovalMatched':s['adminLocalApprovalMatched'],'generation':row.get('adminGeneration',0),
                    'currentGeneration':row.get('generation')==ctx.generation,'productHeld':held()['productHeld']}
        if source=='baseline':return {**returned(),**held(),'adminActive':active}
        if source=='return':
            result=returned()
            if case=='C06':return {**result,'oldAdminGenerationValid':active,'inputEnabled':b['inputEnabled'],
                'producerRestartCount':s['producerRestartCount']}
            secure=next((r for r in trows('admin-desktop') if r['metadata']['desktop']=='Winlogon'),None)
            default=[r for r in trows('admin-desktop') if secure and r['tick']>secure['tick'] and r['metadata']['desktop']=='Default']
            if not secure or not default:raise Pending('secure-desktop-return')
            return {**result,**held(),'secureTick':secure['tick'],'returnTick':default[0]['tick'],'frequency':secure['frequency'],
                'cancelledByViewer':s['cancelledByViewer']}
        if source=='uac':return {'desktop':tlast('admin-desktop')['metadata']['desktop'],'uacVisibleInViewer':s['uacSceneMatched']}
        if source=='revoked':return {'sessionEnded':b['joinVisible'],'inputEnabled':b['inputEnabled']}
        if source=='sas':return {'commandCount':len(messages('admin-sas','dc-sent')),'sasAuditCount':sum(r['Kind']==11 for r in s['audit'])}
        if source=='screen':return {**s['securityScene'],'desktop':tlast('admin-desktop')['metadata']['desktop']}
        if source=='cancel':return {'cancelledByViewer':s['cancelledByViewer'],'cancelCount':local['viewer-cancel-count']}
        if source=='lock-action':return {'actionCount':local['lock-count'],'operatorReady':bool(local.get('operator-ready'))}
        if source=='locked':return {'locked':s['sessionStates'][-1]['locked'],'independentObservation':s['sessionStates'][-1]['runId']==ctx.run_id}
        if source=='operator':return local['operator-return']
        if source=='end':
            if case=='C04':return {**audit(),'adminHelperCount':len(s['adminPids']),'mappingValid':s['adminMappingValid'],'generationValid':active}
            return {**audit(),'observedWindowMs':s['negativeWindow']['observedWindowMs'],
                'newInputPermissions':s['negativeWindow']['newInputPermissions'],'captureResumes':s['producerRestartCount']}
    if case in ('C07','C08','C09'):
        if source in ('session','baseline'):return {**session(),'aReceiveEmpty':s['aReceive']['empty'],'bReceiveEmpty':s['bReceive']['empty'],
            'receiveEmpty':s['bReceive']['empty'],'binaryBytes':sum(r.get('length',0) for r in events if r['kind'].endswith('-binary'))}
        if source=='source':return {**s['sourceFile'],'selectionCount':local['file-selection-count']}
        if source=='offer':return file_offer()
        if source=='accept':return {'acceptedLocally':s['fileAcceptedLocally'],'approveCount':local['receive-approve-count'],'pathOwnerMatched':s['receivePathMatched']}
        if source=='save' and not any(r['kind']=='save-picker-approved' for r in events):raise Pending('save-picker')
        if source=='save':return {'saveApproved':len([r for r in events if r['kind']=='save-picker-approved'])==1,
            'saveApi':'showSaveFilePicker' if any(r['kind']=='save-picker-open' for r in events) else 'unobserved',
            'approveCount':local['receive-approve-count'],'pathOwnerMatched':s['receivePathMatched']}
        if source=='transfer':
            offered=message('file-offer')['message'];complete=message('file-complete');receipt=message('file-received')
            packets=[r for r in events if r['kind'].endswith('-binary') and r.get('id')==offered['id']]
            require(all(r.get('validHeader') is True for r in packets),'file-header-invalid')
            if case=='C08':
                closed=[r for r in events if r['kind']=='save-stream-closed']
                if not closed:raise Pending('save-stream-close')
                require(len(closed)==1 and closed[0].get('id')==offered['id'] and complete['tick']<=closed[0]['tick']<=receipt['tick'],'file-save-close-correlation')
            times=[r['tick'] for r in packets]+[complete['tick'],receipt['tick']]
            return {'offerId':offered['id'],'completeId':complete['message']['id'],'receiptId':receipt['message']['id'],
                'chunkSeqs':[r['chunkSeq'] for r in packets],'chunkLengths':[r['length'] for r in packets],
                'receivedBytes':receipt['message']['size'],'receiptMatched':receipt['message']['hash']==offered['hash'],
                'completeCount':len(messages('file-complete')),'maximumIdleMsWithin10000':all(0<=v-u<=10000 for u,v in zip(times,times[1:])),
                'streamClosed':any(r['kind']=='save-stream-closed' for r in events)}
        if source=='deny':return {'denyClicks':local['file-deny-count'],'timeout':message('file-reject')['message'].get('code')=='timeout'}
        if source=='denied':return {'rejectCode':message('file-reject')['message']['code'],'acceptCount':len(messages('file-accept')),
            'binaryBytes':sum(r.get('length',0) for r in events if r['kind'].endswith('-binary')),
            'completeCount':len(messages('file-complete')),'receiptCount':len(messages('file-received'))}
        if source=='final':return {**s['receivedFile'],**audit(),'automaticReofferCount':max(0,len(messages('file-offer'))-1)}
    if case=='C10':
        if source=='baseline':return session()
        if source in ('a-sent','b-sent'):return {'sentCount':len(messages('chat','dc-sent' if source=='a-sent' else 'dc-received')),'selfLineEquals':b['chatAEqualsTest']}
        if source=='b-chat':return s['bChat']
        if source=='a-chat':return {'remoteLineEquals':b['chatBEqualsTest'],'remoteLineCount':s['aChatLineCount'],
            'renderMode':s['chatRenderMode'],'unicodeValid':all(r['message'].get('textEqualsTest') for r in messages('chat','dc-received'))}
        if source=='final':return {**audit(),'aSentCount':len(messages('chat','dc-sent')),'bSentCount':len(messages('chat','dc-received')),
            'clipboardCount':sum(r.get('message',{}).get('t','').startswith('clipboard-') for r in events)}
    if case=='C11':
        if source=='baseline':return local['clipboard-baseline']
        if source=='session':return {**session(),'chatEnabled':b['chatEnabled'],'automaticRequests':len(messages('clipboard-set'))+len(messages('clipboard-get'))}
        if source in ('set','get'):
            sent=message('clipboard-'+source,'dc-sent');response=message('clipboard-applied' if source=='set' else 'clipboard-text','dc-received')
            require(len(messages('clipboard-'+source,'dc-sent'))==1 and
                    len(messages('clipboard-applied' if source=='set' else 'clipboard-text','dc-received'))==1,'duplicate-clipboard-operation')
            require(sent['message']['id']==response['message']['id'],'clipboard-response-id')
            from e2e_case_runtime import elapsed
            elapsed({'frequency':1000,'sent':sent['tick'],'response':response['tick']},'sent','response',2000)
            require(response['message']['hash']==s['testClipboardHash'],'clipboard-response-hash')
            if source=='get':require(response['message'].get('testText')=='RA-CLIP😀','clipboard-response-text')
            if source=='get' and not b['clipboardEqualsTest']:raise Pending('clipboard-ui')
            return {'id':sent['message']['id'],'responseId':response['message']['id'],
                'hash':sent['message'].get('hash',s['testClipboardHash']),'responseHash':response['message']['hash'],
                'requestedTick':sent['tick'],'responseTick':response['tick'],'frequency':1000,'applied':source=='set',
                'responseEqualsTestText':response['message'].get('testText')=='RA-CLIP😀','uiEqualsTestText':b['clipboardEqualsTest']}
        if source=='os':return s['clipboardOs']
        if source=='counts':return {'setCount':len(messages('clipboard-set','dc-sent')),'getCount':len(messages('clipboard-get','dc-sent')),
            'unsolicitedCount':sum(r['message'].get('id') not in {x['message']['id'] for x in messages('clipboard-set')+messages('clipboard-get')}
                for r in messages('clipboard-text')+messages('clipboard-applied'))}
        if source=='restored':return local['clipboard-restored']
    if case=='C12':
        if source=='session':return session()
        samples=[r['message']['snapshot'] for r in messages('telemetry','dc-received')]
        if source in ('telemetry','compare'):
            if len(samples)<2:raise Pending('two-telemetry-samples')
            return {'samples':samples,'osTotalBytes':s['os']['osTotalBytes'],
                'totalMatchesOs':all(x['memory']['totalBytes']==s['os']['osTotalBytes'] for x in samples[-2:]),
                'cpuSampleValid':all(type(x['cpuPercent']) in (int,float) and 0<=x['cpuPercent']<=100 for x in samples[-2:])}
        if source=='isolation':return {'videoTrackCount':sum(r['kind']=='track' and r.get('mediaKind')=='video' for r in events),
            'frames':b['frames'],'captureStartCount':len(trows('capture-start')),'nativeInputCount':sum(r['metadata']['count'] for r in trows('native-input')),
            'inputEnabled':b['inputEnabled'],'inputChannels':[r['label'] for r in events if r['kind']=='channel-created' and r['label'] in ('input','pointer')]}
        if source=='presentation':
            sample=message('telemetry','dc-received');age=b['tick']-sample['tick']
            return {'failedProviders':[{'value':None,'display':'N/A' if b['diagnosticsHasNA'] else 'unobserved'} for _ in sample['message']['snapshot']['unavailableProviders']],
                'sampleAgeMs':age,'staleDisplaysDelayed':b['diagnosticsDelayed'],'staleBranchObserved':age>=5000}
    if case in ('C13','C14'):
        if source in ('catalog','preview'):return {**(session() if source=='catalog' else {}),**book()}
        if source=='request':return {'requestCount':len(messages('runbook-request','dc-sent')),'approvalWindowPresent':s['bookDialogPresent']}
        if source=='consent':return {**book(),'dialogHash':s['dialogBookHash'],'approveCount':local['book-approve-count'],'workerBeforeCount':s['workerBeforeCount']}
        if source=='worker':
            start=tlast('runbook-start');end=tlast('runbook-exit');m=start['metadata']
            require(m['id']==end['metadata']['id'],'worker-result-id')
            return {'startCount':len(trows('runbook-start')),'state':message('runbook-result')['message']['result']['state'],
                'exitCode':end['metadata']['exitCode'],'session':m['session'],'baselineSession':s['baselineOS']['session'],'isSystem':m['isSystem'],
                'installedHash':s['installedBookHash'],'executedHash':m['hash'],'userSid':m['userSid'],'baselineSid':s['baselineOS']['sid'],
                'startTick':start['tick'],'exitTick':end['tick'],'frequency':start['frequency']}
        if source=='end':return {**audit(),'exited':not s['workerPids']}
        if source=='pending':return {'inputEnabled':b['inputEnabled'],'approvalWindowPresent':s['bookDialogPresent'],'workerStartCount':len(trows('runbook-start'))}
        if source=='cancel':return {'cancelCount':len(messages('runbook-cancel','dc-sent')),
            'requestId':message('runbook-cancel')['message']['id'],'pendingId':message('runbook-request')['message']['id']}
        if source=='dismiss':
            sent=tlast('dismiss-sent');closed=tlast('dialog-closed');ack=tlast('dismiss-ack')
            require(sent['metadata']['id']==closed['metadata']['id']==ack['metadata']['id']==s['operationId'],'dismiss-operation')
            resumes=[r for r in trows('input-resumed') if r['metadata']['epoch']>s['initialInputEpoch']]
            if not resumes:raise Pending('new-epoch-resume')
            require(len(resumes)==1,'duplicate-resume');resumed=resumes[0]
            return {'requestId':sent['metadata']['id'],'ackId':ack['metadata']['id'],'dismissSentTick':sent['tick'],
                'dialogClosedTick':closed['tick'],'dismissAckTick':ack['tick'],'resumedTick':resumed['tick'],
                'frequency':sent['frequency'],'dialogPresent':s['bookDialogPresent'],'machine':'pc-b'}
        if source=='resume':return {'workerStartCount':len(trows('runbook-start')),'state':message('runbook-result')['message']['result']['state'],
            'inputReady':b['inputReady']=='true','newEpoch':tlast('input-resumed')['metadata']['epoch'],'oldEpoch':s['initialInputEpoch']}
    if case=='C15':
        if source=='policy':return {'aPolicy':b['effectiveRelayPolicy'],'bPolicy':tlast('service-settings')['metadata']['policy'],
            'forceRelayEffective':tlast('service-settings')['metadata']['policy']=='relay'}
        if source=='consent':return session()
        if source in ('pairs','allocation'):
            if not b['selected']:raise Pending('selected-pair')
            pair=tlast('selected-pair')['metadata']
            return {'a':{**b['selected'][-1],'generation':ctx.generation},'b':{**pair,'generation':pair['connectionGeneration']},
                'generation':ctx.generation,**s['turn']}
        if source=='end':return {**audit(),'allocationClosed':s['turn']['allocationClosed'],'sharedRelayStopped':s['turn']['sharedRelayStopped']}
    if case=='C16':
        if source=='armed':return local['fault-armed']
        if source in ('down','fault','recovery-order'):
            fault=s['fault'];downs=[r for r in trace if r['kind']=='native-input' and r['metadata']['f12Down']>0]
            if not downs:raise Pending('native-f12-down')
            down=downs[-1];acks=[r for r in trace if r['kind']=='reservation-ack' and r['metadata']['revision']==down['metadata']['revision'] and r['pid']==down['pid']]
            require(len(acks)==1,'reservation-correlation')
            if 'frequency' in down:
                require(down['frequency']==acks[0]['frequency']==fault['frequency'],'fault-clock-domain')
                require(down.get('processStartUtcTicks')==acks[0].get('processStartUtcTicks') and down.get('generation')==acks[0].get('generation'), 'fault-producer-generation')
            result={**fault,'ackTick':acks[0]['tick'],'nativeDownTick':down['tick'],'nativeDownDelta':sum(r['metadata']['f12Down'] for r in downs)}
            if source=='recovery-order':
                fenced=tlast('producers-fenced');up=tlast('recovery-up')
                native=tlast('recovery-native-up')
                require(all(type(v) is int for v in (up['metadata'].get('count'),up['metadata'].get('requested'),up['metadata'].get('f12Up'),
                        native['metadata'].get('count'),native['metadata'].get('f12Up'))),'native-recovery-count-type')
                require(up['metadata'].get('count')==up['metadata'].get('requested')==1 and up['metadata'].get('f12Up')==1 and
                        native['metadata'].get('count')==1 and native['metadata'].get('f12Up')==1,'native-recovery-up-not-completed')
                require(fenced['pid']==up['pid']==native['pid'] and fenced['frequency']==up['frequency']==native['frequency']==fault['frequency'], 'recovery-clock-producer')
                require(fenced.get('processStartUtcTicks')==up.get('processStartUtcTicks')==native.get('processStartUtcTicks'),'recovery-producer-start')
                require(down['pid'] in fenced['metadata']['pids'],'down-producer-not-fenced')
                result.update(machine='pc-b',producerFencedTick=fenced['tick'],recoveryUpTick=up['tick'],
                    toolUpCausedRecovery=s['toolUpCausedRecovery'])
            return result
        if source=='return':return {**returned(),'newHelperCount':s['producerRestartCount'],'sessionEnded':b['joinVisible'],'serviceRestarted':s['serviceRestarted'],
                                   'helperCount':len(s['helperPids']),'adminHelperCount':len(s['adminPids']),'workerCount':len(s['workerPids'])}
        if source=='end':return {**audit(),'serviceRestarted':s['serviceRestarted']}
    if source=='video' and case in ('C01','C15'):
        return {'samples':s['videoSamples'],'markerMatched':s['videoMarkerMatched'],'monitorMatched':s['videoMonitorMatched'],
            'indicatorActive':s['indicatorActive'],'bytesBefore':s['videoBytesBefore'],'bytesAfter':s['videoBytesAfter']}
    raise CaseError('unknown-case-observation',case+':'+source)
