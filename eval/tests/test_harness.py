"""Offline unit tests for the harness core (no Unity, no network)."""
import json
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

from harness import classify as C            # noqa: E402
from harness import env as E                 # noqa: E402
from harness import fixture as F             # noqa: E402
from harness import journal as J             # noqa: E402
from harness import metrics as M             # noqa: E402
from harness import subject as S             # noqa: E402
from harness import uyaml as Y               # noqa: E402
from harness import verify as V              # noqa: E402
from harness.instance import expected_instance_id  # noqa: E402


class ScorecardTests(unittest.TestCase):
    SPEC = [("a", 3, True, "a"), ("b", 2, False, "b"), ("c", 5, False, "c")]

    def test_weights_must_sum_to_ten(self):
        with self.assertRaises(ValueError):
            V.Scorecard([("a", 3, False, ""), ("b", 3, False, "")])

    def test_partial_score_and_strict(self):
        card = V.Scorecard(self.SPEC)
        card.check("a", True)
        card.check("b", False)
        card.check("c", True)
        card.finish()
        self.assertEqual(card.score, 8.0)
        self.assertFalse(card.strict)
        self.assertEqual(card.failed(), ["b"])
        self.assertEqual(card.as_dict()["completion"], 80.0)

    def test_unevaluated_is_unobservable_failure(self):
        card = V.Scorecard(self.SPEC)
        card.check("a", True)
        card.check("b", True)
        card.finish()
        self.assertFalse(card.strict)
        self.assertIn("unobservable", card.results["c"]["note"])
        self.assertEqual(card.critical_failed, [])

    def test_critical_failure_listed(self):
        card = V.Scorecard(self.SPEC)
        card.check("a", False)
        card.finish()
        self.assertEqual(card.critical_failed, ["a"])

    def test_fraction_partial_credit(self):
        card = V.Scorecard(self.SPEC)
        card.check("a", True)
        card.check("b", True)
        card.fraction("c", 0.6)
        card.finish()
        self.assertEqual(card.score, 8.0)
        self.assertFalse(card.strict)
        self.assertEqual(card.failed(), ["c"])
        card.fraction("c", 1.0)
        self.assertTrue(card.strict)


class GeometryTests(unittest.TestCase):
    def test_euler_y90(self):
        self.assertTrue(V.quat_close(V.quat_from_euler(0, 90, 0), (0, 0.70710678, 0, 0.70710678)))

    def test_euler_x90(self):
        self.assertTrue(V.quat_close(V.quat_from_euler(90, 0, 0), (0.70710678, 0, 0, 0.70710678)))

    def test_negated_quaternion_is_same_rotation(self):
        q = V.quat_from_euler(30, 45, 60)
        self.assertTrue(V.quat_close(q, tuple(-x for x in q)))

    def test_small_angle_tolerance(self):
        self.assertFalse(V.quat_close(V.quat_from_euler(0, 0, 0), V.quat_from_euler(0, 1.0, 0)))
        self.assertTrue(V.quat_close(V.quat_from_euler(0, 0, 0), V.quat_from_euler(0, 0.05, 0)))

    def test_unity_composition_order(self):
        # Euler(90, 90, 0) = AngleAxis(90, up) * AngleAxis(90, right): known value (0.5, 0.5, -0.5, 0.5)
        self.assertTrue(V.quat_close(V.quat_from_euler(90, 90, 0), (0.5, 0.5, -0.5, 0.5)))

    def test_floats_and_vectors(self):
        self.assertEqual(V.floats("0,0.5,-1e-3"), [0.0, 0.5, -0.001])
        self.assertEqual(V.floats({"x": 1, "y": 2, "z": 3}), [1.0, 2.0, 3.0])
        self.assertTrue(V.vec_close("(1.0, 2.0, 3.0)", {"x": 1, "y": 2, "z": 3.0005}))
        self.assertFalse(V.vec_close("1,2", "1,2,3"))

    def test_parse_ref(self):
        self.assertEqual(V.parse_ref("-12484:UnityEngine.Rigidbody:R_A"), (-12484, "UnityEngine.Rigidbody", "R_A"))
        self.assertEqual(V.parse_ref("27884:UnityEngine.Material:Lit:2"), (27884, "UnityEngine.Material", "Lit:2"))
        self.assertIsNone(V.parse_ref("null"))
        self.assertIsNone(V.parse_ref(None))


