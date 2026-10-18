"""Scripted agent for the v3 runner: the maintainer-side stand-in for a coding agent (selftest, E2E).

It drives the real runner CLI the way the session prompt tells an agent to (next -> do the work -> write the answer
-> submit) and does the work through the REST API as the SelftestSolver identity (contract 7.3). It is maintainer
tooling and reads runner state freely; the agents under evaluation may not (G6).

Pieces:
  Runner         one runner invocation, as a subprocess (default) or in-process (offline tests)
  StateView      read-only access to Library/UnitySkills/eval
  PackAccess     public task modules, private modules and solutions of the pack a session was started with
  ScriptedAgent  one host session's worth of agent behaviour (start/next/solve/submit/abort, whole sessions)
  EditorLease    advisory lock so two maintainer tools never drive one editor at once
  wait_quiet     waits until the editor has no running job (a Unity Test Runner run, a compile)
"""
import contextlib
import io
import json
import os
import secrets
import signal
import subprocess
import sys
import tempfile
import time
import traceback

sys.dont_write_bytecode = True

HERE = os.path.dirname(os.path.abspath(__file__))
EVAL_DIR = os.path.dirname(HERE)
REPO = os.path.dirname(EVAL_DIR)
RUNNER_DIR = os.path.join(REPO, "SkillsForUnity", "Evaluation~")
RUN_EVAL = os.path.join(RUNNER_DIR, "run_eval.py")
if RUNNER_DIR not in sys.path:
    sys.path.insert(0, RUNNER_DIR)

from usk_eval import instance as runner_instance  # noqa: E402
from usk_eval import journal as runner_journal  # noqa: E402
from usk_eval import pack as runner_pack  # noqa: E402
from usk_eval import package_version  # noqa: E402
from usk_eval import paths as runner_paths  # noqa: E402
from usk_eval import private as runner_private  # noqa: E402
from usk_eval import rest as runner_rest  # noqa: E402
from usk_eval import scope as runner_scope  # noqa: E402
from usk_eval import store  # noqa: E402
from usk_eval.lock import pid_alive  # noqa: E402
from usk_eval.task_api import solver_context  # noqa: E402

# Variables that change how the runner sees its host, the pack or the usage logs; a hermetic run starts without them.
HOST_VARS = ("CLAUDECODE", "CLAUDE_CODE_SESSION_ID", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CONFIG_DIR", "CODEX_THREAD_ID",
             "CODEX_SANDBOX", "CODEX_HOME", "USK_EVAL_PROJECT", "USK_EVAL_ENTRY", "USK_EVAL_PRIVATE_URLS",
             "USK_EVAL_CLAUDE_ROOT", "USK_EVAL_CODEX_ROOT")
TERMINAL_JOB_STATES = runner_rest.TERMINAL_JOB_STATES
LEASE_STALE_S = 4 * 3600.0


class DriverError(RuntimeError):
    pass


class EditorBusy(DriverError):
    pass


class StopSession(Exception):
    """Raised by a hook to end run_session after the current step."""


def clean_environ(base=None, **overrides):
    """A copy of `base` (default os.environ) without host and override variables; a None override deletes the key."""
    env = dict(os.environ if base is None else base)
    for name in HOST_VARS:
        env.pop(name, None)
    for key, value in overrides.items():
        if value is None:
            env.pop(key, None)
        else:
            env[key] = str(value)
    return env


def wait_until(predicate, timeout_s, interval_s=0.05):
    """The first truthy value of predicate() within timeout_s, else None."""
    deadline = time.time() + timeout_s
    while True:
        value = predicate()
        if value:
            return value
        if time.time() >= deadline:
            return None
        time.sleep(interval_s)


# ---------------------------------------------------------------- one runner call


def parse_envelope(text):
    """(envelope or None, clean): clean means stdout held exactly one JSON line."""
    lines = [line for line in (text or "").splitlines() if line.strip()]
    if not lines:
        return None, False
    try:
        envelope = json.loads(lines[-1])
    except ValueError:
        return None, False
    return (envelope if isinstance(envelope, dict) else None), len(lines) == 1


