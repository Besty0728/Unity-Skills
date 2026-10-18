"""Offline checks of DB6 (timed spawner) against fake_db6.FakeEditorDB6.

Runs the common DevTaskTests checklist (prompts, handles, fingerprint, untouched fixture, reference, all negatives with
their exact expected-failure sets, and all alternatives on URP, BuiltIn, and Unity 6000.4 entityId mode) plus DB6's
seed-agreement, midpoint-window separation, and multi-seed rearrangement checks.
"""
import unittest

from .fake_db6 import FakeEditorDB6
from .pack_offline import load_modules
from .test_pack_dev_db import DevTaskTests


class DB6Tests(DevTaskTests, unittest.TestCase):
    TASK_ID = "DB6"
    EDITOR = FakeEditorDB6
    SEED = 7906
    FAILS_UNTOUCHED = {"F1", "F2", "F3", "F6", "F7"}

    def test_spec_agrees_and_midpoints_never_span_a_spawn(self):
        task, private, _solutions = load_modules(self.TASK_ID)
        public = __import__(type(task).__module__)
        for seed in range(7001, 7201):
            values = public.spec(seed)
            self.assertEqual(values, private.spec(seed), seed)
            interval, count = values["interval"], values["maxCount"]
            self.assertLess(private.WINDOW_S, interval / 2.0)
            self.assertNotEqual(interval, private.DEFAULT_INTERVAL)
            self.assertNotEqual(count, private.DEFAULT_COUNT)
            times = private.sample_times(values)
            self.assertEqual(len(times), count + 2)
            for index, moment in enumerate(times[:-1]):
                expected = min(index, count)
                self.assertEqual(private.due(count, interval, max(0.0, moment - private.WINDOW_S)), expected)
                self.assertEqual(private.due(count, interval, moment + private.WINDOW_S), expected)

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
