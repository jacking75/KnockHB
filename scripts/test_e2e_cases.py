"""Offline tests: never construct OwnedGateway or connect to DTD/product/SSH."""
import copy
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from e2e_case_runtime import (Context, CaseError, Step, execute_steps, select_unique,
    read_json, new_json, live_blockers, bind_observation, assert_fields, elapsed,
    prepare_case, collect_saved, abort_case, digest, stop_and_recover)
from e2e_case_steps import (CASES, E, video, file_final, transfer, telemetry,
    provider_ui, relay_pair, fault_order, audit, FILE_HASH)
from e2e_case_adapters import Dtd, CaseBackend, inspect_file, map_video_point, read_audit


class Clock:
    def __init__(self): self.tick=1000000000
    def __call__(self): return self.tick
    def advance(self, seconds): self.tick += int(seconds*1e9)


class Sink:
    def __init__(self, fail_step=None, cleanup_failure=None, expire=False):
        self.actions=[]; self.recovery=[]; self.fail_step=fail_step
        self.cleanup_failure=cleanup_failure; self.expire=expire
    def begin(self,ctx,deadline): ctx.session_id='product-session';ctx.generation=1
    def check_guards(self,ctx,deadline):
        if self.expire: ctx.clock.advance(301)
    def action(self,action,ctx,deadline):
        key=ctx.active_step+':'+str(action)
        ctx.once(key,lambda _: self.record(action))
    def record(self,action): self.actions.append(action);return {'status':'completed'}
    def observe(self,source,ctx,deadline):
        if ctx.active_step==self.fail_step: raise CaseError('fixture-observation-failure',source)
        return {'runId':ctx.run_id,'caseId':ctx.case_id,'stepId':ctx.active_step,'source':source,
                'machine':'pc-b','sessionId':ctx.session_id,'connectionGeneration':ctx.generation,
                'tick':123,'frequency':1000,'available':True,'payload':{'ok':True}}
    def collect(self,files,ctx,deadline): pass
    def recover(self,operation,ctx,deadline):
        self.recovery.append(operation)
        if operation==self.cleanup_failure: raise CaseError('fixture-recovery-failure',operation)
        return {'status':'completed','resourcesAbsent':True,'verified':True}


