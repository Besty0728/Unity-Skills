"""v3 batch suite (maintainer tool): headless Claude Code / Codex agents take the evaluation through the runner.

Every slot is one headless agent session that runs the evaluation the way a user's agent does: it gets the start
command (the frozen docs' bundled entry script, `--launcher batch`), follows the session prompt, runs `next`, does the
task through REST, writes the answer and runs `submit`. Per task by default (strict): one runner session and one agent
per (rep, task, configuration); `--mode suite` gives one agent the whole selection in one session.

Between agents the driver only waits for a quiet editor and reads the runner's state: it submits an attempt the agent
left open (`--no-answer`, submittedBy driver), records the token usage the host itself reported (`usage --record`:
Claude stream-json, Codex through the forwarding observer; the runner's log-based usage stays when neither has any)
and overlays the v1 host checks (model identity, host errors, process cleanup, other editor instances, forbidden
paths) on the runner's classification in the suite ledger.

Slot identity is (suite, rep, task, host, model, effort); without --effort the host's own default applies and the
slot records effort "default". A suite is bound to its cohort: pack id, version and manifest hash, plugin version,
Unity version, render pipeline, each task's gateInputsHash, configurations, frozen docs and host version. A run that
would mix cohorts is refused; tasks without a green gate3 entry run only with --force.

  python3 -B eval/cli.py --project <P> suite3 --suite NAME [--host claude|codex] [--models ...] [--effort E]
      [--track ops] [--tier basic] [--task ID ...] [--mode strict|suite] [--plan pilot|calibration | --reps N]
      [--lang en|zh-CN] [--pack-dir D | --private-zip Z | --private-dir D] [--dry-run]

Exit codes: 0 every slot ran or was already done, 2 the environment needs attention (the run stopped).
"""
import collections
import contextlib
import datetime
import hashlib
import json
import os
import re
import secrets
import shutil
import subprocess
import sys
import tempfile
import threading
import time
from types import SimpleNamespace

sys.dont_write_bytecode = True

HERE = os.path.dirname(os.path.abspath(__file__))
if HERE not in sys.path:
    sys.path.insert(0, HERE)

import selftest3  # noqa: E402
from harness import classify as C1  # noqa: E402
from harness import codex_proxy  # noqa: E402
from harness import env as E1  # noqa: E402
from harness import metrics as M1  # noqa: E402
from harness import solver_driver as D  # noqa: E402
from harness import subject as S1  # noqa: E402
from usk_eval import paths as runner_paths  # noqa: E402
from usk_eval import prompts as runner_prompts  # noqa: E402
from usk_eval import store  # noqa: E402
from usk_eval.focus import FocusKeeper  # noqa: E402
from usk_eval.problems import ProblemError  # noqa: E402
from usk_eval.rest import RestError  # noqa: E402
from usk_eval.usage import claude as usage_claude  # noqa: E402

REPO = D.REPO
RUNS_DIR = os.path.join(REPO, "eval", "runs", "suite3")
STAGE_ROOT = "/tmp/eve-p"            # pack copies the agents see, outside the repo like v1's /tmp/eve-s and /tmp/eve-d
PLANS = {"pilot": 1, "calibration": 3}
DEFAULT_MODELS = {"claude": ("opus", "sonnet", "fable"), "codex": ("astra", "sol", "terra", "luna-max")}
HOST_BINARIES = {"claude": "claude", "codex": "codex"}
DECLARED_HOSTS = {"claude": "claude-code", "codex": "codex"}
MODES = ("strict", "suite")
LANGS = ("en", "zh-CN")
LEDGER_SCHEMA = 1
COHORT_SCHEMA = 1
MAX_LAUNCHES = 3                     # per slot and run: an agent, a resume after the runner re-issues, one more
MIN_TIMEOUT_S = 600.0
ENVIRONMENT_EXIT = 2
# Paths an agent under test never touches, besides v1's metrics.SUSPICIOUS_PATHS: runner state and runner code (G6).
FORBIDDEN_PATHS = ("/Library/UnitySkills/eval", "Evaluation~")
UNSAFE_NAME = re.compile(r"[^A-Za-z0-9._-]")
START_PROMPT = {
    "en": "Run the UnitySkills evaluation. Start it with exactly this command, then follow the session prompt it "
          "points you to (data.sessionPromptFile) until that prompt tells you to stop:\n{start}\n",
    "zh-CN": "运行 UnitySkills 评测。用下面这条命令原样启动，然后按它指向的会话提示（data.sessionPromptFile）执行，"
             "直到该提示让你停止：\n{start}\n",
}

Config = collections.namedtuple("Config", "host alias model effort")
Slot = collections.namedtuple("Slot", "rep task config")


class SuiteError(Exception):
    """The run cannot continue (exit code 2)."""


def echo(message):
    print(message, flush=True)


# ---------------------------------------------------------------- plan


def configs(host, aliases, effort, models):
    """One Config per model alias; luna-max is gpt-6-luna at effort max (as in v1), no --effort is "default"."""
    return [Config(host, alias, models.get(alias, alias), "max" if alias == "luna-max" else (effort or "default"))
            for alias in aliases]


def plan(task_ids, configurations, reps, mode):
    """Rep-major. strict: tasks in pack order with the configurations rotated per task (v1 cli._slots); suite: one
    slot per configuration, rotated per rep."""
    out = []
    for rep in range(reps):
        if mode == "suite":
            k = rep % len(configurations)
            out.extend(Slot(rep, None, c) for c in configurations[k:] + configurations[:k])
            continue
        for i, task_id in enumerate(task_ids):
            k = (rep + i) % len(configurations)
            out.extend(Slot(rep, task_id, c) for c in configurations[k:] + configurations[:k])
    return out


