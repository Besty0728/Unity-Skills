"""State machine, idempotency and crash recovery against the fake editor (contract 2.4, 2.7, 2.8, 2.9, 3)."""
import json
import os
import shutil
import tempfile
import threading
import time
import unittest
from unittest import mock

from .runner_support import Bench, real_private, rest, store, verify_module
from usk_eval import attempt, fixture, session


class Crash(BaseException):
    """Stands in for the runner process being killed mid-command."""


def crash(*_args, **_kwargs):
    raise Crash()


class Base(unittest.TestCase):
    def setUp(self):
        self.b = Bench(self)

    def start(self, *extra, **env):
        code, envelope = self.b.run("start", "--lang", "en", *extra, **env)
        self.assertEqual(code, 0, envelope)
        return envelope["data"]["sessionId"]

    def issue(self, sid, **env):
        code, envelope = self.b.run("next", "--session", sid, **env)
        self.assertEqual(code, 0, envelope)
        self.assertFalse(envelope["data"]["done"], envelope)
        return envelope["data"]

    def submit(self, sid, data, *extra, **env):
        args = list(extra) or ["--answer", data["answerFile"]]
        return self.b.run("submit", "--session", sid, "--attempt", data["attemptId"], *args, **env)

    def codes(self, envelope):
        return [p["code"] for p in envelope["problems"]]

    def run_dir(self, data):
        scope = self.b.state(data["sessionId"], data["attemptId"])["scope"]
        return self.b.unity.disk(scope["assetRoot"])


class IdempotencyTests(Base):
    def test_start_is_idempotent_for_the_same_selection(self):
        sid = self.start("--tasks", "S99")
        code, env = self.b.run("start", "--lang", "en", "--tasks", "S99")
        self.assertEqual((code, env["data"]["sessionId"], env["data"]["created"]), (0, sid, False))
        code, env = self.b.run("start")
        self.assertEqual((code, env["data"]["created"]), (0, False))
        for extra in (["--tasks", "S98"], ["--mode", "strict"], ["--lang", "zh-CN"]):
            code, env = self.b.run("start", *extra)
            self.assertEqual((code, self.codes(env)), (3, ["SESSION_ACTIVE"]), extra)
            self.assertEqual(env["problems"][0]["args"], [sid])

    def test_reissue_does_not_rebuild(self):
        sid = self.start("--tasks", "S99")
        first = self.issue(sid)
        seq = self.b.unity.seq
        again = self.issue(sid)
        self.assertTrue(again["reissued"])
        self.assertEqual(again["attemptId"], first["attemptId"])
        self.assertEqual(self.b.unity.seq, seq)
        self.assertEqual(self.b.state(sid, first["attemptId"])["reissues"], 1)
        os.remove(first["promptFile"])
        self.assertTrue(os.path.isfile(self.issue(sid)["promptFile"]))

    def test_duplicate_submit_returns_acceptance_without_rescoring(self):
        sid = self.start("--tasks", "S99,S98")
        data = self.issue(sid)
        self.b.solve(data)
        code, env = self.submit(sid, data)
        self.assertEqual((code, env["data"]["alreadySubmitted"], env["data"]["remaining"]), (0, False, 1))
        before = self.b.result(sid, data["attemptId"])
        self.b.answer(data, {"objectPath": "elsewhere", "declaredComplete": False})
        code, env = self.submit(sid, data)
        self.assertEqual((code, env["data"]["alreadySubmitted"]), (0, True))
        self.assertEqual(self.b.result(sid, data["attemptId"])["answer"], before["answer"])
        self.assertEqual(self.b.state(sid, data["attemptId"])["duplicateSubmits"], 1)


