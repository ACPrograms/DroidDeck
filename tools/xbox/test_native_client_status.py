#!/usr/bin/env python3
"""Unit tests for privacy-safe guest launcher diagnostic."""

import importlib.util
import io
import json
import os
from pathlib import Path
import stat
import subprocess
import sys
import tempfile
import unittest

# Import native-client-status.py via spec (hyphenated module name)
MODULE_PATH = Path(__file__).with_name("native-client-status.py")
spec = importlib.util.spec_from_file_location("native_client_status", MODULE_PATH)
status_mod = importlib.util.module_from_spec(spec)
spec.loader.exec_module(status_mod)


class NativeClientStatusTests(unittest.TestCase):
    def test_pure_classifier_whitelist_patterns(self):
        cases = [
            # SHM_EXHAUSTED
            ("Failed to allocate in /dev/shm: out of space", "SHM_EXHAUSTED"),
            ("shm_open failed: file exists", "SHM_FAILURE"),
            ("posixsharedmemory allocation error", "SHM_FAILURE"),
            ("Bus error (core dumped)", "BUS_FAULT"),
            ("Process received SIGBUS", "BUS_FAULT"),
            # ALLOCATION_FAILURE
            ("<--- Last few GCs ---> FatalProcessOutOfMemory", "ALLOCATION_FAILURE"),
            ("terminate called after throwing an instance of 'std::bad_alloc'", "ALLOCATION_FAILURE"),
            ("mmap failed: cannot allocate memory", "ALLOCATION_FAILURE"),
            ("out of memory while allocating font atlas", "ALLOCATION_FAILURE"),
            # MISSING_LIBRARY
            ("error while loading shared libraries: libgtk-x11-2.0.so.0: cannot open shared object file", "MISSING_GTK_LIBRARY"),
            ("undefined symbol: XF86VidModeQueryExtension", "MISSING_SYMBOL"),
            ("gameoverlayrenderer.so: cannot open shared object file", "MISSING_STEAM_OVERLAY"),
            ("libEGL.so.1: cannot open shared object file", "MISSING_GRAPHICS_LIBRARY"),
            ("libnm.so.0: cannot open shared object file", "MISSING_INPUT_NETWORK_LIBRARY"),
            # GRAPHICS_FAILURE
            ("libGL error: failed to create gl context", "GRAPHICS_FAILURE"),
            ("dri driver not found for gallium", "GRAPHICS_FAILURE"),
            ("cannot open display :0", "GRAPHICS_FAILURE"),
            ("X Error of failed request: BadMatch (invalid parameter attributes)", "GRAPHICS_FAILURE"),
            ("vk_error_initialization_failed on instance create", "GRAPHICS_FAILURE"),
            # CLIENT_ASSERTION
            ("[FATAL:gpu_watchdog_thread.cc(385)] Check failed: false. BreakDebugger called", "CLIENT_ASSERTION"),
            ("Assertion failed: (m_pClient != nullptr), function Run, file steam.cpp, line 400", "CLIENT_ASSERTION"),
            ("Process terminated with SIGABRT", "CLIENT_ABORT"),
            ("tier0 assertion failure in tier0_s.so", "CLIENT_ASSERTION"),
            # DISK_FULL
            ("write error: no space left on device", "DISK_FULL"),
            ("disk full during package extract", "DISK_FULL"),
            # Normal / non-fatal lines return None
            ("Steam logging initialized successfully", None),
            ("/dev/shm is available", None),
            ("/dev/shm initialized successfully", None),
            ("Starting steamwebhelper in background", None),
            ("PulseAudio connected to default sink", None),
            ("User logged in with normal status", None),
        ]
        for line, expected in cases:
            with self.subTest(line=line):
                self.assertEqual(status_mod.classify_line(line), expected)

    def test_privacy_guarantee_secrets_and_paths_never_serialized(self):
        """Proves passwords, tokens, emails, tickets, and paths are NEVER serialized into JSON."""
        password = "P@ssw0rd!_StrictSecretValue_987654"
        token = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.SuperSecretJWTAuthToken.sig123"
        email = "confidential_engineer@valvesoftware.test"
        ticket = "ticket_deadbeefcafebabe0123456789abcdef"
        secret_path = "/home/droiddeck/.steam/config/loginusers.vdf"
        address = "0x7ffee1234567"

        # Construct lines that mix sensitive data with fatal patterns
        generator_code = f"""
import sys
print("Starting up with user {email} and token {token}")
print("Assertion failed: (auth == valid) for password {password} at {secret_path}")
print("Bus error in /dev/shm for ticket {ticket}")
print("FatalProcessOutOfMemory while reading {secret_path} at memory {address}")
print("Regular info line: user={email} connected")
sys.exit(134)
"""
        with tempfile.TemporaryDirectory() as tmp_dir:
            status_path = Path(tmp_dir) / "test-status.json"
            exit_code = status_mod.run_diagnostic(
                command=[sys.executable, "-c", generator_code],
                status_path=status_path,
            )
            self.assertEqual(exit_code, 134)
            self.assertTrue(status_path.exists())

            # Read raw output bytes and string
            raw_content = status_path.read_text(encoding="utf-8")
            data = json.loads(raw_content)

            # Strict privacy assertions: zero leakage of secrets or paths
            for secret in (password, token, email, ticket, secret_path, address):
                self.assertNotIn(
                    secret,
                    raw_content,
                    f"Privacy violation: Secret substring {secret!r} was serialized into JSON status!",
                )

            # Verify reason counters incremented accurately
            counts = data["reason_counts"]
            self.assertEqual(counts["CLIENT_ASSERTION"], 1)
            self.assertEqual(counts["BUS_FAULT"], 1)
            self.assertEqual(counts["ALLOCATION_FAILURE"], 1)
            self.assertEqual(counts["MISSING_LIBRARY"], 0)
            self.assertEqual(counts["GRAPHICS_FAILURE"], 0)
            self.assertEqual(counts["DISK_FULL"], 0)
            self.assertEqual(counts["DIAGNOSTIC_ERROR"], 0)
            self.assertEqual(data["child_exit_code"], 134)

    def test_schema_invariants_and_mode_0600(self):
        """Verifies output JSON adheres to fixed schema, permissions mode 0600, and no free-text fields."""
        with tempfile.TemporaryDirectory() as tmp_dir:
            status_path = Path(tmp_dir) / "status.json"
            exit_code = status_mod.run_diagnostic(
                command=[sys.executable, "-c", "import sys; sys.exit(0)"],
                status_path=status_path,
            )
            self.assertEqual(exit_code, 0)
            self.assertTrue(status_path.exists())

            # Verify permissions are mode 0600
            file_mode = stat.S_IMODE(os.stat(status_path).st_mode)
            self.assertEqual(
                file_mode,
                0o600,
                f"Expected mode 0600, got {oct(file_mode)}",
            )

            # Verify schema
            data = json.loads(status_path.read_text(encoding="utf-8"))
            self.assertEqual(
                set(data.keys()),
                {"schema_version", "timestamp_utc", "child_exit_code", "reason_counts"},
            )
            self.assertEqual(data["schema_version"], 1)
            self.assertEqual(data["child_exit_code"], 0)
            self.assertIsInstance(data["timestamp_utc"], str)
            self.assertIn("T", data["timestamp_utc"])

            # Verify reason counts keys and types
            self.assertEqual(set(data["reason_counts"].keys()), set(status_mod.REASONS))
            for key, val in data["reason_counts"].items():
                self.assertIsInstance(val, int)
                self.assertEqual(val, 0)

            # Verify no temporary files left in directory
            remaining_files = list(Path(tmp_dir).iterdir())
            self.assertEqual(remaining_files, [status_path])

    def test_bounded_memory_stream(self):
        """Verifies process_stream handles oversized chunks without unbounded RAM accumulation."""
        counts = status_mod.init_reason_counts()
        # Create an oversized single line (100 KB) containing a fatal pattern
        oversized_data = (b"A" * 50000) + b" Bus error " + (b"B" * 50000) + b"\n"
        stream = io.BytesIO(oversized_data)

        status_mod.process_stream(stream, counts)
        self.assertEqual(counts["BUS_FAULT"], 1)

    def test_diagnostic_error_on_missing_executable(self):
        """Verifies missing executable records DIAGNOSTIC_ERROR and exits with 255."""
        with tempfile.TemporaryDirectory() as tmp_dir:
            status_path = Path(tmp_dir) / "error-status.json"
            exit_code = status_mod.run_diagnostic(
                command=["/usr/local/bin/nonexistent-binary-test-droiddeck"],
                status_path=status_path,
            )
            self.assertEqual(exit_code, 255)
            self.assertTrue(status_path.exists())

            data = json.loads(status_path.read_text(encoding="utf-8"))
            self.assertEqual(data["reason_counts"]["DIAGNOSTIC_ERROR"], 1)
            self.assertEqual(data["child_exit_code"], 255)

    def test_reject_arbitrary_cli_argv(self):
        """Verifies CLI rejects arbitrary command line arguments."""
        result = subprocess.run(
            [sys.executable, str(MODULE_PATH), "--arbitrary-arg", "evil-command"],
            capture_output=True,
            text=True,
        )
        self.assertEqual(result.returncode, 2)
        self.assertIn("arbitrary arguments not permitted", result.stderr)


if __name__ == "__main__":
    unittest.main()
