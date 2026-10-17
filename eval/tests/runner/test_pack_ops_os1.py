"""Offline checks of OS1 (three-point lighting rig) against fake_ops_os1: the common task checklist of pack_offline
plus the seed-driven frame of the subject (a rotated parent, a non-zero world yaw) and the rig derivation copied into
the private verifier. The live selftest remains the gate."""
import json
import unittest

from .fake_ops_os1 import FakeEditorOS1
from .pack_offline import OpsTaskTests, load_modules
from usk_eval import pack  # noqa: E402


class OS1Tests(OpsTaskTests, unittest.TestCase):
    TASK_ID = "OS1"
    EDITOR = FakeEditorOS1
    SEED = 7910
    FAILS_UNTOUCHED = {"L1", "L2", "L3", "L4", "L5", "L6", "L7", "L9"}

    def test_task_attributes(self):
        task = load_modules(self.TASK_ID)[0]
        self.assertEqual((task.id, task.track, task.tier, task.case_version, task.minutes),
                         (self.TASK_ID, "ops", "standard", 1, 15))
        requires = pack.normalize_requires(task.requires)
        self.assertEqual((requires["pipeline"], requires["mode"], requires["dedicated"], requires["packages"]),
                         ("URP", "bypass", True, []))

    def test_rig_spec_agrees_between_public_and_private(self):
        task, private, _solutions = load_modules(self.TASK_ID)
        public = __import__(type(task).__module__)
        seen = set()
        for seed in range(7001, 7301):
            spec = public.rig_spec(seed)
            self.assertEqual(spec, private.rig_spec(seed), seed)
            for role in ("fill", "rim"):
                self.assertNotEqual(spec["lights"][role]["pct"], 100)
            for role in ("key", "rim"):
                self.assertNotEqual(spec["lights"][role]["spot"], 30)
            seen.add(json.dumps(spec, sort_keys=True))
        self.assertGreater(len(seen), 100)

    def test_subject_frame_differs_from_the_world_axes(self):
        run = self.run_for()
        subject, stage = run.snap["data"]["subject"], run.snap["data"]["stage"]
        self.assertNotAlmostEqual(stage["rotation"]["y"] % 360.0, 0.0)
        self.assertNotAlmostEqual(subject["rotation"]["y"] % 360.0, 0.0)
        lamp = run.snap["data"]["lamp"]["light"]
        self.assertTrue(lamp["enabled"])
        other = self.run_for(seed=8011)
        self.assertNotEqual(run.fingerprint, other.fingerprint)

# Producer:Betsy
