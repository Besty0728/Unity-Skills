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

    def test_intensity_tolerance_by_either_reading_of_the_percentages(self):
        """R4: a percentage of the specified key or of the key's actual intensity; 0.01 each, nothing accumulates."""
        private = load_modules(self.TASK_ID)[1]
        spec = private.rig_spec(self.SEED)
        key, fill, rim = (spec["lights"][role] for role in ("key", "fill", "rim"))
        self.assertEqual((key["intensity"], fill["pct"], rim["pct"]), (3.0, 50, 80))

        def ok(role, value, key_value):
            return private.intensity_ok(spec, role, value, key_value)

        self.assertTrue(ok("key", 3.009, 3.009))                    # the review's example, all inside
        self.assertTrue(ok("fill", 1.5135, 3.009))
        self.assertTrue(ok("rim", 2.4162, 3.009))
        self.assertTrue(ok("fill", 1.509, 2.991))                   # nominal reading, key low but inside
        self.assertFalse(ok("key", 3.011, 3.011))
        self.assertFalse(ok("fill", 1.511, 3.0))                    # outside both readings
        self.assertFalse(ok("rim", 2.4162, 3.011))                  # the actual key is no base once it is out
        self.assertFalse(ok("fill", None, 3.0))

    def test_old_lamp_snapshot_covers_light_values_and_components(self):
        run = self.run_for()
        lamp = run.snap["data"]["lamp"]
        self.assertEqual([row[0] for row in lamp["values"]], ["Light", "Transform"])
        self.assertNotIn("m_Enabled", [path for path, _value in dict(lamp["values"])["Light"]])
        self.assertIn(["m_Shadows.m_Type", "0"], dict(lamp["values"])["Light"])
        self.assertEqual(lamp["components"], [["Transform", None], ["Light", None]])

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
