"""Offline timeout cleanup tests; the live-process case owns a fresh process group."""
import os
import signal
import subprocess
import sys
import tempfile
import time
import unittest
from unittest import mock

sys.path.insert(0, os.path.dirname(os.path.dirname(__file__)))
from harness import subject as S
from harness import classify as C


class KillGroupMockTests(unittest.TestCase):
    def test_eperm_falls_back_to_leader_and_reports_unverified_group(self):
        proc = mock.Mock(pid=424242)
        proc.wait.side_effect = [subprocess.TimeoutExpired("cmd", 1), -9]
        proc.poll.return_value = -9

        def killpg(pgid, sig):
            if sig == signal.SIGTERM:
                return None
            if sig == 0:
                raise PermissionError(1, "Operation not permitted")
            if sig == signal.SIGKILL:
                raise PermissionError(1, "Operation not permitted")
            raise AssertionError(sig)

        with mock.patch.object(S.os, "getpgid", return_value=424242), \
             mock.patch.object(S.os, "killpg", side_effect=killpg):
            result = S._kill_group(proc, grace_s=0.01, verify_s=0.01, poll_s=0.001)

        proc.kill.assert_called_once()
        self.assertEqual(proc.wait.call_count, 2)
        self.assertEqual(result["leaderReturnCode"], -9)
        self.assertFalse(result["ok"])
        self.assertFalse(result["groupGone"])
        self.assertTrue(any("PermissionError" in error for error in result["errors"]))

    def test_run_preserves_leader_returncode_and_cleanup_error(self):
        with tempfile.TemporaryDirectory() as temp:
            docs = os.path.join(temp, "docs")
            os.mkdir(docs)
            with open(os.path.join(docs, "SKILL.md"), "w", encoding="utf-8") as handle:
                handle.write("frozen docs")
            proc = mock.Mock(pid=424242)
            proc.wait.side_effect = [subprocess.TimeoutExpired("cmd", 1), -9]
            cleanup = {"attempted": True, "ok": False, "groupGone": False,
                       "leaderReturnCode": -9, "errors": ["group remains"]}
            proc.poll.return_value = -9
            with mock.patch.object(S.subprocess, "Popen", return_value=proc), \
                 mock.patch.object(S, "_kill_group", return_value=cleanup):
                result = S.run("prompt", "model", "high", docs, os.path.join(temp, "out"),
                               0.01, 0.5, warm=False)
            self.assertTrue(result["timedOut"])
            self.assertEqual(result["returncode"], -9)
            self.assertEqual(result["processCleanup"], cleanup)
            self.assertEqual(proc.wait.call_count, 1, "run must not add an unbounded wait after cleanup")