class SubmitTests(Base):
    def test_missing_answer_file_keeps_the_attempt_ready(self):
        sid = self.start("--tasks", "S99")
        data = self.issue(sid)
        code, env = self.submit(sid, data)
        self.assertEqual((code, self.codes(env)), (3, ["ANSWER_NOT_FOUND"]))
        self.assertEqual(env["problems"][0]["args"], [data["answerFile"]])
        self.assertEqual(self.b.state(sid, data["attemptId"])["phase"], "ready")
        self.b.solve(data)
        code, env = self.submit(sid, data)
        self.assertEqual(code, 0, env)

    def test_unparseable_answer_is_accepted_and_recorded(self):
        sid = self.start("--tasks", "S99")
        data = self.issue(sid)
        self.b.answer(data, "\ufeff{not json")
        code, env = self.submit(sid, data)
        self.assertEqual(code, 0, env)
        result = self.b.result(sid, data["attemptId"])
        self.assertEqual(result["answer"]["error"], "invalid_json")
        self.assertEqual(result["status"], "partial")
        with open(self.b.paths.result_file(sid, data["attemptId"]).replace("result.json", "answer.json"), "rb") as h:
            self.assertTrue(h.read().startswith(b"\xef\xbb\xbf{not json"))

    def test_wrong_solution_is_partial_with_hints_only_in_the_result(self):
        sid = self.start("--tasks", "S99")
        data = self.issue(sid)
        self.b.solve(data, correct=False)
        code, env = self.submit(sid, data)
        self.assertEqual(code, 0)
        self.assertNotIn("check the parent", json.dumps(env))
        result = self.b.result(sid, data["attemptId"])
        self.assertEqual((result["status"], result["reasons"]), ("partial", ["failed: A1"]))
        self.assertEqual(result["requirementsFailed"], ["R1"])
        rows = {row["id"]: row for row in result["score"]["assertions"]}
        self.assertEqual((rows["A1"]["hint"], rows["A2"]["hint"]), ("check the parent", None))
        self.assertEqual(result["score"]["completion"], 40.0)

    def test_attempt_checks(self):
        sid = self.start("--tasks", "S99")
        data = self.issue(sid)
        code, env = self.submit(sid, data, "--retry-verify")
        self.assertEqual((code, self.codes(env)), (3, ["RETRY_NOT_APPLICABLE"]))
        code, env = self.b.run("submit", "--session", sid, "--attempt", "0123456789ab", "--no-answer")
        self.assertEqual((code, self.codes(env)), (3, ["ATTEMPT_NOT_FOUND"]))
        code, env = self.submit(sid, data, "--answer", data["answerFile"], "--no-answer")
        self.assertEqual((code, self.codes(env)), (3, ["BAD_ARGUMENTS"]))

    def test_pending_verification_blocks_until_retry(self):
        self.b.stand.private_fail = 2
        sid = self.start("--tasks", "S99,S98")
        data = self.issue(sid)
        self.b.solve(data)
        code, env = self.submit(sid, data)
        self.assertEqual((code, self.codes(env)), (2, ["VERIFICATION_PENDING"]))
        self.assertTrue(env["data"]["pending"] and env["data"]["accepted"])
        self.assertIn("--retry-verify", env["problems"][0]["args"][0])
        self.assertEqual(env["next"]["command"], "submit")
        self.assertEqual(self.b.state(sid, data["attemptId"])["phase"], "pending_verification")
        self.assertTrue(os.path.isdir(self.run_dir(data)))
        code, env = self.b.run("next", "--session", sid)
        self.assertEqual((code, self.codes(env)), (2, ["VERIFICATION_PENDING"]))
        code, env = self.submit(sid, data, "--retry-verify")
        self.assertEqual((code, self.codes(env)), (2, ["VERIFICATION_PENDING"]))
        state = self.b.state(sid, data["attemptId"])
        self.assertEqual(state["private"]["attempts"], [{"via": "https://example/S99.py", "error": "http 404"}])
        code, env = self.submit(sid, data, "--retry-verify", "--private-zip", "/tmp/pack.zip")
        self.assertEqual(code, 0, env)
        self.assertEqual(self.b.stand.fetches[-1]["zip"], os.path.abspath("/tmp/pack.zip"))
        result = self.b.result(sid, data["attemptId"])
        self.assertEqual(result["status"], "pass")
        self.assertTrue(result["verifiedLate"])
        self.assertIn("verified_late", result["reasons"])
        self.assertTrue(result["verifier"]["deleted"])
        self.assertFalse(os.path.isdir(self.run_dir(data)))

    def test_verifier_that_cannot_be_written_waits_for_retry(self):
        self.b.stand.load_fail = 1
        sid = self.start("--tasks", "S99")
        data = self.issue(sid)
        self.b.solve(data)
        code, env = self.submit(sid, data)
        self.assertEqual((code, self.codes(env)), (2, ["VERIFICATION_PENDING"]))
        state = self.b.state(sid, data["attemptId"])
        self.assertEqual(state["phase"], "pending_verification")
        self.assertEqual(state["private"]["attempts"][-1]["error"], "temp_write_failed")
        self.assertEqual(self.b.stand.fetches[-1]["environ"]["USK_EVAL_REGISTRY"], self.b.registry)
        code, env = self.submit(sid, data, "--retry-verify")
        self.assertEqual(code, 0, env)
        self.assertEqual(self.b.result(sid, data["attemptId"])["status"], "pass")

    def test_tls_failures_get_their_own_code_and_hint(self):
        tls = [{"via": "https://raw.example/S99.py", "error": "tls_error"},
               {"via": "https://cdn.example/S99.py", "error": "tls_error"}]
        mixed = [tls[0], {"via": "https://cdn.example/S99.py", "error": "http 404"}]
        self.b.stand.probe_reachable = False
        self.b.stand.probe_attempts = tls
        code, env = self.b.run("check", "--tasks", "S99")
        self.assertEqual((code, self.codes(env)), (2, ["PRIVATE_PACK_TLS"]))
        self.assertEqual(env["problems"][0]["fix"], real_private.TLS_HINT)
        self.assertEqual(self.b.stand.probes[-1]["environ"]["USK_EVAL_REGISTRY"], self.b.registry)
        self.b.stand.probe_attempts = mixed
        code, env = self.b.run("check", "--tasks", "S99")
        self.assertEqual(self.codes(env), ["PRIVATE_PACK_UNREACHABLE"])
        self.assertTrue(env["problems"][0]["fix"].endswith(" " + real_private.TLS_HINT))
        self.b.stand.probe_attempts = [mixed[1]]
        code, env = self.b.run("check", "--tasks", "S99")
        self.assertNotIn("TLS", env["problems"][0]["fix"])

        self.b.stand.probe_reachable = True
        self.b.stand.private_fail, self.b.stand.fail_attempts = 2, tls
        sid = self.start("--tasks", "S99")
        data = self.issue(sid)
        self.b.solve(data)
        code, env = self.submit(sid, data)
        self.assertEqual((code, self.codes(env)), (2, ["PRIVATE_PACK_TLS"]))
        issue = env["problems"][0]
        self.assertEqual(issue["args"], [env["next"]["line"]])
        self.assertEqual(issue["fix"], "%s Then run: %s" % (real_private.TLS_HINT, env["next"]["line"]))
        self.assertTrue(env["data"]["pending"])
        self.assertEqual(self.b.state(sid, data["attemptId"])["private"]["hint"], real_private.TLS_HINT)
        self.b.stand.fail_attempts = mixed
        code, env = self.submit(sid, data, "--retry-verify")
        self.assertEqual(self.codes(env), ["VERIFICATION_PENDING"])
        self.assertTrue(env["problems"][0]["fix"].endswith(" " + real_private.TLS_HINT))
        code, env = self.b.run("next", "--session", sid)
        self.assertEqual(self.codes(env), ["VERIFICATION_PENDING"])

    def test_panel_delegated_submission(self):
        sid = self.start("--tasks", "S99")
        data = self.issue(sid)
        result_file = os.path.join(self.b.tmp, "cmd.result.json")
        code, env = self.b.run("submit", "--session", sid, "--attempt", data["attemptId"], "--no-answer",
                               "--launcher", "panel", "--result-file", result_file)
        self.assertEqual((code, env), (0, None))
        with open(result_file, "r", encoding="ascii") as handle:
            panel = json.load(handle)
        self.assertEqual(panel["exitCode"], 0)
        self.assertEqual([n["code"] for n in panel["data"]["notices"]], ["USAGE_UNAVAILABLE"])
        result = self.b.result(sid, data["attemptId"])
        self.assertEqual((result["submittedBy"], result["answer"]["present"]), ("panel", False))


