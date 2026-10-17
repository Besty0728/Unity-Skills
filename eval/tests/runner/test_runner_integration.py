"""The runner with the real pack.py, private.py, scope.py, verifylib and usage modules; only the editor is fake."""
import json
import os
import shutil
import unittest

from .runner_support import Bench, store

TASK_PY = '''from usk_eval import verifylib as V
from usk_eval.task_api import PublicTask, address


class S97(PublicTask):
    id = "S97"
    track = "ops"
    tier = "basic"
    case_version = 1
    minutes = 5
    requires = {"pipeline": "URP", "mode": "bypass", "dedicated": False, "packages": []}

    def build(self, ctx):
        props = ctx.c.write("gameobject_create", name="Props", **address(ctx.root, "parent"))
        return {"props": V.object_id(props), "propsPath": ctx.path("Props")}

    def snapshot(self, ctx):
        return {"data": {"props": ctx.handles["props"]}, "digests": {}}

    def prompt_vars(self, ctx):
        return {"props": ctx.handles["propsPath"]}

    def answer_schema(self):
        return {"type": "object", "required": ["objectPath"], "properties": {"objectPath": {"type": "string"}}}


TASK = S97()
'''

PRIVATE_PY = '''from usk_eval import verifylib as V

# SENTINEL-PRIVATE-SOURCE: this line must never reach a log or result file
ASSERTIONS = [("A1", 6.0, False, ["R1"], "Crate under Props"), ("A2", 4.0, True, ["R2", "G1"], "no side effects")]
HINTS = {"A1": {"zh-CN": "检查父对象", "en": "check the parent"}}
EXPLODE = %s


def verify(ctx, card):
    if EXPLODE:
        raise KeyError("lookup")
    want = ctx.handles["propsPath"] + "/Crate"
    found = ctx.probe.go_path(want)
    card.check("A1", bool(found) and V.answer(ctx, "objectPath") == want, expected=want,
               actual=V.answer(ctx, "objectPath"))
    card.check("A2", not ctx.violations(), actual=len(ctx.violations()))


def verify_runtime(ctx, card):
    pass
'''

PROMPT = "<!-- usk-eval prompt; task S97; lang %s -->\nR1. Create `Crate` under `{props}`.\nR2. Change nothing else.\n"


def write_pack(root, explode=False):
    task_dir = os.path.join(root, "tasks", "S97")
    os.makedirs(task_dir)
    os.makedirs(os.path.join(root, "private"))
    files = {"task.py": TASK_PY, "prompt.zh-CN.md": PROMPT % "zh-CN", "prompt.en.md": PROMPT % "en"}
    public = {}
    for name, text in files.items():
        data = text.encode("utf-8")
        with open(os.path.join(task_dir, name), "wb") as handle:
            handle.write(data)
        public[name] = store.sha256_bytes(data)
    private = (PRIVATE_PY % ("True" if explode else "False")).encode("utf-8")
    with open(os.path.join(root, "private", "S97.py"), "wb") as handle:
        handle.write(private)
    manifest = {"schema": 1, "packId": "test-pack", "packVersion": "1.0.0", "plugin": {"min": None, "max": None},
                "tracks": {"ops": {"basic": ["S97"], "standard": [], "challenge": []},
                           "dev": {"basic": [], "standard": [], "challenge": []}},
                "tasks": {"S97": {"track": "ops", "tier": "basic", "caseVersion": 1, "minutes": 5, "seeds": [7001],
                                  "requires": {"pipeline": "URP", "mode": "bypass", "dedicated": False, "packages": []},
                                  "public": public, "private": {"file": "S97.py", "sha256": store.sha256_bytes(private),
                                                                "bytes": len(private)}}}}
    with open(os.path.join(root, "manifest.json"), "w", encoding="utf-8") as handle:
        json.dump(manifest, handle, indent=1)


