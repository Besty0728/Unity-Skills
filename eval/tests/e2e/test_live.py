"""Live E2E tests: the v3 runner against a real, open Unity editor, driven the way an agent and the panel drive it.

Skipped unless USK_EVAL_E2E_PROJECT names an open project whose UnitySkills server is running in Bypass mode with the
Full surface, no unsaved scene changes and nobody else using the editor:

    USK_EVAL_E2E_PROJECT=/path/to/project [USK_EVAL_E2E_PORT=8091] python3 -B -m unittest discover eval/tests -k Live

The scenarios (scenarios.py) use the self-contained test pack of e2e_pack.py through a copy of the runner, so they
need no ported task and run on any render pipeline. The tests create and delete Assets/__UnitySkillsEval/<runId>
folders and scenes, replace the active scene while an attempt runs, and (domain reload scenario) write one harmless
script under the run's own asset root; each test hands the editor back as it found it. Before every test they wait
until the editor runs no job (a Unity Test Runner run, a compile) and the project is not in use by another driver.
"""
import os
import sys
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from . import scenarios  # noqa: E402
from .scenarios import D, HealthWatcher, ScenarioEnv  # noqa: E402

PROJECT = os.environ.get("USK_EVAL_E2E_PROJECT")
PORT = os.environ.get("USK_EVAL_E2E_PORT")
WAIT_S = float(os.environ.get("USK_EVAL_E2E_WAIT", "900"))
SKIP = "set USK_EVAL_E2E_PROJECT (and USK_EVAL_E2E_PORT) to run the live E2E tests against an open editor"


def say(message):
    sys.stderr.write("[e2e] %s\n" % message)
    sys.stderr.flush()


class LiveEnv(ScenarioEnv):
    can_kill = can_reload = line_runs = True

    def __init__(self, project, port=None):
        ScenarioEnv.__init__(self)
        self.project = os.path.realpath(project)
        self.port = int(port) if port else None
        self.entry = self.pack.install_runner(os.path.join(self.tmp, "runner"))

    @property
    def pack_base_dir(self):
        return os.path.join(os.path.dirname(self.entry), "pack")

    def wait_quiet(self, agent):
        D.wait_quiet(agent.client, timeout_s=WAIT_S, poll_s=5.0, log=say)

    def preflight(self):
        """Locate the editor, confirm it is the one asked for, and require a ready `check`."""
        agent = self.agent()
        health = agent.client.health()
        if self.port is not None and health.get("port") != self.port:
            raise RuntimeError("%s is served on port %s, not %s" % (self.project, health.get("port"), self.port))
        say("editor: Unity %s, UnitySkills %s, instance %s, mode %s" % (
            health.get("unityVersion"), health.get("version"), health.get("instanceId"), health.get("currentMode")))
        self.wait_quiet(agent)
        checked = agent.check("--track", "ops", "--tier", "basic")
        if not checked.ok:
            raise RuntimeError("the editor is not ready for the E2E tests: %s" % checked.describe())

    def reload_scripts(self, agent, attempt):
        script = self.write_script(agent, attempt)
        watcher = HealthWatcher(agent.client).start()
        try:
            refreshed = agent.client.write("asset_refresh")
            triggered = bool(refreshed.get("compileTriggered"))
        except D.runner_rest.RestError:
            triggered = None            # the answer was lost in the reload itself: the watcher decides
        return {"script": script, "compileTriggered": triggered, "watcher": watcher}

    def enter_play_mode(self, agent):
        agent.client.write("editor_play")
        playing = D.wait_until(lambda: agent.client.read("editor_get_state").get("isPlaying"), 120.0, 0.5)
        if not playing:
            raise RuntimeError("the editor did not enter Play Mode")


@unittest.skipUnless(PROJECT, SKIP)
class LiveScenarios(scenarios.ScenarioMixin, unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.env = LiveEnv(PROJECT, PORT)
        cls.addClassCleanup(cls.env.close)
        lease = D.EditorLease(cls.env.project, "e2e", wait_s=WAIT_S, log=say)
        lease.acquire()
        cls.addClassCleanup(lease.release)
        cls.env.preflight()


@unittest.skipUnless(PROJECT, SKIP)
class LiveShippedPack(unittest.TestCase):
    """The shipped pack's reference solutions, run as an agent runs a suite (needs ported tasks, see selftest3)."""

    def test_reference_solutions_pass_as_a_whole_suite(self):
        repo = D.REPO
        manifest = os.path.join(repo, "SkillsForUnity", "Evaluation~", "pack", "manifest.json")
        if not os.path.isfile(manifest):
            self.skipTest("the package ships no task pack yet")
        packs = D.PackAccess(private_dir=os.path.join(repo, "eval", "pack", "private"),
                             solutions_dir=os.path.join(repo, "eval", "pack", "solutions"))
        tasks = [t for t in os.environ.get("USK_EVAL_E2E_TASKS", "").split(",") if t] or [
            t for t in packs.ids() if packs.meta(t)["track"] == "ops" and packs.meta(t)["tier"] == "basic"]
        if not tasks:
            self.skipTest("no ops/basic task in the shipped pack")
        runner = D.Runner(os.path.realpath(PROJECT), environ=D.clean_environ())
        agent = D.ScriptedAgent(runner, packs, with_private=True, log=say)
        with D.EditorLease(runner.project, "e2e-shipped", wait_s=WAIT_S, log=say):
            D.wait_quiet(agent.client, timeout_s=WAIT_S, poll_s=5.0, log=say)
            started = agent.start("--track", "ops", "--tier", "basic", "--tasks", ",".join(tasks), "--no-usage")
            if not started.ok:
                self.skipTest("the shipped tasks cannot run on this editor: %s" % started.describe())
            try:
                runs = agent.run_session({t: packs.solutions(t).reference for t in tasks})
            finally:
                agent.abort_quietly("e2e_shipped_cleanup")
        self.assertTrue(runs and runs[-1].done, [r.next_result.describe() for r in runs if r.next_result])
        sid = started.data["sessionId"]
        results = {t: agent.state.result(sid, a) for t in tasks for a in agent.state.session(sid)["tasks"][t]["attempts"]}
        self.assertEqual({t: r["status"] for t, r in results.items()}, {t: "pass" for t in tasks},
                         {t: r["reasons"] for t, r in results.items()})


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
