"""Linux regression checks for the native Steam shared-memory adapter."""
from pathlib import Path
import json
import os
import platform
import shutil
import subprocess
import tempfile
import unittest


SOURCE = Path(__file__).with_name('native-sparse-zero.c')
FIXTURE = r'''
import ctypes,json,mmap,os,tempfile
lib=ctypes.CDLL(None,use_errno=True)
lib.memset.argtypes=[ctypes.c_void_p,ctypes.c_int,ctypes.c_size_t]
lib.memset.restype=ctypes.c_void_p
n=26214416
with tempfile.TemporaryFile(dir='/dev/shm',prefix='droiddeck-zero-fixture-') as owned:
 os.ftruncate(owned.fileno(),n+4096)
 with mmap.mmap(owned.fileno(),n+4096) as memory, mmap.mmap(owned.fileno(),n+4096) as peer:
  address=ctypes.addressof(ctypes.c_char.from_buffer(memory))
  lib.memset(address,90,n+4096)
  ctypes.set_errno(123)
  assert lib.memset(address,0,n)==address
  assert ctypes.get_errno()==123
  assert peer[:n]==bytes(n) and peer[n:]==bytes([90])*4096
  lib.memset(address,77,n)
  assert peer[:n]==bytes([77])*n
  lib.memset(address+1,0,n-2)
  assert peer[0]==77 and peer[n-1]==77 and peer[1:n-1]==bytes(n-2)
with mmap.mmap(-1,n,flags=mmap.MAP_PRIVATE|mmap.MAP_ANONYMOUS) as memory:
 address=ctypes.addressof(ctypes.c_char.from_buffer(memory))
 lib.memset(address,91,n)
 lib.memset(address,0,n)
 assert memory[:]==bytes(n)
# Shared mappings on an ordinary filesystem must retain their normal behavior.
with tempfile.TemporaryFile() as owned:
 os.ftruncate(owned.fileno(),n)
 with mmap.mmap(owned.fileno(),n) as memory:
  address=ctypes.addressof(ctypes.c_char.from_buffer(memory))
  lib.memset(address,92,n)
  lib.memset(address,0,n)
  assert memory[:]==bytes(n)
print(json.dumps({'bytes_and_shared_views_correct':True,'fallbacks_correct':True}))
'''


@unittest.skipUnless(platform.system() == 'Linux' and platform.machine() == 'x86_64'
                     and shutil.which('gcc'), 'requires native Linux x86-64 gcc')
class NativeSparseZeroTest(unittest.TestCase):
    def test_zero_semantics_and_executable_scope(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for fixture_build, force_fixture in ((False, True), (True, False), (True, True)):
                library = root / ('fixture.so' if fixture_build else 'production.so')
                command = ['gcc', '-shared', '-fPIC', '-O2', '-fno-builtin',
                           '-Wall', '-Wextra', '-Werror']
                if fixture_build:
                    command.append('-DDROIDDECK_SPARSE_ZERO_FIXTURE')
                subprocess.run(command + ['-o', str(library), str(SOURCE), '-ldl'], check=True)
                status = root / f'status-{fixture_build}-{force_fixture}'
                status.mkdir()
                environment = dict(os.environ, LD_PRELOAD=str(library),
                                   DROIDDECK_SPARSE_ZERO='1',
                                   DROIDDECK_SPARSE_ZERO_TEST='1' if force_fixture else '0',
                                   DROIDDECK_SPARSE_ZERO_STATUS_DIR=str(status))
                result = subprocess.run(['python3', '-c', FIXTURE], env=environment,
                                        capture_output=True, text=True, timeout=45)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertTrue(json.loads(result.stdout)['bytes_and_shared_views_correct'])
                states = b''.join(file.read_bytes() for file in status.glob('marker-*.status'))
                self.assertEqual(b'SPARSE_ZERO_USED\n' in states, fixture_build and force_fixture)


if __name__ == '__main__':
    unittest.main()