def slot_key(slot):
    c = slot.config
    return "r%d-%s-%s-%s-%s" % (slot.rep, slot.task or "all", c.host, UNSAFE_NAME.sub("_", c.alias),
                                UNSAFE_NAME.sub("_", c.effort))


def slot_tasks(slot, task_ids):
    return [slot.task] if slot.task else list(task_ids)


def describe_slot(slot):
    c = slot.config
    return "r%d %-5s %s %s effort=%s" % (slot.rep, slot.task or "all", c.host, c.model, c.effort)


# ---------------------------------------------------------------- suite ledger


class SuiteLedger(object):
    """eval/runs/suite3/<suite>/ledger.jsonl, append-only. Kinds:
    session  {slot, sessionId}                    the runner session an agent started for the slot
    launch   {slot, launchId, ...}                one headless agent run
    attempt  {slot, sessionId, attemptId, taskId, runnerStatus, status, reasons, usage, launchId}
    outcome  {slot, task, sessionId, attemptId, status, reasons}   the task's final attempt once its session ended
    """

    def __init__(self, path):
        self.path = path

    def entries(self):
        return store.read_jsonl(self.path)

    def append(self, kind, **fields):
        os.makedirs(os.path.dirname(self.path), exist_ok=True)
        entry = dict({"schema": LEDGER_SCHEMA, "kind": kind, "recordedAt": store.now_iso()}, **fields)
        store.append_jsonl(self.path, entry)
        return entry

    def of(self, key, kind):
        return [e for e in self.entries() if e.get("slot") == key and e.get("kind") == kind]


def slot_state(ledger, slot):
    """todo | resume (its runner session has no outcome yet) | done | rerun (strict: one new session after an
    invalid outcome) | closed (the rerun is spent, or contaminated / cancelled)."""
    key = slot_key(slot)
    sessions = [e["sessionId"] for e in ledger.of(key, "session")]
    if not sessions:
        return "todo"
    outcomes = ledger.of(key, "outcome")
    if not [e for e in outcomes if e.get("sessionId") == sessions[-1]]:
        return "resume"
    if slot.task is None:
        return "done"
    last = [e for e in outcomes if e.get("task") == slot.task][-1]
    if last.get("status") in C1.MODEL_OUTCOMES:
        return "done"
    return "rerun" if last.get("status") == "invalid" and len(sessions) < 2 else "closed"


# ---------------------------------------------------------------- host usage


def read_stamped(stream_path, stamps_path):
    """[(epoch, event)] of a stamped stream; lines that are not JSON objects are skipped."""
    try:
        with open(stream_path, "rb") as handle:
            lines = handle.read().split(b"\n")
        with open(stamps_path, "r", encoding="utf-8") as handle:
            stamps = [float(line) for line in handle.read().split()]
    except (OSError, ValueError):
        return []
    out = []
    for raw, stamp in zip(lines, stamps):
        try:
            event = json.loads(raw.decode("utf-8", "replace"))
        except ValueError:
            continue
        if isinstance(event, dict):
            out.append((stamp, event))
    return out


def claude_usage(run, t0, t1):
    """Usage of (t0, t1] from the stamped stream-json, through the runner's own Claude log reader (the same rules as
    for an interactive session: the last line of a message id, no placeholder responses); None without messages."""
    events = read_stamped(run.get("stream"), run.get("stamps"))
    sid = next((e.get("session_id") for _t, e in events if e.get("type") == "system" and
                runner_usage_safe(e.get("session_id"))), None) or "batch-" + secrets.token_hex(4)
    with tempfile.TemporaryDirectory(prefix="usk-suite3-") as root:
        folder = os.path.join(root, "batch")
        os.makedirs(folder)
        with open(os.path.join(folder, sid + ".jsonl"), "w", encoding="utf-8") as handle:
            for stamp, event in events:
                if event.get("type") == "assistant":
                    moment = datetime.datetime.fromtimestamp(stamp, datetime.timezone.utc)
                    handle.write(json.dumps(dict(event, timestamp=store.iso(moment))) + "\n")
        outcome = usage_claude.read([sid], store.parse_utc(t0), store.parse_utc(t1), {"USK_EVAL_CLAUDE_ROOT": root})
    if "tokens" not in outcome or not outcome.get("messages"):
        return None
    tokens = outcome["tokens"]
    return {"source": "stream", "sessionIds": [sid], "models": sorted(set(outcome.get("models") or [])),
            "tokens": {k: tokens[k] for k in ("input", "output", "cacheRead", "cacheWrite", "reasoning")},
            "raw": outcome.get("raw"), "messages": outcome["messages"]}


def runner_usage_safe(value):
    return isinstance(value, str) and re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]{0,127}", value) is not None


def codex_usage(run, t0, t1):
    """Usage of (t0, t1] from the observer's stamped responses, mapped like the runner's Codex reader (cached and
    cache-write tokens are inside input_tokens); None without a response in the window."""
    try:
        with open(run.get("identity") or "", "r", encoding="utf-8") as handle:
            identity = json.load(handle)
    except (OSError, ValueError):
        return None
    start, end = store.epoch(t0), store.epoch(t1)
    seen, models = set(), set()
    total = {"input_tokens": 0, "output_tokens": 0, "cached_tokens": 0, "cache_write_tokens": 0,
             "reasoning_tokens": 0}
    count = 0
    for obs in identity.get("observations") or []:
        if not isinstance(obs, dict) or obs.get("event") not in ("response.completed", "response.json",
                                                                   "response.failed", "response.incomplete"):
            continue
        stamp, usage = obs.get("t"), obs.get("usage")
        if not isinstance(stamp, (int, float)) or not start < stamp <= end or not isinstance(usage, dict):
            continue
        rid = obs.get("responseId")
        if rid and rid in seen:
            continue
        seen.add(rid)
        count += 1
        details = usage.get("input_tokens_details") or {}
        values = {"input_tokens": usage.get("input_tokens"), "output_tokens": usage.get("output_tokens"),
                  "cached_tokens": details.get("cached_tokens"), "cache_write_tokens": details.get("cache_write_tokens"),
                  "reasoning_tokens": (usage.get("output_tokens_details") or {}).get("reasoning_tokens")}
        for key, value in values.items():
            if isinstance(value, int) and not isinstance(value, bool) and value > 0:
                total[key] += value
        if isinstance(obs.get("model"), str):
            models.add(obs["model"])
    if not count:
        return None
    thread = run.get("threadId")
    return {"source": "proxy", "sessionIds": [thread] if runner_usage_safe(thread) else [], "models": sorted(models),
            "tokens": {"input": max(0, total["input_tokens"] - total["cached_tokens"] - total["cache_write_tokens"]),
                       "output": total["output_tokens"], "cacheRead": total["cached_tokens"],
                       "cacheWrite": total["cache_write_tokens"], "reasoning": total["reasoning_tokens"]},
            "raw": total, "messages": count}