class ClassificationTests(Base):
    def finish(self, before_submit, *tasks):
        sid = self.start("--tasks", ",".join(tasks or ("S99",)))
        data = self.issue(sid)
        self.b.solve(data)
        before_submit(data)
        code, env = self.submit(sid, data)
        return sid, data, code, env, self.b.result(sid, data["attemptId"])

    def test_editor_side_change_contaminates(self):
        _sid, _data, code, _env, result = self.finish(lambda d: self.b.unity.editor_change("Main Camera"))
        self.assertEqual(code, 0)
        self.assertEqual((result["status"], result["reasons"][0]), ("contaminated", "non_rest_changes: 1"))
        self.assertEqual(result["evidence"]["journal"]["nonRest"], 1)

    def test_editor_side_import_under_A_is_not_contamination(self):
        def direct_write(data):
            asset = self.b.state(data["sessionId"], data["attemptId"])["scope"]["assetRoot"] + "/Crate.mat"
            self.b.unity.publish("file_changes", "editor", None, {"imported": [asset], "deleted": [], "moved": [],
                                                                  "count": 1, "omitted": 0})

        _sid, _data, _code, _env, result = self.finish(direct_write)
        self.assertEqual(result["status"], "pass", result["reasons"])
        self.assertEqual(result["evidence"]["journal"]["directWrites"], 1)

    def test_journal_gap_invalidates(self):
        def drop_last_line(_data):
            with open(self.b.unity.journal_path, "r", encoding="utf-8") as handle:
                lines = handle.readlines()
            with open(self.b.unity.journal_path, "w", encoding="utf-8") as handle:
                handle.writelines(lines[:-1] + [json.dumps({"seq": self.b.unity.seq + 1}) + "\n"])
            self.b.unity.seq += 1

        _sid, _data, _code, _env, result = self.finish(drop_last_line)
        self.assertEqual(result["status"], "invalid")
        self.assertRegex(result["reasons"][0], r"^journal_incomplete: gaps=\[\[\d+,\d+\]\]$")
        self.assertFalse(result["evidence"]["journal"]["complete"])

    def test_seq_reset_invalidates(self):
        def reset(_data):
            open(self.b.unity.journal_path, "w").close()
            self.b.unity.seq = 0

        _sid, _data, _code, _env, result = self.finish(reset)
        self.assertEqual((result["status"], result["reasons"][0]), ("invalid", "journal_incomplete: seq_reset"))

    def test_mode_drift_and_editor_restart(self):
        def drift(_data):
            self.b.unity.mode = "auto"
            self.b.write_registry(pid=os.getpid() + 100000)

        _sid, _data, _code, _env, result = self.finish(drift)
        self.assertEqual(result["status"], "invalid")
        self.assertEqual(result["reasons"][:2], ['environment_drift: {"currentMode":["bypass","auto"]}',
                                                 "editor_restarted"])
        self.assertEqual(result["evidence"]["drift"]["currentMode"], ["bypass", "auto"])

    def test_changed_runner_files_contaminate(self):
        tree = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, tree, True)
        with open(os.path.join(tree, "run_eval.py"), "w") as handle:
            handle.write("original\n")
        with mock.patch.object(attempt, "RUNNER_DIR", tree):
            def tamper(_data):
                with open(os.path.join(tree, "run_eval.py"), "w") as handle:
                    handle.write("edited by the agent\n")

            _sid, _data, _code, _env, result = self.finish(tamper)
        self.assertEqual((result["status"], result["reasons"][0]), ("contaminated", 'eval_files_changed: ["Evaluation~"]'))
        self.assertEqual(result["evidence"]["tampered"], ["Evaluation~"])

    def test_play_mode_is_stopped_and_flagged(self):
        def play(_data):
            self.b.unity.playing = True

        _sid, _data, _code, _env, result = self.finish(play)
        self.assertEqual(result["status"], "pass")
        self.assertIn("forced_play_mode_exit", result["reasons"])
        self.assertFalse(self.b.unity.playing)

    def test_teardown_failure_invalidates_and_cleanup_removes_the_residue(self):
        def break_delete(_data):
            self.b.unity.delete_fails = True

        sid, data, code, env, result = self.finish(break_delete)
        run_id = self.b.state(sid, data["attemptId"])["scope"]["runId"]
        self.assertEqual((code, self.codes(env)), (4, ["TEARDOWN_FAILED"]))
        self.assertEqual(result["status"], "invalid")
        self.assertTrue(result["reasons"][0].startswith("teardown_failed: "))
        self.assertTrue(os.path.isdir(self.run_dir(data)))
        self.b.unity.delete_fails = False
        code, env = self.b.run("check")
        self.assertEqual(code, 2)
        self.assertIn("RESIDUE_FOUND", self.codes(env))
        code, env = self.b.run("cleanup", "--stale", "--dry-run")
        self.assertEqual((code, len(env["data"]["removed"])), (0, 1))
        self.assertTrue(os.path.isdir(self.run_dir(data)))
        code, env = self.b.run("cleanup", "--stale")
        self.assertEqual(code, 0, env)
        self.assertEqual(env["data"]["removed"][0]["runId"], run_id)
        self.assertFalse(os.path.isdir(os.path.join(self.b.project, "Assets", "__UnitySkillsEval")))
        code, env = self.b.run("check")
        self.assertEqual(code, 0, env)

    def test_http_approximation_counts_only_agent_requests(self):
        sid = self.start("--tasks", "S99")
        data = self.issue(sid)
        self.b.solve(data)
        code, env = self.b.run("check")            # lock-free command during the attempt
        self.assertEqual(code, 0, env)
        code, env = self.submit(sid, data)
        self.assertEqual(code, 0, env)
        state = self.b.state(sid, data["attemptId"])
        self.assertGreater(state["http"]["runnerCalls"], 10)
        self.assertEqual(self.b.result(sid, data["attemptId"])["evidence"]["httpApprox"], 1)
        summary = self.b.result(sid, data["attemptId"])["evidence"]["telemetry"]
        self.assertEqual((summary["skillCalls"], summary["agents"]), (1, {"ClaudeCode": 1}))


