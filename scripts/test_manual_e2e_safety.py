"""Offline tests; no Controller, SSH, product, registry or desktop calls."""
import importlib
import json
import os
from pathlib import Path
import shutil
import uuid
import unittest
from unittest.mock import patch

os.environ['REMOTEASSIST_E2E_RUN_ID']='20990101-000000'
desktop=importlib.import_module('manual_e2e_desktop')
connect=importlib.import_module('manual_e2e_connect')
watch=importlib.import_module('manual_e2e_watch')


class SafetyTests(unittest.TestCase):
    def setUp(self):
        self.tmp=desktop.ROOT/'.artifacts'/('manual-safety-'+uuid.uuid4().hex)
        self.out=self.tmp/desktop.RUN_ID
        self.patches=[patch.object(m,'OUT',self.out) for m in (desktop,connect,watch)]
        for p in self.patches:p.start()
        desktop.prepare()

    def tearDown(self):
        for p in self.patches:p.stop()
        if self.tmp.resolve().parent != (desktop.ROOT/'.artifacts').resolve():
            raise RuntimeError('Unexpected test cleanup path')
        shutil.rmtree(self.tmp)

    def state(self,end):
        (self.out/'run.json').write_text(json.dumps({'runId':desktop.RUN_ID,'activeEnd':end}),encoding='utf-8')

    def test_prepare_never_reuses_run(self):
        with self.assertRaises(FileExistsError):desktop.prepare()

    def test_deadline_and_failure_block_controller(self):
        self.state(0)
        with patch.object(desktop.subprocess,'run') as call:
            with self.assertRaises(RuntimeError):desktop.cli('--status')
            call.assert_not_called()
        self.state(1e20)
        (self.out/'failed.json').write_text('{}')
        with self.assertRaises(RuntimeError):desktop.remaining()

    def test_cleanup_allowed_after_deadline(self):
        self.state(0)
        with patch.object(desktop.subprocess,'run') as call:
            call.return_value.stdout='{"status":"completed"}'
            desktop.cli('--session-close','owned-session',cleanup=True)
            self.assertEqual(call.call_args.kwargs['timeout'],15)

    def test_initial_flow_never_clicks_b(self):
        (self.out/'viewer-window.txt').write_text('viewer')
        elements=[{'automationId':'PairingCode','name':'123456789'},
                  {'automationId':'PairingPin','name':'1234'}]
        with patch.object(connect,'window',return_value='tray'),patch.object(connect,'click') as click,patch.object(connect,'action',return_value={'data':{'elements':elements}}):
            connect.connect()
            self.assertTrue(all(call.args[0]=='a' for call in click.call_args_list))
        with self.assertRaises(RuntimeError):connect.consent()

    def test_watchdog_closes_even_if_ssh_fails(self):
        self.state(0)
        with patch.object(watch.subprocess,'run',side_effect=TimeoutError),patch.object(watch,'close_sessions') as close:
            watch.watch()
            close.assert_called_once()
        result=json.loads((self.out/'watchdog-result.json').read_text())
        self.assertFalse(result['desktopReturnVerified'])
        self.assertTrue(result['errors'])
        self.assertTrue((self.out/'failed.json').exists())


if __name__=='__main__':unittest.main()
