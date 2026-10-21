"""Reviewed, closed C01-C16 recipes. No command text is accepted from config.

Each observation is a run/session/generation-bound envelope (runtime.py).
These predicates are deliberately strict about missing data. Live source adapters
must establish these fields from the sources in section 7 of each case document.
"""
from e2e_case_runtime import Step, assert_fields, require, get, numeric, elapsed, FILE_HASH


def E(**fields):
    window=fields.pop('observedWindowMs',None)
    def check(data):
        assert_fields(data,fields)
        if window is not None:require(numeric(get(data,'observedWindowMs'))>=window,'observation-window-too-short')
    return check


def all_of(*checks):
    def check(data):
        for predicate in checks:
            predicate(data)
    return check


def latency(start, end, ms):
    return lambda d: elapsed(d, start, end, ms)


def sequence(*paths):
    def check(d):
        values = [numeric(get(d, p)) for p in paths]
        require(all(a <= b for a,b in zip(values, values[1:])), 'causal-order')
    return check


def same(*paths):
    def check(d):
        values = [get(d, p) for p in paths]
        require(bool(values[0]) and all(type(x) is type(values[0]) and x == values[0] for x in values),
                'correlation-mismatch')
    return check


def video(d):
    samples = get(d, 'samples')
    require(isinstance(samples, list) and len(samples) >= 2, 'video-samples')
    a,b = samples[-2:]
    require(numeric(get(a,'frames')) >= 3 and numeric(get(b,'frames')) > a['frames'], 'video-progress')
    for s in (a,b):
        require(numeric(get(s,'width')) > 0 and numeric(get(s,'height')) > 0, 'video-size')
    assert_fields(d, {'markerMatched':True, 'monitorMatched':True})


def session(mask, channels):
    return E(mask=mask, channels=channels, beforeConsentFrames=0,
             beforeConsentInputCount=0, ownerMatched=True, consentMatched=True)


def file_final(d):
    assert_fields(d, {'length':262144, 'sha256':FILE_HASH, 'finalCount':1, 'partialCount':0})


def transfer(d):
    assert_fields(d, {'chunkSeqs':list(range(16)), 'chunkLengths':[16384]*16,
                      'receivedBytes':262144, 'receiptMatched':True, 'completeCount':1,
                      'maximumIdleMsWithin10000':True})
    same('offerId','completeId','receiptId')(d)


def offer(name):
    return all_of(E(name=name,size=262144,hash=FILE_HASH,binaryBeforeAccept=0),same('offerId','dialogId'))


def audit(*pairs):
    def check(d):
        rows = get(d, 'rows')
        require(isinstance(rows,list), 'audit-schema')
        for row in rows:
            for key in ('SessionId','ConnectionId'):
                require(get(row,key) == get(d,key), 'audit-scope', key)
            expected_generation = 0 if get(row,'Kind') in (2,3) else get(d,'PermissionGeneration')
            require(get(row,'PermissionGeneration') == expected_generation, 'audit-generation')
            require(type(get(row,'Kind')) is int and type(get(row,'Result')) is int, 'audit-enum')
        for kind,result,count in pairs:
            found = [r for r in rows if r['Kind']==kind and r['Result']==result]
            require(len(found)==count, 'audit-count', f'{kind}/{result}')
            if kind in (7,8,9,10):
                for row in found:
                    require(get(row,'Operation.Id') == get(d,'operationId'), 'audit-operation')
    return check


END = audit((1,0,1),(3,2,1))
FILE_END = audit((1,0,1),(7,0,1),(8,3,1),(3,2,1))
ADMIN_END = audit((1,0,1),(4,0,1),(6,2,1),(3,2,1))
HELD = E(productHeld=0,osHeld=0,observationValid=True)
RETURN = all_of(E(desktop='Default',osHeld=0), same('sid','baselineSid'),same('session','baselineSession'))
BOOK = all_of(E(bookId='network-summary',bookVersion=1,detail='summary',requiresAdmin=False),
              same('installedHash','catalogHash','previewHash'))


def telemetry(d):
    samples = get(d,'samples')
    require(len(samples)>=2 and get(samples[-1],'utc') != get(samples[-2],'utc'), 'telemetry-freshness')
    for s in samples[-2:]:
        require(0 <= numeric(get(s,'cpuPercent')) <= 100, 'cpu-range')
        total, available = numeric(get(s,'memory.totalBytes')), numeric(get(s,'memory.availableBytes'))
        require(total > 0 and 0 <= available <= total and total == get(d,'osTotalBytes'), 'memory-range')


