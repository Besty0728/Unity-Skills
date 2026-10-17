"""cli.py / run_eval.py: one-line ASCII envelope, exit codes, argparse errors, panel result files (contract 2.2, 2.3)."""
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

from .runner_support import EVAL_DIR, Bench, cli, store

RUN_EVAL = os.path.join(EVAL_DIR, "run_eval.py")


class EnvelopeTests(unittest.TestCase):
    def setUp(self):
        self.b = Bench(self)

    def test_argparse_errors_are_bad_arguments(self):
        for argv in (["bogus"], [], ["submit"], ["start", "--mode", "turbo"], ["status", "--launcher", "panel"],
                     ["check", "--pack-dir", "a", "--private-zip", "b"], ["status", "-h"]):
            out = _Buf()
            code = cli.main(argv + ["--project", self.b.project], environ=self.b.env, stdout=out)
            env = json.loads(out.getvalue())
            self.assertEqual(code, 3, argv)
            self.assertEqual(env["problems"][0]["code"], "BAD_ARGUMENTS", argv)
            self.assertFalse(env["ok"])
            self.assertEqual(env["command"], argv[0] if argv and argv[0] in cli.COMMANDS else None)

    def test_unexpected_exception_is_internal_error_with_traceback_in_log_only(self):
        with mock.patch("usk_eval.session.cmd_status", side_effect=RuntimeError("kaboom")):
            code, env = self.b.run("status")
        self.assertEqual(code, 4)
        issue = env["problems"][0]
        self.assertEqual(issue["code"], "INTERNAL_ERROR")
        self.assertNotIn("kaboom", json.dumps(env))
        with open(issue["args"][0], "r", encoding="utf-8") as handle:
            log = handle.read()
        self.assertIn("kaboom", log)
        self.assertIn("    Traceback", log)

    def test_exit_code_is_the_maximum(self):
        from usk_eval.problems import exit_code, problem
        self.assertEqual(exit_code([problem("SCENE_DIRTY", ["x"]), problem("REST_ERROR", ["a", "b"])]), 4)
        self.assertEqual(exit_code([]), 0)

    def test_status_without_session(self):
        code, env = self.b.run("status")
        self.assertEqual((code, env["problems"][0]["code"]), (3, "NO_ACTIVE_SESSION"))
        self.assertIsNone(env["next"])

    def test_corrupt_state_is_reported(self):
        os.makedirs(self.b.paths.root, exist_ok=True)
        with open(self.b.paths.current, "w") as handle:
            handle.write('{"sessionId": "s1"}')
        code, env = self.b.run("status")
        self.assertEqual((code, env["problems"][0]["code"]), (4, "STATE_CORRUPT"))
        with open(self.b.paths.current, "w") as handle:
            handle.write('{"schema": 7, "sessionId": "s1"}')
        code, env = self.b.run("status")
        self.assertEqual((code, env["problems"][0]["code"]), (4, "SCHEMA_UNSUPPORTED"))

    def test_stray_prints_never_reach_stdout(self):
        def noisy(rt):
            print("hello from a task module")
            sys.stderr.write("warning text\n")
            from usk_eval.problems import Outcome
            return Outcome({"x": 1})

        with mock.patch("usk_eval.session.cmd_status", side_effect=noisy):
            out = _Buf()
            code = cli.main(["status", "--project", self.b.project], environ=self.b.env, stdout=out)
        self.assertEqual(code, 0)
        self.assertEqual(out.getvalue().count("\n"), 1)
        self.assertEqual(json.loads(out.getvalue())["data"], {"x": 1})
        with open(self.b.paths.log, "r", encoding="utf-8") as handle:
            self.assertIn("output: hello from a task module", handle.read())

    def test_panel_mode_writes_result_file_not_stdout(self):
        result = os.path.join(self.b.paths.panel, "cmd-20261017T030405123-abcdef.result.json")
        out = _Buf()
        code = cli.main(["status", "--project", self.b.project, "--launcher", "panel", "--result-file", result,
                         "--cmd-id", "cmd-20261017T030405123-abcdef"], environ=self.b.env, stdout=out)
        self.assertEqual(code, 3)
        self.assertEqual(out.getvalue(), "")
        with open(result, "rb") as handle:
            raw = handle.read()
        raw.decode("ascii")
        data = json.loads(raw)
        self.assertEqual((data["schema"], data["exitCode"], data["cmdId"]), (1, 3, "cmd-20261017T030405123-abcdef"))
        self.assertEqual(data["problems"][0]["code"], "NO_ACTIVE_SESSION")
        self.assertRegex(data["finishedAt"], r"Z$")


