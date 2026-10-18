"""Offline checks of the dev/challenge tasks DC1, DC3, DC5 and DC7 against fake_dev_dc: the pack_offline checklist
(prompts, handles, fingerprint, untouched fixture, solutions with legacy and Unity 6000.4 ids), the dev attributes,
the private SCENARIO played on the model followed by verify_runtime as the runner does at submit, and structure that
follows the seed. The live selftest on a URP editor remains the gate: the model only knows the behaviour the tasks
ask for and the wrong variants the negatives mark."""
import json
import os
import re
import unittest

from .fake_dev_dc import FakeEditorDC1, FakeEditorDC3, FakeEditorDC5, FakeEditorDC7
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


class DC1Tests(DevChallengeTests, unittest.TestCase):
    TASK_ID = "DC1"
    EDITOR = FakeEditorDC1
    SEED = 7001
    MINUTES = 30
    FAILS_UNTOUCHED = {"K6", "K7", "K8"}

    def test_a_script_created_again_at_its_path_keeps_its_guid(self):
        """The live gate on 6000.3: Unity hands the old GUID back to a script created again at the path of the one just
        deleted, so the scene component stays attached and nothing breaks. DC1 therefore has no delete-and-create
        negative, and the model must not hand out a fresh GUID there; a script created at another path is new."""
        solutions = load_modules("DC1")[2]
        run = self.run_for()
        script = run.handles["script"]

        def guid(path):
            with open(run.editor.disk(path) + ".meta", "r", encoding="utf-8") as handle:
                return re.search(r"guid:\s*(\w+)", handle.read()).group(1)

        before = guid(script)

        def recreate(ctx):
            text = solutions._current(ctx)
            start, end = text.index(solutions.BLOCK_START), text.index(solutions.BLOCK_END)
            solutions._wait(ctx, ctx.c.write("script_delete", scriptPath=script))
            solutions._wait(ctx, ctx.c.write("script_create", scriptName=solutions._cls(ctx),
                                             folder=os.path.dirname(script),
                                             content=text[:start] + solutions.methods() + text[end:]))
            return solutions._answer(ctx)

        card = run.verify(run.solve(recreate))
        self.assertEqual(guid(script), before)
        self.assertTrue(card.strict, card.as_dict())
        other = os.path.dirname(script) + "/Other.cs"
        run.solve(lambda ctx: solutions._wait(ctx, ctx.c.write("script_create", scriptName="Other",
                                                               folder=os.path.dirname(script),
                                                               content="public class Other {}\n")))
        self.assertNotEqual(guid(other), before)

    def test_there_is_no_delete_and_create_negative(self):
        self.assertNotIn("recreated", [negative.name for negative in load_modules("DC1")[2].NEGATIVES])

    def test_private_spec_matches_public(self):
        public = __import__(load_modules("DC1")[0].__class__.__module__, fromlist=["spec"])
        private = load_modules("DC1")[1]
        for seed in range(300):
            self.assertEqual(public.spec(seed), private.spec(seed))

    def test_plan_exercises_every_rule(self):
        """On the reference trajectory the planned calls hit each rule they are meant to: top-up with a new stack,
        refused 0 and negative counts, a full inventory with leftover, a call that stores nothing, removals from the
        back that span two stacks, removals of nothing, and new stacks after removals."""
        private = load_modules("DC1")[1]
        for seed in range(200):
            ctx = type("Ctx", (), {"seed": seed})()
            values = private.spec(seed)
            size, slots = values["maxStack"], values["slotCount"]
            stacks, results = [list(row) for row in values["initial"]], []
            for op, item, count in private.plan(ctx):
                value, after, raised = private.apply_call(stacks, size, slots, op, item, count)
                results.append((op, value, len(after), raised, after))
                stacks = after
            with self.subTest(seed=seed):
                self.assertEqual([r[3] for r in results[:13]], [1, 0, 0, 1, 1, 0, 1, 1, 1, 0, 0, 1, 1])
                self.assertEqual(results[4][2], slots)
                self.assertGreater(results[4][1], 0)
                self.assertEqual(results[5][1], 6)
                self.assertGreater(results[6][1], 0)
                mains = [r for r in results[7][4] if r[0] == values["main"]]
                self.assertEqual([r[1] for r in mains], [size, size - 3])
                self.assertEqual(results[8][1], size)
                self.assertEqual(results[9][1], 0)
                self.assertTrue(all(1 <= count <= size for _item, count in stacks))


class DC7Tests(DevChallengeTests, unittest.TestCase):
    TASK_ID = "DC7"
    EDITOR = FakeEditorDC7
    SEED = 7001
    MINUTES = 40
    FAILS_UNTOUCHED = {"A1", "A3", "A5", "A9", "A10"}

    def test_private_spec_matches_public(self):
        public = __import__(load_modules("DC7")[0].__class__.__module__, fromlist=["spec"])
        private = load_modules("DC7")[1]
        for seed in range(300):
            self.assertEqual(public.spec(seed), private.spec(seed))

    def test_fixture_does_not_compile(self):
        """After the build the gameplay scripts do not compile, and only they fail."""
        run = self.run_for()
        self.assertEqual(sorted(run.editor.compile_errors),
                         sorted([run.handles["combatScript"], run.handles["reportScript"]]))


class DC3Tests(DevChallengeTests, unittest.TestCase):
    TASK_ID = "DC3"
    EDITOR = FakeEditorDC3
    SEED = 12354                 # the proposed public seed: Game.AI.Patroller
    MINUTES = 45
    FAILS_UNTOUCHED = {"Q1", "Q2", "Q3", "Q4", "Q5", "Q6", "Q10", "Q11"}

    def test_private_names_match_public(self):
        public = __import__(load_modules("DC3")[0].__class__.__module__, fromlist=["names"])
        private = load_modules("DC3")[1]
        for seed in range(300):
            self.assertEqual(public.names(seed), private.names(seed))
        self.assertEqual(public.names(self.SEED), {"namespace": "Game.AI", "class": "Patroller"})

    def test_fixture_holds_six_instances(self):
        """Four scene holders (two plain objects, an overridden prefab instance, a variant instance) and two prefab
        assets, all with values that differ from the field defaults and from each other's overrides."""
        run = self.run_for()
        holders, assets = run.snap["data"]["holders"], run.snap["data"]["assets"]
        self.assertEqual(len(holders), 4)
        speeds = [float(entry["speed"]) for entry in holders] + [float(assets[k]["speed"]) for k in sorted(assets)]
        self.assertNotIn(1.5, speeds)
        self.assertEqual(len(set(speeds[:3] + [speeds[5]])), 4)
        self.assertEqual(assets["basePrefab"]["links"], ["P0", "P1"])
        self.assertEqual([len(entry["links"]) for entry in holders], [3, 2, 2, 2])

# Producer:Betsy
