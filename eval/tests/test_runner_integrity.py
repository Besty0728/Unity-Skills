"""Focused offline integrity tests for run identity, snapshots and cleanup classification."""
import json
import os
import sys
import tempfile
import unittest
from contextlib import ExitStack
from unittest import mock

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

from harness import env as E  # noqa: E402
from harness import runner as R  # noqa: E402
from harness import verify as V  # noqa: E402


class TreeHashTests(unittest.TestCase):
    def test_python_cache_files_do_not_change_tree_hash(self):
        with tempfile.TemporaryDirectory() as root:
            with open(os.path.join(root, "task.py"), "wb") as handle:
                handle.write(b"value = 1\n")
            before = E.tree_hash(root)
            cache = os.path.join(root, "__pycache__")
            os.mkdir(cache)
            with open(os.path.join(cache, "task.cpython-313.pyc"), "wb") as handle:
                handle.write(b"compiled")
            with open(os.path.join(root, "other.pyc"), "wb") as handle:
                handle.write(b"compiled")
            with open(os.path.join(root, "other.pyo"), "wb") as handle:
                handle.write(b"optimized")
            self.assertEqual(E.tree_hash(root), before)


class CleanupOutcomeTests(unittest.TestCase):
    def test_scored_results_become_invalid_when_cleanup_fails(self):
        for status in ("pass", "partial", "fail"):
            with self.subTest(status=status):
                new_status, reasons, error = R._cleanup_outcome(status, ["existing"], {"error": "teardown"})
                self.assertEqual(new_status, "invalid")
                self.assertEqual(reasons, ["existing", "cleanup_error: teardown"])
                self.assertEqual(error, "teardown")

    def test_non_scored_status_and_reason_are_preserved(self):
        self.assertEqual(R._cleanup_outcome("unsupported", ["problem"], {"error": "teardown"}),
                         ("unsupported", ["problem"], "teardown"))


