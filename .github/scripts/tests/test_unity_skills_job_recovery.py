#!/usr/bin/env python3
"""Instance-bound job recovery and unknown write outcomes, with no live Editor."""
import importlib.util
from pathlib import Path
import unittest
from unittest import mock

CLIENT = Path(__file__).resolve().parents[3] / "SkillsForUnity/unity-skills~/scripts/unity_skills.py"
spec = importlib.util.spec_from_file_location("job_recovery_client", CLIENT)
client_module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(client_module)


class JobRecoveryTests(unittest.TestCase):
    def setUp(self):
        self.client = client_module.UnitySkills.__new__(client_module.UnitySkills)
        self.client.url = "http://localhost:8090"
        self.client.timeout = 30
        self.client._expected_instance_id = "Original"
        self.client._session = mock.Mock()
        self.client._health_info = None

    def response(self, data, status=200):
        response = mock.Mock()
        response.json.return_value = data
        response.status_code = status
        return response

    def test_job_reads_follow_only_original_registry_instance_and_send_guard(self):
        self.client._session.get.return_value = self.response({"jobId": "j", "status": "running"})
        with mock.patch.object(client_module, "_find_registry_entry", return_value={"id": "Original", "port": 8093}) as lookup:
            self.client.get_job("j")
        lookup.assert_called_once_with("Original")
        args, kwargs = self.client._session.get.call_args
        self.assertEqual(args[0], "http://localhost:8093/jobs/j?expectInstance=Original")
        self.assertLessEqual(kwargs["timeout"], client_module.HEALTH_TIMEOUT)

    def test_exact_instance_lookup_does_not_fall_back_to_another_project_entry(self):
        with mock.patch.object(client_module, "_registry_entries", return_value=[{"id": "Other", "path": "/project", "port": 8090}]):
            self.assertIsNone(client_module._find_registry_entry("Original", "/project"))

    def test_transport_timeout_preserves_last_phase_and_snapshot(self):
        clock = [0.0]
        snapshots = iter([
            {"jobId": "j", "status": "waiting_domain_reload", "currentStage": "reload_pending", "terminal": False},
            {"success": False, "transportError": "connection", "error": "Cannot connect"}])
        self.client.get_job = mock.Mock(side_effect=lambda *a, **k: next(snapshots))
        def sleep(seconds):
            clock[0] += seconds
        with mock.patch.object(client_module.time, "monotonic", side_effect=lambda: clock[0]), mock.patch.object(client_module.time, "sleep", side_effect=sleep):
            result = self.client.poll_job("j", interval=1, timeout=1)
        self.assertTrue(result["_pollTimeout"])
        self.assertEqual(result["currentStage"], "reload_pending")
        self.assertEqual(result["lastTransportError"]["transportError"], "connection")
        self.assertEqual(self.client.get_job.call_count, 2)
        self.assertLessEqual(self.client.get_job.call_args.kwargs["_timeout"], 0.01)

    def test_poll_retries_read_only_503_then_completes(self):
        self.client.get_job = mock.Mock(side_effect=[
            {"status": "error", "errorCode": "COMPILING", "retryStrategy": "wait_and_retry", "_httpStatus": 503},
            {"jobId": "j", "status": "completed", "terminal": True}])
        with mock.patch.object(client_module.time, "sleep"):
            result = self.client.poll_job("j", timeout=1)
        self.assertEqual(result["status"], "completed")
        self.client._session.post.assert_not_called()

    def test_temporary_non_json_responses_retry_get_then_return_terminal_result(self):
        for status in (200, 502, 503, 504):
            with self.subTest(status=status):
                transient = self.response(None, status=status)
                transient.json.side_effect = ValueError("HTML or empty response during reload")
                done = {"jobId": "j", "status": "completed", "terminal": True,
                        "resultData": {"compilation": {"compiled": True, "domainLoaded": True}}}
                self.client._session.get.side_effect = [transient, self.response(done)]
                self.client._session.get.reset_mock()
                with mock.patch.object(client_module, "_find_registry_entry", return_value=None), mock.patch.object(client_module.time, "sleep"):
                    result = self.client.wait_for_job("j", timeout=1)
                self.assertTrue(result["success"])
                self.assertTrue(result["details"]["compilation"]["domainLoaded"])
                self.assertEqual(self.client._session.get.call_count, 2)
                self.client._session.post.assert_not_called()

    def test_non_json_not_found_is_a_terminal_error_without_retries(self):
        response = self.response(None, status=404)
        response.json.side_effect = ValueError("Not found")
        self.client._session.get.return_value = response
        with mock.patch.object(client_module, "_find_registry_entry", return_value=None):
            result = self.client.poll_job("j", timeout=1)
        self.assertFalse(result["retryable"])
        self.client._session.get.assert_called_once()

    def test_write_is_not_replayed_after_unknown_transport_timeout(self):
        self.client._post_skill = mock.Mock(side_effect=client_module.requests.exceptions.Timeout())
        with mock.patch.object(client_module.time, "sleep") as sleep:
            result = self.client._post_skill_with_retries("script_create", {}, _retries=10)
        self.assertFalse(result["ok"])
        self.assertTrue(result["transport_error"]["requestOutcomeUnknown"])
        self.client._post_skill.assert_called_once()
        sleep.assert_not_called()


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
