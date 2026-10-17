"""Offline Codex adapter tests; forwarding tests use local fixture servers only."""
import http.client
import http.server
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import threading
import unittest
from unittest import mock
import urllib.parse

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
from harness import metrics as M
from harness import subject as S
from harness.codex_proxy import CodexProxy, ResponseMetadata, _BodyMetadata


class CommandTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.docs = self.temp.name
        Path(self.docs, "SKILL.md").write_text("Frozen protocol.\n", encoding="utf-8")

    def test_claude_default_remains_compatible(self):
        command = S.build_command("TASK", "opus", "high", self.docs, 2.5, True)
        self.assertEqual(command[:6], ["claude", "-p", "TASK", "--model", "opus", "--effort"])
        self.assertIn("--max-budget-usd", command)
        self.assertEqual(command[command.index("--max-budget-usd") + 1], "2.50")
        self.assertIn("--append-system-prompt", command)

    def test_codex_isolation_flags_and_no_fake_budget(self):
        command = S.build_command("TASK", "gpt-6.1-sol", "high", self.docs, 2.5, True, host="codex")
        self.assertEqual(command[:2], ["codex", "exec"])
        for flag in ("--json", "--ephemeral", "--skip-git-repo-check", "--ignore-user-config", "--ignore-rules"):
            self.assertIn(flag, command)
        self.assertNotIn("--max-budget-usd", command)
        self.assertIn("project_doc_max_bytes=0", command)
        self.assertIn("features.skip_host_skill_discovery=true", command)
        self.assertIn('model_reasoning_effort="high"', command)
        self.assertEqual(command[-1], "TASK")
        redacted = S.redact(command + ["-c", 'model_providers.custom={experimental_bearer_token="secret"}'], "TASK")
        self.assertNotIn("Frozen protocol", " ".join(redacted))
        self.assertNotIn("secret", " ".join(redacted))

    def test_unknown_host_rejected(self):
        with self.assertRaises(ValueError):
            S.build_command("TASK", "m", "high", self.docs, 1, False, host="other")

    def test_codex_session_environment_is_removed(self):
        with mock.patch.dict(os.environ, {"CODEX_HOME": "/secret/home", "CODEX_THREAD_ID": "thread",
                                          "CLAUDE_CODE_SESSION_ID": "session", "OPENAI_API_KEY": "secret"}):
            env = S.child_env("codex")
            self.assertNotIn("CODEX_HOME", env)
            self.assertNotIn("CODEX_THREAD_ID", env)
            self.assertNotIn("CLAUDE_CODE_SESSION_ID", env)
            self.assertEqual(S.env_record("codex")["OPENAI_API_KEY"], "<set>")

    def test_claude_imports_only_missing_auth_transport_from_user_settings(self):
        Path(self.docs, "settings.json").write_text(json.dumps({"env": {
            "ANTHROPIC_AUTH_TOKEN": "configured-secret", "ANTHROPIC_API_KEY": "configured-key",
            "ANTHROPIC_BASE_URL": "https://fixture.invalid", "ANTHROPIC_DEFAULT_OPUS_MODEL": "alias",
            "CLAUDE_EFFORT": "low", "MAX_THINKING_TOKENS": "1"}}), encoding="utf-8")
        with mock.patch.dict(os.environ, {"CLAUDE_CONFIG_DIR": self.docs,
                                          "ANTHROPIC_API_KEY": "existing-key"}, clear=True):
            env = S.child_env("claude")
            self.assertEqual(env["ANTHROPIC_AUTH_TOKEN"], "configured-secret")
            self.assertEqual(env["ANTHROPIC_API_KEY"], "existing-key")
            self.assertEqual(env["ANTHROPIC_BASE_URL"], "https://fixture.invalid")
            self.assertNotIn("ANTHROPIC_DEFAULT_OPUS_MODEL", env)
            self.assertNotIn("CLAUDE_EFFORT", env)
            self.assertNotIn("MAX_THINKING_TOKENS", env)
            self.assertEqual(S.env_record()["ANTHROPIC_AUTH_TOKEN"], "<set>")
            self.assertNotIn("ANTHROPIC_AUTH_TOKEN", S.child_env("codex"))

    @unittest.skipUnless(sys.version_info >= (3, 11), "subject.py reads config.toml with tomllib, a 3.11+ module")
    def test_neutral_codex_home_copies_auth_and_only_transport(self):
        source = Path(self.docs, "source")
        source.mkdir()
        (source / "auth.json").write_text('{"credential":"secret"}', encoding="utf-8")
        (source / "AGENTS.md").write_text("Leaked global instruction", encoding="utf-8")
        (source / "config.toml").write_text(
            'model_provider="custom"\ndeveloper_instructions="leaked"\n'
            '[model_providers.custom]\nname="Fixture"\nbase_url="http://127.0.0.1:1/v1"\n'
            'wire_api="responses"\nexperimental_bearer_token="secret"\n', encoding="utf-8")
        with mock.patch.dict(os.environ, {"CODEX_HOME": str(source)}):
            home, command, proxy = S._codex_home()
        try:
            self.assertEqual(os.listdir(home), ["auth.json"])
            self.assertEqual(Path(home, "auth.json").read_text(), '{"credential":"secret"}')
            self.assertEqual(os.stat(Path(home, "auth.json")).st_mode & 0o777, 0o600)
            self.assertNotIn("leaked", " ".join(command))
            self.assertIn(proxy.base_url, " ".join(command))
            self.assertNotIn("secret", " ".join(S.redact(command, "TASK")))
        finally:
            proxy.close()
            shutil.rmtree(home)


