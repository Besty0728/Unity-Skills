"""Offline checks of DS1 (patrol path) and DS3 (configurable waves) against fake_dev_ds: the common task checklist of
pack_offline plus the SCENARIO played on the model (verify_runtime included), the seed-copied specs and the start
state the behaviour builds on. The live selftest on a URP editor remains the gate: the model only knows the behaviour
the tasks ask for and the wrong variants the negatives mark."""
import json
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
        self.assertTrue(all(kind in ("wait_seconds", "read", "assert") for kind in kinds))


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
        times = private.sample_times(waypoints, spec["speed"], spec["pause"])
        self.assertEqual(len(times), 10)
        self.assertLess(times[-1], 60)
        other = self.run_for(seed=8213)
        self.assertNotEqual(run.fingerprint, other.fingerprint)


class DS3Tests(DevTaskTests, unittest.TestCase):
    TASK_ID = "DS3"
    EDITOR = FakeEditorDS3
    SEED = 8314
    MINUTES = 35
    FAILS_UNTOUCHED = {"D1", "D2", "D3", "D4", "D9", "D10", "D11"}

    def test_spec_agrees_and_own_midpoints_never_span_a_spawn(self):
        task, private, _solutions = load_modules(self.TASK_ID)
        public = __import__(type(task).__module__)
        for seed in range(7001, 7201):
            spec = public.wave_spec(seed)
            self.assertEqual(spec, private.wave_spec(seed), seed)
            for key in ("a", "b"):
                count, interval = spec[key]["count"], spec[key]["interval"]
                self.assertLess(private.WINDOW_S, interval / 2.0)
                self.assertNotEqual((count, interval), (2, 2.0))
                for index in range(count + 1):
                    moment = interval * (index + 0.5)
                    self.assertIn(round(moment, 3), private.sample_times(spec))
                    self.assertEqual(private.due(count, interval, moment - private.WINDOW_S),
                                     private.due(count, interval, moment + private.WINDOW_S), (seed, key, moment))
        run = self.run_for()
        other = self.run_for(seed=8415)
        self.assertNotEqual(run.fingerprint, other.fingerprint)
        self.assertNotEqual(run.handles["prefab"], other.handles["prefab"])

# Producer:Betsy
