"""The six basic tasks through the real runner CLI (start, next, submit) against runner_support's fake REST server.

The HTTP side (/health, /jobs, telemetry, instance identity) is runner_support.FakeUnity; every skill runs on the
editor model of pack_fake_editor, whose journal entries go to the file the runner reads back. This covers what the
direct tests do not: the shipped manifest and prompt files, the private verifier fetched from eval/pack/private, the
answer copied by submit, result.json and the teardown back to the resting scene.
"""
import json
import os
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
            if name == "editor_run_scenario":
                return self.scenario(args)
            try:
                result = self.model.call(name, args, agent)
            except SkillError as exc:
                payload = exc.payload or {}
                raise RS.SkillFailure(payload.get("errorCode") or "ERROR", payload.get("error") or str(exc))
            if isinstance(result, dict) and result.get("jobId"):
                # The model finishes script jobs at once (a fixture's script_create is awaited through /jobs).
                self.jobs[result["jobId"]] = {"jobId": result["jobId"], "kind": name, "status": "completed",
                                              "resultData": {}}
            return result

    def scenario(self, args):
        """editor_run_scenario as a finished job: runs the steps on the model, /jobs/<id> answers the result."""
        result = self.model.run_scenario({"steps": json.loads(args["steps"]),
                                          "executionMode": args.get("executionMode") or "play"})
        job_id = "scenario-%d" % (len(self.jobs) + 1)
        self.jobs[job_id] = {"jobId": job_id, "kind": "run_scenario", "status": result["status"],
                             "error": result["error"], "resultData": result}
        return {"success": True, "status": "accepted", "jobId": job_id, "kind": "run_scenario"}


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

    def attempt(self, bench, task_id, solver, lang="zh-CN", tmp="ugui"):
        """start, next, the solver, submit. A task that requires uGUI runs in a project with TextMesh Pro as `tmp`
        says (see FakeEditor.set_text_setup), its Essential Resources imported."""
        task = modules(task_id)[0]
        if task.requires.get("dedicated"):
            with open(os.path.join(bench.project, "ProjectSettings", "UnitySkillsEvaluation.json"), "w") as handle:
                json.dump({"schema": 1, "kind": "UnitySkillsEvaluationProject"}, handle)
        if "com.unity.ugui" in (task.requires.get("packages") or ()):
            bench.unity.model.set_text_setup(tmp, settings=True)
        lock_path = os.path.join(bench.project, "Packages", "packages-lock.json")
        with open(lock_path, encoding="utf-8") as handle:
            lock = json.load(handle)
        for name in task.requires.get("packages") or ():
            lock["dependencies"].setdefault(name, {"version": "1.0.0", "depth": 0, "source": "builtin"})
        with open(lock_path, "w", encoding="utf-8") as handle:
            json.dump(lock, handle)
        code, env = bench.run("start", "--lang", lang, "--track", task.track, "--tier", task.tier, "--tasks",
                              task_id, "--private-dir", PRIVATE_ROOT)
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

    def test_dev_tasks_through_the_runner_scenario(self):
        for task_id, runtime_negative in (("T1", "turnspeed_unset"), ("T2", "wrong_menu"), ("T9", "awake_reset")):
            _task, _private, solutions = modules(task_id)
            with self.subTest(task=task_id, case="reference"):
                bench = self.bench("URP")
                result, state = self.attempt(bench, task_id, solutions.reference)
                self.assertEqual(result["status"], "pass", result)
                self.assertEqual(result["evidence"]["scenario"]["passed"], True, result["evidence"]["scenario"])
                self.assertTrue(os.path.isfile(os.path.join(
                    bench.paths.attempt_dir(state["sessionId"], state["attemptId"]), "scenario.json")))
            negative = next(n for n in solutions.NEGATIVES if n.name == runtime_negative)
            with self.subTest(task=task_id, case=negative.name):
                bench = self.bench("URP")
                result, _state = self.attempt(bench, task_id, negative.apply, lang="en")
                self.assertEqual(self.failed(result), negative.expect_failed, result.get("score"))

    def test_t2_on_a_project_with_only_the_input_system(self):
        """The prompt names InputSystemUIInputModule; its Standalone counterpart fails D6 and the Play Mode run."""
        _task, _private, solutions = modules("T2")
        for case, solver, status, failed in (("reference", solutions.reference, "pass", set()),
                                             ("other_module", solutions.other_input_module, None, {"D6", "D14"})):
            with self.subTest(case=case):
                bench = self.bench("URP")
                bench.unity.model.set_input_setup(1)
                result, state = self.attempt(bench, "T2", solver, lang="en")
                self.assertEqual(state["handles"]["inputModule"], "InputSystemUIInputModule")
                with open(os.path.join(bench.paths.attempt_dir(state["sessionId"], state["attemptId"]),
                                       "prompt.md"), encoding="utf-8-sig") as handle:
                    prompt = handle.read()
                self.assertIn("`InputSystemUIInputModule` component", prompt)
                self.assertNotIn("StandaloneInputModule", prompt)
                scenario = result["evidence"]["scenario"]
                self.assertEqual(self.failed(result), failed, result.get("score"))
                if status:
                    self.assertEqual((result["status"], scenario["passed"]), (status, True), result)
                else:
                    self.assertEqual((scenario["passed"], scenario["error"]),
                                     (False, "Runtime error logs were captured."))

    def test_t2_label_component_follows_text_mesh_pro(self):
        """The prompt names TextMeshProUGUI where the project carries TextMesh Pro (uGUI 2.x, or its package on 2022)
        and UnityEngine.UI.Text where it does not; the reference passes in each, the other component's text fails D3,
        and a project that has TextMesh Pro without its Essential Resources is stopped before the task is built."""
        _task, _private, solutions = modules("T2")
        for tmp, component, other in (("ugui", "TextMeshProUGUI", "UnityEngine.UI.Text"),
                                      ("package", "TextMeshProUGUI", "UnityEngine.UI.Text"),
                                      (None, "UnityEngine.UI.Text", "TextMeshProUGUI")):
            with self.subTest(tmp=tmp):
                bench = self.bench("URP")
                result, state = self.attempt(bench, "T2", solutions.reference, lang="en", tmp=tmp)
                self.assertEqual(state["handles"]["labelComponent"], component)
                with open(os.path.join(bench.paths.attempt_dir(state["sessionId"], state["attemptId"]),
                                       "prompt.md"), encoding="utf-8-sig") as handle:
                    prompt = handle.read()
                self.assertIn("its text is a `%s` component" % component, prompt)
                self.assertNotIn(other, prompt)
                self.assertEqual((result["status"], self.failed(result)), ("pass", set()), result.get("score"))
        bench = self.bench("URP")
        bench.unity.model.set_text_setup("ugui", settings=False)
        with open(os.path.join(bench.project, "ProjectSettings", "UnitySkillsEvaluation.json"), "w") as handle:
            json.dump({"schema": 1, "kind": "UnitySkillsEvaluationProject"}, handle)
        code, env = bench.run("check", "--track", "dev", "--tier", "standard", "--tasks", "T2", "--private-dir",
                              PRIVATE_ROOT)
        self.assertEqual((code, [p["code"] for p in env["problems"]]), (2, ["TMP_RESOURCES_MISSING"]))

    def test_urp_reference(self):
        for task_id in ("S01", "S06"):
            with self.subTest(task=task_id):
                bench = self.bench("URP")
                result, _state = self.attempt(bench, task_id, modules(task_id)[2].reference)
                self.assertEqual(result["status"], "pass", result)

# Producer:Betsy