def host_usage(run, t0, t1):
    if not t0 or not t1:
        return None
    return claude_usage(run, t0, t1) if run.get("host") == "claude" else codex_usage(run, t0, t1)


# ---------------------------------------------------------------- host checks


class _AllPass(object):
    """Scorecard stand-in for v1 classify: only its host-level verdicts (invalid, contaminated) are used."""
    strict, score = True, 1.0


def forbidden_access(stream_path, host):
    """Agent inputs that name runner state or runner code (contract G6), read the way v1's metrics.parse reads them
    for suspiciousAccess: every string of a Claude tool input, a Codex command or the whole Codex item."""
    found = []
    for event in M1.load_events(stream_path):
        inputs = []
        if host == "claude" and event.get("type") == "assistant":
            for block in (event.get("message") or {}).get("content") or []:
                if isinstance(block, dict) and block.get("type") == "tool_use":
                    given = block.get("input") or {}
                    inputs.append((block.get("name"), " ".join(str(v) for v in given.values() if isinstance(v, str))))
        elif host == "codex" and event.get("type") in ("item.started", "item.completed"):
            item = event.get("item") or {}
            if item.get("type") in ("command_execution", "file_change", "mcp_tool_call", "web_search"):
                inputs.append((item.get("type"), (item.get("command") or "") if item.get("type") == "command_execution"
                               else json.dumps(item)))
        for tool, text in inputs:
            for marker in FORBIDDEN_PATHS:
                if marker in text:
                    found.append({"tool": tool, "marker": marker, "input": text[:300]})
    return found


def host_verdict(run, eval_port):
    """(status, reasons, metrics): status None when the host evidence leaves the runner's classification standing,
    else invalid or contaminated by the v1 rules (process cleanup, finalization, model identity, usage evidence,
    host errors, API retries, other instances, forbidden paths)."""
    metrics = M1.parse(run["stream"], eval_port, None, host=run["host"])
    metrics["suspiciousAccess"] = list(metrics.get("suspiciousAccess") or []) + forbidden_access(run["stream"],
                                                                                             run["host"])
    status, reasons = C1.classify(_AllPass(), subject=run, metrics=metrics,
                                  model_ok=M1.model_matches(run["model"], metrics))
    if status not in ("invalid", "contaminated"):
        return None, [r for r in reasons if r in ("timed_out",) or r.startswith("host_subtype=")], metrics
    if run.get("host") == "codex" and run.get("isolationVerified") is False:
        reasons = ["codex_isolation_unverified"] + reasons
    return status, reasons, metrics


# ---------------------------------------------------------------- one headless agent


class StampedMetadata(codex_proxy.ResponseMetadata):
    """The observer's response records with the epoch they were seen at (`t`), for per-attempt usage windows."""

    def observe(self, payload):
        stamp = time.time()
        with self._lock:
            first = len(self._observations)
        codex_proxy.ResponseMetadata.observe(self, payload)
        with self._lock:
            for record in self._observations[first:]:
                record.setdefault("t", stamp)


def build_command(prompt, config, docs, budget_usd, warm):
    """v1's subject command; effort "default" leaves the host's own reasoning effort in place."""
    cmd = S1.build_command(prompt, config.model, config.effort, docs, budget_usd, warm, host=config.host)
    if config.effort != "default":
        return cmd
    out, skip = [], False
    for index, token in enumerate(cmd):
        if skip:
            skip = False
        elif config.host == "claude" and token == "--effort":
            skip = True
        elif config.host == "codex" and token == "-c" and index + 1 < len(cmd) and \
                cmd[index + 1].startswith("model_reasoning_effort="):
            skip = True
        else:
            out.append(token)
    return out


def _pump(pipe, stream_path, stamps_path):
    """Copies the host's stdout byte for byte and writes the arrival epoch of every line next to it."""
    with open(stream_path, "wb") as out, open(stamps_path, "w", encoding="utf-8") as stamps:
        for raw in iter(pipe.readline, b""):
            out.write(raw)
            out.flush()
            stamps.write("%.6f\n" % time.time())
            stamps.flush()


def agent_environ(host, overrides=None):
    """v1's subject environment without the runner's own override variables, plus `overrides`."""
    env = S1.child_env(host)
    for name in ("USK_EVAL_PROJECT", "USK_EVAL_ENTRY", "USK_EVAL_PRIVATE_URLS", "USK_EVAL_CLAUDE_ROOT",
                 "USK_EVAL_CODEX_ROOT"):
        env.pop(name, None)
    env.update(overrides or {})
    return env