class CallResult(object):
    def __init__(self, argv, code, stdout, stderr, seconds, envelope=None, clean=None):
        self.argv = list(argv)
        self.code = code
        self.stdout = stdout
        self.stderr = stderr
        self.seconds = seconds
        if envelope is None:
            envelope, clean = parse_envelope(stdout)
        self.envelope = envelope
        self.clean = bool(clean) and not (stderr or "").strip()

    @property
    def ok(self):
        return bool(self.envelope and self.envelope.get("ok") is True)

    @property
    def data(self):
        return (self.envelope or {}).get("data") or {}

    @property
    def problems(self):
        return (self.envelope or {}).get("problems") or []

    @property
    def codes(self):
        return [p.get("code") for p in self.problems]

    @property
    def notice_codes(self):
        return [n.get("code") for n in self.data.get("notices") or []]

    @property
    def next(self):
        return (self.envelope or {}).get("next")

    def require(self, *allowed):
        """self when the call is ok or every problem code is in `allowed`; DriverError with the details otherwise."""
        if self.ok or (self.envelope is not None and self.codes and all(c in allowed for c in self.codes)):
            return self
        raise DriverError("%s failed (exit %s): %s" % (" ".join(self.argv[3:6]), self.code, self.describe()))

    def describe(self):
        if self.envelope is None:
            return "no envelope; stdout=%r stderr=%r" % ((self.stdout or "")[:300], (self.stderr or "")[:300])
        return "; ".join("%s: %s" % (p.get("code"), p.get("fix") or p.get("message")) for p in self.problems) or "ok"


