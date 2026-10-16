import importlib.util
from pathlib import Path
import unittest
from unittest.mock import patch


MODULE = Path(__file__).resolve().parents[3] / "SkillsForUnity/unity-skills~/scripts/runtime_verification.py"
spec = importlib.util.spec_from_file_location("runtime_verification", MODULE)
runtime = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runtime)


class Client:
    def __init__(self, outcomes):
        self.outcomes = iter(outcomes)
        self.calls = []
        self.job = 0

    def dry_run_skill(self, skill, **kwargs):
        return {"success": True, "dryRunToken": "preview"}

    def call(self, skill, **kwargs):
        self.calls.append(skill)
        if skill == "editor_get_state":
            return {"success": True, "isPlaying": False}
        self.job += 1
        return {"success": True, "jobId": str(self.job)}

    def get_job(self, job, **kwargs):
        return {"jobId": job, "kind": "compile", "startedAt": runtime.time.time(),
                "status": "completed", "terminal": True}

    def wait_for_job(self, job, **kwargs):
        return next(self.outcomes)


def compiled(**changes):
    evidence = dict(compiled=True, success=True, cycleId=9, domainLoaded=True)
    evidence.update(changes)
    return {"success": True, "status": "completed", "details": {"compilation": evidence}}


def scenario(passed):
    return {"success": passed, "status": "completed" if passed else "failed",
            "details": {"passed": passed}}


