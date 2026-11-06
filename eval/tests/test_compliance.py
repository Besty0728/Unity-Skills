"""eval/tools/compliance.py: request shape from agent streams, skill executions from attempt telemetry."""
import json
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "tools"))
import compliance as C  # noqa: E402

SCHEMA = {
    "gameobject_get_info": {"flags": ["readOnly"]},
    "gameobject_create": {"flags": ["tracksWorkflow", "mutatesScene"]},
    "gameobject_delete": {"operation": ["Delete"], "flags": ["tracksWorkflow", "mutatesScene"]},
    "script_create": {"flags": ["tracksWorkflow", "mutatesAssets", "mayTriggerReload"]},
    "scene_save": {"riskLevel": "high", "flags": ["tracksWorkflow", "mutatesAssets"]},
    "editor_play": {"flags": ["mayEnterPlayMode"]},
}


def bash(command):
    return {"type": "assistant", "message": {"content": [{"type": "tool_use", "name": "Bash",
                                                          "input": {"command": command}}]}}


def read(path):
    return {"type": "assistant", "message": {"content": [{"type": "tool_use", "name": "Read",
                                                          "input": {"file_path": path}}]}}


def result(text):
    return {"type": "user", "message": {"content": [{"type": "tool_result", "content": text}]}}


class ExpandTests(unittest.TestCase):
    def test_shell_variables_resolve_within_one_command(self):
        cmd = "U='http://127.0.0.1:8091'; E=\"$U/skill\"; curl -s \"${E}/gameobject_create\" -d '{}'"
        self.assertIn("http://127.0.0.1:8091/skill/gameobject_create", C.expand(cmd))

    def test_unknown_variables_stay(self):
        self.assertEqual(C.expand("curl $NOPE/health"), "curl $NOPE/health")


class HttpCallTests(unittest.TestCase):
    def test_header_variable_pins_every_call(self):
        cmd = ("H='X-Expect-Instance: Eval_1'; U=http://127.0.0.1:8091; "
               "curl -H \"$H\" \"$U/health\"; curl -H \"$H\" \"$U/skill/gameobject_create\" -d '{}'")
        self.assertEqual(C.http_calls(cmd), [("/health", "", True), ("/skill/gameobject_create", "", True)])

    def test_query_pin_and_unpinned_call(self):
        pinned = C.http_calls("curl 'http://localhost:8090/skill/x?expectProject=P&wire=v2'")
        bare = C.http_calls("curl http://localhost:8090/skill/x")
        self.assertTrue(pinned[0][2])
        self.assertFalse(bare[0][2])

    def test_a_base_url_alone_is_no_request(self):
        self.assertEqual(C.http_calls("U='http://127.0.0.1:8091'; echo ready"), [])


class StreamTests(unittest.TestCase):
    def stream(self, events):
        tmp = tempfile.NamedTemporaryFile("w", suffix=".jsonl", delete=False, encoding="utf-8")
        with tmp:
            for event in events:
                tmp.write(json.dumps(event) + "\n")
        self.addCleanup(os.remove, tmp.name)
        return tmp.name

    def test_counts_requests_discovery_docs_and_error_codes(self):
        path = self.stream([
            bash("U=http://127.0.0.1:8091; curl $U/health && curl \"$U/skills/recommend?intent=a&expectProject=P\""),
            read("/tmp/eve-d/abc/docs/skills/console/SKILL.md"),
            bash("curl http://127.0.0.1:8091/skill/gameobject_create -d '{}'"),
            bash("cat /tmp/eve-d/abc/docs/references/protocol-discovery.md"),
            result('{"status":"error","errorCode":"UNKNOWN_PARAM"}'),
            bash("curl 'http://127.0.0.1:8091/jobs/ab12?wait=90'"),
        ])
        row = C.stream_metrics(path)
        self.assertTrue(row["healthBundled"])
        self.assertEqual((row["health"], row["recommend"], row["skillCalls"], row["pinned"], row["polls"]),
                         (1, 1, 1, 0, 1))
        self.assertEqual(row["docs"], ["references/protocol-discovery.md", "skills/console/SKILL.md"])
        self.assertEqual(row["errors"], {"UNKNOWN_PARAM": 1})

    def test_a_reload_503_is_counted_apart_from_compiling(self):
        row = C.stream_metrics(self.stream([
            bash("curl http://127.0.0.1:8091/jobs/ab12?wait=90"),
            result('{"errorCode":"COMPILING","error":"Server reloading; retry the same URL"}'
                   '{"errorCode":"COMPILING","error":"Unity is compiling or reloading scripts"}'),
        ]))
        self.assertEqual(row["errors"], {"COMPILING_RELOAD": 1, "COMPILING": 1})

    def test_first_request_without_health_is_not_bundled(self):
        row = C.stream_metrics(self.stream([bash("curl http://127.0.0.1:8091/skill/x"),
                                            bash("curl http://127.0.0.1:8091/health")]))
        self.assertFalse(row["healthBundled"])


