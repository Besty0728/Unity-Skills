#!/usr/bin/env python3
"""What the client does with the answers a domain reload or a server stop produces, with no live Editor:
a 503 never ran (resend anything), a 504 or a dropped connection may have run (never resend a write),
and the read-only GET helpers wait out a reload instead of returning its first 503.

    python3 -m unittest discover -s .github/scripts/tests -t .github/scripts/tests
"""
import importlib.util
import json
from pathlib import Path
import sys
import unittest
from unittest import mock

CLIENT = Path(__file__).resolve().parents[3] / "SkillsForUnity/unity-skills~/scripts/unity_skills.py"
spec = importlib.util.spec_from_file_location("reload_answers_client", CLIENT)
client_module = importlib.util.module_from_spec(spec)
# The client ships inside the package: loading it must never leave a __pycache__ there, with or without -B.
_dont_write_bytecode = sys.dont_write_bytecode
sys.dont_write_bytecode = True
try:
    spec.loader.exec_module(client_module)
finally:
    sys.dont_write_bytecode = _dont_write_bytecode

NOT_EXECUTED = {"status": "error", "errorCode": "COMPILING", "retryStrategy": "wait_and_retry",
                "retryAfterSeconds": 5, "details": {"domainReloadPending": True, "executed": False}}
TIMEOUT = {"status": "error", "errorCode": "TIMEOUT", "retryStrategy": "wait_and_retry",
           "retryAfterSeconds": 5, "details": {"domainReloadPending": True}}
SUCCESS = {"status": "success", "result": {"name": "Cube"}}


def answer(status, data=None, headers=None):
    response = mock.Mock()
    response.status_code = status
    response.headers = dict(headers or {})
    if data is None:
        response.json.side_effect = ValueError("no JSON body")
        response.text = ""
        response.content = b""
    else:
        response.json.return_value = data
        response.text = json.dumps(data)
        response.content = response.text.encode("utf-8")
    return response


def dropped():
    return client_module.requests.exceptions.ConnectionError(
        "('Connection aborted.', RemoteDisconnected('Remote end closed connection without response'))")


def refused():
    return client_module.requests.exceptions.ConnectionError(
        "HTTPConnectionPool(host='localhost', port=8090): Max retries exceeded (Caused by NewConnectionError("
        "'Failed to establish a new connection: [Errno 61] Connection refused'))")


class ReloadAnswerTests(unittest.TestCase):
    def setUp(self):
        self.client = client_module.UnitySkills.__new__(client_module.UnitySkills)
        self.client.url = "http://localhost:8090"
        self.client.timeout = 30
        self.client._expected_instance_id = None
        self.client._session = mock.Mock()
        self.client._health_info = None
        self.client._meta_cache = None
        sleeper = mock.patch.object(client_module.time, "sleep")
        self.sleep = sleeper.start()
        self.addCleanup(sleeper.stop)

    def post(self, *answers):
        self.client._post_skill = mock.Mock(side_effect=list(answers))

    def test_write_is_resent_after_a_not_executed_503(self):
        self.post(answer(503, NOT_EXECUTED), answer(200, SUCCESS))
        result = self.client.call("gameobject_create", name="Cube")
        self.assertTrue(result["success"])
        self.assertEqual(self.client._post_skill.call_count, 2)
        self.sleep.assert_called_once_with(5.0)

    def test_write_is_resent_after_an_empty_503_using_retry_after(self):
        self.post(answer(503, headers={"Retry-After": "4"}), answer(200, SUCCESS))
        result = self.client.call("gameobject_create", name="Cube")
        self.assertTrue(result["success"])
        self.sleep.assert_called_once_with(4.0)

    def test_write_is_not_resent_after_a_504(self):
        self.post(answer(504, TIMEOUT))
        result = self.client.call("gameobject_create", name="Cube")
        self.assertFalse(result["success"])
        self.assertEqual(result["errorCode"], "TIMEOUT")
        self.assertTrue(result["requestOutcomeUnknown"])
        self.client._post_skill.assert_called_once()
        self.sleep.assert_not_called()

    def test_write_with_an_empty_200_or_504_is_an_unknown_outcome(self):
        for status in (200, 504):
            with self.subTest(status=status):
                self.post(answer(status))
                result = self.client.call("gameobject_create", name="Cube")
                self.assertFalse(result["success"])
                self.assertTrue(result["requestOutcomeUnknown"])
                self.client._post_skill.assert_called_once()

    def test_write_is_not_resent_after_a_dropped_connection(self):
        self.post(dropped())
        result = self.client.call("test_run", testMode="EditMode")
        self.assertTrue(result["requestOutcomeUnknown"])
        self.assertEqual(result["transportError"], "connection")
        self.client._post_skill.assert_called_once()

    def test_refused_connection_is_not_an_unknown_outcome(self):
        self.post(refused())
        result = self.client.call("gameobject_create", name="Cube")
        self.assertEqual(result["transportError"], "connection")
        self.assertNotIn("requestOutcomeUnknown", result)

    def test_preview_retries_a_504_and_an_empty_answer(self):
        self.post(answer(504, TIMEOUT), answer(200), answer(200, {"valid": True}))
        result = self.client.dry_run_skill("gameobject_create", name="Cube")
        self.assertEqual(result, {"valid": True})
        self.assertEqual(self.client._post_skill.call_count, 3)

    def test_batch_write_is_not_resent_after_a_504(self):
        self.client._session.post.return_value = answer(504, dict(TIMEOUT))
        result = self.client.execute_batch([{"skill": "gameobject_create", "args": {"name": "Cube"}}])
        self.assertTrue(result["requestOutcomeUnknown"])
        self.client._session.post.assert_called_once()

    def test_batch_write_is_resent_after_a_not_executed_503(self):
        self.client._session.post.side_effect = [answer(503, NOT_EXECUTED), answer(200, {"status": "success"})]
        result = self.client.execute_batch([{"skill": "gameobject_create", "args": {"name": "Cube"}}])
        self.assertEqual(result["status"], "success")
        self.assertEqual(self.client._session.post.call_count, 2)

    def test_retry_helper_returns_an_unknown_outcome_at_once(self):
        unknown = self.client._dropped_connection_result()
        with mock.patch.object(client_module, "call_skill", return_value=unknown) as call, \
                mock.patch.object(client_module, "wait_for_unity") as wait:
            result = client_module.call_skill_with_retry("test_run", max_retries=3)
        self.assertTrue(result["requestOutcomeUnknown"])
        call.assert_called_once()
        wait.assert_not_called()

    def test_get_helpers_wait_out_a_reload(self):
        meta = {"status": "ok", "categories": ["GameObject"]}
        self.client._session.get.side_effect = [answer(503, NOT_EXECUTED), refused(), answer(200), answer(200, meta)]
        self.assertEqual(self.client.get_meta(), meta)
        self.assertEqual(self.client._session.get.call_count, 4)
        self.assertEqual(self.client._meta_cache, meta)

    def test_get_helper_does_not_cache_a_final_503(self):
        self.client._session.get.return_value = answer(503, NOT_EXECUTED)
        result = self.client.get_meta()
        self.assertEqual(result["errorCode"], "COMPILING")
        self.assertIsNone(self.client._meta_cache)
        self.assertEqual(self.client._session.get.call_count, 4)


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
