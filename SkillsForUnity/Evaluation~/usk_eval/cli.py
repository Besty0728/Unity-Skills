"""Runner CLI (contract 2): argparse whose errors also become envelopes, dispatch, exit codes, panel result file."""
import argparse
import io
import json
import os
import sys
import traceback

from . import package_version, submodule
from . import attempt
from . import instance as I
from . import paths as P
from . import prompts
from . import rest
from . import session
from . import store
from .lock import Lock
from .problems import Outcome, ProblemError, exit_code, problem, store_problem

COMMANDS = ("check", "start", "next", "submit", "status", "abort", "report", "usage", "cleanup")
LAUNCHERS = ("agent", "panel", "batch")
LOCK_WAIT_S = 120.0
LOCATE_WAIT_S = 120.0
CLIENT_RETRY_S = 600.0                      # reloads and long imports answer 503 or nothing for a while
CHECK_RETRY_S = 30.0
COUNTED_COMMANDS = ("check", "cleanup")       # commands that may call REST while an attempt is ready


class ArgError(Exception):
    pass


class _Parser(argparse.ArgumentParser):
    def error(self, message):
        raise ArgError(message)

    def exit(self, status=0, message=None):
        raise ArgError((message or "invalid arguments").strip())


def _parser(command):
    parser = _Parser(prog=command, add_help=False, allow_abbrev=False)
    parser.add_argument("--project")
    parser.add_argument("--session")
    parser.add_argument("--launcher", choices=LAUNCHERS, default="agent")
    parser.add_argument("--result-file", dest="result_file")
    parser.add_argument("--log-file", dest="log_file")
    parser.add_argument("--cmd-id", dest="cmd_id")
    parser.add_argument("--inline", action="store_true")
    if command in ("check", "start"):
        parser.add_argument("--track", choices=session.TRACKS)
        parser.add_argument("--tier", choices=session.TIERS)
        parser.add_argument("--tasks")
    if command in ("check", "start", "submit"):
        group = parser.add_mutually_exclusive_group()
        if command != "submit":
            group.add_argument("--pack-dir", dest="pack_dir")
        group.add_argument("--private-zip", dest="private_zip")
        group.add_argument("--private-dir", dest="private_dir")
    if command == "start":
        parser.add_argument("--mode", choices=session.MODES)
        parser.add_argument("--lang", choices=("zh-CN", "en", "ru"))
        parser.add_argument("--declared")
        parser.add_argument("--seed", type=int)
        parser.add_argument("--no-usage", dest="no_usage", action="store_true")
    if command == "submit":
        parser.add_argument("--attempt", required=True)
        parser.add_argument("--answer")
        parser.add_argument("--no-answer", dest="no_answer", action="store_true")
        parser.add_argument("--retry-verify", dest="retry_verify", action="store_true")
        parser.add_argument("--submitted-by", dest="submitted_by", choices=("agent", "panel", "driver"))
    if command == "abort":
        parser.add_argument("--reason")
    if command == "report":
        parser.add_argument("--format", choices=("json", "md"), default="json")
    if command == "usage":
        parser.add_argument("--attempt", required=True)
        parser.add_argument("--manual", nargs="+")
        parser.add_argument("--record")
    if command == "cleanup":
        parser.add_argument("--stale", action="store_true")
        parser.add_argument("--dry-run", dest="dry_run", action="store_true")
    return parser


def parse(argv):
    if not argv or argv[0] not in COMMANDS:
        raise ArgError("expected a command (%s) first, got %r" % ("|".join(COMMANDS), argv[0] if argv else None))
    args = _parser(argv[0]).parse_args(argv[1:])
    args.command = argv[0]
    if args.launcher == "panel" and not args.result_file:
        raise ArgError("--launcher panel needs --result-file")
    return args


def _sniff(argv, flag):
    """Value of a global flag even when parsing failed (where to put the panel result)."""
    for index, token in enumerate(argv):
        if token == flag and index + 1 < len(argv):
            return argv[index + 1]
        if token.startswith(flag + "="):
            return token.split("=", 1)[1]
    return None


class Runtime(object):
    """Per-command context handed to session.py / attempt.py."""

    def __init__(self, args, environ, project, log, runner_version):
        self.args = args
        self.command = args.command
        self.environ = environ
        self.launcher = args.launcher
        self.project = project
        self.paths = P.StatePaths(project)
        self.log = log
        self.runner_version = runner_version
        self.user_agent = rest.user_agent(runner_version)
        self.requests_at_start = rest.REQUESTS.value
        self._instance = None
        self._instance_error = None
        self._client = None

    def instance(self, wait_s=None, busy_wait_s=None):
        """The verified editor, located once per command; a failed lookup is not retried within the command."""
        if self._instance is None:
            if self._instance_error is not None:
                raise self._instance_error
            try:
                self._instance = I.locate(self.project, self.environ, self.user_agent,
                                          wait_s=LOCATE_WAIT_S if wait_s is None else wait_s,
                                          busy_wait_s=busy_wait_s)
            except ProblemError as exc:
                self._instance_error = exc
                raise
        return self._instance

    def client(self):
        if self._client is None:
            retry = CHECK_RETRY_S if self.command == "check" else CLIENT_RETRY_S
            self._client = rest.Client(self.instance(), agent_ua=self.user_agent, retry_window_s=retry)
        return self._client

    def registry_pid(self):
        entry = P.registry_entry(P.read_registry(P.registry_path(self.environ)), self.project)
        pid = (entry or {}).get("pid")
        return pid if isinstance(pid, int) else (self._instance.pid if self._instance else None)

    def lock(self):
        return Lock(self.paths.lock, self.command, self.launcher, log=self.log, timeout_s=LOCK_WAIT_S)

    def step(self, command, args):
        return prompts.next_step(command, args, self.environ)


