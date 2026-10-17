"""usk_eval.usage public API: host detection, routing, window handling, manual and unavailable records."""
import os
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from unittest import mock

try:
    from . import test_usage_support as support
except ImportError:
    import test_usage_support as support

from usk_eval import store, usage
from usk_eval.usage import claude, common

SID = support.CLAUDE_SID
R = support.CODEX_ROOT_THREAD
NO_VALUES = {"values": [], "source": None}


class HostDetectionTests(unittest.TestCase):
    def test_detect_host(self):
        cases = [({}, "none"), ({"CLAUDE_CODE_SESSION_ID": "abc"}, "claude-code"), ({"CLAUDECODE": "1"}, "claude-code"),
                 ({"CLAUDECODE": "0"}, "none"), ({"CLAUDECODE": ""}, "none"), ({"CODEX_THREAD_ID": "t"}, "codex"),
                 ({"CODEX_SANDBOX": "seatbelt"}, "codex"), ({"CODEX_THREAD_ID": "", "CODEX_SANDBOX": ""}, "none"),
                 ({"CLAUDECODE": "1", "CODEX_THREAD_ID": "t"}, "claude-code")]  # contract 8.1 order
        for env, expected in cases:
            with self.subTest(env=env):
                self.assertEqual(usage.detect_host(env), expected)

    def test_capture_host(self):
        cases = [({"CLAUDE_CODE_SESSION_ID": SID}, ("claude-code", [SID])), ({"CLAUDECODE": "1"}, ("claude-code", [])),
                 ({"CODEX_THREAD_ID": R}, ("codex", [R])), ({"CODEX_SANDBOX": "seatbelt"}, ("codex", [])),
                 ({"CLAUDE_CODE_SESSION_ID": "../x"}, ("claude-code", [])), ({}, ("none", []))]
        for env, (kind, ids) in cases:
            with self.subTest(env=env):
                self.assertEqual(usage.capture_host(env, cwd="/work"), {"kind": kind, "sessionIds": ids, "cwd": "/work"})

    def test_capture_host_defaults_to_the_current_directory_and_process_environment(self):
        with mock.patch.dict(os.environ, {"CLAUDE_CODE_SESSION_ID": SID}):
            captured = usage.capture_host()
        self.assertEqual((captured["kind"], captured["sessionIds"], captured["cwd"]), ("claude-code", [SID], os.getcwd()))

    def test_merge_session_ids_keeps_order_and_drops_duplicates_and_junk(self):
        self.assertEqual(usage.merge_session_ids([SID, "b"], ["b", "c", "../d", "", None, 5], None, ("e",)), [SID, "b", "c", "e"])
        self.assertEqual(usage.merge_session_ids(), [])


    def test_home_directory_prefers_the_platform_variable(self):
        both = {"HOME": "/posix/home", "USERPROFILE": "C:\\Users\\win"}
        self.assertEqual(common.home_dir(both), "C:\\Users\\win" if os.name == "nt" else "/posix/home")
        self.assertEqual(common.home_dir({"HOME": "/only/home"}), "/only/home")
        self.assertEqual(common.home_dir({}), os.path.expanduser("~"))


