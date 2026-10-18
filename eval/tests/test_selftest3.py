"""Offline tests for eval/selftest3.py and eval/harness/solver_driver.py.

The runner CLI runs in-process against the fake editor of eval/tests/runner/runner_support.py, with the two-task test
pack of eval/tests/e2e/e2e_pack.py as a --pack-dir pack.
"""
import argparse
import json
import os
import shutil
import sys
import threading
import time
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)                      # eval/tests: runner/, e2e/
sys.path.insert(0, os.path.dirname(HERE))     # eval: harness/, selftest3, cli

from runner.runner_support import Bench, package_version, store  # noqa: E402
from e2e.e2e_pack import E2EPack  # noqa: E402
from harness import solver_driver as D  # noqa: E402
import cli  # noqa: E402
import selftest3  # noqa: E402
from usk_eval import environment  # noqa: E402


def namespace(**kw):
    values = dict(project=None, task=None, private_dir=None, pack_dir=None, solutions_dir=None, gate_file=None,
                  repeat=2, extra_seed=None, case=None, no_gate=False, abort_active=False, cleanup_stale=False,
                  wait_quiet=5.0, wait_lease=0.0, lang="en", dry_run=False, unity_major=None, pipeline=None,
                  plugin_version=None)
    values.update(kw)
    return argparse.Namespace(**values)


class Fixture(object):
    """Bench + test pack + selftest3 entry points writing into temp files."""

    def __init__(self, test, tasks=("E01",), **pack_kw):
        self.test = test
        self.b = Bench(test, stand_ins=False)
        self.pack = E2EPack.create(os.path.join(self.b.tmp, "e2e"), tasks=tasks, **pack_kw)
        self.gate_file = os.path.join(self.b.tmp, "runs", "gate.json")
        self.lines = []

    def args(self, **kw):
        values = dict(project=self.b.project, pack_dir=self.pack.pack_dir, task=["E01"], gate_file=self.gate_file)
        values.update(kw)
        return namespace(**values)

    def selftest(self, **kw):
        return selftest3.run_from_args(self.args(**kw), cli.GATE_INPUTS, out=self.lines.append, environ=self.b.env,
                                       inproc=True)

    def gate3(self, **kw):
        kw.setdefault("unity_major", "6000")
        kw.setdefault("pipeline", "URP")                  # what the fake editor reports
        kw.setdefault("plugin_version", package_version())
        return selftest3.gate_from_args(self.args(**kw), cli.GATE_INPUTS, out=self.lines.append, environ=self.b.env,
                                        inproc=True)

    @property
    def text(self):
        return "\n".join(self.lines)

    def gate(self):
        with open(self.gate_file, "r", encoding="utf-8") as handle:
            return json.load(handle)

    def rows(self):
        directory = os.path.dirname(self.gate_file)
        names = sorted(n for n in os.listdir(directory) if n != "gate.json" and n.endswith(".json"))
        with open(os.path.join(directory, names[-1]), "r", encoding="utf-8") as handle:
            return json.load(handle)["rows"]

    def edit_solutions(self, task_id, old, new):
        path = os.path.join(self.pack.solutions_dir, task_id + ".py")
        with open(path, "r", encoding="utf-8") as handle:
            text = handle.read()
        self.test.assertIn(old, text)
        with open(path, "w", encoding="utf-8") as handle:
            handle.write(text.replace(old, new))


