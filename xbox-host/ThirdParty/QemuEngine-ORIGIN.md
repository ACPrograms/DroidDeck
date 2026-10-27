# Native engine provenance

The Xbox platform adapter uses the native engine, ANGLE libraries and BIOS assets
from YoCanIGetUhhhh/XboxInstallOtherOS-dev, source commit
426235db19fc1ae17f8d50e8702015efbb676496, Actions run 39386346738.
The original launcher and its identity are not included.

Source, native patches and build recipe:
https://github.com/YoCanIGetUhhhh/XboxInstallOtherOS-dev/tree/426235db19fc1ae17f8d50e8702015efbb676496

That build uses QEMU 10.0.0; its source is available at
https://gitlab.com/qemu-project/qemu/-/tree/v10.0.0

The upstream project license is included as QemuEngine-LICENSE.txt. QEMU,
ANGLE, firmware and other linked dependencies retain their upstream licenses;
the source tree and build recipe identify those components. The package and
every staged native asset are checksum-pinned in tools/xbox/native-sdk-lock.json.
Original DroidDeck sources retain their original license.