def _dispatch(rt):
    table = {"check": session.cmd_check, "start": session.cmd_start, "next": attempt.cmd_next,
             "submit": attempt.cmd_submit, "status": session.cmd_status, "abort": session.cmd_abort,
             "report": session.cmd_report, "usage": session.cmd_usage, "cleanup": session.cmd_cleanup}
    return table[rt.command](rt)


def _ready_attempt_calls_file(rt):
    """attempts/<aid>/runner-calls.jsonl when an attempt is ready (contract 4.5 runnerCalls), else None."""
    try:
        current = session.read_current(rt.paths)
        if not current or current.get("status") != "active" or not current.get("activeAttemptId"):
            return None
        state = session.load_state(rt.paths, current["sessionId"], current["activeAttemptId"])
    except (ProblemError, store.StoreError):
        return None
    if state.get("phase") != "ready":
        return None
    return os.path.join(rt.paths.attempt_dir(state["sessionId"], state["attemptId"]), attempt.CALLS_FILE)


def _cleanup_private_temp(log):
    try:
        submodule("private").cleanup_stale()
    except Exception as exc:
        if not isinstance(exc, ImportError):
            log.warn("private temp cleanup: %s" % type(exc).__name__)


def _run(args, environ, log):
    project = P.locate_project(args.project, environ, os.getcwd())
    paths = P.StatePaths(project)
    log.primary = paths.log
    rt = Runtime(args, environ, project, log, package_version())
    _cleanup_private_temp(log)
    calls_file = _ready_attempt_calls_file(rt) if rt.command in COUNTED_COMMANDS else None
    try:
        return _dispatch(rt)
    finally:
        if calls_file:
            delta = rest.REQUESTS.value - rt.requests_at_start
            if delta > 0:
                try:
                    store.append_jsonl(calls_file, {"schema": 1, "cmd": rt.command, "pid": os.getpid(),
                                                    "calls": delta, "at": store.now_iso()})
                except store.StoreError:
                    pass


def envelope(command, outcome):
    data = outcome.data
    problems = list(outcome.problems)
    return {"ok": not problems, "command": command, "data": data, "problems": problems, "next": outcome.next_step}


def _emit(env, code, args, stdout, log):
    if args is not None and args.launcher == "panel":
        result = {"schema": 1}
        result.update(env)
        result.update({"exitCode": code, "finishedAt": store.now_iso(), "cmdId": args.cmd_id})
        try:
            store.atomic_write_bytes(os.path.abspath(args.result_file),
                                     json.dumps(result, ensure_ascii=True, separators=(",", ":")).encode("ascii"))
        except store.StoreError as exc:
            log.error("result file not written: %s" % exc)
        return
    stdout.write(json.dumps(env, ensure_ascii=True, separators=(",", ":")) + "\n")
    stdout.flush()


class _LogStream(io.TextIOBase):
    """Anything printed during a command (task modules, warnings) goes to the log, never to stdout/stderr."""

    def __init__(self, log):
        io.TextIOBase.__init__(self)
        self._log = log
        self._buffer = ""

    def writable(self):
        return True

    def write(self, text):
        self._buffer += text
        while "\n" in self._buffer:
            line, self._buffer = self._buffer.split("\n", 1)
            if line.strip():
                self._log.info("output: %s" % line)
        return len(text)

    def flush(self):
        if self._buffer.strip():
            self._log.info("output: %s" % self._buffer)
        self._buffer = ""


def main(argv=None, environ=None, stdout=None):
    """Run one command; returns the exit code (run_eval.py passes it to sys.exit)."""
    argv = list(sys.argv[1:] if argv is None else argv)
    environ = os.environ if environ is None else environ
    stdout = stdout or sys.stdout
    command = argv[0] if argv and argv[0] in COMMANDS else None
    log = store.RunLog(command, extra=_sniff(argv, "--log-file"))
    saved = sys.stdout, sys.stderr
    sink = _LogStream(log)
    sys.stdout = sys.stderr = sink
    args = None
    try:
        args = parse(argv)
        outcome = _run(args, environ, log)
    except ArgError as exc:
        outcome = Outcome(None, [problem("BAD_ARGUMENTS", [str(exc)])])
        if _sniff(argv, "--launcher") == "panel" and _sniff(argv, "--result-file"):
            args = argparse.Namespace(launcher="panel", result_file=_sniff(argv, "--result-file"),
                                      cmd_id=_sniff(argv, "--cmd-id"))
    except ProblemError as exc:
        outcome = Outcome(exc.data, exc.problems, exc.next_step)
    except store.StoreError as exc:
        outcome = Outcome(None, [store_problem(exc)])
    except rest.RestError as exc:
        log.error("REST %s: %s" % (type(exc).__name__, exc))
        outcome = Outcome(None, rest.as_problems(exc, log.path))
    except Exception as exc:
        log.error("unexpected %s: %s\n%s" % (type(exc).__name__, exc, traceback.format_exc()))
        outcome = Outcome(None, [problem("INTERNAL_ERROR", [log.path])])
    finally:
        sink.flush()
        sys.stdout, sys.stderr = saved
    env = envelope(command, outcome)
    code = exit_code(env["problems"])
    log.info("exit %d %s" % (code, ",".join(p["code"] for p in env["problems"]) or "ok"))
    _emit(env, code, args, stdout, log)
    return code

# Producer:Betsy
