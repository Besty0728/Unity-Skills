"""telemetry.py: the (t0, t1] window across UnitySkillsTelemetry.jsonl and .1-.3, minus the runner (contract 2.8 S6)."""
import json
import os
import shutil
import tempfile
import unittest

from .runner_support import store
from usk_eval import telemetry


def line(ts, skill, agent="ClaudeCode", ok=True, mode="execute", code=None):
    rec = {"ts": ts, "skill": skill, "agent": agent, "mode": mode, "ok": ok}
    if not ok:
        rec["errorCode"] = code
    rec["ms"] = 3
    return json.dumps(rec, separators=(",", ":"))


class TelemetryTests(unittest.TestCase):
    T0 = "2026-10-17T03:00:00.000Z"
    T1 = "2026-10-17T03:10:00.000Z"

    def setUp(self):
        self.dir = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.dir, True)

    def write(self, name, lines, tail=""):
        with open(os.path.join(self.dir, name), "w", encoding="utf-8") as handle:
            handle.write("".join(text + "\n" for text in lines) + tail)

    def test_window_bounds_runner_exclusion_and_rotation_dedupe(self):
        shared = line("2026-10-17T03:05:00.000Z", "gameobject_create")
        self.write("UnitySkillsTelemetry.jsonl", [
            shared,
            line("2026-10-17T03:06:00.000Z", "material_create", ok=False, code="NOT_FOUND"),
            line("2026-10-17T03:07:00.000Z", "asset_refresh", agent="UnitySkillsEval"),
            line("2026-10-17T03:10:00.000Z", "scene_save", mode="batch_step"),           # t1 is inclusive
        ], tail='{"ts":"2026-10-17T03:08:00.000Z","sk')                                  # torn tail
        self.write("UnitySkillsTelemetry.1.jsonl", [
            line("2026-10-17T03:00:00.000Z", "too_early"),                               # t0 is exclusive
            shared,                                                                      # read twice in a rotation
            line("2026-10-17T03:01:00.000Z", "component_add", agent="SelftestSolver"),
        ])
        self.write("UnitySkillsTelemetry.3.jsonl", [line("2026-10-17T03:11:00.000Z", "too_late")])
        out = os.path.join(self.dir, "copy.jsonl")
        summary = telemetry.freeze(self.dir, self.T0, self.T1, out)
        self.assertTrue(summary["available"])
        self.assertEqual(summary["files"], ["UnitySkillsTelemetry.jsonl", "UnitySkillsTelemetry.1.jsonl",
                                            "UnitySkillsTelemetry.3.jsonl"])
        self.assertEqual(summary["skillCalls"], 4)
        self.assertEqual(summary["bySkill"], {"component_add": 1, "gameobject_create": 1, "material_create": 1,
                                              "scene_save": 1})
        self.assertEqual(summary["errors"], {"NOT_FOUND": 1})
        self.assertEqual(summary["agents"], {"ClaudeCode": 3, "SelftestSolver": 1})
        self.assertEqual(summary["modes"], {"batch_step": 1, "execute": 3})
        copied = store.read_jsonl(out)
        self.assertEqual([r["skill"] for r in copied], ["component_add", "gameobject_create", "material_create",
                                                         "scene_save"])
        self.assertNotIn("UnitySkillsEval", json.dumps(copied))

    def test_no_files_means_unavailable(self):
        summary = telemetry.freeze(self.dir, self.T0, self.T1, os.path.join(self.dir, "copy.jsonl"))
        self.assertFalse(summary["available"])
        self.assertEqual(summary["skillCalls"], 0)


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
