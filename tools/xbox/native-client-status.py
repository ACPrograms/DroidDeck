#!/usr/bin/env python3
"""Privacy-safe guest-only launcher diagnostic for native DroidDeck Steam session.

Launches the fixed command ['/usr/local/bin/droiddeck-steam-launch', 'desktop'], consumes
stdout and stderr inside the guest, and emits a minimal atomic JSON status report
with mode 0600 to /run/droiddeck/native-client-status.json.

Never logs, stores, or forwards raw output text, credentials, paths, or tokens.
Classifies fatal failure patterns into whitelisted enum reason counters.
"""

from datetime import datetime, timezone
import json
import os
from pathlib import Path
import re
import signal
import subprocess
import sys
import tempfile
from typing import Dict, List, Optional

SCHEMA_VERSION = 1
FIXED_COMMAND = ["/usr/local/bin/droiddeck-steam-launch", "desktop"]
DEFAULT_STATUS_PATH = Path("/run/droiddeck/native-client-status.json")
MAX_LINE_BYTES = 4096

# Fixed enum reason names (whitelisted fatal diagnostic indicators only).
# No authentication, ownership, or game success claims.
REASONS = (
    "SHM_EXHAUSTED",
    "SHM_FAILURE",
    "BUS_FAULT",
    "CLIENT_ABORT",
    "ALLOCATION_FAILURE",
    "MISSING_LIBRARY",
    "GRAPHICS_FAILURE",
    "CLIENT_ASSERTION",
    "DISK_FULL",
    "DIAGNOSTIC_ERROR",
)

# Ordered pattern whitelist: specific patterns precede general ones.
# Input strings are evaluated solely for enum classification and immediately discarded.
WHITELIST_PATTERNS = [
    ("SHM_EXHAUSTED", re.compile(
        r"^(?=.*(?:/dev/shm|shared memory|shm_open|shmat))(?=.*\b(?:no space|out of space|exhausted|enospc|full)\b)", re.IGNORECASE)),
    ("SHM_FAILURE", re.compile(
        r"(?:shm_open|shmat|posixsharedmemory|createanonymoussharedmemory).*(?:failed|error)|failed to allocate shared memory", re.IGNORECASE)),
    ("BUS_FAULT", re.compile(r"bus error|sigbus", re.IGNORECASE)),
    ("CLIENT_ABORT", re.compile(r"sigabrt|abort\(\)", re.IGNORECASE)),
    (
        "ALLOCATION_FAILURE",
        re.compile(
            r"(out of memory|cannot allocate memory|fatalprocessoutofmemory|"
            r"std::bad_alloc|malloc failed|calloc failed|realloc failed|"
            r"mmap failed.*cannot allocate memory|oom-killer|memory limit exceeded)",
            re.IGNORECASE,
        ),
    ),
    (
        "MISSING_LIBRARY",
        re.compile(
            r"(cannot open shared object file|error while loading shared libraries|"
            r"undefined symbol|not found \(required by|"
            r"lib[a-z0-9_.-]+\.so[a-z0-9_.-]*: cannot open|"
            r"no such file or directory:.*\.so)",
            re.IGNORECASE,
        ),
    ),
    (
        "GRAPHICS_FAILURE",
        re.compile(
            r"(failed to create gl context|cannot open display|failed to open display|"
            r"dri driver not found|failed to load driver|failed to load gallium|"
            r"x error of failed request|glx: could not create|eglinitialize failed|"
            r"vkcreateinstance failed|vk_error_initialization_failed|failed to initialize vulkan|"
            r"x11 connection rejected|x11 connection failed)",
            re.IGNORECASE,
        ),
    ),
    (
        "CLIENT_ASSERTION",
        re.compile(
            r"(assertion failed|assert\(|check failed:|fatal error:|"
            r"tier0 assertion|breakdebugger|breakpoint trapped)",
            re.IGNORECASE,
        ),
    ),
    (
        "DISK_FULL",
        re.compile(
            r"(no space left on device|disk full|disk quota exceeded|enospc)",
            re.IGNORECASE,
        ),
    ),
]


def classify_line(line: str) -> Optional[str]:
    """Pure classifier: matches known fatal error patterns against a bounded text slice.

    Returns the matching enum reason string, or None if no whitelisted pattern matches.
    The input string is never stored, logged, or returned.
    """
    for reason, pattern in WHITELIST_PATTERNS:
        if pattern.search(line):
            return reason
    return None


