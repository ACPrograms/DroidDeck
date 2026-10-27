#!/usr/bin/env python3
"""Xbox platform adapter: fixed controller state packets -> Linux standard uinput pad.

QEMU forwards ONLY host loopback port 2224. This accepts no commands or paths and
returns no guest data. Disconnects and stalled input release every control.
"""
import os
import socket
import struct

PACKET = struct.Struct("<4sII6h")
BUTTONS = {4: 304, 8: 305, 16: 308, 32: 307, 1024: 310, 2048: 311,
           2: 314, 1: 315, 4096: 317, 8192: 318}
AXES = (0, 1, 3, 4, 2, 5, 16, 17)


def events(packet):
    magic, sequence, buttons, lx, ly, rx, ry, lt, rt = PACKET.unpack(packet)
    if magic != b"DDP1" or buttons & ~0x3FFF:
        raise ValueError("Invalid controller packet")
    state = [(1, code, int(bool(buttons & flag))) for flag, code in BUTTONS.items()]
    values = (lx, ly, rx, ry, max(0, lt) * 255 // 32767, max(0, rt) * 255 // 32767,
              int(bool(buttons & 512)) - int(bool(buttons & 256)),
              int(bool(buttons & 128)) - int(bool(buttons & 64)))
    return state + [(3, code, value) for code, value in zip(AXES, values)] + [(0, 0, 0)]


def receive_exact(connection):
    data = bytearray()
    while len(data) < PACKET.size:
        part = connection.recv(PACKET.size - len(data))
        if not part:
            raise ConnectionError("Controller disconnected")
        data.extend(part)
    return data


def run():
    import fcntl
    fd = os.open("/dev/uinput", os.O_WRONLY | os.O_NONBLOCK)
    try:
        fcntl.ioctl(fd, 0x40045564, 1)  # UI_SET_EVBIT EV_KEY
        fcntl.ioctl(fd, 0x40045564, 3)  # EV_ABS
        for code in BUTTONS.values():
            fcntl.ioctl(fd, 0x40045565, code)
        for code in AXES:
            fcntl.ioctl(fd, 0x40045567, code)
        maximum, minimum = [0] * 64, [0] * 64
        for code in AXES:
            minimum[code], maximum[code] = (-32768, 32767) if code in (0, 1, 3, 4) else ((-1, 1) if code in (16, 17) else (0, 255))
        description = struct.pack("=80sHHHHI", b"Xbox Wireless Controller", 3, 0x045E, 0x028E, 0x0114, 0)
        description += struct.pack("=256i", *(maximum + minimum + [0] * 128))
        os.write(fd, description)
        fcntl.ioctl(fd, 0x5501)  # UI_DEV_CREATE

        def emit(packet):
            payload = b"".join(struct.pack("@llHHi", 0, 0, kind, code, value) for kind, code, value in events(packet))
            os.write(fd, payload)

        neutral = PACKET.pack(b"DDP1", 0, 0, 0, 0, 0, 0, 0, 0)
        with socket.socket() as listener:
            listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            listener.bind(("0.0.0.0", 2224))
            listener.listen(1)
            while True:
                connection, _ = listener.accept()
                with connection:
                    connection.settimeout(0.5)
                    try:
                        while True:
                            emit(receive_exact(connection))
                    except (OSError, ConnectionError, ValueError):
                        emit(neutral)
    finally:
        try:
            fcntl.ioctl(fd, 0x5502)
        finally:
            os.close(fd)


if __name__ == "__main__":
    run()