class Runner(object):
    """`python -B run_eval.py <command> --project P [--session S] ...` with a controlled environment.

    inproc=True calls usk_eval.cli.main in this process instead (offline tests: no interpreter start-up, and the
    test's patches of runner constants apply).
    """

    def __init__(self, project, entry=None, python=None, environ=None, inproc=False, timeout_s=1800.0, cwd=None,
                 session=None):
        self.project = os.path.realpath(project)
        self.entry = entry or RUN_EVAL
        self.python = python or sys.executable
        self.environ = clean_environ() if environ is None else dict(environ)
        self.inproc = inproc
        self.timeout_s = timeout_s
        self.cwd = cwd
        self.session = session
        self.history = []

    def derive(self, **env_overrides):
        """A runner for the same project and session with some environment variables changed (None deletes one)."""
        other = Runner(self.project, self.entry, self.python, _merge(self.environ, env_overrides), self.inproc,
                       self.timeout_s, self.cwd, self.session)
        return other

    def env(self, overrides=None):
        return _merge(self.environ, overrides or {})

    def tail(self, command, args, session=True):
        out = [command, "--project", self.project]
        if session and self.session:
            out += ["--session", self.session]
        return out + [str(a) for a in args]

    def argv(self, command, *args, **kw):
        return [self.python, "-B", self.entry] + self.tail(command, args, kw.get("session", True))

    def run(self, command, *args, **kw):
        """Run one command and return its CallResult. kw: timeout_s, environ (overrides), session (default True)."""
        tail = self.tail(command, args, kw.get("session", True))
        return self._execute(tail, kw.get("environ"), kw.get("timeout_s"))

    def run_panel(self, command, *args, **kw):
        """Panel launcher: --result-file/--log-file/--cmd-id; stdout and stderr stay empty, the envelope is the file.

        kw: result_file, log_file, cmd_id plus the run() ones.
        """
        result_file, log_file = kw["result_file"], kw["log_file"]
        extra = ["--launcher", "panel", "--result-file", result_file, "--log-file", log_file,
                 "--cmd-id", kw.get("cmd_id") or "cmd-e2e"]
        tail = self.tail(command, list(args) + extra, kw.get("session", True))
        if os.path.exists(result_file):
            os.remove(result_file)
        outcome = self._execute(tail, kw.get("environ"), kw.get("timeout_s"))
        envelope = None
        try:
            with open(result_file, "rb") as handle:
                envelope = json.loads(handle.read().decode("utf-8"))
        except (OSError, ValueError):
            pass
        return CallResult(outcome.argv, outcome.code, outcome.stdout, outcome.stderr, outcome.seconds,
                          envelope=envelope, clean=outcome.stdout.strip() == "")

    def run_argv(self, argv, timeout_s=None, environ=None):
        """An argv an agent was handed (NextStep.argv / submitArgv), run as is."""
        return self._spawn([str(a) for a in argv], environ, timeout_s)

    def run_line(self, line, timeout_s=None, environ=None):
        """A pasteable command line (submitCommand / NextStep.line) run through the shell, as an agent's shell would."""
        started = time.time()
        proc = subprocess.run(line, shell=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE, cwd=self.cwd,
                              env=self.env(environ), timeout=timeout_s or self.timeout_s)
        return self._record(CallResult([line], proc.returncode, proc.stdout.decode("utf-8", "replace"),
                                       proc.stderr.decode("utf-8", "replace"), time.time() - started))

    def popen(self, command, *args, **kw):
        """Start a command without waiting for it (kill scenarios); a new session, so killpg spares the caller."""
        argv = self.argv(command, *args, session=kw.get("session", True))
        options = {"start_new_session": True} if os.name == "posix" else {}
        return subprocess.Popen(argv, stdout=subprocess.PIPE, stderr=subprocess.PIPE, cwd=self.cwd,
                                env=self.env(kw.get("environ")), **options)

    # ------------------------------------------------------------------ transports

    def _execute(self, tail, environ, timeout_s):
        if self.inproc:
            return self._inproc(tail, environ)
        return self._spawn([self.python, "-B", self.entry] + tail, environ, timeout_s)

    def _spawn(self, argv, environ, timeout_s):
        started = time.time()
        proc = subprocess.run(argv, stdout=subprocess.PIPE, stderr=subprocess.PIPE, cwd=self.cwd,
                              env=self.env(environ), timeout=timeout_s or self.timeout_s)
        return self._record(CallResult(argv, proc.returncode, proc.stdout.decode("utf-8", "replace"),
                                       proc.stderr.decode("utf-8", "replace"), time.time() - started))

    def _inproc(self, tail, environ):
        from usk_eval import cli as runner_cli
        started = time.time()
        out = io.StringIO()
        code = runner_cli.main(list(tail), environ=self.env(environ), stdout=out)
        return self._record(CallResult(["inproc", "-B", "run_eval.py"] + tail, code, out.getvalue(), "",
                                       time.time() - started))

    def _record(self, result):
        self.history.append((result.argv[3] if len(result.argv) > 3 else result.argv[0], result.code))
        return result


def _merge(base, overrides):
    env = dict(base)
    for key, value in overrides.items():
        if value is None:
            env.pop(key, None)
        else:
            env[key] = str(value)
    return env


def kill_process(proc, timeout_s=10.0):
    """SIGKILL (the runner gets no chance to clean up) and reap."""
    try:
        if os.name == "posix":
            os.killpg(proc.pid, signal.SIGKILL)
        else:
            proc.kill()
    except (OSError, ProcessLookupError):
        pass
    try:
        proc.communicate(timeout=timeout_s)
    except Exception:
        pass


# ---------------------------------------------------------------- state directory