class RuntimeTests(unittest.TestCase):
    def setUp(self):
        self.tmp=tempfile.TemporaryDirectory(prefix='ra-case-offline-')
        self.addCleanup(self.tmp.cleanup)
        self.root=Path(self.tmp.name)
    def ctx(self,case='C01',name='run'):
        folder=self.root/name;folder.mkdir()
        return Context(case,'a'*32,folder,Clock(),execution_mode='offline-fixture')
    def steps(self,case):
        # Keep real fixed actions/order/budgets; isolate executor from case predicates.
        return tuple(Step(s.name,s.seconds,s.actions,(('fixture',E(ok=True)),)) for s in CASES[case])
    def test_all_case_step_orders_and_first_failure_at_every_step(self):
        for case in CASES:
            steps=self.steps(case)
            for stop in range(len(steps)+1):
                with self.subTest(case=case,fail=stop):
                    ctx=self.ctx(case,f'{case}-{stop}')
                    sink=Sink(f'{stop:02d}' if stop else None)
                    result=execute_steps(ctx,sink,steps)
                    self.assertEqual('FAIL' if stop else 'PASS',result['result'])
                    expected=[a for s in steps[:stop or len(steps)] for a in s.actions]
                    self.assertEqual(expected,sink.actions)
                    self.assertEqual(stop-1 if stop else len(steps),len(ctx.done))
                    self.assertEqual('finish-remote',sink.recovery[0])
                    self.assertTrue((ctx.run_dir/'finish.json').exists())
                    self.assertEqual('offline-fixture',result['executionMode'])
    def test_cleanup_failures_never_hide_first_failure(self):
        for op in ('finish-remote','release-owned-input','stop-owned-runtime','restore-case-state','inspect-independent','retire-and-close'):
            ctx=self.ctx(name=op)
            result=execute_steps(ctx,Sink('01',op),self.steps('C01'))
            self.assertEqual('FAIL_RECOVERY_REQUIRED',result['result'])
            self.assertEqual('fixture-observation-failure',result['firstFailureStep']['code'])
    def test_timeout_stops_before_action_and_preserves_origin(self):
        ctx=self.ctx(); sink=Sink(expire=True)
        r=execute_steps(ctx,sink,self.steps('C03'))
        self.assertEqual([],sink.actions)
        self.assertEqual(1000000000,ctx.origin)
        self.assertEqual('deadline-expired',r['firstFailureStep']['code'])
    def test_duplicate_and_unknown_mutation_never_resubmits(self):
        ctx=self.ctx(); calls=[]
        def unknown(rid): calls.append(rid);return {'status':'completionUnknown'}
        with self.assertRaises(CaseError):ctx.once('click',unknown)
        with self.assertRaises(CaseError):ctx.once('click',unknown)
        self.assertEqual(1,len(calls))
        self.assertNotIn('arguments',ctx.ledger[0])
    def test_replay_does_not_cleanup_existing_owner(self):
        ctx=self.ctx();new_json(ctx.run_dir/'launch-once.json',{'runId':'old'})
        sink=Sink(); execute_steps(ctx,sink,self.steps('C01'))
        self.assertIsNone(ctx.origin);self.assertEqual([],sink.recovery)
    def test_missing_fields_and_bool_int_confusion(self):
        for data in ({},{'count':None},{'count':True}):
            with self.assertRaises(CaseError): assert_fields(data,{'count':1})
    def test_selector_ambiguity_owner_and_disabled(self):
        row={'name':'Approve','controlType':'Button','enabled':True,'pid':1,'imagePath':'x','createdAt':'t'}
        rule={'name':'Approve','controlType':'Button'}
        self.assertEqual(row,select_unique([row],rule,row))
        for rows in ([],[row,row],[dict(row,enabled=False)]):
            with self.assertRaises(CaseError):select_unique(rows,rule,row)
        with self.assertRaises(CaseError):select_unique([row],rule,dict(row,pid=2))
    def test_json_duplicate_oversize_and_unicode(self):
        path=self.root/'input.json'
        path.write_text('{"a":1,"a":2}')
        with self.assertRaises(CaseError):read_json(path)
        path.write_text(' '*1048577)
        with self.assertRaises(CaseError):read_json(path)
        path.write_text('{"text":"RA-한글😀"}',encoding='utf-8')
        self.assertEqual('RA-한글😀',read_json(path)['text'])
    def test_binding_wrong_run_generation_or_unavailable(self):
        ctx=self.ctx();sink=Sink();sink.begin(ctx,0);ctx.active_step='01'
        valid=sink.observe('fixture',ctx,0)
        self.assertEqual({'ok':True},bind_observation(valid,ctx,'01','fixture'))
        for k,v in [('runId','old'),('connectionGeneration',2),('available',False),('sessionId','old')]:
            with self.assertRaises(CaseError):bind_observation(dict(valid,**{k:v}),ctx,'01','fixture')
    def test_live_capability_gaps_cannot_be_configured_away(self):
        for case in CASES:self.assertGreater(len(live_blockers(case)),0)
        self.assertFalse(any('coturn' in x for x in live_blockers('C01')))
        self.assertTrue(any('EXCLUDED' in x for x in live_blockers('C15')))
    def test_prepare_freezes_without_opening_session_or_claiming_ready(self):
        ctx=Context('C01','a'*32,self.root/'prepared',Clock())
        class ReadOnlyDtd:
            def __init__(self):self.calls=[]
            def status(self,machine,c,deadline):
                self.calls.append(machine)
                return {'data':{'machineId':machine,'apiVersion':1,'desktop':'Default','inputAvailable':True,
                    'interactiveSessionId':1 if machine=='pc-a' else 2,
                    'supportedOperations':['windows','controls','focus','click','type-text','release-input']}}
        dtd=ReadOnlyDtd()
        with patch('e2e_case_runtime.freeze_inputs',return_value={'configSha256':'a'*64,'toolManifestSha256':'b'*64}):
            result=prepare_case({'a':{'session':1},'b':{'session':2}},'unused',ctx,dtd)
        self.assertEqual('BLOCKED',result['state']);self.assertEqual(0,result['functionalActions'])
        self.assertEqual(['pc-a','pc-b'],dtd.calls)
        self.assertIsNone(result['campaignSha256']);self.assertFalse((ctx.run_dir/'launch-once.json').exists())
    def test_collect_rehashes_and_never_confirms_current_recovery(self):
        ctx=self.ctx();p=ctx.run_dir/'evidence.json';new_json(p,{'runId':ctx.run_id})
        new_json(ctx.run_dir/'result.json',{'caseId':ctx.case_id,'runId':ctx.run_id,'result':'PASS',
            'evidence':[{'relativePath':p.name,'sha256':digest(p),'bytes':p.stat().st_size}]})
        p.write_text('{}')
        result=collect_saved(ctx)
        self.assertFalse(result['evidence'][0]['hashMatches']);self.assertFalse(result['recoveryVerified'])
    def test_abort_checks_owner_and_preserves_local_stop_on_remote_failure(self):
        ctx=self.ctx();new_json(ctx.run_dir/'launch-once.json',{'caseId':'C02','runId':ctx.run_id,'origin':123})
        with self.assertRaises(CaseError):abort_case(ctx,None)
        self.assertFalse((ctx.run_dir/'finish.json').exists())
        (ctx.run_dir/'launch-once.json').unlink()
        new_json(ctx.run_dir/'launch-once.json',{'caseId':'C01','runId':ctx.run_id,'origin':123})
        result=abort_case(ctx,None)
        self.assertTrue((ctx.run_dir/'finish.json').exists());self.assertEqual(123,ctx.origin)
        self.assertEqual('FAIL_RECOVERY_REQUIRED',result['state'])
    def test_stop_marker_write_failure_still_releases_input(self):
        ctx=self.ctx();ctx.begin();sink=Sink()
        with patch('e2e_case_runtime.new_json',side_effect=OSError('fixture-disk-failure')):
            stop_and_recover(ctx,sink)
        self.assertIn('release-owned-input',sink.recovery)
        self.assertFalse(ctx.recovery['verified'])


