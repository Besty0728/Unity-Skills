"""task_api.py / fixture ids: id-agnostic object references (Unity 6000.4+ reports instanceId 0) and context guards."""
import json
import unittest

from .runner_support import Bench, real_pack
from usk_eval import task_api
from usk_eval.fixture import Scope, address, new_run_id

MODERN = "4294974300"          # 6000.4+: EntityId as a ulong, instanceId always 0


class ObjectIdTests(unittest.TestCase):
    """address() names objects through verifylib.object_id: non-zero instanceId, else entityId."""

    def test_legacy_ids_address_by_instance_id(self):
        result = {"entityId": "-1234", "instanceId": -1234, "name": "Props"}
        self.assertEqual(address(result), {"instanceId": -1234})
        self.assertEqual(address(result, "parent"), {"parentInstanceId": -1234})
        self.assertEqual(address(-1234), {"instanceId": -1234})

    def test_modern_ids_address_by_entity_id(self):
        result = {"entityId": MODERN, "instanceId": 0}
        self.assertEqual(address(result), {"entityId": MODERN})
        self.assertEqual(address(result, "parent"), {"parentEntityId": MODERN})
        self.assertEqual(address(MODERN), {"entityId": MODERN})
        self.assertEqual(address(9999999999), {"entityId": "9999999999"})      # beyond C# int: only entityId

    def test_unusable_ids(self):
        for value in ({}, {"instanceId": True}, {"entityId": "0", "instanceId": 0}, None, "Props", 0):
            with self.assertRaises(ValueError):
                address(value)

    def test_scope_keeps_every_root_id(self):
        scope = Scope(new_run_id(), root_iid=0, root_entity_id=MODERN, root_id=MODERN)
        stored = scope.as_dict()
        self.assertEqual((stored["rootInstanceId"], stored["rootEntityId"], stored["rootId"]), (0, MODERN, MODERN))
        self.assertEqual(Scope.from_dict(stored).root_id, MODERN)
        self.assertIsNone(Scope.from_dict({"runId": scope.run_id, "rootInstanceId": 5}).root_id)


class ContextGuardTests(unittest.TestCase):
    def args(self, seed=7001):
        scope = Scope("r20260101T000000abcdef", root_iid=-10, root_entity_id="-10", root_id=-10)
        return dict(scope=scope, seed=seed, project_path="/p", env={}, lang="en", track="ops", tier="basic",
                    task=None)

    def test_build_needs_an_integer_seed(self):
        for seed in (None, True, "7001", 7001.0):
            with self.assertRaises(ValueError):
                task_api.BuildContext(None, **self.args(seed=seed))
        first = task_api.BuildContext(None, **self.args()).rng.random()
        self.assertEqual(task_api.BuildContext(None, **self.args()).rng.random(), first)
        with self.assertRaises(KeyError):
            task_api.context_args({"lang": "en"}, {"scope": {"runId": "r20260101T000000abcdef"}}, None, "/p")

    def verify_ctx(self, records):
        return task_api.VerifyContext(None, records=records, **self.args())

    def test_violations_extra_must_be_callable(self):
        ctx = self.verify_ctx([])
        with self.assertRaises(TypeError) as caught:
            ctx.violations(object())
        self.assertIn("callable", str(caught.exception))
        self.assertEqual(ctx.violations(lambda rec: False), [])
        self.assertIsNone(ctx.log("never written"))

    def test_saved_other_scenes_normalises_slashes(self):
        scene = Scope("r20260101T000000abcdef").scene_path
        ctx = self.verify_ctx([{"kind": "scene_saved", "path": scene.replace("/", "\\")},
                               {"kind": "scene_saved", "path": "Assets/Scenes/Main.unity"}])
        self.assertEqual([r["path"] for r in ctx.saved_other_scenes()], ["Assets/Scenes/Main.unity"])

    def test_default_requires_is_read_only(self):
        with self.assertRaises(TypeError):
            task_api.PublicTask.requires["mode"] = "auto"
        self.assertEqual(real_pack.normalize_requires(task_api.PublicTask.requires), real_pack.DEFAULT_REQUIRES)


class Unity6000_4Tests(unittest.TestCase):
    def test_attempt_with_zero_instance_ids(self):
        b = Bench(self)
        b.unity.entity_mode = "6000.4"
        code, env = b.run("start", "--lang", "en", "--tasks", "S99")
        sid = env["data"]["sessionId"]
        code, env = b.run("next", "--session", sid)
        self.assertEqual(code, 0, env)
        data = env["data"]
        state = b.state(sid, data["attemptId"])
        self.assertEqual(state["scope"]["rootInstanceId"], 0)
        self.assertRegex(state["scope"]["rootEntityId"], r"^\d{10,}$")
        self.assertEqual(state["scope"]["rootId"], state["scope"]["rootEntityId"])
        self.assertRegex(state["handles"]["props"], r"^\d{10,}$")
        b.solve(data)
        code, env = b.run("submit", "--session", sid, "--attempt", data["attemptId"], "--answer", data["answerFile"])
        self.assertEqual(code, 0, env)
        self.assertEqual(b.result(sid, data["attemptId"])["status"], "pass")
        with open(b.unity.journal_path, "r", encoding="utf-8") as handle:
            created = [c for line in handle for c in json.loads(line)["payload"].get("changes") or []]
        self.assertTrue(created and all(c["instanceId"] == 0 for c in created))


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
