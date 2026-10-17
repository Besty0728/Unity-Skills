"""lock.py: O_EXCL lock, contention timeout, stale takeover rules, safe release (contract 2.6)."""
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
import unittest

from .runner_support import Bench, cli, lock, store
from usk_eval.problems import ProblemError


def dead_pid():
    proc = subprocess.Popen([sys.executable, "-B", "-c", "pass"])
    proc.wait()
    return proc.pid


class LockTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.path = os.path.join(self.tmp, "eval", "lock")
        os.makedirs(os.path.dirname(self.path))

    def holder(self):
        proc = subprocess.Popen([sys.executable, "-B", "-c", "import time; time.sleep(30)"])
        self.addCleanup(proc.wait)
        self.addCleanup(proc.kill)
        return proc.pid

    def write_lock(self, pid, started=None, cmd="submit"):
        with open(self.path, "w", encoding="utf-8") as handle:
            json.dump({"schema": 1, "pid": pid, "cmd": cmd, "launcher": "agent",
                       "startedAt": started or store.now_iso(), "host": "h"}, handle)

    def test_acquire_writes_content_and_release_removes(self):
        held = lock.Lock(self.path, "next", "agent").acquire()
        with open(self.path, "r", encoding="utf-8") as handle:
            info = json.load(handle)
        self.assertEqual((info["schema"], info["pid"], info["cmd"], info["launcher"]), (1, os.getpid(), "next", "agent"))
        self.assertRegex(info["startedAt"], r"Z$")
        held.release()
        self.assertFalse(os.path.exists(self.path))

    def test_live_holder_times_out_with_pid_and_command(self):
        pid = self.holder()
        self.write_lock(pid)
        with self.assertRaises(ProblemError) as caught:
            lock.Lock(self.path, "next", "agent", timeout_s=0.3, sleep=lambda s: time.sleep(0.05)).acquire()
        issue = caught.exception.problems[0]
        self.assertEqual(issue["code"], "LOCK_TIMEOUT")
        self.assertEqual(issue["args"], [str(pid), "submit"])
        self.assertTrue(os.path.exists(self.path))

    def test_dead_pid_is_taken_over_and_moved_aside(self):
        self.write_lock(dead_pid())
        held = lock.Lock(self.path, "next", "agent", timeout_s=1).acquire()
        self.assertTrue(held.held)
        aside = [n for n in os.listdir(os.path.dirname(self.path)) if n.startswith("lock.stale-")]
        self.assertEqual(len(aside), 1)
        held.release()

    def test_empty_lock_is_stale_only_after_ten_seconds(self):
        open(self.path, "w").close()
        with self.assertRaises(ProblemError):
            lock.Lock(self.path, "next", "agent", timeout_s=0.2, sleep=lambda s: None).acquire()
        old = time.time() - 30
        os.utime(self.path, (old, old))
        lock.Lock(self.path, "next", "agent", timeout_s=1).acquire().release()

    def test_lock_older_than_an_hour_is_stale_even_with_a_live_pid(self):
        pid = self.holder()
        self.write_lock(pid, started=store.iso(store.now().replace(year=2020)))
        lock.Lock(self.path, "next", "agent", timeout_s=1).acquire().release()

    def test_release_leaves_a_lock_that_was_taken_over(self):
        held = lock.Lock(self.path, "next", "agent").acquire()
        self.write_lock(12345, cmd="abort")
        held.release()
        self.assertTrue(os.path.exists(self.path))

    def test_takeover_restores_a_lock_that_changed_after_the_stale_judgement(self):
        self.write_lock(self.holder(), cmd="fresh")
        stale = {"schema": 1, "pid": dead_pid(), "cmd": "old", "launcher": "agent", "startedAt": store.now_iso()}
        lock.Lock(self.path, "next", "agent")._take_over(stale)
        with open(self.path, "r", encoding="utf-8") as handle:
            self.assertEqual(json.load(handle)["cmd"], "fresh")

    def test_old_stale_files_are_swept(self):
        aside = self.path + ".stale-1-abcdef"
        open(aside, "w").close()
        old = time.time() - 2 * 24 * 3600
        os.utime(aside, (old, old))
        lock.Lock(self.path, "next", "agent").acquire().release()
        self.assertFalse(os.path.exists(aside))

    def test_pid_alive(self):
        self.assertTrue(lock.pid_alive(os.getpid()))
        self.assertFalse(lock.pid_alive(dead_pid()))
        for bad in (0, -5, None, True, "12"):
            self.assertFalse(lock.pid_alive(bad))

    @unittest.skipUnless(sys.platform == "win32", "Windows liveness check")
    def test_windows_liveness_never_signals(self):
        pid = self.holder()
        self.assertTrue(lock._win_pid_alive(pid))
        self.assertTrue(lock.pid_alive(pid))           # the process must survive the probe (no os.kill)
        self.assertFalse(lock._win_pid_alive(dead_pid()))


class CliLockTests(unittest.TestCase):
    def test_locked_command_reports_lock_timeout_exit_5(self):
        bench = Bench(self)
        old = cli.LOCK_WAIT_S
        cli.LOCK_WAIT_S = 0.2
        self.addCleanup(setattr, cli, "LOCK_WAIT_S", old)
        proc = subprocess.Popen([sys.executable, "-B", "-c", "import time; time.sleep(30)"])
        self.addCleanup(proc.wait)
        self.addCleanup(proc.kill)
        os.makedirs(bench.paths.root, exist_ok=True)
        with open(bench.paths.lock, "w", encoding="utf-8") as handle:
            json.dump({"schema": 1, "pid": proc.pid, "cmd": "submit", "launcher": "agent",
                       "startedAt": store.now_iso(), "host": "h"}, handle)
        code, env = bench.run("start", "--lang", "en")
        self.assertEqual(code, 5)
        self.assertEqual(env["problems"][0]["code"], "LOCK_TIMEOUT")
        code, env = bench.run("check")          # lock-free commands are not blocked
        self.assertEqual(code, 0, env)


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
