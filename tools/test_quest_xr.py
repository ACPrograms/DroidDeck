import argparse
import json
import os
import subprocess
import time
from pathlib import Path

from adb_device import resolve_serial


def main():
    parser = argparse.ArgumentParser(description='Validate immersive DroidDeck XR on a connected Quest')
    parser.add_argument('--adb', default=os.environ.get('ADB', 'adb'))
    parser.add_argument('--serial')
    parser.add_argument('--seconds', type=int, default=20)
    parser.add_argument('--output', type=Path, default=Path('questxr-validation'))
    parser.add_argument('--require-controllers', action='store_true')
    args = parser.parse_args()
    if args.seconds < 5 or args.seconds > 600:
        parser.error('--seconds must be between 5 and 600')
    serial = resolve_serial(args.adb, args.serial)
    args.output.mkdir(parents=True, exist_ok=True)

    def adb(*command):
        return subprocess.run([args.adb, '-s', serial, *command], check=True,
                              capture_output=True, text=True).stdout

    health_path = '/sdcard/Android/data/com.droiddeck.questxr/files/xr-health.json'
    adb('shell', 'am', 'force-stop', 'com.droiddeck.questxr')
    adb('shell', 'rm', '-f', health_path)
    adb('shell', 'input', 'keyevent', 'KEYCODE_WAKEUP')
    adb('shell', 'am', 'broadcast', '-a', 'com.oculus.vrpowermanager.prox_close')
    adb('shell', 'am', 'start', '-n', 'com.droiddeck.questxr/android.app.NativeActivity')
    samples = []
    failure = None
    deadline = time.monotonic() + args.seconds
    while time.monotonic() < deadline:
        time.sleep(1)
        adb('shell', 'am', 'broadcast', '-a', 'com.oculus.vrpowermanager.prox_close')
        try:
            sample = json.loads(adb('shell', 'cat', health_path))
            samples.append(sample)
            if sample.get('error'):
                failure = sample['error']
                break
        except (subprocess.CalledProcessError, json.JSONDecodeError):
            pass
    log = adb('logcat', '-d', '-s', 'DroidDeckXR:I', 'OpenXR-Loader:W', 'AndroidRuntime:E')
    (args.output / 'logcat.txt').write_text(log, encoding='utf-8')
    if len(samples) < 2:
        failure = failure or 'No advancing telemetry; check runtime initialization and headset wake/proximity'
    elif samples[-1]['rendered'] - samples[0]['rendered'] < 72:
        failure = failure or 'Fewer than 72 additional stereo frames were submitted'
    elif not any(sample['sessionState'] == 5 and sample['headFlags'] & 3 == 3 for sample in samples):
        failure = failure or 'No focused session with valid position and orientation'
    elif args.require_controllers and not any(all(sample['controllersActive']) for sample in samples):
        failure = failure or 'Both controllers did not become active'
    report = {'ok': failure is None, 'error': failure, 'samples': samples}
    (args.output / 'report.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps({'ok': report['ok'], 'error': failure, 'samples': len(samples),
                      'last': samples[-1] if samples else None}, indent=2))
    return 0 if report['ok'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
