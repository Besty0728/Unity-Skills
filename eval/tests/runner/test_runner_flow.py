"""End-to-end runner flow against the fake editor: check, start, next, submit, status, report (contract 2.4-2.8)."""
import json
import os
import unittest

from .runner_support import Bench


class HappyPathTests(unittest.TestCase):
    def setUp(self):
        self.b = Bench(self)

    def start(self, *extra):
        code, env = self.b.run("start", "--lang", "en", *extra)
        self.assertEqual(code, 0, env)
        return env["data"]

    def test_check_reports_ready_and_points_at_start(self):
        code, env = self.b.run("check")
        self.assertEqual(code, 0, env)
        self.assertTrue(env["ok"])
        data = env["data"]
        self.assertTrue(data["ready"])
        self.assertEqual(data["selection"], {"track": "ops", "tier": "basic", "tasks": ["S99", "S98"]})
        self.assertEqual(data["instance"]["port"], self.b.unity.port)
        self.assertEqual(data["package"]["source"], "local")
        self.assertEqual(env["next"]["command"], "start")
        codes = {n["code"] for n in data["notices"]}
        self.assertIn("PROJECT_USER", codes)
        self.assertIn("SKILL_DOCS_MISSING", codes)

    def test_full_suite_session(self):
        data = self.start("--tasks", "S99")
        sid = data["sessionId"]
        self.assertTrue(data["created"])
        self.assertTrue(os.path.isfile(data["sessionPromptFile"]))
        with open(data["sessionPromptFile"], "rb") as handle:
            self.assertTrue(handle.read().startswith(b"\xef\xbb\xbf"))

        code, env = self.b.run("next", "--session", sid)
        self.assertEqual(code, 0, env)
        issued = env["data"]
        self.assertFalse(issued["done"])
        self.assertEqual((issued["taskId"], issued["k"], issued["n"]), ("S99", 1, 1))
        self.assertEqual(env["next"]["command"], "submit")
        with open(issued["promptFile"], "rb") as handle:
            raw = handle.read()
        self.assertTrue(raw.startswith(b"\xef\xbb\xbf"))
        text = raw.decode("utf-8-sig")
        self.assertIn("R1. Create a cube named `Crate`", text)
        self.assertIn(issued["submitCommand"], text)
        self.assertNotIn("{", text.split("Answer format (JSON):")[0].replace("{session", ""))

        self.b.solve(issued)
        code, env = self.b.run("submit", "--session", sid, "--attempt", issued["attemptId"],
                               "--answer", issued["answerFile"])
        self.assertEqual(code, 0, env)
        accepted = env["data"]
        self.assertTrue(accepted["accepted"])
        self.assertTrue(accepted["done"])
        for leaked in ("score", "status", "assertions", "completion"):
            self.assertNotIn(leaked, accepted)
        self.assertNotIn("pass", json.dumps(env))

        result = self.b.result(sid, issued["attemptId"])
        self.assertEqual(result["status"], "pass", result["reasons"])
        self.assertTrue(result["evidence"]["journal"]["complete"])
        self.assertTrue(result["teardown"]["ok"])
        self.assertFalse(os.path.isdir(os.path.join(self.b.project, "Assets", "__UnitySkillsEval")))
        self.assertEqual(self.b.unity.active()["path"], "Assets/Scenes/Main.unity")
        session = self.b.session(sid)
        self.assertEqual(session["status"], "completed")
        self.assertEqual(session["tasks"]["S99"]["state"], "done")
        self.assertFalse(os.path.isdir(self.b.paths.io_session(sid)))

        code, env = self.b.run("report", "--session", sid, "--format", "md")
        self.assertEqual(code, 0, env)
        self.assertEqual(env["data"]["summary"]["pass"], 1)
        self.assertIn("# UnitySkills evaluation report", env["data"]["markdown"])

        code, env = self.b.run("submit", "--session", sid, "--attempt", issued["attemptId"],
                               "--answer", issued["answerFile"])
        self.assertEqual(code, 0, env)
        self.assertTrue(env["data"]["alreadySubmitted"])
        self.assertEqual(self.b.state(sid, issued["attemptId"])["duplicateSubmits"], 1)

        code, env = self.b.run("next", "--session", sid)
        self.assertEqual(code, 0, env)
        self.assertTrue(env["data"]["done"])


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
