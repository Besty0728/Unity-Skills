"""Offline tests for eval/suite3.py (v3 batch mode): the plan, host usage and verdicts, the slot state machine, a fake
headless host process, and whole suites in which a fake agent takes the evaluation through the runner (in-process,
the fake editor of eval/tests/runner/runner_support.py, the test pack of eval/tests/e2e/e2e_pack.py as --pack-dir).
"""
import argparse
import datetime
import json
import os
import shlex
import shutil
import sys
import tempfile
import time
import unittest
from unittest import mock

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)                      # eval/tests: runner/, e2e/
sys.path.insert(0, os.path.dirname(HERE))     # eval: harness/, selftest3, suite3, cli

from runner.runner_support import Bench, store  # noqa: E402
from e2e.e2e_pack import E2EPack  # noqa: E402
from harness import solver_driver as D  # noqa: E402
import cli  # noqa: E402
import selftest3  # noqa: E402
import suite3  # noqa: E402

SONNET = cli.MODELS["sonnet"]
OPUS = cli.MODELS["opus"]


def iso(epoch):
    return store.iso(datetime.datetime.fromtimestamp(epoch, datetime.timezone.utc))


def write_stamped(out_dir, events):
    """subject.stream.jsonl + subject.stream.times from [(epoch, event)]; returns the run record parts."""
    stream, stamps = os.path.join(out_dir, "subject.stream.jsonl"), os.path.join(out_dir, "subject.stream.times")
    with open(stream, "w", encoding="utf-8") as handle:
        handle.write("".join(json.dumps(event) + "\n" for _t, event in events))
    with open(stamps, "w", encoding="utf-8") as handle:
        handle.write("".join("%.6f\n" % t for t, _event in events))
    return {"stream": stream, "stamps": stamps}


def assistant(mid, inp, out, model="claude-sonnet-5-5", cache_read=0, cache_write=0, tool=None):
    content = [{"type": "tool_use", "id": "tu_" + mid, "name": "Bash", "input": {"command": tool}}] if tool else \
        [{"type": "text", "text": "SENTINEL-AGENT-TEXT"}]
    return {"type": "assistant", "message": {"id": mid, "model": model, "content": content, "usage": {
        "input_tokens": inp, "output_tokens": out, "cache_read_input_tokens": cache_read,
        "cache_creation_input_tokens": cache_write}}}


def result_event(model=SONNET):
    return {"type": "result", "subtype": "success", "is_error": False, "num_turns": 3, "result": "done",
            "modelUsage": {model: {"inputTokens": 1, "outputTokens": 1, "cacheReadInputTokens": 0,
                                   "cacheCreationInputTokens": 0}}}


# ---------------------------------------------------------------- plan, commands, usage, verdicts


class PlanTests(unittest.TestCase):
    def test_strict_slots_follow_the_v1_latin_rows_and_effort_defaults(self):
        configs = suite3.configs("codex", ["astra", "sol", "luna-max"], None, cli.MODELS)
        self.assertEqual([(c.model, c.effort) for c in configs],
                         [("gpt-6-astra", "default"), ("gpt-6.1-sol", "default"), ("gpt-6-luna", "max")])
        slots = suite3.plan(["S01", "S03"], configs, 2, "strict")
        v1 = cli._slots(["S01", "S03"], ["astra", "sol", "luna-max"], 2)
        self.assertEqual([(s.rep, s.task, s.config.alias) for s in slots], v1)
        self.assertEqual(len({suite3.slot_key(s) for s in slots}), len(slots))
        self.assertEqual(suite3.slot_key(slots[0]), "r0-S01-codex-astra-default")
        high = suite3.configs("claude", ["sonnet"], "high", cli.MODELS)
        self.assertEqual(suite3.slot_key(suite3.plan(["S01"], high, 1, "strict")[0]), "r0-S01-claude-sonnet-high")

    def test_suite_mode_has_one_slot_per_configuration_and_rep(self):
        configs = suite3.configs("claude", ["opus", "sonnet"], "high", cli.MODELS)
        slots = suite3.plan(["S01", "S03"], configs, 2, "suite")
        self.assertEqual([(s.rep, s.task, s.config.alias) for s in slots],
                         [(0, None, "opus"), (0, None, "sonnet"), (1, None, "sonnet"), (1, None, "opus")])
        self.assertEqual(suite3.slot_tasks(slots[0], ["S01", "S03"]), ["S01", "S03"])

    def test_default_effort_leaves_the_host_setting_alone(self):
        def command(host, alias, effort):
            config = suite3.configs(host, [alias], effort, cli.MODELS)[0]
            return suite3.build_command("PROMPT", config, "/tmp/docs", 5.0, False)
        claude = command("claude", "sonnet", None)
        self.assertNotIn("--effort", claude)
        self.assertEqual(claude[:5], ["claude", "-p", "PROMPT", "--model", SONNET])
        self.assertEqual(command("claude", "sonnet", "high")[5:7], ["--effort", "high"])
        codex = command("codex", "sol", None)
        self.assertFalse([t for t in codex if t.startswith("model_reasoning_effort=")])
        self.assertEqual(codex[-1], "PROMPT")
        self.assertIn('model_reasoning_effort="high"', command("codex", "sol", "high"))


class UsageTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.dir, True)

    def test_claude_window_uses_the_runner_log_rules(self):
        t = 1800000000.0
        events = [(t, {"type": "system", "subtype": "init", "session_id": "s-claude-1", "model": SONNET}),
                  (t + 1, assistant("m1", 100, 10)),                       # before the attempt: start, next
                  (t + 11, assistant("m2", 500, 50, cache_read=2000)),
                  (t + 12, assistant("m2", 500, 80, cache_read=2000)),     # same response, later line wins
                  (t + 13, assistant("m3", 0, 0, model="<synthetic>")),    # placeholder of a failed call
                  (t + 14, assistant("m4", 300, 30, cache_write=40)),
                  (t + 30, assistant("m5", 9, 9)),                         # after submit
                  (t + 31, result_event())]
        run = dict(write_stamped(self.dir, events), host="claude")
        record = suite3.host_usage(run, iso(t + 10), iso(t + 20))
        self.assertEqual(record["tokens"], {"input": 800, "output": 110, "cacheRead": 2000, "cacheWrite": 40,
                                            "reasoning": 0})
        self.assertEqual((record["source"], record["sessionIds"], record["models"], record["messages"]),
                         ("stream", ["s-claude-1"], ["claude-sonnet-5-5"], 2))
        self.assertIsNone(suite3.host_usage(run, iso(t + 40), iso(t + 50)))
        self.assertIsNone(suite3.host_usage(run, None, iso(t + 50)))
        self.assertEqual(suite3.stream_ids(run), "s-claude-1")

    def test_codex_window_from_stamped_observer_records(self):
        t = 1800000000.0
        metadata = suite3.StampedMetadata()
        metadata.observe({"type": "response.created", "response": {"id": "r0", "model": "gpt-6.1-sol"}})
        self.assertIsInstance(metadata.snapshot()["observations"][0]["t"], float)

        def completed(rid, stamp, inp, out, cached=0, reasoning=0):
            return {"event": "response.completed", "model": "gpt-6.1-sol", "responseId": rid, "t": stamp,
                    "usage": {"input_tokens": inp, "output_tokens": out,
                              "input_tokens_details": {"cached_tokens": cached},
                              "output_tokens_details": {"reasoning_tokens": reasoning}}}
        identity = {"observations": [completed("r1", t + 1, 50, 5), completed("r2", t + 11, 1000, 100, 800, 40),
                                     completed("r2", t + 12, 1000, 100, 800, 40), completed("r3", t + 15, 200, 20),
                                     {"event": "response.created", "model": "gpt-6.1-sol", "responseId": "r4",
                                      "t": t + 16, "usage": None}]}
        path = os.path.join(self.dir, "subject.identity.json")
        with open(path, "w", encoding="utf-8") as handle:
            json.dump(identity, handle)
        run = {"host": "codex", "identity": path, "threadId": "th-1"}
        record = suite3.host_usage(run, iso(t + 10), iso(t + 20))
        self.assertEqual(record["tokens"], {"input": 400, "output": 120, "cacheRead": 800, "cacheWrite": 0,
                                            "reasoning": 40})
        self.assertEqual((record["source"], record["sessionIds"], record["models"], record["messages"]),
                         ("proxy", ["th-1"], ["gpt-6.1-sol"], 2))
        self.assertIsNone(suite3.host_usage(run, iso(t + 30), iso(t + 40)))


class VerdictTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.dir, True)

    def run_record(self, *events, **extra):
        t = time.time()
        stamped = [(t + i, e) for i, e in enumerate(events)]
        run = dict(write_stamped(self.dir, stamped), host="claude", model=SONNET, returncode=0, timedOut=False,
                   processCleanup={"attempted": False, "ok": True, "errors": []}, finalizationErrors=[])
        run.update(extra)
        return run

    def init(self, model=SONNET):
        return {"type": "system", "subtype": "init", "session_id": "s-v", "model": model}

    def test_a_clean_run_leaves_the_runner_verdict(self):
        run = self.run_record(self.init(), assistant("m1", 1, 1, tool="python3 -B /tmp/eve-d/x/scripts/unity_eval.py "
                                                                       "next"), result_event())
        status, reasons, _metrics = suite3.host_verdict(run, 8090)
        self.assertEqual((status, reasons), (None, []))

    def test_runner_state_and_runner_code_are_forbidden_paths(self):
        for command in ("cat /p/Library/UnitySkills/eval/current.json",
                        "sed -n 1,9p /p/Library/PackageCache/com.besty.unity-skills@1/Evaluation~/usk_eval/scope.py"):
            run = self.run_record(self.init(), assistant("m1", 1, 1, tool=command), result_event())
            status, reasons, _metrics = suite3.host_verdict(run, 8090)
            self.assertEqual(status, "contaminated", command)
            self.assertIn("subject touched evaluation/user paths", reasons[0])
        run = self.run_record(self.init(), assistant("m1", 1, 1, tool="cat /p/Library/UnitySkillsEvalIO/s/prompt.md"),
                              result_event())
        self.assertIsNone(suite3.host_verdict(run, 8090)[0])

    def test_host_level_failures_invalidate(self):
        run = self.run_record(self.init(OPUS), assistant("m1", 1, 1), result_event())
        status, reasons, _m = suite3.host_verdict(run, 8090)
        self.assertEqual(status, "invalid")
        self.assertTrue(reasons[0].startswith("model_mismatch"), reasons)
        run = self.run_record(self.init(), assistant("m1", 1, 1))                   # no result event
        self.assertEqual(suite3.host_verdict(run, 8090)[:2],
                         ("invalid", ["metric_evidence: canonical_model_usage_unavailable"]))
        run = self.run_record(self.init(), assistant("m1", 1, 1, tool="curl http://127.0.0.1:8095/health"),
                              result_event())
        self.assertEqual(suite3.host_verdict(run, 8090)[0], "contaminated")
        run = self.run_record(self.init(), assistant("m1", 1, 1), result_event(), timedOut=True,
                              processCleanup={"attempted": True, "ok": False, "errors": ["group remains"]})
        self.assertIn("subject_process_cleanup_unconfirmed", suite3.host_verdict(run, 8090)[1][0])


class SlotStateTests(unittest.TestCase):
    def test_transitions(self):
        tmp = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, tmp, True)
        ledger = suite3.SuiteLedger(os.path.join(tmp, "ledger.jsonl"))
        slot = suite3.plan(["S01"], suite3.configs("claude", ["sonnet"], None, cli.MODELS), 1, "strict")[0]
        key = suite3.slot_key(slot)
        self.assertEqual(suite3.slot_state(ledger, slot), "todo")
        ledger.append("session", slot=key, sessionId="s1")
        self.assertEqual(suite3.slot_state(ledger, slot), "resume")
        ledger.append("outcome", slot=key, task="S01", sessionId="s1", attemptId="a1", status="invalid", reasons=[])
        self.assertEqual(suite3.slot_state(ledger, slot), "rerun")
        ledger.append("session", slot=key, sessionId="s2")
        self.assertEqual(suite3.slot_state(ledger, slot), "resume")
        ledger.append("outcome", slot=key, task="S01", sessionId="s2", attemptId="a2", status="invalid", reasons=[])
        self.assertEqual(suite3.slot_state(ledger, slot), "closed")
        other = suite3.plan(["S01"], suite3.configs("claude", ["opus"], None, cli.MODELS), 1, "strict")[0]
        ledger.append("session", slot=suite3.slot_key(other), sessionId="s3")
        ledger.append("outcome", slot=suite3.slot_key(other), task="S01", sessionId="s3", attemptId="a3",
                      status="partial", reasons=[])
        self.assertEqual(suite3.slot_state(ledger, other), "done")