class SelftestTests(unittest.TestCase):
    def test_every_case_green_records_a_green_gate(self):
        fx = Fixture(self)
        self.assertEqual(fx.selftest(), 0, fx.text)
        rows = fx.rows()
        self.assertEqual([r["case"] for r in rows], [
            "reference#0", "reference#1", "determinism", "negative:wrong_parent", "negative:wrong_report",
            "negative:nothing_done", "negative:stray_object", "negative:stray_and_wrong_parent",
            "alternative:by_path"])
        self.assertTrue(all(r["ok"] for r in rows), fx.text)
        by_case = {r["case"]: r for r in rows}
        self.assertEqual(by_case["negative:stray_and_wrong_parent"]["failed"], ["A1", "A3"])
        self.assertEqual(by_case["negative:wrong_report"]["status"], "partial")
        self.assertEqual(by_case["reference#0"]["fingerprint"], by_case["reference#1"]["fingerprint"])
        gate = fx.gate()
        self.assertEqual(gate["schema"], 1)
        (key, entry), = gate["gates"].items()
        inputs = selftest3.gate_inputs_hash(D.PackAccess(pack_dir=fx.pack.pack_dir, solutions_dir=fx.pack.solutions_dir),
                                            "E01", cli.GATE_INPUTS)
        self.assertEqual(entry, {"taskId": "E01", "caseVersion": 1, "unityMajor": "6000",
                                 "pluginVersion": package_version(), "pipeline": "URP", "gateInputsHash": inputs,
                                 "green": True, "cases": 9, "failing": [], "recordedAt": entry["recordedAt"],
                                 "buildIdentity": "unavailable", "packageRoot": None, "assembliesLoadedAtUtc": None})
        self.assertEqual(key, store.canonical_digest({
            "taskId": "E01", "caseVersion": 1, "unityMajor": "6000", "pluginVersion": package_version(),
            "pipeline": "URP", "gateInputsHash": inputs}))
        self.assertEqual({r["pipeline"] for r in rows if r["case"] != "determinism"}, {"URP"})
        # nothing is left behind in the editor or on disk
        self.assertEqual(fx.b.paths and D.StateView(fx.b.project).current()["status"], "completed")
        self.assertEqual(D.StateView(fx.b.project).residue(), [])
        self.assertFalse(os.path.exists(fx.b.paths.io_root))
        self.assertEqual([s["path"] for s in fx.b.unity.scenes], ["Assets/Scenes/Main.unity"])

    def test_the_solver_acts_as_selftest_solver(self):
        fx = Fixture(self)
        self.assertEqual(fx.selftest(case=["reference#0"]), 0, fx.text)
        agents = {agent for _method, path, agent in fx.b.unity.requests if path == "/skill/gameobject_create"}
        self.assertEqual(agents, {"UnitySkillsEval", "SelftestSolver"})
        journal = D.StateView(fx.b.project)
        current = journal.current()
        self.assertIn("SelftestSolver", fx.b.result(current["sessionId"], journal.session(current["sessionId"])[
            "tasks"]["E01"]["attempts"][0])["evidence"]["journal"]["agents"])

    def test_case_filter_runs_matching_cases_and_records_no_gate(self):
        fx = Fixture(self)
        self.assertEqual(fx.selftest(case=["alternative"]), 0, fx.text)
        self.assertEqual([r["case"] for r in fx.rows()], ["alternative:by_path"])
        self.assertFalse(os.path.exists(fx.gate_file))
        self.assertIn("gate not recorded", fx.text)

    def test_unexpected_failed_set_is_red_and_recorded(self):
        fx = Fixture(self)
        fx.edit_solutions("E01", 'Negative("wrong_parent", wrong_parent, {"A1"})',
                          'Negative("wrong_parent", wrong_parent, {"A3"})')
        self.assertEqual(fx.selftest(), 1, fx.text)
        (entry,) = fx.gate()["gates"].values()
        self.assertFalse(entry["green"])
        self.assertEqual(entry["failing"], ["negative:wrong_parent"])
        self.assertIn("expected: ['A3']", fx.text)
        self.assertEqual(fx.gate3(), 1)
        self.assertIn("recorded red: negative:wrong_parent", fx.text)

    def test_negative_that_passes_is_red(self):
        fx = Fixture(self)
        fx.edit_solutions("E01", "def nothing_done(ctx):\n    return _answer(_right_path(ctx))",
                          "def nothing_done(ctx):\n    return reference(ctx)")
        self.assertEqual(fx.selftest(case=["nothing_done"]), 1, fx.text)
        row = fx.rows()[0]
        self.assertEqual((row["status"], row["failed"], row["ok"]), ("pass", [], False))

    def test_nondeterministic_build_fails_the_determinism_row(self):
        fx = Fixture(self, nondeterministic=("E01",))
        self.assertEqual(fx.selftest(case=["reference"]), 1, fx.text)
        rows = {r["case"]: r for r in fx.rows()}
        self.assertTrue(rows["reference#0"]["ok"] and rows["reference#1"]["ok"])
        self.assertFalse(rows["determinism"]["ok"])
        self.assertEqual(rows["determinism"]["status"], "differs")

    def test_other_manifest_seeds_and_extra_seeds_get_a_reference_run(self):
        fx = Fixture(self, seeds=(7001, 7002))
        self.assertEqual(fx.selftest(case=["reference@"], extra_seed=[9]), 0, fx.text)
        self.assertEqual([r["case"] for r in fx.rows()], ["reference@7002", "reference@9"])
        self.assertEqual([r["seed"] for r in fx.rows()], [7002, 9])

    def test_a_solver_that_raises_aborts_its_session_and_the_run_goes_on(self):
        fx = Fixture(self)
        fx.edit_solutions("E01", "def stray_object(ctx):\n    ans = reference(ctx)",
                          "def stray_object(ctx):\n    raise RuntimeError('boom')\n    ans = reference(ctx)")
        self.assertEqual(fx.selftest(case=["stray_object", "alternative"]), 1, fx.text)
        rows = {r["case"]: r for r in fx.rows()}
        self.assertEqual(rows["negative:stray_object"]["status"], "solver_error")
        self.assertIn("boom", rows["negative:stray_object"]["note"])
        self.assertTrue(rows["alternative:by_path"]["ok"])
        view = D.StateView(fx.b.project)
        self.assertEqual(view.current()["status"], "completed")
        self.assertEqual(view.residue(), [])

    def test_solutions_that_cannot_be_gated_are_reported(self):
        fx = Fixture(self)
        fx.edit_solutions("E01", "    Negative(\"wrong_parent\", wrong_parent, {\"A1\"}),\n", "")
        self.assertEqual(fx.selftest(), 1, fx.text)
        (row,) = fx.rows()
        self.assertEqual((row["case"], row["ok"]), ("solutions", False))
        self.assertIn("needs at least 5 negatives, has 4", row["reasons"][0])
        self.assertIn("gate E01 not recorded: no attempt was issued", fx.text)
        self.assertFalse(os.path.exists(fx.gate_file))
        os.remove(os.path.join(fx.pack.solutions_dir, "E01.py"))
        self.assertEqual(selftest3.run_from_args(fx.args(dry_run=True), cli.GATE_INPUTS, out=fx.lines.append), 1)
        self.assertIn("no solutions module for E01", fx.text)

    def test_dry_run_lists_the_cases_without_an_editor(self):
        fx = Fixture(self)
        out = []
        code = selftest3.run_from_args(fx.args(dry_run=True, project="/nonexistent"), cli.GATE_INPUTS, out=out.append)
        self.assertEqual(code, 0, out)
        text = "\n".join(out)
        for name in ("reference#0", "reference#1", "negative:wrong_parent", "alternative:by_path"):
            self.assertIn(name, text)

    def test_repeat_below_two_and_unknown_tasks_are_refused(self):
        fx = Fixture(self)
        self.assertEqual(fx.selftest(repeat=1), 2)
        self.assertIn("at least 2 reference repeats", fx.text)
        self.assertEqual(fx.selftest(task=["S99"]), 2)
        self.assertIn("not in the pack: S99", fx.text)
        self.assertEqual(fx.selftest(project="/nonexistent"), 2)
        self.assertEqual(fx.selftest(pack_dir=fx.pack.pack_dir, private_dir=fx.pack.private_dir), 2)
        self.assertIn("mutually exclusive", fx.text)

    def test_environment_problems_end_the_run_with_the_runner_fix(self):
        fx = Fixture(self)
        fx.b.unity.mode = "approval"
        self.assertEqual(fx.selftest(), 2, fx.text)
        self.assertIn("MODE_NOT_BYPASS", fx.text)
        self.assertIn("Switch the UnitySkills operating mode to Bypass", fx.text)
        self.assertFalse(os.path.exists(fx.gate_file))

    def test_a_session_left_active_is_refused_unless_told_to_abort_it(self):
        fx = Fixture(self)
        code, env = fx.b.run("start", "--lang", "en", "--pack-dir", fx.pack.pack_dir, "--tasks", "E01")
        self.assertEqual(code, 0, env)
        self.assertEqual(fx.selftest(case=["alternative"]), 2, fx.text)
        self.assertIn("still active", fx.text)
        self.assertEqual(fx.selftest(case=["alternative"], abort_active=True), 0, fx.text)
        self.assertEqual(D.StateView(fx.b.project).current()["status"], "completed")

    def test_two_maintainer_tools_never_drive_one_editor(self):
        fx = Fixture(self)
        with D.EditorLease(fx.b.project, "someone else"):
            self.assertEqual(fx.selftest(case=["alternative"]), 2, fx.text)
        self.assertIn("in use by someone else", fx.text)
        self.assertEqual(fx.selftest(case=["alternative"]), 0, fx.text)

    def test_a_compiling_editor_is_waited_for_not_failed(self):
        fx = Fixture(self)
        fx.b.unity.compiling = True
        threading.Timer(1.5, lambda: setattr(fx.b.unity, "compiling", False)).start()
        started = time.time()
        self.assertEqual(fx.selftest(case=["alternative"]), 0, fx.text)
        self.assertGreaterEqual(time.time() - started, 1.0)
        (row,) = fx.rows()
        self.assertTrue(row["ok"])
        self.assertIn("compiling", fx.text)

    def inject_foreign_import(self, fx, times=1):
        """A third party's file change that the runner's own asset_refresh picks up (outside A)."""
        original = fx.b.unity.call
        left = [times]

        def call(name, args, agent):
            result = original(name, args, agent)
            if name == "asset_refresh" and agent == "UnitySkillsEval" and left[0] > 0:
                left[0] -= 1
                fx.b.unity.publish("file_changes", "rest", "UnitySkillsEval", {
                    "imported": ["Packages/other/Tool.cs"], "deleted": [], "moved": [], "count": 1, "omitted": 0})
            return result
        fx.b.unity.call = call

    def test_a_failed_case_is_never_run_again_but_names_the_records_it_did_not_make(self):
        fx = Fixture(self)
        self.inject_foreign_import(fx, times=1)
        self.assertEqual(fx.selftest(case=["alternative"]), 1, fx.text)
        (row,) = fx.rows()
        self.assertEqual((row["ok"], row["status"], row["failed"]), (False, "partial", ["A3"]))
        self.assertEqual(len(row["foreign"]), 1)
        self.assertIn("foreign record: imported Packages/other/Tool.cs", fx.text)
        self.assertNotIn("[retry]", fx.text)
        self.assertEqual(len(os.listdir(fx.b.paths.sessions)), 1, "exactly one session ran: a gate passes in one go")

    def test_the_gate_pipeline_comes_from_the_runners_snapshot_and_keeps_pipelines_apart(self):
        fx = Fixture(self)
        for pipeline in ("URP", "BuiltIn"):
            fx.b.unity.pipeline = pipeline
            self.assertEqual(fx.selftest(), 0, fx.text)
        gates = fx.gate()["gates"]
        self.assertEqual(sorted(entry["pipeline"] for entry in gates.values()), ["BuiltIn", "URP"])
        self.assertEqual(len(gates), 2, "the second pipeline must not overwrite the first")
        self.assertEqual(fx.gate3(pipeline="URP"), 0, fx.text)
        self.assertEqual(fx.gate3(pipeline="BuiltIn"), 0, fx.text)
        self.assertEqual(fx.gate3(pipeline="HDRP"), 1)
        self.assertIn("BuiltIn: green", fx.text)
        self.assertIn("HDRP: no gate recorded", fx.text)
        fx.b.unity.pipeline = "URP"
        self.assertEqual(fx.gate3(pipeline=None), 0, "gate3 asks the editor for the pipeline it was not given")

    def test_one_run_keeps_one_pipeline(self):
        fx = Fixture(self)
        runner = D.Runner(fx.b.project, environ=D.clean_environ(dict(os.environ, **fx.b.env)), inproc=True)
        packs = D.PackAccess(pack_dir=fx.pack.pack_dir, solutions_dir=fx.pack.solutions_dir)
        run = selftest3.Selftest(runner, packs, ["E01"], out=fx.lines.append)
        run.observe_pipeline(None)
        run.observe_pipeline("URP")
        run.observe_pipeline("URP")
        with self.assertRaises(selftest3.SelftestError):
            run.observe_pipeline("BuiltIn")

    def test_gate3_is_green_only_for_the_inputs_that_ran(self):
        fx = Fixture(self)
        self.assertEqual(fx.gate3(), 1)
        self.assertIn("no gate recorded for the current inputs", fx.text)
        self.assertEqual(fx.selftest(), 0, fx.text)
        self.assertEqual(fx.gate3(), 0, fx.text)
        self.assertEqual(fx.gate3(unity_major="2022"), 1)             # the other platform has not been gated
        self.assertEqual(fx.gate3(pipeline="BuiltIn"), 1)             # nor the other render pipeline
        self.assertEqual(fx.gate3(plugin_version="9.9.9"), 1)
        fx.edit_solutions("E01", "CHILD = ", "# changed\nCHILD = ")
        self.assertEqual(fx.gate3(), 1)
        self.assertEqual(fx.selftest(case=["alternative"]), 0, fx.text)        # a partial run does not re-green it
        self.assertEqual(fx.gate3(), 1)
        self.assertEqual(fx.selftest(), 0, fx.text)
        self.assertEqual(fx.gate3(), 0)
        self.assertEqual(len(fx.gate()["gates"]), 1, "the superseded entry is dropped")

    def test_gate3_asks_the_editor_for_the_versions(self):
        fx = Fixture(self)
        self.assertEqual(fx.selftest(), 0, fx.text)
        self.assertEqual(fx.gate3(unity_major=None, plugin_version=None, pipeline=None), 0, fx.text)

    def test_hash_matches_the_environment_function_for_the_repo_layout(self):
        fx = Fixture(self)
        root = os.path.join(fx.b.tmp, "repo")
        package = os.path.join(root, "SkillsForUnity", "Evaluation~")
        for rel, text in (("SkillsForUnity/Evaluation~/run_eval.py", "run"), ("SkillsForUnity/Evaluation~/usk_eval/a.py", "a"),
                          ("SkillsForUnity/Evaluation~/usk_eval/usage/u.py", "usage"),
                          ("SkillsForUnity/Evaluation~/usk_eval/report.py", "report"),
                          ("eval/cli.py", "cli"), ("eval/selftest3.py", "st"), ("eval/harness/solver_driver.py", "sd")):
            path = os.path.join(root, *rel.split("/"))
            os.makedirs(os.path.dirname(path), exist_ok=True)
            with open(path, "w") as handle:
                handle.write(text)
        shutil.copytree(os.path.join(fx.pack.pack_dir, "tasks"), os.path.join(package, "pack", "tasks"))
        shutil.copy(os.path.join(fx.pack.pack_dir, "manifest.json"), os.path.join(package, "pack", "manifest.json"))
        shutil.copytree(fx.pack.private_dir, os.path.join(root, "eval", "pack", "private"))
        shutil.copytree(fx.pack.solutions_dir, os.path.join(root, "eval", "pack", "solutions"))
        packs = D.PackAccess(private_dir=os.path.join(root, "eval", "pack", "private"),
                             base_dir=os.path.join(package, "pack"),
                             solutions_dir=os.path.join(root, "eval", "pack", "solutions"))
        mine = selftest3.gate_inputs_hash(packs, "E01", cli.GATE_INPUTS, repo_root=root)
        self.assertEqual(mine, environment.gate_inputs_hash(root, "E01", extra=cli.GATE_INPUTS))
        with open(os.path.join(package, "usk_eval", "usage", "u.py"), "w") as handle:
            handle.write("changed")
        with open(os.path.join(package, "usk_eval", "report.py"), "w") as handle:
            handle.write("changed")
        self.assertEqual(selftest3.gate_inputs_hash(packs, "E01", cli.GATE_INPUTS, repo_root=root), mine)
        with open(os.path.join(package, "usk_eval", "a.py"), "w") as handle:
            handle.write("changed")
        self.assertNotEqual(selftest3.gate_inputs_hash(packs, "E01", cli.GATE_INPUTS, repo_root=root), mine)

    def test_gate_inputs_name_the_maintainer_files_of_the_gate(self):
        self.assertEqual(set(cli.GATE_INPUTS), {"eval/cli.py", "eval/selftest3.py", "eval/harness/solver_driver.py"})
        for rel in cli.GATE_INPUTS:
            self.assertTrue(os.path.isfile(os.path.join(selftest3.REPO, *rel.split("/"))), rel)