class MetricsTests(unittest.TestCase):
    def parse(self, events, identity=None):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp, "subject.stream.jsonl")
            path.write_text("\n".join(json.dumps(e) for e in events) + "\n", encoding="utf-8")
            if identity is not None:
                Path(temp, "subject.identity.json").write_text(json.dumps(identity), encoding="utf-8")
            return M.parse(str(path), 8091, is_read_only=lambda name: name.endswith("_find"), host="codex")

    def events(self):
        command = "curl http://localhost:8091/health; curl http://127.0.0.1:8090/skill/gameobject_find"
        item = {"id": "i1", "type": "command_execution", "command": command}
        return [
            {"type": "thread.started", "thread_id": "thread"},
            {"type": "turn.started"},
            {"type": "item.started", "item": dict(item, status="in_progress")},
            {"type": "item.completed", "item": dict(item, status="completed", exit_code=0,
                aggregated_output='{"errorCode":"TARGET_NOT_FOUND"}')},
            {"type": "item.completed", "item": {"id": "i2", "type": "agent_message", "text": "Done"}},
            {"type": "turn.completed", "usage": {"input_tokens": 100, "cached_input_tokens": 20,
                "cache_write_input_tokens": 5, "output_tokens": 10, "reasoning_output_tokens": 3}},
        ]

    def test_missing_identity_is_not_requested_model(self):
        row = self.parse(self.events())
        self.assertIsNone(row["init"]["model"])
        self.assertEqual(row["identitySource"], "unavailable")
        self.assertFalse(M.model_matches("gpt-6.1-sol", row))
        self.assertEqual(row["toolCounts"], {"command_execution": 1})
        self.assertEqual(row["httpTotal"], 2)
        self.assertEqual(row["foreignPorts"], [8090])
        self.assertEqual(row["errorCodes"], {"TARGET_NOT_FOUND": 1})
        self.assertEqual(row["tokens"]["input"], 75)
        self.assertEqual(row["tokens"]["processed"], 110)
        self.assertEqual(row["tokens"]["reasoningOutput"], 3)
        self.assertIsNone(row["result"]["total_cost_usd"])
        self.assertIsNone(row["tokens"]["costUSD"])

    def test_provider_identity_is_required_to_match(self):
        identity = {"source": "response.model", "models": ["gpt-6.1-sol"]}
        row = self.parse(self.events(), identity)
        self.assertEqual(row["identitySource"], "response.model")
        self.assertTrue(M.model_matches("gpt-6.1-sol", row))
        self.assertFalse(M.model_matches("gpt-6-astra", row))
        identity["models"].append("gpt-6-luna")
        self.assertFalse(M.model_matches("gpt-6.1-sol", self.parse(self.events(), identity)))

    def test_provider_usage_is_cross_checked_and_can_recover_missing_terminal_usage(self):
        identity = {"source": "response.model", "models": ["gpt-6.1-sol"], "observations": [
            {"event": "response.completed", "responseId": "r", "model": "gpt-6.1-sol",
             "usage": {"input_tokens": 100, "output_tokens": 10,
                       "input_tokens_details": {"cached_tokens": 20, "cache_write_tokens": 5}}}]}
        row = self.parse(self.events(), identity)
        self.assertEqual(row["evidenceProblems"], [])
        self.assertIn("gpt-6.1-sol", row["tokens"]["byModel"])
        identity["observations"][0]["usage"]["output_tokens"] = 11
        row = self.parse(self.events(), identity)
        self.assertIn("provider_usage_mismatch:output", row["evidenceProblems"])
        row = self.parse(self.events()[:-1], identity)
        self.assertEqual(row["tokens"]["source"], "response.usage")
        self.assertEqual(row["tokens"]["processed"], 111)
        self.assertEqual(row["evidenceProblems"], [])

    def test_claude_missing_assistant_identity_and_degraded_usage(self):
        self.assertFalse(M.model_matches("opus", {"init": {"model": "opus"}, "assistantModels": []}))
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp, "s.jsonl")
            path.write_text(json.dumps({"type": "result", "usage": {"input_tokens": 3}}), encoding="utf-8")
            row = M.parse(str(path), 8091)
        self.assertEqual(row["evidenceProblems"], ["canonical_model_usage_unavailable"])
        self.assertTrue(row["tokens"]["degraded"])

    def test_errors_retries_and_suspicious_access(self):
        row = self.parse([
            {"type": "error", "message": "Reconnecting 1/5 after HTTP 503"},
            {"type": "item.completed", "item": {"id": "e", "type": "command_execution",
                "command": "cat /Users/person/.codex/AGENTS.md", "exit_code": 1, "status": "failed"}},
            {"type": "turn.failed", "error": {"message": "HTTP 503 unavailable"}},
        ])
        self.assertEqual(row["apiRetries"], 1)
        self.assertEqual(row["toolErrors"], 1)
        self.assertEqual(row["suspiciousAccess"][0]["marker"], "/.codex/")
        self.assertTrue(row["result"]["is_error"])
        self.assertIn("503", row["result"]["api_error_status"])

    def test_auto_detection_and_multiple_usage_events(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp, "subject.stream.jsonl")
            events = self.events() + [{"type": "turn.completed", "usage": {"input_tokens": 3, "output_tokens": 2}}]
            path.write_text("\n".join(json.dumps(e) for e in events), encoding="utf-8")
            row = M.parse(str(path), 8091)
        self.assertEqual(row["host"], "codex")
        self.assertEqual(row["tokens"]["processed"], 115)


