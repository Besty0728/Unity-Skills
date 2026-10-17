"""Offline checks of the six basic tasks (S01 S03 S04 S06 S08 D01) against the in-memory editor of pack_fake_editor.

Builds, prompts and answer schemas render in both languages; one seed gives one fingerprint; handles and C0 stay free
of role labels and read-only tasks keep C0 to digests; the reference and every alternative strict-pass and every
negative fails exactly its declared assertions, on both render pipelines and with Unity 6000.4 ids. The live selftest
against a real editor remains the gate: this only catches mistakes in the task code before it.
"""
import json
import os
import re
import tempfile
import unittest

from .pack_fake_editor import REPO, FakeEditor, remove_tree
from usk_eval import attempt, environment, fixture, journal, pack, prompts, verifylib  # noqa: E402
from usk_eval.rest import RUNNER_AGENT, SOLVER_AGENT  # noqa: E402
from usk_eval.task_api import BuildContext, SolverContext, VerifyContext  # noqa: E402

TASKS = ("S01", "S03", "S04", "S06", "S08", "D01")
READ_ONLY = ("S08", "D01")
SEEDS = {"S01": 7001, "S03": 7102, "S04": 7203, "S06": 7304, "S08": 7405, "D01": 7506, "T1": 7607, "T2": 7708,
         "T9": 8213}
PACK_ROOT = os.path.join(REPO, "SkillsForUnity", "Evaluation~", "pack")
PRIVATE_ROOT = os.path.join(REPO, "eval", "pack", "private")
SOLUTIONS_ROOT = os.path.join(REPO, "eval", "pack", "solutions")
ROLE_RE = attempt.HANDLE_KEY_RE
UNTOUCHED = {"S01": {"A1", "A2", "A3", "A4", "A5", "A6", "A9"}, "S03": {"B1", "B2", "B3", "B4", "B7", "B10"},
             "S04": {"C1", "C5"}, "S06": {"D1", "D2", "D3", "D5"}, "S08": {"E1", "E3", "E4"},
             "D01": {"F1", "F3", "F4"}}
MODULES = {}


def modules(task_id):
    """(TASK, private module, solutions module) of a task, loaded once."""
    if task_id not in MODULES:
        task = pack.load_task_file(task_id, os.path.join(PACK_ROOT, "tasks", task_id, "task.py"))
        private = pack.load_source_module("usk_test_private_" + task_id, os.path.join(PRIVATE_ROOT, task_id + ".py"))
        solutions = pack.load_source_module("usk_test_solutions_" + task_id,
                                            os.path.join(SOLUTIONS_ROOT, task_id + ".py"))
        MODULES[task_id] = (task, private, solutions)
    return MODULES[task_id]


def keys(value, out=None):
    out = [] if out is None else out
    if isinstance(value, dict):
        for key, item in value.items():
            out.append(key)
            keys(item, out)
    elif isinstance(value, (list, tuple)):
        for item in value:
            keys(item, out)
    return out


