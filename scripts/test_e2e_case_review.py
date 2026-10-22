"""Regressions for RV01-RV05. Every machine/OS/process boundary is replaced."""
import contextlib
import copy
import io
import json
import runpy
import sys
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import e2e_case_runtime as runtime
from e2e_case_runtime import Context,CaseError,MASKS,new_json,digest,stop_and_recover,write_result
from e2e_case_observations import Pending,await_completion,invariants
from e2e_case_steps import CASES
from e2e_case_sources import read
from e2e_case_recovery import abort_existing
from e2e_case_adapters import CaseBackend,Harness
from e2e_case_desktop import DesktopSources


class ReviewRegressions(unittest.TestCase):
    def setUp(self):
        temporary=tempfile.TemporaryDirectory();self.addCleanup(temporary.cleanup)
        self.root=Path(temporary.name);self.tick=1_000_000_000
        self.run=self.root/'.artifacts'/('e2e-C01-'+'a'*32);self.run.mkdir(parents=True)
        self.ctx=Context('C01','a'*32,self.run,clock=lambda:self.tick)
        self.ctx.session_id='session';self.ctx.generation=1;self.ctx.active_step='01'

    def source(self):
        return {'browser':{'joinVisible':True},'events':[],'trace':[],
                'audit':[],'last':{},'context':self.ctx,'connectionId':'connection'}

    def test_file_decline_completes_before_later_host_stop(self):
        self.ctx.case_id='C09';s=self.source();s['browser']['joinVisible']=False
        s['events']=[{'kind':'dc-received','message':{'t':'file-reject','code':'declined'}}]
        observer=SimpleNamespace(last={})
        with patch('e2e_case_sources.assemble',return_value=s):
            result=read(observer,'denied',self.ctx,self.tick+10**9)
        predicate=CASES['C09'][-2].checks[0][1];predicate(result['payload'])
        self.assertEqual((('host-stop',),),CASES['C09'][-1].actions)
        with self.assertRaises(Pending):await_completion('C09','final',s)
        with self.assertRaises(Pending):await_completion('C02','denied',s)

    def test_every_non_screen_case_rejects_each_media_and_input_violation(self):
        for case in ('C02','C07','C08','C09','C10','C11','C12','C13'):
            trace=[{'kind':'consent-ui','tick':1,'metadata':{'mask':MASKS[int(case[1:])-1]}}]
            vectors=[({'frames':3},[],[]),({'srcObjectPresent':True},[],[]),
                     ({},[{'kind':'track','mediaKind':'video'}],[]),
                     ({},[],[{'kind':'capture-start','tick':2}]),
                     ({},[],[{'kind':'native-input','tick':2,'metadata':{'count':1}}]),
                     ({'inputEnabled':True},[],[])]
            for browser,events,extra in vectors:
                with self.subTest(case=case,browser=browser,events=events,extra=extra),self.assertRaises(CaseError):
                    invariants(case,browser,events,trace+extra)
            invariants(case,{'frames':0,'inputEnabled':False},[],trace)

    def test_screen_permission_does_not_grant_input(self):
        trace=[{'kind':'consent-ui','tick':1,'metadata':{'mask':1}},
               {'kind':'capture-start','tick':2}]
        invariants('C01',{'frames':3},[],trace)
        with self.assertRaises(CaseError):
            invariants('C01',{'frames':3},[],trace+[{'kind':'native-input','tick':3}])
        invariants('C03',{'frames':3,'inputEnabled':True},[],[
            {'kind':'consent-ui','tick':1,'metadata':{'mask':3}},
            {'kind':'capture-start','tick':2},{'kind':'native-input','tick':3}])

    def audit_rows(self):
        return [{'Kind':kind,'Result':result,'SessionId':'session','ConnectionId':'connection',
                 'PermissionGeneration':generation}
                for kind,result,generation in ((1,0,1),(3,2,0))]

    def test_input_and_clipboard_require_real_scoped_terminal_audit(self):
        for case in ('C03','C11'):
            self.ctx.case_id=case;s=self.source();s['audit']=self.audit_rows()
            step=CASES[case][-1]
            self.assertEqual('audit',step.checks[0][0]);self.assertIn('audit.ndjson',step.evidence)
            with patch('e2e_case_sources.assemble',return_value=s):
                record=read(SimpleNamespace(last={}), 'audit',self.ctx,self.tick+10**9)
            step.checks[0][1](record['payload'])
            for rows in ([s['audit'][1]],s['audit']+[s['audit'][1]],
                         [{**r,'ConnectionId':'other'} for r in s['audit']],
                         [{**s['audit'][0],'PermissionGeneration':2},s['audit'][1]],
                         [s['audit'][0],{**s['audit'][1],'Result':3}]):
                with self.subTest(case=case,rows=rows),self.assertRaises(CaseError),patch('e2e_case_sources.assemble',return_value={**s,'audit':rows}):
                    record=read(SimpleNamespace(last={}), 'audit',self.ctx,self.tick+10**9)
                    step.checks[0][1](record['payload'])

    def test_missing_terminal_audit_waits_only_to_original_deadline(self):
        for case in ('C03','C11'):
            self.ctx.case_id=case;s=self.source();deadline=self.tick+100
            def advance(_):self.tick+=100
            with patch('e2e_case_sources.assemble',return_value=s),patch('e2e_case_sources.time.sleep',side_effect=advance),self.assertRaises(CaseError) as error:
                read(SimpleNamespace(last={}), 'audit',self.ctx,deadline)
            self.assertEqual('deadline-expired',error.exception.code)
        self.assertEqual(1,sum(('host-stop',) in step.actions for step in CASES['C11']))
        self.assertEqual((('host-stop',),),CASES['C03'][-1].actions)

    def preparation(self):
        original=self.root/'config.json'
        config={'caseId':'C01','runId':self.ctx.run_id,'runDir':str(self.run),
                'candidateSha256':'b'*64,'candidateDirectory':str(self.root/'missing-candidate'),
                'sourceEvidence':str(self.root/'missing-readiness.json'),
                'staging':'C:/Users/jacking75NoteBook/RemoteAssist-E2E-123',
                'a':{'machine':'pc-a','sid':'a','session':1},'b':{'machine':'pc-b','sid':'b','session':2},
                'dtdExe':'F:/github/DesktopTestDriver/bin/live_Release/Controller/Controller.exe',
                'dtdConfig':str(self.root/'missing-dtd.json'),'uiRules':{}}
        new_json(original,config);saved=self.run/'recovery-config.json';new_json(saved,config)
        new_json(self.run/'frozen-inputs.json',{'caseId':'C01','runId':self.ctx.run_id,
            'configPath':str(original),'configSha256':digest(original),'recoveryConfigSha256':digest(saved)})
        new_json(self.run/'campaign.json',{'version':4,'id':self.ctx.run_id,'caseId':'C01','localRun':str(self.run),
            'staging':config['staging'],'candidateSha256':config['candidateSha256'],'identity':{'a':config['a'],'b':config['b']}})
        new_json(self.run/'prepared.json',{'caseId':'C01','runId':self.ctx.run_id,'campaignSha256':digest(self.run/'campaign.json')})
        new_json(self.run/'launch-once.json',{'caseId':'C01','runId':self.ctx.run_id,'origin':1,
            'frequency':10**9,'controllerPid':7})
        return original,config

    def test_abort_uses_snapshot_when_current_config_candidate_or_evidence_is_missing(self):
        original,config=self.preparation();original.unlink()
        def finished(saved,ctx,transport):
            self.assertTrue((self.run/'finish.json').exists())
            self.assertEqual(config['staging'],saved['staging']);self.assertEqual(1,ctx.origin)
            return {'state':'ABORTED_RECOVERED','functionalActions':0}
        with patch('e2e_case_recovery.ROOT',self.root),patch('e2e_case_recovery.finish_existing',side_effect=finished) as finish:
            result=abort_existing('C01',str(self.run),str(original),None)
            original.write_text('changed and invalid JSON')
            second=abort_existing('C01',str(self.run),str(original),None)
        self.assertEqual('ABORTED_RECOVERED',result['state']);self.assertEqual(result,second)
        self.assertEqual(2,finish.call_count)

    def test_abort_cli_bypasses_execution_validation_including_excluded_case(self):
        for case in ('C01','C15'):
            with patch.object(runtime,'read_config',side_effect=AssertionError('execution validation forbidden')), \
                 patch('e2e_case_recovery.abort_existing',return_value={'state':'ABORTED_RECOVERED'}) as abort, \
                 contextlib.redirect_stdout(io.StringIO()):
                code=runtime.main(['--case',case,'--action','Abort','--run-dir','bound-run','--config','missing'])
            self.assertEqual(20,code);abort.assert_called_once()

    def test_abort_tampered_recovery_snapshot_still_stops_locally_but_does_not_dispatch(self):
        original,_=self.preparation();(self.run/'recovery-config.json').write_text('{}')
        with patch('e2e_case_recovery.ROOT',self.root),patch('e2e_case_recovery.finish_existing') as finish:
            result=abort_existing('C01',str(self.run),str(original),None)
        self.assertEqual('FAIL_RECOVERY_REQUIRED',result['state']);self.assertTrue(result['stopRequested'])
        self.assertEqual('abort-recovery-config-changed',result['code']);finish.assert_not_called()

    def test_abort_wrong_case_cannot_signal_or_dispatch(self):
        original,_=self.preparation()
        with patch('e2e_case_recovery.ROOT',self.root),patch('e2e_case_recovery.finish_existing') as finish,self.assertRaises(CaseError):
            abort_existing('C16',str(self.run),str(original),None)
        self.assertFalse((self.run/'finish.json').exists());finish.assert_not_called()

    def test_abort_older_preparation_accepts_only_unchanged_original_config(self):
        original,_=self.preparation();(self.run/'recovery-config.json').unlink()
        with patch('e2e_case_recovery.ROOT',self.root),patch('e2e_case_recovery.finish_existing',return_value={'state':'ABORTED_RECOVERED'}) as finish:
            self.assertEqual('ABORTED_RECOVERED',abort_existing('C01',str(self.run),str(original),None)['state'])
            original.write_text('{}')
            self.assertEqual('FAIL_RECOVERY_REQUIRED',abort_existing('C01',str(self.run),str(original),None)['state'])
        self.assertEqual(1,finish.call_count)

    def test_expired_safety_calls_are_bounded_and_never_turn_into_pass(self):
        self.ctx.origin=self.tick;self.tick+=901*10**9
        self.ctx.first_failure={'stepId':'01','code':'original-failure'};calls=[]
        def recover(operation,ctx,deadline):
            self.assertGreater(deadline,self.tick);self.assertLessEqual(deadline-self.tick,10*10**9)
            ctx.remaining(deadline);calls.append(operation);self.tick+=10**8
            return {'status':'completed','resourcesAbsent':True,'verified':True}
        stop_and_recover(self.ctx,SimpleNamespace(recover=recover))
        self.assertIn('finish-remote',calls);self.assertIn('release-owned-input',calls)
        self.assertIn('restore-case-state',calls);self.assertIn('close-owned-sessions',calls)
        self.assertEqual(1_000_000_000,self.ctx.origin)
        self.assertEqual('original-failure',self.ctx.first_failure['code'])
        result=write_result(self.ctx,())
        self.assertEqual('FAIL_RECOVERY_REQUIRED',result['result']);self.assertGreater(result['actualSeconds'],900)

    def test_feature_and_interactive_probe_cannot_use_recovery_grant(self):
        self.ctx.stopping=True;self.ctx.origin=self.tick
        with self.assertRaises(CaseError):CaseBackend({},None,None,None).action(('start-runtimes',),self.ctx,self.tick+10**9)
        for operation in ('uac-trigger','fault-guard','clipboard-guard'):
            with self.subTest(operation=operation),self.assertRaises(CaseError):
                DesktopSources({},None,None).launch_probe('pc-b',operation,self.ctx,self.tick+10**9)
        self.ctx.stopping=False
        with self.assertRaises(CaseError):self.ctx.recovery_deadline(900,10)

    def test_recovery_transport_allows_only_fixed_read_and_stop_actions(self):
        path=self.run/'campaign.json';new_json(path,{'id':self.ctx.run_id,'localRun':str(self.run)})
        self.ctx.stopping=True;captured=[]
        with patch('e2e_case_adapters.ROOT',self.root):
            harness=Harness(str(path),lambda exe,args,*_:captured.append(args))
            for action in ('finish','status','collect','evidence'):harness.call(action,self.ctx,self.tick+10**9)
            with self.assertRaises(CaseError):harness.call('arm',self.ctx,self.tick+10**9)
        self.assertTrue(all('--recovery-only' in args for args in captured));self.assertEqual(4,len(captured))

    def test_prepare_and_run_share_one_lifecycle_sequence(self):
        self.ctx.resource('prepared:remote','intent');self.ctx.resource('prepared:remote','acquired','campaign.json')
        resumed=Context('C01',self.ctx.run_id,self.run,clock=lambda:self.tick)
        resumed.resource('dtd:pc-a','intent');resumed.resource('dtd:pc-a','acquired','dtd-pc-a.json')
        from e2e_case_records import decode,sequence
        rows=sequence(decode((self.run/'lifecycle.ndjson').read_bytes(),True),self.ctx.run_id)
        self.assertEqual([1,2,3,4],[row['seq'] for row in rows])
        self.assertEqual('prepared:remote',resumed.lifecycle[0]['resource'])

    def test_run_rejects_tampered_recovery_snapshot_before_any_live_action(self):
        self.preparation();(self.run/'recovery-config.json').write_text('{}')
        (self.run/'launch-once.json').unlink()
        from e2e_case_readiness import verify_frozen
        frozen=runtime.read_json(self.run/'frozen-inputs.json')
        with patch.object(runtime,'freeze_inputs',return_value=frozen), \
             patch('e2e_case_readiness.readiness') as readiness,self.assertRaises(CaseError) as error:
            verify_frozen({},'unused',self.ctx)
        self.assertEqual('prepared-recovery-config-changed',error.exception.code);readiness.assert_not_called()

    def test_stopped_or_already_launched_preparation_cannot_run_again(self):
        from e2e_case_readiness import verify_frozen
        for name,value in (('finish.json',{'id':self.ctx.run_id}),('launch-once.json',{'runId':self.ctx.run_id})):
            path=self.run/name;new_json(path,value)
            with patch.object(runtime,'freeze_inputs') as freeze,self.assertRaises(CaseError) as error:
                verify_frozen({},'unused',self.ctx)
            self.assertEqual('prepared-run-stopped-or-consumed',error.exception.code);freeze.assert_not_called()
            path.unlink()

    def test_failure_before_runtime_retires_prepared_tasks_after_independent_return(self):
        from e2e_case_live import LiveObserver
        dtd=SimpleNamespace(close=lambda *args:closed.append(args[0]));closed=[]
        campaign=SimpleNamespace(remote=lambda *args:{'status':'completed'})
        observer=LiveObserver({},campaign,dtd,None)
        snapshots={m:{'available':True,'desktop':'Default','sid':m,'session':i,'activeSessionId':i,
                       'heldVirtualKeys':[],'_sourceName':'current.json'} for i,m in enumerate(('pc-a','pc-b'),1)}
        observer.baselines=copy.deepcopy(snapshots)
        with patch.object(observer.desktop,'snapshot',side_effect=lambda machine,*_:snapshots[machine]), \
             patch.object(observer.desktop,'stop_probes'):
            result=observer.independent_recovery(self.ctx,self.tick+10**9)
            self.assertTrue(result['verified']);self.assertEqual(snapshots,observer.last['recovery'])
            observer.retire(self.ctx,self.tick+10**9)
        self.assertEqual(['pc-a','pc-b'],closed)
        self.assertEqual('released',self.ctx.lifecycle[-1]['state'])

    def test_overdue_product_client_reads_evidence_only_with_recovery_scope(self):
        # Exercise the actual downstream CLI under a stub transport, no SSH/OS.
        with tempfile.TemporaryDirectory(prefix='offline-client-',dir=runtime.ROOT/'.artifacts') as directory:
            root=Path(directory);manifest=root/'campaign.json'
            new_json(manifest,{'id':'c'*32,'localRun':str(root),'staging':'unused'})
            new_json(root/'local-start.json',{'id':'c'*32,'deadline':1,'frequency':1000})
            calls=[]
            def transport(argv,**kwargs):
                calls.append(argv)
                output=Path(argv[argv.index('--output')+1]);new_json(output,{'stdout':'{}'})
                return SimpleNamespace(returncode=0)
            script=str(runtime.ROOT/'scripts/e2e-product-client.py')
            for action in ('status','evidence'):
                with patch.object(sys,'argv',[script,action,'--campaign',str(manifest),'--recovery-only']), \
                     patch('subprocess.run',side_effect=transport),contextlib.redirect_stdout(io.StringIO()):
                    runpy.run_path(script,run_name='__main__')
            self.assertEqual(2,len(calls))
            for action in ('status','arm'):
                args=[script,action,'--campaign',str(manifest)]+(['--recovery-only'] if action=='arm' else [])
                with patch.object(sys,'argv',args),patch('subprocess.run') as blocked, \
                     contextlib.redirect_stdout(io.StringIO()),self.assertRaises(SystemExit):
                    runpy.run_path(script,run_name='__main__')
                blocked.assert_not_called()


if __name__=='__main__':
    with patch('subprocess.Popen',side_effect=AssertionError('external process forbidden')), \
         patch('socket.socket',side_effect=AssertionError('network forbidden')):
        unittest.main(verbosity=2)