@unittest.skipIf(os.name != "posix", "process groups and shebang scripts")
class HostProcessTests(unittest.TestCase):
    """launch_host with a fake `claude` on PATH: the stream is copied byte for byte with one stamp per line."""

    FAKE = r'''#!%s
import json, os, sys, time
with open(os.environ["FAKE_ARGV"], "w") as handle:
    json.dump(sys.argv[1:], handle)
model = sys.argv[sys.argv.index("--model") + 1]
print(json.dumps({"type": "system", "subtype": "init", "session_id": "s-fake", "model": model}), flush=True)
time.sleep(0.05)
print(json.dumps({"type": "assistant", "message": {"id": "m1", "model": "claude-sonnet-5-5", "content": [],
                  "usage": {"input_tokens": 3, "output_tokens": 1}}}), flush=True)
if os.environ.get("FAKE_HANG"):
    time.sleep(60)
print(json.dumps({"type": "result", "subtype": "success", "is_error": False}), flush=True)
'''

    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.tmp, True)
        bin_dir = os.path.join(self.tmp, "bin")
        os.makedirs(bin_dir)
        path = os.path.join(bin_dir, "claude")
        with open(path, "w", encoding="utf-8") as handle:
            handle.write(self.FAKE % sys.executable)
        os.chmod(path, 0o755)
        self.environ = {"PATH": bin_dir + os.pathsep + os.environ.get("PATH", ""),
                        "FAKE_ARGV": os.path.join(self.tmp, "argv.json")}
        for name in ("sandbox", "out", "docs"):
            os.makedirs(os.path.join(self.tmp, name))

    def launch(self, timeout_s=30.0, **env):
        config = suite3.configs("claude", ["sonnet"], None, cli.MODELS)[0]
        return suite3.launch_host("PROMPT", config, os.path.join(self.tmp, "docs"), os.path.join(self.tmp, "sandbox"),
                                  os.path.join(self.tmp, "out"), timeout_s, 5.0, False, dict(self.environ, **env))

    def test_stream_is_copied_and_stamped(self):
        run = self.launch()
        self.assertEqual((run["returncode"], run["timedOut"], run["hostError"]), (0, False, None))
        events = suite3.read_stamped(run["stream"], run["stamps"])
        self.assertEqual([e["type"] for _t, e in events], ["system", "assistant", "result"])
        self.assertEqual([t for t, _e in events], sorted(t for t, _e in events))
        with open(self.environ["FAKE_ARGV"], "r", encoding="utf-8") as handle:
            argv = json.load(handle)
        self.assertNotIn("--effort", argv)
        self.assertIn("<prompt>", run["command"])

    def test_a_hanging_agent_is_killed_at_the_timeout(self):
        started = time.time()
        run = self.launch(timeout_s=5.0, FAKE_HANG="1")
        self.assertLess(time.time() - started, 30.0)
        self.assertTrue(run["timedOut"])
        self.assertTrue(run["processCleanup"]["attempted"])
        seen = [e["type"] for _t, e in suite3.read_stamped(run["stream"], run["stamps"])]
        self.assertEqual(seen, ["system", "assistant"][:len(seen)])      # what it printed before hanging, no more


# ---------------------------------------------------------------- whole suites with a fake agent