class CrashRecoveryTests(Base):
    def test_crash_while_building_is_invalidated_and_rerun(self):
        sid = self.start("--tasks", "S99,S98")
        with mock.patch.object(fixture, "save_run_scene", crash):
            with self.assertRaises(Crash):
                self.b.run("next", "--session", sid)
        first = self.b.session(sid)["tasks"]["S99"]["attempts"][0]
        state = self.b.state(sid, first)
        self.assertEqual(state["phase"], "building")
        run_dir = self.b.unity.disk(state["scope"]["assetRoot"])
        self.assertTrue(os.path.isdir(run_dir))
        data = self.issue(sid)
        self.assertEqual((data["taskId"], data["rerunOf"], data["k"]), ("S99", first, 1))
        self.assertEqual([n["code"] for n in data["notices"]], ["RERUN"])
        old = self.b.state(sid, first)
        self.assertEqual((old["phase"], old["status"], old["reasons"]), ("torn_down", "invalid", ["build_interrupted"]))
        self.assertFalse(os.path.isdir(run_dir))
        self.assertEqual(self.b.session(sid)["tasks"]["S99"]["rerunsUsed"], 1)

    def test_build_failures_use_the_single_rerun(self):
        sid = self.start("--tasks", "S99,S98")
        task = self.b.stand.pack.load_task("S99")
        with mock.patch.object(task, "build", side_effect=ValueError("no props")):
            for _ in range(2):
                code, env = self.b.run("next", "--session", sid)
                self.assertEqual(code, 4)
                self.assertEqual(self.codes(env), ["FIXTURE_FAILED"])
        session = self.b.session(sid)
        self.assertEqual((session["tasks"]["S99"]["state"], len(session["tasks"]["S99"]["attempts"])), ("invalid", 2))
        result = self.b.result(sid, session["tasks"]["S99"]["attempts"][0])
        self.assertEqual(result["reasons"], ["build_failed: ValueError: no props"])
        self.assertEqual(self.issue(sid)["taskId"], "S98")
        live = self.b.state(sid, self.b.session(sid)["tasks"]["S98"]["attempts"][0])["scope"]["runId"]
        root = os.path.join(self.b.project, "Assets", "__UnitySkillsEval")
        self.assertEqual([n for n in os.listdir(root) if not n.endswith(".meta")], [live])

    def test_crash_after_s1_resumes_freezing(self):
        sid = self.start("--tasks", "S99")
        data = self.issue(sid)
        self.b.solve(data)
        with mock.patch.object(attempt, "_freeze", crash):
            with self.assertRaises(Crash):
                self.submit(sid, data)
        self.assertEqual(self.b.state(sid, data["attemptId"])["phase"], "submitted")
        code, env = self.b.run("next", "--session", sid)
        self.assertEqual((code, env["data"]["done"]), (0, True))
        self.assertEqual(self.b.result(sid, data["attemptId"])["status"], "pass")

    def test_unreachable_editor_during_the_freeze_keeps_the_submission(self):
        from usk_eval import cli
        for name, value in (("LOCATE_WAIT_S", 0.3), ("CLIENT_RETRY_S", 1.0)):
            old = getattr(cli, name)
            setattr(cli, name, value)
            self.addCleanup(setattr, cli, name, old)
        sid = self.start("--tasks", "S99")
        data = self.issue(sid)
        self.b.solve(data)
        self.b.unity.http_status = [503] * 1000
        code, env = self.submit(sid, data)
        self.assertEqual((code, self.codes(env)), (4, ["REST_UNREACHABLE"]))
        self.assertEqual(self.b.state(sid, data["attemptId"])["phase"], "submitted")
        self.b.unity.http_status = []
        code, env = self.submit(sid, data)
        self.assertEqual((code, env["data"]["alreadySubmitted"]), (0, False))
        self.assertEqual(self.b.result(sid, data["attemptId"])["status"], "pass")

    def test_crash_while_verifying_resumes_from_frozen_evidence(self):
        sid = self.start("--tasks", "S99")
        data = self.issue(sid)
        self.b.solve(data)
        with mock.patch.object(attempt, "_verify", crash):
            with self.assertRaises(Crash):
                self.submit(sid, data)
        self.assertEqual(self.b.state(sid, data["attemptId"])["phase"], "verifying")
        code, env = self.submit(sid, data)
        self.assertEqual((code, env["data"]["alreadySubmitted"]), (0, False))
        self.assertEqual(self.b.result(sid, data["attemptId"])["status"], "pass")

    def test_crash_before_teardown_is_finished_by_next(self):
        sid = self.start("--tasks", "S99")
        data = self.issue(sid)
        self.b.solve(data)
        with mock.patch.object(attempt, "_teardown", crash):
            with self.assertRaises(Crash):
                self.submit(sid, data)
        self.assertEqual(self.b.state(sid, data["attemptId"])["phase"], "scored")
        code, env = self.b.run("next", "--session", sid)
        self.assertEqual((code, env["data"]["done"]), (0, True))
        self.assertEqual(self.b.state(sid, data["attemptId"])["phase"], "torn_down")
        self.assertEqual(self.b.session(sid)["status"], "completed")
        rows = [r for r in store.read_jsonl(self.b.paths.ledger) if r["kind"] == "attempt"]
        self.assertEqual([r["attemptId"] for r in rows], [data["attemptId"]])


class ReloadingEditorTests(Base):
    """A listener shutting down for a domain reload can answer 200 with no body (seen right after editor_play)."""

    def setUp(self):
        Base.setUp(self)
        patcher = mock.patch.object(rest, "TRANSIENT_WAIT_S", 0.01)
        patcher.start()
        self.addCleanup(patcher.stop)

    def stops(self):
        return sum(1 for _method, path, _agent in self.b.unity.requests if path == "/skill/editor_stop")

    def test_an_empty_schema_answer_during_submit_is_retried(self):
        sid = self.start("--tasks", "S99")
        data = self.issue(sid)
        self.b.solve(data)
        self.b.unity.spoil["/skills/schema"] = ["empty"]
        code, env = self.submit(sid, data)
        self.assertEqual(code, 0, env)
        self.assertEqual(self.b.unity.spoil["/skills/schema"], [])
        self.assertEqual(self.b.result(sid, data["attemptId"])["status"], "pass")

    def test_a_cut_short_health_answer_while_locating_the_editor_is_retried(self):
        sid = self.start("--tasks", "S99")
        data = self.issue(sid)
        self.b.solve(data)
        self.b.unity.spoil["/health"] = ["empty", "cut"]
        code, env = self.submit(sid, data)
        self.assertEqual(code, 0, env)
        self.assertEqual(self.b.unity.spoil["/health"], [])
        self.assertEqual(self.b.result(sid, data["attemptId"])["status"], "pass")

    def test_an_editor_stop_that_did_not_run_is_asked_again(self):
        sid = self.start("--tasks", "S99")
        data = self.issue(sid)
        self.b.solve(data)
        self.b.unity.playing = True
        self.b.unity.spoil["/skill/editor_stop"] = ["empty"]
        code, env = self.submit(sid, data)
        self.assertEqual(code, 0, env)
        self.assertEqual((self.b.unity.playing, self.stops()), (False, 2))

    def test_an_editor_stop_that_ran_is_not_repeated(self):
        sid = self.start("--tasks", "S99")
        data = self.issue(sid)
        self.b.solve(data)
        self.b.unity.playing = True
        self.b.unity.spoil["/skill/editor_stop"] = ["cut"]
        code, env = self.submit(sid, data)
        self.assertEqual(code, 0, env)
        self.assertEqual((self.b.unity.playing, self.stops()), (False, 1))


