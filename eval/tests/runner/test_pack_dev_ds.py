"""Offline checks of DS1 (patrol path) and DS3 (configurable waves) against fake_dev_ds: the common task checklist of
pack_offline plus the SCENARIO played on the model (verify_runtime included), the seed-copied specs and the start
state the behaviour builds on. The live selftest on a URP editor remains the gate: the model only knows the behaviour
the tasks ask for and the wrong variants the negatives mark."""
import json
import types
import unittest

from .fake_dev_ds import FakeEditorDS1, FakeEditorDS3
from .pack_offline import Offline, OpsTaskTests, load_modules
from usk_eval import journal, pack, verifylib  # noqa: E402
from usk_eval.rest import RUNNER_AGENT  # noqa: E402
from usk_eval.task_api import VerifyContext  # noqa: E402


class DevOffline(Offline):
    """pack_offline.Offline plus the dev track's S9: the SCENARIO played on the model, then verify_runtime."""

    def verify(self, answer):
        start = self.editor.seq
        self.editor.refresh()
        window = {"from": start, "to": self.editor.seq}
        entries = [entry for entry in self.editor.entries if entry["seq"] > self.cursor0]
        ctx = VerifyContext(self.editor.client(RUNNER_AGENT), self.scope, self.seed, self.project, self.env,
                            self.lang, self.track, self.tier, self.task, handles=self.handles,
                            c0=self.snap.get("data"), c0_digests=self.snap.get("digests"),
                            answer=json.loads(json.dumps(answer)) if answer is not None else None,
                            records=journal.flatten(entries, window), journal_complete=True,
                            journal={"cursor0": self.cursor0, "cursorEnd": self.editor.seq, "refreshWindow": window,
                                     "forcedPlayModeExit": False},
                            allowed_change=getattr(self.private, "allowed_change", None))
        card = verifylib.Scorecard(self.private.ASSERTIONS)
        self.private.verify(ctx, card)
        definition = getattr(self.private, "SCENARIO", None)
        self.scenario = None
        if definition is not None:
            self.scenario = self.editor.run_scenario(definition(ctx) if callable(definition) else definition)
            ctx.scenario = self.scenario
            self.private.verify_runtime(ctx, card)
        return card.finish()


class DevTaskTests(OpsTaskTests):
    TIER = "standard"
    MINUTES = None

    def run_for(self, **kwargs):
        kwargs.setdefault("tier", self.TIER)
        kwargs.setdefault("track", "dev")
        return DevOffline(self, self.TASK_ID, self.EDITOR, kwargs.pop("seed", self.SEED), **kwargs)

    def test_task_attributes(self):
        task = load_modules(self.TASK_ID)[0]
        self.assertEqual((task.id, task.track, task.tier, task.case_version, task.minutes, task.needs_focus),
                         (self.TASK_ID, "dev", "standard", 1, self.MINUTES, True))
        requires = pack.normalize_requires(task.requires)
        self.assertEqual((requires["pipeline"], requires["mode"], requires["dedicated"], requires["packages"]),
                         ("URP", "bypass", True, []))

    def test_scenario_shape(self):
        run = self.run_for()
        run.verify(run.solve(load_modules(self.TASK_ID)[2].reference))
        spec = run.scenario
        self.assertTrue(spec["passed"], spec)
        kinds = [step["kind"] for step in spec["steps"]]
        self.assertEqual(kinds[-1], "assert")
        self.assertNotIn("invoke", kinds)
        self.assertTrue(all(kind in ("wait_seconds", "wait_condition", "read", "assert") for kind in kinds))


