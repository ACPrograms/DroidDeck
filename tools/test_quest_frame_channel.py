import argparse
import json
import os
import subprocess
import time
from pathlib import Path

from adb_device import resolve_serial


def main():
    parser = argparse.ArgumentParser(description='Validate private owned-frame transport and GLES upload, not SteamVR gameplay')
    parser.add_argument('--adb', default=os.environ.get('ADB', 'adb'))
    parser.add_argument('--serial')
    parser.add_argument('--native-tests', type=Path, required=True)
    parser.add_argument('--output', type=Path, default=Path('questxr-validation/frames'))
    parser.add_argument('--require-rendering', action='store_true')
    args = parser.parse_args()
    serial = resolve_serial(args.adb, args.serial)
    package = 'com.droiddeck.questxr'
    health_path = f'/sdcard/Android/data/{package}/files/xr-health.json'
    args.output.mkdir(parents=True, exist_ok=True)

    def adb(*command):
        return subprocess.run([args.adb, '-s', serial, *command], check=True,
                              capture_output=True, text=True, timeout=30).stdout

    for name in ('test_xr_frames', 'send_xr_frame'):
        binary = args.native_tests / name
        if not binary.is_file():
            parser.error(f'Missing Android executable: {binary}')
        remote = f'/data/local/tmp/droiddeck-{name}'
        adb('push', str(binary), remote)
        adb('shell', 'run-as', package, 'cp', remote, f'./{name}')
        adb('shell', 'run-as', package, 'chmod', '700', f'./{name}')
    unit_output = adb('shell', 'run-as', package, './test_xr_frames', './files')
    adb('shell', 'am', 'force-stop', package)
    adb('shell', 'rm', '-f', health_path)
    failure = None
    samples = []
    sent = {}
    try:
        adb('shell', 'input', 'keyevent', 'KEYCODE_WAKEUP')
        adb('shell', 'am', 'broadcast', '-a', 'com.oculus.vrpowermanager.prox_close')
        adb('shell', 'am', 'start', '-n', f'{package}/android.app.NativeActivity',
            '--ez', 'frame_input', 'true', '--ez', 'frame_verify', 'true')
        sent = json.loads(adb('shell', 'run-as', package, './send_xr_frame',
                             './files/.wayland-rt/droiddeck-xr-frames'))
        deadline = time.monotonic() + 5
        while time.monotonic() < deadline:
            try:
                sample = json.loads(adb('shell', 'cat', health_path))
                samples.append(sample)
                if sample.get('error'):
                    failure = sample['error']
                    break
                if sample.get('gpuVerifiedFrameId') == sent['lastFrameId']:
                    break
            except (subprocess.CalledProcessError, json.JSONDecodeError):
                pass
            time.sleep(0.2)
    except (subprocess.CalledProcessError, subprocess.TimeoutExpired, json.JSONDecodeError) as error:
        failure = f'Frame channel operation failed: {error}'
    finally:
        try:
            adb('shell', 'am', 'broadcast', '-a', 'com.oculus.vrpowermanager.prox_far')
            adb('shell', 'am', 'force-stop', package)
        except (subprocess.CalledProcessError, subprocess.TimeoutExpired):
            failure = failure or 'Could not restore headset proximity after testing'
    latest = samples[-1] if samples else {}
    if not failure:
        if latest.get('source') != 'frames' or latest.get('receivedFrames') != sent.get('sent'):
            failure = 'Not all complete frames arrived in the private receiver'
        elif latest.get('gpuVerifiedFrameId') != sent.get('lastFrameId') or latest.get('gpuChecksum') != sent.get('checksum'):
            failure = 'GPU readback does not prove the final image and frame ID match'
        elif latest.get('rejectedFrames') != 0 or latest.get('uploadedFrames', 0) < 1:
            failure = 'Malformed frames or no actual GPU upload'
        elif args.require_rendering and (latest.get('rendered', 0) < 72 or latest.get('headFlags', 0) & 15 != 15):
            failure = 'Owned upload passed but sustained tracked immersive projection did not'
    report = {'ok': failure is None, 'scope': 'owned-frame GLES upload', 'error': failure,
              'unitTests': unit_output, 'sender': sent, 'samples': samples}
    (args.output / 'report.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps(report, indent=2))
    return 0 if report['ok'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