class JournalTests(unittest.TestCase):
    ROOT, ASSETS, SCENE = "EV_r1", "Assets/__UnitySkillsEval/r1", "Assets/__UnitySkillsEval/r1/Eval_r1.unity"

    def events(self):
        return [
            {"seq": 1, "type": "scene_changes", "source": "rest", "payload": {"changes": [
                {"kind": "gameobject_created", "path": "EV_r1/A", "scenePath": self.SCENE},
                {"kind": "gameobject_reparented", "fromPath": "EV_r1/A", "toPath": "EV_r1/B/A",
                 "newParentPath": "EV_r1/B"},
                {"kind": "property_changed", "path": "Main Camera", "scenePath": "Assets/Scenes/S.unity"}]}},
            {"seq": 2, "type": "file_changes", "source": "rest", "payload": {
                "imported": [self.ASSETS + "/M.mat", self.ASSETS + "/M.mat.meta", "Assets/Other.mat"],
                "moved": [{"from": self.ASSETS + "/a.mat", "to": self.ASSETS + "/b/a.mat"}]}},
            {"seq": 3, "type": "scene_saved", "source": "rest", "payload": {"path": "Assets/Scenes/S.unity"}},
            {"seq": 4, "type": "scene_saved", "source": "editor", "payload": {"path": self.SCENE}},
        ]

    def test_flatten_and_scope(self):
        recs = J.flatten(self.events())
        kinds = [r["kind"] for r in recs]
        self.assertEqual(kinds.count("imported"), 3)
        bad = J.scope_violations(recs, self.ROOT, self.ASSETS, self.SCENE)
        bad_kinds = sorted((r["kind"], r.get("path") or r.get("assetPath")) for r in bad)
        self.assertEqual(bad_kinds, [("imported", "Assets/Other.mat"), ("property_changed", "Main Camera"),
                                     ("scene_saved", "Assets/Scenes/S.unity")])
        self.assertEqual(len(J.non_rest(recs)), 1)

    def test_under(self):
        self.assertTrue(J.under("EV_r1/A", "EV_r1"))
        self.assertFalse(J.under("EV_r10/A", "EV_r1"))
        self.assertTrue(J.under("Assets\\X\\y.mat", "Assets/X"))


class MetricsTests(unittest.TestCase):
    def stream(self, tmp):
        events = [
            {"type": "system", "subtype": "init", "model": "claude-opus-5-5[1m]", "cwd": "/tmp/eve-s/x",
             "tools": ["Bash"], "skills": [], "mcp_servers": [], "claude_code_version": "2.1.287"},
            {"type": "assistant", "message": {"id": "m1", "model": "claude-opus-5-5", "usage": {
                "input_tokens": 10, "output_tokens": 5, "cache_read_input_tokens": 100},
                "content": [{"type": "tool_use", "id": "t1", "name": "Bash", "input": {"command":
                    "curl -s 'http://localhost:8091/health'; curl -s localhost:8091/skills/batch -d "
                    "'{\"steps\":[{\"skill\":\"gameobject_create\"},{\"skill\":\"material_create\"}]}'; "
                    "curl -s http://127.0.0.1:8090/skill/gameobject_find"}}]}},
            {"type": "user", "message": {"content": [{"type": "tool_result", "tool_use_id": "t1",
                                                      "content": "{\"errorCode\":\"TARGET_NOT_FOUND\"}"}]}},
            {"type": "system", "subtype": "api_retry"},
            {"type": "result", "subtype": "success", "is_error": False, "num_turns": 3, "total_cost_usd": 0.5,
             "usage": {"input_tokens": 10, "output_tokens": 5, "cache_read_input_tokens": 90},
             "modelUsage": {"claude-opus-5-5[1m]": {"inputTokens": 10, "outputTokens": 5,
                                                    "cacheReadInputTokens": 100, "cacheCreationInputTokens": 0,
                                                    "costUSD": 0.5, "costBasis": "list"}}},
        ]
        path = os.path.join(tmp, "s.jsonl")
        with open(path, "w") as handle:
            handle.write("\n".join(json.dumps(e) for e in events) + "\n")
        return path

    def test_parse(self):
        with tempfile.TemporaryDirectory() as tmp:
            row = M.parse(self.stream(tmp), 8091, is_read_only=lambda s: s.endswith("_find"))
        self.assertEqual(row["httpTotal"], 3)
        self.assertEqual(row["httpByKind"], {"health": 1, "exec_write": 1, "exec_read": 1})
        self.assertEqual(row["foreignPorts"], [8090])
        self.assertEqual(row["batchSteps"], 2)
        self.assertEqual(row["tokens"]["processed"], 115)
        self.assertEqual(row["tokens"]["source"], "modelUsage")
        self.assertEqual(row["tokens"]["resultUsageMismatch"], 10)
        self.assertEqual(row["errorCodes"], {"TARGET_NOT_FOUND": 1})
        self.assertEqual(row["apiRetries"], 1)
        self.assertTrue(M.model_matches("claude-opus-5-5[1M]", row))
        self.assertFalse(M.model_matches("claude-sonnet-5-5[1M]", row))

    def test_kimi_alias_is_mismatch(self):
        row = {"init": {"model": "kimi-k3[1M]"}, "assistantModels": ["kimi-k3"]}
        self.assertFalse(M.model_matches("claude-sonnet-5-5[1M]", row))


