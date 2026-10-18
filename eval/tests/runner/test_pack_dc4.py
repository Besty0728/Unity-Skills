"""Offline unit tests for DC4 (ballistic launcher) against fake_dc4: runs the pack_offline checklist (prompts, handles,
fingerprint, untouched fixture, reference/negatives/alternatives with legacy and Unity 6000.4 entity IDs), scenario
shape checks, multi-seed builds and solutions, and physics trajectory separation between the reference solver and each
ballistic trap."""
import unittest

from .fake_dc4 import FakeEditorDC4
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

    def test_ballistic_traps_miss_targets_across_seeds(self):
        """Across seeds, the reference discrete solver lands within 0.05 m of every target pad under both linear and
        reciprocal damping, whereas the vacuum parabola (no_drag) and Earth-gravity (-9.81) trajectories miss by more
        than the 0.25 m threshold."""
        private = load_modules("DC4")[1]
        for seed in (7001, 11, 12, 13, 14):
            run = self.run_for(seed=seed)
            spec = private.launcher_spec(seed)
            origin = [run.snap["data"]["muzzle"]["position"][k] for k in ("x", "y", "z")]
            for _ref, state in run.snap["data"]["pads"]:
                goal = [state["position"][k] for k in ("x", "y", "z")]
                v_ref = private.solve_launch_velocity(origin, goal, spec["gravityY"], spec["drag"], flight_steps=60)
                trajs_ref = private.simulate_trajectory(origin, v_ref, spec["gravityY"], spec["drag"])
                best_ref = min(private.min_distance_to_goal(pts, goal) for pts in trajs_ref)
                self.assertLessEqual(best_ref, 0.05)

                t_vac = 1.2
                v_vac = ((goal[0] - origin[0]) / t_vac,
                         (goal[1] - origin[1] - 0.5 * spec["gravityY"] * t_vac * t_vac) / t_vac,
                         (goal[2] - origin[2]) / t_vac)
                trajs_vac = private.simulate_trajectory(origin, v_vac, spec["gravityY"], spec["drag"])
                best_vac = min(private.min_distance_to_goal(pts, goal) for pts in trajs_vac)
                self.assertGreater(best_vac, private.HIT_TOL)

                v_earth = private.solve_launch_velocity(origin, goal, -9.81, spec["drag"], flight_steps=60)
                trajs_earth = private.simulate_trajectory(origin, v_earth, spec["gravityY"], spec["drag"])
                best_earth = min(private.min_distance_to_goal(pts, goal) for pts in trajs_earth)
                self.assertGreater(best_earth, private.HIT_TOL)

# Producer:Betsy
