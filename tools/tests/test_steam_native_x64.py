from pathlib import Path
import hashlib
import os
import subprocess
import socket
import shlex
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
        (self.steam / 'package/steam_client_steamrt64.manifest').write_text('\"version\" \"1\"\n')
        for path in (self.steam / 'steam.sh', self.steam / 'steamrt64/steam'):
            path.write_text('#!/bin/bash\nprintf "%s\\n" "$@"\n')
            path.chmod(0o755)

    def test_native_launch_preserves_the_real_client_arguments(self):
        self.installed()
        result = self.run_client('--exec', '-nobigpicture', 'steam://rungameid/123', 'argument with spaces')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stdout.splitlines(), ['-nobigpicture', 'steam://rungameid/123', 'argument with spaces'])
        self.assertFalse((self.root / '.droiddeck-x64').exists())

    def test_native_software_renderer_clears_mobile_runtime_overrides(self):
        self.installed()
        self.env.update(STEAM_RUNTIME='1', STEAM_STEAMRT_RECURSION_GUARD='1')
        (self.steam / 'steam.sh').write_text(
            '#!/bin/bash\nprintf "%s\\n" "$LIBGL_ALWAYS_SOFTWARE" "$GALLIUM_DRIVER" "$LIBGL_DRI3_DISABLE" "$STEAM_RUNTIME" "$STEAM_STEAMRT_RECURSION_GUARD"\n')
        result = self.run_client('--exec')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stdout.splitlines(), ['1', 'llvmpipe', '1', '', ''])

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
        self.env.update(TEST_MANIFEST=str(manifest), TEST_ARCHIVE=str(archive),
                        TEST_REQUESTS=str(self.root / "public-requests"))
        self.stub('curl', 'url= out=\nwhile [ "$#" -gt 0 ]; do\n'
                         ' case "$1" in -o) out=$2; shift 2 ;; -*) shift ;; *) url=$1; shift ;; esac\ndone\n'
                         'printf \"%s\\n\" \"$url\" >> \"$TEST_REQUESTS\"\n'
                         'case "$url" in */client.zip) cp "$TEST_ARCHIVE" "$out" ;; '
                         '*/steam_client_*) cp "$TEST_MANIFEST" "$out" ;; *) exit 22 ;; esac\n')

    def test_first_native_install_uses_the_original_checked_downloader(self):
        self.client_archive()
        result = self.run_client('--prepare')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual((self.steam / 'package/droiddeck-installed').read_text().strip(), '1')
        self.assertTrue(os.access(self.steam / 'steamrt64/steam', os.X_OK))
        self.assertIn('/steam_client_steamrt64\n', (self.root / 'public-requests').read_text())

    def test_native_beta_opt_in_keeps_original_channel(self):
        self.client_archive()
        self.env['BL_NATIVE_STEAM_STABLE'] = '0'
        result = self.run_client('--prepare')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn('/steam_client_steamdeck_publicbeta_steamrt64',
                      (self.root / 'public-requests').read_text())

    def test_native_session_stable_channel_does_not_change_legacy_launch(self):
        source = (SCRIPT.parent / 'droiddeck-session').read_text()
        block = source[source.index('    steam_channel=steamdeck_publicbeta'):source.index('    _bl_lang=()')]
        for native, stable, expected in [('1', '1', ['0']), ('1', '0', ['2', '-clientbeta', 'steamdeck_publicbeta']),
                                          ('0', '1', ['2', '-clientbeta', 'steamdeck_publicbeta'])]:
            env = dict(self.env, BL_X64_NATIVE=native, BL_NATIVE_STEAM_STABLE=stable)
            command = 'steam_x64=1\n' + block + '\nprintf "%s\\n" "${#_bl_channel[@]}"; if [ "${#_bl_channel[@]}" -gt 0 ]; then printf "%s\\n" "${_bl_channel[@]}"; fi'
            result = subprocess.run(['bash', '-c', command], env=env, capture_output=True, text=True, timeout=10)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(result.stdout.splitlines(), expected)

    def test_native_cef_timeout_probe_is_opt_in_and_leaves_legacy_unchanged(self):
        source = (SCRIPT.parent / 'droiddeck-session').read_text()
        block = source[source.index('    _bl_ui=(-gamepadui)'):source.index('    if [ "${BL_STEAM_UI:-}" != desktop ]; then')]
        for native, probe in [('0', '0'), ('0', '1'), ('1', '0'), ('1', '1')]:
            env = dict(self.env, BL_X64_NATIVE=native, BL_NATIVE_CEF_DISABLE_HANG_TIMEOUTS=probe,
                       BL_DESKTOP='1', BL_STEAM_UI='desktop')
            result = subprocess.run(['bash', '-c', block + '\nprintf "%s\\n" "${_bl_cef[@]}"'],
                                    env=env, capture_output=True, text=True, timeout=10)
            self.assertEqual(result.returncode, 0, result.stderr)
            expected = ['-cef-disable-gpu']
            if native == probe == '1':
                expected.append('-cef-disable-hang-timeouts')
            self.assertEqual(result.stdout.splitlines(), expected)

    def test_native_cef_gpu_probe_keeps_legacy_software_default(self):
        source = (SCRIPT.parent / 'droiddeck-session').read_text()
        block = source[source.index('    _bl_ui=(-gamepadui)'):source.index('    if [ "${BL_STEAM_UI:-}" != desktop ]; then')]
        for native, gpu in [('0', '0'), ('0', '1'), ('1', '0'), ('1', '1')]:
            env = dict(self.env, BL_X64_NATIVE=native, BL_NATIVE_CEF_GPU=gpu,
                       BL_NATIVE_CEF_DISABLE_HANG_TIMEOUTS='0', BL_DESKTOP='1', BL_STEAM_UI='desktop')
            result = subprocess.run(['bash', '-c', block + '\nprintf "%s\\n" "${_bl_cef[@]}"'],
                                    env=env, capture_output=True, text=True, timeout=10)
            self.assertEqual(result.returncode, 0, result.stderr)
            expected = ['-cef-disable-gpu']
            if native == gpu == '1':
                expected = ['-cef-force-gpu', '-cef-use-gl=angle', '-cef-use-angle=vulkan']
            self.assertEqual(result.stdout.splitlines(), expected)

    def test_native_single_process_probe_is_opt_in_and_keeps_legacy_default(self):
        source = (SCRIPT.parent / 'droiddeck-session').read_text()
        block = source[source.index('    _bl_ui=(-gamepadui)'):source.index('    if [ "${BL_STEAM_UI:-}" != desktop ]; then')]
        for native, probe in [('0', '0'), ('0', '1'), ('1', '0'), ('1', '1')]:
            env = dict(self.env, BL_X64_NATIVE=native, BL_NATIVE_CEF_SINGLE_PROCESS=probe,
                       BL_NATIVE_CEF_DISABLE_HANG_TIMEOUTS='0', BL_NATIVE_CEF_GPU='0',
                       BL_DESKTOP='1', BL_STEAM_UI='desktop')
            result = subprocess.run(['bash', '-c', block + '\nprintf "%s\\n" "${_bl_cef[@]}"'],
                                    env=env, capture_output=True, text=True, timeout=10)
            self.assertEqual(result.returncode, 0, result.stderr)
            expected = ['-cef-disable-gpu']
            if native == probe == '1':
                expected.append('-cef-single-process')
            self.assertEqual(result.stdout.splitlines(), expected)

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

    def test_native_session_does_not_download_arm_proton(self):
        source = (SCRIPT.parent / 'droiddeck-session').read_text()
        start = source.index('    _bl_proton_root=$steam_root')
        end = source.index('    if [ -s "$HOME/.bl-steam-urls" ]; then', start)
        self.stub('droiddeck-proton-extra', 'touch "$HOME/arm-downloader-ran"\n')
        self.stub('droiddeck-steam-x64', 'exit 0\n')
        pending = self.root / '.bl-proton-extra'
        pending.write_text('ge\n')
        result = subprocess.run(['bash', '-c',
                                 'steam_x64=1; steam_root=$HOME; XDG_DATA_HOME=$HOME; '
                                 'step() { :; }; ' + source[start:end]],
                                env=self.env, capture_output=True, text=True, timeout=10)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertFalse((self.root / 'arm-downloader-ran').exists())
        self.assertEqual(pending.read_text(), 'ge\n')

    def test_native_session_preserves_the_existing_system_bus(self):
        source = (SCRIPT.parent / 'droiddeck-session').read_text()
        start = source.index('    if [ "${BL_X64_NATIVE:-0}" = 1 ]; then\n      [ -S /run/dbus')
        end = source.index('    # The UI bridge launches SteamVR', start)
        block = source[start:end].replace('/run/dbus', shlex.quote(str(self.root / 'bus')))
        block = block.replace('/usr/local/share/droiddeck', shlex.quote(str(self.root / 'config')))
        bus_path = self.root / 'bus/system_bus_socket'
        bus_path.parent.mkdir()
        with socket.socket(socket.AF_UNIX) as bus:
            bus.bind(str(bus_path))
            self.stub('dbus-daemon', 'touch "$HOME/replaced-bus"\n')
            result = subprocess.run(['bash', '-c', block + '\nprintf "%s" "$DBUS_SYSTEM_BUS_ADDRESS"'],
                                    env=self.env, capture_output=True, text=True, timeout=10)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(result.stdout, 'unix:path=' + str(bus_path))
            self.assertTrue(bus_path.exists())
            self.assertFalse((self.root / 'replaced-bus').exists())
        bus_path.unlink()
        missing = subprocess.run(['bash', '-c', block], env=self.env,
                                 capture_output=True, text=True, timeout=10)
        self.assertEqual(missing.returncode, 69)

    def test_native_session_keeps_standard_valve_compatibility_mapping(self):
        source = (SCRIPT.parent / 'droiddeck-session').read_text()
        start = source.index('    if [ "$steam_x64" = 1 ] && [ "${BL_X64_NATIVE:-0}" != 1 ]; then\n      { droiddeck-steam-compat')
        end = source.index('    _bl_ui=', start)
        self.stub('droiddeck-steam-compat', 'touch "$HOME/android-mapping-ran"\n')
        for native in ('1', '0'):
            result = subprocess.run(['bash', '-c', 'steam_x64=1; steam_root=$HOME; ' + source[start:end] + '\nwait'],
                                    env=dict(self.env, BL_X64_NATIVE=native), capture_output=True, text=True, timeout=10)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual((self.root / 'android-mapping-ran').exists(), native == '0')

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