def launch_host(prompt, config, docs, sandbox, out_dir, timeout_s, budget_usd, warm, environ):
    """Runs one headless agent in `sandbox` (process group killed on timeout, v1 subject.run) and returns its run
    record. stdout goes to subject.stream.jsonl with one arrival epoch per line in subject.stream.times."""
    stream_path = os.path.join(out_dir, "subject.stream.jsonl")
    stamps_path = os.path.join(out_dir, "subject.stream.times")
    stderr_path = os.path.join(out_dir, "subject.stderr.txt")
    run = {"host": config.host, "model": config.model, "effort": config.effort, "warm": warm,
           "budgetUsd": budget_usd if config.host == "claude" else None, "timeoutSeconds": timeout_s,
           "stream": stream_path, "stamps": stamps_path, "stderr": stderr_path, "returncode": None,
           "timedOut": False, "hostError": None, "finalizationErrors": [], "command": [],
           "processCleanup": {"attempted": False, "ok": True, "errors": []},
           "identity": os.path.join(out_dir, "subject.identity.json") if config.host == "codex" else None,
           "isolationVerified": False if config.host == "codex" else None, "env": S1.env_record(config.host)}
    codex_home = proxy = None
    started = time.time()
    try:
        cmd = build_command(prompt, config, docs, budget_usd, warm)
        if config.host == "codex":
            codex_home, transport, proxy = S1._codex_home(timeout_s=timeout_s)
            proxy.records = StampedMetadata()
            environ = dict(environ, CODEX_HOME=codex_home)
            isolation = S1._codex_isolation(cmd, sandbox, environ, out_dir)
            run["isolationVerified"] = True
            cmd = cmd[:-1] + isolation + transport + cmd[-1:]
        run["command"] = S1.redact(cmd, prompt)
        with open(stderr_path, "w", encoding="utf-8") as err:
            proc = subprocess.Popen(cmd, cwd=sandbox, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=err,
                                    env=environ, start_new_session=True)
            pump = threading.Thread(target=_pump, args=(proc.stdout, stream_path, stamps_path), daemon=True)
            pump.start()
            try:
                run["returncode"] = proc.wait(timeout=timeout_s)
            except subprocess.TimeoutExpired:
                run["timedOut"] = True
                run["processCleanup"] = S1._kill_group(proc)
                code = run["processCleanup"].get("leaderReturnCode")
                run["returncode"] = code if code is not None else proc.poll()
            except BaseException:           # Ctrl-C: the agent must not keep writing to the editor
                S1._kill_group(proc)
                raise
            pump.join(timeout=10.0)
            with contextlib.suppress(OSError):
                proc.stdout.close()
    except (OSError, ValueError, ImportError) as exc:
        run["hostError"] = "%s: %s" % (type(exc).__name__, exc)
        with open(stderr_path, "a", encoding="utf-8") as err:
            err.write(run["hostError"] + "\n")
        for path in (stream_path, stamps_path):
            open(path, "a").close()
    finally:
        run["wallSeconds"] = round(time.time() - started, 2)
        if proxy:
            try:
                proxy.close()
                with open(run["identity"], "w", encoding="utf-8") as handle:
                    json.dump(proxy.metadata(), handle, indent=1)
            except Exception as exc:
                run["finalizationErrors"].append("identity: %s: %s" % (type(exc).__name__, exc))
        if codex_home:
            shutil.rmtree(codex_home, ignore_errors=True)
    if run["finalizationErrors"]:
        run["hostError"] = "; ".join(([run["hostError"]] if run["hostError"] else []) + run["finalizationErrors"])
    return run


def stream_ids(run):
    """The host's own session id (Claude init session_id) or thread id (Codex thread.started)."""
    for _stamp, event in read_stamped(run.get("stream"), run.get("stamps")):
        if event.get("type") == "system" and runner_usage_safe(event.get("session_id")):
            return event["session_id"]
        if event.get("type") == "thread.started" and runner_usage_safe(event.get("thread_id")):
            return event["thread_id"]
    return None


# ---------------------------------------------------------------- docs, packs, cohort


def freeze_docs(project, store_root):
    """The installed package's unity-skills~ copied once per content (v1 env.freeze_docs, any package source)."""
    source = os.path.join(runner_paths.resolve_package_root(project)["root"], "unity-skills~")
    digest, _count = E1.tree_hash(source)
    dest = os.path.join(store_root, digest[:16], "docs")
    if not os.path.isdir(dest):
        staging = dest + ".tmp-%d" % os.getpid()
        shutil.rmtree(staging, ignore_errors=True)
        shutil.copytree(source, staging, ignore=shutil.ignore_patterns("__pycache__", "*.pyc"))
        os.makedirs(os.path.dirname(dest), exist_ok=True)
        os.rename(staging, dest)
    if E1.tree_hash(dest)[0] != digest:
        raise SuiteError("frozen docs %s differ from the installed copy" % dest)
    return dest, digest


def pack_option(args):
    """(flag, path) of the runner pack option the sessions use: --pack-dir, --private-zip or --private-dir, by default
    --private-dir eval/pack/private like selftest3."""
    given = [(flag, value) for flag, value in (("--pack-dir", args.pack_dir), ("--private-zip", args.private_zip),
                                               ("--private-dir", args.private_dir)) if value]
    if len(given) > 1:
        raise SuiteError("%s are mutually exclusive" % " and ".join(flag for flag, _value in given))
    flag, value = given[0] if given else ("--private-dir", selftest3.DEFAULT_PRIVATE_DIR)
    return flag, os.path.abspath(value)


def open_run_packs(option, environ):
    flag, path = option
    try:
        return D.PackAccess(pack_dir=path if flag == "--pack-dir" else None,
                            private_zip=path if flag == "--private-zip" else None,
                            private_dir=path if flag == "--private-dir" else None, environ=environ)
    except ProblemError as exc:
        raise SuiteError("cannot open the pack: %s" % "; ".join(p["message"] for p in exc.problems))