class StateView(object):
    """Read-only access to Library/UnitySkills/eval (maintainer tooling; agents under test must not read it)."""

    def __init__(self, project):
        self.project = os.path.realpath(project)
        self.paths = runner_paths.StatePaths(self.project)

    def _read(self, path, missing_ok=True):
        for attempt in range(5):
            try:
                return store.read_json(path, missing_ok=missing_ok)
            except store.StoreError:
                if attempt == 4:
                    raise
                time.sleep(0.05)

    def current(self):
        return self._read(self.paths.current)

    def session(self, sid):
        return self._read(self.paths.session_file(sid))

    def state(self, sid, aid):
        return self._read(self.paths.state_file(sid, aid))

    def result(self, sid, aid):
        return self._read(self.paths.result_file(sid, aid))

    def report(self, sid):
        return self._read(os.path.join(self.paths.session_dir(sid), "report.json"))

    def ledger(self):
        return store.read_jsonl(self.paths.ledger)

    def log_text(self, sid=None):
        path = self.paths.session_log(sid) if sid else self.paths.log
        try:
            with open(path, "r", encoding="utf-8") as handle:
                return handle.read()
        except OSError:
            return ""

    def active_state(self):
        """state.json of the active attempt, or None."""
        current = self.current()
        if not current or not current.get("activeAttemptId"):
            return None
        return self.state(current["sessionId"], current["activeAttemptId"])

    def residue(self):
        """Run folders left under Assets/__UnitySkillsEval."""
        from usk_eval import fixture
        owned, foreign = fixture.residue(self.paths.eval_assets)
        return [item["runId"] for item in owned] + list(foreign)

    def wait_state(self, predicate, timeout_s=60.0, interval_s=0.01):
        """state.json of the active attempt as soon as predicate(state) holds (polled; an unreadable file is retried)."""
        def look():
            try:
                state = self.active_state()
            except store.StoreError:
                return None
            return state if state and predicate(state) else None
        return wait_until(look, timeout_s, interval_s)

    def wait_phase(self, phase, timeout_s=60.0, interval_s=0.01):
        return self.wait_state(lambda state: state.get("phase") == phase, timeout_s, interval_s)


def attempt_violations(view, session_id, attempt_id):
    """Scope violations in an attempt's frozen journal, as the verifiers' ctx.violations() sees them."""
    state = view.state(session_id, attempt_id)
    scope = state["scope"]
    path = os.path.join(view.paths.attempt_dir(session_id, attempt_id), "journal.jsonl")
    records = runner_journal.flatten(runner_journal.load_entries(path), state["journal"].get("refreshWindow"))
    records = runner_journal.split_reimports(records, state)[0]
    records = [r for r in records if not r.get("isPlaying")
               and not (r.get("agent") == runner_rest.RUNNER_AGENT and r.get("origin") == "attempt")]
    return runner_scope.scope_violations(records, scope["objectRoot"], scope["assetRoot"], scope["scenePath"])


def foreign_violations(view, session_id, attempt_id):
    """Violation records the solver did not make: another tool's file changes imported by the runner's refresh, edits
    in the editor, other agents. They are what a shared editor adds to an attempt; used to explain a red result, never
    to excuse one."""
    return [r for r in attempt_violations(view, session_id, attempt_id) if r.get("agent") != runner_rest.SOLVER_AGENT]


def describe_record(rec):
    where = rec.get("assetPath") or rec.get("path") or rec.get("toPath") or rec.get("fromPath") or ""
    return "%s %s (%s by %s, %s)" % (rec.get("kind"), where, rec.get("source"), rec.get("agent"), rec.get("origin"))


# ---------------------------------------------------------------- pack, solutions


class Solutions(object):
    """eval/pack/solutions/<ID>.py: reference(ctx), NEGATIVES, ALTERNATIVES (contract 5.6)."""

    def __init__(self, task_id, module, path=None):
        self.task_id = task_id
        self.path = path
        self.reference = getattr(module, "reference", None)
        self.negatives = list(getattr(module, "NEGATIVES", None) or [])
        self.alternatives = list(getattr(module, "ALTERNATIVES", None) or [])

    def problems(self, min_negatives=5, min_alternatives=1):
        """Why this solutions module cannot be gated; [] when it can."""
        found = []
        if not callable(self.reference):
            found.append("reference(ctx) is missing")
        names = set()
        for neg in self.negatives:
            name = getattr(neg, "name", None)
            if not name or not callable(getattr(neg, "apply", None)):
                found.append("negative %r needs a name and a callable apply" % (name,))
            elif not getattr(neg, "expect_failed", None):
                found.append("negative %s declares no expected failed assertions" % name)
            if name in names:
                found.append("negative %s is listed twice" % name)
            names.add(name)
        alt_names = set()
        for item in self.alternatives:
            if not (isinstance(item, (tuple, list)) and len(item) == 2 and item[0] and callable(item[1])):
                found.append("alternative %r must be (name, callable)" % (item,))
            elif item[0] in alt_names:
                found.append("alternative %s is listed twice" % item[0])
            else:
                alt_names.add(item[0])
        if len(self.negatives) < min_negatives:
            found.append("needs at least %d negatives, has %d" % (min_negatives, len(self.negatives)))
        if len(self.alternatives) < min_alternatives:
            found.append("needs at least %d alternative, has %d" % (min_alternatives, len(self.alternatives)))
        return found