def provider_ui(d):
    # Unobserved branches are explicitly NOT counted as a live branch PASS.
    failed = get(d,'failedProviders')
    require(isinstance(failed,list), 'provider-list')
    for item in failed:
        require('value' in item and item['value'] is None and get(item,'display')=='N/A', 'provider-na')
    if numeric(get(d,'sampleAgeMs')) >= 5000:
        assert_fields(d, {'staleDisplaysDelayed':True})
    else:
        assert_fields(d, {'staleBranchObserved':False})


def selected_pairs(d):
    for side in ('a','b'):
        p = get(d,side)
        require(get(p,'policy')=='relay' and get(p,'selected') is True, 'relay-policy')
        require('relay' in (get(p,'localType'),get(p,'remoteType')), 'selected-not-relay')
        require(get(p,'generation') == get(d,'generation'), 'relay-generation')


def relay_pair(d):
    selected_pairs(d)
    assert_fields(d, {'allocationMatched':True, 'allocationOwnerMatched':True})


def fault_order(d):
    assert_fields(d, {'machine':'pc-b','consumedCount':1,'identityMatched':True,
                      'f12HeldAtFault':True,'toolUpCausedRecovery':False, 'nativeDownDelta':1})
    sequence('ackTick','nativeDownTick','faultTick','producerFencedTick','recoveryUpTick')(d)
    require(get(d,'ackTick') < get(d,'nativeDownTick'), 'reservation-not-before-down')
    elapsed(d,'nativeDownTick','faultTick',500)
    elapsed(d,'faultTick','recoveryUpTick',3000)


def S(name, seconds, actions, source, predicate, *files):
    return Step(name, seconds, tuple(actions), ((source,predicate),),tuple(files))


def click(machine, target): return ('click',machine,target)
def text(machine, target, value): return ('text',machine,target,value)
def open_session(mask, target='none'): return ('open-session',mask,target)
def end(): return ('host-stop',)