class Offline(object):
    """One attempt the way the runner runs it: run folder, scene and root (N4), build (N5), C0 (N6), then a solver,
    the submit-time refresh (S3), the private verify on the flattened journal (S8) and, for dev tasks, the SCENARIO
    on the model followed by verify_runtime (S9)."""

    def __init__(self, test, task_id, seed=None, pipeline="BuiltIn", entity_mode="legacy", lang="en"):
        self.tmp = tempfile.mkdtemp(prefix="usk-pack-test-")
        test.addCleanup(remove_tree, self.tmp)
        self.project = os.path.join(self.tmp, "Proj")
        self.editor = FakeEditor(self.project, pipeline, entity_mode)
        self.task, self.private, self.solutions = modules(task_id)
        self.seed = SEEDS[task_id] if seed is None else seed
        self.lang = lang
        self.scope = fixture.Scope(fixture.new_run_id())
        client = self.editor.client(RUNNER_AGENT)
        fixture.enter(client, self.scope, lambda: None)
        self.env = environment.snapshot(client, 4242)
        self.track, self.tier = self.task.track, self.task.tier
        ctx = BuildContext(client, self.scope, self.seed, self.project, self.env, lang, self.track, self.tier,
                           self.task)
        self.handles = self.task.build(ctx)
        attempt._check_handles(self.handles)
        json.dumps(self.handles, allow_nan=False)
        ctx.handles = self.handles
        fixture.save_run_scene(client, self.scope)
        self.snap = self.task.snapshot(ctx)
        json.dumps(self.snap, allow_nan=False)
        self.fingerprint = verifylib.fingerprint(ctx)
        self.build_ctx = ctx
        self.cursor0 = self.editor.seq

    def solve(self, fn):
        ctx = SolverContext(self.editor.client(SOLVER_AGENT), self.scope, self.seed, self.project, self.env,
                            self.lang, self.track, self.tier, self.task, handles=self.handles, private=self.private)
        return fn(ctx)

    def verify(self, answer):
        start = self.editor.seq
        self.editor.refresh()
        window = {"from": start, "to": self.editor.seq}
        entries = [entry for entry in self.editor.entries if entry["seq"] > self.cursor0]
        ctx = VerifyContext(self.editor.client(RUNNER_AGENT), self.scope, self.seed, self.project, self.env,
                            self.lang, self.track, self.tier, self.task, handles=self.handles,
                            c0=self.snap.get("data"),
                            c0_digests=self.snap.get("digests"),
                            answer=json.loads(json.dumps(answer)) if answer is not None else None,
                            records=journal.flatten(entries, window), journal_complete=True,
                            journal={"cursor0": self.cursor0, "cursorEnd": self.editor.seq, "refreshWindow": window,
                                     "forcedPlayModeExit": False},
                            allowed_change=getattr(self.private, "allowed_change", None))
        card = verifylib.Scorecard(self.private.ASSERTIONS)
        self.private.verify(ctx, card)
        definition = getattr(self.private, "SCENARIO", None)
        if self.track == "dev" and definition is not None:
            self.scenario = self.editor.run_scenario(definition(ctx) if callable(definition) else definition)
            ctx.scenario = self.scenario
            self.private.verify_runtime(ctx, card)
        return card.finish()

    def failed(self, fn):
        return set(self.verify(self.solve(fn)).failed())


def _detail(card):
    return json.dumps([row for row in card.as_dict()["assertions"] if not row["pass"]], default=str)[:3000]


class TaskShapeTests(unittest.TestCase):
    def test_prompts_and_schema_render_in_both_languages(self):
        for task_id in TASKS:
            run = Offline(self, task_id)
            builtins = {"session": "s", "k": 1, "n": 6, "track": "ops", "tier": "basic", "project": run.project,
                        "unity": "2022.3.62f2", "pipeline": "BuiltIn", "port": 8091, "instance": "Fake_0000",
                        "R": run.scope.object_root, "A": run.scope.asset_root, "scene": run.scope.scene_path,
                        "minutes": run.task.minutes}
            variables = prompts.merge_vars(builtins, run.task.prompt_vars(run.build_ctx))
            for lang in prompts.LANGS:
                with self.subTest(task=task_id, lang=lang):
                    with open(os.path.join(PACK_ROOT, "tasks", task_id, "prompt.%s.md" % lang), "rb") as handle:
                        raw = handle.read()
                    self.assertFalse(raw.startswith(b"\xef\xbb\xbf"))
                    self.assertNotIn(b"\r", raw)
                    text = prompts.render_requirements(prompts.parse_prompt(raw.decode("utf-8")), variables)
                    for value in run.task.prompt_vars(run.build_ctx).values():
                        self.assertIn(str(value), text)
                    schema = prompts.render_schema(prompts.with_declared(run.task.answer_schema()), lang)
                    self.assertIn('"declaredComplete": <boolean>', schema)
                    self.assertNotIn("<value", schema)

    def test_handles_and_c0_carry_no_role_labels(self):
        for task_id in TASKS:
            with self.subTest(task=task_id):
                run = Offline(self, task_id)
                bad = [key for key in keys(run.handles) + keys(run.snap)
                       if isinstance(key, str) and ROLE_RE.search(key)]
                self.assertEqual(bad, [])
                if task_id in READ_ONLY:
                    self.assertEqual(run.snap["data"], {})
                    self.assertTrue(run.snap["digests"])
                    for digest in run.snap["digests"].values():
                        self.assertRegex(digest, r"^[0-9a-f]{64}$")

    def test_one_seed_one_fingerprint(self):
        for task_id in TASKS:
            with self.subTest(task=task_id):
                first, second = Offline(self, task_id), Offline(self, task_id)
                self.assertNotEqual(first.scope.run_id, second.scope.run_id)
                self.assertEqual(first.fingerprint, second.fingerprint)

    def test_task_attributes(self):
        for task_id in TASKS:
            with self.subTest(task=task_id):
                task = modules(task_id)[0]
                self.assertEqual((task.id, task.track, task.tier, task.case_version), (task_id, "ops", "basic", 2))
                self.assertEqual(pack.normalize_requires(task.requires)["pipeline"], ["URP", "BuiltIn"])
                self.assertFalse(task.requires["dedicated"])