class RealModulesTests(unittest.TestCase):
    def setUp(self):
        self.b = Bench(self, stand_ins=False)
        self.pack_dir = os.path.join(self.b.tmp, "packdir")

    def session(self, explode=False):
        write_pack(self.pack_dir, explode)
        code, env = self.b.run("start", "--lang", "en", "--pack-dir", self.pack_dir)
        self.assertEqual(code, 0, env)
        self.assertFalse(env["data"]["official"])
        sid = env["data"]["sessionId"]
        code, env = self.b.run("next", "--session", sid)
        self.assertEqual(code, 0, env)
        return sid, env["data"]

    def test_full_attempt_with_the_real_modules(self):
        sid, data = self.session()
        self.b.solve(data)
        code, env = self.b.run("submit", "--session", sid, "--attempt", data["attemptId"], "--answer",
                               data["answerFile"])
        self.assertEqual(code, 0, env)
        result = self.b.result(sid, data["attemptId"])
        self.assertEqual(result["status"], "pass", result["reasons"])
        self.assertEqual((result["verifier"]["kind"], result["verifier"]["official"], result["verifier"]["deleted"]),
                         ("pack-dir", False, True))
        self.assertEqual([row["req"] for row in result["score"]["assertions"]], [["R1"], ["R2", "G1"]])
        self.assertEqual(len(self.b.state(sid, data["attemptId"])["fingerprint"]), 64)
        self.assertFalse([d for d, sub, _f in os.walk(self.pack_dir) if "__pycache__" in sub])

    def test_unity_6000_4_ids_with_the_real_modules(self):
        self.b.unity.entity_mode = "6000.4"
        sid, data = self.session()
        state = self.b.state(sid, data["attemptId"])
        self.assertEqual(state["scope"]["rootInstanceId"], 0)
        self.assertEqual(state["scope"]["rootId"], state["scope"]["rootEntityId"])
        self.assertIsInstance(state["handles"]["props"], str)
        self.b.solve(data)
        self.b.run("submit", "--session", sid, "--attempt", data["attemptId"], "--answer", data["answerFile"])
        self.assertEqual(self.b.result(sid, data["attemptId"])["status"], "pass")
        shutil.rmtree(self.pack_dir)
        sid2, again = self.session()
        self.assertEqual(state["fingerprint"], self.b.state(sid2, again["attemptId"])["fingerprint"])

    def test_same_seed_gives_the_same_fingerprint(self):
        sid, first = self.session()
        self.b.run("abort", "--session", sid)
        shutil.rmtree(self.pack_dir)
        sid2, second = self.session()
        self.assertNotEqual(sid, sid2)
        self.assertEqual(self.b.state(sid, first["attemptId"])["fingerprint"],
                         self.b.state(sid2, second["attemptId"])["fingerprint"])

    def test_wrong_answer_and_broken_verifier(self):
        sid, data = self.session()
        self.b.solve(data, correct=False)
        self.b.run("submit", "--session", sid, "--attempt", data["attemptId"], "--answer", data["answerFile"])
        result = self.b.result(sid, data["attemptId"])
        self.assertEqual((result["status"], result["requirementsFailed"]), ("partial", ["R1"]))
        self.assertEqual(result["score"]["assertions"][0]["hint"], "check the parent")

        shutil.rmtree(self.pack_dir)
        sid, data = self.session(explode=True)
        self.b.solve(data)
        self.b.run("submit", "--session", sid, "--attempt", data["attemptId"], "--answer", data["answerFile"])
        result = self.b.result(sid, data["attemptId"])
        self.assertEqual((result["status"], result["reasons"][0]), ("invalid", "verifier_error: KeyError: 'lookup'"))
        with open(self.b.paths.session_log(sid), "r", encoding="utf-8") as handle:
            log = handle.read()
        self.assertIn("verifier error KeyError", log)
        for text in (log, json.dumps(result)):
            self.assertNotIn("SENTINEL-PRIVATE-SOURCE", text)
            self.assertNotIn("raise KeyError", text)


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
