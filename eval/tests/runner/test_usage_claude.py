"""Claude Code session log reader (contract 8.2): dedupe by message id, time window, subagents, record shape."""
import os
import tempfile
import unittest

try:
    from . import test_usage_support as support
except ImportError:
    import test_usage_support as support

from usk_eval import usage
from usk_eval.usage import claude

SID = support.CLAUDE_SID
SID_B = "c1a2b3c4-0000-4000-8000-0000000000bb"
MAIN_NAME = SID + ".jsonl"
line = support.claude_line


class GoldenFixtureTests(unittest.TestCase):
    """fixtures/usage/claude: split lines, window edges, subagents, placeholder, cost-state and corrupt lines."""

    def setUp(self):
        env = {"USK_EVAL_CLAUDE_ROOT": str(support.FIXTURES / "claude"), "HOME": str(support.FIXTURES / "no-home")}
        self.record = usage.read_usage(support.host_claude(SID), support.T0, support.T1, env=env, now=support.NOW)

    def test_record_shape(self):
        self.assertEqual(list(self.record), support.CONTRACT_KEYS)
        self.assertEqual((self.record["host"], self.record["source"]), ("claude-code", "claude-session-log"))
        self.assertEqual(self.record["sessionIds"], [SID])
        self.assertEqual(self.record["window"], {"from": support.T0, "to": support.T1})
        self.assertEqual((self.record["parserVersion"], self.record["unavailableReason"]), (1, None))
        self.assertEqual(self.record["computedAt"], support.NOW)

    def test_tokens_use_the_final_line_of_every_message_inside_the_window(self):
        # main: msg_split (final line only), msg_next, msg_weird (non-integers -> 0), msg_nousage, msg_last (== t1)
        # subagents: msg_sub1 (final line only), msg_sub2; msg_before/at_t0/after/late and the placeholder are out
        self.assertEqual(self.record["tokens"], {"input": 10, "output": 645, "cacheRead": 12600, "cacheWrite": 1700,
                                                 "reasoning": 100, "processed": 14955})
        self.assertEqual(self.record["raw"], {"input_tokens": 10, "output_tokens": 645, "cache_read_input_tokens": 12600,
                                              "cache_creation_input_tokens": 1700, "thinking_tokens": 100})
        self.assertEqual(self.record["messages"], 7)

    def test_subagent_subtotal_is_included_in_the_total(self):
        self.assertEqual(self.record["subagents"], {"files": 2, "tokens": {"input": 3, "output": 105, "cacheRead": 1500,
                                                                             "cacheWrite": 200, "reasoning": 10,
                                                                             "processed": 1808}})

    def test_models_and_efforts_come_from_counted_lines_only(self):
        self.assertEqual(self.record["model"], {"values": ["claude-haiku-5-5", "claude-opus-5-5", "claude-sonnet-5-5"],
                                                "source": "provider-returned"})
        self.assertEqual(self.record["effort"], {"values": ["high", "max"], "source": "host-reported"})

    def test_bad_lines_are_reported_by_file_line_and_type_only(self):
        self.assertEqual([(e["file"], e["line"]) for e in self.record["errors"]], [(MAIN_NAME, 12), (MAIN_NAME, 15)])
        self.assertEqual(self.record["errors"][0]["error"], "JSONDecodeError")
        self.assertEqual(self.record["errors"][1]["error"], "ValueError")  # unparsable timestamp of msg_badts

    def test_declared_detection_takes_the_latest_line_up_to_the_end_of_the_window(self):
        env = {"USK_EVAL_CLAUDE_ROOT": str(support.FIXTURES / "claude"), "HOME": str(support.FIXTURES / "no-home")}
        declared = usage.detect_declared(support.host_claude(SID), support.T0, support.T1, env=env, now=support.NOW)
        self.assertEqual(declared, {"host": {"value": "claude-code", "source": "detected"},
                                    "hostVersion": {"value": "2.1.296", "source": "detected"},
                                    "model": {"value": "claude-opus-5-5[1m]", "source": "detected"},
                                    "effort": {"value": "max", "source": "detected"}})


