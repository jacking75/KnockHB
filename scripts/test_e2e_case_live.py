"""Input-free integration contracts for the real live source and dispatcher code."""
import ast
import base64
import json
import tempfile
import unittest
from types import SimpleNamespace
from pathlib import Path
from unittest.mock import patch
from e2e_case_runtime import Context,CaseError,new_json,digest
from e2e_case_live import LiveObserver
from e2e_case_observations import derive,Pending
from e2e_case_campaign import verify_candidate
from e2e_case_turn import observe_turn
from e2e_case_sources import read,assemble
from e2e_case_adapters import CaseBackend


class LiveContracts(unittest.TestCase):
    def setUp(self):
        self.tmp=tempfile.TemporaryDirectory();self.root=Path(self.tmp.name)
        self.ctx=Context('C01','a'*32,self.root);self.ctx.session_id='s';self.ctx.generation=2;self.ctx.active_step='01'
    def tearDown(self):self.tmp.cleanup()
    def state(self):
        return {'context':self.ctx,'browser':{},'events':[],'trace':[],'audit':[],'last':{},'connectionId':'c'}
    def test_product_decode_enforces_run_and_sequence(self):
        o=LiveObserver({},None,None,None)
        def encoded(rows):return {'trace-1-2.ndjson':base64.b64encode(b''.join(json.dumps(r).encode()+b'\n' for r in rows)).decode()}
        row={'sequence':1,'runId':self.ctx.run_id,'tick':7,'kind':'native-input'}
        with patch.object(o,'harness_data',return_value=encoded([row])):
            self.assertEqual([row],o.product(self.ctx,10)['trace'])
        for bad in ({**row,'sequence':2},{**row,'runId':'b'*32}):
            with patch.object(o,'harness_data',return_value=encoded([bad])),self.assertRaises(CaseError):o.product(self.ctx,10)
    def test_partial_trace_is_pending_not_zero_events(self):
        o=LiveObserver({},None,None,None)
        with patch.object(o,'harness_data',return_value={'trace-1-2.ndjson':base64.b64encode(b'{').decode()}),self.assertRaises(Pending):
            o.product(self.ctx,10)
    def test_preconsent_uses_observed_window_and_frames(self):
        s=self.state();s['browser']={'inputEnabled':False,'videoWidth':0};s['preConsent']={'framesMax':9,'observedWindowMs':5000}
        self.assertEqual(9,derive('C01','pre-consent',s)['framesMax'])
    def test_stop_uses_real_service_and_action_ids(self):
        s=self.state();s['service']=[{'kind':'local-stop-observation','observation':{'requestId':'observed'}}]
        s['trace']=[{'kind':'stop-ui','sessionId':'s','metadata':{'requestId':'clicked','frequency':10}}]
        value=derive('C01','stop',s)
        self.assertNotEqual(value['requestId'],value['actionRequestId'])
    def test_c16_down_requires_ack_same_process_revision(self):
        s=self.state();s['fixture']={};s['fixtureBaseline']={};s['fault']={'faultTick':30}
        down={'kind':'native-input','sessionId':'s','pid':5,'tick':20,'metadata':{'f12Down':1,'revision':4}}
        ack={'kind':'reservation-ack','sessionId':'s','pid':5,'tick':10,'metadata':{'revision':4}}
        s['trace']=[ack,down]
        self.assertEqual(10,derive('C16','down',s)['ackTick'])
        s['trace'][0]={**ack,'pid':6}
        with self.assertRaises(CaseError):derive('C16','down',s)
    def test_revoked_session_invalidates_last_active_admin_state(self):
        s=self.state();s['browser']={'joinVisible':True,'inputEnabled':False}
        s['events']=[{'kind':'dc-received','message':{'t':'admin-state','state':'active'}}]
        s['adminPids']=[];s['adminMappingValid']=False
        self.assertFalse(derive('C04','end',s)['generationValid'])
    def test_live_read_returns_current_bound_envelope(self):
        o=LiveObserver({},None,None,None);o.last['clipboard-baseline']={'captured':True}
        self.ctx.case_id='C11'
        r=read(o,'baseline',self.ctx,self.ctx.clock()+1000000000)
        self.assertEqual('s',r['sessionId']);self.assertEqual(2,r['connectionGeneration']);self.assertEqual({'captured':True},r['payload'])
    def test_candidate_windows_paths_and_uppercase_hash(self):
        payload=self.root/'payload';(payload/'child').mkdir(parents=True)
        (payload/'child/f').write_bytes(b'x')
        new_json(payload/'host.settings.json',{'SignalingUri':'https://192.168.0.61:25001/'})
        entries=[{'path':str(p.relative_to(payload)).replace('/','\\'),'size':p.stat().st_size,'sha256':digest(p).upper()} for p in payload.rglob('*') if p.is_file()]
        new_json(self.root/'candidate.json',{'kind':'UNSIGNED_DEVELOPMENT_NOT_RELEASE','files':entries})
        verify_candidate({'candidateDirectory':str(self.root),'caseId':'C01'})
        (payload/'child/f').write_bytes(b'y')
        with self.assertRaises(CaseError):verify_candidate({'candidateDirectory':str(self.root),'caseId':'C01'})
    def test_turn_requires_selected_address_and_session_owner(self):
        self.ctx.case_id='C15';path=self.root/'turn.json'
        evidence={'runId':self.ctx.run_id,'caseId':'C15','provider':'coturn','collectorReady':True,'relayRunning':True,
            'relayInstance':'relay','allocations':[{'relayAddress':'relay-ip','relayPort':27001,'sessionId':'s',
                'connectionGeneration':2,'runId':self.ctx.run_id,'allocationId':'allocation','sourceRecordId':'raw:1','state':'closed'}]}
        new_json(path,evidence)
        pair={'localType':'relay','localAddress':'relay-ip','localPort':27001,'remoteType':'host'}
        config={'caseInputs':{'turnEvidence':str(path),'relayInstance':'relay'}}
        result=observe_turn(config,self.ctx,{'selected':[pair]},[{'kind':'selected-pair','metadata':pair}],'end')
        self.assertTrue(result['allocationClosed']);self.assertFalse(result['sharedRelayStopped'])
        self.ctx.session_id='other'
        with self.assertRaises(CaseError):observe_turn(config,self.ctx,{'selected':[pair]},[{'kind':'selected-pair','metadata':pair}],'end')
    def test_every_observer_dispatch_method_exists(self):
        tree=ast.parse(Path(__file__).with_name('e2e_case_adapters.py').read_text(encoding='utf-8'))
        methods={n.func.attr for n in ast.walk(tree) if isinstance(n,ast.Call) and isinstance(n.func,ast.Attribute)
                 and isinstance(n.func.value,ast.Attribute) and n.func.value.attr=='observer'}
        for name in methods:self.assertTrue(callable(getattr(LiveObserver,name,None)),name)
    def test_external_abort_prevents_next_mutation(self):
        from e2e_case_adapters import Dtd
        dtd=Dtd('unused','unused',lambda *args:self.fail('must not dispatch'))
        dtd.sessions['pc-a']='owned';new_json(self.root/'finish.json',{'id':self.ctx.run_id})
        with self.assertRaises(CaseError):dtd.request('pc-a','click',{},self.ctx,self.ctx.clock()+1000000000)
    def source_observer(self,product,browser,events):
        config={'uiRules':{'pc-b':{'ConsentApprove':{'window':{'title':'consent'}},'runbook-approve':{'window':{'title':'book'}}}}}
        dtd=SimpleNamespace(request=lambda *args:{'data':{'windows':[]}})
        o=LiveObserver(config,None,dtd,None)
        o.backend=SimpleNamespace(owners={'pc-b':{'tray':{'pid':1,'imagePath':'tray','createdAt':'start'}}})
        o.baselines={'pc-b':{},'fixture':{}}
        o.browser=lambda ctx:browser;o.events=lambda ctx:events;o.product=lambda *args:product
        return o
    def test_book_consent_keeps_observed_dialog_hash(self):
        self.ctx.case_id='C13'
        o=self.source_observer({'trace':[],'product-inventory.json':{'processes':[]},'installed-book.json':{'hash':'installed'}},
            {'bookPreviewHash':'preview'},[])
        o.last['book-consent']={'dialogHash':'actual-dialog','workerBeforeCount':0}
        s=assemble(o,'consent',self.ctx,self.ctx.clock()+1000000000)
        self.assertEqual('actual-dialog',s['dialogBookHash'])
        self.assertNotEqual(s['dialogBookHash'],s['installedBookHash'])
    def test_scaled_video_matches_actual_monitor_identity(self):
        capture={'monitor':'device','x':0,'y':0,'width':1920,'height':1080}
        product={'trace':[{'kind':'consent-ui','metadata':{'mask':1}},{'kind':'capture-start','metadata':capture}],'product-inventory.json':{'processes':[]}}
        b={'tick':2,'frames':5,'videoWidth':960,'videoHeight':540,'markerMatched':True,'selected':[{'bytesReceived':20}]}
        messages=[{'message':{'t':'data-ready','monitor':{'identity':'device','width':1920,'height':1080}}}]
        o=self.source_observer(product,b,messages);o.fixture={'pid':5}
        o.desktop=SimpleNamespace(await_file=lambda *args:{'pid':5,'observationValid':True,'marker':{'id':self.ctx.run_id,'x':1,'y':1,'width':128,'height':64}})
        o.last['videoSamples']=[{'tick':1,'frames':3,'width':960,'height':540,'bytes':10}]
        s=assemble(o,'video',self.ctx,self.ctx.clock()+1000000000)
        self.assertTrue(s['videoMonitorMatched'])
        messages[0]['message']['monitor']['identity']='other-device'
        self.assertFalse(assemble(o,'video',self.ctx,self.ctx.clock()+1000000000)['videoMonitorMatched'])


if __name__=='__main__':
    with patch('subprocess.run',side_effect=AssertionError('No child processes in offline tests')),patch('socket.socket',side_effect=AssertionError('No network in offline tests')):
        unittest.main(verbosity=2)
