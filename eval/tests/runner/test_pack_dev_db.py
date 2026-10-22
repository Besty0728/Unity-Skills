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


class SlowEditorDB(FakeEditorDB):
    """Play Mode at 20 fps with RuntimeScenarioService's pacing, and Play starting 0.3 s before the first step."""
    step_cost = 1.0 / 20

    def _enter_play(self):
        FakeEditorDB._enter_play(self)
        self._tick(0.3)


class DB3Tests(DevTaskTests, unittest.TestCase):
    TASK_ID = "DB3"
    SEED = 7708
    FAILS_UNTOUCHED = {"D1", "D2", "D3", "D6", "D7"}

    def test_scenario_stamps_each_toggle(self):
        run = self.run_for()
        private = load_modules(self.TASK_ID)[1]
        steps = private.SCENARIO(run.build_ctx)["steps"]
        self.assertEqual([step["kind"] for step in steps],
                         ["read", "wait_frames", "read"] + ["wait_condition", "read"] * 3 + ["assert"])
        waits = [step for step in steps if step["kind"] == "wait_condition"]
        self.assertEqual([step["expected"] for step in waits], [False, True, False])
        self.assertEqual({(step["skill"], step["select"]) for step in waits}, {("component_list", private.LIGHT)})
        self.assertEqual({step["skill"] for step in steps if step["kind"] == "read"},
                         {"editor_get_state", "component_list"})

    def test_toggle_tolerances(self):
        private = load_modules(self.TASK_ID)[1]
        ok = lambda stamps: private.toggles_ok(stamps, 0.8)[0]    # noqa: E731
        self.assertTrue(ok([10.0, 10.8, 11.6, 12.4]))
        self.assertTrue(ok([10.0, 10.1, 10.9, 11.7]))              # Play started 0.7 s before the first stamp
        self.assertTrue(ok([10.0, 11.1, 11.8, 12.9]))              # 0.3 late, gaps 0.7 and 1.1
        self.assertFalse(ok([10.0, 10.05, 10.85, 11.65]))          # 0.75 s early: a toggle at Play start
        self.assertTrue(ok([10.0, 11.6, 12.4, 13.2]))              # first toggle 0.8 late: slow first Play frames
        self.assertFalse(ok([10.0, 11.7, 12.5, 13.3]))             # 0.9 late
        self.assertTrue(ok([10.0, 10.73, 11.84, 12.64]))           # a hitch lengthens the second gap by 0.31
        self.assertFalse(ok([10.0, 10.8, 12.15, 12.95]))           # a gap 0.55 long: beyond the hitch allowance
        self.assertFalse(ok([10.0, 10.4, 10.8, 11.2]))             # every half interval
        self.assertFalse(ok([10.0, 10.5, 11.0, 11.5]))             # the 0.5 s code default: gaps pass, the span not
        self.assertFalse(ok([10.0, 10.8, 11.6]))

    def test_verdicts_hold_at_20_fps_with_a_late_first_step(self):
        """Every case keeps its verdict when each step costs a 20 fps tick and Play starts 0.3 s early, for the
        default seed and the tightest spec (interval 0.8): the pacing that broke the fixed sampling schedule live."""
        private, solutions = load_modules(self.TASK_ID)[1:]
        seeds = [self.SEED] + [seed for seed in range(7001, 7400) if private.spec(seed)["interval"] == 0.8][:1]
        for seed in seeds:
            cases = [("reference", solutions.reference, set())] + \
                [(name, fn, set()) for name, fn in solutions.ALTERNATIVES] + \
                [(negative.name, negative.apply, negative.expect_failed) for negative in solutions.NEGATIVES]
            for name, fn, failed in cases:
                with self.subTest(seed=seed, case=name):
                    run = self.run_for(seed=seed)
                    run.editor.__class__ = SlowEditorDB
                    self.assertEqual(set(run.verify(run.solve(fn)).failed()), failed)


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