def init_reason_counts() -> Dict[str, int]:
    """Initializes fixed enum reason counters to zero."""
    return {reason: 0 for reason in REASONS}


def build_status_payload(
    reason_counts: Dict[str, int],
    child_exit_code: Optional[int],
) -> dict:
    """Constructs a small, privacy-safe status dictionary with fixed schema.

    Contains exclusively schema version, UTC timestamp, child exit code, and
    integer reason counts. No free-text fields, paths, or secrets.
    """
    return {
        "schema_version": SCHEMA_VERSION,
        "timestamp_utc": datetime.now(timezone.utc).isoformat(),
        "child_exit_code": child_exit_code,
        "reason_counts": {reason: int(reason_counts.get(reason, 0)) for reason in REASONS},
    }


def write_status_atomic(status_path: Path, payload: dict) -> None:
    """Atomically writes status JSON with mode 0600."""
    status_path.parent.mkdir(parents=True, exist_ok=True)
    data = json.dumps(payload, indent=2).encode("utf-8")
    fd, name = tempfile.mkstemp(prefix=status_path.name + ".tmp.", dir=status_path.parent)
    tmp_path = Path(name)
    try:
        with os.fdopen(fd, "wb") as f:
            f.write(data)
            f.flush()
            os.fsync(f.fileno())
    except Exception:
        if tmp_path.exists():
            tmp_path.unlink()
        raise

    os.chmod(tmp_path, 0o600)
    os.replace(tmp_path, status_path)


def process_stream(stream, counts: Dict[str, int], status_path=None) -> None:
    """Consumes process stream in bounded chunks without accumulating text in memory."""
    while True:
        chunk = stream.readline(MAX_LINE_BYTES)
        if not chunk:
            break
        text = chunk.decode("utf-8", errors="replace")
        reason = classify_line(text)
        if reason:
            counts[reason] = min(counts.get(reason, 0) + 1, 1000000)
            if status_path is not None and counts[reason] == 1:
                try:
                    write_status_atomic(status_path, build_status_payload(counts, child_exit_code=None))
                except OSError:
                    counts["DIAGNOSTIC_ERROR"] += 1
        del text
        del chunk


def run_diagnostic(
    command: Optional[List[str]] = None,
    status_path: Optional[Path] = None,
) -> int:
    """Launches the fixed command wrapper, consumes stream, and emits privacy-safe status."""
    cmd = list(command) if command is not None else list(FIXED_COMMAND)
    out_path = Path(status_path) if status_path is not None else DEFAULT_STATUS_PATH

    counts = init_reason_counts()

    # Pre-launch status write
    try:
        write_status_atomic(out_path, build_status_payload(counts, child_exit_code=None))
    except Exception:
        pass

    try:
        proc = subprocess.Popen(
            cmd,
            # Valve's logger otherwise redirects console output before classification.
            env=dict(os.environ, STEAM_RUNTIME_LOGGER="0"),
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            close_fds=True,
        )
    except Exception:
        counts["DIAGNOSTIC_ERROR"] = counts.get("DIAGNOSTIC_ERROR", 0) + 1
        try:
            write_status_atomic(out_path, build_status_payload(counts, child_exit_code=255))
        except Exception:
            pass
        return 255

    def handle_signal(signum, frame):
        if proc.poll() is None:
            proc.send_signal(signum)

    old_sigterm = signal.signal(signal.SIGTERM, handle_signal)
    old_sigint = signal.signal(signal.SIGINT, handle_signal)

    try:
        if proc.stdout is not None:
            process_stream(proc.stdout, counts, out_path)
            proc.stdout.close()
        proc.wait()
    finally:
        if proc.stdout is not None and not proc.stdout.closed:
            proc.stdout.close()
        signal.signal(signal.SIGTERM, old_sigterm)
        signal.signal(signal.SIGINT, old_sigint)

    exit_code = proc.returncode if proc.returncode is not None else 255
    write_status_atomic(out_path, build_status_payload(counts, child_exit_code=exit_code))
    return exit_code


def main() -> int:
    # Strictly launch fixed ['/usr/local/bin/droiddeck-steam-launch', 'desktop']
    # Arbitrary command-line arguments are rejected to enforce isolation
    if len(sys.argv) > 1:
        sys.stderr.write("native-client-status: arbitrary arguments not permitted\n")
        return 2
    return run_diagnostic()


if __name__ == "__main__":
    sys.exit(main())