class CliWiringTests(unittest.TestCase):
    """eval/cli.py reaches selftest3 and gate3; the global --project works before and after the subcommand."""

    def run_cli(self, argv):
        import io
        from contextlib import redirect_stdout
        out = io.StringIO()
        with redirect_stdout(out):
            code = cli.main(argv)
        return code, out.getvalue()

    def test_dry_run_and_gate3_through_the_real_parser(self):
        fx = Fixture(self)
        pack = fx.pack.pack_dir
        code, text = self.run_cli(["--project", fx.b.project, "selftest3", "--pack-dir", pack, "--task", "E01",
                                   "--dry-run"])
        self.assertEqual(code, 0, text)
        self.assertIn("negative:stray_object", text)
        code, text = self.run_cli(["selftest3", "--project", fx.b.project, "--pack-dir", pack, "--dry-run"])
        self.assertEqual(code, 0, text)
        code, text = self.run_cli(["gate3", "--pack-dir", pack, "--unity-major", "6000", "--pipeline", "URP",
                                   "--plugin-version", "9.9.9", "--gate-file", fx.gate_file])
        self.assertEqual(code, 1, text)
        self.assertIn("no gate recorded for the current inputs", text)
        self.assertIn("6000, URP:", text)

    def test_the_cli_offers_no_generic_retry(self):
        import io
        from contextlib import redirect_stderr
        with redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            self.run_cli(["selftest3", "--retries", "2", "--dry-run"])

    def test_v1_commands_are_untouched(self):
        for name in ("setup-check", "probe", "selftest", "run", "suite", "estimate", "report"):
            with self.assertRaises(SystemExit) as raised:
                self.run_cli([name, "--help"])
            self.assertEqual(raised.exception.code, 0, name)