class ClassifyTests(unittest.TestCase):
    def card(self, passes):
        card = V.Scorecard([("a", 5, False, ""), ("b", 5, False, "")])
        for aid, ok in zip(("a", "b"), passes):
            card.check(aid, ok)
        return card.finish()

    def test_outcomes(self):
        self.assertEqual(C.classify(self.card([True, True]))[0], "pass")
        self.assertEqual(C.classify(self.card([True, False]))[0], "partial")
        self.assertEqual(C.classify(self.card([False, False]))[0], "fail")

    def test_infra_and_contamination_win(self):
        subject = {"model": "m", "timedOut": False, "returncode": 0}
        ok_metrics = {"result": {"subtype": "success"}, "apiRetries": 0}
        self.assertEqual(C.classify(self.card([True, True]), subject, ok_metrics, model_ok=False)[0], "invalid")
        self.assertEqual(C.classify(self.card([True, True]), subject, {"result": {}, "apiRetries": 3})[0], "invalid")
        self.assertEqual(C.classify(self.card([True, True]), subject, ok_metrics,
                                    drift={"currentMode": ("bypass", "auto")})[0], "invalid")
        self.assertEqual(C.classify(self.card([True, True]), subject, dict(ok_metrics, foreignPorts=[8090]))[0],
                         "contaminated")
        self.assertEqual(C.classify(self.card([True, True]), subject, dict(ok_metrics, suspiciousAccess=[
            {"marker": "/CodeSpace/Unity-Skills", "tool": "Read", "input": "x"}]))[0], "contaminated")
        self.assertEqual(C.classify(self.card([True, True]), subject, ok_metrics, tampered=["tasks"])[0],
                         "contaminated")
        self.assertEqual(C.classify(self.card([True, True]), subject, ok_metrics, non_rest_changes=2)[0],
                         "contaminated")
        self.assertEqual(C.classify(self.card([True, True]), subject, ok_metrics, journal_complete=False)[0],
                         "invalid")
        self.assertEqual(C.classify(self.card([True, True]), verifier_error="boom")[0], "invalid")

    def test_ledger_rerunnable(self):
        with tempfile.TemporaryDirectory() as tmp:
            ledger = C.Ledger(os.path.join(tmp, "l.jsonl"))
            ledger.append({"attemptId": "x1", "suite": "s", "task": "T", "model": "m", "kind": "model",
                           "status": "invalid"})
            ledger.append({"attemptId": "x2", "suite": "s", "task": "T", "model": "m", "kind": "model",
                           "status": "pass"})
            self.assertEqual([e["attemptId"] for e in ledger.rerunnable("s", "T", "m")], ["x1"])
            ledger.append({"attemptId": "x3", "suite": "s", "task": "T", "model": "m", "kind": "model",
                           "status": "fail", "rerunOf": "x1"})
            self.assertEqual(ledger.rerunnable("s", "T", "m"), [])
            self.assertEqual(ledger.valid_count("s", "T", "m"), 2)


