import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

PRELOAD = Path(__file__).resolve().parents[1] / "linuxfs/preload"

# Each path opened read-only through the preload, from a thread other than the main one: how it was
# served, and its TracerPid line.
PROGRAM = r"""
import fcntl, json, os, sys, threading
seen = {}
def open_all():
    for path in sys.argv[1:]:
        path = path.replace("PID", str(os.getpid())).replace("TID", str(threading.get_native_id()))
        fd = os.open(path, os.O_RDONLY)
        try:
            text = os.read(fd, 65536).decode()
            seen[path] = {"read_only": fcntl.fcntl(fd, fcntl.F_GETFL) & os.O_ACCMODE == os.O_RDONLY,
                          "file": os.readlink("/proc/self/fd/%d" % fd),
                          "tracer": next(line for line in text.splitlines() if line.startswith("TracerPid:"))}
        finally:
            os.close(fd)
thread = threading.Thread(target=open_all)
thread.start()
thread.join()
print(json.dumps(seen))
"""


@unittest.skipUnless(sys.platform.startswith("linux") and shutil.which("cc"), "needs Linux and a C compiler")
class TracerTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.build = tempfile.TemporaryDirectory()
        cls.library = Path(cls.build.name) / "libblsession.so"
        subprocess.run(["cc", "-shared", "-fPIC", "-O2", "-pthread", "-o", str(cls.library),
                        *map(str, sorted(PRELOAD.glob("*.c"))), "-ldl"], check=True)

    @classmethod
    def tearDownClass(cls):
        cls.build.cleanup()

    def opened(self, *paths):
        result = subprocess.run([sys.executable, "-c", PROGRAM, *paths], env=dict(os.environ, LD_PRELOAD=str(self.library)),
                                check=True, capture_output=True, text=True)
        return json.loads(result.stdout)

    def test_a_process_reads_its_own_status_without_the_tracer(self):
        for path, seen in self.opened("/proc/self/status", "/proc/thread-self/status", "/proc/PID/status",
                                      "/proc/TID/status", "/proc/PID/task/TID/status").items():
            self.assertEqual("TracerPid:\t0", seen["tracer"], path)
            self.assertTrue(seen["file"].startswith("/memfd:status"), path)

    def test_another_process_status_is_the_real_read_only_file(self):
        # Chromium hands a renderer its status as a read-only file and checks that it is one.
        path = "/proc/%d/status" % os.getpid()
        seen = self.opened(path)[path]
        self.assertTrue(seen["read_only"])
        self.assertEqual(path, seen["file"])


if __name__ == "__main__":
    unittest.main()