class MetadataTests(unittest.TestCase):
    def test_fragmented_sse_only_records_response_metadata(self):
        records = ResponseMetadata()
        collector = _BodyMetadata("text/event-stream", records)
        payload = (b'data: {"type":"response.output_text.delta","delta":"secret text"}\r\n\r\n'
                   b'data: {"type":"response.created","response":{"id":"r","model":"actual",'
                   b'"output":[{"text":"secret output"}]}}\r\n\r\n'
                   b'data: {"type":"response.completed","response":{"id":"r","model":"actual",'
                   b'"usage":{"input_tokens":10,"output_tokens":5,"secret":"credential"}}}\n\n'
                   b'data: [DONE]\n\n')
        for offset in range(0, len(payload), 7):
            collector.feed(payload[offset:offset + 7])
        collector.finish()
        record = records.snapshot()
        self.assertEqual(record["models"], ["actual"])
        self.assertEqual(record["observations"][-1]["usage"], {"input_tokens": 10, "output_tokens": 5})
        self.assertNotIn("secret", json.dumps(record))
        self.assertNotIn("credential", json.dumps(record))

    def test_json_response_and_missing_model(self):
        records = ResponseMetadata()
        collector = _BodyMetadata("application/json", records)
        collector.feed(b'{"object":"response","id":"r","model":"actual","usage":{"input_tokens":3}}')
        collector.finish()
        self.assertEqual(records.snapshot()["models"], ["actual"])
        absent = ResponseMetadata()
        absent.observe({"type": "response.completed", "response": {"id": "r"}})
        self.assertIsNone(absent.snapshot()["source"])


