from pathlib import Path
import hashlib
import os
import subprocess
import sys
import tempfile
import unittest
import zipfile


SCRIPT = Path(__file__).resolve().parents[1] / 'linuxfs/overlay/usr/local/bin/droiddeck-steam-x64'


class NativeX64Test(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.bin = self.root / 'bin'
        self.bin.mkdir()
        self.steam = self.root / '.local/share/Steam'
        self.stub('uname', 'case "$1" in -s) echo Linux ;; -m) echo x86_64 ;; esac\n')
        self.stub('curl', 'exit 22\n')
        if sys.platform == 'darwin':
            self.stub('sha256sum', 'exec /usr/bin/shasum -a 256 "$@"\n')
        self.env = dict(os.environ, HOME=str(self.root), BL_X64_NATIVE='1',
                        PATH=str(self.bin) + os.pathsep + os.environ['PATH'])
        self.env.pop('BL_X64_HOME', None)
        self.env.pop('XDG_DATA_HOME', None)

    def stub(self, name, body):
        p = self.bin / name
        p.write_text('#!/bin/bash\n' + body)
        p.chmod(0o755)

    def run_client(self, *args):
        return subprocess.run(['bash', str(SCRIPT), *args], env=self.env,
                              capture_output=True, text=True, timeout=10)

    def installed(self):
        (self.steam / 'steamrt64').mkdir(parents=True)
        (self.steam / 'package').mkdir()
        (self.steam / 'package/droiddeck-installed').write_text('1\n')
        for path in (self.steam / 'steam.sh', self.steam / 'steamrt64/steam'):
            path.write_text('#!/bin/bash\nprintf "%s\\n" "$@"\n')
            path.chmod(0o755)

    def test_native_launch_preserves_the_real_client_arguments(self):
        self.installed()
        result = self.run_client('--exec', '-nobigpicture', 'steam://rungameid/123', 'argument with spaces')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stdout.splitlines(), ['-nobigpicture', 'steam://rungameid/123', 'argument with spaces'])
        self.assertFalse((self.root / '.droiddeck-x64').exists())

    def test_existing_native_install_does_not_require_fex(self):
        self.installed()
        result = self.run_client('--prepare')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertTrue((self.steam / '.steam-enable-steamrt64-client').exists())
        self.assertFalse((self.root / 'rootfs').exists())

    def test_native_mode_rejects_a_non_x86_host(self):
        self.stub('uname', 'case "$1" in -s) echo Linux ;; -m) echo aarch64 ;; esac\n')
        result = self.run_client('--prepare')
        self.assertEqual(result.returncode, 64)
        self.assertIn('requires an x86-64 Linux host', result.stderr)
        self.assertFalse(self.steam.exists())

    def test_missing_native_client_is_not_reported_as_prepared(self):
        result = self.run_client('--prepare')
        self.assertEqual(result.returncode, 69)
        self.assertFalse((self.steam / 'package/droiddeck-installed').exists())

    def client_archive(self, corrupt=False):
        archive = self.root / 'client.zip'
        with zipfile.ZipFile(archive, 'w') as out:
            out.writestr('steam.sh', '#!/bin/bash\nexit 0\n')
            out.writestr('steamrt64/steam', '#!/bin/bash\nexit 0\n')
        digest = '0' * 64 if corrupt else hashlib.sha256(archive.read_bytes()).hexdigest()
        manifest = self.root / 'manifest'
        manifest.write_text('"version"\t"1"\n\t"bootstrap_all"\n\t{\n'
                            '\t\t"file"\t"client.zip"\n\t\t"sha2"\t"' + digest + '"\n\t}\n')
        self.env.update(TEST_MANIFEST=str(manifest), TEST_ARCHIVE=str(archive))
        self.stub('curl', 'url= out=\nwhile [ "$#" -gt 0 ]; do\n'
                         ' case "$1" in -o) out=$2; shift 2 ;; -*) shift ;; *) url=$1; shift ;; esac\ndone\n'
                         'case "$url" in */client.zip) cp "$TEST_ARCHIVE" "$out" ;; '
                         '*/steam_client_*) cp "$TEST_MANIFEST" "$out" ;; *) exit 22 ;; esac\n')

    def test_first_native_install_uses_the_original_checked_downloader(self):
        self.client_archive()
        result = self.run_client('--prepare')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual((self.steam / 'package/droiddeck-installed').read_text().strip(), '1')
        self.assertTrue(os.access(self.steam / 'steamrt64/steam', os.X_OK))

    def test_bad_download_checksum_cannot_become_an_install(self):
        self.client_archive(corrupt=True)
        result = self.run_client('--prepare')
        self.assertEqual(result.returncode, 69)
        self.assertFalse((self.steam / 'steamrt64/steam').exists())
        self.assertFalse((self.steam / 'package/droiddeck-installed').exists())

    def test_partial_install_with_client_executable_is_not_reported_as_ready(self):
        self.client_archive()
        manifest = self.root / 'manifest'
        with manifest.open('a') as output:
            output.write('\t"zzlate_steamrt64"\n\t{\n\t\t"file"\t"missing.zip"\n'
                         '\t\t"sha2"\t"' + '0' * 64 + '"\n\t}\n')
        result = self.run_client('--prepare')
        self.assertEqual(result.returncode, 69)
        self.assertTrue((self.steam / 'steamrt64/steam').exists())
        self.assertFalse((self.steam / 'package/droiddeck-installed').exists())

    def test_arm_specific_profile_and_tool_mapping_are_not_applied(self):
        self.installed()
        profile = self.steam / 'config/config.vdf'
        profile.parent.mkdir()
        profile.write_text('native-client-settings')
        for verb in ('--sync-login', '--share', '--map-direct', '--default-tool'):
            result = self.run_client(verb)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(result.stdout, '')
        self.assertEqual(profile.read_text(), 'native-client-settings')


if __name__ == '__main__':
    unittest.main()
