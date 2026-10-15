from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


@unittest.skipUnless(shutil.which("gcc"), "needs gcc on a Linux host")
class RobustPreloadTest(unittest.TestCase):
    def test_native_and_missing_syscall_contract(self):
        with tempfile.TemporaryDirectory() as directory:
            executable = Path(directory) / "robust-preload-test"
            harness = Path(__file__).with_name("robust_preload_harness.c")
            subprocess.run(["gcc", "-O2", "-Wall", "-Wextra", "-Werror", "-pthread",
                            str(harness), "-ldl", "-o", str(executable)], check=True)
            result = subprocess.run([str(executable)], capture_output=True, text=True, timeout=20)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertIn("permission failures passed", result.stdout)
