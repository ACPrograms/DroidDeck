"""Stage only checksum-locked native engine assets, never the upstream launcher."""
import argparse
import hashlib
import json
from pathlib import Path
import zipfile

repo = Path(__file__).resolve().parents[2]
lock = json.loads(Path(__file__).with_name('native-sdk-lock.json').read_text())
out = repo / 'xbox-host/NativeSdk'
parser = argparse.ArgumentParser()
parser.add_argument('--artifact-dir', type=Path)
parser.add_argument('--verify-only', action='store_true')
args = parser.parse_args()

if not args.verify_only:
    if args.artifact_dir is None:
        parser.error('--artifact-dir is required when staging')
    matches = list(args.artifact_dir.rglob(lock['msix']))
    if len(matches) != 1:
        raise SystemExit('Expected exactly one pinned native SDK package')
    package = matches[0]
    if hashlib.sha256(package.read_bytes()).hexdigest() != lock['msix_sha256']:
        raise SystemExit('Native SDK package checksum mismatch')
    with zipfile.ZipFile(package) as archive:
        for name, expected in lock['files'].items():
            with archive.open(name) as source:
                data = source.read(expected['bytes'] + 1)
            if len(data) != expected['bytes'] or hashlib.sha256(data).hexdigest() != expected['sha256']:
                raise SystemExit('Native SDK asset checksum mismatch: ' + name)
            destination = out / name
            destination.parent.mkdir(parents=True, exist_ok=True)
            destination.write_bytes(data)

for name, expected in lock['files'].items():
    path = out / name
    if not path.is_file() or path.stat().st_size != expected['bytes']:
        raise SystemExit('Native SDK asset missing or wrong size: ' + name)
    if hashlib.sha256(path.read_bytes()).hexdigest() != expected['sha256']:
        raise SystemExit('Cached native SDK asset checksum mismatch: ' + name)
print('Verified', len(lock['files']), 'pinned native SDK and BIOS assets')
