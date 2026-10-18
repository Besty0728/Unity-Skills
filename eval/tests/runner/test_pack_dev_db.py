"""Offline checks of the dev/basic tasks DB1, DB3 and DB5 against fake_dev_db.FakeEditorDB.

The pack_offline checklist (prompts, handles, fingerprint, untouched fixture, solutions on URP and with Unity 6000.4
ids) plus the Built-in pipeline, the dev attributes, and the private SCENARIO run on the model followed by
verify_runtime, as the runner does at submit. Scripts "compile" here by parsing their public fields and the model
applies the behaviour the solutions' scripts describe; the live selftest on a real editor stays the gate.
"""
import json
import unittest

from .fake_dev_db import FakeEditorDB
from .pack_offline import Offline, OpsTaskTests, load_modules
from usk_eval import journal, pack, verifylib  # noqa: E402
from usk_eval.rest import RUNNER_AGENT  # noqa: E402
from usk_eval.task_api import VerifyContext  # noqa: E402

STEP_KINDS = ("invoke", "read", "assert", "wait_frames", "wait_seconds", "wait_condition")


class DevOffline(Offline):
    """pack_offline.Offline plus the SCENARIO on the model and verify_runtime (S9)."""

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


class DevTaskTests(OpsTaskTests):
    EDITOR = FakeEditorDB
    TIER = "basic"

    def run_for(self, **kwargs):
        kwargs.setdefault("tier", "basic")
        kwargs.setdefault("track", "dev")
        return DevOffline(self, self.TASK_ID, self.EDITOR, kwargs.pop("seed", self.SEED), **kwargs)

    def test_task_attributes(self):
        task = load_modules(self.TASK_ID)[0]
        self.assertEqual((task.id, task.track, task.tier, task.case_version), (self.TASK_ID, "dev", "basic", 1))
        requires = pack.normalize_requires(task.requires)
        self.assertEqual((requires["pipeline"], requires["mode"], requires["dedicated"]),
                         (["URP", "BuiltIn"], "bypass", False))

    def test_builtin_pipeline(self):
        self.check_solutions("BuiltIn", "legacy")

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
            if step["kind"] == "wait_seconds":
                self.assertTrue(0 <= step["seconds"] <= 600)
            if step["kind"] in ("assert", "wait_condition"):
                self.assertIn("expected", step)
            if "args" in step:
                self.assertIsInstance(step["args"], dict)


class DB1Tests(DevTaskTests, unittest.TestCase):
    TASK_ID = "DB1"
    SEED = 7607
    FAILS_UNTOUCHED = {"C1", "C2", "C3", "C6", "C7"}


class DB3Tests(DevTaskTests, unittest.TestCase):
    TASK_ID = "DB3"
    SEED = 7708
    FAILS_UNTOUCHED = {"D1", "D2", "D3", "D6", "D7"}


class DB5Tests(DevTaskTests, unittest.TestCase):
    TASK_ID = "DB5"
    SEED = 7809
    FAILS_UNTOUCHED = {"E1", "E2", "E3", "E4", "E6", "E7"}

    def test_label_is_a_ugui_text_showing_0(self):
        """ui_create_text gives a TextMeshProUGUI wherever TextMeshPro is present (the model too), which a Text field
        cannot point at, so the fixture adds the UnityEngine.UI.Text itself; the task needs uGUI installed."""
        for ids in ("legacy", "6000.4"):
            with self.subTest(ids=ids):
                run = self.run_for(entity_mode=ids)
                label = run.editor.find(verifylib.address(run.handles["label"]))
                self.assertEqual([kind for kind, _enabled in label.components],
                                 ["RectTransform", "CanvasRenderer", "Text"])
                self.assertEqual(run.editor.font_sizes.get(label.eid), 32)
                self.assertEqual(run.snap["data"]["labelText"], "0")
        requires = pack.normalize_requires(load_modules(self.TASK_ID)[0].requires)
        self.assertEqual(requires["packages"], ["com.unity.ugui"])

# Producer:Betsy