class BusyEditorTests(Base):
    """start and next wait for a compiling, importing or reloading editor before EDITOR_BUSY; check reports it."""

    def set_constant(self, name, value):
        old = getattr(session, name)
        setattr(session, name, value)
        self.addCleanup(setattr, session, name, old)

    def compile_for(self, seconds):
        self.b.unity.compiling = True
        timer = threading.Timer(seconds, setattr, (self.b.unity, "compiling", False))
        timer.start()
        self.addCleanup(timer.cancel)

    def test_start_and_next_wait_for_a_compile_to_finish(self):
        self.set_constant("BUSY_WAIT_S", 30.0)
        self.compile_for(0.5)
        started = time.time()
        sid = self.start("--tasks", "S99")
        self.assertGreaterEqual(time.time() - started, 0.5)
        self.compile_for(0.5)
        started = time.time()
        self.assertEqual(self.issue(sid)["taskId"], "S99")
        self.assertGreaterEqual(time.time() - started, 0.5)

    def test_check_reports_a_compiling_editor_at_once(self):
        self.set_constant("BUSY_WAIT_S", 30.0)
        self.b.unity.compiling = True
        started = time.time()
        code, env = self.b.run("check", "--tasks", "S99")
        self.assertEqual((code, self.codes(env)), (2, ["EDITOR_BUSY"]))
        self.assertLess(time.time() - started, 10.0)

    def test_an_editor_still_busy_after_the_wait_is_reported(self):
        self.b.unity.compiling = True                              # the bench waits 0.3 s
        code, env = self.b.run("start", "--tasks", "S99")
        self.assertEqual((code, self.codes(env), env["data"]["created"]), (2, ["EDITOR_BUSY"], False))
        self.assertEqual(env["problems"][0]["fix"], "Wait until Unity finishes compiling and importing, then retry.")
        self.b.unity.compiling = False
        sid = self.start("--tasks", "S99")
        self.b.unity.compiling = True
        code, env = self.b.run("next", "--session", sid)
        self.assertEqual((code, self.codes(env)), (2, ["EDITOR_BUSY"]))
        self.assertEqual(self.b.session(sid)["tasks"]["S99"]["attempts"], [])

    def test_start_waits_for_a_reloading_editor_and_check_does_not(self):
        self.set_constant("BUSY_WAIT_S", 30.0)
        self.set_constant("CHECK_WAIT_S", 0.5)
        self.b.write_registry(status="reloading")
        code, env = self.b.run("check", "--tasks", "S99")
        self.assertEqual((code, self.codes(env)), (2, ["INSTANCE_NOT_RUNNING"]))
        timer = threading.Timer(2.5, self.b.write_registry)      # locate polls every second
        timer.start()
        self.addCleanup(timer.cancel)
        self.start("--tasks", "S99")

    def test_start_waits_while_health_is_refused_for_load(self):
        self.set_constant("BUSY_WAIT_S", 30.0)
        self.set_constant("CHECK_WAIT_S", 0.5)
        self.b.unity.http_status = [503] * 100000
        timer = threading.Timer(2.5, setattr, (self.b.unity, "http_status", []))
        timer.start()
        self.addCleanup(timer.cancel)
        self.start("--tasks", "S99")


class ImportAttributionTests(Base):
    def test_files_the_refresh_imported_at_submit_are_marked_and_explained(self):
        def verify(ctx, card):
            card.check("A1", True)
            bad = ctx.violations()
            card.check("A2", not bad, actual={"violations": bad[:10]})

        self.b.stand.verify = verify_module(verify)
        original = self.b.unity.call

        def call(name, args, agent):             # another tool's file, found by the runner's refresh at submit
            result = original(name, args, agent)
            if name == "asset_refresh" and agent == rest.RUNNER_AGENT:
                self.b.unity.publish("file_changes", "rest", agent, {
                    "imported": ["Packages/other/Tool.cs"], "deleted": [], "moved": [], "count": 1, "omitted": 0})
            return result

        self.b.unity.call = call
        sid = self.start("--tasks", "S99")
        data = self.issue(sid)
        self.b.solve(data)
        code, env = self.submit(sid, data)
        self.assertEqual(code, 0, env)
        result = self.b.result(sid, data["attemptId"])
        row = result["score"]["assertions"][1]
        self.assertEqual((row["id"], row["pass"]), ("A2", False))
        self.assertEqual([(v["assetPath"], v.get("importedAtSubmit")) for v in row["actual"]["violations"]],
                         [("Packages/other/Tool.cs", True)])
        self.assertEqual(row["hint"], attempt.IMPORT_HINT["en"])
        self.assertEqual(result["score"]["completion"], 60.0)
        report = store.read_json(os.path.join(self.b.paths.session_dir(sid), "report.json"))
        self.assertEqual(report["tasks"][0]["failed"], [{"id": "A2", "req": ["R2", "G1"],
                                                        "hint": attempt.IMPORT_HINT["en"]}])
        with open(os.path.join(self.b.paths.session_dir(sid), "report.md"), "r", encoding="utf-8") as handle:
            self.assertIn("- A2 (R2, G1): " + attempt.IMPORT_HINT["en"], handle.read())


class AbortTests(Base):
    def test_abort_cancels_the_ready_attempt(self):
        sid = self.start("--tasks", "S99,S98")
        data = self.issue(sid)
        self.b.solve(data)
        code, env = self.b.run("abort", "--session", sid, "--reason", "user stopped")
        self.assertEqual(code, 0, env)
        self.assertEqual(env["data"], {"sessionId": sid, "status": "aborted", "cancelledAttempt": data["attemptId"],
                                       "teardown": {"ok": True, "error": None}})
        result = self.b.result(sid, data["attemptId"])
        self.assertEqual((result["status"], result["reasons"], result["score"]),
                         ("cancelled", ["aborted: user stopped"], None))
        self.assertTrue(result["evidence"]["journal"]["complete"])
        session = self.b.session(sid)
        self.assertEqual((session["status"], session["abortReason"]), ("aborted", "user stopped"))
        self.assertEqual(session["tasks"]["S99"]["state"], "cancelled")
        self.assertTrue(os.path.isfile(os.path.join(self.b.paths.session_dir(sid), "report.json")))
        self.assertFalse(os.path.isdir(self.b.paths.io_session(sid)))
        self.assertFalse(os.path.isdir(self.run_dir(data)))
        code, again = self.b.run("abort", "--session", sid)
        self.assertEqual((code, again["data"]), (0, env["data"]))
        code, env = self.b.run("next", "--session", sid)
        self.assertEqual((code, self.codes(env)), (3, ["NO_ACTIVE_SESSION"]))
        code, env = self.b.run("report", "--session", sid)
        self.assertEqual((code, env["data"]["summary"]["cancelled"]), (0, 1))

    def test_abort_without_an_attempt(self):
        sid = self.start("--tasks", "S99")
        code, env = self.b.run("abort")
        self.assertEqual((code, env["data"]["cancelledAttempt"]), (0, None))
        self.assertEqual(self.b.session(sid)["abortReason"], "user")