CASES = {
 'C01': (
  S('StartRuntimes',90,[('start-runtimes',)],'runtime',E(aReady=True,bReady=True,guardsReady=True),'local-start.json'),
  S('IssuePairing',20,[('issue-pairing',)],'pairing',E(privateBindingValid=True,hostStartCount=1),'pairing-action.json'),
  S('JoinViewer',20,[('join-viewer',)],'join',E(consentPending=True,joinCount=1),'join-action.json'),
  S('ObserveBeforeConsent',10,[],'pre-consent',E(framesMax=0,inputEnabled=False,inputChannels=[],videoWidth=0,observedWindowMs=5000),'pre-consent.json'),
  S('ApproveScreen',15,[('grant',1)],'consent',session(1,['control']),'consent-ui.json'),
  S('ObserveVideoAndIndicator',30,[],'video',all_of(video,E(indicatorActive=True)),'video-samples.ndjson','indicator.png'),
  S('HostStopOnce',10,[end()],'stop',all_of(E(processExited=True,completeInventory=True,result='PASS'),same('requestId','actionRequestId'),latency('requestedTick','observedTick',1000)),'stop-observation.json'),
  S('ObserveExitAndAudit',20,[],'end',all_of(E(joinVisible=True,srcObjectPresent=False,indicatorActive=False,helperExited=True),END),'viewer-ended.json','service-status.ndjson','audit.ndjson')),
 'C02': (
  S('ConnectPending',130,[open_session(0)],'pending',E(pending=True,validPairing=True),'consent-pending.json'),
  S('ObserveDeniedBaseline',10,[],'baseline',E(approvedMask=0,framesMax=0,inputEnabled=False,privilegedStartCount=0),'before-deny.json'),
  S('DenyOnce',5,[click('pc-b','ConsentDeny')],'deny',E(deniedClicks=1,approvedMask=0),'deny-action.json'),
  S('ObserveDeniedEnd',20,[],'denied',E(totalFrames=0,nativeCount=0,featureChannelsCreated=[],joinVisible=True,consentWindowPresent=False,newOfferCount=0,newDataReadyCount=0,observedWindowMs=5000),'denied-events.ndjson','after-deny.json'),
  S('CollectDeniedAudit',15,[],'audit',audit((2,1,1),(3,2,1)),'audit.ndjson')),
 'C03': (
  S('OpenControlTarget',140,[open_session(3,'input-target')],'baseline',all_of(session(3,['control','input','pointer']),HELD,E(recoveryBound=True,foreground=True)),'target-before.json','data-ready.json'),
  S('RemoteButtonClick',10,[('video-click','fixture-button')],'click',E(clickDelta=1,textFocused=True),'target-after-click.json'),
  S('SendFixedText',10,[text('pc-a','remote-text','RA-한글😀'),click('pc-a','send-text')],'text',E(text='RA-한글😀'),'target-after-text.json'),
  S('RemoteF12Pair',10,[('video-key-pair','F12',1500)],'key',E(f12DownDelta=1,f12UpDelta=1,defaultPrevented=True),'target-after-key.json'),
  S('RemoteDragOnce',10,[('video-drag','fixed-drag')],'drag',E(pathMatched=True,buttonTransitions=['down','up'],dragCount=1,observationValid=True),'target-after-drag.json'),
  S('RemoteWheelOnce',5,[('video-wheel',120)],'wheel',E(wheelDeltaSum=120,wheelCount=1,observationValid=True),'target-after-wheel.json'),
  S('BlurResetOnce',10,[('focus','pc-a','blur-window')],'reset',all_of(same('requestId','ackId'),same('epoch','ackEpoch'),latency('requestedTick','ackTick',1000),E(resetCount=1)),'reset-events.ndjson'),
  S('CompareTargetAndHeld',10,[],'held',HELD,'target-final.json','input-held-summary.json','input-actions.ndjson')),
 'C04': (
  S('OpenAdminEligibleSession',140,[open_session(7)],'admin-before',E(privilegedStartCount=0,nativeInputCount=0),'admin-before.json'),
  S('RequestAdminOnce',10,[click('pc-a','admin-request')],'admin-pending',E(pending=True,inputEnabled=False,productHeld=0),'admin-pending.json'),
  S('ApproveAdminLocally',20,[('approve-admin-local',)],'admin-active',all_of(E(active=True,localApprovalMatched=True),lambda d:require(numeric(get(d,'generation'))>0,'admin-generation')),'admin-consent.json','admin-active.json'),
  S('ShowPreparedUac',10,[('trigger-uac-once',)],'uac',E(desktop='Winlogon',uacVisibleInViewer=True),'uac-enter.json','uac-view.png'),
  S('CancelUacThroughViewer',20,[('video-click','uac-cancel')],'return',all_of(RETURN,E(cancelledByViewer=True),latency('secureTick','returnTick',30000)),'uac-cancel-action.json','desktop-return.json'),
  S('RevokeAdminOnce',10,[click('pc-a','admin-revoke')],'revoked',E(sessionEnded=True,inputEnabled=False),'admin-revoked.json'),
  S('CheckPrivilegeTeardown',15,[],'end',all_of(E(adminHelperCount=0,mappingValid=False,generationValid=False),ADMIN_END),'admin-after.json','audit.ndjson')),
 'C05': (
  S('OpenApprovedAdminSession',150,[open_session(7),('approve-admin-session',)],'admin',E(active=True,currentGeneration=True,productHeld=0),'admin-active.json'),
  S('SendSasOnce',5,[click('pc-a','admin-sas')],'sas',E(commandCount=1,sasAuditCount=1),'sas-action.json'),
  S('ObserveSecurityScreen',10,[],'screen',E(kind='Windows security options',desktop='Winlogon',actualScreenMatched=True),'security-screen.json','security-screen.png'),
  S('CancelSecurityScreen',10,[('video-key','ESC','press')],'cancel',E(cancelledByViewer=True,cancelCount=1),'sas-cancel-action.json'),
  S('VerifySameUserReturn',10,[],'return',all_of(RETURN,HELD,latency('secureTick','returnTick',20000)),'desktop-return.json','held-summary.json'),
  S('EndSasSession',15,[end()],'audit',audit((1,0,1),(4,0,1),(11,0,1),(6,2,1),(3,2,1)),'audit.ndjson')),
 'C06': (
  S('OpenApprovedLockSession',150,[open_session(7),('approve-admin-session',)],'baseline',all_of(RETURN,HELD,E(adminActive=True)),'lock-before.json'),
  S('LockOnce',5,[('lock-local-once',)],'lock-action',E(actionCount=1,operatorReady=True),'lock-action.json'),
  S('ObserveLockedDesktop',10,[],'locked',E(locked=True,independentObservation=True),'locked.json'),
  S('AwaitOperatorLogin',60,[('await-operator-login',)],'operator',E(returned=True,designatedOperator=True),'operator-return.json'),
  S('VerifySameUserAndRevocation',10,[],'return',all_of(RETURN,E(oldAdminGenerationValid=False,inputEnabled=False,producerRestartCount=0)),'lock-after.json'),
  S('CheckNoResumeAndEnd',15,[('observe-no-resume',5000),end()],'end',all_of(E(observedWindowMs=5000,newInputPermissions=0,captureResumes=0),ADMIN_END),'post-unlock-events.ndjson','audit.ndjson')),
 'C07': (
  S('OpenFileSession',140,[open_session(8)],'session',session(8,['control','file']),'file-session.json'),
  S('ChooseASource',15,[('choose-file','pc-a','ra-c07.bin')],'source',E(name='ra-c07.bin',size=262144,sha256=FILE_HASH),'source-choice.json'),
  S('OfferAFileOnce',5,[click('pc-a','file-send')],'offer',offer('ra-c07.bin'),'offer.json','pre-accept.json'),
  S('ApproveBReceive',20,[('approve-receive','pc-b')],'accept',E(acceptedLocally=True,approveCount=1,pathOwnerMatched=True),'receive-consent.json'),
  S('ObserveAtoBTransfer',15,[],'transfer',transfer,'transfer-events.ndjson'),
  S('HashBFileAndAudit',15,[end()],'final',all_of(file_final,FILE_END),'file-final.json','audit.ndjson')),
 'C08': (
  S('OpenReceiveFileSession',140,[open_session(8)],'session',all_of(session(8,['control','file']),E(aReceiveEmpty=True,bReceiveEmpty=True)),'file-session.json'),
  S('ChooseBSource',20,[('choose-file','pc-b','ra-c08.bin')],'source',E(name='ra-c08.bin',size=262144,sha256=FILE_HASH,selectionCount=1),'send-choice.json'),
  S('ObserveBOffer',5,[],'offer',offer('ra-c08.bin'),'offer.json','pre-accept.json'),
  S('ApproveASave',20,[('approve-receive','pc-a')],'save',E(saveApproved=True,saveApi='showSaveFilePicker',approveCount=1,pathOwnerMatched=True),'save-consent.json'),
  S('ObserveBtoATransfer',15,[],'transfer',all_of(transfer,E(streamClosed=True)),'transfer-events.ndjson'),
  S('HashAFileAndAudit',15,[end()],'final',all_of(file_final,FILE_END),'file-final.json','audit.ndjson')),
 'C09': (
  S('OpenFileDenySession',140,[open_session(8)],'baseline',all_of(session(8,['control','file']),E(receiveEmpty=True,binaryBytes=0)),'file-before.json'),
  S('OfferFixedFile',20,[('choose-file','pc-a','ra-c09.bin'),click('pc-a','file-send')],'offer',offer('ra-c09.bin'),'offer.json'),
  S('DenyBReceiveOnce',5,[click('pc-b','file-deny')],'deny',E(denyClicks=1,timeout=False),'file-deny-action.json'),
  S('ObserveNoPayload',10,[],'denied',E(rejectCode='declined',acceptCount=0,binaryBytes=0,completeCount=0,receiptCount=0),'file-denied-events.ndjson'),
  S('CheckEmptyFolderAndDeniedAudit',10,[end()],'final',all_of(E(newFinalFiles=0,partialFiles=0,automaticReofferCount=0),audit((1,0,1),(7,0,1),(8,1,1),(3,2,1))),'file-after.json','audit.ndjson')),
 'C10': (
  S('OpenChatSession',140,[open_session(128)],'baseline',session(128,['control','chat']),'chat-before.json'),
  S('SendAChatOnce',10,[text('pc-a','chat-text','RA-한글😀'),click('pc-a','chat-send')],'a-sent',E(sentCount=1,selfLineEquals=True),'a-chat-action.json'),
  S('ReadBChat',5,[],'b-chat',E(remoteLineEquals=True,remoteLineCount=1,unicodeValid=True),'b-chat-after.json'),
  S('SendBChatOnce',10,[text('pc-b','chat-text','ACK-RA'),click('pc-b','chat-send')],'b-sent',E(sentCount=1),'b-chat-action.json'),
  S('ReadAChat',5,[],'a-chat',E(remoteLineEquals=True,remoteLineCount=1,renderMode='textContent',unicodeValid=True),'a-chat-after.json'),
  S('VerifyChatCountsAndEnd',10,[end()],'final',all_of(E(aSentCount=1,bSentCount=1,clipboardCount=0),END),'chat-events.ndjson','audit.ndjson')),
 'C11': (
  S('CaptureClipboardBaseline',10,[('capture-clipboard-baseline',)],'baseline',E(captured=True,restorable=True,ownerMatched=True),'clipboard-owner.json'),
  S('OpenClipboardSession',140,[open_session(16)],'session',all_of(session(16,['control','chat']),E(chatEnabled=False,automaticRequests=0)),'clipboard-session.json'),
  S('SetClipboardOnce',10,[text('pc-a','clipboard-text','RA-CLIP😀'),click('pc-a','clipboard-send')],'set',all_of(E(applied=True),same('id','responseId'),same('hash','responseHash'),latency('requestedTick','responseTick',2000)),'clipboard-set.json'),
  S('CompareRealBClipboard',5,[],'os',E(osEqualsTestText=True),'clipboard-os-check.json'),
  S('GetClipboardOnce',10,[click('pc-a','clipboard-get')],'get',all_of(E(responseEqualsTestText=True,uiEqualsTestText=True),same('id','responseId'),same('hash','responseHash'),latency('requestedTick','responseTick',2000)),'clipboard-get.json'),
  S('CheckNoAutomaticSync',5,[end()],'counts',E(setCount=1,getCount=1,unsolicitedCount=0),'clipboard-events.ndjson'),
  S('RestoreClipboardBaseline',10,[('restore-clipboard-baseline',)],'restored',E(baselineRestored=True,privateContentExposed=False),'clipboard-restored.json','audit.ndjson')),
 'C12': (
  S('ApproveDiagnosticsOnly',140,[open_session(32)],'session',session(32,['control','telemetry']),'diagnostics-consent.json'),
  S('ReceiveTwoTelemetrySamples',10,[],'telemetry',telemetry,'telemetry-samples.ndjson'),
  S('CompareCpuAndMemory',5,[],'compare',all_of(telemetry,E(totalMatchesOs=True,cpuSampleValid=True)),'os-baseline.json','diagnostics-compare.json'),
  S('VerifyNoCaptureOrInput',5,[],'isolation',E(videoTrackCount=0,frames=0,captureStartCount=0,nativeInputCount=0,inputEnabled=False,inputChannels=[]),'diagnostics-isolation.json'),
  S('CheckProviderPresentation',5,[],'presentation',provider_ui,'diagnostics-ui.json'),
  S('EndDiagnosticsSession',10,[end()],'audit',END,'audit.ndjson')),
 'C13': (
  S('OpenRunbookCatalog',140,[open_session(64)],'catalog',all_of(session(64,['control','runbook']),BOOK),'catalog.json','installed-book.json'),
  S('SelectAndCompareInstalledBook',10,[('select-book','network-summary','summary')],'preview',BOOK,'runbook-preview.json'),
  S('RequestBookOnce',5,[click('pc-a','runbook-send')],'request',E(requestCount=1,approvalWindowPresent=True),'runbook-request.json'),
  S('ApproveBookLocally',20,[('approve-book-local',)],'consent',all_of(BOOK,same('installedHash','dialogHash'),E(approveCount=1,workerBeforeCount=0)),'runbook-consent.json','worker-before.json'),
  S('ObserveOneWorkerCompletion',25,[],'worker',all_of(E(startCount=1,state='completed',exitCode=0,isSystem=False),same('session','baselineSession'),same('installedHash','executedHash'),same('userSid','baselineSid'),latency('startTick','exitTick',20000)),'worker-events.ndjson','runbook-result.json'),
  S('VerifyWorkerExitAndAudit',10,[end()],'end',all_of(E(exited=True),audit((1,0,1),(9,0,1),(10,3,1),(3,2,1))),'worker-after.json','audit.ndjson')),
 'C14': (
  S('OpenControlRunbookTarget',140,[open_session(67,'input-target')],'baseline',all_of(session(67,['control','input','pointer','runbook']),BOOK,E(workerStartCount=0,inputReady=True)),'target-before.json','catalog.json'),
  S('RequestBookAndObservePending',10,[('select-book','network-summary','summary'),click('pc-a','runbook-send')],'pending',E(inputEnabled=False,approvalWindowPresent=True,workerStartCount=0),'pending-ui.json','pending-events.ndjson'),
  S('CancelPendingBookOnce',5,[click('pc-a','runbook-cancel')],'cancel',all_of(E(cancelCount=1),same('requestId','pendingId')),'cancel-action.json'),
  S('ObserveRealDismissAck',10,[],'dismiss',all_of(same('requestId','ackId'),sequence('dismissSentTick','dialogClosedTick','dismissAckTick','resumedTick'),latency('dismissSentTick','dismissAckTick',2000),latency('dismissAckTick','resumedTick',2000),E(dialogPresent=False,machine='pc-b')),'dismiss-order.ndjson'),
  S('ObserveResumeAndNoWorker',5,[],'resume',all_of(E(workerStartCount=0,state='cancelled',inputReady=True),lambda d:require(get(d,'newEpoch')>get(d,'oldEpoch'),'epoch-not-advanced')),'runbook-cancelled.json','worker-events.ndjson'),
  S('ClickAfterCancelOnce',10,[('video-click','fixture-button')],'click',all_of(E(clickDelta=1),HELD),'target-final.json'),
  S('CollectCancelAudit',10,[end()],'audit',audit((1,0,1),(9,0,0),(10,2,1),(3,2,1)),'audit.ndjson')),
 'C15': (
  S('StartRelayRuntimes',90,[('start-relay-runtimes',)],'ready',E(allocationSourceReady=True,guardsReady=True),'turn-readiness.json'),
  S('VerifyEffectiveRelayPolicy',10,[],'policy',E(aPolicy='relay',bPolicy='relay',forceRelayEffective=True),'relay-settings.json'),
  S('ApproveRelayScreen',40,[('issue-pairing',),('join-viewer',),('grant',1)],'consent',session(1,['control']),'consent-ui.json'),
  S('ReadBothSelectedPairs',30,[],'pairs',selected_pairs,'viewer-selected-pair.json','host-selected-pair.json'),
  S('CorrelateCoturnAllocation',10,[],'allocation',relay_pair,'turn-allocation.json','relay-correlation.json'),
  S('ObserveRelayVideo',20,[],'video',all_of(video,lambda d:require(get(d,'bytesAfter')>get(d,'bytesBefore'),'relay-no-bytes')),'relay-video-samples.ndjson'),
  S('EndOwnedAllocation',20,[end()],'end',all_of(E(allocationClosed=True,sharedRelayStopped=False),END),'turn-after.json','audit.ndjson')),
 'C16': (
  S('OpenControlRecoveryTarget',140,[open_session(3,'input-target')],'baseline',all_of(session(3,['control','input','pointer']),HELD,E(recoveryBound=True,ownerHandlesBound=True,foreground=True)),'fault-owner.json','target-before.json'),
  S('ArmOneShotFault',10,[('arm-owned-fault',)],'armed',E(identityMatched=True,consumedCount=0,finiteDeadline=True,ready=True),'fault-armed.json'),
  S('SendRemoteF12Down',5,[('video-key','F12','down')],'down',all_of(E(nativeDownDelta=1),lambda d:require(get(d,'ackTick')<get(d,'nativeDownTick'),'reservation-order')),'reservation-order.ndjson','target-down.json'),
  S('ObserveVerifiedServiceCrash',5,[],'fault',all_of(E(consumedCount=1,identityMatched=True,f12HeldAtFault=True),latency('nativeDownTick','faultTick',500)),'fault-fired.json'),
  S('ObserveIndependentRecoveryUp',10,[],'recovery-order',fault_order,'recovery-order.ndjson','target-after.json'),
  S('ReleaseToolAndVerifyNoResume',10,[('release-input','pc-a')],'return',all_of(RETURN,E(newHelperCount=0,helperCount=0,adminHelperCount=0,workerCount=0,sessionEnded=True,serviceRestarted=False)),'tool-release.json','fault-after.json'),
  S('PreserveAndRecoverFaultResources',20,[],'end',all_of(E(serviceRestarted=False),END),'audit.ndjson','final-recovery.json')),
}
