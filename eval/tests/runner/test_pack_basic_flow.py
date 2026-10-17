"""The six basic tasks through the real runner CLI (start, next, submit) against runner_support's fake REST server.

The HTTP side (/health, /jobs, telemetry, instance identity) is runner_support.FakeUnity; every skill runs on the
editor model of pack_fake_editor, whose journal entries go to the file the runner reads back. This covers what the
direct tests do not: the shipped manifest and prompt files, the private verifier fetched from eval/pack/private, the
answer copied by submit, result.json and the teardown back to the resting scene.
"""
import unittest

from . import runner_support as RS
from .pack_fake_editor import READ_SKILLS, FakeEditor
from .test_pack_basic_tasks import PRIVATE_ROOT, TASKS, modules
from usk_eval import task_api  # noqa: E402
from usk_eval.rest import SOLVER_AGENT, SkillError  # noqa: E402


class PackUnity(RS.FakeUnity):
    """runner_support.FakeUnity whose skills run on the pack_fake_editor model."""

    def __init__(self, project, pipeline="BuiltIn"):
        RS.FakeUnity.__init__(self, project)
        self.model = FakeEditor(project, pipeline, journal_path=self.journal_path)

    def call(self, name, args, agent):
        with self.lock:
            try:
                return self.model.call(name, args, agent)
            except SkillError as exc:
                payload = exc.payload or {}
                raise RS.SkillFailure(payload.get("errorCode") or "ERROR", payload.get("error") or str(exc))


class RunnerFlowTests(unittest.TestCase):
    def bench(self, pipeline="BuiltIn"):
        bench = RS.Bench(self, stand_ins=False)
        bench.unity.stop()
        bench.unity = PackUnity(bench.project, pipeline).start()
        self.addCleanup(bench.unity.stop)
        bench.write_registry()
        self.addCleanup(setattr, RS, "READ_ONLY", RS.READ_ONLY)
        RS.READ_ONLY = set(READ_SKILLS)
        return bench

    def attempt(self, bench, task_id, solver, lang="zh-CN"):
        code, env = bench.run("start", "--lang", lang, "--tasks", task_id, "--private-dir", PRIVATE_ROOT)
        self.assertEqual(code, 0, env)
        code, env = bench.run("next")
        self.assertEqual(code, 0, env)
        data = env["data"]
        sid, aid = data["sessionId"], data["attemptId"]
        task, private, _solutions = modules(task_id)
        ctx = task_api.solver_context(bench.unity.model.client(SOLVER_AGENT), bench.session(sid),
                                      bench.state(sid, aid), task, bench.project, private=private)
        path = bench.answer(data, solver(ctx))
        code, env = bench.run("submit", "--attempt", aid, "--answer", path)
        self.assertEqual(code, 0, env)
        return bench.result(sid, aid), bench.state(sid, aid)

    @staticmethod
    def failed(result):
        return {row["id"] for row in (result.get("score") or {}).get("assertions") or [] if not row["pass"]}

    def test_reference_and_first_negative_of_every_task(self):
        for task_id in TASKS:
            _task, _private, solutions = modules(task_id)
            with self.subTest(task=task_id, case="reference"):
                bench = self.bench()
                result, state = self.attempt(bench, task_id, solutions.reference)
                self.assertEqual(result["status"], "pass", result)
                self.assertEqual(result["caseVersion"], 2)
                self.assertEqual(state["phase"], "torn_down")
                self.assertTrue(result["teardown"]["ok"], result["teardown"])
                self.assertTrue(result["evidence"]["journal"]["complete"])
            negative = solutions.NEGATIVES[0]
            with self.subTest(task=task_id, case=negative.name):
                bench = self.bench()
                result, _state = self.attempt(bench, task_id, negative.apply, lang="en")
                self.assertIn(result["status"], ("partial", "fail"), result)
                self.assertEqual(self.failed(result), negative.expect_failed, result.get("score"))
                self.assertTrue(result["requirementsFailed"])
                failed_rows = [r for r in result["score"]["assertions"] if not r["pass"]]
                self.assertTrue(all(r["hint"] for r in failed_rows), failed_rows)

    def test_urp_reference(self):
        for task_id in ("S01", "S06"):
            with self.subTest(task=task_id):
                bench = self.bench("URP")
                result, _state = self.attempt(bench, task_id, modules(task_id)[2].reference)
                self.assertEqual(result["status"], "pass", result)

# Producer:Betsy