class PredicateTests(unittest.TestCase):
    def check(self,case,source,data):
        found=[p for s in CASES[case] for name,p in s.checks if name==source]
        self.assertEqual(1,len(found));found[0](data)
    def test_c01_stop_boundary_video_and_preconsent(self):
        d=dict(processExited=True,completeInventory=True,result='PASS',requestId='x',actionRequestId='x',requestedTick=0,observedTick=1000,frequency=1000)
        self.check('C01','stop',d)
        for changes in ({'observedTick':1001},{'completeInventory':False},{'actionRequestId':'other'}):
            with self.assertRaises(CaseError):self.check('C01','stop',dict(d,**changes))
        v={'samples':[{'frames':3,'width':800,'height':600},{'frames':4,'width':800,'height':600}], 'markerMatched':True,'monitorMatched':True}
        video(v)
        with self.assertRaises(CaseError):video(dict(v,markerMatched=None))
        with self.assertRaises(CaseError):self.check('C01','pre-consent',dict(framesMax=1,inputEnabled=False,inputChannels=[],videoWidth=0,observedWindowMs=5000))
    def test_c02_cumulative_frame_leak_and_real_deny(self):
        d=dict(totalFrames=0,nativeCount=0,featureChannelsCreated=[],joinVisible=True,consentWindowPresent=False,newOfferCount=0,newDataReadyCount=0,observedWindowMs=5000)
        self.check('C02','denied',d)
        with self.assertRaises(CaseError):self.check('C02','denied',dict(d,totalFrames=1))
        with self.assertRaises(CaseError):self.check('C02','pending',dict(pending=False,validPairing=False))
    def test_c03_unicode_wheel_reset(self):
        self.check('C03','text',{'text':'RA-한글😀'})
        with self.assertRaises(CaseError):self.check('C03','text',{'text':'RA-한글�'})
        self.check('C03','wheel',dict(wheelDeltaSum=120,wheelCount=1,observationValid=True))
        with self.assertRaises(CaseError):self.check('C03','wheel',dict(wheelCount=1,observationValid=True))
        d=dict(requestId='r',ackId='r',epoch=1,ackEpoch=1,requestedTick=0,ackTick=1000,frequency=1000,resetCount=1)
        self.check('C03','reset',d)
        for change in ({'ackTick':1001},{'ackEpoch':2},{'resetCount':2}):
            with self.assertRaises(CaseError):self.check('C03','reset',dict(d,**change))
    def test_c04_privileged_boundary_and_local_recovery_not_product_success(self):
        self.check('C04','admin-before',dict(privilegedStartCount=0,nativeInputCount=0))
        with self.assertRaises(CaseError):self.check('C04','admin-before',dict(privilegedStartCount=1,nativeInputCount=0))
        d=dict(desktop='Default',session=2,baselineSession=2,osHeld=0,sid='s',baselineSid='s',cancelledByViewer=True,secureTick=0,returnTick=20000,frequency=1000)
        self.check('C04','return',d)
        with self.assertRaises(CaseError):self.check('C04','return',dict(d,cancelledByViewer=False))
    def test_c05_screen_kind(self):
        d=dict(kind='Windows security options',desktop='Winlogon',actualScreenMatched=True)
        self.check('C05','screen',d)
        with self.assertRaises(CaseError):self.check('C05','screen',dict(d,kind='UAC'))
    def test_c06_wrong_sid_or_old_permission(self):
        d=dict(desktop='Default',session=2,baselineSession=2,osHeld=0,sid='s',baselineSid='s',oldAdminGenerationValid=False,inputEnabled=False,producerRestartCount=0)
        self.check('C06','return',d)
        for change in ({'sid':'other'},{'oldAdminGenerationValid':True},{'producerRestartCount':1}):
            with self.assertRaises(CaseError):self.check('C06','return',dict(d,**change))
    def test_c07_c08_chunk_hash_and_close(self):
        d=dict(chunkSeqs=list(range(16)),chunkLengths=[16384]*16,receivedBytes=262144,receiptMatched=True,completeCount=1,maximumIdleMsWithin10000=True,offerId='x',completeId='x',receiptId='x',streamClosed=True)
        self.check('C07','transfer',d);self.check('C08','transfer',d)
        for change in ({'receiptId':'wrong'},{'chunkSeqs':[0]*16},{'receivedBytes':1}):
            with self.assertRaises(CaseError):transfer(dict(d,**change))
        with self.assertRaises(CaseError):self.check('C08','transfer',dict(d,streamClosed=False))
        final=dict(length=262144,sha256=FILE_HASH,finalCount=1,partialCount=0)
        file_final(final)
        with self.assertRaises(CaseError):file_final(dict(final,partialCount=1))
        with self.assertRaises(CaseError):file_final(dict(final,sha256='0'*64))
    def test_c09_deny_not_timeout_or_bytes(self):
        d=dict(rejectCode='declined',acceptCount=0,binaryBytes=0,completeCount=0,receiptCount=0)
        self.check('C09','denied',d)
        with self.assertRaises(CaseError):self.check('C09','denied',dict(d,binaryBytes=1))
        with self.assertRaises(CaseError):self.check('C09','deny',dict(denyClicks=0,timeout=True))
    def test_c10_ui_not_just_sent_frame(self):
        d=dict(remoteLineEquals=True,remoteLineCount=1,renderMode='textContent',unicodeValid=True)
        self.check('C10','a-chat',d)
        for change in ({'remoteLineCount':2},{'unicodeValid':False},{'remoteLineEquals':None}):
            with self.assertRaises(CaseError):self.check('C10','a-chat',dict(d,**change))
    def test_c11_restore_and_response_deadline(self):
        self.check('C11','restored',dict(baselineRestored=True,privateContentExposed=False))
        with self.assertRaises(CaseError):self.check('C11','restored',dict(baselineRestored=False,privateContentExposed=False))
        d=dict(responseEqualsTestText=True,uiEqualsTestText=True,id='x',responseId='x',hash='h',responseHash='h',requestedTick=0,responseTick=2000,frequency=1000)
        self.check('C11','get',d)
        with self.assertRaises(CaseError):self.check('C11','get',dict(d,responseTick=2001))
    def test_c12_missing_capture_and_null_telemetry(self):
        d={'samples':[{'utc':'a','cpuPercent':0,'memory':{'totalBytes':100,'availableBytes':50}}, {'utc':'b','cpuPercent':100,'memory':{'totalBytes':100,'availableBytes':0}}], 'osTotalBytes':100}
        telemetry(d)
        bad=copy.deepcopy(d);bad['samples'][0]['memory']=None
        with self.assertRaises(CaseError):telemetry(bad)
        isolation=dict(videoTrackCount=0,frames=0,captureStartCount=0,nativeInputCount=0,inputEnabled=False,inputChannels=[])
        self.check('C12','isolation',isolation)
        with self.assertRaises(CaseError):self.check('C12','isolation',dict(isolation,captureStartCount=1))
        provider_ui({'failedProviders':[{'value':None,'display':'N/A'}],'sampleAgeMs':5001,'staleDisplaysDelayed':True})
    def test_c13_worker_count_privilege_and_installed_hash(self):
        d=dict(startCount=1,state='completed',exitCode=0,session=2,baselineSession=2,isSystem=False,installedHash='h',executedHash='h',userSid='s',baselineSid='s',startTick=0,exitTick=20000,frequency=1000)
        self.check('C13','worker',d)
        for change in ({'startCount':2},{'isSystem':True},{'executedHash':'wrong'}):
            with self.assertRaises(CaseError):self.check('C13','worker',dict(d,**change))
    def test_c14_ack_before_resume(self):
        d=dict(requestId='r',ackId='r',dismissSentTick=0,dialogClosedTick=1000,dismissAckTick=2000,resumedTick=2000,frequency=1000,dialogPresent=False,machine='pc-b')
        self.check('C14','dismiss',d)
        for change in ({'resumedTick':1999},{'dismissAckTick':2001,'resumedTick':2001},{'ackId':'wrong'}):
            with self.assertRaises(CaseError):self.check('C14','dismiss',dict(d,**change))
    def test_c15_selected_pair_not_candidate_presence(self):
        pair=dict(policy='relay',selected=True,localType='relay',remoteType='host',generation=1)
        d={'a':pair,'b':dict(pair),'generation':1,'allocationMatched':True,'allocationOwnerMatched':True}
        relay_pair(d)
        bad=copy.deepcopy(d);bad['a']['localType']='host'
        with self.assertRaises(CaseError):relay_pair(bad)
        with self.assertRaises(CaseError):relay_pair(dict(d,allocationMatched=False))
    def test_c16_fault_identity_order_and_tool_up(self):
        d=dict(machine='pc-b',consumedCount=1,identityMatched=True,f12HeldAtFault=True,toolUpCausedRecovery=False,nativeDownDelta=1,ackTick=0,nativeDownTick=1,faultTick=501,producerFencedTick=502,recoveryUpTick=3501,frequency=1000)
        fault_order(d)
        for change in ({'consumedCount':2},{'toolUpCausedRecovery':True},{'identityMatched':False},{'faultTick':502},{'recoveryUpTick':3502}):
            with self.assertRaises(CaseError):fault_order(dict(d,**change))
    def test_audit_numeric_enum_scope_count_and_operation(self):
        row=dict(Kind=8,Result=3,SessionId='s',ConnectionId='c',PermissionGeneration=1,Operation={'Id':'o'})
        d=dict(rows=[row],SessionId='s',ConnectionId='c',PermissionGeneration=1,operationId='o')
        audit((8,3,1))(d)
        for change in ({'rows':[row,row]},{'SessionId':'other'},{'operationId':'wrong'}):
            with self.assertRaises(CaseError):audit((8,3,1))(dict(d,**change))