class PanelCommandLineTests(unittest.TestCase):
    """The exact command lines the Evaluation tab sends (contract 7.6), with its global arguments appended."""

    PANEL = ["--project", "/p", "--launcher", "panel", "--result-file", "/p/r.json", "--log-file", "/p/r.log",
             "--cmd-id", "cmd-20261017T030405123-abcdef"]
    LINES = (
        ["check", "--track", "ops", "--tier", "basic"],
        ["start", "--track", "ops", "--tier", "basic", "--mode", "strict", "--lang", "zh-CN", "--declared", "/p/d.json"],
        ["start", "--track", "dev", "--tier", "standard", "--mode", "suite", "--lang", "en", "--declared", "/p/d.json",
         "--no-usage"],
        ["submit", "--session", "s1", "--attempt", "0a1b2c3d4e5f", "--answer", "/p/a.json"],
        ["submit", "--session", "s1", "--attempt", "0a1b2c3d4e5f", "--no-answer"],
        ["submit", "--session", "s1", "--attempt", "0a1b2c3d4e5f", "--retry-verify"],
        ["abort", "--session", "s1", "--reason", "user"],
        ["report", "--session", "s1"],
        ["usage", "--session", "s1", "--attempt", "0a1b2c3d4e5f", "--manual", "input=10", "output=2"],
        ["cleanup", "--stale"],
    )

    def test_every_panel_line_parses(self):
        for line in self.LINES:
            args = cli.parse(line + self.PANEL)
            self.assertEqual((args.command, args.launcher, args.cmd_id), (line[0], "panel", self.PANEL[-1]), line)
        usage = cli.parse(self.LINES[8] + self.PANEL)
        self.assertEqual((usage.manual, usage.session, usage.project), (["input=10", "output=2"], "s1", "/p"))
        start = cli.parse(self.LINES[2] + self.PANEL)
        self.assertEqual((start.no_usage, start.lang, start.mode, start.declared), (True, "en", "suite", "/p/d.json"))


class _Buf(object):
    def __init__(self):
        self.parts = []

    def write(self, text):
        self.parts.append(text)

    def flush(self):
        pass

    def getvalue(self):
        return "".join(self.parts)


class SubprocessTests(unittest.TestCase):
    """The real entry point: python -B run_eval.py, as agents and the panel launch it."""

    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.env = dict(os.environ, USK_EVAL_REGISTRY=os.path.join(self.tmp, "none.json"), HOME=self.tmp,
                        PYTHONDONTWRITEBYTECODE="1")

    def launch(self, *argv):
        return subprocess.run([sys.executable, "-B", RUN_EVAL] + list(argv), cwd=self.tmp, env=self.env,
                              capture_output=True, text=True, timeout=120)

    def test_agent_mode_prints_exactly_one_ascii_line(self):
        proc = self.launch("start", "--lang", "zh-CN", "--project", self.tmp)
        self.assertEqual(proc.returncode, 2)
        self.assertEqual(proc.stderr, "")
        self.assertEqual(proc.stdout.count("\n"), 1)
        proc.stdout.encode("ascii")
        envelope = json.loads(proc.stdout)
        self.assertEqual(set(envelope), {"ok", "command", "data", "problems", "next"})
        self.assertEqual(envelope["problems"][0]["code"], "PROJECT_NOT_FOUND")

    def test_panel_mode_keeps_stdout_and_stderr_empty(self):
        result = os.path.join(self.tmp, "r.result.json")
        log = os.path.join(self.tmp, "r.log")
        proc = self.launch("bogus-command", "--launcher", "panel", "--result-file", result, "--log-file", log,
                           "--cmd-id", "cmd-1")
        self.assertEqual((proc.returncode, proc.stdout, proc.stderr), (3, "", ""))
        with open(result, "r", encoding="ascii") as handle:
            data = json.load(handle)
        self.assertEqual((data["exitCode"], data["cmdId"], data["problems"][0]["code"]), (3, "cmd-1", "BAD_ARGUMENTS"))

    def test_no_bytecode_is_written(self):
        self.launch("status", "--project", self.tmp)
        found = [root for root, dirs, _files in os.walk(EVAL_DIR) if "__pycache__" in dirs]
        self.assertEqual(found, [])


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
