"""fixture.py: scope ownership, resting-scene restore order, teardown retry, residue listing (contract 2.8 N4, S11)."""
import os
import unittest

from .runner_support import Bench, SkillFailure, rest
from usk_eval import fixture as F


class FixtureTests(unittest.TestCase):
    def setUp(self):
        self.b = Bench(self)
        target = type("T", (), {"instance_id": self.b.unity.instance_id,
                                "base_url": "http://127.0.0.1:%d" % self.b.unity.port})()
        self.client = rest.Client(target, retry_window_s=5.0)
        for name in ("Level", "Lights"):
            with open(self.b.unity.disk("Assets/Scenes/%s.unity" % name), "w") as handle:
                handle.write("%YAML 1.1\n")

    def test_scope_paths_and_ownership(self):
        scope = F.Scope(F.new_run_id())
        self.assertRegex(scope.run_id, r"^r\d{8}T\d{6}[0-9a-f]{6}$")
        self.assertEqual(scope.asset_root, "Assets/__UnitySkillsEval/" + scope.run_id)
        self.assertEqual(scope.scene_path, "%s/Eval_%s.unity" % (scope.asset_root, scope.run_id))
        self.assertTrue(scope.owned())
        self.assertEqual(F.Scope.from_dict(scope.as_dict()).as_dict(), scope.as_dict())
        with self.assertRaises(ValueError):
            F.Scope("../Assets")

    def test_resting_scenes_come_back_in_order_with_the_active_one(self):
        resting = {"scenes": [{"path": "Assets/Scenes/Level.unity", "isActive": False, "isLoaded": True},
                              {"path": "Assets/Scenes/Lights.unity", "isActive": True, "isLoaded": True},
                              {"path": "Assets/Scenes/Main.unity", "isActive": False, "isLoaded": False}]}
        actions = F.restore_resting(self.client, resting)
        self.assertEqual(actions, ["scene_load Assets/Scenes/Level.unity", "scene_load+ Assets/Scenes/Lights.unity",
                                   "scene_set_active Assets/Scenes/Lights"])
        self.assertEqual([(s["path"], s["isActive"]) for s in self.b.unity.scenes],
                         [("Assets/Scenes/Level.unity", False), ("Assets/Scenes/Lights.unity", True)])

    def enter(self):
        scope = F.Scope(F.new_run_id())
        resting = F.resting_record(F.loaded_scenes(self.client))
        F.enter(self.client, scope, lambda: None)
        return scope, resting

    def test_teardown_retries_once(self):
        scope, resting = self.enter()
        self.b.unity.fail_skill["asset_delete"] = SkillFailure("BUSY", "importing")
        outcome = F.teardown(self.client, scope, resting, {}, sleep=lambda s: None)
        self.assertTrue(outcome["ok"], outcome)
        self.assertTrue(any(a.startswith("retry after SkillError") for a in outcome["actions"]))
        self.assertFalse(os.path.isdir(self.b.unity.disk(scope.asset_root)))
        self.assertEqual(self.b.unity.active()["path"], "Assets/Scenes/Main.unity")

    def test_a_failing_task_hook_still_cleans_up_but_reports_failure(self):
        scope, resting = self.enter()
        calls = []

        def hook():
            calls.append(1)
            raise RuntimeError("settings")

        outcome = F.teardown(self.client, scope, resting, {}, hook, sleep=lambda s: None)
        self.assertFalse(outcome["ok"])
        self.assertEqual(outcome["error"], "FixtureError: task teardown: RuntimeError: settings")
        self.assertEqual(len(calls), 2)
        self.assertFalse(os.path.isdir(self.b.unity.disk(scope.asset_root)))

    def test_collision_is_restored(self):
        snapshot = F.collision_snapshot(self.client, [(8, 9)])
        self.b.unity.collision[(8, 9)] = False
        self.assertEqual(F.restore_collision(self.client, snapshot), ["8,9"])
        self.assertTrue(self.b.unity.collision[(8, 9)])

    def test_residue_listing(self):
        root = self.b.unity.disk("Assets/__UnitySkillsEval")
        for name in ("r20260101T000000abcdef", "r20260102T000000abcdef", "Manual", "r1"):
            os.makedirs(os.path.join(root, name))
        open(os.path.join(root, "r20260103T000000abcdef.meta"), "w").close()
        owned, foreign = F.residue(root, active_run_id="r20260102T000000abcdef")
        self.assertEqual(owned, [{"runId": "r20260101T000000abcdef",
                                  "assetRoot": "Assets/__UnitySkillsEval/r20260101T000000abcdef"}])
        self.assertEqual(foreign, ["Manual", "r1"])


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