class RoutingTests(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.env = {"USK_EVAL_CLAUDE_ROOT": os.path.join(tmp.name, "c"), "USK_EVAL_CODEX_ROOT": os.path.join(tmp.name, "x"),
                    "HOME": os.path.join(tmp.name, "home")}

    def read(self, host, **kwargs):
        kwargs.setdefault("env", self.env)
        return usage.read_usage(host, support.T0, support.T1, now=support.NOW, **kwargs)

    def assert_unavailable(self, record, host, reason):
        self.assertEqual(list(record), support.CONTRACT_KEYS)
        self.assertEqual((record["host"], record["source"], record["unavailableReason"]), (host, "none", reason))
        for key in ("tokens", "raw", "messages", "subagents"):
            self.assertIsNone(record[key])
        self.assertEqual((record["sessionIds"], record["errors"], record["parserVersion"]), ([], [], 1))
        self.assertEqual((record["model"], record["effort"]), (NO_VALUES, NO_VALUES))
        self.assertEqual(record["window"], {"from": support.T0, "to": support.T1})

    def test_disabled_by_user_does_not_touch_the_logs(self):
        with mock.patch.object(claude, "read", side_effect=AssertionError("log read")):
            self.assert_unavailable(self.read(support.host_claude(SID), enabled=False), "claude-code", "disabled_by_user")
        self.assert_unavailable(self.read({"kind": "none"}, enabled=False), "none", "disabled_by_user")

    def test_hosts_without_a_session_are_unavailable(self):
        hosts = (None, {}, "claude-code", {"kind": "none"}, {"kind": "cursor", "sessionIds": [SID]},
                 {"kind": "none", "sessionIds": [SID]})
        for host in hosts:
            with self.subTest(host=host):
                self.assert_unavailable(self.read(host), "none", "no_host_session")
        self.assert_unavailable(self.read({"kind": "claude-code", "sessionIds": [], "cwd": "/work"}),
                                "claude-code", "no_host_session")
        self.assert_unavailable(self.read({"kind": "codex", "sessionIds": [], "cwd": ""}), "codex", "no_host_session")
        self.assert_unavailable(self.read({"kind": "codex", "sessionIds": ["../x"], "cwd": None}), "codex", "no_host_session")

    def test_codex_with_only_a_working_directory_reads_the_rollouts(self):
        record = self.read({"kind": "codex", "sessionIds": [], "cwd": "/work"})
        self.assertEqual((record["source"], record["unavailableReason"]), ("codex-rollout", "rollout_not_found"))

    def test_an_unexpected_reader_failure_becomes_a_parse_error_without_leaking_the_message(self):
        with mock.patch.object(claude, "read", side_effect=RuntimeError("ZZ_SENTINEL_BOOM")):
            record = self.read(support.host_claude(SID))
        self.assertEqual((record["unavailableReason"], record["tokens"], record["source"]),
                         ("parse_error", None, "claude-session-log"))
        self.assertEqual(record["errors"], [{"file": "", "line": 0, "error": "RuntimeError"}])
        self.assertNotIn("ZZ_SENTINEL_BOOM", repr(record))

    def test_window_accepts_datetimes_and_other_fraction_lengths_and_is_written_with_milliseconds(self):
        start = datetime(2026, 10, 17, 3, 0, 0, 123456, tzinfo=timezone.utc)
        end = datetime(2026, 10, 17, 11, 10, 0, 999999, tzinfo=timezone(timedelta(hours=8)))
        record = usage.read_usage(support.host_claude(SID), start, end, env=self.env, now=support.NOW)
        self.assertEqual(record["window"], {"from": "2026-10-17T03:00:00.123Z", "to": "2026-10-17T03:10:00.999Z"})
        record = usage.read_usage(support.host_claude(SID), "2026-10-17T03:00:00.1234567Z", "2026-10-17T11:10:00+08:00",
                                  env=self.env, now=support.NOW)
        self.assertEqual(record["window"], {"from": "2026-10-17T03:00:00.123Z", "to": "2026-10-17T03:10:00.000Z"})

    def test_an_open_end_means_now(self):
        record = usage.read_usage(support.host_claude(SID), support.T0, None, env=self.env, now=support.NOW)
        self.assertEqual(record["window"]["to"], support.NOW)

    def test_the_clock_is_the_runner_store_clock(self):
        with mock.patch.object(store, "now_iso", return_value="2026-10-17T05:00:00.000Z"):
            record = usage.read_usage(support.host_claude(SID), support.T0, None, env=self.env)
        self.assertEqual((record["computedAt"], record["window"]["to"]), ("2026-10-17T05:00:00.000Z",) * 2)

    def test_an_invalid_window_is_a_caller_bug(self):
        for t0, t1 in ((None, support.T1), ("yesterday", support.T1), (support.T0, "tomorrow"), (7, support.T1)):
            with self.subTest(t0=t0, t1=t1), self.assertRaises(ValueError):
                usage.read_usage(support.host_claude(SID), t0, t1, env=self.env)

    def test_computed_at_defaults_to_the_current_time_in_contract_format(self):
        record = usage.read_usage({"kind": "none"}, support.T0, support.T1)
        self.assertRegex(record["computedAt"], r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$")

    def test_unavailable_usage_normalizes_the_host(self):
        record = usage.unavailable_usage("cursor", "no_host_session", support.T0, support.T1, now=support.NOW)
        self.assert_unavailable(record, "other", "no_host_session")


class DeclaredDetectionTests(unittest.TestCase):
    def test_nothing_is_detected_without_a_host_or_a_log(self):
        unknown = {"value": None, "source": "unknown"}
        declared = usage.detect_declared({"kind": "none"}, support.T0, support.T1, env={}, now=support.NOW)
        self.assertEqual(declared, {"host": unknown, "hostVersion": unknown, "model": unknown, "effort": unknown})

    def test_the_host_comes_from_the_environment_when_none_is_given(self):
        with tempfile.TemporaryDirectory() as tmp:
            env = {"CODEX_THREAD_ID": R, "USK_EVAL_CODEX_ROOT": tmp, "HOME": tmp}
            declared = usage.detect_declared(None, support.T0, support.T1, env=env, now=support.NOW)
        self.assertEqual(declared["host"], {"value": "codex", "source": "detected"})
        self.assertEqual(declared["model"], {"value": None, "source": "unknown"})

    def test_a_reader_failure_leaves_the_values_unknown(self):
        with mock.patch.object(claude, "detect", side_effect=RuntimeError("ZZ_SENTINEL_BOOM")):
            declared = usage.detect_declared(support.host_claude(SID), support.T0, support.T1, env={}, now=support.NOW)
        self.assertEqual(declared["host"], {"value": "claude-code", "source": "detected"})
        self.assertEqual(declared["model"], {"value": None, "source": "unknown"})


class ManualUsageTests(unittest.TestCase):
    def test_parse_manual_args(self):
        items = ["input=100", "output=50", "cacheRead=7", "cacheWrite=3", "reasoning=20", "model=gpt-x"]
        values, model = usage.parse_manual_args(items)
        self.assertEqual(values, {"input": 100, "output": 50, "cacheRead": 7, "cacheWrite": 3, "reasoning": 20})
        self.assertEqual(model, "gpt-x")
        self.assertEqual(usage.parse_manual_args(["output=1", "input=2"]), ({"output": 1, "input": 2}, None))

    def test_parse_manual_args_rejects_bad_input(self):
        bad = [["input=1"], ["output=1"], [], ["input=1", "output=1", "Input=2"], ["input=1", "output=1", "input=2"],
               ["input=-1", "output=1"], ["input=1.5", "output=1"], ["input= 1", "output=1"], ["input=\u0661\u0662", "output=1"],
               ["input=", "output=1"], ["input=1", "output=1", "model="], ["input=1", "output=1", "model=a", "model=b"],
               ["input=1", "output=1", "model=\n"], ["input=1", "output=1", "cacheRead"], ["input=" + "9" * 16, "output=1"]]
        for items in bad:
            with self.subTest(items=items), self.assertRaises(ValueError):
                usage.parse_manual_args(items)

    def test_manual_usage_record(self):
        record = usage.manual_usage({"input": 100, "output": 50, "cacheRead": 7, "reasoning": 20}, support.T0, support.T1,
                                    host="cursor", model="gpt-x", now=support.NOW)
        self.assertEqual(list(record), support.CONTRACT_KEYS)
        self.assertEqual((record["host"], record["source"], record["unavailableReason"]), ("other", "user", None))
        self.assertEqual(record["tokens"], {"input": 100, "output": 50, "cacheRead": 7, "cacheWrite": 0, "reasoning": 20,
                                            "processed": 157})
        self.assertEqual(record["model"], {"values": ["gpt-x"], "source": "user"})
        self.assertEqual((record["effort"], record["raw"], record["messages"], record["subagents"]),
                         (NO_VALUES, None, None, None))
        self.assertEqual((record["sessionIds"], record["errors"], record["parserVersion"]), ([], [], 1))

    def test_manual_usage_without_a_model_and_for_known_hosts(self):
        record = usage.manual_usage({"input": 1, "output": 2}, support.T0, support.T1, host="codex", now=support.NOW)
        self.assertEqual((record["host"], record["model"]), ("codex", NO_VALUES))

    def test_manual_usage_validates_its_values(self):
        for values in ({"input": 1}, {"output": 1}, {"input": 1, "output": 1, "extra": 1}, {"input": -1, "output": 1},
                       {"input": 1.5, "output": 1}, {"input": True, "output": 1}, {"input": "1", "output": 1}):
            with self.subTest(values=values), self.assertRaises(ValueError):
                usage.manual_usage(values, support.T0, support.T1)
        for model in ("", "two\nlines", "x" * 200):
            with self.subTest(model=model[:8]), self.assertRaises(ValueError):
                usage.manual_usage({"input": 1, "output": 1}, support.T0, support.T1, model=model)

    def test_ledger_summary(self):
        manual = usage.manual_usage({"input": 100, "output": 50}, support.T0, support.T1, now=support.NOW)
        self.assertEqual(usage.ledger_summary(manual), {"source": "user", "processed": 150})
        none = usage.unavailable_usage("none", "no_host_session", support.T0, support.T1, now=support.NOW)
        self.assertEqual(usage.ledger_summary(none), {"source": "none", "processed": None})
        env = {"USK_EVAL_CLAUDE_ROOT": str(support.FIXTURES / "claude")}
        read = usage.read_usage(support.host_claude(SID), support.T0, support.T1, env=env, now=support.NOW)
        self.assertEqual(usage.ledger_summary(read), {"source": "claude-session-log", "processed": 14955})

    def test_parser_version_is_exported(self):
        self.assertEqual(usage.PARSER_VERSION, 1)


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
