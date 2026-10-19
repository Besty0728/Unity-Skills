"""Offline unit tests for DC4 (ballistic launcher) against fake_dc4: runs the pack_offline checklist (prompts, handles,
fingerprint, untouched fixture, reference/negatives/alternatives with legacy and Unity 6000.4 entity IDs), scenario
shape checks, multi-seed builds and solutions, and the flight separation between the reference solver and the
vacuum-parabola and Earth-gravity launches."""
import unittest

from .fake_dc4 import FakeEditorDC4, closest_approach, integrate, parabola_velocity, solve_velocity
from .pack_offline import load_modules
from .test_pack_dev_dc import DevChallengeTests


class DC4Tests(DevChallengeTests, unittest.TestCase):
    TASK_ID = "DC4"
    EDITOR = FakeEditorDC4
    SEED = 7001
    MINUTES = 45
    PACKAGES = []
    FAILS_UNTOUCHED = {"D1", "D2", "D3", "D8", "D9", "D10"}

    def test_private_spec_matches_public(self):
        public = __import__(load_modules("DC4")[0].__class__.__module__, fromlist=["launcher_spec"])
        private = load_modules("DC4")[1]
        for seed in range(300):
            self.assertEqual(public.launcher_spec(seed), private.launcher_spec(seed))

    def test_teardown_restores_project_gravity(self):
        run = self.run_for()
        spec = load_modules("DC4")[1].launcher_spec(self.SEED)
        self.assertAlmostEqual(run.editor.gravity["y"], spec["gravityY"], places=3)
        run.teardown()
        self.assertAlmostEqual(run.editor.gravity["y"], -9.81, places=3)
        run.teardown()
        self.assertAlmostEqual(run.editor.gravity["y"], -9.81, places=3)

    def test_flight_paths_across_seeds(self):
        """The reference solver passes within 0.05 m of every pad; the vacuum parabola and Earth gravity miss by more
        than the hit tolerance."""
        private = load_modules("DC4")[1]
        for seed in (7001, 11, 12, 13, 14):
            run = self.run_for(seed=seed)
            spec = private.launcher_spec(seed)
            origin = [run.snap["data"]["muzzle"]["position"][k] for k in ("x", "y", "z")]
            for _ref, state in run.snap["data"]["pads"]:
                goal = [state["position"][k] for k in ("x", "y", "z")]
                reference = solve_velocity(origin, goal, spec["gravityY"], spec["drag"])
                parabola = parabola_velocity(origin, goal, spec["gravityY"])
                earth = solve_velocity(origin, goal, -9.81, spec["drag"])
                with self.subTest(seed=seed, pad=goal):
                    for velocity, hits in ((reference, True), (parabola, False), (earth, False)):
                        points = integrate(origin, velocity, spec["gravityY"], spec["drag"])
                        reach = closest_approach(points, goal)
                        self.assertLessEqual(reach, 0.05) if hits else self.assertGreater(reach, private.HIT_TOL)

# Producer:Betsy