class StrictModeTests(Base):
    def test_one_task_per_agent_session(self):
        sid = self.start("--tasks", "S99,S98", "--mode", "strict")
        data = self.issue(sid, CLAUDE_CODE_SESSION_ID="aaaa-1")
        self.assertEqual(self.b.state(sid, data["attemptId"])["host"]["isolation"], "verified")
        self.b.solve(data)
        code, env = self.submit(sid, data, "--answer", data["answerFile"], CLAUDE_CODE_SESSION_ID="aaaa-1")
        self.assertEqual(code, 0)
        self.assertEqual((env["data"]["stop"], env["data"]["nextCommand"], env["next"]), (True, None, None))
        code, env = self.b.run("next", "--session", sid, CLAUDE_CODE_SESSION_ID="aaaa-1")
        self.assertEqual((code, self.codes(env)), (3, ["STRICT_NEEDS_NEW_SESSION"]))
        second = self.issue(sid, CLAUDE_CODE_SESSION_ID="bbbb-2")
        self.assertEqual(second["taskId"], "S98")
        self.assertEqual(self.b.session(sid)["hostSessions"], {"aaaa-1": [data["attemptId"]],
                                                                "bbbb-2": [second["attemptId"]]})

    def test_unobservable_agent_session_is_flagged(self):
        sid = self.start("--tasks", "S99", "--mode", "strict")
        data = self.issue(sid)
        self.assertEqual([n["code"] for n in data["notices"]], ["ISOLATION_UNVERIFIED"])
        self.b.solve(data)
        self.submit(sid, data)
        result = self.b.result(sid, data["attemptId"])
        self.assertEqual((result["isolation"], result["reasons"]), ("unverified", ["isolation_unverified"]))


class CheckTests(Base):
    def test_every_problem_is_listed(self):
        unity = self.b.unity
        unity.compiling, unity.playing, unity.mode, unity.surface = True, True, "auto", "guide"
        unity.pipeline, unity.dry_run, unity.telemetry_enabled = "BuiltIn", "highRisk", False
        unity.version = "0.0.1"
        unity.scenes[0]["isDirty"] = True
        self.b.stand.probe_reachable = False
        self.b.stand.pack.manifest["tasks"]["S99"]["requires"].update({"dedicated": True,
                                                                        "packages": ["com.unity.cinemachine"]})
        os.makedirs(os.path.join(self.b.project, "Assets", "__UnitySkillsEval", "r20260101T000000abcdef"))
        code, env = self.b.run("check", "--tasks", "S99")
        self.assertEqual(code, 2)
        self.assertEqual(self.codes(env), ["VERSION_MISMATCH", "EDITOR_BUSY", "PLAY_MODE_ACTIVE", "MODE_NOT_BYPASS",
                                           "SURFACE_NOT_FULL", "PIPELINE_UNSUPPORTED", "SCENE_DIRTY",
                                           "PROJECT_NOT_DEDICATED", "PACKAGE_MISSING", "RESIDUE_FOUND",
                                           "PRIVATE_PACK_UNREACHABLE"])
        self.assertEqual(env["problems"][0]["args"], ["0.0.1", env["data"]["package"]["runnerVersion"]])
        self.assertEqual(env["problems"][8]["args"], ["com.unity.cinemachine", "S99"])
        notices = [n["code"] for n in env["data"]["notices"]]
        self.assertIn("SETTINGS_NONSTANDARD", notices)
        self.assertIn("TELEMETRY_DISABLED", notices)
        self.assertFalse(env["data"]["ready"])
        self.assertIsNone(env["next"])
        code, env = self.b.run("start", "--tasks", "S99")
        self.assertEqual((code, env["data"]["created"]), (2, False))
        self.assertFalse(os.path.exists(self.b.paths.current))

    def test_list_valued_pipeline_requirements(self):
        from usk_eval.session import allowed_pipelines
        self.assertIsNone(allowed_pipelines([{"pipeline": None}, {}]))
        self.assertEqual(allowed_pipelines([{"pipeline": ["URP", "BuiltIn"]}, {"pipeline": None}]), ["URP", "BuiltIn"])
        self.assertEqual(allowed_pipelines([{"pipeline": ["URP", "BuiltIn"]}, {"pipeline": "URP"}]), ["URP"])
        self.assertEqual(allowed_pipelines([{"pipeline": "HDRP"}, {"pipeline": "URP"}]), [])
        tasks = self.b.stand.pack.manifest["tasks"]
        tasks["S99"]["requires"]["pipeline"] = ["URP", "BuiltIn"]
        self.b.unity.pipeline = "BuiltIn"
        code, env = self.b.run("check", "--tasks", "S99")
        self.assertEqual(code, 0, env)
        self.b.unity.pipeline = "HDRP"
        code, env = self.b.run("check", "--tasks", "S99")
        self.assertEqual(self.codes(env), ["PIPELINE_UNSUPPORTED"])
        self.assertEqual(env["problems"][0]["args"], ["URP, BuiltIn"])
        self.assertEqual(env["problems"][0]["fix"],
                         "Use a project whose render pipeline is one the selected tasks support (URP, BuiltIn).")
        self.b.unity.pipeline = "BuiltIn"
        code, env = self.b.run("check", "--tasks", "S99,S98")                 # S98 keeps the default "URP"
        self.assertEqual((self.codes(env), env["problems"][0]["args"]), (["PIPELINE_UNSUPPORTED"], ["URP"]))
        sid = self.start("--tasks", "S99")                                    # next's N1 applies the same rule
        self.assertEqual(self.issue(sid)["taskId"], "S99")

    def test_selection_and_pack_range(self):
        code, env = self.b.run("check", "--tasks", "S01")
        self.assertEqual((code, self.codes(env)), (3, ["TASKS_INVALID"]))
        self.b.stand.pack.manifest["plugin"]["min"] = "99.0.0"
        code, env = self.b.run("check")
        self.assertEqual(self.codes(env), ["PACK_PLUGIN_RANGE"])
        self.assertEqual(env["problems"][0]["args"], ["99.0.0", "latest"])

    def test_marker_and_skill_docs(self):
        with open(self.b.paths.marker, "w", encoding="utf-8") as handle:
            json.dump({"schema": 1, "kind": "UnitySkillsEvaluationProject", "createdAt": store.now_iso(),
                       "createdWith": "3.0.0"}, handle)
        docs = os.path.join(self.b.home, ".claude", "skills", "unity-skills", "scripts")
        os.makedirs(docs)
        with open(os.path.join(docs, "agent_config.json"), "w") as handle:
            json.dump({"agentId": "claude-code", "version": "1.0.0", "installedAt": "x"}, handle)
        code, env = self.b.run("check")
        self.assertEqual(code, 0, env)
        project = env["data"]["project"]
        self.assertEqual((project["dedicated"], project["environment"]), (True, "dedicated"))
        self.assertEqual(len(project["markerSha256"]), 64)
        notices = {n["code"]: n for n in env["data"]["notices"]}
        self.assertNotIn("PROJECT_USER", notices)
        self.assertEqual(notices["SKILL_DOCS_OUTDATED"]["args"][1], "1.0.0")
        self.assertEqual(env["data"]["skillDocs"][0]["agentId"], "claude-code")

    def test_next_blocks_before_creating_an_attempt(self):
        sid = self.start("--tasks", "S99")
        self.b.unity.scenes[0]["isDirty"] = True
        code, env = self.b.run("next", "--session", sid)
        self.assertEqual((code, self.codes(env)), (2, ["SCENE_DIRTY"]))
        self.b.unity.scenes[0]["isDirty"] = False
        self.b.unity.compiling = True
        code, env = self.b.run("next", "--session", sid)
        self.assertEqual((code, self.codes(env)), (2, ["EDITOR_BUSY"]))
        self.assertEqual(self.b.session(sid)["tasks"]["S99"]["attempts"], [])