def stage_pack(option, suite):
    """The pack option the agents get, pointing at a copy under /tmp/eve-p/<suite hash>: outside the repo, so naming
    it is no forbidden path; kept after the run for a later --retry-verify of the same suite."""
    flag, source = option
    target = os.path.join(STAGE_ROOT, hashlib.sha256(suite.encode("utf-8")).hexdigest()[:12], flag.strip("-"),
                          os.path.basename(source.rstrip(os.sep)))
    if os.path.isdir(target):
        shutil.rmtree(target)
    elif os.path.exists(target):
        os.remove(target)
    os.makedirs(os.path.dirname(target), exist_ok=True)
    if os.path.isdir(source):
        shutil.copytree(source, target, ignore=shutil.ignore_patterns("__pycache__", "*.pyc"))
    else:
        shutil.copy2(source, target)
    return [flag, target]


def open_gate_packs(args, environ):
    """The same pack opened the way selftest3 gated it (--pack-dir, else the package pack with --private-dir or
    eval/pack/private, and the solutions), so gateInputsHash names the files the gate ran."""
    gate_args = SimpleNamespace(pack_dir=args.pack_dir, private_dir=args.private_dir,
                                solutions_dir=getattr(args, "solutions_dir", None))
    try:
        return selftest3.open_packs(gate_args, environ)
    except selftest3.SelftestError as exc:
        raise SuiteError(str(exc))


def host_version(host, environ):
    try:
        out = subprocess.run([HOST_BINARIES[host], "--version"], capture_output=True, text=True, timeout=30,
                             env=environ)
        text = (out.stdout or out.stderr).strip()
        return text.splitlines()[0] if text else None
    except (OSError, subprocess.SubprocessError) as exc:
        return "unavailable: %s" % type(exc).__name__


# ---------------------------------------------------------------- the suite


