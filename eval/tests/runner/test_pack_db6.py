"""Offline checks of DB6 (timed spawner) against fake_db6.FakeEditorDB6.

Runs the common DevTaskTests checklist (prompts, handles, fingerprint, untouched fixture, reference, all negatives with
their exact expected-failure sets, and all alternatives on URP, BuiltIn, and Unity 6000.4 entityId mode) plus DB6's
seed agreement, the timed scenario's shape and tolerances, the verdicts under the editor's real step pacing (one tick
per step at 20 fps, Play starting 0.3 s before the first step), and multi-seed rearrangement checks.
"""
import unittest

from .fake_db6 import FakeEditorDB6
from .pack_offline import load_modules
from .test_pack_dev_db import DevTaskTests


class SlowEditorDB6(FakeEditorDB6):
    """Play Mode at 20 fps with RuntimeScenarioService's pacing, and Play starting 0.3 s before the first step."""
    step_cost = 1.0 / 20
    poll = 1.0 / 20
    start_offset = 0.3


class DB6Tests(DevTaskTests, unittest.TestCase):
    TASK_ID = "DB6"
    EDITOR = FakeEditorDB6
    SEED = 7906
    FAILS_UNTOUCHED = {"F1", "F2", "F3", "F6", "F7"}

    def test_spec_agrees_and_never_takes_the_code_defaults(self):
        task, private, _solutions = load_modules(self.TASK_ID)
        public = __import__(type(task).__module__)
        for seed in range(7001, 7201):
            values = public.spec(seed)
            self.assertEqual(values, private.spec(seed), seed)
            self.assertNotEqual(values["interval"], private.DEFAULT_INTERVAL)
            self.assertNotEqual(values["maxCount"], private.DEFAULT_COUNT)
            # a spawn at half an interval (or twice as often) stays outside the gap tolerance
            self.assertGreater(values["interval"] / 2.0, private.TIME_TOL)

    def test_scenario_stamps_each_spawn(self):
        run = self.run_for()
        private = load_modules(self.TASK_ID)[1]
        values = private.spec(self.SEED)
        steps = private.SCENARIO(run.build_ctx)["steps"]
        kinds = [step["kind"] for step in steps]
        self.assertEqual(kinds, ["read", "read"] + ["wait_condition", "read"] * values["maxCount"] +
                         ["wait_seconds", "read", "read", "assert"])
        self.assertEqual([step["expected"] for step in steps if step["kind"] == "wait_condition"],
                         list(range(1, values["maxCount"] + 1)))
        self.assertEqual({step["skill"] for step in steps if step["kind"] == "read"},
                         {"editor_get_state", "gameobject_get_info"})
        self.assertTrue(steps[-2]["args"]["path"].endswith(private.clone_name(run.handles["prefab"])))

    def test_schedule_tolerances(self):
        private = load_modules(self.TASK_ID)[1]
        ok = lambda stamps: private.schedule_ok(stamps, 0.8)[0]   # noqa: E731
        self.assertTrue(ok([10.0, 10.8, 11.6, 12.4]))
        self.assertTrue(ok([10.0, 10.1, 10.9, 11.7]))              # Play started 0.7 s before the first stamp
        self.assertFalse(ok([10.0, 10.05, 10.85, 11.65]))          # 0.75 s early: a spawn at Play start
        self.assertTrue(ok([10.0, 11.1, 11.8, 12.9]))              # 0.3 late, gaps 0.7 and 1.1
        self.assertFalse(ok([10.0, 11.2, 12.0]))                   # first spawn 0.4 late
        self.assertFalse(ok([10.0, 10.8, 11.2, 11.6]))             # every half interval
        self.assertFalse(ok([10.0, 10.8, 12.4]))                   # one spawn skipped
        self.assertFalse(ok([10.0]))

    def test_verdicts_hold_at_20_fps_with_a_late_first_step(self):
        """Every case keeps its verdict when each step costs a 20 fps tick and Play starts 0.3 s early, for the
        manifest seed and the tightest spec (interval 0.8, maxCount 5)."""
        private, solutions = load_modules(self.TASK_ID)[1:]
        seeds = [10233] + [seed for seed in range(7001, 7400)
                           if private.spec(seed) == {"interval": 0.8, "maxCount": 5}][:1]
        for seed in seeds:
            with self.subTest(seed=seed, case="reference"):
                run = self.run_for(seed=seed)
                run.editor.__class__ = SlowEditorDB6
                card = run.verify(run.solve(solutions.reference))
                self.assertTrue(card.strict, card.failed())
            for name, fn in solutions.ALTERNATIVES:
                with self.subTest(seed=seed, alternative=name):
                    run = self.run_for(seed=seed)
                    run.editor.__class__ = SlowEditorDB6
                    self.assertTrue(run.verify(run.solve(fn)).strict)
            for negative in solutions.NEGATIVES:
                with self.subTest(seed=seed, negative=negative.name):
                    run = self.run_for(seed=seed)
                    run.editor.__class__ = SlowEditorDB6
                    self.assertEqual(set(run.verify(run.solve(negative.apply)).failed()), negative.expect_failed)

    def test_second_seed_rearranges_structure_and_keeps_verdicts(self):
        first = self.run_for(seed=7906)
        second = self.run_for(seed=7919)
        self.assertNotEqual(first.fingerprint, second.fingerprint)
        self.assertNotEqual(first.handles["script"], second.handles["script"])
        self.assertNotEqual(first.handles["prefab"], second.handles["prefab"])
        self.check_solutions_for_seed(7919)

    def check_solutions_for_seed(self, seed):
        solutions = load_modules(self.TASK_ID)[2]
        run = self.run_for(seed=seed, pipeline="URP", entity_mode="legacy")
        card = run.verify(run.solve(solutions.reference))
        self.assertTrue(card.strict)
        for negative in solutions.NEGATIVES:
            with self.subTest(seed=seed, negative=negative.name):
                run = self.run_for(seed=seed, pipeline="URP", entity_mode="legacy")
                card = run.verify(run.solve(negative.apply))
                self.assertEqual(set(card.failed()), negative.expect_failed)

# Producer:Betsy