def load_solutions(task_id, path):
    name = "usk_eval_solutions_%s_%s" % (task_id, secrets.token_hex(3))
    try:
        module = runner_pack.load_source_module(name, path)
    except (Exception, SystemExit) as exc:
        raise DriverError("solutions %s: %s" % (path, runner_pack.describe_error(exc)))
    return Solutions(task_id, module, path)


class PackAccess(object):
    """The pack a session starts with, opened the way the runner opens it, plus the maintainer-only modules.

    pack_dir / private_zip / private_dir are the runner's own options (at most one); base_dir stands in for
    <pkg>/Evaluation~/pack; solutions_dir holds <ID>.py solution modules.
    """

    def __init__(self, pack_dir=None, private_dir=None, private_zip=None, base_dir=None, solutions_dir=None,
                 environ=None):
        self.pack_dir, self.private_dir, self.private_zip = pack_dir, private_dir, private_zip
        self.pack = runner_pack.open_pack(pack_dir=pack_dir, private_zip=private_zip, private_dir=private_dir,
                                          base_dir=base_dir)
        self.solutions_dir = solutions_dir
        self.environ = environ
        self._private, self._solutions = {}, {}

    @property
    def pack_args(self):
        """The runner options that make a session use this pack."""
        for flag, value in (("--pack-dir", self.pack_dir), ("--private-zip", self.private_zip),
                            ("--private-dir", self.private_dir)):
            if value:
                return [flag, value]
        return []

    def ids(self):
        return self.pack.task_ids()

    def meta(self, task_id):
        return self.pack.meta(task_id)

    def task(self, task_id):
        return self.pack.load_task(task_id)

    def private_module(self, task_id):
        """The private verify module as a plain import, for SolverContext.private (shared derivations)."""
        if task_id not in self._private:
            try:
                fetched = runner_private.fetch(self.pack, task_id, user_agent="unity-skills-eval-selftest",
                                               environ=self.environ)
            except runner_private.PrivateUnavailable as exc:
                raise DriverError("private module of %s unavailable: %s" % (task_id, exc))
            name = "usk_eval_private_driver_%s_%s" % (task_id, secrets.token_hex(3))
            try:
                self._private[task_id] = runner_pack.load_source_module(
                    name, os.path.join(tempfile.gettempdir(), fetched.file), fetched.data)
            except (Exception, SystemExit) as exc:
                raise DriverError("private module of %s: %s" % (task_id, runner_pack.describe_error(exc)))
            finally:
                fetched.discard()
        return self._private[task_id]

    def private_path(self, task_id):
        """Where the task's verifier file lives on disk (private-dir, pack-dir); None for a zip or URL source."""
        name = self.pack.private_entry(task_id)["file"]
        if self.private_dir:
            return os.path.join(self.private_dir, name)
        if self.pack_dir:
            return os.path.join(self.pack_dir, "private", name)
        return None

    def solutions_path(self, task_id):
        return os.path.join(self.solutions_dir or "", task_id + ".py")

    def solutions(self, task_id):
        if task_id not in self._solutions:
            path = self.solutions_path(task_id)
            if not self.solutions_dir or not os.path.isfile(path):
                raise DriverError("no solutions module for %s: %s" % (task_id, path))
            self._solutions[task_id] = load_solutions(task_id, path)
        return self._solutions[task_id]


# ---------------------------------------------------------------- the agent


