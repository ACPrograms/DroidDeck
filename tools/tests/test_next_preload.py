from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

PRELOAD = Path(__file__).resolve().parents[1] / "linuxfs/preload"

PROBE = r"""
#define _GNU_SOURCE
#include <dlfcn.h>

void *bl_next(const char *name) __attribute__((visibility("hidden")));
void *__real_dlsym(void *handle, const char *name);

/* Lookups the walk left to dlsym. */
static int walking;
static long passed_on;

void *__wrap_dlsym(void *handle, const char *name) {
  passed_on += walking;
  return __real_dlsym(handle, name);
}

void *probe_walk(const char *name) {
  walking = 1;
  void *found = bl_next(name);
  walking = 0;
  return found;
}

void *probe_dlsym(const char *name) { return dlsym(RTLD_NEXT, name); }
long probe_passed_on(void) { return passed_on; }
"""

# Counts the allocations a thread makes, as an allocator that is still starting up would see them.
COUNT = r"""
#include <stddef.h>

extern void *__libc_malloc(size_t);
extern void *__libc_calloc(size_t, size_t);
extern void *__libc_realloc(void *, size_t);
static __thread long made;

void *malloc(size_t size) { made++; return __libc_malloc(size); }
void *calloc(size_t count, size_t size) { made++; return __libc_calloc(count, size); }
void *realloc(void *old, size_t size) { made++; return __libc_realloc(old, size); }
void count_reset(void) { made = 0; }
long count_get(void) { return made; }
"""

PROGRAM = r"""
#include <pthread.h>
#include <stdio.h>

void *probe_walk(const char *name);
void *probe_dlsym(const char *name);
long probe_passed_on(void);
void count_reset(void);
long count_get(void);

static void *first_lookup(void *made) {
  long before = probe_passed_on();
  count_reset();
  void *found = probe_walk("openat");
  ((long *)made)[0] = found ? count_get() : -1;
  ((long *)made)[1] = probe_passed_on() - before;
  return NULL;
}

int main(int argc, char **argv) {
  int differ = 0;
  for (int i = 1; i < argc; i++) {
    void *walked = probe_walk(argv[i]), *found = probe_dlsym(argv[i]);
    if (walked != found) {
      printf("%s: %p, dlsym %p\n", argv[i], walked, found);
      differ = 1;
    }
  }
  long made[2] = {0, 0};
  pthread_t thread;
  pthread_create(&thread, NULL, first_lookup, made);
  pthread_join(thread, NULL);
  printf("allocations %ld, left to dlsym %ld\n", made[0], made[1]);
  return differ;
}
"""

NAMES = ["open", "openat", "read", "recvmsg", "syscall", "close_range", "fstatat", "stat", "pthread_create",
         "dlopen", "malloc", "calloc", "memcpy", "strlen", "environ", "no_such_symbol"]


@unittest.skipUnless(sys.platform.startswith("linux") and shutil.which("cc"), "needs Linux and a C compiler")
class NextTest(unittest.TestCase):
    """bl_next built as on glibc before 2.34, where the walk replaces dlsym(RTLD_NEXT)."""

    @classmethod
    def setUpClass(cls):
        cls.build = tempfile.TemporaryDirectory()
        build = Path(cls.build.name)
        (build / "probe.c").write_text(PROBE)
        (build / "count.c").write_text(COUNT)
        (build / "program.c").write_text(PROGRAM)
        cls.count = build / "libcount.so"
        cls.program = build / "program"
        subprocess.run(["cc", "-shared", "-fPIC", "-O2", "-DNEXT_TEST", "-Wl,--wrap=dlsym", "-o", str(build / "libprobe.so"),
                        str(build / "probe.c"), str(PRELOAD / "next.c"), "-ldl"], check=True)
        subprocess.run(["cc", "-shared", "-fPIC", "-O2", "-o", str(cls.count), str(build / "count.c")], check=True)
        subprocess.run(["cc", "-O2", "-pthread", "-o", str(cls.program), str(build / "program.c"),
                        str(build / "libprobe.so"), str(cls.count), "-Wl,-rpath," + str(build)], check=True)

    @classmethod
    def tearDownClass(cls):
        cls.build.cleanup()

    def run_program(self):
        return subprocess.run([str(self.program)] + NAMES, capture_output=True, text=True)

    def test_finds_what_dlsym_finds(self):
        result = self.run_program()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_a_threads_first_lookup_walks_and_allocates_nothing(self):
        self.assertIn("allocations 0, left to dlsym 0\n", self.run_program().stdout)


if __name__ == "__main__":
    unittest.main()
