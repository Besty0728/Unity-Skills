"""Shared harness of the offline tests of the standard-tier ops tasks (T3 T4 T5 T7 T10).

pack_fake_editor.FakeEditor models what the basic tasks touch. Each standard task adds a subclass of OpsFakeEditor with
the few skills and state it needs (extra_reads lists its additional read-only skills); Offline runs one attempt the way
the runner does (run folder, scene and root, build, C0, solver, submit-time refresh, private verify) and OpsTaskTests is
the common checklist every task test class inherits. The live selftest against a real editor remains the gate: this
only catches mistakes in the task code before it.
"""
import json
import os
import re
import tempfile

from .pack_fake_editor import REPO, READ_SKILLS, FakeClient, FakeEditor, remove_tree
from usk_eval import attempt, environment, fixture, journal, pack, prompts, verifylib  # noqa: E402
from usk_eval.rest import RUNNER_AGENT, SOLVER_AGENT, RestError  # noqa: E402
from usk_eval.task_api import BuildContext, SolverContext, VerifyContext  # noqa: E402

PACK_ROOT = os.path.join(REPO, "SkillsForUnity", "Evaluation~", "pack")
PRIVATE_ROOT = os.path.join(REPO, "eval", "pack", "private")
SOLUTIONS_ROOT = os.path.join(REPO, "eval", "pack", "solutions")
ROLE_RE = attempt.HANDLE_KEY_RE
MODULES = {}


class OpsFakeClient(FakeClient):
    """FakeClient whose read-only set is READ_SKILLS plus the editor's extra_reads."""

    def _reads(self):
        return READ_SKILLS | self.editor.extra_reads

    def is_read_only(self, skill):
        return skill in self._reads()

    def schema(self, names):
        reads = self._reads()
        return {name: {"name": name, "flags": ["readOnly"] if name in reads else ["mutatesScene"]} for name in names}

    def read(self, skill, **args):
        if skill not in self._reads():
            raise RestError("refused non-read-only skill %s" % skill)
        return self.editor.call(skill, args, self.agent)


class OpsFakeEditor(FakeEditor):
    extra_reads = frozenset()

    def client(self, agent):
        return OpsFakeClient(self, agent)


def load_modules(task_id):
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


def detail(card):
    return json.dumps([row for row in card.as_dict()["assertions"] if not row["pass"]], default=str)[:3000]


class Offline(object):
    """One attempt the way the runner runs it: collision snapshot (N4), run folder, scene and root, build (N5), C0 (N6),
    then a solver, the submit-time refresh (S3) and the private verify on the flattened journal (S8)."""

    def __init__(self, test, task_id, editor_class, seed, pipeline="URP", entity_mode="legacy", lang="en",
                 track="ops", tier="standard"):
        self.tmp = tempfile.mkdtemp(prefix="usk-pack-test-")
        test.addCleanup(remove_tree, self.tmp)
        self.project = os.path.join(self.tmp, "Proj")
        self.editor = editor_class(self.project, pipeline, entity_mode)
        self.task, self.private, self.solutions = load_modules(task_id)
        self.seed, self.lang, self.track, self.tier = seed, lang, track, tier
        self.scope = fixture.Scope(fixture.new_run_id())
        client = self.editor.client(RUNNER_AGENT)
        self.collision = fixture.collision_snapshot(client, self.task.layer_pairs)
        fixture.enter(client, self.scope, lambda: None)
        self.env = environment.snapshot(client, 4242)
        ctx = BuildContext(client, self.scope, seed, self.project, self.env, lang, track, tier, self.task)
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
                            c0=self.snap.get("data"), c0_digests=self.snap.get("digests"),
                            answer=json.loads(json.dumps(answer)) if answer is not None else None,
                            records=journal.flatten(entries, window), journal_complete=True,
                            journal={"cursor0": self.cursor0, "cursorEnd": self.editor.seq, "refreshWindow": window,
                                     "forcedPlayModeExit": False},
                            allowed_change=getattr(self.private, "allowed_change", None))
        card = verifylib.Scorecard(self.private.ASSERTIONS)
        self.private.verify(ctx, card)
        return card.finish()

    def failed(self, fn):
        return set(self.verify(self.solve(fn)).failed())

    def teardown(self):
        """The runner's teardown for this attempt: the task hook, then the layer collision restore (S11)."""
        client = self.editor.client(RUNNER_AGENT)
        ctx = BuildContext(client, self.scope, self.seed, self.project, self.env, self.lang, self.track, self.tier,
                           self.task, handles=self.handles)
        out = self.task.teardown(ctx)
        fixture.restore_collision(client, self.collision)
        return out


