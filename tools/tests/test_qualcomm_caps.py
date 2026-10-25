from pathlib import Path
import json
import runpy
import tempfile
import unittest

CAPS = runpy.run_path(str(Path(__file__).resolve().parents[1] / "linuxfs/overlay/usr/local/bin/droiddeck-qualcomm-caps"))
decide = CAPS["decide"]
cache_key = CAPS["cache_key"]
cached_probe = CAPS["cached_probe"]
REQUIRED = set(CAPS["REQUIRED"])
CAPABLE = {"extensions": REQUIRED | {"VK_EXT_image_drm_format_modifier"}, "linear_export": True,
           "linear_import": True, "export_fd": True, "gamescope_dmabuf": True}


class QualcommCapsTest(unittest.TestCase):
    def test_a_driver_with_working_dma_buf_export_enables_zero_copy(self):
        self.assertEqual(decide(CAPABLE), (True, []))

    def test_adreno_740_without_dma_buf_keeps_shm(self):
        extensions = REQUIRED - {"VK_EXT_external_memory_dma_buf"}
        enabled, reasons = decide({"extensions": extensions, "gamescope_dmabuf": False})
        self.assertFalse(enabled)
        self.assertEqual(reasons, ["missing VK_EXT_external_memory_dma_buf", "Gamescope offers no linux-dmabuf"])

    def test_every_check_must_pass(self):
        for key, reason in (("linear_export", "LINEAR DMA-BUF images are not exportable"),
                            ("linear_import", "LINEAR DMA-BUF images are not importable"),
                            ("export_fd", "DMA-BUF export failed"),
                            ("gamescope_dmabuf", "Gamescope offers no linux-dmabuf")):
            with self.subTest(key=key):
                self.assertEqual(decide(dict(CAPABLE, **{key: False})), (False, [reason]))
        self.assertEqual(decide(dict(CAPABLE, export_fd=False, export_error="vkGetMemoryFdKHR failed (-1)")),
                         (False, ["vkGetMemoryFdKHR failed (-1)"]))

    def test_a_failed_probe_never_enables_zero_copy(self):
        self.assertEqual(decide({"error": "vkCreateInstance returned -9"}),
                         (False, ["probe failed: vkCreateInstance returned -9"]))
        self.assertFalse(decide({})[0])



class QualcommCapsCacheTest(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.dir.cleanup)
        root = Path(self.dir.name)
        self.hal = root / "vulkan.adreno.so"
        self.hal.write_bytes(b"driver one")
        self.script = root / "caps"
        self.script.write_text("checks")
        self.path = str(root / "cache/qualcomm-caps.json")
        self.env = {"HYBRIS_VULKAN_HAL": str(self.hal), "DROIDDECK_QUALCOMM_DRIVER": "system-qualcomm",
                    "DROIDDECK_QUALCOMM_ROOT": "/data/qualcomm-system/abc"}
        self.probes = 0
        globals_ = cached_probe.__globals__
        original = globals_["probe"]
        self.addCleanup(globals_.__setitem__, "probe", original)
        globals_["probe"] = self.fake_probe

    def fake_probe(self):
        self.probes += 1
        return {"device": "Adreno (TM) 750", "driver": "0.762.24", "extensions": set(REQUIRED),
                "linear_export": True, "linear_import": True, "export_fd": True}

    def key(self, **changes):
        return cache_key(dict(self.env, **changes), str(self.script))

    def test_the_key_follows_the_driver_file_the_selection_and_the_checks(self):
        key = self.key()
        self.assertEqual(key, self.key())
        self.assertNotEqual(key, self.key(DROIDDECK_QUALCOMM_DRIVER="qualcomm-adrenotools:v891"))
        self.assertNotEqual(key, self.key(DROIDDECK_QUALCOMM_ROOT="/data/qualcomm-system/def"))
        self.hal.write_bytes(b"driver two")
        self.assertNotEqual(key, self.key())
        self.hal.write_bytes(b"driver one")
        self.script.write_text("new checks")
        self.assertNotEqual(key, self.key())
        self.assertIsNone(self.key(HYBRIS_VULKAN_HAL=str(self.hal) + ".missing"))

    def test_a_saved_result_is_reused_only_for_the_same_key(self):
        caps, saved = cached_probe(self.path, self.key())
        self.assertEqual((saved, self.probes), (False, 1))
        caps, saved = cached_probe(self.path, self.key())
        self.assertEqual((saved, self.probes), (True, 1))
        self.assertEqual(caps["extensions"], set(REQUIRED))
        self.assertEqual(decide(dict(caps, gamescope_dmabuf=True)), (True, []))
        self.assertNotIn("gamescope_dmabuf", json.loads(Path(self.path).read_text())["caps"])
        _, saved = cached_probe(self.path, self.key(DROIDDECK_QUALCOMM_DRIVER="qualcomm-adrenotools:v891"))
        self.assertEqual((saved, self.probes), (False, 2))

    def test_failed_probes_and_unreadable_drivers_are_not_saved(self):
        def failing():
            raise RuntimeError("vkCreateInstance returned -9")
        cached_probe.__globals__["probe"] = failing
        self.assertEqual(cached_probe(self.path, self.key()), ({"error": "vkCreateInstance returned -9"}, False))
        self.assertFalse(Path(self.path).exists())
        cached_probe.__globals__["probe"] = self.fake_probe
        self.assertEqual(cached_probe(self.path, None)[1], False)
        self.assertFalse(Path(self.path).exists())

    def test_a_damaged_saved_file_probes_again(self):
        Path(self.path).parent.mkdir(parents=True)
        Path(self.path).write_text("{not json")
        self.assertEqual(cached_probe(self.path, self.key())[1], False)
        self.assertEqual(self.probes, 1)


if __name__ == "__main__":
    unittest.main()