class Suite(object):
    def __init__(self, args, gate_inputs, models, out=echo, environ=None, inproc=False, launcher=launch_host,
                 runs_dir=RUNS_DIR, agent_overrides=None, version_probe=host_version):
        if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]{0,63}", args.suite or ""):
            raise SuiteError("--suite needs a name of letters, digits, '.', '_' or '-'")
        if args.mode not in MODES:
            raise SuiteError("--mode is strict or suite")
        self.args, self.gate_inputs, self.out = args, gate_inputs, out
        self.environ = dict(os.environ if environ is None else environ)
        self.inproc, self.launcher, self.version_probe = inproc, launcher, version_probe
        self.agent_overrides = dict(agent_overrides or {})
        self.suite_dir = os.path.join(runs_dir, args.suite)
        self.docs_root = os.path.join(runs_dir, "docs")
        self.ledger = SuiteLedger(os.path.join(self.suite_dir, "ledger.jsonl"))
        self.cohort_path = os.path.join(self.suite_dir, "cohort.json")
        self.pack_option = pack_option(args)
        self.run_packs, self.gate_packs = open_run_packs(self.pack_option, self.environ), None
        self.track, self.tier = args.track or "ops", args.tier or "basic"
        try:
            self.task_ids = self.run_packs.pack.select(self.track, self.tier, selftest3.split_tasks(args.task) or None)
        except ProblemError as exc:
            raise SuiteError("; ".join(p["message"] + (": " + p["args"][0] if p.get("args") else "")
                                       for p in exc.problems))
        aliases = list(args.models or DEFAULT_MODELS[args.host])
        reps = args.reps if args.reps else PLANS[args.plan or "pilot"]
        if reps < 1:
            raise SuiteError("--reps must be at least 1")
        self.configurations = configs(args.host, aliases, args.effort, models)
        self.slots = plan(self.task_ids, self.configurations, reps, args.mode)
        self.runner = self.client = self.view = self.host_version = None

    # ------------------------------------------------------------------ dry run

    def dry_run(self):
        pack = self.run_packs.pack
        self.out("suite3 %s: pack %s %s (%s), %s/%s, %s mode, lang %s" % (
            self.args.suite, pack.id, pack.version, pack.source, self.track, self.tier, self.args.mode,
            self.args.lang))
        self.out("configurations: %s" % "; ".join("%s %s effort %s" % (c.host, c.model, c.effort)
                                                   for c in self.configurations))
        cohort = store.read_json(self.cohort_path, missing_ok=True)
        self.out("cohort: %s" % ("bound %s" % cohort.get("boundAt") if cohort else "not bound yet"))
        counts = collections.Counter()
        minutes = 0.0
        for slot in self.slots:
            state = slot_state(self.ledger, slot)
            counts[state] += 1
            if state in ("todo", "resume", "rerun"):
                minutes += self.timeout_s(slot) / 60.0
            self.out("[%-6s] %s" % (state, describe_slot(slot)))
        self.out("slots: %d (%s); open slots may take up to %.1f h" % (
            len(self.slots), ", ".join("%s %d" % (k, counts[k]) for k in sorted(counts)), minutes / 60.0))
        return 0

    def timeout_s(self, slot):
        minutes = sum(int(self.run_packs.meta(t).get("minutes") or 10) for t in slot_tasks(slot, self.task_ids))
        return max(MIN_TIMEOUT_S, self.args.timeout_factor * minutes * 60.0)

    # ------------------------------------------------------------------ run

    def run(self):
        project = selftest3.require_project(self.args.project)
        self.runner = D.Runner(project, environ=selftest3.runner_environ(self.environ), inproc=self.inproc)
        self.view = D.StateView(project)
        with D.EditorLease(project, "suite3 %s" % self.args.suite, wait_s=self.args.wait_lease, log=self.out):
            self.client = D.ScriptedAgent(self.runner).client
            self.unity_version, self.plugin_version, self.pipeline = self.editor_facts()
            self.gate_packs = open_gate_packs(self.args, self.environ)
            self.preflight()
            docs, docs_hash = freeze_docs(project, self.docs_root)
            self.bind_cohort(docs_hash)
            pack_args = stage_pack(self.pack_option, self.args.suite)
            for slot in self.slots:
                if slot_state(self.ledger, slot) in ("done", "closed"):
                    continue
                self.run_slot(slot, docs, pack_args)
        self.out("suite3 %s: %s" % (self.args.suite, self.summary()))
        return 0

    def editor_facts(self):
        health = self.client.health()
        pipeline = self.client.read("project_get_render_pipeline").get("pipelineType")
        return health.get("unityVersion"), health.get("version"), pipeline

    def bind_cohort(self, docs_hash):
        """Writes the cohort on the first run of a suite; any later run must match it exactly."""
        unity_version, plugin_version, pipeline = self.unity_version, self.plugin_version, self.pipeline
        self.host_version = self.version_probe(self.args.host, agent_environ(self.args.host, self.agent_overrides))
        pack = self.run_packs.pack
        current = {
            "schema": COHORT_SCHEMA, "suite": self.args.suite,
            "pack": {"id": pack.id, "version": pack.version, "manifestSha256": pack.manifest_sha256},
            "pluginVersion": plugin_version, "unityVersion": unity_version, "pipeline": pipeline,
            "gateInputs": {t: selftest3.gate_inputs_hash(self.gate_packs, t, self.gate_inputs) for t in self.task_ids},
            "track": self.track, "tier": self.tier, "mode": self.args.mode, "lang": self.args.lang,
            "configurations": [[c.host, c.model, c.effort] for c in self.configurations],
            "warm": not self.args.cold, "docsTreeHash": docs_hash,
            "hostVersion": self.host_version,
            "timeoutFactor": self.args.timeout_factor, "budgetUsd": self.args.budget_usd}
        previous = store.read_json(self.cohort_path, missing_ok=True)
        if previous is None:
            store.atomic_write_json(self.cohort_path, dict(current, boundAt=store.now_iso()))
            self.out("cohort bound: pack %s %s, plugin %s, Unity %s %s" % (
                pack.id, pack.version, plugin_version, unity_version, pipeline))
        else:
            changed = sorted(k for k in current if previous.get(k) != current[k])
            if changed:
                raise SuiteError("the cohort of suite %s changed (%s); use a new --suite name instead of mixing "
                                 "cohorts" % (self.args.suite, ", ".join(changed)))

    def preflight(self):
        gate = selftest3.load_gate(os.path.abspath(self.args.gate_file) if self.args.gate_file
                                   else selftest3.DEFAULT_GATE_FILE)
        red = []
        for task_id in self.task_ids:
            status = selftest3.gate_status(gate, self.gate_packs, task_id, self.unity_version, self.plugin_version,
                                           self.pipeline, self.gate_inputs)
            if not status["green"]:
                red.append("%s: %s" % (task_id, status["reason"]))
        if red and not self.args.force:
            raise SuiteError("gate3 is not green (Unity %s, %s, plugin %s): %s; run selftest3 first or pass --force"
                             % (self.unity_version, self.pipeline, self.plugin_version, "; ".join(red)))
        for line in red:
            self.out("warning: gate not green, running anyway (--force): %s" % line)
        current = self.view.current()
        if current and current.get("status") == "active" and not self.owned(current.get("sessionId")):
            raise SuiteError("evaluation session %s is active and belongs to no slot of this suite; abort it first"
                             % current.get("sessionId"))
        if not (current and current.get("status") == "active"):
            checked = self.runner.run("check", "--track", self.track, "--tier", self.tier, "--tasks",
                                      ",".join(self.task_ids), *self.run_packs.pack_args, session=False)
            if not checked.ok:
                raise SuiteError("check: %s" % checked.describe())

    def owned(self, session_id):
        return any(e.get("sessionId") == session_id for e in self.ledger.entries() if e.get("kind") == "session")

    def summary(self):
        counts = collections.Counter(slot_state(self.ledger, s) for s in self.slots)
        return ", ".join("%s %d" % (k, counts[k]) for k in sorted(counts))

    # ------------------------------------------------------------------ one slot

    def run_slot(self, slot, docs, pack_args):
        """Agents for the slot until its session ended: a strict session the runner still holds open (it re-issues a
        task after an infrastructure failure) gets another agent, at most MAX_LAUNCHES; then the driver aborts it."""
        key = slot_key(slot)
        for number in range(MAX_LAUNCHES):
            state = slot_state(self.ledger, slot)
            if state in ("done", "closed"):
                return
            sid = self.ledger.of(key, "session")[-1]["sessionId"] if state == "resume" else None
            current = self.view.current()
            active = current.get("sessionId") if current and current.get("status") == "active" else None
            if sid and active != sid:            # ended while the driver was away: only its outcome is missing
                self.finish_session(slot, sid)
                continue
            if not sid and active:
                raise SuiteError("evaluation session %s is still active before slot %s" % (active, key))
            if sid:
                self.close_leftovers(slot, sid)
                if self.session_ended(sid):
                    self.finish_session(slot, sid)
                    continue
            D.wait_quiet(self.client, timeout_s=self.args.wait_quiet, log=self.out)
            run, launched_at = self.launch(slot, docs, pack_args)
            sid = sid or self.bind_session(slot, launched_at)
            if sid is None:
                checked = self.runner.run("check", "--track", self.track, "--tier", self.tier, session=False)
                raise SuiteError("the agent of slot %s started no session (%s); check: %s" % (
                    key, run.get("hostError") or "exit %s" % run.get("returncode"), checked.describe()))
            verdict, recorded = self.close_and_record(slot, sid, run, launched_at)
            if not self.session_ended(sid) and (slot.task is None or number == MAX_LAUNCHES - 1):
                self.runner.session = sid
                self.runner.run("abort", "--reason", "driver", "--launcher", "batch")
            if self.session_ended(sid):
                self.finish_session(slot, sid)
            self.stop_if_needed(slot, run, verdict, recorded)

    def launch(self, slot, docs, pack_args):
        key, config = slot_key(slot), slot.config
        launch_id = store.stamp_id("l")
        out_dir = os.path.join(self.suite_dir, key, launch_id)
        os.makedirs(out_dir)
        sandbox, docs_copy, docs_parent = S1.neutral_dirs(docs)
        declared = os.path.join(sandbox, "declared.json")
        given = {"host": {"value": DECLARED_HOSTS[config.host], "source": "batch"},
                 "model": {"value": config.model, "source": "batch"},
                 "effort": {"value": config.effort, "source": "batch"}}
        if self.host_version and not self.host_version.startswith("unavailable"):
            given["hostVersion"] = {"value": self.host_version, "source": "batch"}
        with open(declared, "w", encoding="utf-8") as handle:
            json.dump(given, handle)
        start = [runner_prompts.python_for_line(), "-B", os.path.join(docs_copy, "scripts", "unity_eval.py"),
                 "start", "--project", self.runner.project, "--launcher", "batch", "--track", self.track,
                 "--tier", self.tier, "--tasks", ",".join(slot_tasks(slot, self.task_ids)), "--mode", self.args.mode,
                 "--lang", self.args.lang, "--declared", declared] + list(pack_args)
        prompt = START_PROMPT[self.args.lang].format(start=runner_prompts.render_command(start))
        tasks = len(slot_tasks(slot, self.task_ids))
        environ = agent_environ(config.host, self.agent_overrides)
        with open(os.path.join(out_dir, "subject.prompt.txt"), "w", encoding="utf-8") as handle:
            handle.write(prompt)
        launched_at = store.now_iso()
        self.out("[run ] %s (%s)" % (describe_slot(slot), launch_id))
        try:
            with FocusKeeper(self.client, getattr(self.client.instance, "pid", None), environ=self.environ):
                run = self.launcher(prompt, config, docs_copy, sandbox, out_dir, self.timeout_s(slot),
                                    self.args.budget_usd * tasks, not self.args.cold, environ)
        finally:
            outputs = S1._collect_outputs(sandbox, os.path.join(out_dir, "subject_outputs"))
            shutil.rmtree(sandbox, ignore_errors=True)
            shutil.rmtree(docs_parent, ignore_errors=True)
        run.update(launchId=launch_id, launchedAt=launched_at, endedAt=store.now_iso(), outputs=outputs)
        run["threadId"] = stream_ids(run)
        self.ledger.append("launch", slot=key, launchId=launch_id, launchedAt=launched_at, endedAt=run["endedAt"],
                           returncode=run.get("returncode"), timedOut=run.get("timedOut"),
                           wallSeconds=run.get("wallSeconds"), hostError=run.get("hostError"),
                           hostSessionId=run["threadId"], dir=os.path.relpath(out_dir, self.suite_dir))
        store.atomic_write_json(os.path.join(out_dir, "launch.json"), dict({"schema": 1}, **run))
        return run, launched_at

    def bind_session(self, slot, launched_at):
        """The runner session this slot's agent started: the current one when it is batch, new and has the slot's
        selection."""
        current = self.view.current()
        if not current or not current.get("sessionId") or self.owned(current["sessionId"]):
            return None
        session = self.view.session(current["sessionId"])
        if not session or session.get("launcher") != "batch" or session.get("createdAt", "") < launched_at or \
                session["selection"]["tasks"] != slot_tasks(slot, self.task_ids):
            return None
        self.ledger.append("session", slot=slot_key(slot), sessionId=session["sessionId"])
        return session["sessionId"]

    def session_ended(self, sid):
        session = self.view.session(sid)
        return None if session is None else session.get("status") != "active"

    def close_attempt(self, sid, aid, state):
        """(state, result) once the attempt is scored: an attempt left ready is submitted without an answer (as the
        driver), a pending verification is retried once; still pending stops the run."""
        self.runner.session = sid
        if state.get("phase") == "ready":
            self.runner.run("submit", "--attempt", aid, "--no-answer", "--launcher", "batch")
            state = self.view.state(sid, aid)
        if state.get("phase") == "pending_verification":
            retried = self.runner.run("submit", "--attempt", aid, "--retry-verify", "--launcher", "batch")
            state = self.view.state(sid, aid)
            if state.get("phase") == "pending_verification":
                raise SuiteError("verification of attempt %s is pending (%s); make the private verifier reachable, "
                                 "then run suite3 again" % (aid, retried.describe()))
        return state, self.view.result(sid, aid)

    def known_attempts(self):
        return {e.get("attemptId") for e in self.ledger.entries() if e.get("kind") == "attempt"}

    def record_attempt(self, slot, sid, task_id, aid, result, status, reasons, usage, launch_id):
        self.ledger.append("attempt", slot=slot_key(slot), rep=slot.rep, task=task_id, host=slot.config.host,
                           model=slot.config.model, effort=slot.config.effort, sessionId=sid, attemptId=aid,
                           runnerStatus=result.get("status"), status=status, reasons=reasons,
                           submittedBy=result.get("submittedBy"), usage=usage, launchId=launch_id)
        self.out("       %s %s: %s%s" % (task_id, aid, status, " (%s)" % "; ".join(reasons) if reasons else ""))

    def close_and_record(self, slot, sid, run, launched_at):
        """Closes what the agent left open and records one ledger line per attempt created during this launch, with
        the host's usage and verdict. Returns (host verdict, recorded attempt ids)."""
        session = self.view.session(sid)
        verdict = host_verdict(run, getattr(self.client.instance, "port", None))
        known, recorded = self.known_attempts(), []
        for task_id in session["order"]:
            for aid in session["tasks"][task_id]["attempts"]:
                state = self.view.state(sid, aid)
                if aid in known or not state or (state.get("createdAt") or "") < launched_at:
                    continue
                state, result = self.close_attempt(sid, aid, state)
                if result is None:
                    continue
                usage = self.record_usage(run, sid, aid, state)
                status, reasons = result.get("status"), list(result.get("reasons") or [])
                if verdict[0] and status in C1.MODEL_OUTCOMES:
                    status, reasons = verdict[0], verdict[1] + reasons
                elif verdict[1]:
                    reasons = reasons + verdict[1]
                self.record_attempt(slot, sid, task_id, aid, result, status, reasons, usage, run.get("launchId"))
                recorded.append(aid)
        return verdict, recorded

    def close_leftovers(self, slot, sid):
        """Attempts of a resumed session that an interrupted run left open or unrecorded: closed and recorded with the
        runner's own classification (their agent's output is not judged again)."""
        session = self.view.session(sid)
        known = self.known_attempts()
        for task_id in session["order"]:
            for aid in session["tasks"][task_id]["attempts"]:
                state = self.view.state(sid, aid)
                if aid in known or not state or state.get("phase") in ("building", None):
                    continue
                state, result = self.close_attempt(sid, aid, state)
                if result is not None:
                    self.record_attempt(slot, sid, task_id, aid, result, result.get("status"),
                                        list(result.get("reasons") or []) + ["recorded_after_restart"],
                                        self.runner_usage(sid, aid), None)

    def runner_usage(self, sid, aid):
        usage = (self.view.result(sid, aid) or {}).get("usage") or {}
        return {"source": usage.get("source"), "processed": (usage.get("tokens") or {}).get("processed")}

    def record_usage(self, run, sid, aid, state):
        """The host's own count for the attempt window, handed to the runner (usage --record); the runner's
        log-based usage stays when the host output has no message in the window."""
        record = host_usage(run, state.get("issuedAt"), state.get("submittedAt"))
        if record is None:
            return self.runner_usage(sid, aid)
        path = os.path.join(self.suite_dir, "usage", aid + ".json")
        os.makedirs(os.path.dirname(path), exist_ok=True)
        store.atomic_write_json(path, record)
        self.runner.session = sid
        recorded = self.runner.run("usage", "--attempt", aid, "--record", path, "--launcher", "batch")
        if not recorded.ok:
            self.out("warning: usage --record %s: %s" % (aid, recorded.describe()))
            return self.runner_usage(sid, aid)
        return self.runner_usage(sid, aid)

    def finish_session(self, slot, sid):
        """One outcome line per task of the slot once its session ended: the task's last attempt."""
        session = self.view.session(sid)
        if session is None or session.get("status") == "active":
            return
        entries = {e.get("attemptId"): e for e in self.ledger.entries() if e.get("kind") == "attempt"}
        for task_id in slot_tasks(slot, self.task_ids):
            attempts = session["tasks"].get(task_id, {}).get("attempts") or []
            last = attempts[-1] if attempts else None
            entry = entries.get(last) or {}
            if last and not entry:
                result = self.view.result(sid, last) or {}
                entry = {"status": result.get("status") or "invalid", "reasons": result.get("reasons") or []}
            status = entry.get("status") or ("cancelled" if session.get("status") == "aborted" else "invalid")
            self.ledger.append("outcome", slot=slot_key(slot), task=task_id, sessionId=sid, attemptId=last,
                               status=status, reasons=entry.get("reasons") or ([] if last else ["never_issued"]))

    def stop_if_needed(self, slot, run, verdict, recorded):
        """v1's suite stops: Codex isolation, model mismatch, a host process that may still write, failed teardown."""
        reasons = verdict[1]
        if run.get("host") == "codex" and run.get("isolationVerified") is False:
            raise SuiteError("Codex isolation preflight failed (%s): stopping the suite" % run.get("hostError"))
        if any(r.startswith("model_mismatch") for r in reasons):
            raise SuiteError("model mismatch in slot %s: %s" % (slot_key(slot), "; ".join(reasons)))
        if run.get("timedOut") and not (run.get("processCleanup") or {}).get("ok", True):
            raise SuiteError("the agent of slot %s may still be running: %s" % (
                slot_key(slot), "; ".join(run["processCleanup"].get("errors") or [])))
        sid = self.ledger.of(slot_key(slot), "session")[-1]["sessionId"]
        for aid in recorded:
            result = self.view.result(sid, aid) or {}
            if (result.get("teardown") or {}).get("ok") is False:
                raise SuiteError("teardown failed after %s: %s" % (aid, result["teardown"].get("error")))


# ---------------------------------------------------------------- command line


def run_from_args(args, gate_inputs, models, out=echo, environ=None, inproc=False, launcher=launch_host,
                  runs_dir=RUNS_DIR, agent_overrides=None, version_probe=host_version):
    """suite3 entry used by eval/cli.py (and the tests); returns the exit code."""
    try:
        suite = Suite(args, gate_inputs, models, out=out, environ=environ, inproc=inproc, launcher=launcher,
                      runs_dir=runs_dir, agent_overrides=agent_overrides, version_probe=version_probe)
        return suite.dry_run() if args.dry_run else suite.run()
    except (SuiteError, selftest3.SelftestError, D.EditorBusy) as exc:
        out("suite3: %s" % exc)
        return ENVIRONMENT_EXIT
    except (ProblemError, RestError) as exc:
        detail = "; ".join(p["fix"] for p in exc.problems) if isinstance(exc, ProblemError) else str(exc)
        out("suite3: the editor is not usable: %s" % detail)
        return ENVIRONMENT_EXIT

# Producer:Betsy