class DS1Tests(DevTaskTests, unittest.TestCase):
    TASK_ID = "DS1"
    EDITOR = FakeEditorDS1
    SEED = 8112
    MINUTES = 30
    FAILS_UNTOUCHED = {"D1", "D2", "D3", "D8", "D9"}

    def test_spec_agrees_and_guard_starts_on_the_first_waypoint(self):
        task, private, _solutions = load_modules(self.TASK_ID)
        public = __import__(type(task).__module__)
        for seed in range(7001, 7201):
            self.assertEqual(public.patrol_spec(seed), private.patrol_spec(seed), seed)
        run = self.run_for()
        c0 = run.snap["data"]
        waypoints = private.waypoints_of(c0)
        self.assertEqual(len(waypoints), 4)
        self.assertTrue(verifylib.close_tuple(c0["guard"]["position"], waypoints[0], 1e-4))
        self.assertNotAlmostEqual(c0["route"]["rotation"]["y"] % 360.0, 0.0)
        self.assertNotAlmostEqual(c0["post"]["rotation"]["y"] % 360.0, 0.0)
        spec = private.patrol_spec(run.seed)
        gaps = private.expected_gaps(waypoints, spec["speed"], spec["pause"])
        self.assertEqual(len(gaps), len(private.ARRIVALS))
        self.assertLess(sum(gaps), 60)
        other = self.run_for(seed=8213)
        self.assertNotEqual(run.fingerprint, other.fingerprint)

    def test_scenario_stamps_each_arrival(self):
        run = self.run_for()
        private = load_modules(self.TASK_ID)[1]
        ctx = types.SimpleNamespace(seed=run.seed, handles=run.handles, c0=run.snap["data"])
        steps = private.SCENARIO(ctx)["steps"]
        self.assertEqual([step["kind"] for step in steps], ["read"] + ["wait_condition", "read"] * 5 + ["assert"])
        waypoints = private.waypoints_of(run.snap["data"])
        waits = [step for step in steps if step["kind"] == "wait_condition"]
        self.assertEqual([tuple(step["expected"][axis] for axis in "xyz") for step in waits],
                         [waypoints[index] for index in private.ARRIVALS])
        self.assertEqual({(step["skill"], step["select"], step["tolerance"]) for step in waits},
                         {("gameobject_get_info", "position", private.POSITION_TOL)})
        self.assertEqual({step["skill"] for step in steps if step["kind"] == "read"}, {"editor_get_state"})

    def test_arrival_tolerances(self):
        private = load_modules(self.TASK_ID)[1]
        gaps = [1.5, 2.0, 2.5, 2.0, 2.5]                          # first leg, then pause + leg; lap 9.0

        def score(stamps):
            return private.arrivals_ok(stamps, gaps)[:2]

        exact = [10.0, 11.5, 13.5, 16.0, 18.0, 20.5]
        self.assertEqual(score(exact), (6, 6))
        self.assertEqual(score([10.0, 10.8, 12.8, 15.3, 17.3, 19.8]), (6, 6))   # Play started 0.7 s before t0
        self.assertEqual(score([10.0, 12.3, 14.6, 17.1, 19.1, 21.7]), (6, 6))   # 0.8 late, later gaps 0.3/0.1 long
        self.assertEqual(score([10.0, 10.75, 12.75, 15.25, 17.25, 19.75]), (5, 6))  # 0.75 s early: too early
        self.assertEqual(score([10.0, 11.5, 13.2, 15.7, 17.7, 20.2]), (6, 6))  # a gap and the lap 0.3 short: bounds hold
        self.assertEqual(score([10.0, 11.5, 13.15, 15.65, 17.65, 20.15]), (4, 6))  # 0.35 short: the gap and the lap
        self.assertEqual(score([10.0, 11.5, 14.05, 16.55, 18.55, 21.05]), (4, 6))  # 0.55 long: beyond the hitch allowance
        self.assertEqual(score([10.0, 11.5, 13.9, 16.8, 19.2, 21.7]), (5, 6))  # gaps up to 0.4 long each: the lap drifts
        self.assertEqual(score([10.0, 11.5, 13.5]), (2, 6))                     # the third wait timed out

    def test_verdicts_hold_at_20_fps_with_a_late_first_step(self):
        """Every case keeps its verdict when each step costs a 20 fps tick and Play starts 0.3 s early, for the default
        seed and the fastest spec (speed 3, pause 0.5): the pacing that broke the fixed sampling schedule live."""
        private, solutions = load_modules(self.TASK_ID)[1:]
        fastest = next(seed for seed in range(7001, 7400) if private.patrol_spec(seed) == {"speed": 3.0, "pause": 0.5})
        for seed in (self.SEED, fastest):
            cases = [("reference", solutions.reference, set())] + \
                [(name, fn, set()) for name, fn in solutions.ALTERNATIVES] + \
                [(negative.name, negative.apply, negative.expect_failed) for negative in solutions.NEGATIVES]
            for name, fn, failed in cases:
                with self.subTest(seed=seed, case=name):
                    run = self.run_for(seed=seed)
                    run.editor.__class__ = SlowEditorDS1
                    self.assertEqual(set(run.verify(run.solve(fn)).failed()), failed)


class SlowEditorDS1(FakeEditorDS1):
    """Play Mode at 20 fps with RuntimeScenarioService's pacing, and Play starting 0.3 s before the first step."""
    step_cost = 1.0 / 20
    play_lead = 0.3