def suite_args(**kw):
    values = dict(project=None, task=None, private_dir=None, pack_dir=None, solutions_dir=None, gate_file=None,
                  suite="test-suite", private_zip=None, host="claude", models=["sonnet"], effort=None, track="ops",
                  tier="basic", mode="strict", plan=None, reps=None, lang="en", timeout_factor=4.0, budget_usd=5.0,
                  cold=True, force=True, wait_quiet=5.0, wait_lease=0.0, dry_run=False)
    values.update(kw)
    return argparse.Namespace(**values)


class FakeAgent(object):
    """Plays a headless agent: runs the start line it was given, then the session prompt's loop with the reference
    solutions (in-process runner), and writes a stamped stream-json transcript of what it did."""

    def __init__(self, bench, behaviour="solve"):
        self.bench, self.behaviour = bench, behaviour
        self.prompts, self.launches = [], 0

    def __call__(self, prompt, config, docs, sandbox, out_dir, timeout_s, budget_usd, warm, environ):
        self.launches += 1
        self.prompts.append(prompt)
        sid_host = "s-fake-%d" % self.launches
        events = [(time.time(), {"type": "system", "subtype": "init", "session_id": sid_host, "model": config.model})]
        base = config.model.split("[")[0]

        def say(mid, inp, out, tool=None):
            time.sleep(0.01)
            events.append((time.time(), assistant(mid, inp, out, model=base, tool=tool)))

        line = prompt.strip().splitlines()[-1]
        argv = shlex.split(line)
        self.check_start_line(argv, docs, config)
        say("m-start", 100, 10, tool=line)
        if self.behaviour != "no_start":
            env = dict(self.bench.env, CLAUDE_CODE_SESSION_ID=sid_host, USK_EVAL_ENTRY=argv[2])   # as the entry script
            runner = D.Runner(self.bench.project, environ=env, inproc=True)
            started = runner._inproc(argv[3:], None)
            assert started.ok, started.describe()
            runner.session = started.data["sessionId"]
            pack_dir = argv[argv.index("--pack-dir") + 1]
            packs = D.PackAccess(pack_dir=pack_dir, solutions_dir=os.path.join(pack_dir, "solutions"))
            agent = D.ScriptedAgent(runner, packs)
            for _ in range(10):
                result, attempt = agent.next()
                if attempt is None:
                    break
                say("m-work-%s" % attempt.task_id, 1000, 100)
                if self.behaviour == "peek":
                    say("m-peek", 5, 5, tool="cat %s/Library/UnitySkills/eval/current.json" % self.bench.project)
                answer = packs.solutions(attempt.task_id).reference(agent.context(attempt))
                if self.behaviour == "leave_open":
                    break
                agent.write_answer(attempt, answer)
                say("m-submit-%s" % attempt.task_id, 200, 20, tool=attempt.submit_command)
                submitted = agent.submit(attempt)
                if submitted.data.get("stop"):
                    break
        time.sleep(0.01)
        say("m-final", 7, 7)
        events.append((time.time(), result_event(config.model)))
        run = {"host": config.host, "model": config.model, "effort": config.effort, "warm": warm, "returncode": 0,
               "timedOut": False, "hostError": None, "finalizationErrors": [], "command": ["claude", "<prompt>"],
               "processCleanup": {"attempted": False, "ok": True, "errors": []}, "identity": None,
               "isolationVerified": None, "wallSeconds": 0.1}
        run.update(write_stamped(out_dir, events))
        return run

    def check_start_line(self, argv, docs, config):
        test = self.bench.test
        test.assertEqual(argv[2], os.path.join(docs, "scripts", "unity_eval.py"))
        test.assertEqual(argv[3:7], ["start", "--project", self.bench.project, "--launcher"])
        test.assertEqual(argv[7], "batch")
        declared_path = argv[argv.index("--declared") + 1]
        with open(declared_path, "r", encoding="utf-8") as handle:
            declared = json.load(handle)
        test.assertEqual(declared["effort"], {"value": config.effort, "source": "batch"})
        test.assertTrue(argv[argv.index("--pack-dir") + 1].startswith(test.stage_root))