class UsageAndReportTests(Base):
    def test_manual_usage_and_report_summary(self):
        sid = self.start("--tasks", "S99,S98")
        first = self.issue(sid)
        self.b.solve(first)
        self.submit(sid, first)
        second = self.issue(sid)
        self.b.solve(second, correct=False)
        self.submit(sid, second)
        report_json = os.path.join(self.b.paths.session_dir(sid), "report.json")
        self.assertIsNone(store.read_json(report_json)["summary"]["usage"]["processed"])
        code, env = self.b.run("usage", "--session", sid, "--attempt", first["attemptId"], "--manual", "input=100",
                               "output=20", "model=claude-test")
        self.assertEqual(code, 0, env)
        report = store.read_json(report_json)                  # the ended session's report is regenerated
        self.assertEqual(report["summary"]["usage"]["processed"], 120)
        self.assertEqual(report["tasks"][0]["usage"]["source"], "user")
        with open(os.path.join(self.b.paths.session_dir(sid), "report.md"), "r", encoding="utf-8") as handle:
            self.assertIn("Tokens processed: 120", handle.read())
        self.assertEqual((env["data"]["usage"]["source"], env["data"]["usage"]["tokens"]["processed"]), ("user", 120))
        self.assertEqual(self.b.result(sid, first["attemptId"])["usage"]["source"], "user")
        rows = store.read_jsonl(self.b.paths.ledger)
        self.assertEqual([r["kind"] for r in rows], ["attempt", "attempt", "usage"])
        self.assertEqual(rows[0]["result"], "sessions/%s/attempts/%s/result.json" % (sid, first["attemptId"]))
        self.assertEqual(rows[-1]["usage"], {"source": "user", "processed": 120})
        code, env = self.b.run("usage", "--attempt", first["attemptId"], "--manual", "input=abc", "output=1")
        self.assertEqual((code, self.codes(env)), (3, ["BAD_ARGUMENTS"]))
        code, env = self.b.run("report", "--session", sid)
        summary = env["data"]["summary"]
        self.assertEqual((summary["pass"], summary["partial"], summary["strictPassRate"], summary["meanCompletion"]),
                         (1, 1, 0.5, 70.0))

    def claude_log(self, session_id, message_id, input_tokens):
        """A synthetic Claude Code session log line inside the attempt window (no real conversation content)."""
        time.sleep(0.01)
        folder = os.path.join(self.b.env["USK_EVAL_CLAUDE_ROOT"], "-work-proj")
        os.makedirs(folder, exist_ok=True)
        path = os.path.join(folder, session_id + ".jsonl")
        line = {"type": "assistant", "timestamp": store.now_iso(), "sessionId": session_id,
                "message": {"id": message_id, "model": "claude-test",
                            "content": [{"type": "text", "text": "SENTINEL-HOST-CONTENT"}],
                            "usage": {"input_tokens": input_tokens, "output_tokens": 5, "cache_read_input_tokens": 0,
                                      "cache_creation_input_tokens": 0}}}
        with open(path, "a", encoding="utf-8") as handle:
            handle.write(json.dumps(line) + "\n")
        return path

    def test_recompute_without_tokens_keeps_the_earlier_record(self):
        host = {"CLAUDE_CODE_SESSION_ID": "c1a2b3c4-0000-4000-8000-00000000abcd"}
        sid = self.start("--tasks", "S99,S98")
        first = self.issue(sid, **host)
        self.b.solve(first)
        log = self.claude_log(host["CLAUDE_CODE_SESSION_ID"], "msg_first", 100)
        code, _env = self.submit(sid, first, "--answer", first["answerFile"], **host)
        self.assertEqual(code, 0)
        usage = self.b.result(sid, first["attemptId"])["usage"]
        self.assertEqual((usage["source"], usage["tokens"]["input"], usage["tokens"]["processed"]),
                         ("claude-session-log", 100, 105))
        os.remove(log)                                         # the host log becomes unreadable afterwards
        code, env = self.b.run("usage", "--attempt", first["attemptId"], **host)
        self.assertEqual((code, env["data"]["usage"]["tokens"]["input"]), (0, 100))
        self.assertEqual(env["data"]["usage"]["errors"][-1], {"file": "", "line": 0, "error": "session_log_not_found"})
        self.assertEqual(env["data"]["notices"], [])
        second = self.issue(sid, **host)
        self.b.solve(second)
        self.submit(sid, second, "--answer", second["answerFile"], **host)      # the session ends: recompute
        self.assertEqual(self.b.session(sid)["status"], "completed")
        kept = self.b.result(sid, first["attemptId"])["usage"]
        self.assertEqual(kept["tokens"]["input"], 100)
        self.assertEqual([e["error"] for e in kept["errors"]].count("session_log_not_found"), 2)
        rows = [r for r in store.read_jsonl(self.b.paths.ledger) if r.get("attemptId") == first["attemptId"]]
        self.assertEqual([(r["kind"], r["usage"]["processed"]) for r in rows], [("attempt", 105)])
        for path in (self.b.paths.result_file(sid, first["attemptId"]), self.b.paths.ledger,
                     self.b.paths.session_log(sid), os.path.join(self.b.paths.session_dir(sid), "report.json")):
            with open(path, "r", encoding="utf-8") as handle:
                self.assertNotIn("SENTINEL-HOST-CONTENT", handle.read(), path)

    def test_status_never_carries_scores(self):
        sid = self.start("--tasks", "S99")
        data = self.issue(sid)
        code, env = self.b.run("status")
        self.assertEqual(code, 0)
        active = env["data"]["active"]
        self.assertEqual((active["attemptId"], active["phase"], active["minutes"]), (data["attemptId"], "ready", 5))
        self.b.solve(data)
        self.submit(sid, data)
        code, env = self.b.run("status", "--session", sid)
        self.assertEqual(env["data"]["status"], "completed")
        self.assertNotIn("score", json.dumps(env["data"]))


