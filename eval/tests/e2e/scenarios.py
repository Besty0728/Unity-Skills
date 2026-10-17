"""Scenarios for the v3 runner as an agent (or the panel) uses it, shared by the live E2E and its offline twin.

A scenario class mixes ScenarioMixin into a unittest.TestCase and sets `env` (a ScenarioEnv) in setUpClass:
  test_live.py     real editor (USK_EVAL_E2E_PROJECT), the runner as a subprocess, a runner copy that ships the test pack
  test_offline.py  the fake editor of eval/tests/runner/runner_support.py, the runner in-process

The scenarios use the two-task pack of e2e_pack.py (E01, E02) and the runner CLI exactly as the session prompt tells an
agent to; the work itself is done through REST as SelftestSolver by eval/harness/solver_driver.py.
"""
import datetime
import json
import os
import shutil
import sys
import tempfile
import threading
import time

HERE = os.path.dirname(os.path.abspath(__file__))
EVAL_DIR = os.path.dirname(os.path.dirname(HERE))
if EVAL_DIR not in sys.path:
    sys.path.insert(0, EVAL_DIR)

from harness import solver_driver as D  # noqa: E402
from usk_eval import fixture, store  # noqa: E402
from usk_eval.rest import RestError  # noqa: E402

from .e2e_pack import E2EPack, PrivateServer  # noqa: E402

SENTINEL = "ZZ_E2E_SENTINEL_CONTENT"
RELOAD_SCRIPT = ("namespace UnitySkillsE2E\n{\n    public static class ReloadProbe\n    {\n"
                 "        public const int Value = 1;\n    }\n}\n")
ACCEPTED_KEYS = {"accepted", "alreadySubmitted", "pending", "sessionId", "attemptId", "taskId", "remaining", "done",
                 "stop", "nextCommand", "notices"}
SCORE_WORDS = ('"completion"', '"assertions"', '"score"', '"strict"', '"criticalFailed"', '"requirementsFailed"')


# ---------------------------------------------------------------- environment of a scenario run