class RuntimeVerificationTests(unittest.TestCase):
    steps = [{"kind": "assert", "expected": 1}]

    def test_explicit_assertion_required(self):
        with self.assertRaises(ValueError):
            runtime.run_verified_scenario(Client([]), [{"kind": "invoke"}])

    def test_stale_or_unloaded_compile_does_not_run(self):
        for evidence in (compiled(cycleId=None), compiled(domainLoaded=False), compiled(compiled=False)):
            client = Client([evidence])
            result = runtime.run_verified_scenario(client, self.steps)
            self.assertEqual("failed", result["status"])
            self.assertNotIn("editor_run_scenario", client.calls)

    def test_assertion_failure_repairs_and_rechecks_compilation(self):
        client = Client([compiled(), scenario(False), compiled(), scenario(True)])
        repairs = []
        result = runtime.run_verified_scenario(client, self.steps,
                                               lambda evidence, number, budget: repairs.append(number))
        self.assertEqual("passed", result["status"])
        self.assertEqual([1], repairs)
        self.assertEqual(2, client.calls.count("debug_force_recompile"))

    def test_repair_limit(self):
        client = Client([compiled(), scenario(False)] * 4)
        result = runtime.run_verified_scenario(client, self.steps, lambda *args: None)
        self.assertEqual("failed", result["status"])
        self.assertEqual(3, result["repairCount"])
        self.assertEqual(4, len(result["attempts"]))

    def test_fresh_mutation_compile_job_is_used_without_forcing_again(self):
        proof = compiled()
        proof["snapshot"] = {"kind": "compile", "startedAt": runtime.time.time()}
        client = Client([proof, scenario(True)])
        result = runtime.run_verified_scenario(client, self.steps, compilation_job_id="fresh")
        self.assertEqual("passed", result["status"])
        self.assertNotIn("debug_force_recompile", client.calls)

    def test_pending_compile_is_waited_before_state_read(self):
        client = Client([dict(compiled(), snapshot={"kind": "compile", "startedAt": runtime.time.time()}), scenario(True)])
        snapshots = iter([{"jobId": "pending", "kind": "compile", "startedAt": runtime.time.time(), "status": "running"},
                          client.get_job("pending")])
        ready = [False]
        def get_job(*args, **kwargs):
            value = next(snapshots)
            ready[0] = value.get("terminal", False)
            return value
        client.get_job = get_job
        original_call = client.call
        def call(skill, **kwargs):
            if skill == "editor_get_state":
                self.assertTrue(ready[0])
            return original_call(skill, **kwargs)
        client.call = call
        with patch.object(runtime.time, "sleep"):
            result = runtime.run_verified_scenario(client, self.steps, compilation_job_id="pending")
        self.assertEqual("passed", result["status"])

    def test_deferred_compile_exits_and_restores_original_play(self):
        proof = dict(compiled(), snapshot={"kind": "compile", "startedAt": runtime.time.time()})
        client = Client([proof, scenario(True)])
        snapshots = iter([{"jobId": "deferred", "kind": "compile", "startedAt": runtime.time.time(),
                           "currentStage": "waiting_play_mode_exit", "status": "waiting_domain_reload"},
                          client.get_job("deferred")])
        client.get_job = lambda *args, **kwargs: next(snapshots)
        playing = [True]
        original_call = client.call
        def call(skill, **kwargs):
            if skill == "editor_get_state":
                return {"success": True, "isPlaying": playing[0]}
            if skill in ("editor_stop", "editor_play"):
                client.calls.append(skill)
                playing[0] = skill == "editor_play"
                return {"success": True}
            return original_call(skill, **kwargs)
        client.call = call
        with patch.object(runtime.time, "sleep"):
            result = runtime.run_verified_scenario(client, self.steps, compilation_job_id="deferred")
        self.assertEqual("passed", result["status"])
        self.assertEqual(1, client.calls.count("editor_stop"))
        self.assertEqual(1, client.calls.count("editor_play"))
        self.assertTrue(playing[0])

    def test_old_explicit_compile_job_is_rejected(self):
        proof = compiled()
        proof["snapshot"] = {"kind": "compile", "startedAt": 0}
        client = Client([proof])
        result = runtime.run_verified_scenario(client, self.steps, compilation_job_id="old")
        self.assertEqual("interrupted", result["status"])
        self.assertNotIn("editor_run_scenario", client.calls)

    def test_no_compilation_is_unverified_and_not_a_repair_trigger(self):
        proof = compiled(compiled=False)
        proof["currentStage"] = "no_compilation"
        client = Client([proof])
        repairs = []
        result = runtime.run_verified_scenario(client, self.steps, lambda *args: repairs.append(True))
        self.assertEqual("unverified", result["status"])
        self.assertEqual([], repairs)

    def test_deadline_exhausted_before_execution(self):
        client = Client([])
        with patch.object(runtime.time, "monotonic", side_effect=[0, 1801]):
            result = runtime.run_verified_scenario(client, self.steps)
        self.assertEqual("interrupted", result["status"])
        self.assertNotIn("debug_force_recompile", client.calls)

    def test_permission_failure_is_not_repaired(self):
        client = Client([compiled(), {"success": False, "status": "failed",
                                    "details": {"errorCode": "PERMISSION_DENIED"}}])
        repairs = []
        result = runtime.run_verified_scenario(client, self.steps, lambda *args: repairs.append(True))
        self.assertEqual("interrupted", result["status"])
        self.assertEqual([], repairs)

    def test_invalid_preview_prevents_write(self):
        client = Client([])
        client.dry_run_skill = lambda *args, **kwargs: {"status": "dryRun", "valid": False}
        result = runtime.run_verified_scenario(client, self.steps)
        self.assertEqual("interrupted", result["status"])
        self.assertNotIn("debug_force_recompile", client.calls)

    def test_timeout_does_not_replay_or_repair(self):
        client = Client([dict(status="running", timedOut=True, jobId="1")])
        repairs = []
        result = runtime.run_verified_scenario(client, self.steps, lambda *args: repairs.append(True))
        self.assertEqual("interrupted", result["status"])
        self.assertEqual("1", result["jobId"])
        self.assertEqual([], repairs)
        self.assertEqual(1, client.calls.count("debug_force_recompile"))


if __name__ == "__main__":
    unittest.main()


# Producer:Betsy
