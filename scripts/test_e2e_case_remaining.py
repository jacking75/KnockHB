"""Regressions for actual partial-start, polling, provenance and recovery bugs.
Every external transport and OS boundary is replaced; no live machine is used.
"""
import copy
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch
from e2e_case_runtime import Context,CaseError,new_json,digest,elapsed,write_result,Step
from e2e_case_campaign import Campaign,package_campaign
from e2e_case_live import LiveObserver
from e2e_case_adapters import CaseBackend,map_video_point,ocr_point,OwnedGateway
from e2e_case_desktop import DesktopSources
from e2e_case_observations import Pending,invariants,derive,await_completion
from e2e_case_sources import read
from e2e_case_records import decode,sequence
from e2e_case_evidence import CHECKS,PRODUCERS,validate_measurements,inspect
from e2e_case_ui import rules
from e2e_case_ui_evidence import targets

class Remaining(unittest.TestCase):
    def setUp(self):
        self.tmp=tempfile.TemporaryDirectory();self.addCleanup(self.tmp.cleanup);self.root=Path(self.tmp.name)
        self.tick=1_000_000_000;self.ctx=Context('C01','a'*32,self.root,clock=lambda:self.tick)
    def test_deploy_partial_failure_preserves_primary_and_attempts_unprepare(self):
        new_json(self.root/'campaign.json',{'id':self.ctx.run_id,'caseId':'C01'})
        campaign=Campaign({'staging':'unused'},self.ctx,None);calls=[]
        def remote(script,*args,**kwargs):
            calls.append(script);raise CaseError('deploy-failure' if 'deploy.' in script else 'cleanup-failure','sanitized')
        with patch.object(campaign,'remote',side_effect=remote),self.assertRaises(CaseError) as error:campaign.deploy(self.tick+10**9)
        self.assertEqual('deploy-failure',error.exception.code)
        self.assertEqual(['e2e-case-deploy.ps1','e2e-case-unprepare.ps1'],calls)
        report=json.loads((self.root/'prepare-failure.json').read_text());self.assertEqual('cleanup-failure',report['cleanup']['error'])
    def test_begin_b_failure_keeps_a_session_for_cleanup(self):
        sessions={};closed=[]
        class Dtd:
            def open(_,machine,*args):
                if machine=='pc-b':raise CaseError('b-open-failed','sanitized')
                sessions[machine]='owned'
        dtd=Dtd();dtd.sessions=sessions
        observer=LiveObserver({'a':{'sid':'s','session':1}},SimpleNamespace(authorize=lambda:None),dtd,None)
        baseline={'available':True,'desktop':'Default','session':1,'activeSessionId':1,'sid':'s','heldVirtualKeys':[]}
        with patch.object(observer.desktop,'snapshot',return_value=baseline),self.assertRaises(CaseError):observer.begin(None,self.ctx,self.tick+10**9)
        self.assertEqual({'pc-a':'owned'},sessions);self.assertEqual({'pc-a':baseline},observer.baselines)
        dtd.close=lambda machine,*_:closed.append(machine)
        CaseBackend({},dtd,None,observer).recover('close-owned-sessions',self.ctx,self.tick+10**9)
        self.assertEqual(['pc-a'],closed)
    def test_clipboard_failure_does_not_skip_target_or_fault_cleanup(self):
        self.ctx.case_id='C11';observer=LiveObserver({},None,None,None);observer.runtime_started=True;observer.desktop.clipboard={}
        calls=[]
        with patch.object(observer,'restore_clipboard',side_effect=CaseError('restore-failed','sanitized')),patch.object(observer.desktop,'signal',side_effect=lambda n,*_:calls.append(n)),self.assertRaises(CaseError):
            observer.restore_case_state(self.ctx,self.tick+10**9)
        self.assertEqual(['target-finish','fault-stop'],calls)
    def test_missing_baseline_never_claims_recovery(self):
        observer=LiveObserver({},None,None,None)
        with self.assertRaises(CaseError):observer.independent_recovery(self.ctx,self.tick+10**9)
    def test_clipboard_mutation_is_blocked_until_entire_baseline_captured(self):
        self.ctx.case_id='C11';observer=LiveObserver({},None,None,None);backend=CaseBackend({},None,None,observer)
        with patch.object(backend,'ui_text') as action,self.assertRaises(CaseError):backend.action(('text','pc-a','clipboard-text','RA-CLIP😀'),self.ctx,self.tick+10**9)
        action.assert_not_called()
    def test_clipboard_restore_owner_and_ten_second_bound(self):
        self.ctx.case_id='C11';desktop=DesktopSources({},None,None)
        base={'runId':self.ctx.run_id,'caseId':'C11','pid':7,'createdAt':'t','frequency':1000}
        desktop.clipboard={'baseline':base}
        data={**base,'state':'restored','baselineRestored':True,'withinLimit':True,'comparisonVerified':True,'flushCompleted':True,'startedTick':1,'tick':10001}
        with patch.object(desktop,'bridge'),patch.object(desktop,'await_file',return_value=data):
            self.assertTrue(desktop.restore_clipboard(self.ctx,self.tick+10**9)['baselineRestored'])
        for changed in ({'runId':'b'*32},{'pid':8},{'tick':10002},{'comparisonVerified':False},{'state':'restoring'}):
            with patch.object(desktop,'bridge'),patch.object(desktop,'await_file',return_value={**data,**changed}),self.assertRaises(CaseError):desktop.restore_clipboard(self.ctx,self.tick+10**9)
    def test_clipboard_lost_launch_reply_still_receives_restore_signal(self):
        self.ctx.case_id='C11';self.ctx.resource('clipboard:pc-b','intent');observer=LiveObserver({},None,None,None);calls=[]
        with patch.object(observer.desktop,'signal',side_effect=lambda name,*_:calls.append(name)):
            observer.restore_case_state(self.ctx,self.tick+10**9)
        self.assertEqual(['clipboard-restore'],calls)
    def test_abort_saved_result_is_not_fresh_recovery(self):
        from e2e_case_recovery import finish_existing
        new_json(self.root/'launch-once.json',{'caseId':self.ctx.case_id,'runId':self.ctx.run_id,'origin':self.tick,'controllerPid':7})
        new_json(self.root/'result.json',{'caseId':self.ctx.case_id,'runId':self.ctx.run_id,'result':'PASS','recovery':{'verified':True}})
        with patch('e2e_case_adapters.Harness') as harness,patch('e2e_case_recovery.exists',return_value=False):
            result=finish_existing({},self.ctx,None)
        self.assertEqual('SAVED_RESULT_ONLY',result['state']);self.assertFalse(result['recovery']['verified'])
    def test_prepare_abort_without_launch_only_unprepares(self):
        from e2e_case_recovery import finish_existing
        with patch('e2e_case_campaign.Campaign') as campaign:
            campaign.return_value.remote.return_value={'status':'completed','preparedTasksRetired':True}
            result=finish_existing({},self.ctx,None)
        self.assertEqual('PREPARATION_RETIRED_NOT_EXECUTED',result['state']);self.assertEqual(0,result['functionalActions'])
        self.assertEqual('e2e-case-unprepare.ps1',campaign.return_value.remote.call_args.args[0])
    def test_cleanup_takeover_queries_same_request_without_replaying(self):
        from e2e_case_adapters import Dtd
        self.ctx.stopping=True;key='cleanup:release:pc-a';self.ctx.consumed.add(key)
        self.ctx.ledger=[{'operation':key,'status':'intent','requestId':'old-id'}];calls=[]
        def transport(exe,args,*_):
            calls.append(args);return {'status':'completed','requestId':'old-id','controlSessionId':'owned','inputReleased':True}
        dtd=Dtd('unused','unused',transport);dtd.sessions['pc-a']='owned'
        dtd.release('pc-a',self.ctx,self.tick+10**9,cleanup=True)
        self.assertEqual([['--config','unused','--request-status','owned','old-id']],calls)
    def test_screen_bound_has_separate_origin_and_is_stable(self):
        self.ctx.begin();self.tick+=2*10**9;self.ctx.screen_start();first=self.ctx.screen_start_tick
        self.tick+=10**9;self.ctx.screen_start();self.assertEqual(first,self.ctx.screen_start_tick)
        self.ctx.screen_end_tick=self.tick;result=write_result(self.ctx,())
        self.assertIsNone(result['screenOccupiedSeconds']);self.assertEqual(1,result['screenOccupiedUpperBoundSeconds'])
        self.assertEqual(3,result['actualSeconds']);self.assertEqual('bounded',result['screenMeasurement'])
    def test_deadline_and_function_sla_boundaries(self):
        self.ctx.begin()
        for phase in (300,540,840,900):
            self.tick=self.ctx.end(phase)-1;self.assertGreater(self.ctx.remaining(self.ctx.end(phase)),0)
            self.tick+=1
            with self.assertRaises(CaseError):self.ctx.remaining(self.ctx.end(phase))
        for bound in (500,1000,2000,3000):
            elapsed({'frequency':1000,'a':0,'b':bound},'a','b',bound)
            with self.assertRaises(CaseError):elapsed({'frequency':1000,'a':0,'b':bound+1},'a','b',bound)
    def test_tool_active_budget_does_not_consume_cleanup_reserve(self):
        for _ in range(320):self.ctx.tool_call('dtd')
        with self.assertRaises(CaseError):self.ctx.tool_call('dtd')
        self.ctx.stopping=True;self.ctx.tool_call('dtd');self.assertEqual(1,self.ctx.tool_calls['cleanup:dtd']['count'])
    def test_gateway_does_not_add_timeout_outside_caller_budget(self):
        import base64
        exe=self.root/'unused.exe';exe.write_bytes(b'unused')
        seen=[]
        def run(argv,**kwargs):
            request=json.loads(Path(argv[argv.index('-Request')+1]).read_text())
            seen.append((request['timeoutSeconds'],kwargs['timeout']))
            new_json(request['output'],{'exitCode':0,'stdout':base64.b64encode(b'{"status":"completed"}').decode()})
            return SimpleNamespace(returncode=0)
        with patch('e2e_case_adapters.subprocess.run',side_effect=run):OwnedGateway()(str(exe),[],5,self.ctx)
        self.assertEqual([(3,5)],seen)
    def test_c14_resume_after_ack_has_its_own_two_second_sla(self):
        from e2e_case_steps import CASES
        check=next(p for step in CASES['C14'] for source,p in step.checks if source=='dismiss')
        data=dict(requestId='r',ackId='r',dismissSentTick=0,dialogClosedTick=1,dismissAckTick=2,resumedTick=2002,frequency=1000,dialogPresent=False,machine='pc-b')
        check(data)
        with self.assertRaises(CaseError):check({**data,'resumedTick':2003})
    def test_producer_before_later_consent_cannot_be_hidden_by_arrival_order(self):
        trace=[{'kind':'consent-ui','tick':20,'metadata':{'mask':3}},{'kind':'native-input','tick':10,'metadata':{'count':1}}]
        with self.assertRaises(CaseError):invariants('C03',{},[],trace)
    def test_partial_ndjson_waits_but_completed_malformed_and_duplicate_fail(self):
        for raw in (b'{',b'{}'):
            with self.assertRaises(Pending):decode(raw,True)
        for raw in (b'{\n',b'{"x":1,"x":2}\n',b'{"x":NaN}\n',b'\xff\n'):
            with self.assertRaises(CaseError) as e:decode(raw,True)
            self.assertNotIsInstance(e.exception,Pending)
    def test_stale_duplicate_reverse_sequence_rejected(self):
        valid={'runId':self.ctx.run_id,'seq':1,'tick':1,'frequency':1000}
        self.assertEqual([valid],sequence([valid],self.ctx.run_id))
        for rows in ([valid,valid],[{**valid,'seq':2},valid],[{**valid,'runId':'b'*32}],[{**valid,'frequency':0}]):
            with self.assertRaises(CaseError):sequence(rows,self.ctx.run_id)
    def test_pending_poll_uses_same_deadline_and_violation_stops_before_later_success(self):
        observer=LiveObserver({},None,None,None);calls=[]
        def assembled(*args):
            calls.append(args[-1]);self.tick+=1
            if len(calls)==1:raise Pending('not-yet')
            raise CaseError('arrived-violation','sanitized')
        with patch('e2e_case_sources.assemble',side_effect=assembled),patch('e2e_case_sources.time.sleep'),self.assertRaises(CaseError) as error:
            read(observer,'video',self.ctx,self.tick+10**9)
        self.assertEqual('arrived-violation',error.exception.code);self.assertEqual(2,len(calls));self.assertEqual(calls[0],calls[1])
    def test_each_active_case_rejects_unauthorized_channels_before_pending(self):
        for n in (*range(1,15),16):
            with self.subTest(case=n),self.assertRaises(CaseError):invariants('C%02d'%n,{},[{'kind':'channel-created','label':'unauthorized'}],[])
    def test_direct_path_is_observed_not_assumed(self):
        with self.assertRaises(CaseError) as e:invariants('C01',{'selected':[{'localType':'relay','remoteType':'host'}]},[],[])
        self.assertEqual('direct-path-assumption-not-met',e.exception.code)
    def test_binary_after_deny_fails_immediately(self):
        events=[{'kind':'dc-received','message':{'t':'file-reject'}},{'kind':'dc-sent-binary','tick':3}]
        with self.assertRaises(CaseError):invariants('C09',{},events,[])
    def test_fixture_counts_wait_without_replaying(self):
        for case in ('C03','C14'):
            with self.assertRaises(Pending):await_completion(case,'click',{'browser':{},'events':[],'trace':[],'fixture':{'clicks':0},'fixtureBaseline':{'clicks':0}})
    def test_ui_owner_override_is_not_selector_override(self):
        base=rules({});changed=copy.deepcopy(base['pc-a']['screen']);changed['ownerRole']='tray'
        with self.assertRaises(CaseError):rules({'pc-a':{'screen':changed}})
        with self.assertRaises(CaseError):rules({'pc-a':{'screen':{**base['pc-a']['screen'],'command':'ignored'}}})
    def test_all_required_ui_targets_exist(self):
        selectors=rules({})
        for n in (*range(1,15),16):
            for machine,required in targets('C%02d'%n).items():self.assertTrue(required<=set(selectors[machine]))
    def test_negative_origin_letterbox_and_ocr_bounds(self):
        point=map_video_point((-960,540),dict(x=-1920,y=0,width=1920,height=1080),dict(x=10,y=20,width=960,height=960),dict(valid=True,captureId='c'))
        self.assertEqual({'x':490,'y':500,'captureId':'c'},point)
        for pos in ({'x':float('nan'),'y':50},{'x':101,'y':50},{'x':10,'y':10}):
            with self.assertRaises(CaseError):ocr_point(pos,{'width':100,'height':100,'captureId':'c'},{'x':20,'y':20,'width':60,'height':60})

    def evidence(self):
        config={'caseId':'C01','candidateSha256':'a'*64,'a':{'sid':'sid-a','session':1},'b':{'sid':'sid-b','session':2},'sourceEvidence':str(self.root/'absent.json')}
        original=self.root/'original.json';new_json(original,{'synthetic':'unit-only'})
        index={'schema':'remoteassist-e2e-measurements-v1','candidateSha256':'a'*64,'caseIds':['C01'],
               'toolHashes':['b'*64],'identity':{k:config[k] for k in ('a','b')},'measurements':[]}
        for name in CHECKS:
            record={'schema':'remoteassist-e2e-measurement-artifact-v1','check':name,'candidateSha256':'a'*64,
                'caseIds':['C01'],'toolHashes':['b'*64],'identity':index['identity'],'producer':PRODUCERS[name],
                'mode':'witnessed','outcome':'PASS','runId':'b'*32,'startTick':100,'endTick':500,'frequency':1000,
                'operatorWitnessed':True,'resourcesAbsent':True,'recoveryVerified':True,
                'phaseTicks':dict(zip(('origin','activeEnd','cleanupEnd','recoveryEnd','reportEnd'),(100,200,300,400,500))),
                'sourceEvidence':[{'artifact':str(original),'sha256':digest(original)}]}
            path=self.root/(name+'.json');new_json(path,record)
            index['measurements'].append({k:record[k] for k in ('check','producer','mode','outcome','runId','startTick','endTick','frequency')}|
                                         {'artifact':str(path),'sha256':digest(path)})
        return config,index
    def test_first_inspect_requires_no_fake_proof(self):
        config,_=self.evidence();self.assertEqual(list(CHECKS),inspect(config)['missing']);self.assertFalse(Path(config['sourceEvidence']).exists())
    def test_measured_phase_budgets_are_derived(self):
        config,index=self.evidence();checks,budget=validate_measurements(config,index,['b'*64])
        self.assertEqual(7,len(checks));self.assertEqual(.1,budget['activeSeconds']);self.assertEqual(.4,budget['totalSeconds'])
    def test_wrong_environment_candidate_tools_scope_and_synthetic_rejected(self):
        config,index=self.evidence()
        for key,value in [('candidateSha256','c'*64),('identity',{}),('toolHashes',[]),('caseIds',['C02'])]:
            with self.subTest(key=key),self.assertRaises(CaseError):validate_measurements(config,{**index,key:value},['b'*64])
        altered=copy.deepcopy(index);altered['measurements'][0]['mode']='synthetic'
        with self.assertRaises(CaseError):validate_measurements(config,altered,['b'*64])
    def test_original_source_tamper_and_terminal_missing_rejected(self):
        config,index=self.evidence()
        with self.assertRaises(CaseError):validate_measurements(config,{**index,'measurements':index['measurements'][:-2]},['b'*64])
        (self.root/'original.json').write_text('{}')
        with self.assertRaises(CaseError):validate_measurements(config,index,['b'*64])

if __name__=='__main__':
    with patch('subprocess.Popen',side_effect=AssertionError('external process forbidden')),patch('socket.socket',side_effect=AssertionError('network forbidden')):
        unittest.main(verbosity=2)
