"""Offline checks of DS2 (trigger-activated sliding door) against fake_ds2: the common task checklist of pack_offline
plus the SCENARIO played on the model (verify_runtime included), the seed-copied specs, and the initial fixture state.
"""
import json
import unittest

from .fake_ds2 import FakeEditorDS2
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


class DS2Tests(OpsTaskTests, unittest.TestCase):
    TASK_ID = "DS2"
    TIER = "standard"
    EDITOR = FakeEditorDS2
    SEED = 8220
    MINUTES = 30
    FAILS_UNTOUCHED = {"D1", "D2", "D3", "D8", "D9", "D10"}

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

    def test_spec_agrees_and_fixture_starts_unconfigured(self):
        task, private, _solutions = load_modules(self.TASK_ID)
        public = __import__(type(task).__module__)
        for seed in range(7001, 7201):
            spec = public.door_spec(seed)
            self.assertEqual(spec, private.door_spec(seed), seed)
            self.assertNotEqual((spec["ox"], spec["oy"], spec["oz"]), (0.0, 1.0, 0.0))
            self.assertNotEqual(spec["speed"], 2.0)
        run = self.run_for()
        c0 = run.snap["data"]
        self.assertEqual(c0["player"]["tag"], "Player")
        self.assertEqual(c0["visitor"]["tag"], "Untagged")
        self.assertNotAlmostEqual(c0["hall"]["rotation"]["y"] % 360.0, 0.0)
        other = self.run_for(seed=8321)
        self.assertNotEqual(run.fingerprint, other.fingerprint)

    def test_sample_margins_and_reference_strict_across_300_seeds(self):
        import sys
        _task, private, solutions = load_modules(self.TASK_ID)
        margin = private.MIN_MARGIN_S
        seen_specs = {}
        for seed in range(4100, 4400):
            spec = private.door_spec(seed)
            seen_specs.setdefault((spec["ox"], spec["oy"], spec["oz"], spec["speed"]), seed)
            ev = private.event_instants(seed)
            sched = private.sample_schedule(seed)
            self.assertEqual(len(sched), 5)
            t0, t1, t2, t3, t4 = [row[0] for row in sched]
            self.assertGreaterEqual(t0 - 0.0, margin - 1e-6, seed)
            self.assertGreaterEqual(ev["playerEnter1"] - t0, margin - 1e-6, seed)
            self.assertGreaterEqual(t1 - ev["playerOpen1"], margin - 1e-6, seed)
            self.assertGreaterEqual(ev["playerExit1"] - t1, margin - 1e-6, seed)
            self.assertGreaterEqual(t2 - ev["playerClose1"], margin - 1e-6, seed)
            self.assertGreaterEqual(ev["playerEnter2"] - t2, margin - 1e-6, seed)
            self.assertGreaterEqual(t3 - ev["playerOpen2"], margin - 1e-6, seed)
            self.assertGreaterEqual(ev["playerExit2"] - t3, margin - 1e-6, seed)
            self.assertGreaterEqual(t4 - ev["playerClose2"], margin - 1e-6, seed)
            self.assertGreaterEqual(ev["playerReenter"] - t4, margin - 1e-6, seed)
            for p_inst in (ev["playerEnter1"], ev["playerExit1"], ev["playerEnter2"], ev["playerExit2"],
                           ev["playerReenter"]):
                for t in (t0, t1, t2, t3, t4):
                    self.assertGreaterEqual(abs(t - p_inst), margin - 1e-6, (seed, t, p_inst))
        self.assertEqual(len(seen_specs), 12)
        in_csharp_check = any("csharp_check" in str(arg) for arg in sys.argv)
        seeds_to_run = sorted(seen_specs.values()) if in_csharp_check else range(4100, 4400)
        for seed in seeds_to_run:
            run = self.run_for(seed=seed)
            card = run.verify(run.solve(solutions.reference))
            self.assertTrue(card.strict, (seed, card.failed()))

# Producer:Betsy