class Attempt(object):
    """The `next` answer for an issued task."""

    def __init__(self, data):
        self.data = data
        self.session_id = data["sessionId"]
        self.attempt_id = data["attemptId"]
        self.task_id = data["taskId"]
        self.prompt_file = data["promptFile"]
        self.answer_file = data["answerFile"]
        self.submit_command = data["submitCommand"]
        self.submit_argv = data["submitArgv"]
        self.k, self.n = data["k"], data["n"]
        self.reissued = bool(data.get("reissued"))
        self.rerun_of = data.get("rerunOf")

    def prompt_text(self):
        with open(self.prompt_file, "r", encoding="utf-8-sig") as handle:
            return handle.read()


class AttemptRun(object):
    """What one pass of the agent loop did."""

    def __init__(self):
        self.next_result = None
        self.attempt = None
        self.answer = None
        self.solver_error = None
        self.submit_result = None
        self.aborted = None

    @property
    def done(self):
        return bool(self.next_result is not None and self.next_result.ok and self.next_result.data.get("done"))


class ScriptedAgent(object):
    """One host session's worth of agent behaviour against a runner.

    Hooks (all optional, name -> callable): after_next(agent, attempt), before_submit(agent, attempt, answer),
    after_submit(agent, attempt, result). A hook may raise StopSession to end run_session.
    """

    def __init__(self, runner, packs=None, with_private=False, log=None, abort_on_error=True):
        self.runner = runner
        self.packs = packs
        self.with_private = with_private
        self.log = log or (lambda message: None)
        self.abort_on_error = abort_on_error
        self.state = StateView(runner.project)
        self._client = None

    @property
    def client(self):
        """REST client of the project's editor as SelftestSolver (contract 7.3)."""
        if self._client is None:
            env = self.runner.env()
            agent = runner_rest.user_agent(package_version(), solver=True)
            instance = runner_instance.locate(self.runner.project, env, agent, wait_s=120.0)
            self._client = runner_rest.Client(instance, agent_id=runner_rest.SOLVER_AGENT, agent_ua=agent,
                                              retry_window_s=600.0)
        return self._client

    # ------------------------------------------------------------------ runner steps

    def check(self, *args, **kw):
        return self.runner.run("check", *(list(args) + self._pack_args()), session=False, **kw)

    def start(self, *args, **kw):
        """`start` with the pack options of this agent; a started (or already active) session becomes current."""
        result = self.runner.run("start", *(list(args) + self._pack_args()), session=False, **kw)
        if result.ok and result.data.get("sessionId"):
            self.runner.session = result.data["sessionId"]
        return result

    def _pack_args(self):
        return list(self.packs.pack_args) if self.packs is not None else []

    def next(self, **kw):
        """(CallResult, Attempt or None); None when there is no task (done, or a problem)."""
        result = self.runner.run("next", **kw)
        if result.ok and not result.data.get("done"):
            return result, Attempt(result.data)
        return result, None

    def submit(self, attempt, how="answer", extra=(), submitted_by=None, **kw):
        """how: answer (--answer <answerFile>), no-answer, retry (--retry-verify); panel=True uses the panel launcher."""
        args = ["--attempt", attempt.attempt_id]
        if how == "answer":
            args += ["--answer", kw.pop("answer_file", None) or attempt.answer_file]
        elif how == "no-answer":
            args.append("--no-answer")
        elif how == "retry":
            args.append("--retry-verify")
        else:
            raise ValueError("unknown submit mode %r" % how)
        if submitted_by:
            args += ["--submitted-by", submitted_by]
        args += list(extra)
        if kw.pop("panel", False):
            return self.runner.run_panel("submit", *args, **kw)
        return self.runner.run("submit", *args, **kw)

    def abort(self, reason="driver", **kw):
        return self.runner.run("abort", "--reason", reason, **kw)

    def abort_quietly(self, reason="driver_cleanup"):
        """Abort an active session if there is one; never raises (cleanup after a failure or Ctrl-C)."""
        try:
            current = self.state.current()
            if current and current.get("status") == "active":
                self.runner.session = current["sessionId"]
                return self.abort(reason)
        except Exception as exc:
            self.log("abort_quietly: %s: %s" % (type(exc).__name__, exc))
        return None

    # ------------------------------------------------------------------ doing the work

    def context(self, attempt):
        """SolverContext of an issued attempt, rebuilt from session.json and state.json (contract 5.7)."""
        if self.packs is None:
            raise DriverError("a PackAccess is needed to build a solver context")
        session = self.state.session(attempt.session_id)
        state = self.state.state(attempt.session_id, attempt.attempt_id)
        private = self.packs.private_module(attempt.task_id) if self.with_private else None
        return solver_context(self.client, session, state, self.packs.task(attempt.task_id), self.runner.project,
                              private=private, log=self.log)

    def write_answer(self, attempt, answer):
        """answer: dict/list (JSON), str (written verbatim, e.g. broken JSON), None (no file). Returns the path."""
        if answer is None:
            return None
        os.makedirs(os.path.dirname(attempt.answer_file), exist_ok=True)
        text = answer if isinstance(answer, str) else json.dumps(answer, ensure_ascii=False)
        with open(attempt.answer_file, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(text)
        return attempt.answer_file

    def run_attempt(self, solver, how=None, submitted_by=None, hooks=None, submit_kw=None):
        """One pass: next -> solver(ctx) -> answer file -> submit.

        solver is callable(ctx) -> answer, {taskId: callable} or None (submit without doing the work). A solver that
        raises leaves AttemptRun.solver_error and (abort_on_error) aborts the session instead of submitting, so the
        editor is never left in a half-done attempt.
        """
        hooks = hooks or {}
        run = AttemptRun()
        run.next_result, run.attempt = self.next()
        if run.attempt is None:
            return run
        attempt = run.attempt
        work = solver.get(attempt.task_id) if isinstance(solver, dict) else solver
        try:
            if hooks.get("after_next"):
                hooks["after_next"](self, attempt)
            if work is not None:
                run.answer = work(self.context(attempt))
        except StopSession:
            raise
        except Exception:
            run.solver_error = traceback.format_exc()
            self.log("solver failed for %s: %s" % (attempt.task_id, run.solver_error.strip().splitlines()[-1]))
            if self.abort_on_error:
                run.aborted = self.abort("driver_solver_error")
            return run
        written = self.write_answer(attempt, run.answer)
        if hooks.get("before_submit"):
            hooks["before_submit"](self, attempt, run.answer)
        run.submit_result = self.submit(attempt, how or ("answer" if written else "no-answer"),
                                        submitted_by=submitted_by, **(submit_kw or {}))
        if hooks.get("after_submit"):
            hooks["after_submit"](self, attempt, run.submit_result)
        return run

    def run_session(self, solver, hooks=None, max_tasks=100, **kw):
        """The session prompt's loop (contract 6.4): next, do the task, submit, until `done`, a problem, or stop.

        Strict mode ends after one submit (submit answers stop). A StopSession from a hook ends the loop. Returns
        the AttemptRun list; when the session finished, the last run holds the `done` answer.
        """
        runs = []
        for _ in range(max_tasks):
            try:
                run = self.run_attempt(solver, hooks=hooks, **kw)
            except StopSession:
                break
            runs.append(run)
            if run.attempt is None or run.solver_error or run.submit_result is None or \
                    not run.submit_result.ok or run.submit_result.data.get("stop"):
                break
        return runs


# ---------------------------------------------------------------- sharing one editor


class EditorLease(object):
    """Advisory lock <project>/Library/UnitySkills/driver.lock: one maintainer tool (selftest, E2E) per project.

    The runner's own lock is held per command only; a driver works between commands, so two drivers on one editor
    would start sessions under each other. Stale when the owner pid is gone or the lease is older than 4 hours.
    """

    def __init__(self, project, owner, wait_s=0.0, poll_s=2.0, log=None):
        self.path = os.path.join(os.path.realpath(project), "Library", "UnitySkills", "driver.lock")
        self.owner, self.wait_s, self.poll_s = owner, wait_s, poll_s
        self.log = log or (lambda message: None)
        self.held = False
        self._token = None

    def _read(self):
        try:
            with open(self.path, "r", encoding="utf-8") as handle:
                info = json.load(handle)
            return info if isinstance(info, dict) else None
        except (OSError, ValueError):
            return None

    def _stale(self, info):
        if info is None:
            try:
                return time.time() - os.path.getmtime(self.path) > 10.0
            except OSError:
                return False
        if not pid_alive(info.get("pid")):
            return True
        try:
            return time.time() - store.epoch(info.get("startedAt")) > LEASE_STALE_S
        except (ValueError, TypeError):
            return False

    def acquire(self):
        os.makedirs(os.path.dirname(self.path), exist_ok=True)
        deadline = time.time() + self.wait_s
        self._token = secrets.token_hex(4)
        content = {"schema": 1, "pid": os.getpid(), "owner": self.owner, "startedAt": store.now_iso(),
                   "token": self._token}
        announced = False
        while True:
            try:
                fd = os.open(self.path, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o644)
            except FileExistsError:
                info = self._read()
                if self._stale(info):
                    aside = "%s.stale-%d-%s" % (self.path, os.getpid(), secrets.token_hex(3))
                    try:
                        os.rename(self.path, aside)
                        os.remove(aside)
                    except OSError:
                        pass
                    continue
                if time.time() >= deadline:
                    raise EditorBusy("the editor of %s is in use by %s (pid %s since %s)" % (
                        os.path.dirname(os.path.dirname(os.path.dirname(self.path))), (info or {}).get("owner"),
                        (info or {}).get("pid"), (info or {}).get("startedAt")))
                if not announced:
                    self.log("waiting for %s (pid %s) to finish with the editor" % (
                        (info or {}).get("owner"), (info or {}).get("pid")))
                    announced = True
                time.sleep(self.poll_s)
                continue
            with os.fdopen(fd, "w", encoding="utf-8") as handle:
                json.dump(content, handle)
            self.held = True
            return self

    def release(self):
        if not self.held:
            return
        self.held = False
        info = self._read()
        if info and info.get("token") == self._token:
            with contextlib.suppress(OSError):
                os.remove(self.path)

    def __enter__(self):
        return self.acquire()

    def __exit__(self, *exc):
        self.release()
        return False


def active_jobs(client, stale_s=900.0):
    """Editor jobs that have not finished (a Unity Test Runner run is kind `test`, a compile kind `compile`).

    A job not updated for stale_s seconds is a leftover of a crashed run (its record never reached a final state),
    not something to wait for.
    """
    listing = client.get("/jobs", {"limit": 100})
    now = time.time()
    return [job for job in listing.get("jobs") or []
            if job.get("status") not in TERMINAL_JOB_STATES
            and not (isinstance(job.get("updatedAt"), (int, float)) and now - job["updatedAt"] > stale_s)]


def wait_quiet(client, timeout_s=900.0, poll_s=5.0, log=None):
    """Block until the editor runs no job and is not compiling; EditorBusy after timeout_s. Returns the kinds waited on."""
    log = log or (lambda message: None)
    deadline = time.time() + timeout_s
    seen = []
    interval = min(poll_s, 1.0)         # look again soon at first: most waits (a compile) are short
    while True:
        busy = active_jobs(client)
        health = client.health()
        comp = health.get("compilation") or {}
        if comp.get("isCompiling") or comp.get("isUpdating") or health.get("domainReloadPending"):
            busy = busy + [{"kind": "editor-busy", "status": "compiling"}]
        if not busy:
            return seen
        kinds = sorted(set("%s:%s" % (j.get("kind"), j.get("status")) for j in busy))
        if kinds != seen:
            log("editor is busy (%s); waiting" % ", ".join(kinds))
            seen = kinds
        if time.time() >= deadline:
            raise EditorBusy("the editor still runs: %s" % ", ".join(kinds))
        time.sleep(interval)
        interval = min(poll_s, interval * 2)

# Producer:Betsy
