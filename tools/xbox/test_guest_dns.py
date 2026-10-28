import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('guest_dns', Path(__file__).with_name('guest-dns.py'))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class GuestDnsTests(unittest.TestCase):
    def test_preserves_servers_search_and_other_options(self):
        original = '# generated\nnameserver 10.0.2.3\nnameserver fec0::3\nsearch example.test\noptions timeout:2\n'
        candidate = module.tcp_dns_config(original)
        self.assertEqual(candidate, original + 'options use-vc\n')
        self.assertEqual(module.tcp_dns_config(candidate), candidate)

    def test_existing_option_is_not_rewritten(self):
        original = 'nameserver 10.0.2.3\noptions rotate use-vc timeout:2 # retain\n'
        self.assertEqual(module.tcp_dns_config(original), original)

    def test_comment_does_not_activate_option(self):
        original = 'nameserver 10.0.2.3\n# options use-vc'
        self.assertEqual(module.tcp_dns_config(original), original + '\noptions use-vc\n')


if __name__ == '__main__':
    unittest.main()