class SolutionTests(unittest.TestCase):
    def check_task(self, task_id, pipeline, entity_mode):
        _task, _private, solutions = modules(task_id)
        for repeat in (1, 2):
            run = Offline(self, task_id, pipeline=pipeline, entity_mode=entity_mode)
            card = run.verify(run.solve(solutions.reference))
            self.assertTrue(card.strict, "%s reference #%d on %s/%s: %s" % (task_id, repeat, pipeline, entity_mode,
                                                                           _detail(card)))
        for negative in solutions.NEGATIVES:
            with self.subTest(task=task_id, negative=negative.name, pipeline=pipeline, ids=entity_mode):
                run = Offline(self, task_id, pipeline=pipeline, entity_mode=entity_mode)
                card = run.verify(run.solve(negative.apply))
                self.assertEqual(set(card.failed()), negative.expect_failed, _detail(card))
        for name, fn in solutions.ALTERNATIVES:
            with self.subTest(task=task_id, alternative=name, pipeline=pipeline, ids=entity_mode):
                run = Offline(self, task_id, pipeline=pipeline, entity_mode=entity_mode)
                card = run.verify(run.solve(fn))
                self.assertTrue(card.strict, _detail(card))

    def test_builtin_pipeline(self):
        for task_id in TASKS:
            self.check_task(task_id, "BuiltIn", "legacy")

    def test_urp_pipeline(self):
        for task_id in TASKS:
            self.check_task(task_id, "URP", "legacy")

    def test_unity_6000_4_ids(self):
        for task_id in TASKS:
            self.check_task(task_id, "URP", "6000.4")

    def test_untouched_fixture_fails_only_the_work_assertions(self):
        for task_id in TASKS:
            with self.subTest(task=task_id):
                run = Offline(self, task_id)
                card = run.verify(None)
                self.assertEqual(set(card.failed()), UNTOUCHED[task_id], _detail(card))


class PromptTextTests(unittest.TestCase):
    def test_s08_states_case_sensitivity_in_both_languages(self):
        for lang, word in (("zh-CN", "区分大小写"), ("en", "case-sensitive")):
            with open(os.path.join(PACK_ROOT, "tasks", "S08", "prompt.%s.md" % lang), encoding="utf-8") as handle:
                self.assertIn(word, handle.read())

    def test_no_prompt_forbids_saving_the_run_scene(self):
        for task_id in TASKS:
            for lang in prompts.LANGS:
                with open(os.path.join(PACK_ROOT, "tasks", task_id, "prompt.%s.md" % lang), encoding="utf-8") as h:
                    text = h.read()
                with self.subTest(task=task_id, lang=lang):
                    self.assertIsNone(re.search(r"(?i)scene save|save the scene|保存场景|另存", text))

# Producer:Betsy