class RunReportTests(unittest.TestCase):
    def test_host_is_forwarded_to_subject_and_metrics_and_recorded(self):
        class Instance:
            instance_id = "eval_instance"
            port = 8091

            def as_dict(self):
                return {"instanceId": self.instance_id}

        class Client:
            is_read_only = lambda self, skill: False

            @staticmethod
            def wait_idle(timeout_s):
                return None

        class Task:
            id = "T"
            case_version = 1
            timeout_s = 10
            budget_usd = 1
            layer_pairs = []

            @staticmethod
            def scorecard():
                return V.Scorecard([("check", 10, True, "")])

            @staticmethod
            def build(ctx):
                return None

            @staticmethod
            def after_fixture(ctx):
                return None

            @staticmethod
            def fingerprint(ctx):
                return {}

            @staticmethod
            def full_prompt(ctx):
                return "solve"

            @staticmethod
            def verify(ctx, card):
                card.check("check", True)

            @staticmethod
            def allowed_change(ctx, record):
                return False

        class Collector:
            events = []
            complete = True
            gaps = []
            errors = []

            def start(self):
                return self

            def stop(self):
                return None

        class Focus:
            activations = 0

            def __init__(self, instance):
                pass

            def __enter__(self):
                return self

            def __exit__(self, *args):
                return False

        subject_result = {"stream": "subject.stream.jsonl", "answer": "done", "model": "m",
                          "returncode": 0, "timedOut": False}
        with tempfile.TemporaryDirectory() as root, ExitStack() as stack:
            stack.enter_context(mock.patch.object(R, "RUNS_DIR", root))
            stack.enter_context(mock.patch.object(R, "connect", return_value=(Instance(), Client())))
            stack.enter_context(mock.patch.object(R.C, "new_attempt_id", return_value="attempt"))
            stack.enter_context(mock.patch.object(R.F, "new_run_id", return_value="r20261011T123456abcdef"))
            stack.enter_context(mock.patch.object(E, "snapshot", side_effect=[{"currentMode": "bypass"},
                                                                                {"currentMode": "bypass"}]))
            stack.enter_context(mock.patch.object(E, "requirement_problems", return_value=[]))
            stack.enter_context(mock.patch.object(E, "cleanliness", return_value={"clean": True}))
            stack.enter_context(mock.patch.object(E, "freeze_docs", return_value=("docs", "hash", 1)))
            stack.enter_context(mock.patch.object(E, "drift", return_value={}))
            stack.enter_context(mock.patch.object(R.F, "settings_snapshot", return_value={}))
            stack.enter_context(mock.patch.object(R.F, "enter", side_effect=lambda client, scope:
                                                  setattr(scope, "root_iid", 1)))
            stack.enter_context(mock.patch.object(R.F, "save_run_scene"))
            stack.enter_context(mock.patch.object(R, "_eval_hash", return_value={"harness": "hash"}))
            stack.enter_context(mock.patch.object(R.J, "current_cursor", return_value=0))
            stack.enter_context(mock.patch.object(R.J, "Collector", return_value=Collector()))
            stack.enter_context(mock.patch.object(R.J, "flatten", return_value=[]))
            stack.enter_context(mock.patch.object(R.J, "non_rest", return_value=[]))
            subject_call = stack.enter_context(mock.patch.object(R.S, "run", return_value=subject_result))
            metrics_call = stack.enter_context(mock.patch.object(R.M, "parse", return_value={
                "result": {"subtype": "success"}, "apiRetries": 0}))
            stack.enter_context(mock.patch.object(R.M, "telemetry", return_value={}))
            stack.enter_context(mock.patch.object(R.M, "model_matches", return_value=True))
            stack.enter_context(mock.patch.object(R, "FocusKeeper", Focus))
            stack.enter_context(mock.patch.object(R, "_cleanup", return_value={}))

            report = R._run_attempt(Task(), "/project", model="m", host="codex")

        self.assertEqual(report["host"], "codex")
        self.assertEqual(report["status"], "pass", report.get("reasons"))
        self.assertEqual(subject_call.call_args.kwargs["host"], "codex")
        self.assertEqual(metrics_call.call_args.kwargs["host"], "codex")

    def test_unconfirmed_subject_cleanup_preserves_fixture_and_invalidates_report(self):
        class Instance:
            instance_id = "eval_instance"
            port = 8091

            def as_dict(self):
                return {"instanceId": self.instance_id}

        class Client:
            is_read_only = lambda self, skill: False

            @staticmethod
            def wait_idle(timeout_s):
                return None

        class Task:
            id = "T"
            case_version = 1
            timeout_s = 10
            budget_usd = 1
            layer_pairs = []

            @staticmethod
            def scorecard():
                return V.Scorecard([("check", 10, True, "")])

            @staticmethod
            def build(ctx):
                return None

            @staticmethod
            def after_fixture(ctx):
                return None

            @staticmethod
            def fingerprint(ctx):
                return {}

            @staticmethod
            def full_prompt(ctx):
                return "solve"

            @staticmethod
            def verify(ctx, card):
                card.check("check", True)

            @staticmethod
            def allowed_change(ctx, record):
                return False

        class Collector:
            events = []
            complete = True
            gaps = []
            errors = []

            def start(self):
                return self

            def stop(self):
                return None

        class Focus:
            activations = 0

            def __init__(self, instance):
                pass

            def __enter__(self):
                return self

            def __exit__(self, *args):
                return False

        subject_result = {"stream": "subject.stream.jsonl", "answer": None, "model": "m",
                          "returncode": -9, "timedOut": True,
                          "processCleanup": {"attempted": True, "ok": False, "groupGone": False,
                                             "errors": ["process group remains"]}}
        with tempfile.TemporaryDirectory() as root, ExitStack() as stack:
            stack.enter_context(mock.patch.object(R, "RUNS_DIR", root))
            stack.enter_context(mock.patch.object(R, "connect", return_value=(Instance(), Client())))
            stack.enter_context(mock.patch.object(R.C, "new_attempt_id", return_value="attempt"))
            stack.enter_context(mock.patch.object(R.F, "new_run_id", return_value="r20261011T123456abcdef"))
            stack.enter_context(mock.patch.object(E, "snapshot", side_effect=[{"currentMode": "bypass"},
                                                                                {"currentMode": "bypass"}]))
            stack.enter_context(mock.patch.object(E, "requirement_problems", return_value=[]))
            stack.enter_context(mock.patch.object(E, "cleanliness", return_value={"clean": True}))
            stack.enter_context(mock.patch.object(E, "freeze_docs", return_value=("docs", "hash", 1)))
            stack.enter_context(mock.patch.object(E, "drift", return_value={}))
            stack.enter_context(mock.patch.object(R.F, "settings_snapshot", return_value={}))
            stack.enter_context(mock.patch.object(R.F, "enter", side_effect=lambda client, scope:
                                                  setattr(scope, "root_iid", 1)))
            stack.enter_context(mock.patch.object(R.F, "save_run_scene"))
            stack.enter_context(mock.patch.object(R, "_eval_hash", return_value={"harness": "hash"}))
            stack.enter_context(mock.patch.object(R.J, "current_cursor", return_value=0))
            stack.enter_context(mock.patch.object(R.J, "Collector", return_value=Collector()))
            stack.enter_context(mock.patch.object(R.J, "flatten", return_value=[]))
            stack.enter_context(mock.patch.object(R.J, "non_rest", return_value=[]))
            stack.enter_context(mock.patch.object(R.S, "run", return_value=subject_result))
            stack.enter_context(mock.patch.object(R.M, "parse", return_value={"result": {}, "apiRetries": 0}))
            stack.enter_context(mock.patch.object(R.M, "telemetry", return_value={}))
            stack.enter_context(mock.patch.object(R.M, "model_matches", return_value=True))
            stack.enter_context(mock.patch.object(R, "FocusKeeper", Focus))
            cleanup_call = stack.enter_context(mock.patch.object(R, "_cleanup"))

            report = R._run_attempt(Task(), "/project", model="m", host="codex")
            report_path = os.path.join(root, "adhoc", "T", "m", "attempt", "report.json")
            with open(report_path, encoding="utf-8") as handle:
                saved = json.load(handle)
            with open(os.path.join(root, "ledger.jsonl"), encoding="utf-8") as handle:
                ledger_row = json.loads(handle.readline())

        self.assertEqual(report["status"], "invalid")
        self.assertTrue(any(reason.startswith("subject_process_cleanup_unconfirmed:")
                            for reason in report["reasons"]))
        self.assertEqual(report["cleanup"]["fixturePreserved"], True)
        self.assertIn("unconfirmed", report["cleanup"]["error"])
        cleanup_call.assert_not_called()
        self.assertEqual(saved["status"], "invalid")
        self.assertTrue(saved["cleanup"]["fixturePreserved"])
        self.assertEqual(ledger_row["status"], "invalid")
        self.assertTrue(any(reason.startswith("subject_process_cleanup_unconfirmed:")
                            for reason in ledger_row["reasons"]))

    def test_unsupported_early_exit_still_writes_cleanup_report_and_host(self):
        class Instance:
            instance_id = "eval_instance"

            def as_dict(self):
                return {"instanceId": self.instance_id}

        class Task:
            id = "T"
            case_version = 1

            @staticmethod
            def scorecard():
                return V.Scorecard([("check", 10, True, "")])

        with tempfile.TemporaryDirectory() as root:
            with mock.patch.object(R, "RUNS_DIR", root), \
                    mock.patch.object(R, "connect", return_value=(Instance(), object())), \
                    mock.patch.object(R.C, "new_attempt_id", return_value="attempt"), \
                    mock.patch.object(R.F, "new_run_id", return_value="r20261011T123456abcdef"), \
                    mock.patch.object(E, "snapshot", return_value={}), \
                    mock.patch.object(E, "requirement_problems", return_value=["unsupported environment"]), \
                    mock.patch.object(R, "_cleanup", return_value={"error": "cleanup failed"}):
                report = R._run_attempt(Task(), "/project", suite="suite", host="codex")

            report_path = os.path.join(root, "suite", "T", "model", "attempt", "report.json")
            with open(report_path, encoding="utf-8") as handle:
                saved = json.load(handle)
            self.assertEqual(report["status"], "unsupported")
            self.assertEqual(report["reasons"], ["unsupported environment"])
            self.assertEqual(saved["cleanup"], {"error": "cleanup failed"})
            self.assertEqual(saved["cleanup_error"], "cleanup failed")
            self.assertEqual(saved["host"], "codex")
            with open(os.path.join(root, "ledger.jsonl"), encoding="utf-8") as handle:
                ledger_row = json.loads(handle.readline())
            self.assertEqual(ledger_row["host"], "codex")


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
