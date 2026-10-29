"""Offline unit tests for DC2 (door state machine) against fake_dc2: the pack_offline checklist (prompts, handles,
fingerprint, untouched fixture, reference/negatives/alternatives with legacy and Unity 6000.4 entity IDs), scenario
shape, multi-seed builds, and the Play Mode grading under slow, fast and jittery frame clocks."""
import unittest

from .fake_dc2 import FakeEditorDC2
from .pack_offline import load_modules
from .test_pack_dev_dc import DevChallengeTests, DevOffline


def clocked(fps=None, jitter_seed=None, step_cost=0.0):
    return type("ClockedDC2", (FakeEditorDC2,), {"fps": fps or FakeEditorDC2.fps, "jitter_seed": jitter_seed,
                                                 "step_cost": step_cost})


class DC2Tests(DevChallengeTests, unittest.TestCase):
    TASK_ID = "DC2"
    EDITOR = FakeEditorDC2
    SEED = 7001
    MINUTES = 40
    PACKAGES = []
    FAILS_UNTOUCHED = {"C1", "C2", "C3", "C8", "X1", "X2", "X3"}

    def run_clocked(self, editor, seed=None):
        return DevOffline(self, self.TASK_ID, editor, self.SEED if seed is None else seed, tier=self.TIER, track="dev")

    def test_private_spec_matches_public(self):
        public = __import__(load_modules("DC2")[0].__class__.__module__, fromlist=["travel_seconds"])
        private = load_modules("DC2")[1]
        for seed in range(300):
            self.assertEqual(public.travel_seconds(seed), private.travel_seconds(seed))

    def test_plan_keeps_calls_at_full_speed(self):
        """At most 100 steps; every door call sits between reads with a frame wait on each side and runs at
        Time.timeScale 1; the scenario ends with timeScale 1 and the isPlaying assert."""
        private = load_modules("DC2")[1]
        for travel in (2.5, 3.0, 3.5):
            items = private.plan(travel)
            self.assertLessEqual(len(items), 100)
            scale = 1.0
            for index, item in enumerate(items):
                if item[0] == "scale":
                    scale = item[1]
                if item[0] == "call":
                    self.assertEqual(scale, 1.0)
                    self.assertEqual([items[index - 1], items[index + 1]], [("wait_frames", 1), ("wait_frames", 1)])
                    self.assertEqual(items[index - 2][0], "read")
                    self.assertEqual(items[index + 2][0], "read")
            self.assertEqual(items[-2:], [("scale", 1.0), ("assert",)])

    def test_reference_and_alternatives_on_any_clock(self):
        solutions = load_modules("DC2")[2]
        cases = [("reference", solutions.reference)] + list(solutions.ALTERNATIVES)
        for editor in (clocked(fps=10), clocked(fps=30), clocked(fps=144), clocked(jitter_seed=5),
                       clocked(jitter_seed=9)):
            for name, fn in cases:
                with self.subTest(fps=editor.fps, jitter=editor.jitter_seed, case=name):
                    run = self.run_clocked(editor)
                    card = run.verify(run.solve(fn))
                    self.assertTrue(card.strict, card.as_dict())

    def test_negatives_on_other_clocks(self):
        solutions = load_modules("DC2")[2]
        for editor in (clocked(fps=37), clocked(fps=120), clocked(jitter_seed=3)):
            for negative in solutions.NEGATIVES:
                with self.subTest(fps=editor.fps, jitter=editor.jitter_seed, negative=negative.name):
                    run = self.run_clocked(editor)
                    card = run.verify(run.solve(negative.apply))
                    self.assertEqual(set(card.failed()), negative.expect_failed)

    def test_everything_on_stepped_slow_and_very_fast_clocks(self):
        """Reference and every alternative pass strictly at 20 fps with a step cost, 60 fps and 1000 fps; every
        negative fails exactly its expected set at 20 fps with a step cost and at 1000 fps."""
        solutions = load_modules("DC2")[2]
        cases = [("reference", solutions.reference)] + list(solutions.ALTERNATIVES)
        for editor in (clocked(fps=20, step_cost=0.05), clocked(fps=60), clocked(fps=1000)):
            for name, fn in cases:
                with self.subTest(fps=editor.fps, cost=editor.step_cost, case=name):
                    run = self.run_clocked(editor)
                    card = run.verify(run.solve(fn))
                    self.assertTrue(card.strict, card.as_dict())
        for editor in (clocked(fps=20, step_cost=0.05), clocked(fps=1000)):
            for negative in solutions.NEGATIVES:
                with self.subTest(fps=editor.fps, cost=editor.step_cost, negative=negative.name):
                    run = self.run_clocked(editor)
                    card = run.verify(run.solve(negative.apply))
                    failed = set(card.failed())
                    if negative.name == "no_clamp" and editor.step_cost:
                        # a slow, step-heavy clock lets the door finish inside the X2 window, where overshoot counts
                        self.assertTrue(negative.expect_failed <= failed <= negative.expect_failed | {"X2"}, failed)
                    else:
                        self.assertEqual(failed, negative.expect_failed)

    def test_open_while_opening_is_planned(self):
        private = load_modules("DC2")[1]
        items = private.plan(2.5)
        labels = [item[1] for item in items if item[0] == "read"]
        self.assertEqual(labels[labels.index("opening") + 1:labels.index("opening") + 3],
                         ["open-while-opening", "opening-on"])

    def test_default_literal_forms(self):
        private = load_modules("DC2")[1]
        for literal in ("0.5f", ".5f", "0.50f", "0.5F", "5e-1f", "5E-1F"):
            src = "public class C { [SerializeField] private float travelSeconds = %s; }" % literal
            self.assertEqual(private.travel_default(src), (True, 0.5), literal)
        for literal in ("0.6f", "0.5", "1f"):
            src = "public class C { public float travelSeconds = %s; }" % literal
            serialized, value = private.travel_default(src)
            self.assertFalse(value is not None and abs(value - 0.5) < 1e-4, literal)

    def test_reversals_happen_mid_motion(self):
        """On the reference the reversing calls land while the door is between its ends, so the reversal checks are
        not empty."""
        private = load_modules("DC2")[1]
        for editor in (clocked(fps=15), clocked(fps=60)):
            for seed in (7001, 11, 12):
                run = self.run_clocked(editor, seed)
                run.verify(run.solve(load_modules("DC2")[2].reference))
                rows = {row["label"]: row for row in private.grade(run.scenario["steps"],
                                                                   private.travel_seconds(seed))}
                with self.subTest(fps=editor.fps, seed=seed):
                    for label in ("freeze", "closing-on", "reopen", "reopening", "reclose"):
                        self.assertTrue(0.05 < rows[label]["reading"]["o"] < 0.95, rows[label])

# Producer:Betsy