class TelemetryTests(unittest.TestCase):
    def test_reads_writes_batches_and_dry_runs(self):
        rows = [
            {"skill": "gameobject_get_info", "agent": "UnitySkillsEval", "mode": "execute", "ok": True},
            {"skill": "gameobject_get_info", "agent": "A", "mode": "execute", "ok": False},
            {"skill": "gameobject_create", "agent": "A", "mode": "batch_step", "ok": True},
            {"skill": "gameobject_create", "agent": "A", "mode": "execute", "ok": True},
            {"skill": "script_create", "agent": "A", "mode": "dryRun", "ok": True},
            {"skill": "script_create", "agent": "A", "mode": "execute", "ok": True},
            {"skill": "gameobject_delete", "agent": "A", "mode": "batch_step_dryRun", "ok": False},
            {"skill": "gameobject_delete", "agent": "A", "mode": "execute", "ok": True},
            {"skill": "gameobject_delete", "agent": "B", "mode": "execute", "ok": True},
        ]
        out = C.telemetry_metrics(rows, SCHEMA)
        self.assertEqual((out["reads"], out["writes"], out["batchWrites"]), (1, 5, 1))
        self.assertEqual((out["failed"], out["failedDryRun"], out["dryRuns"]), (1, 1, 2))
        self.assertEqual(out["riskyWithoutDryRun"], 0, "each risky execution followed a dryRun of the same skill")

    def test_risky_execution_without_a_dry_run_counts(self):
        rows = [{"skill": "gameobject_delete", "agent": "A", "mode": "execute", "ok": True},
                {"skill": "gameobject_delete", "agent": "A", "mode": "dryRun", "ok": True}]
        out = C.telemetry_metrics(rows, SCHEMA)
        self.assertEqual(out["riskyWithoutDryRun"], 1)
        self.assertEqual(out["riskySkills"], {"gameobject_delete": 1})

    def test_high_risk_and_play_mode_count_only_under_the_old_rule(self):
        rows = [{"skill": "scene_save", "agent": "A", "mode": "execute", "ok": True},
                {"skill": "editor_play", "agent": "A", "mode": "execute", "ok": True},
                {"skill": "script_create", "agent": "A", "mode": "execute", "ok": True}]
        out = C.telemetry_metrics(rows, SCHEMA)
        self.assertEqual((out["riskyWithoutDryRun"], out["riskyWithoutDryRunLegacy"]), (1, 3))
        self.assertEqual(out["riskySkills"], {"script_create": 1})


class LatestAttemptTests(unittest.TestCase):
    def test_a_later_attempt_in_another_suite_replaces_the_first(self):
        with tempfile.TemporaryDirectory() as root:
            for suite, at, status in (("g-a", "2026-11-01T00:00:00Z", "invalid"), ("g-b", "2026-11-02T00:00:00Z", "pass")):
                os.makedirs(os.path.join(root, suite))
                with open(os.path.join(root, suite, "ledger.jsonl"), "w", encoding="utf-8") as handle:
                    handle.write(json.dumps({"kind": "launch", "launchId": "l1", "dir": "d"}) + "\n")
                    handle.write(json.dumps({"kind": "attempt", "task": "S01", "recordedAt": at, "status": status,
                                             "launchId": "l1"}) + "\n")
            chosen = C.latest_attempts("g-", root)
            self.assertEqual(chosen["S01"][0]["status"], "pass")
            self.assertTrue(chosen["S01"][2].endswith("g-b"))


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
