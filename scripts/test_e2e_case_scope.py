"""Input-free checks for the user-selected campaign scope."""
import contextlib
import io
import json
import unittest
from unittest.mock import patch

import e2e_case_runtime as runtime
from e2e_case_readiness import prerequisites


class ScopeTests(unittest.TestCase):
    def test_turn_excluded_before_config_or_external_actions(self):
        for action in ('Validate', 'Prepare', 'Run'):
            with self.subTest(action=action), patch.object(runtime, 'read_config', side_effect=AssertionError('config accessed')):
                output = io.StringIO()
                args = ['--case', 'C15', '--action', action, '--run-dir', 'unused', '--config', 'missing']
                if action == 'Run':
                    args.append('--execute')
                with contextlib.redirect_stdout(output):
                    code = runtime.main(args)
                self.assertEqual(code, 10)
                result = json.loads(output.getvalue())
                self.assertEqual(result['state'], 'EXCLUDED')
                self.assertEqual(result['functionalActions'], 0)

    def test_turn_cannot_bypass_cli_scope_through_readiness(self):
        with self.assertRaises(runtime.CaseError) as error:
            prerequisites({'caseId': 'C15'})
        self.assertEqual(error.exception.code, 'turn-excluded-20261022')

    def test_historical_recovery_and_other_cases_still_reach_config(self):
        for case, action in (('C15', 'Abort'), ('C15', 'Collect'), ('C01', 'Validate'), ('C16', 'Validate')):
            with patch.object(runtime, 'read_config', side_effect=runtime.CaseError('scope-test', '', exit_code=10)) as read:
                with contextlib.redirect_stdout(io.StringIO()):
                    runtime.main(['--case', case, '--action', action, '--run-dir', 'unused', '--config', 'missing'])
                read.assert_called_once()


if __name__ == '__main__':
    unittest.main()