class DriverTests(unittest.TestCase):
    def agent(self, fx, **kw):
        runner = D.Runner(fx.b.project, environ=D.clean_environ(dict(os.environ, **fx.b.env)), inproc=True)
        packs = D.PackAccess(pack_dir=fx.pack.pack_dir, solutions_dir=fx.pack.solutions_dir)
        return D.ScriptedAgent(runner, packs, **kw)

    def reference(self, packs, task_id):
        return packs.solutions(task_id).reference

    def solvers(self, agent):
        return {t: agent.packs.solutions(t).reference for t in agent.packs.ids()}

    def start(self, agent, tasks="E01,E02", mode="suite"):
        result = agent.start("--track", "ops", "--tier", "basic", "--tasks", tasks, "--mode", mode, "--no-usage")
        self.assertTrue(result.ok, result.describe())
        return result

    def test_suite_session_runs_to_done_and_hooks_fire_in_order(self):
        fx = Fixture(self, tasks=("E01", "E02"))
        agent = self.agent(fx)
        started = self.start(agent)
        self.assertEqual(started.data["n"], 2)
        events = []
        hooks = {"after_next": lambda a, att: events.append(("next", att.task_id)),
                 "before_submit": lambda a, att, ans: events.append(("submit", att.task_id, ans["objectPath"][-5:])),
                 "after_submit": lambda a, att, res: events.append(("sent", att.task_id, res.data["remaining"]))}
        runs = agent.run_session(self.solvers(agent), hooks=hooks)
        self.assertEqual([r.attempt.task_id for r in runs[:2]], ["E01", "E02"])
        self.assertTrue(all(r.submit_result.ok and r.submit_result.clean for r in runs[:2]))
        self.assertTrue(runs[-1].done, runs[-1].next_result.describe())
        self.assertEqual(events, [("next", "E01"), ("submit", "E01", "Crate"), ("sent", "E01", 1),
                                  ("next", "E02"), ("submit", "E02", "arrel"), ("sent", "E02", 0)])
        view = agent.state
        sid = started.data["sessionId"]
        results = [view.result(sid, a) for t in ("E01", "E02") for a in view.session(sid)["tasks"][t]["attempts"]]
        self.assertEqual([r["status"] for r in results], ["pass", "pass"])
        self.assertEqual([r["submittedBy"] for r in results], ["agent", "agent"])

    def test_strict_session_ends_after_one_task(self):
        fx = Fixture(self, tasks=("E01", "E02"))
        agent = self.agent(fx)
        self.start(agent, mode="strict")
        runs = agent.run_session(self.solvers(agent))
        self.assertEqual(len(runs), 1)
        self.assertTrue(runs[0].submit_result.data["stop"])
        self.assertIsNone(runs[0].submit_result.data["nextCommand"])

    def test_a_failing_solver_aborts_instead_of_submitting(self):
        fx = Fixture(self)
        agent = self.agent(fx)
        self.start(agent, "E01")

        def broken(ctx):
            raise ValueError("no")
        run = agent.run_attempt(broken)
        self.assertIn("ValueError: no", run.solver_error)
        self.assertTrue(run.aborted.ok)
        self.assertIsNone(run.submit_result)
        self.assertEqual(D.StateView(fx.b.project).current()["status"], "aborted")

    def test_submit_without_work_and_stop_hook(self):
        fx = Fixture(self, tasks=("E01", "E02"))
        agent = self.agent(fx)
        started = self.start(agent)

        def stop(a, attempt, result):
            raise D.StopSession()
        runs = agent.run_session(None, hooks={"after_submit": stop})
        self.assertEqual(len(runs), 0)          # StopSession leaves run_session before the run is recorded
        sid = started.data["sessionId"]
        aid = agent.state.session(sid)["tasks"]["E01"]["attempts"][0]
        result = agent.state.result(sid, aid)
        self.assertEqual((result["status"], result["answer"]["present"]), ("partial", False))

    def test_panel_style_calls_write_a_result_file_and_nothing_else(self):
        fx = Fixture(self)
        agent = self.agent(fx)
        self.start(agent, "E01")
        run_next, attempt = agent.next()
        out = os.path.join(fx.b.tmp, "panel")
        os.makedirs(out)
        result = agent.submit(attempt, "no-answer", panel=True, result_file=os.path.join(out, "r.json"),
                              log_file=os.path.join(out, "r.log"), cmd_id="cmd-1")
        self.assertTrue(result.ok, result.describe())
        self.assertEqual(result.envelope["cmdId"], "cmd-1")
        self.assertEqual(result.envelope["exitCode"], 0)
        self.assertEqual(agent.state.result(attempt.session_id, attempt.attempt_id)["submittedBy"], "panel")

    def test_clean_environ_drops_host_and_override_variables(self):
        base = {"PATH": "/bin", "CLAUDECODE": "1", "CLAUDE_CODE_SESSION_ID": "x", "CODEX_THREAD_ID": "y",
                "USK_EVAL_PRIVATE_URLS": "[]", "USK_EVAL_REGISTRY": "/r", "KEEP": "1"}
        env = D.clean_environ(base, EXTRA="2", KEEP=None)
        self.assertEqual(env, {"PATH": "/bin", "USK_EVAL_REGISTRY": "/r", "EXTRA": "2"})

    def test_runner_derive_changes_only_the_environment(self):
        runner = D.Runner("/tmp", environ={"A": "1"}, session="s1", inproc=True)
        other = runner.derive(A=None, B=2)
        self.assertEqual((other.environ, other.session, other.inproc), ({"B": "2"}, "s1", True))
        self.assertEqual(runner.environ, {"A": "1"})

    def test_wait_quiet_waits_for_running_jobs(self):
        fx = Fixture(self)
        agent = self.agent(fx)
        fx.b.unity.jobs["j1"] = {"jobId": "j1", "kind": "test", "status": "running"}
        threading.Timer(0.4, lambda: fx.b.unity.jobs["j1"].update(status="completed")).start()
        messages = []
        started = time.time()
        seen = D.wait_quiet(agent.client, timeout_s=10, poll_s=0.1, log=messages.append)
        self.assertGreaterEqual(time.time() - started, 0.3)
        self.assertEqual(seen, ["test:running"])
        self.assertTrue(messages and "test:running" in messages[0])
        fx.b.unity.jobs["j2"] = {"jobId": "j2", "kind": "compile", "status": "running"}
        with self.assertRaises(D.EditorBusy):
            D.wait_quiet(agent.client, timeout_s=0.3, poll_s=0.1)

    def test_wait_quiet_ignores_the_leftover_of_a_crashed_run(self):
        fx = Fixture(self)
        agent = self.agent(fx)
        fx.b.unity.jobs["old"] = {"jobId": "old", "kind": "test", "status": "running", "updatedAt": time.time() - 3600}
        self.assertEqual(D.wait_quiet(agent.client, timeout_s=0.5), [])
        fx.b.unity.jobs["old"]["updatedAt"] = time.time()
        with self.assertRaises(D.EditorBusy):
            D.wait_quiet(agent.client, timeout_s=0.5, poll_s=0.1)

    def test_lease_takes_over_a_dead_owner_and_refuses_a_live_one(self):
        fx = Fixture(self)
        path = os.path.join(fx.b.project, "Library", "UnitySkills", "driver.lock")
        with open(path, "w") as handle:
            json.dump({"pid": 2 ** 22 + 12345, "owner": "gone", "startedAt": store.now_iso(), "token": "t"}, handle)
        with D.EditorLease(fx.b.project, "me"):
            with self.assertRaises(D.EditorBusy):
                D.EditorLease(fx.b.project, "second").acquire()
        self.assertFalse(os.path.exists(path))

    def test_solutions_lint(self):
        fx = Fixture(self)
        packs = D.PackAccess(pack_dir=fx.pack.pack_dir, solutions_dir=fx.pack.solutions_dir)
        solutions = packs.solutions("E01")
        self.assertEqual(solutions.problems(), [])
        self.assertTrue(any("at least 6 negatives" in p for p in solutions.problems(min_negatives=6)))
        solutions.negatives = solutions.negatives + [solutions.negatives[0]]
        self.assertTrue(any("listed twice" in p for p in solutions.problems()))


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