class DS3Tests(DevTaskTests, unittest.TestCase):
    TASK_ID = "DS3"
    EDITOR = FakeEditorDS3
    SEED = 8314
    MINUTES = 35
    FAILS_UNTOUCHED = {"D1", "D2", "D3", "D4", "D9", "D10", "D11"}

    def test_spec_agrees_and_container_a_is_empty_for_a_capped_first_frame(self):
        task, private, _solutions = load_modules(self.TASK_ID)
        public = __import__(type(task).__module__)
        for seed in range(7001, 7201):
            spec = public.wave_spec(seed)
            self.assertEqual(spec, private.wave_spec(seed), seed)
            # The start read lands within the first frames; Time.maximumDeltaTime caps one at 1/3 s of game time.
            self.assertGreater(spec["a"]["interval"], 2.0 / 3.0)
            for key in ("a", "b"):
                self.assertNotEqual((spec[key]["count"], spec[key]["interval"]), (2, 2.0))
        run = self.run_for()
        other = self.run_for(seed=8415)
        self.assertNotEqual(run.fingerprint, other.fingerprint)
        self.assertNotEqual(run.handles["prefab"], other.handles["prefab"])

    def test_scenario_stamps_each_spawn(self):
        run = self.run_for()
        private = load_modules(self.TASK_ID)[1]
        ctx = types.SimpleNamespace(seed=run.seed, handles=run.handles, c0=run.snap["data"])
        steps = private.SCENARIO(ctx)["steps"]
        events = private.spawn_events(private.wave_spec(run.seed))
        self.assertEqual([step["kind"] for step in steps],
                         ["read", "read"] + ["wait_condition", "read"] * len(events) +
                         ["wait_seconds", "read", "read", "assert"])
        waits = [step for step in steps if step["kind"] == "wait_condition"]
        handles = dict(private.CONTAINERS)
        self.assertEqual([(step["args"], step["expected"] - private.AT_LEAST) for step in waits],
                         [(verifylib.address(run.handles[handles[key]]), k) for _t, key, k in events])
        self.assertEqual({(step["select"], step["tolerance"]) for step in waits}, {("childCount", private.AT_LEAST)})
        self.assertEqual([moment for moment, _key, _k in events], sorted(moment for moment, _key, _k in events))

    def test_spawn_tolerances(self):
        private = load_modules(self.TASK_ID)[1]

        def score(stamps):
            return private.spawns_ok(10.0, stamps, 3, 1.0)[:2]

        self.assertEqual(score([11.0, 12.0, 13.0]), (4, 4))
        self.assertEqual(score([10.3, 11.3, 12.3]), (4, 4))                    # Play started 0.7 s before t0
        self.assertEqual(score([11.8, 12.8, 13.8]), (4, 4))                    # first spawn 0.8 s late
        self.assertEqual(score([10.25, 11.25, 12.25]), (3, 4))                 # 0.75 s early: a spawn at Play start
        self.assertEqual(score([11.0, 11.7, 12.7]), (4, 4))                    # a gap 0.3 short: bounds hold
        self.assertEqual(score([11.0, 11.65, 12.65]), (2, 4))                  # 0.35 short: the gap and the span
        self.assertEqual(score([11.0, 12.55, 13.55]), (2, 4))                  # 0.55 long: the gap and the span
        self.assertEqual(score([11.0, 12.0]), (2, 4))                          # the third wait timed out

    def test_verdicts_hold_at_20_fps_with_a_late_first_step(self):
        """Every case keeps its verdict when each step costs a 20 fps tick and Play starts 0.3 s early, for the default
        seed and the fastest spawner B (interval 0.5): the pacing that broke the fixed sampling schedule live."""
        private, solutions = load_modules(self.TASK_ID)[1:]
        fastest = next(seed for seed in range(7001, 7400) if private.wave_spec(seed)["b"]["interval"] == 0.5)
        for seed in (self.SEED, fastest):
            cases = [("reference", solutions.reference, set())] + \
                [(name, fn, set()) for name, fn in solutions.ALTERNATIVES] + \
                [(negative.name, negative.apply, negative.expect_failed) for negative in solutions.NEGATIVES]
            for name, fn, failed in cases:
                with self.subTest(seed=seed, case=name):
                    run = self.run_for(seed=seed)
                    run.editor.__class__ = SlowEditorDS3
                    self.assertEqual(set(run.verify(run.solve(fn)).failed()), failed)


class SlowEditorDS3(FakeEditorDS3):
    """Play Mode at 20 fps with RuntimeScenarioService's pacing, and Play starting 0.3 s before the first step."""
    step_cost = 1.0 / 20
    play_lead = 0.3

# Producer:Betsy
