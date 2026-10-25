from pathlib import Path
import hashlib
import json
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools/qualcomm"))
import build_gamescope  # noqa: E402


class QualcommGamescopeSyncTest(unittest.TestCase):
    def test_the_pinned_qualcomm_gamescope_was_built_from_the_current_patches(self):
        # The Qualcomm Gamescope carries the regular DroidDeck patches too. When one of them (or a Qualcomm one)
        # changes, the pinned binary no longer matches this tree.
        pinned = json.loads((ROOT / "tools/qualcomm/runtime.json").read_text())["gamescope"]["patches"]
        current = {str(p.relative_to(ROOT)).replace("\\", "/"): hashlib.sha256(p.read_bytes()).hexdigest()
                   for p in build_gamescope.patches()}
        self.assertEqual(list(current.items()), list(pinned.items()),
                         "The Gamescope patches changed after the Qualcomm Gamescope was built: rebuild it with "
                         "DroidDeck-Components build-qualcomm-runtime.yml, then copy the pins from its "
                         "gamescope-build.json, zink-build.json and wsi-build.json into tools/qualcomm/runtime.json")


if __name__ == "__main__":
    unittest.main()
