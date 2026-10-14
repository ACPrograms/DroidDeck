import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from test_quest_xr import validate_samples


class QuestXrValidationTest(unittest.TestCase):
    def samples(self, source='native'):
        return [{'rendered': count, 'sessionState': 5, 'headFlags': 15, 'source': source,
                 'controllersActive': [False, False], 'guestFrames': count,
                 'uploadedFrames': count, 'sourceFrameId': count + 1, 'steamvrState': 4}
                for count in (0, 144)]

    def test_native_and_surface_are_separate_diagnostics(self):
        self.assertIsNone(validate_samples(self.samples()))
        self.assertIsNone(validate_samples(self.samples('surface'), surface=True))
        self.assertEqual('Wrong projection source', validate_samples(self.samples('surface')))

    def test_position_must_be_tracked_not_merely_valid(self):
        samples = self.samples()
        for sample in samples:
            sample['headFlags'] = 7
        self.assertIn('tracked 6DoF', validate_samples(samples))

    def test_optional_controllers_cannot_be_claimed_as_tested(self):
        self.assertIn('controllers', validate_samples(self.samples(), require_controllers=True))

    def test_steamvr_requires_owned_images_and_source_frame_ids(self):
        samples = self.samples('frames')
        self.assertIsNone(validate_samples(samples, guest=True, steamvr=True))
        samples[-1]['uploadedFrames'] = 0
        self.assertIn('owned SteamVR images', validate_samples(samples, guest=True, steamvr=True))
        samples[-1]['uploadedFrames'] = 144
        samples[-1]['sourceFrameId'] = samples[0]['sourceFrameId']
        self.assertIn('frame IDs', validate_samples(samples, guest=True, steamvr=True))

    def test_running_process_alone_is_not_success(self):
        samples = self.samples('frames')
        samples[-1]['rendered'] = 0
        self.assertIn('stereo frames', validate_samples(samples, guest=True, steamvr=True))
        samples[-1]['rendered'] = 144
        samples[-1]['steamvrState'] = 3
        self.assertIn('RUNNING', validate_samples(samples, guest=True, steamvr=True))


if __name__ == '__main__':
    unittest.main()
