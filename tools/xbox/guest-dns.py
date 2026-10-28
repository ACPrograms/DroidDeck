#!/usr/bin/env python3
"""Use reliable TCP DNS through the Xbox guest's existing DNS relay."""
from pathlib import Path


def tcp_dns_config(original):
    for line in original.splitlines():
        fields = line.split('#', 1)[0].split()
        if fields and fields[0] == 'options' and 'use-vc' in fields[1:]:
            return original
    return original.rstrip('\r\n') + '\noptions use-vc\n'


def main():
    # Resolve the OS-managed symlink without replacing it. The boot unit runs
    # after networking, before Steam. No DNS server or search domain changes.
    resolver = Path('/etc/resolv.conf').resolve(strict=True)
    original = resolver.read_text()
    candidate = tcp_dns_config(original)
    if candidate != original:
        resolver.write_text(candidate)


if __name__ == '__main__':
    main()