class MiscTests(unittest.TestCase):
    def test_instance_id_formula(self):
        self.assertEqual(expected_instance_id("skill", "/Users/betsy/UnityProject/skill"), "skill_51873CE6")
        self.assertEqual(expected_instance_id("Unity Skills-Eval", "/x"), "UnitySkillsEval_" +
                         __import__("hashlib").sha256(b"/x").digest()[:4].hex().upper())

    def test_scope_paths_and_run_id(self):
        run_id = F.new_run_id()
        scope = F.Scope(run_id)
        self.assertEqual(scope.object_root, "EV_" + run_id)
        self.assertTrue(scope.scene_path.startswith(scope.asset_root + "/"))
        with self.assertRaises(ValueError):
            F.Scope("../evil")

    def test_drift(self):
        self.assertEqual(E.drift({"currentMode": "bypass", "x": 1}, {"currentMode": "auto", "x": 2}),
                         {"currentMode": ("bypass", "auto")})

    def test_subject_env_has_no_session_vars_and_records_no_secrets(self):
        os.environ["CLAUDE_CODE_SESSION_ID"] = "s"
        os.environ["ANTHROPIC_AUTH_TOKEN"] = os.environ.get("ANTHROPIC_AUTH_TOKEN", "secret")
        env = S.child_env()
        self.assertNotIn("CLAUDE_CODE_SESSION_ID", env)
        self.assertEqual(S.env_record().get("ANTHROPIC_AUTH_TOKEN"), "<set>")

    def test_redact(self):
        cmd = ["claude", "-p", "PROMPT", "--append-system-prompt", "DOC", "--model", "m"]
        self.assertEqual(S.redact(cmd, "PROMPT"), ["claude", "-p", "<prompt>", "--append-system-prompt",
                                                  "<SKILL.md contents>", "--model", "m"])


class UYamlTests(unittest.TestCase):
    TEXT = """%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!21 &2100000
Material:
  m_Name: Glow
  m_Shader: {fileID: 4800000, guid: 933532a4fcc9baf4fa0491de14d08ed7, type: 3}
  m_ValidKeywords:
  - _EMISSION
  m_SavedProperties:
    serializedVersion: 3
    m_Colors:
    - _BaseColor: {r: 0.8, g: 0.1, b: 0.1, a: 1}
    - _EmissionColor: {r: 2, g: 1, b: 0, a: 1}
--- !u!1001 &55
PrefabInstance:
  m_Modification:
    m_Modifications:
    - target: {fileID: 1, guid: abc, type: 3}
      propertyPath: m_Mass
      value: 50
      objectReference: {fileID: 0}
    m_RemovedComponents: []
  m_SourcePrefab: {fileID: 100100000, guid: abc, type: 3}
"""

    def test_wrapped_flow_mapping(self):
        text = ("--- !u!114 &11400000\nMonoBehaviour:\n  projectilePrefab: {fileID: 3707123456789012345, guid: abcdef,\n"
                "    type: 3}\n  fireRate: 2.5\n")
        doc = Y.loads(text)[0]
        self.assertEqual(doc.get("projectilePrefab", "guid"), "abcdef")
        self.assertEqual(doc.get("projectilePrefab", "type"), "3")
        self.assertEqual(doc.get("fireRate"), "2.5")

    def test_material_and_prefab_instance(self):
        docs = Y.loads(self.TEXT)
        self.assertEqual([d.type_name for d in docs], ["Material", "PrefabInstance"])
        mat = docs[0]
        self.assertEqual(mat.get("m_Shader", "guid"), "933532a4fcc9baf4fa0491de14d08ed7")
        self.assertEqual(mat.get("m_ValidKeywords"), ["_EMISSION"])
        self.assertEqual(Y.material_colors(mat)["_EmissionColor"], (2.0, 1.0, 0.0, 1.0))
        mods = docs[1].get("m_Modification", "m_Modifications")
        self.assertEqual(mods[0]["propertyPath"], "m_Mass")
        self.assertEqual(mods[0]["value"], "50")
        self.assertEqual(mods[0]["target"]["guid"], "abc")
        self.assertEqual(docs[1].get("m_Modification", "m_RemovedComponents"), [])
        self.assertEqual(docs[1].get("m_SourcePrefab", "guid"), "abc")


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
