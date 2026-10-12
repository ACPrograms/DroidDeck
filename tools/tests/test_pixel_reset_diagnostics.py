"""Test freshness, narrow output, and lock handling without accessing a device."""
from datetime import datetime, timezone
import importlib.util
import json
from pathlib import Path
import subprocess
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('reset_projection', Path(__file__).resolve().parents[1] / 'pixel_reset_diagnostics.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class ResetProjectionTest(unittest.TestCase):
    def test_only_primary_state(self):
        self.assertEqual(module.primary_state('Private space secret\nStarted users state: [0=RUNNING_UNLOCKED, 10=STOPPED]'), 'RUNNING_UNLOCKED')
        self.assertEqual(module.primary_state('State: RUNNING_UNLOCKED'), 'unknown')

    def test_stale_body_not_used(self):
        raw = '2026-10-10 20:10:09 SYSTEM_LAST_KMSG (text, 1024 bytes)\nGNOC error type 0x2000'
        result = module.kernel_projection(raw, 'America/Toronto', datetime(2026, 10, 11, 21, tzinfo=timezone.utc))
        self.assertEqual(result['status'], 'stale_entry_not_attributed_to_current_trial')
        self.assertNotIn('categories', result)

    def test_latest_entry_and_no_arbitrary_body(self):
        raw = ('2026-10-10 20:10:09 SYSTEM_LAST_KMSG (text)\nGNOC error type 0x1111\n'
               '2026-10-11 17:35:01 SYSTEM_LAST_KMSG (text)\n'
               'PRIVATE_TEST_VALUE=a_secret_that_must_not_be_emitted\n'
               'Error from GNOC at addr 0x28220000\nGNOC error type 0x2000\n'
               'Reboot reason: 0xc005 - Secret arbitrary description\nLast boot reason: reset,warm,gnoc_error\n')
        result = module.kernel_projection(raw, 'America/Toronto', datetime(2026, 10, 11, 21, tzinfo=timezone.utc))
        self.assertEqual(result['entry_date_utc'], '2026-10-11T21:35:01+00:00')
        self.assertEqual(result['hardware_fields']['gnoc_error_types'], ['0x2000'])
        self.assertNotIn('0x1111', json.dumps(result))
        self.assertNotIn('a_secret', json.dumps(result))
        self.assertNotIn('arbitrary description', json.dumps(result))
        self.assertFalse(result['root_cause_proven'])

    def test_missing_entry_and_timezone(self):
        self.assertEqual(module.kernel_projection('Permission denied', 'America/Toronto')['status'], 'no_accessible_entry')
        self.assertEqual(module.kernel_projection('2026-10-11 17:00:00 SYSTEM_LAST_KMSG\nGNOC', 'Unknown/Region')['status'], 'unknown_device_timezone')

    def test_reason_rejects_paths_and_urls(self):
        self.assertEqual(module.safe_reason('reset,warm,gnoc_error\n'), 'reset,warm,gnoc_error')
        self.assertEqual(module.safe_reason('https://private.invalid/login?secret=abc'), 'unknown')
        self.assertEqual(module.safe_reason('/data/private'), 'unknown')

    def test_locked_skips_kernel_body(self):
        commands = []
        def run(argv, **kwargs):
            commands.append(argv)
            if argv[-2:] == ['dumpsys', 'user']:
                text = 'Started users state: [0=RUNNING_LOCKED]'
            elif argv[-1] == 'persist.sys.timezone':
                text = 'America/Toronto'
            elif argv[-3:] == ['get', 'global', 'boot_count']:
                text = '12'
            else:
                text = ''
            return subprocess.CompletedProcess(argv, 0, text, 'PRIVATE_STDERR')
        with patch.object(module.subprocess, 'run', side_effect=run):
            result = module.collect('adb', 'test-device', True)
        self.assertEqual(result['kernel']['status'], 'requires_user_unlock')
        self.assertFalse(any('--print' in command for command in commands))
        self.assertNotIn('PRIVATE_STDERR', json.dumps(result))

    def test_command_failure_does_not_export_stderr(self):
        with patch.object(module.subprocess, 'run', return_value=subprocess.CompletedProcess([], 1, 'UNRELATED_DATA', 'PRIVATE_STDERR')):
            result = module.collect('adb', 'test-device', True)
        self.assertIsNone(result['boot_count'])
        self.assertNotIn('UNRELATED_DATA', json.dumps(result))
        self.assertNotIn('PRIVATE_STDERR', json.dumps(result))


if __name__ == '__main__':
    unittest.main()