class StartOptionsTests(Base):
    def test_declared_seed_usage_switch_and_inline_prompt(self):
        declared = os.path.join(self.b.tmp, "declared.json")
        with open(declared, "w", encoding="utf-8") as handle:
            json.dump({"schema": 1, "host": {"value": "cursor", "source": "user"},
                       "model": {"value": "gpt-x", "source": "user"}}, handle)
        code, env = self.b.run("start", "--lang", "zh-CN", "--tasks", "S99", "--seed", "42", "--no-usage",
                               "--declared", declared, "--inline")
        self.assertEqual(code, 0, env)
        data = env["data"]
        self.assertFalse(data["official"])
        self.assertIn("整套模式", data["sessionPrompt"])
        session = self.b.session(data["sessionId"])
        self.assertEqual(session["declared"]["host"], {"value": "cursor", "source": "user"})
        self.assertEqual(session["declared"]["effort"], {"value": None, "source": "unknown"})
        self.assertEqual((session["selection"]["seedOverride"], session["usageReading"]), (42, False))
        issued = self.issue(data["sessionId"])
        self.assertEqual(self.b.state(data["sessionId"], issued["attemptId"])["seed"], 42)
        self.b.solve(issued)
        self.submit(data["sessionId"], issued)
        usage = self.b.result(data["sessionId"], issued["attemptId"])["usage"]
        self.assertEqual((usage["source"], usage["unavailableReason"]), ("none", "disabled_by_user"))

    def test_bad_declared_file(self):
        declared = os.path.join(self.b.tmp, "declared.json")
        with open(declared, "w", encoding="utf-8") as handle:
            json.dump({"host": {"value": "vim", "source": "user"}}, handle)
        code, env = self.b.run("start", "--declared", declared)
        self.assertEqual((code, self.codes(env)), (3, ["BAD_ARGUMENTS"]))


class CleanupTests(Base):
    RUN = "r20260101T000000abcdef"

    def residue(self, with_state=True, open_scene=True):
        scene = "Assets/__UnitySkillsEval/%s/Eval_%s.unity" % (self.RUN, self.RUN)
        os.makedirs(self.b.unity.disk("Assets/__UnitySkillsEval/" + self.RUN))
        with open(self.b.unity.disk(scene), "w") as handle:
            handle.write("%YAML 1.1\n")
        if open_scene:
            self.b.unity.scenes = [{"name": "Eval_" + self.RUN, "path": scene, "isLoaded": True, "isDirty": False,
                                    "isActive": True}]
        if with_state:
            store.atomic_write_json(self.b.paths.state_file("s20260101T000000aaaaaa", "0123456789ab"), {
                "schema": 1, "scope": {"runId": self.RUN},
                "resting": {"scenes": [{"path": "Assets/Scenes/Main.unity", "isActive": True, "isLoaded": True}]}})

    def test_cleanup_restores_the_resting_scene_first(self):
        self.residue()
        code, env = self.b.run("cleanup", "--stale")
        self.assertEqual(code, 0, env)
        self.assertEqual(env["data"]["restoredScenes"], ["Assets/Scenes/Main.unity"])
        self.assertEqual([r["runId"] for r in env["data"]["removed"]], [self.RUN])
        self.assertEqual(self.b.unity.active()["path"], "Assets/Scenes/Main.unity")
        self.assertFalse(os.path.isdir(os.path.join(self.b.project, "Assets", "__UnitySkillsEval")))

    def test_cleanup_without_a_resting_record_needs_a_scene(self):
        self.residue(with_state=False)
        code, env = self.b.run("cleanup", "--stale")
        self.assertEqual((code, self.codes(env)), (2, ["CLEANUP_NEEDS_SCENE"]))
        self.assertTrue(os.path.isdir(self.b.unity.disk("Assets/__UnitySkillsEval/" + self.RUN)))

    def test_cleanup_skips_foreign_folders_and_the_active_attempt(self):
        os.makedirs(self.b.unity.disk("Assets/__UnitySkillsEval/notarun"))
        sid = self.start("--tasks", "S99")
        data = self.issue(sid)
        code, env = self.b.run("cleanup", "--stale")
        self.assertEqual(code, 0, env)
        reasons = {item["runId"]: item["reason"] for item in env["data"]["skipped"]}
        self.assertEqual(reasons, {"notarun": "ownership",
                                   self.b.state(sid, data["attemptId"])["scope"]["runId"]: "active_attempt"})
        self.assertTrue(os.path.isdir(self.run_dir(data)))
        code, env = self.b.run("cleanup")
        self.assertEqual((code, self.codes(env)), (3, ["BAD_ARGUMENTS"]))


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
