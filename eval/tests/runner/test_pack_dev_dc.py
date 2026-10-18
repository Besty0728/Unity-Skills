"""Offline checks of the dev/challenge tasks DC1, DC3, DC5 and DC7 against fake_dev_dc: the pack_offline checklist
(prompts, handles, fingerprint, untouched fixture, solutions with legacy and Unity 6000.4 ids), the dev attributes,
the private SCENARIO played on the model followed by verify_runtime as the runner does at submit, and structure that
follows the seed. The live selftest on a URP editor remains the gate: the model only knows the behaviour the tasks
ask for and the wrong variants the negatives mark."""
import json
import unittest

from .fake_dev_dc import FakeEditorDC5
from .pack_offline import Offline, OpsTaskTests, load_modules
from usk_eval import journal, pack, verifylib  # noqa: E402
from usk_eval.rest import RUNNER_AGENT  # noqa: E402
from usk_eval.task_api import VerifyContext  # noqa: E402

STEP_KINDS = ("invoke", "read", "assert", "wait_frames", "wait_seconds", "wait_condition")


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
        self.spec = definition(ctx) if callable(definition) else definition
        self.scenario = self.editor.run_scenario(self.spec)
        ctx.scenario = self.scenario
        self.private.verify_runtime(ctx, card)
        return card.finish()


class DevChallengeTests(OpsTaskTests):
    TIER = "challenge"
    MINUTES = None
    PACKAGES = []
    OTHER_SEEDS = (11, 12, 13, 14)

    def run_for(self, **kwargs):
        kwargs.setdefault("tier", self.TIER)
        kwargs.setdefault("track", "dev")
        return DevOffline(self, self.TASK_ID, self.EDITOR, kwargs.pop("seed", self.SEED), **kwargs)

    def test_task_attributes(self):
        task = load_modules(self.TASK_ID)[0]
        self.assertEqual((task.id, task.track, task.tier, task.case_version, task.minutes, task.needs_focus),
                         (self.TASK_ID, "dev", "challenge", 1, self.MINUTES, True))
        requires = pack.normalize_requires(task.requires)
        self.assertEqual((requires["pipeline"], requires["mode"], requires["dedicated"], requires["packages"]),
                         ("URP", "bypass", True, self.PACKAGES))

    def test_scenario_shape(self):
        run = self.run_for()
        run.verify(run.solve(load_modules(self.TASK_ID)[2].reference))
        spec, result = run.spec, run.scenario
        self.assertTrue(result["passed"], result)
        self.assertEqual(spec["executionMode"], "play")
        self.assertTrue(1 <= spec["timeoutSeconds"] <= 600)
        self.assertTrue(1 <= len(spec["steps"]) <= 100)
        kinds = [step["kind"] for step in spec["steps"]]
        self.assertTrue({"assert", "wait_condition"} & set(kinds))
        for step in spec["steps"]:
            self.assertIn(step["kind"], STEP_KINDS)
            if step["kind"] == "invoke":
                self.assertEqual(step["skill"], "component_invoke_method")
            if step["kind"] in ("assert", "wait_condition"):
                self.assertIn("expected", step)
            if "args" in step:
                self.assertIsInstance(step["args"], dict)
                json.dumps(step["args"])

    def test_other_seeds_build_and_solve(self):
        """Reserved-seed stand-ins: other seeds rearrange the fixture and the reference still passes strictly."""
        prints = set()
        for seed in self.OTHER_SEEDS:
            with self.subTest(task=self.TASK_ID, seed=seed):
                run = self.run_for(seed=seed)
                prints.add(run.fingerprint)
                card = run.verify(run.solve(load_modules(self.TASK_ID)[2].reference))
                self.assertTrue(card.strict, card.as_dict())
        self.assertGreater(len(prints), 1)


class DC5Tests(DevChallengeTests, unittest.TestCase):
    TASK_ID = "DC5"
    EDITOR = FakeEditorDC5
    SEED = 7001
    MINUTES = 35
    PACKAGES = ["com.unity.ugui"]
    FAILS_UNTOUCHED = {"P1", "P2", "P3", "P4", "P5", "P9", "P10"}

    def test_paused_negatives_end_running(self):
        """The scenario ends by setting Time.timeScale back to 1, also after a negative that leaves the game paused."""
        run = self.run_for()
        run.verify(run.solve(load_modules("DC5")[2].timescale_only))
        last = [step for step in run.spec["steps"] if step["kind"] == "invoke"][-1]
        self.assertEqual(last["args"]["methodName"], "set_timeScale")

# Producer:Betsy