class OpsTaskTests(object):
    """Mixin for a unittest.TestCase per task. Set TASK_ID, EDITOR (an OpsFakeEditor subclass), SEED and FAILS_UNTOUCHED
    (the assertions an untouched fixture fails); READ_ONLY marks digest-only C0."""

    TASK_ID = None
    EDITOR = OpsFakeEditor
    SEED = 7001
    FAILS_UNTOUCHED = set()
    READ_ONLY = False
    TIER = "standard"

    def run_for(self, **kwargs):
        kwargs.setdefault("tier", self.TIER)
        return Offline(self, self.TASK_ID, self.EDITOR, kwargs.pop("seed", self.SEED), **kwargs)

    def test_prompts_and_schema_render_in_both_languages(self):
        run = self.run_for()
        builtins = {"session": "s", "k": 1, "n": 5, "track": "ops", "tier": self.TIER, "project": run.project,
                    "unity": "2022.3.62f2", "pipeline": "URP", "port": 8091, "instance": "Fake_0000",
                    "R": run.scope.object_root, "A": run.scope.asset_root, "scene": run.scope.scene_path,
                    "minutes": run.task.minutes}
        values = run.task.prompt_vars(run.build_ctx)
        variables = prompts.merge_vars(builtins, values)
        for lang in prompts.LANGS:
            with self.subTest(task=self.TASK_ID, lang=lang):
                with open(os.path.join(PACK_ROOT, "tasks", self.TASK_ID, "prompt.%s.md" % lang), "rb") as handle:
                    raw = handle.read()
                self.assertFalse(raw.startswith(b"\xef\xbb\xbf"))
                self.assertNotIn(b"\r", raw)
                text = prompts.render_requirements(prompts.parse_prompt(raw.decode("utf-8")), variables)
                for value in values.values():
                    self.assertIn(str(value), text)
                schema = prompts.render_schema(prompts.with_declared(run.task.answer_schema()), lang)
                self.assertIn('"declaredComplete": <boolean>', schema)
                self.assertNotIn("<value", schema)

    def test_handles_and_c0_carry_no_role_labels(self):
        run = self.run_for()
        bad = [key for key in keys(run.handles) + keys(run.snap) if isinstance(key, str) and ROLE_RE.search(key)]
        self.assertEqual(bad, [])
        if self.READ_ONLY:
            self.assertEqual(run.snap["data"], {})
            self.assertTrue(run.snap["digests"])
            for digest in run.snap["digests"].values():
                self.assertRegex(digest, r"^[0-9a-f]{64}$")

    def test_one_seed_one_fingerprint(self):
        first, second = self.run_for(), self.run_for()
        self.assertNotEqual(first.scope.run_id, second.scope.run_id)
        self.assertEqual(first.fingerprint, second.fingerprint)

    def test_task_attributes(self):
        task = load_modules(self.TASK_ID)[0]
        self.assertEqual((task.id, task.track, task.tier, task.case_version),
                         (self.TASK_ID, "ops", self.TIER, 2))
        requires = pack.normalize_requires(task.requires)
        self.assertEqual((requires["pipeline"], requires["mode"], requires["dedicated"]), ("URP", "bypass", True))

    def test_no_prompt_forbids_saving_the_run_scene(self):
        for lang in prompts.LANGS:
            with open(os.path.join(PACK_ROOT, "tasks", self.TASK_ID, "prompt.%s.md" % lang), encoding="utf-8") as h:
                text = h.read()
            with self.subTest(task=self.TASK_ID, lang=lang):
                self.assertIsNone(re.search(r"(?i)scene save|save the scene|保存场景|另存", text))

    def test_untouched_fixture_fails_only_the_work_assertions(self):
        card = self.run_for().verify(None)
        self.assertEqual(set(card.failed()), set(self.FAILS_UNTOUCHED), detail(card))

    def test_teardown_can_run_twice(self):
        run = self.run_for()
        run.teardown()
        run.teardown()

    def check_solutions(self, pipeline, entity_mode):
        solutions = load_modules(self.TASK_ID)[2]
        for repeat in (1, 2):
            run = self.run_for(pipeline=pipeline, entity_mode=entity_mode)
            card = run.verify(run.solve(solutions.reference))
            self.assertTrue(card.strict, "%s reference #%d on %s/%s: %s" % (self.TASK_ID, repeat, pipeline,
                                                                           entity_mode, detail(card)))
        for negative in solutions.NEGATIVES:
            with self.subTest(task=self.TASK_ID, negative=negative.name, pipeline=pipeline, ids=entity_mode):
                run = self.run_for(pipeline=pipeline, entity_mode=entity_mode)
                card = run.verify(run.solve(negative.apply))
                self.assertEqual(set(card.failed()), negative.expect_failed, detail(card))
        for name, fn in solutions.ALTERNATIVES:
            with self.subTest(task=self.TASK_ID, alternative=name, pipeline=pipeline, ids=entity_mode):
                run = self.run_for(pipeline=pipeline, entity_mode=entity_mode)
                card = run.verify(run.solve(fn))
                self.assertTrue(card.strict, detail(card))

    def test_urp_pipeline(self):
        self.check_solutions("URP", "legacy")

    def test_unity_6000_4_ids(self):
        self.check_solutions("URP", "6000.4")

# Producer:Betsy