class FinalizationTests(unittest.TestCase):
    def test_finalization_errors_invalidate_successful_subject(self):
        card = mock.Mock(strict=True)
        for timed_out in (False, True):
            with self.subTest(timed_out=timed_out):
                status, reasons = C.classify(
                    card, subject={"timedOut": timed_out, "processCleanup": {"ok": True},
                                   "finalizationErrors": ["close_identity_proxy: failure"]},
                    metrics={"result": {"subtype": "success"}})
                self.assertEqual(status, "invalid")
                self.assertTrue(reasons[0].startswith("subject_finalization_error:"))

    def test_collection_race_is_recorded_and_other_files_are_collected(self):
        with tempfile.TemporaryDirectory() as temp:
            sandbox = os.path.join(temp, "sandbox")
            os.mkdir(sandbox)
            for name in ("gone.txt", "kept.txt"):
                with open(os.path.join(sandbox, name), "w") as handle:
                    handle.write("evidence")
            original = S.shutil.copy2

            def copy(src, dest):
                if src.endswith("gone.txt"):
                    os.unlink(src)
                return original(src, dest)

            with mock.patch.object(S.shutil, "copy2", side_effect=copy):
                outputs = S._collect_outputs(sandbox, os.path.join(temp, "outputs"))
            by_path = {item["path"]: item for item in outputs}
            self.assertIn("FileNotFoundError", by_path["gone.txt"]["error"])
            self.assertEqual(by_path["kept.txt"]["bytes"], 8)

    def test_collection_errors_are_reported_as_finalization_errors(self):
        with tempfile.TemporaryDirectory() as temp:
            docs = os.path.join(temp, "docs")
            os.mkdir(docs)
            proc = mock.Mock()
            proc.wait.return_value = 0
            evidence = [{"path": "final.json", "error": "FileNotFoundError: removed"}]
            with mock.patch.object(S.subprocess, "Popen", return_value=proc), \
                 mock.patch.object(S, "_collect_outputs", return_value=evidence):
                result = S.run("prompt", "model", "high", docs, os.path.join(temp, "out"),
                               1, 0.5, warm=False)
            self.assertEqual(result["outputs"], evidence)
            self.assertEqual(result["finalizationErrors"],
                             ["collect_outputs final.json: FileNotFoundError: removed"])
            self.assertIn("FileNotFoundError", result["hostError"])

    def test_finalization_failures_preserve_uncertain_process_cleanup(self):
        with tempfile.TemporaryDirectory() as temp:
            docs = os.path.join(temp, "docs")
            os.mkdir(docs)
            proc = mock.Mock(pid=424242)
            proc.wait.side_effect = subprocess.TimeoutExpired("cmd", 1)
            cleanup = {"attempted": True, "ok": False, "groupGone": False,
                       "leaderReturnCode": -9, "errors": ["group remains"]}
            proxy = mock.Mock()
            proxy.close.side_effect = RuntimeError("close failed")
            proxy.metadata.side_effect = RuntimeError("metadata failed")
            home = os.path.join(temp, "home")
            os.mkdir(home)
            with mock.patch.object(S.subprocess, "Popen", return_value=proc), \
                 mock.patch.object(S, "_kill_group", return_value=cleanup), \
                 mock.patch.object(S, "_codex_isolation", return_value=[]), \
                 mock.patch.object(S, "_codex_home", return_value=(home, [], proxy)), \
                 mock.patch.object(S, "_collect_outputs", side_effect=OSError("collection failed")):
                result = S.run("prompt", "model", "high", docs, os.path.join(temp, "out"),
                               0.01, 0.5, warm=False, host="codex")
            self.assertTrue(result["timedOut"])
            self.assertEqual(result["processCleanup"], cleanup)
            self.assertEqual(result["returncode"], -9)
            self.assertEqual(len(result["finalizationErrors"]), 3)
            self.assertIn("collection failed", result["hostError"])
            self.assertFalse(os.path.exists(home))


class RealProcessGroupTests(unittest.TestCase):
    def test_kills_grandchild_after_group_leader_exits_on_term(self):
        with tempfile.TemporaryDirectory() as temp:
            pidfile = os.path.join(temp, "grandchild.pid")
            child_code = "\n".join((
                "import os, signal, time",
                "signal.signal(signal.SIGTERM, signal.SIG_IGN)",
                "with open(%r, 'w') as handle: handle.write(str(os.getpid()))" % pidfile,
                "while True:",
                "    time.sleep(1)",
            ))
            parent_code = "\n".join((
                "import subprocess, sys, time",
                "subprocess.Popen([sys.executable, '-c', %r])" % child_code,
                "time.sleep(60)",
            ))
            leader = subprocess.Popen([sys.executable, "-c", parent_code], start_new_session=True,
                                      stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
                                      stderr=subprocess.DEVNULL)
            grandchild_pid = None
            try:
                deadline = time.monotonic() + 5
                while time.monotonic() < deadline and not os.path.exists(pidfile):
                    time.sleep(0.02)
                self.assertTrue(os.path.exists(pidfile), "grandchild did not start")
                with open(pidfile, encoding="ascii") as handle:
                    grandchild_pid = int(handle.read())
                result = S._kill_group(leader, grace_s=2, verify_s=3, poll_s=0.03)
                self.assertIsNotNone(result["leaderReturnCode"])
                self.assertTrue(result["groupGone"], result)
                self.assertTrue(result["ok"], result)
                self.assertFalse(_process_running(grandchild_pid), "grandchild survived group SIGKILL")
            finally:
                # Safety cleanup targets only the isolated session created above.
                try:
                    os.killpg(leader.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
                try:
                    leader.wait(timeout=2)
                except subprocess.TimeoutExpired:
                    leader.kill()
                    leader.wait()
                if grandchild_pid is not None and _process_running(grandchild_pid):
                    try:
                        os.kill(grandchild_pid, signal.SIGKILL)
                    except ProcessLookupError:
                        pass


def _process_running(pid):
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return False
    try:
        result = subprocess.run(["ps", "-o", "stat=", "-p", str(pid)], capture_output=True, text=True,
                                timeout=2, check=False)
        state = result.stdout.strip()
        return bool(state) and not state.startswith("Z")
    except (OSError, subprocess.TimeoutExpired):
        return True


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