class SuiteTests(unittest.TestCase):
    def setUp(self):
        self.b = Bench(self, stand_ins=False)
        self.b.test = self
        self.pack = E2EPack.create(os.path.join(self.b.tmp, "e2e"), tasks=("E01", "E02"))
        self.runs = os.path.join(self.b.tmp, "suite-runs")
        self.stage_root = os.path.join(self.b.tmp, "stage")
        for name, value in (("STAGE_ROOT", self.stage_root),):
            patcher = mock.patch.object(suite3, name, value)
            patcher.start()
            self.addCleanup(patcher.stop)
        self.lines = []
        self.agent = FakeAgent(self.b)

    def suite(self, **kw):
        kw.setdefault("project", self.b.project)
        kw.setdefault("pack_dir", self.pack.pack_dir)
        kw.setdefault("gate_file", os.path.join(self.b.tmp, "gate.json"))
        return suite3.run_from_args(suite_args(**kw), cli.GATE_INPUTS, cli.MODELS, out=self.lines.append,
                                    environ=self.b.env, inproc=True, launcher=self.agent, runs_dir=self.runs,
                                    agent_overrides=self.b.env, version_probe=lambda host, env: "fake-cli 1.0")

    @property
    def text(self):
        return "\n".join(self.lines)

    def ledger(self, kind):
        path = os.path.join(self.runs, "test-suite", "ledger.jsonl")
        return [e for e in store.read_jsonl(path) if e.get("kind") == kind]

    def test_strict_suite_one_agent_and_session_per_task(self):
        self.assertEqual(self.suite(), 0, self.text)
        self.assertEqual(self.agent.launches, 2)
        sessions = [e["sessionId"] for e in self.ledger("session")]
        self.assertEqual(len(set(sessions)), 2)
        for sid, task in zip(sessions, ("E01", "E02")):
            session = self.b.session(sid)
            self.assertEqual((session["launcher"], session["integrity"], session["mode"], session["order"],
                              session["status"]), ("batch", "batch-isolated", "strict", [task], "completed"))
            self.assertEqual(session["declared"]["model"], {"value": SONNET, "source": "batch"})
            self.assertEqual(session["declared"]["effort"], {"value": "default", "source": "batch"})
            self.assertEqual(session["declared"]["hostVersion"], {"value": "fake-cli 1.0", "source": "batch"})
        attempts = self.ledger("attempt")
        self.assertEqual([(e["task"], e["status"], e["submittedBy"], e["effort"]) for e in attempts],
                         [("E01", "pass", "agent", "default"), ("E02", "pass", "agent", "default")])
        result = self.b.result(attempts[0]["sessionId"], attempts[0]["attemptId"])
        self.assertEqual((result["usage"]["source"], result["usage"]["tokens"]["input"],
                          result["usage"]["tokens"]["output"]), ("stream", 1200, 120))
        self.assertEqual(result["usage"]["model"], {"values": ["claude-sonnet-5-5"], "source": "provider-returned"})
        self.assertEqual(attempts[0]["usage"], {"source": "stream", "processed": 1320})
        self.assertEqual([(e["task"], e["status"]) for e in self.ledger("outcome")], [("E01", "pass"), ("E02", "pass")])
        cohort = store.read_json(os.path.join(self.runs, "test-suite", "cohort.json"))
        self.assertEqual((cohort["pack"]["id"], cohort["configurations"], cohort["hostVersion"], cohort["pipeline"]),
                         ("usk-e2e-pack", [["claude", SONNET, "default"]], "fake-cli 1.0", "URP"))
        self.assertEqual(sorted(cohort["gateInputs"]), ["E01", "E02"])
        self.assertEqual(self.agent.prompts[0].splitlines()[0], suite3.START_PROMPT["en"].splitlines()[0])
        self.assertEqual(self.suite(), 0, self.text)                  # resuming a finished suite runs nothing
        self.assertEqual(self.agent.launches, 2)
        self.lines[:] = []
        self.assertEqual(self.suite(dry_run=True), 0, self.text)
        self.assertEqual([l.split("]")[0] for l in self.lines if l.startswith("[")], ["[done  ", "[done  "])
        for path in (os.path.join(self.runs, "test-suite", "ledger.jsonl"),):
            with open(path, "r", encoding="utf-8") as handle:
                self.assertNotIn("SENTINEL-AGENT-TEXT", handle.read())

    def test_dry_run_lists_every_slot_and_runs_nothing(self):
        code = self.suite(dry_run=True, models=["sonnet", "opus"], reps=2, project=os.path.join(self.b.tmp, "none"))
        self.assertEqual(code, 0, self.text)
        slots = [l for l in self.lines if l.startswith("[")]
        self.assertEqual(len(slots), 8)
        self.assertTrue(all(l.startswith("[todo  ]") for l in slots))
        self.assertIn("effort=default", slots[0])
        self.assertIn("slots: 8 (todo 8)", self.text)
        self.assertEqual(self.agent.launches, 0)
        self.assertFalse(os.path.exists(self.runs))

    def test_an_attempt_left_open_is_submitted_by_the_driver(self):
        self.agent.behaviour = "leave_open"
        self.assertEqual(self.suite(task=["E01"]), 0, self.text)
        (attempt,) = self.ledger("attempt")
        self.assertEqual((attempt["submittedBy"], attempt["runnerStatus"]), ("driver", attempt["status"]))
        self.assertIn(attempt["status"], ("partial", "fail"))
        state = self.b.state(attempt["sessionId"], attempt["attemptId"])
        self.assertEqual(state["submittedBy"], "driver")

    def test_reading_runner_state_contaminates_the_slot(self):
        self.agent.behaviour = "peek"
        self.assertEqual(self.suite(task=["E01"]), 0, self.text)
        (attempt,) = self.ledger("attempt")
        self.assertEqual((attempt["runnerStatus"], attempt["status"]), ("pass", "contaminated"))
        self.assertIn("evaluation/user paths", attempt["reasons"][0])
        self.assertEqual(self.ledger("outcome")[0]["status"], "contaminated")

    def test_an_agent_that_starts_nothing_stops_the_suite(self):
        self.agent.behaviour = "no_start"
        self.assertEqual(self.suite(task=["E01"]), 2, self.text)
        self.assertIn("started no session", self.text)

    def test_suite_mode_one_agent_for_the_whole_selection(self):
        self.assertEqual(self.suite(mode="suite"), 0, self.text)
        self.assertEqual(self.agent.launches, 1)
        (session_entry,) = self.ledger("session")
        session = self.b.session(session_entry["sessionId"])
        self.assertEqual((session["mode"], session["order"], session["status"]), ("suite", ["E01", "E02"], "completed"))
        attempts = self.ledger("attempt")
        self.assertEqual([e["task"] for e in attempts], ["E01", "E02"])
        for entry in attempts:                                   # each window holds its own task's messages only
            usage = self.b.result(entry["sessionId"], entry["attemptId"])["usage"]
            self.assertEqual((usage["source"], usage["tokens"]["input"]), ("stream", 1200))

    def test_a_changed_cohort_is_refused(self):
        self.assertEqual(self.suite(task=["E01"]), 0, self.text)
        self.assertEqual(self.suite(task=["E01"], effort="high"), 2, self.text)
        self.assertIn("cohort of suite test-suite changed (configurations)", self.text)

    def test_the_gate_must_be_green_unless_forced(self):
        self.assertEqual(self.suite(task=["E01"], force=False), 2, self.text)
        self.assertIn("gate3 is not green", self.text)
        self.assertEqual(self.agent.launches, 0)
        gate_args = argparse.Namespace(project=self.b.project, task=["E01"], private_dir=None,
                                       pack_dir=self.pack.pack_dir, solutions_dir=None,
                                       gate_file=os.path.join(self.b.tmp, "gate.json"), repeat=2, extra_seed=None,
                                       case=None, no_gate=False, abort_active=False, cleanup_stale=False,
                                       wait_quiet=5.0, wait_lease=0.0, lang="en", dry_run=False)
        self.assertEqual(selftest3.run_from_args(gate_args, cli.GATE_INPUTS, out=self.lines.append,
                                                 environ=self.b.env, inproc=True), 0, self.text)
        self.assertEqual(self.suite(task=["E01"], force=False, suite="gated"), 0, self.text)
        self.assertEqual(self.agent.launches, 1)


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
