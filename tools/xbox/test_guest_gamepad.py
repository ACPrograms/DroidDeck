import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("gamepad", Path(__file__).with_name("guest-gamepad.py"))
pad = importlib.util.module_from_spec(spec)
spec.loader.exec_module(pad)


class ControllerProtocolTests(unittest.TestCase):
    def test_standard_buttons_sticks_triggers_and_hat(self):
        packet = pad.PACKET.pack(b"DDP1", 1, 4 | 1 | 64 | 512, -32767, 32767, 0, 123, 32767, 0)
        event = {(kind, code): value for kind, code, value in pad.events(packet)}
        self.assertEqual(pad.PACKET.size, 24)
        self.assertEqual(event[1, 304], 1)  # A -> BTN_SOUTH
        self.assertEqual(event[1, 315], 1)  # Menu -> BTN_START
        self.assertEqual(event[3, 0], -32767)
        self.assertEqual(event[3, 1], 32767)
        self.assertEqual(event[3, 2], 255)
        self.assertEqual(event[3, 5], 0)
        self.assertEqual((event[3, 16], event[3, 17]), (1, -1))

    def test_disconnect_neutral_state_releases_every_control(self):
        neutral = pad.PACKET.pack(b"DDP1", 0, 0, 0, 0, 0, 0, 0, 0)
        self.assertTrue(all(value == 0 for kind, code, value in pad.events(neutral)))

    def test_x_and_y_match_the_advertised_xbox_360_driver(self):
        for flag, code, other in ((16, 307, 308), (32, 308, 307)):
            packet = pad.PACKET.pack(b"DDP1", 1, flag, 0, 0, 0, 0, 0, 0)
            event = {(kind, key): value for kind, key, value in pad.events(packet)}
            self.assertEqual(event[1, code], 1)
            self.assertEqual(event[1, other], 0)

    def test_fragmented_tcp_packets_and_disconnect(self):
        expected = pad.PACKET.pack(b"DDP1", 7, 0, 0, 0, 0, 0, 0, 0)

        class Fragments:
            def __init__(self):
                self.remaining = expected

            def recv(self, count):
                result, self.remaining = self.remaining[:1], self.remaining[1:]
                return result

        connection = Fragments()
        self.assertEqual(pad.receive_exact(connection), expected)
        with self.assertRaises(ConnectionError):
            pad.receive_exact(connection)

    def test_unknown_protocol_and_flags_rejected(self):
        for magic, flags in ((b"bad!", 0), (b"DDP1", 0x80000000)):
            with self.assertRaises(ValueError):
                pad.events(pad.PACKET.pack(magic, 0, flags, 0, 0, 0, 0, 0, 0))


if __name__ == "__main__":
    unittest.main()
