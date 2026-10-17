"""The E2E scenarios against the fake editor (eval/tests/runner/runner_support.py): the same scenario code as the live
tests, with the runner in-process. USK_EVAL_E2E_FAKE_PROCESS=1 runs it as a subprocess instead (slow: the runner's
own idle waits are not patched), which also enables the kill scenario and the printed-command-line check."""
import os
import sys
import types
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))      # eval/tests: runner/

from runner.runner_support import Bench  # noqa: E402
from usk_eval import pack as runner_pack  # noqa: E402
from usk_eval import rest as runner_rest  # noqa: E402

from . import scenarios  # noqa: E402
from .scenarios import D, ScenarioEnv  # noqa: E402


class FakeEnv(ScenarioEnv):
    can_spoil = True

    def __init__(self, cleanup):
        ScenarioEnv.__init__(self)
        self.process = bool(os.environ.get("USK_EVAL_E2E_FAKE_PROCESS"))
        self.bench = Bench(cleanup, stand_ins=False)
        self.project = self.bench.project
        self.inproc = not self.process
        self.base_environ = D.clean_environ(dict(os.environ, **self.bench.env))
        if self.process:
            self.entry = self.pack.install_runner(os.path.join(self.tmp, "runner"))
            self.can_kill = self.line_runs = True
        else:
            previous = runner_pack.PACK_DIR
            runner_pack.PACK_DIR = self.pack.pack_dir
            cleanup.addCleanup(setattr, runner_pack, "PACK_DIR", previous)
            wait = runner_rest.TRANSIENT_WAIT_S          # the runner's pause before it asks again after a bad answer
            runner_rest.TRANSIENT_WAIT_S = 0.05
            cleanup.addCleanup(setattr, runner_rest, "TRANSIENT_WAIT_S", wait)

    @property
    def pack_base_dir(self):
        return os.path.join(os.path.dirname(self.entry), "pack") if self.process else self.pack.pack_dir

    def enter_play_mode(self, agent):
        self.bench.unity.playing = True

    def spoil(self, answers):
        self.bench.unity.spoil = {path: list(kinds) for path, kinds in answers.items()}

    def spoil_left(self):
        return {path: kinds for path, kinds in self.bench.unity.spoil.items() if kinds}

    def reset(self):
        unity = self.bench.unity
        unity.http_status, unity.fail_skill, unity.delete_fails, unity.mode = [], {}, False, "bypass"
        unity.spoil, unity.playing = {}, False
        ScenarioEnv.reset(self)


class OfflineScenarios(scenarios.ScenarioMixin, unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.env = FakeEnv(types.SimpleNamespace(addCleanup=cls.addClassCleanup))
        cls.addClassCleanup(cls.env.close)


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