class AdapterTests(unittest.TestCase):
    def test_missing_toggle_observation_sends_no_click(self):
        class Observer:
            def consent_states(self,ctx,deadline):return {'ConsentScreen':None}
        with tempfile.TemporaryDirectory() as tmp:
            ctx=Context('C01','a'*32,Path(tmp),Clock())
            backend=CaseBackend({},None,None,Observer())
            with patch.object(backend,'ui_click') as click:
                with self.assertRaises(CaseError):backend.grant(1,ctx,ctx.clock()+1000000000)
                click.assert_not_called()
    def test_grant_uses_actual_states_and_rechecks_before_approval(self):
        names=['ConsentScreen','ConsentControl','ConsentAdmin','consent-file','consent-clipboard',
               'consent-diagnostics','consent-runbook','consent-chat']
        class Observer:
            def __init__(self):self.calls=0
            def after_grant(self,ctx,deadline):self.calls+=1
            def consent_states(self,ctx,deadline):
                self.calls+=1
                return {n: n=='ConsentScreen' and self.calls==2 for n in names}
        with tempfile.TemporaryDirectory() as tmp:
            ctx=Context('C01','a'*32,Path(tmp),Clock());ctx.active_step='05'
            backend=CaseBackend({},None,None,Observer())
            with patch.object(backend,'ui_click') as click:
                backend.grant(1,ctx,ctx.clock()+1000000000)
                self.assertEqual(['ConsentScreen','ConsentApprove'],[c.args[1] for c in click.call_args_list])
    def test_unknown_live_operation_is_not_shell_fallback(self):
        with tempfile.TemporaryDirectory() as tmp:
            ctx=Context('C16','a'*32,Path(tmp),Clock())
            backend=CaseBackend({},None,None,None)
            with self.assertRaises(CaseError):backend.action(('arbitrary-shell','exit 0'),ctx,ctx.clock()+1000000000)
    def test_live_pass_requires_hashed_evidence_and_candidate(self):
        with tempfile.TemporaryDirectory() as tmp:
            ctx=Context('C01','a'*32,Path(tmp),Clock())
            result=execute_steps(ctx,Sink(),(Step('one',1,(),(('fixture',E(ok=True)),),('required.json',)),))
            self.assertNotEqual('PASS',result['result'])
    def test_dtd_original_request_query_no_retry_and_unicode(self):
        with tempfile.TemporaryDirectory() as tmp:
            ctx=Context('C10','a'*32,Path(tmp),Clock());ctx.active_step='02';calls=[]
            request={}
            def transport(exe,args,seconds,c):
                calls.append(args)
                if '--request' in args:
                    request.update(read_json(args[-1]));status='completionUnknown'
                else:status='completed'
                return dict(status=status,requestId=request['requestId'],controlSessionId='dtd')
            dtd=Dtd('unused','unused',transport);dtd.sessions['pc-a']='dtd'
            dtd.request('pc-a','type-text',{'windowId':'w','text':'RA-한글😀'},ctx,ctx.clock()+5_000_000_000)
            self.assertEqual('RA-한글😀',request['arguments']['text'])
            self.assertEqual(1,sum('--request' in a for a in calls))
            self.assertEqual(1,sum('--request-status' in a for a in calls))
            self.assertEqual([],list(Path(tmp).glob('request-*.json')))
    def test_real_file_hash_and_partial_detection(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);folder=root/'receive';folder.mkdir()
            (folder/'ra-c07.bin').write_bytes(bytes(range(256))*1024)
            file_final(inspect_file(str(folder),'ra-c07.bin',root))
            (folder/'partial').write_bytes(b'x')
            with self.assertRaises(CaseError):file_final(inspect_file(str(folder),'ra-c07.bin',root))
    def test_video_letterbox_and_capture_binding(self):
        p=map_video_point((960,540),dict(x=0,y=0,width=1920,height=1080),dict(x=10,y=20,width=1000,height=1000),dict(valid=True,captureId='c'))
        self.assertEqual(dict(x=510,y=520,captureId='c'),p)
        with self.assertRaises(CaseError):map_video_point((0,0),dict(x=0,y=0,width=1,height=1),dict(x=0,y=0,width=1,height=1),dict(valid=False,captureId='c'))


if __name__=='__main__':
    # Accidental process/network use anywhere in the tested path is a test failure.
    with patch('subprocess.Popen',side_effect=AssertionError('external process forbidden')), \
         patch('socket.socket',side_effect=AssertionError('network forbidden')):
        unittest.main(verbosity=2)