class ScenarioEnv(object):
    """Editor-independent plumbing: scratch directory, test pack, private server, runners and agents.

    Subclasses provide `project`, `inproc` and `entry`, and say what they support (can_kill, can_reload, line_runs).
    """

    project = None
    inproc = False
    entry = None
    can_kill = False
    can_spoil = False       # the editor can be told to answer a request with an empty or cut-short body
    can_reload = False
    line_runs = False       # the printed submit command can be run through a shell and reaches the same editor

    def __init__(self, tmp=None):
        self.tmp = tmp or tempfile.mkdtemp(prefix="usk-e2e-")
        self.pack = E2EPack.create(os.path.join(self.tmp, "pack-root"))
        self.private = PrivateServer(self.pack.private_dir)
        self.claude_root = os.path.join(self.tmp, "claude-logs")
        os.makedirs(self.claude_root)
        self.base_environ = D.clean_environ()

    def close(self):
        self.private.stop()
        shutil.rmtree(self.tmp, ignore_errors=True)

    # ------------------------------------------------------------------ runners and agents

    @property
    def pack_base_dir(self):
        """The directory the runner opens as its package pack."""
        return self.pack.pack_dir

    def runner(self, **env):
        merged = dict(self.base_environ)
        merged.update(self.private.environ)
        merged["USK_EVAL_CLAUDE_ROOT"] = self.claude_root
        return D.Runner(self.project, entry=self.entry, environ=self._environ(merged, env), inproc=self.inproc)

    @staticmethod
    def _environ(base, overrides):
        out = dict(base)
        for key, value in overrides.items():
            if value is None:
                out.pop(key, None)
            else:
                out[key] = str(value)
        return out

    def agent(self, **env):
        packs = D.PackAccess(base_dir=self.pack_base_dir, solutions_dir=self.pack.solutions_dir)
        return D.ScriptedAgent(self.runner(**env), packs)

    # ------------------------------------------------------------------ between tests

    def reset(self):
        """No session, no residue, a quiet editor, a healthy private server: the state every scenario starts from."""
        self.private.mode = "ok"
        agent = self.agent()
        agent.abort_quietly("e2e_reset")
        agent.runner.session = None
        agent.runner.run("cleanup", "--stale", session=False)
        self.wait_quiet(agent)

    def wait_quiet(self, agent):
        pass

    def scene_state(self, agent):
        """[(path, isActive)] of the loaded scenes: what the runner must hand back after a run."""
        return [(s.get("path"), bool(s.get("isActive"))) for s in fixture.loaded_scenes(agent.client)]

    def eval_assets(self):
        return os.path.join(self.project, "Assets", "__UnitySkillsEval")

    # ------------------------------------------------------------------ editor specifics

    def write_script(self, agent, attempt):
        """Writes a harmless C# file under the attempt's own asset root, straight to disk (no REST); returns its path."""
        state = agent.state.state(attempt.session_id, attempt.attempt_id)
        return self.write_script_in(state)

    def write_script_in(self, state):
        script = os.path.join(self.project, *state["scope"]["assetRoot"].split("/"), "E2EReloadProbe.cs")
        with open(script, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(RELOAD_SCRIPT)
        return script

    def reload_scripts(self, agent, attempt):
        """Make the editor recompile and reload its script domain while the attempt is open. Returns what was seen."""
        raise NotImplementedError

    def enter_play_mode(self, agent):
        """Leave the editor in Play Mode, the way an agent that forgot to stop its test run would."""
        raise NotImplementedError

    def spoil(self, answers):
        """{path: ["empty" | "cut" | "garbled" | "ok", ...]}: answer the next requests to each path that way (a listener
        that is shutting down for a domain reload answers 200 with no body); "ok" lets a request through."""
        raise NotImplementedError

    def spoil_left(self):
        """The spoiled answers not served yet, {path: [...]} without the empty lists."""
        raise NotImplementedError


class HealthWatcher(object):
    """Polls /health from a thread and remembers whether the editor was ever busy or silent."""

    def __init__(self, client, interval_s=0.1):
        self.client, self.interval_s = client, interval_s
        self.saw_silence = self.saw_compiling = False
        self.polls = 0
        self._stop = threading.Event()
        self._thread = threading.Thread(target=self._loop, daemon=True)

    def _loop(self):
        while not self._stop.is_set():
            health = self.client.probe_health(timeout=2.0)
            self.polls += 1
            if health is None:
                self.saw_silence = True
            elif (health.get("compilation") or {}).get("isCompiling") or health.get("domainReloadPending"):
                self.saw_compiling = True
            self._stop.wait(self.interval_s)

    def start(self):
        self._thread.start()
        return self

    def stop(self):
        self._stop.set()
        self._thread.join(timeout=10)

    @property
    def saw_reload(self):
        return self.saw_silence or self.saw_compiling


# ---------------------------------------------------------------- the scenarios


class ScenarioMixin(object):
    env = None

    def setUp(self):
        super(ScenarioMixin, self).setUp()
        self.env.reset()
        self.addCleanup(self.env.reset)

    # ------------------------------------------------------------------ helpers

    def begin(self, agent, tasks=("E01", "E02"), mode="suite", usage=False, extra=()):
        args = ["--track", "ops", "--tier", "basic", "--tasks", ",".join(tasks), "--mode", mode, "--lang", "en"]
        if not usage:
            args.append("--no-usage")
        result = agent.start(*(args + list(extra)))
        self.assertTrue(result.ok, result.describe())
        self.assertTrue(result.clean, "start wrote more than one JSON line: %r %r" % (result.stdout, result.stderr))
        return result

    def issue(self, agent):
        result, attempt = agent.next()
        self.assertTrue(result.ok and attempt is not None, result.describe())
        self.assertTrue(result.clean, "next wrote more than the envelope: %r %r" % (result.stdout, result.stderr))
        return result, attempt

    def solve(self, agent, attempt, how="reference"):
        """The work and the answer file of an issued attempt, as the agent would leave them before submitting."""
        answer = agent.packs.solutions(attempt.task_id).reference(agent.context(attempt))
        agent.write_answer(attempt, answer)
        return answer

    def solvers(self, agent):
        return {t: agent.packs.solutions(t).reference for t in agent.packs.ids()}

    def result_of(self, agent, attempt):
        return agent.state.result(attempt.session_id, attempt.attempt_id)

    def passed(self, agent, attempt):
        return self.passed_ids(agent, attempt.session_id, attempt.attempt_id, attempt.task_id)

    def passed_ids(self, agent, session_id, attempt_id, label):
        """The attempt's result, failing the test with the cause when it did not pass.

        An editor shared with other work shows up in the journal: another tool's file changes imported by the runner's
        refresh, edits made in the editor. The message names such records, so a red run points at the environment.
        """
        final = agent.state.result(session_id, attempt_id)
        if final["status"] != "pass":
            foreign = D.foreign_violations(agent.state, session_id, attempt_id)
            note = "" if not foreign else "\n  records its solver did not make (another tool used the editor?): " + \
                "; ".join(D.describe_record(r) for r in foreign[:5])
            self.fail("%s ended %s: %s%s" % (label, final["status"], final["reasons"], note))
        return final

    def all_state_text(self, agent):
        """Every runner-written file under Library/UnitySkills/eval, concatenated (privacy greps)."""
        parts = []
        for base, _dirs, files in os.walk(agent.state.paths.root):
            for name in files:
                try:
                    with open(os.path.join(base, name), "r", encoding="utf-8", errors="replace") as handle:
                        parts.append(handle.read())
                except OSError:
                    pass
        return "\n".join(parts)

    def assertClean(self, agent, scenes_before):
        self.assertEqual(agent.state.residue(), [], "run folders left behind")
        self.assertFalse(os.path.exists(self.env.eval_assets()), "Assets/__UnitySkillsEval was not removed")
        self.assertEqual(self.env.scene_state(agent), scenes_before, "the resting scenes were not restored")
        self.assertFalse(os.path.exists(agent.state.paths.io_root), "the IO directory was not removed")

    # ------------------------------------------------------------------ suite mode

    def test_suite_mode_runs_every_task_and_never_leaks_a_score(self):
        env = self.env
        agent = env.agent()
        scenes = env.scene_state(agent)
        started = self.begin(agent)
        sid = started.data["sessionId"]
        self.assertTrue(started.data["created"])
        self.assertEqual((started.data["n"], started.data["mode"]), (2, "suite"))
        with open(started.data["sessionPromptFile"], "r", encoding="utf-8-sig") as handle:
            session_prompt = handle.read()
        self.assertIn("suite mode", session_prompt)
        self.assertIn(started.next["line"], session_prompt)
        again = self.begin(agent)
        self.assertEqual((again.data["created"], again.data["sessionId"]), (False, sid))

        first, attempt = self.issue(agent)
        self.assertEqual((attempt.task_id, attempt.k, attempt.n, attempt.reissued), ("E01", 1, 2, False))
        state = agent.state.state(sid, attempt.attempt_id)
        prompt = attempt.prompt_text()
        for needle in ("G1", "G8", state["scope"]["objectRoot"], state["scope"]["assetRoot"], "Crate"):
            self.assertIn(needle, prompt)
        self.assertIn(first.data["submitCommand"], prompt)
        reissued = agent.runner.run("next")
        self.assertTrue(reissued.ok and reissued.data["reissued"], reissued.describe())
        self.assertEqual(reissued.data["attemptId"], attempt.attempt_id)

        self.solve(agent, attempt)
        if env.line_runs:
            submitted = agent.runner.run_line(first.data["submitCommand"])
        else:
            submitted = agent.submit(attempt)
        self.assertTrue(submitted.ok and submitted.clean, submitted.describe())
        self.assertEqual(set(submitted.data) - ACCEPTED_KEYS, set(), "submit answered with more than it may")
        self.assertEqual((submitted.data["remaining"], submitted.data["done"], submitted.data["stop"]), (1, False, False))
        self.assertTrue(submitted.data["nextCommand"])
        status = agent.runner.run("status")
        self.assertTrue(status.ok and status.clean, status.describe())
        self.assertEqual([t["state"] for t in status.data["tasks"]], ["done", "pending"])
        for text in (json.dumps(submitted.envelope), json.dumps(status.envelope)):
            for word in SCORE_WORDS:
                self.assertNotIn(word, text, "a score reached the agent")
            self.assertNotIn('"status":"pass"', text.replace(" ", ""))

        runs = agent.run_session(self.solvers(agent))
        self.assertEqual([r.attempt.task_id for r in runs[:1]], ["E02"])
        self.assertTrue(runs[0].submit_result.ok and runs[0].submit_result.data["done"], runs[0].submit_result.describe())
        self.assertTrue(runs[-1].done, "the loop did not end with done")
        self.assertEqual(runs[-1].next_result.data["completed"], 2)

        done = agent.runner.run("status")
        self.assertEqual((done.data["status"], done.data["completed"], done.data["remaining"]), ("completed", 2, 0))
        results = [self.passed_ids(agent, sid, a, task["taskId"]) for task in done.data["tasks"]
                   for a in task["attempts"]]
        self.assertEqual({r["submittedBy"] for r in results}, {"agent"})
        self.assertTrue(all(r["evidence"]["journal"]["complete"] for r in results))
        self.assertIn("SelftestSolver", results[0]["evidence"]["journal"]["agents"])
        duplicate = agent.submit(attempt)
        self.assertTrue(duplicate.ok and duplicate.data["alreadySubmitted"], duplicate.describe())
        report = agent.runner.run("report", "--format", "md")
        self.assertTrue(report.ok, report.describe())
        self.assertEqual((report.data["summary"]["pass"], report.data["summary"]["tasks"]), (2, 2))
        self.assertIn("E02", report.data["markdown"])
        ledger = [row for row in agent.state.ledger() if row.get("kind") == "attempt" and row["sessionId"] == sid]
        self.assertEqual([(row["taskId"], row["status"], row["launcher"]) for row in ledger],
                         [("E01", "pass", "agent"), ("E02", "pass", "agent")])
        self.assertClean(agent, scenes)

    # ------------------------------------------------------------------ strict mode

    def test_strict_mode_gives_each_host_session_one_task(self):
        env = self.env
        a = env.agent(CLAUDECODE="1", CLAUDE_CODE_SESSION_ID="e2e-host-a")
        scenes = env.scene_state(a)
        started = self.begin(a, mode="strict")
        sid = started.data["sessionId"]
        with open(started.data["sessionPromptFile"], "r", encoding="utf-8-sig") as handle:
            self.assertIn("strict mode", handle.read())
        first = a.run_attempt(self.solvers(a))
        self.assertTrue(first.submit_result.ok, first.submit_result.describe())
        self.assertTrue(first.submit_result.data["stop"])
        self.assertIsNone(first.submit_result.data["nextCommand"])
        self.assertEqual(first.submit_result.data["remaining"], 1)

        refused = a.runner.run("next")
        self.assertEqual((refused.code, refused.codes), (3, ["STRICT_NEEDS_NEW_SESSION"]), refused.describe())
        self.assertIsNone(a.state.current()["activeAttemptId"], "a refused next must not build a fixture")

        b = env.agent(CLAUDECODE="1", CLAUDE_CODE_SESSION_ID="e2e-host-b")
        b.runner.session = sid
        second = b.run_attempt(self.solvers(b))
        self.assertEqual((second.attempt.task_id, second.attempt.k), ("E02", 2))
        self.assertTrue(second.submit_result.ok and second.submit_result.data["done"], second.submit_result.describe())
        results = [self.passed(a, first.attempt), self.passed(a, second.attempt)]
        self.assertEqual({r["isolation"] for r in results}, {"verified"})
        self.assertEqual(sorted(a.state.session(sid)["hostSessions"]), ["e2e-host-a", "e2e-host-b"])
        self.assertClean(a, scenes)

    def test_strict_mode_without_a_host_session_is_flagged_not_blocked(self):
        agent = self.env.agent()
        self.begin(agent, tasks=("E01",), mode="strict")
        result, attempt = self.issue(agent)
        self.assertIn("ISOLATION_UNVERIFIED", result.notice_codes)
        self.solve(agent, attempt)
        self.assertTrue(agent.submit(attempt).ok)
        final = self.passed(agent, attempt)
        self.assertEqual(final["isolation"], "unverified")
        self.assertIn("isolation_unverified", final["reasons"])

    # ------------------------------------------------------------------ abort

    def test_abort_cancels_the_attempt_and_hands_the_editor_back(self):
        env = self.env
        agent = env.agent()
        scenes = env.scene_state(agent)
        self.begin(agent)
        result, attempt = self.issue(agent)
        state = agent.state.state(attempt.session_id, attempt.attempt_id)
        run_folder = os.path.join(env.project, *state["scope"]["assetRoot"].split("/"))
        self.assertTrue(os.path.isdir(run_folder))
        self.solve(agent, attempt)                              # leaves work in the run scene

        aborted = agent.abort("e2e-abort")
        self.assertTrue(aborted.ok and aborted.clean, aborted.describe())
        self.assertEqual((aborted.data["status"], aborted.data["cancelledAttempt"]), ("aborted", attempt.attempt_id))
        self.assertTrue(aborted.data["teardown"]["ok"], aborted.data)
        self.assertFalse(os.path.exists(run_folder))
        final = self.result_of(agent, attempt)
        self.assertEqual((final["status"], final["reasons"][0], final["score"]), ("cancelled", "aborted: e2e-abort", None))
        session = agent.state.session(attempt.session_id)
        self.assertEqual((session["status"], session["abortReason"]), ("aborted", "e2e-abort"))
        self.assertClean(agent, scenes)

        again = agent.abort("e2e-abort")
        self.assertTrue(again.ok, again.describe())
        self.assertEqual(again.data["cancelledAttempt"], attempt.attempt_id)
        after = agent.runner.run("next")
        self.assertEqual((after.code, after.codes), (3, ["NO_ACTIVE_SESSION"]))
        report = agent.runner.run("report")
        self.assertTrue(report.ok, report.describe())
        self.assertEqual(report.data["summary"]["cancelled"], 1)

    # ------------------------------------------------------------------ the runner dies while building

    def kill_during_build(self, ready, what):
        env = self.env
        if not env.can_kill:
            self.skipTest("needs a runner subprocess")
        agent = env.agent()
        scenes = env.scene_state(agent)
        started = self.begin(agent, tasks=("E01",))
        sid = started.data["sessionId"]
        proc = agent.runner.popen("next")
        try:
            state = agent.state.wait_state(ready, timeout_s=120.0, interval_s=0.005)
            self.assertIsNotNone(state, "never saw the attempt %s" % what)
        finally:
            D.kill_process(proc)
        first = state["attemptId"]
        self.assertEqual(agent.state.state(sid, first)["phase"], "building", "the kill came too late")
        self.assertTrue(os.path.exists(agent.state.paths.lock), "the killed runner should have left its lock behind")

        result, attempt = self.issue(agent)
        self.assertEqual(attempt.rerun_of, first)
        self.assertIn("RERUN", result.notice_codes)
        broken = agent.state.result(sid, first)
        self.assertEqual((broken["status"], broken["reasons"]), ("invalid", ["build_interrupted"]))
        self.assertEqual(agent.state.state(sid, first)["phase"], "torn_down")
        self.solve(agent, attempt)
        submitted = agent.submit(attempt)
        self.assertTrue(submitted.ok and submitted.data["done"], submitted.describe())
        self.passed(agent, attempt)
        self.assertEqual(agent.state.session(sid)["tasks"]["E01"]["rerunsUsed"], 1)
        self.assertClean(agent, scenes)

    def test_runner_killed_at_the_start_of_the_build_is_recovered(self):
        self.kill_during_build(lambda s: s["phase"] == "building", "in the building phase")

    def test_runner_killed_with_the_built_scene_unsaved_is_recovered(self):
        self.kill_during_build(lambda s: s["phase"] == "building" and bool(s["handles"]),
                               "with its objects built and the run scene unsaved")

    def kill_while_submitting(self, phase, recover):
        """SIGKILL the runner while its submit is in `phase`, then finish the attempt with `next` or a second submit."""
        env = self.env
        if not env.can_kill:
            self.skipTest("needs a runner subprocess")
        agent = env.agent()
        scenes = env.scene_state(agent)
        self.begin(agent, tasks=("E01",))
        result, attempt = self.issue(agent)
        self.solve(agent, attempt)
        proc = agent.runner.popen("submit", "--attempt", attempt.attempt_id, "--answer", attempt.answer_file)
        try:
            state = agent.state.wait_phase(phase, timeout_s=300.0, interval_s=0.005)
            self.assertIsNotNone(state, "never saw the attempt in the %s phase" % phase)
        finally:
            D.kill_process(proc)
        self.assertIn(agent.state.state(attempt.session_id, attempt.attempt_id)["phase"],
                      ("submitted", "verifying", "scored", "torn_down"))
        if recover == "next":
            resumed = agent.runner.run("next")
            self.assertTrue(resumed.ok and resumed.clean, resumed.describe())
            self.assertTrue(resumed.data["done"], resumed.data)
        else:
            resumed = agent.submit(attempt)
            self.assertTrue(resumed.ok and resumed.clean, resumed.describe())
            self.assertTrue(resumed.data["accepted"] and resumed.data["done"], resumed.data)
        final = self.passed(agent, attempt)
        self.assertEqual(final["teardown"]["ok"], True)
        self.assertEqual(agent.state.state(attempt.session_id, attempt.attempt_id)["phase"], "torn_down")
        self.assertEqual(agent.state.session(attempt.session_id)["status"], "completed")
        self.assertClean(agent, scenes)

    def test_runner_killed_while_freezing_is_finished_by_a_second_submit(self):
        self.kill_while_submitting("submitted", "submit")

    def test_runner_killed_while_verifying_is_finished_by_next(self):
        self.kill_while_submitting("verifying", "next")

    def test_runner_killed_before_teardown_is_finished_by_next(self):
        self.kill_while_submitting("scored", "next")

    # ------------------------------------------------------------------ the panel submits for the user

    def test_panel_submission_without_an_answer(self):
        env = self.env
        agent = env.agent()
        self.begin(agent, tasks=("E01",))
        result, attempt = self.issue(agent)
        panel = os.path.join(env.tmp, "panel")
        os.makedirs(panel, exist_ok=True)
        files = {"result_file": os.path.join(panel, "cmd-1.result.json"), "log_file": os.path.join(panel, "cmd-1.log")}

        bad = agent.runner.run_panel("submit", result_file=os.path.join(panel, "cmd-0.result.json"),
                                     log_file=files["log_file"], cmd_id="cmd-0")
        self.assertEqual((bad.code, bad.codes, bad.stdout), (3, ["BAD_ARGUMENTS"], ""))
        self.assertEqual((bad.envelope["exitCode"], bad.envelope["cmdId"]), (3, "cmd-0"))

        sent = agent.submit(attempt, "no-answer", panel=True, cmd_id="cmd-1", **files)
        self.assertEqual(sent.stdout.strip(), "", "a panel run must not write to stdout")
        self.assertTrue(sent.ok, sent.describe())
        self.assertEqual((sent.envelope["exitCode"], sent.envelope["cmdId"]), (0, "cmd-1"))
        self.assertTrue(sent.envelope["finishedAt"].endswith("Z"))
        self.assertTrue(sent.data["accepted"] and sent.data["done"])
        final = self.result_of(agent, attempt)
        self.assertEqual((final["submittedBy"], final["answer"]["present"]), ("panel", False))
        self.assertIn(final["status"], ("partial", "fail"))
        self.assertIsNone(final["declaredComplete"])
        with open(files["log_file"], "r", encoding="utf-8") as handle:
            self.assertIn("submitted by panel", handle.read())
        done = agent.runner.run("status")
        self.assertEqual(done.data["status"], "completed")

    # ------------------------------------------------------------------ the verifiers cannot be downloaded

    def test_pending_verification_survives_until_the_download_works(self):
        env = self.env
        agent = env.agent()
        scenes = env.scene_state(agent)
        started = self.begin(agent, tasks=("E01",))
        sid = started.data["sessionId"]
        self.assertFalse(started.data["official"], "the URL override must make the session unofficial")
        result, attempt = self.issue(agent)
        self.solve(agent, attempt)
        state = agent.state.state(sid, attempt.attempt_id)
        run_folder = os.path.join(env.project, *state["scope"]["assetRoot"].split("/"))

        env.private.mode = "not_found"
        pending = agent.submit(attempt)
        self.assertEqual((pending.code, pending.codes), (2, ["VERIFICATION_PENDING"]), pending.describe())
        self.assertTrue(pending.data["accepted"] and pending.data["pending"])
        retry_line = pending.problems[0]["args"][0]
        self.assertIn("--retry-verify", retry_line)
        self.assertEqual(pending.next["line"], retry_line)
        self.assertIsNone(pending.data["nextCommand"])
        self.assertEqual(agent.runner.run("status").data["active"]["phase"], "pending_verification")
        self.assertTrue(os.path.isdir(run_folder), "the fixture must stay while verification is pending")
        self.assertEqual(agent.state.state(sid, attempt.attempt_id)["private"]["attempts"][0]["error"], "http 404")

        blocked = agent.runner.run("next")
        self.assertEqual((blocked.code, blocked.codes), (2, ["VERIFICATION_PENDING"]))
        self.assertIsNone(agent.state.result(sid, attempt.attempt_id), "nothing is scored while verification is pending")
        still = agent.submit(attempt, "retry")
        self.assertEqual((still.code, still.codes), (2, ["VERIFICATION_PENDING"]), still.describe())
        wrong = agent.runner.run("submit", "--attempt", "000000000000", "--retry-verify")
        self.assertEqual(wrong.codes, ["ATTEMPT_NOT_FOUND"])

        env.private.mode = "ok"
        fixed = agent.submit(attempt, "retry")
        self.assertTrue(fixed.ok and fixed.data["accepted"] and not fixed.data["pending"], fixed.describe())
        self.assertTrue(fixed.data["done"])
        final = self.passed(agent, attempt)
        self.assertTrue(final["verifiedLate"])
        self.assertIn("verified_late", final["reasons"])
        self.assertEqual((final["verifier"]["kind"], final["verifier"]["deleted"]), ("url", True))
        self.assertClean(agent, scenes)

    def test_pending_attempt_can_be_verified_from_a_local_folder(self):
        env = self.env
        agent = env.agent()
        self.begin(agent, tasks=("E01",))
        result, attempt = self.issue(agent)
        self.solve(agent, attempt)
        env.private.mode = "error"
        pending = agent.submit(attempt)
        self.assertEqual(pending.codes, ["VERIFICATION_PENDING"], pending.describe())
        self.assertEqual(agent.state.state(attempt.session_id, attempt.attempt_id)["private"]["attempts"][0]["error"],
                         "http 500")
        fixed = agent.submit(attempt, "retry", extra=["--private-dir", env.pack.private_dir])
        self.assertTrue(fixed.ok, fixed.describe())
        final = self.passed(agent, attempt)
        self.assertEqual(final["verifier"]["kind"], "private-dir")

    # ------------------------------------------------------------------ usage from a host log

    def test_usage_is_read_from_a_synthetic_claude_log(self):
        env = self.env
        host = "e2e-host-%s" % store.new_attempt_id()
        agent = env.agent(CLAUDECODE="1", CLAUDE_CODE_SESSION_ID=host)
        started = self.begin(agent, tasks=("E01",), usage=True)
        sid = started.data["sessionId"]
        result, attempt = self.issue(agent)
        t0 = agent.state.state(sid, attempt.attempt_id)["issuedAt"]
        before = store.iso(store.parse_utc(t0) - datetime.timedelta(seconds=120))
        time.sleep(0.1)
        now = store.now_iso()
        log = os.path.join(env.claude_root, "-e2e-project", host + ".jsonl")
        os.makedirs(os.path.dirname(log), exist_ok=True)
        lines = [
            self.claude_line("m0", before, 9999, 9999),                        # before the window
            self.claude_line("m1", now, 100, 10, 1000, 200),                   # an earlier part of m1 ...
            self.claude_line("m1", now, 100, 50, 1000, 200),                   # ... the last part of m1 wins
            self.claude_line("m2", now, 7, 3, 5, 0),
            self.claude_line("m3", now, 5, 5, 5, 5, model="<synthetic>"),     # Claude Code's placeholder for API errors
            '{"type": "assistant", "message": %s broken' % SENTINEL,
        ]
        with open(log, "w", encoding="utf-8") as handle:
            for line in lines:
                handle.write((line if isinstance(line, str) else json.dumps(line)) + "\n")
        self.solve(agent, attempt)
        submitted = agent.submit(attempt)
        self.assertTrue(submitted.ok and submitted.data["done"], submitted.describe())

        usage = self.result_of(agent, attempt)["usage"]
        self.assertEqual((usage["host"], usage["source"], usage["sessionIds"]), ("claude-code", "claude-session-log", [host]))
        self.assertIsNone(usage["unavailableReason"])
        self.assertEqual(usage["tokens"], {"input": 107, "output": 53, "cacheRead": 1005, "cacheWrite": 200,
                                          "reasoning": 0, "processed": 1365})
        self.assertEqual(usage["messages"], 2)
        self.assertIn("claude-e2e-model", usage["model"]["values"])
        self.assertEqual([e["error"] for e in usage["errors"]], ["JSONDecodeError"])
        ledger = [row for row in agent.state.ledger() if row.get("attemptId") == attempt.attempt_id]
        self.assertEqual(ledger[-1]["usage"], {"source": "claude-session-log", "processed": 1365})
        self.assertEqual(agent.state.report(sid)["summary"]["usage"]["processed"], 1365)
        self.assertNotIn(SENTINEL, self.all_state_text(agent), "host log content reached the runner's files")

        manual = agent.runner.run("usage", "--attempt", attempt.attempt_id, "--manual", "input=10", "output=20",
                                  "model=typed-by-hand")
        self.assertTrue(manual.ok, manual.describe())
        self.assertEqual((manual.data["usage"]["source"], manual.data["usage"]["tokens"]["processed"]), ("user", 30))
        self.assertEqual(agent.state.report(sid)["summary"]["usage"]["processed"], 30)

    @staticmethod
    def claude_line(mid, ts, inp, out, cache_read=0, cache_write=0, model="claude-e2e-model"):
        return {"type": "assistant", "timestamp": ts, "requestedModel": model, "version": "2.1.0", "effort": "high",
                "message": {"id": mid, "model": model, "role": "assistant",
                            "content": [{"type": "text", "text": SENTINEL}],
                            "usage": {"input_tokens": inp, "output_tokens": out,
                                      "cache_read_input_tokens": cache_read,
                                      "cache_creation_input_tokens": cache_write,
                                      "output_tokens_details": {"thinking_tokens": 0}}}}

    def test_abort_while_verification_is_pending_tears_the_fixture_down(self):
        env = self.env
        agent = env.agent()
        scenes = env.scene_state(agent)
        self.begin(agent, tasks=("E01",))
        result, attempt = self.issue(agent)
        self.solve(agent, attempt)
        env.private.mode = "not_found"
        self.assertEqual(agent.submit(attempt).codes, ["VERIFICATION_PENDING"])
        aborted = agent.abort("e2e-pending")
        self.assertTrue(aborted.ok, aborted.describe())
        self.assertEqual((aborted.data["cancelledAttempt"], aborted.data["teardown"]["ok"]), (attempt.attempt_id, True))
        self.assertEqual(self.result_of(agent, attempt)["status"], "cancelled")
        self.assertClean(agent, scenes)

    def test_runner_rides_through_empty_cut_and_garbled_answers(self):
        env = self.env
        if not env.can_spoil:
            self.skipTest("needs the fake editor")
        agent = env.agent()
        scenes = env.scene_state(agent)
        self.begin(agent, tasks=("E01",))
        result, attempt = self.issue(agent)
        self.solve(agent, attempt)
        # the first /health is the runner locating the editor, the later ones come from its own client
        env.spoil({"/skills/schema": ["empty"], "/health": ["empty", "cut", "garbled"],
                   "/skill/asset_refresh": ["empty"]})
        submitted = agent.submit(attempt)
        self.assertTrue(submitted.ok and submitted.data["done"], submitted.describe())
        self.assertEqual(env.spoil_left(), {}, "some of the bad answers were never served")
        self.passed(agent, attempt)
        self.assertClean(agent, scenes)

    # ------------------------------------------------------------------ two commands at once

    def test_concurrent_submits_are_serialised_and_the_second_is_a_duplicate(self):
        env = self.env
        if not env.can_kill:
            self.skipTest("needs runner subprocesses")
        agent = env.agent()
        self.begin(agent, tasks=("E01",))
        result, attempt = self.issue(agent)
        self.solve(agent, attempt)
        args = ["--attempt", attempt.attempt_id, "--answer", attempt.answer_file]
        procs = [agent.runner.popen("submit", *args), agent.runner.popen("submit", *args)]
        try:
            status = agent.runner.run("status")             # reads files only: never waits for the runner's lock
            self.assertTrue(status.ok, status.describe())
            outs = [D.parse_envelope(p.communicate(timeout=1800)[0].decode("utf-8"))[0] for p in procs]
        finally:
            for proc in procs:
                if proc.poll() is None:
                    D.kill_process(proc)
        self.assertTrue(all(o and o["ok"] for o in outs), outs)
        self.assertEqual(sorted(o["data"]["alreadySubmitted"] for o in outs), [False, True])
        state = agent.state.state(attempt.session_id, attempt.attempt_id)
        self.assertEqual(state["duplicateSubmits"], 1)
        self.passed(agent, attempt)

    # ------------------------------------------------------------------ check, cleanup, play mode

    def test_check_reports_what_it_found(self):
        env = self.env
        agent = env.agent()
        checked = agent.check("--track", "ops", "--tier", "basic")
        self.assertTrue(checked.ok and checked.clean, checked.describe())
        data = checked.data
        self.assertTrue(data["ready"])
        self.assertEqual((data["settings"]["mode"], data["settings"]["surface"]), ("bypass", "full"))
        self.assertEqual(data["selection"]["tasks"], ["E01", "E02"])
        self.assertEqual(data["pack"]["id"], "usk-e2e-pack")
        self.assertEqual((data["privatePack"]["reachable"], data["privatePack"]["probedTask"]), (True, "E01"))
        self.assertEqual(data["instance"]["pluginVersion"], data["package"]["runnerVersion"])
        self.assertTrue(data["scenes"]["loaded"] and not data["scenes"]["dirty"])
        self.assertEqual((data["session"]["active"], data["residue"]), (False, []))
        self.assertEqual(checked.next["command"], "start")
        env.private.mode = "not_found"
        unreachable = agent.check("--track", "ops", "--tier", "basic")
        self.assertEqual((unreachable.code, unreachable.codes), (2, ["PRIVATE_PACK_UNREACHABLE"]))
        self.assertFalse(unreachable.data["ready"])
        self.assertIsNone(unreachable.next)
        self.assertEqual(unreachable.data["instance"]["id"], data["instance"]["id"], "check lists everything it found")

    def test_cleanup_removes_leftover_run_folders_and_nothing_else(self):
        env = self.env
        agent = env.agent()
        scenes = env.scene_state(agent)
        client = agent.client
        stale = "Assets/__UnitySkillsEval/r20250101T000000abcdef"
        foreign = "Assets/__UnitySkillsEval/NotARunFolder"
        client.write("asset_create_folder", folderPath="Assets/__UnitySkillsEval")
        client.write("asset_create_folder", folderPath=stale)
        client.write("asset_create_folder", folderPath=foreign)
        try:
            blocked = agent.start("--track", "ops", "--tier", "basic", "--tasks", "E01", "--no-usage")
            self.assertEqual((blocked.code, blocked.codes), (2, ["RESIDUE_FOUND"]), blocked.describe())
            self.assertIn("r20250101T000000abcdef", blocked.problems[0]["fix"])
            dry = agent.runner.run("cleanup", "--stale", "--dry-run", session=False)
            self.assertEqual([r["runId"] for r in dry.data["removed"]], ["r20250101T000000abcdef"])
            self.assertTrue(os.path.isdir(os.path.join(env.project, *stale.split("/"))))
            cleaned = agent.runner.run("cleanup", "--stale", session=False)
            self.assertTrue(cleaned.ok and cleaned.clean, cleaned.describe())
            self.assertEqual([r["runId"] for r in cleaned.data["removed"]], ["r20250101T000000abcdef"])
            self.assertEqual(cleaned.data["skipped"], [{"runId": "NotARunFolder", "reason": "ownership"}])
            self.assertFalse(os.path.exists(os.path.join(env.project, *stale.split("/"))))
            self.assertTrue(os.path.isdir(os.path.join(env.project, *foreign.split("/"))), "foreign folder was touched")
            started = self.begin(agent, tasks=("E01",))
            self.assertTrue(started.data["created"])
            agent.abort("e2e-cleanup")
        finally:
            client.write("asset_delete", assetPath="Assets/__UnitySkillsEval")
        self.assertClean(agent, scenes)

    def test_play_mode_left_running_is_stopped_and_flagged(self):
        env = self.env
        agent = env.agent()
        scenes = env.scene_state(agent)
        self.begin(agent, tasks=("E01",))
        result, attempt = self.issue(agent)
        self.solve(agent, attempt)
        env.enter_play_mode(agent)
        submitted = agent.submit(attempt)
        self.assertTrue(submitted.ok and submitted.data["done"], submitted.describe())
        final = self.passed(agent, attempt)
        self.assertIn("forced_play_mode_exit", final["reasons"])
        self.assertTrue(final["evidence"]["journal"]["forcedPlayModeExit"])
        self.assertFalse(agent.client.read("editor_get_state").get("isPlaying"))
        self.assertClean(agent, scenes)

    def test_agent_that_switches_scenes_is_scored_and_the_editor_is_still_handed_back(self):
        env = self.env
        agent = env.agent()
        scenes = env.scene_state(agent)
        self.begin(agent, tasks=("E01",))
        result, attempt = self.issue(agent)
        state = agent.state.state(attempt.session_id, attempt.attempt_id)
        self.solve(agent, attempt)
        resting = state["resting"]["scenes"][0]["path"]
        agent.client.write("scene_load", scenePath=resting)            # G2: opens another scene (drops the run scene)
        submitted = agent.submit(attempt)
        self.assertTrue(submitted.ok and submitted.data["done"], submitted.describe())
        final = self.result_of(agent, attempt)
        self.assertIn(final["status"], ("partial", "fail"), final["reasons"])
        self.assertIn("A3", [row["id"] for row in final["score"]["assertions"] if not row["pass"]])
        self.assertTrue(final["teardown"]["ok"], final["teardown"])
        self.assertClean(agent, scenes)

    # ------------------------------------------------------------------ the script domain reloads mid-attempt

    def test_script_written_to_disk_is_compiled_by_the_submit_refresh(self):
        env = self.env
        if not env.can_reload:
            self.skipTest("needs a real editor")
        agent = env.agent()
        scenes = env.scene_state(agent)
        self.begin(agent, tasks=("E01",))
        result, attempt = self.issue(agent)
        self.solve(agent, attempt)
        script = env.write_script(agent, attempt)              # no asset_refresh: the runner's own refresh imports it
        watcher = HealthWatcher(agent.client).start()
        try:
            submitted = agent.submit(attempt)
        finally:
            watcher.stop()
        self.assertTrue(watcher.saw_reload, "the submit refresh did not compile the script: the test proved nothing")
        self.assertTrue(submitted.ok and submitted.data["done"], submitted.describe())
        final = self.passed(agent, attempt)
        evidence = final["evidence"]["journal"]
        self.assertTrue(evidence["complete"])
        self.assertIsNotNone(evidence["refreshWindow"])
        self.assertGreaterEqual(evidence["directWrites"], 1, "the imported script is a direct write by the agent")
        self.assertFalse(os.path.exists(script))
        D.wait_quiet(agent.client, timeout_s=300, poll_s=1.0)
        self.assertClean(agent, scenes)

    def test_domain_reload_during_an_attempt(self):
        env = self.env
        if not env.can_reload:
            self.skipTest("needs a real editor")
        agent = env.agent()
        scenes = env.scene_state(agent)
        self.begin(agent, tasks=("E01",))
        result, attempt = self.issue(agent)
        self.solve(agent, attempt)
        seen = env.reload_scripts(agent, attempt)
        try:
            self.assertIsNot(seen["compileTriggered"], False, "asset_refresh did not start a compilation")
            submitted = agent.submit(attempt)             # the editor is compiling and reloading right now
        finally:
            seen["watcher"].stop()
        self.assertTrue(seen["watcher"].saw_reload, "the editor never compiled or went silent: the test proved nothing")
        self.assertTrue(submitted.ok and submitted.data["done"], submitted.describe())
        final = self.passed(agent, attempt)
        for reason in final["reasons"]:
            for forbidden in ("journal_incomplete", "environment_drift", "editor_restarted"):
                self.assertNotIn(forbidden, reason)
        evidence = final["evidence"]
        self.assertTrue(evidence["journal"]["complete"], evidence["journal"])
        self.assertIn("asset_refresh", evidence["telemetry"]["bySkill"], "telemetry lost the call made before the reload")
        self.assertIn("gameobject_create", evidence["telemetry"]["bySkill"])
        self.assertFalse(os.path.exists(seen["script"]), "teardown left the script behind")
        D.wait_quiet(agent.client, timeout_s=300, poll_s=1.0)
        self.assertClean(agent, scenes)

# Producer:Betsy

    def test_domain_reload_while_the_runner_builds(self):
        env = self.env
        if not env.can_reload:
            self.skipTest("needs a real editor")
        agent = env.agent()
        scenes = env.scene_state(agent)
        started = self.begin(agent, tasks=("E01",))
        sid = started.data["sessionId"]
        proc = agent.runner.popen("next")
        watcher = HealthWatcher(agent.client)
        script = None
        try:
            state = agent.state.wait_state(lambda s: s["phase"] == "building" and s["scope"]["folderCreated"],
                                           timeout_s=120.0, interval_s=0.005)
            self.assertIsNotNone(state, "never saw the run folder created")
            script = env.write_script_in(state)
            watcher.start()
            try:
                agent.client.write("asset_refresh")
            except D.runner_rest.RestError:
                pass                                           # the answer may be lost in the reload itself
            out, _err = proc.communicate(timeout=1800)
        finally:
            watcher.stop()
            if proc.poll() is None:
                D.kill_process(proc)
        envelope, clean = D.parse_envelope(out.decode("utf-8"))
        self.assertTrue(clean, out)
        if not envelope["ok"]:
            # a write cut off by the reload cannot be replayed blindly: the attempt is invalid and `next` rebuilds
            self.assertIn("FIXTURE_FAILED", [p["code"] for p in envelope["problems"]], envelope["problems"])
            first = agent.state.session(sid)["tasks"]["E01"]["attempts"][0]
            self.assertEqual(agent.state.result(sid, first)["status"], "invalid")
        result, attempt = self.issue(agent)
        self.assertTrue(watcher.saw_reload, "the editor never compiled or went silent: the test proved nothing")
        self.solve(agent, attempt)
        submitted = agent.submit(attempt)
        self.assertTrue(submitted.ok and submitted.data["done"], submitted.describe())
        self.passed(agent, attempt)
        self.assertFalse(script and os.path.exists(script), "teardown left the script behind")
        D.wait_quiet(agent.client, timeout_s=300, poll_s=1.0)
        self.assertClean(agent, scenes)

# Producer:Betsy
