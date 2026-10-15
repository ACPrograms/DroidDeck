import os
import shutil
import subprocess
import tempfile
import time
import unittest
from pathlib import Path


SOURCE = Path(__file__).resolve().parents[1] / 'linuxfs/overlay/usr/local/bin/droiddeck-steamvr'


class SteamVrHelperTest(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.home = self.root / 'home'
        self.runtime = self.root / 'runtime'
        self.commands = self.root / 'commands'
        self.driver = self.root / 'driver/droiddeck/bin/linuxarm64'
        self.steamvr = self.root / 'steamvr'
        for directory in (self.commands, self.driver, self.steamvr / 'bin/linuxarm64'):
            directory.mkdir(parents=True)
        self.helper = self.root / 'droiddeck-steamvr'
        self.helper.write_text(SOURCE.read_text(), encoding='utf-8')
        self.executable(self.commands / 'pgrep', 'exit 1')
        self.executable(self.commands / 'sleep', 'exit 0')
        self.executable(self.commands / 'xset', 'exit 0')
        self.executable(self.commands / 'setsid', 'exec "$@"')
        self.executable(self.driver / 'xr-display-mode', 'echo 3360x1760@120')
        self.executable(self.steamvr / 'bin/linuxarm64/vrstartup',
                        'echo "display=${DROIDDECK_VR_DISPLAY_MODE:-unset}"')
        (self.driver / 'driver_droiddeck.so').touch()
        self.environment = dict(os.environ)
        self.environment.pop('DROIDDECK_VR_DISPLAY_MODE', None)
        self.environment.pop('DROIDDECK_VR_PRESENT', None)
        self.environment.update(HOME=str(self.home), XDG_RUNTIME_DIR=str(self.runtime),
                                STEAMVR=str(self.steamvr), DROIDDECK_VR_DIR=str(self.root / 'driver'),
                                PATH=str(self.commands) + os.pathsep + os.environ['PATH'])

    def executable(self, destination, body):
        destination.write_text('#!/bin/sh\n' + body + '\n', encoding='utf-8')
        destination.chmod(0o755)

    def start(self):
        result = subprocess.run(['sh', str(self.helper), 'start'], env=self.environment,
                                capture_output=True, text=True, timeout=5)
        self.assertEqual(result.returncode, 0, result.stderr)
        log = self.home / '.local/share/Steam/logs/droiddeck-vrstartup.log'
        deadline = time.monotonic() + 2
        while time.monotonic() < deadline:
            if log.is_file() and 'display=' in log.read_text():
                return log
            time.sleep(0.01)
        self.fail('Startup log did not arrive')

    def test_fresh_runtime_mode_is_selected_and_log_survives_runtime_cleanup(self):
        log = self.start()
        self.assertEqual(log.read_text().strip(), 'display=3360x1760@120')
        self.assertEqual((self.runtime / 'droiddeck-steamvr/vrstartup.log').resolve(), log)
        shutil.rmtree(self.runtime)
        self.assertTrue(log.is_file())

    def test_explicit_display_override_is_preserved(self):
        self.environment['DROIDDECK_VR_DISPLAY_MODE'] = '1920x1080@90'
        self.assertEqual(self.start().read_text().strip(), 'display=1920x1080@90')

    def test_stale_or_missing_tracking_keeps_the_default(self):
        self.executable(self.driver / 'xr-display-mode', 'exit 1')
        self.assertEqual(self.start().read_text().strip(), 'display=unset')

    def test_older_runtime_without_the_reader_still_starts(self):
        (self.driver / 'xr-display-mode').unlink()
        self.assertEqual(self.start().read_text().strip(), 'display=unset')


if __name__ == '__main__':
    unittest.main()