class ClaudeReaderTests(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.tmp = tmp.name
        self.root = os.path.join(self.tmp, "projects")
        self.tree = support.ClaudeTree(self.root)
        self.env = {"USK_EVAL_CLAUDE_ROOT": self.root, "HOME": os.path.join(self.tmp, "home")}

    def read(self, *ids, **kwargs):
        host = support.host_claude(*(ids or (SID,)))
        return usage.read_usage(host, kwargs.get("t0", support.T0), kwargs.get("t1", support.T1), env=self.env, now=support.NOW)

    def test_last_line_of_a_message_wins_even_when_it_is_smaller(self):
        self.tree.main(SID, [line("m1", "03:01:00.000Z", out=50, thinking=5), line("m1", "03:01:01.000Z", out=90, thinking=9),
                             line("m1", "03:01:02.000Z", out=70, thinking=7)])
        record = self.read()
        self.assertEqual((record["tokens"]["output"], record["tokens"]["reasoning"], record["messages"]), (70, 7, 1))

    def test_window_is_open_at_t0_and_closed_at_t1(self):
        self.tree.main(SID, [line("a", "03:00:00.000Z", out=1), line("b", "03:00:00.001Z", out=10),
                             line("c", "03:10:00.000Z", out=100), line("d", "03:10:00.001Z", out=1000)])
        record = self.read()
        self.assertEqual((record["tokens"]["output"], record["messages"]), (110, 2))

    def test_messages_are_deduplicated_before_the_window_is_applied(self):
        self.tree.main(SID, [
            line("late", "03:05:00.000Z", out=5), line("late", "03:10:00.001Z", out=500),  # final line outside: whole id dropped
            line("early", "02:59:00.000Z", out=7), line("early", "03:01:00.000Z", out=70),  # final line inside: counted once
        ])
        record = self.read()
        self.assertEqual((record["tokens"]["output"], record["messages"]), (70, 1))

    def test_the_later_file_wins_for_an_id_present_in_two_files(self):
        self.tree.main(SID, [line("dup", "03:01:00.000Z", out=10)])
        self.tree.sub(SID, "agent-aone-0000000000000001.jsonl", [line("dup", "03:02:00.000Z", out=40)])
        record = self.read()
        self.assertEqual((record["tokens"]["output"], record["messages"]), (40, 1))
        self.assertEqual((record["subagents"]["files"], record["subagents"]["tokens"]["output"]), (1, 40))

    def test_only_agent_jsonl_files_of_the_subagents_directory_are_read(self):
        for name in ("agent-ok-0000000000000001.jsonl", "agent-x-0000000000000002.meta.json", "agent-y.jsonl.bak",
                     "notes.jsonl", "xagent-z.jsonl"):
            self.tree.sub(SID, name, [line("m-" + name, "03:01:00.000Z", out=1)])
        self.tree.main(SID, [])
        self.assertEqual(self.read()["subagents"]["files"], 1)

    def test_only_assistant_lines_with_a_string_message_id_count(self):
        user_line = dict(line("u1", "03:01:00.000Z", out=100), type="user")
        no_id = line("x", "03:01:00.000Z", out=100)
        del no_id["message"]["id"]
        int_id = line("x", "03:01:00.000Z", out=100)
        int_id["message"]["id"] = 7
        not_a_dict = dict(line("x", "03:01:00.000Z", out=100), message="assistant")
        self.tree.main(SID, [user_line, no_id, int_id, not_a_dict, line("ok", "03:02:00.000Z", out=3)])
        record = self.read()
        self.assertEqual((record["tokens"]["output"], record["messages"], record["errors"]), (3, 1, []))

    def test_session_log_not_found(self):
        self.tree.main(SID_B, [line("m", "03:01:00.000Z", out=5)])
        record = self.read()
        self.assertEqual((record["unavailableReason"], record["source"], record["sessionIds"]),
                         ("session_log_not_found", "claude-session-log", [SID]))
        for key in ("tokens", "raw", "messages", "subagents"):
            self.assertIsNone(record[key])

    def test_subagent_files_alone_do_not_make_a_session(self):
        self.tree.sub(SID, "agent-aone-0000000000000001.jsonl", [line("m", "03:01:00.000Z", out=5)])
        self.assertEqual(self.read()["unavailableReason"], "session_log_not_found")

    def test_missing_root_is_not_found(self):
        self.env["USK_EVAL_CLAUDE_ROOT"] = os.path.join(self.tmp, "absent")
        self.assertEqual(self.read()["unavailableReason"], "session_log_not_found")

    def test_no_messages_in_window_reports_zero_tokens(self):
        self.tree.main(SID, [line("m", "02:00:00.000Z", out=5), line("n", "05:00:00.000Z", out=5)])
        record = self.read()
        self.assertEqual(record["unavailableReason"], "no_messages_in_window")
        self.assertEqual(record["tokens"], support.ZERO_TOKENS)
        self.assertEqual((record["messages"], record["subagents"]["files"]), (0, 0))
        self.assertEqual((record["model"], record["effort"]), ({"values": [], "source": None}, {"values": [], "source": None}))

    def test_a_log_without_a_single_parsable_candidate_line_is_a_parse_error(self):
        self.tree.main(SID, ['{"type":"assistant", broken', '{"type":"assistant","message":{"id":'])
        record = self.read()
        self.assertEqual(record["unavailableReason"], "parse_error")
        self.assertIsNone(record["tokens"])
        self.assertEqual([(e["line"], e["error"]) for e in record["errors"]], [(1, "JSONDecodeError"), (2, "JSONDecodeError")])

    def test_some_bad_lines_do_not_spoil_the_rest(self):
        self.tree.main(SID, ['{"type":"assistant", broken', line("m", "03:01:00.000Z", out=4)])
        record = self.read()
        self.assertEqual((record["unavailableReason"], record["tokens"]["output"], len(record["errors"])), (None, 4, 1))

    def test_errors_are_capped_at_twenty(self):
        self.tree.main(SID, ['{"type":"assistant", broken'] * 30 + [line("m", "03:01:00.000Z", out=4)])
        record = self.read()
        self.assertEqual((len(record["errors"]), record["tokens"]["output"]), (20, 4))

    def test_invalid_utf8_inside_a_line_does_not_stop_the_read(self):
        path = os.path.join(self.root, "-work-p", MAIN_NAME)
        os.makedirs(os.path.dirname(path))
        with open(path, "wb") as handle:
            good = support.jsonl([line("m", "03:01:00.000Z", out=6)]).encode("utf-8")
            handle.write(good.replace(b"ZZ_SENTINEL_CONTENT", b"bad \xff\xfe bytes"))
        record = self.read()
        self.assertEqual((record["tokens"]["output"], record["errors"]), (6, []))

    def test_several_session_ids_are_added_up(self):
        self.tree.main(SID, [line("a", "03:01:00.000Z", out=10)])
        self.tree.main(SID_B, [line("b", "03:02:00.000Z", out=20)], project="-work-q")
        record = self.read(SID, SID_B)
        self.assertEqual((record["tokens"]["output"], record["messages"], record["sessionIds"]), (30, 2, [SID, SID_B]))

    def test_one_missing_session_id_is_tolerated(self):
        self.tree.main(SID, [line("a", "03:01:00.000Z", out=10)])
        record = self.read(SID, SID_B)
        self.assertEqual((record["unavailableReason"], record["tokens"]["output"]), (None, 10))

    def test_root_precedence(self):
        roots = {name: os.path.join(self.tmp, name) for name in ("override", "config", "home")}
        for root, out in ((roots["override"], 1), (os.path.join(roots["config"], "projects"), 2),
                          (os.path.join(roots["home"], ".claude", "projects"), 3)):
            support.ClaudeTree(root).main(SID, [line("m", "03:01:00.000Z", out=out)])
        cases = [({"USK_EVAL_CLAUDE_ROOT": roots["override"], "CLAUDE_CONFIG_DIR": roots["config"], "HOME": roots["home"]}, 1),
                 ({"CLAUDE_CONFIG_DIR": roots["config"], "HOME": roots["home"]}, 2),
                 ({"HOME": roots["home"]}, 3)]
        for env, expected in cases:
            with self.subTest(env=sorted(env)):
                record = usage.read_usage(support.host_claude(SID), support.T0, support.T1, env=env, now=support.NOW)
                self.assertEqual(record["tokens"]["output"], expected)

    def test_unsafe_session_ids_never_reach_the_file_system(self):
        support.write_text(os.path.join(self.root, "escape.jsonl"), support.jsonl([line("m", "03:01:00.000Z", out=999)]))
        self.tree.main(SID, [line("a", "03:01:00.000Z", out=1)])
        for bad in ("../escape", "..", "a/b", "a\\b", "*", "[ab]", "x y", "", "-lead", "a" * 200):
            with self.subTest(bad=bad[:12]):
                self.assertEqual(claude.session_files(self.root, [bad]), ([], []))
        record = usage.read_usage(support.host_claude("../escape"), support.T0, support.T1, env=self.env, now=support.NOW)
        self.assertEqual(record["unavailableReason"], "no_host_session")

    def test_declared_detection_prefers_the_latest_line(self):
        self.tree.main(SID, [line("a", "03:01:00.000Z", version="2.1.290", effort="high", model="claude-opus-5-5"),
                             line("b", "03:02:00.000Z", version="2.1.297", effort="max", model="claude-sonnet-5-5",
                                  requested="claude-sonnet-5-5[1M]"),
                             line("c", "03:30:00.000Z", version="9.9.9", effort="low", model="claude-haiku-5-5")])
        declared = usage.detect_declared(support.host_claude(SID), support.T0, support.T1, env=self.env, now=support.NOW)
        self.assertEqual(declared["hostVersion"], {"value": "2.1.297", "source": "detected"})
        self.assertEqual(declared["model"], {"value": "claude-sonnet-5-5[1M]", "source": "detected"})
        self.assertEqual(declared["effort"], {"value": "max", "source": "detected"})

    def test_declared_detection_without_a_window_looks_at_everything_before_now(self):
        self.tree.main(SID, [line("a", "03:01:00.000Z", version="2.1.290"), line("b", "03:30:00.000Z", version="2.1.299"),
                             line("c", "04:30:00.000Z", version="9.9.9")])
        declared = usage.detect_declared(support.host_claude(SID), env=self.env, now=support.NOW)
        self.assertEqual(declared["hostVersion"], {"value": "2.1.299", "source": "detected"})

    def test_declared_detection_without_a_log_is_unknown_except_the_host(self):
        declared = usage.detect_declared(support.host_claude(SID), support.T0, support.T1, env=self.env, now=support.NOW)
        unknown = {"value": None, "source": "unknown"}
        self.assertEqual(declared, {"host": {"value": "claude-code", "source": "detected"}, "hostVersion": unknown,
                                    "model": unknown, "effort": unknown})


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