class ProxyTests(unittest.TestCase):
    def fixture(self, body, content_type="text/event-stream", status=200):
        captured = {}

        class Handler(http.server.BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass

            def do_POST(self):
                captured["path"] = self.path
                captured["auth"] = self.headers.get("Authorization")
                captured["body"] = self.rfile.read(int(self.headers.get("Content-Length", 0)))
                self.send_response(status)
                self.send_header("Content-Type", content_type)
                self.send_header("Content-Length", str(len(body)))
                self.send_header("x-request-id", "request-123")
                self.end_headers()
                self.wfile.write(body)

        server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        self.addCleanup(server.server_close)
        self.addCleanup(server.shutdown)
        return server, captured

    def forward(self, body, content_type="text/event-stream", status=200):
        upstream, captured = self.fixture(body, content_type, status)
        proxy = CodexProxy("http://127.0.0.1:%d/v1" % upstream.server_port)
        self.addCleanup(proxy.close)
        address = urllib.parse.urlsplit(proxy.base_url)
        self.assertEqual(address.hostname, "127.0.0.1")
        connection = http.client.HTTPConnection(address.hostname, address.port)
        self.addCleanup(connection.close)
        connection.request("POST", "/v1/responses?query=x", body=b'{"input":"secret prompt"}',
                           headers={"Authorization": "Bearer secret credential", "Content-Type": "application/json"})
        response = connection.getresponse()
        received = response.read()
        return response.status, received, captured, proxy.metadata()

    def test_sse_forwarding_preserves_bytes_route_and_auth(self):
        body = b'data: {"type":"response.completed","response":{"id":"r","model":"actual","output":["secret content"]}}\n\n'
        status, received, captured, metadata = self.forward(body)
        self.assertEqual(status, 200)
        self.assertEqual(received, body)
        self.assertEqual(captured["path"], "/v1/responses?query=x")
        self.assertEqual(captured["auth"], "Bearer secret credential")
        self.assertEqual(captured["body"], b'{"input":"secret prompt"}')
        self.assertEqual(metadata["models"], ["actual"])
        self.assertEqual(metadata["requestIds"], ["request-123"])
        self.assertNotIn("secret", json.dumps(metadata))

    def test_json_and_upstream_error_are_forwarded_without_fabricating_identity(self):
        body = b'{"object":"response","model":"actual","id":"r"}'
        _, received, _, metadata = self.forward(body, "application/json")
        self.assertEqual(received, body)
        self.assertEqual(metadata["models"], ["actual"])
        status, received, _, metadata = self.forward(b'{"error":"secret"}', "application/json", 503)
        self.assertEqual(status, 503)
        self.assertEqual(received, b'{"error":"secret"}')
        self.assertEqual(metadata["models"], [])
        self.assertEqual(metadata["errors"], ["upstream HTTP 503"])


class LaunchTests(unittest.TestCase):
    def run_fixture(self, timeout=False, fail=False):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        root = Path(temp.name)
        docs = root / "frozen"
        docs.mkdir()
        (docs / "SKILL.md").write_text("Frozen protocol", encoding="utf-8")
        auth_home = root / "neutral-auth"
        auth_home.mkdir()
        seen = {}
        proxy = mock.Mock()
        proxy.metadata.return_value = {"source": "response.model", "models": ["actual"], "observations": []}

        def launch(command, **kwargs):
            seen.update(kwargs)
            seen["command"] = command
            if fail:
                raise FileNotFoundError("codex binary missing")
            Path(kwargs["cwd"], "final.json").write_text('{"ok":true}', encoding="utf-8")
            kwargs["stdout"].write(json.dumps({"type": "thread.started", "thread_id": "fixture"}) + "\n")
            proc = mock.Mock()
            proc.wait.side_effect = [subprocess.TimeoutExpired(command, 1), -9] if timeout else [0]
            return proc

        with mock.patch.object(S, "_codex_isolation", return_value=[]), \
             mock.patch.object(S, "_codex_home", return_value=(str(auth_home), [], proxy)), \
             mock.patch.object(S.subprocess, "Popen", side_effect=launch), \
             mock.patch.object(S, "_kill_group", return_value={"ok": True, "leaderReturnCode": -9}) as kill:
            result = S.run("Task at {docs}", "actual", "high", str(docs), str(root / "out"),
                           1, 2, warm=False, host="codex")
        self.assertFalse(Path(seen["cwd"]).exists())
        self.assertFalse(auth_home.exists())
        self.assertTrue(str(seen["cwd"]).startswith("/tmp/eve-s/"))
        self.assertEqual(seen["env"]["CODEX_HOME"], str(auth_home))
        self.assertTrue(seen["start_new_session"])
        self.assertIsNone(result["budgetUsd"])
        self.assertFalse(result["budgetSupported"])
        proxy.close.assert_called_once()
        self.assertTrue(Path(result["identity"]).is_file())
        self.assertEqual(kill.call_count, int(timeout))
        return result

    def test_run_collects_answer_and_cleans_neutral_directories(self):
        result = self.run_fixture()
        self.assertEqual(result["answer"], {"ok": True})
        self.assertEqual(result["returncode"], 0)
        self.assertFalse(result["timedOut"])

    def test_timeout_kills_group_and_cleans_directories(self):
        result = self.run_fixture(timeout=True)
        self.assertTrue(result["timedOut"])
        self.assertEqual(result["returncode"], -9)

    def test_missing_binary_is_reported_and_cleaned(self):
        result = self.run_fixture(fail=True)
        self.assertIsNone(result["returncode"])
        self.assertIn("FileNotFoundError", result["hostError"])


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
