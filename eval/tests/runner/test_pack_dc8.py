"""Offline unit tests for DC8 (ScriptableObject difficulty configs, a spawner switched between them) against fake_dc8:
the pack_offline checklist (prompts, handles, fingerprint, untouched fixture, reference/negatives/alternatives with
legacy and Unity 6000.4 entity IDs), scenario shape, multi-seed builds, and the Play Mode grading on fast and slow
editors (frame rate and per-step cost), partial waves at the cap and the step plan."""
import unittest

from .fake_dc8 import FakeEditorDC8
from .pack_offline import load_modules
from .test_pack_dev_dc import DevChallengeTests, DevOffline


def clocked(fps=None, step_cost=0.0, start_hitch=0.0):
    return type("ClockedDC8", (FakeEditorDC8,), {"fps": fps or FakeEditorDC8.fps, "step_cost": step_cost,
                                                "start_hitch": start_hitch})


class DC8Tests(DevChallengeTests, unittest.TestCase):
    TASK_ID = "DC8"
    EDITOR = FakeEditorDC8
    SEED = 10536
    MINUTES = 45
    PACKAGES = []
    FAILS_UNTOUCHED = {"C1", "C2", "C3", "C4", "C9", "C10", "C11", "C12", "C13", "C14"}

    def run_clocked(self, editor, seed=None):
        return DevOffline(self, self.TASK_ID, editor, self.SEED if seed is None else seed, tier=self.TIER, track="dev")

    def modules(self):
        return load_modules("DC8")

    def test_private_spec_matches_public(self):
        public = __import__(self.modules()[0].__class__.__module__, fromlist=["difficulty_spec"])
        private = self.modules()[1]
        for seed in range(300):
            self.assertEqual(public.difficulty_spec(seed), private.difficulty_spec(seed))

    def test_spec_keeps_the_ordering_the_phases_rely_on(self):
        """Easy caps below normal (the switch to it finds the container above its cap), hard at least 5 above normal."""
        private = self.modules()[1]
        for seed in range(300):
            spec = private.difficulty_spec(seed)
            self.assertLess(spec["easy"]["maxAlive"], spec["normal"]["maxAlive"])
            self.assertGreaterEqual(spec["hard"]["maxAlive"] - spec["normal"]["maxAlive"], 5)

    def test_partial_waves_at_the_cap(self):
        """Each phase's last wave only fills up to maxAlive, and every wave before it is full."""
        private = self.modules()[1]
        for seed in range(300):
            spec = private.difficulty_spec(seed)
            for start, values in ((0, spec["normal"]), (spec["normal"]["maxAlive"], spec["hard"])):
                counts = private.wave_counts(start, values)
                self.assertEqual(counts[-1], values["maxAlive"])
                steps = [b - a for a, b in zip([start] + counts, counts)]
                self.assertTrue(all(step == values["spawnsPerWave"] for step in steps[:-1]), (seed, steps))
                self.assertLessEqual(steps[-1], values["spawnsPerWave"])
        partial = [seed for seed in range(300) if private.difficulty_spec(seed)["normal"]["maxAlive"] % 2 == 1]
        self.assertTrue(partial)

    def test_reference_container_follows_the_waves(self):
        """The counts the reference's waits matched with are the spec's: (a) from 0 with the normal asset, (d) from the
        normal cap with the hard one."""
        private = self.modules()[1]
        run = self.run_clocked(self.EDITOR)
        run.verify(run.solve(self.modules()[2].reference))
        spec = private.difficulty_spec(self.SEED)
        seen = [row["actual"] for row in run.scenario["steps"] if row.get("kind") == "wait_condition"]
        want = private.wave_counts(0, spec["normal"]) + private.wave_counts(spec["normal"]["maxAlive"], spec["hard"])
        self.assertEqual(seen, want)

    def test_reference_and_alternatives_on_any_clock(self):
        solutions = self.modules()[2]
        cases = [("reference", solutions.reference)] + list(solutions.ALTERNATIVES)
        for editor in (clocked(fps=20, step_cost=0.05), clocked(fps=30), clocked(fps=144),
                       clocked(fps=60, step_cost=0.02, start_hitch=0.25)):
            for name, fn in cases:
                with self.subTest(fps=editor.fps, cost=editor.step_cost, case=name):
                    run = self.run_clocked(editor)
                    card = run.verify(run.solve(fn))
                    self.assertTrue(card.strict, card.as_dict())

    def test_negatives_on_other_clocks(self):
        solutions = self.modules()[2]
        for editor in (clocked(fps=20, step_cost=0.05), clocked(fps=120)):
            for negative in solutions.NEGATIVES:
                with self.subTest(fps=editor.fps, cost=editor.step_cost, negative=negative.name):
                    run = self.run_clocked(editor)
                    card = run.verify(run.solve(negative.apply))
                    self.assertEqual(set(card.failed()), negative.expect_failed)

    def test_reference_on_other_seeds_and_clocks(self):
        solutions = self.modules()[2]
        for seed in (11, 12, 13, 14):
            for editor in (clocked(fps=20, step_cost=0.05), clocked(fps=60)):
                with self.subTest(seed=seed, fps=editor.fps):
                    run = self.run_clocked(editor, seed)
                    card = run.verify(run.solve(solutions.reference))
                    self.assertTrue(card.strict, card.as_dict())

    def test_very_slow_editors_are_where_the_timing_gives_out(self):
        """Documents the limit: at 10 fps with 0.1 s per step the reference is strict, at 3 fps without a step cost
        phase (a) loses C10 (frames of about 0.33 s against EARLY_SLACK 1.0 and TIME_TOL 0.3)."""
        solutions = self.modules()[2]
        run = self.run_clocked(clocked(fps=10, step_cost=0.1))
        self.assertTrue(run.verify(run.solve(solutions.reference)).strict)
        run = self.run_clocked(clocked(fps=3))
        self.assertFalse(run.verify(run.solve(solutions.reference)).strict)

    def test_plan_ends_with_the_play_assert_inside_the_timeout_cap(self):
        run = self.run_clocked(self.EDITOR)
        run.verify(run.solve(self.modules()[2].reference))
        steps = run.spec["steps"]
        self.assertLessEqual(run.spec["timeoutSeconds"], 600)
        self.assertEqual((steps[-1]["kind"], steps[-1]["select"], steps[-1]["expected"]),
                         ("assert", "isPlaying", True))
        self.assertTrue(run.scenario["passed"], run.scenario["error"])

# Producer:Betsy
