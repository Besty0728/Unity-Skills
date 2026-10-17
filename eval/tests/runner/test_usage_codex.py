"""Codex rollout reader (contract 8.3): counter differences, candidate selection, subagents, record shape."""
import os
import tempfile
import unittest

try:
    from . import test_usage_support as support
except ImportError:
    import test_usage_support as support

from usk_eval import usage

R = "01a10000-0000-7000-8000-000000000001"
S1 = "01a10000-0000-7000-8000-000000000002"
S2 = "01a10000-0000-7000-8000-000000000003"
SIB = "01a10000-0000-7000-8000-000000000004"
R2 = "01a10000-0000-7000-8000-000000000005"
meta, count, context, message = support.cx_meta, support.cx_count, support.cx_context, support.cx_message
NO_VALUES = {"values": [], "source": None}


def totals(record):
    tokens = record["tokens"]
    return tokens["input"], tokens["output"], tokens["processed"]


class GoldenFixtureTests(unittest.TestCase):
    """fixtures/usage/codex: root thread with a forked subagent, a finished subagent, foreign threads, compaction."""

    def setUp(self):
        self.env = {"USK_EVAL_CODEX_ROOT": str(support.FIXTURES / "codex"), "HOME": str(support.FIXTURES / "no-home")}

    def read(self, host):
        return usage.read_usage(host, support.T0, support.T1, env=self.env, now=support.NOW)

    def check(self, record):
        self.assertEqual(list(record), support.CONTRACT_KEYS)
        self.assertEqual((record["host"], record["source"]), ("codex", "codex-rollout"))
        self.assertEqual(record["sessionIds"], [support.CODEX_ROOT_THREAD])
        self.assertEqual((record["parserVersion"], record["unavailableReason"], record["errors"]), (1, None, []))
        # root thread: counters 5000/3000/100/400/150 at 02:59 -> 15000/11000/300/1300/500 at 03:10:00.000
        # subagent bbbb: 0 -> 9000/6000/50/800/300 (its copied parent history is skipped); cccc ended before t0
        self.assertEqual(record["tokens"], {"input": 4750, "output": 1700, "cacheRead": 14000, "cacheWrite": 250,
                                            "reasoning": 650, "processed": 20700})
        self.assertEqual(record["raw"], {"input_tokens": 19000, "cached_input_tokens": 14000, "cache_write_input_tokens": 250,
                                         "output_tokens": 1700, "reasoning_output_tokens": 650, "total_tokens": 20700})
        self.assertEqual(record["subagents"], {"files": 1, "tokens": {"input": 2950, "output": 800, "cacheRead": 6000,
                                                                       "cacheWrite": 50, "reasoning": 300, "processed": 9800}})
        self.assertEqual(record["messages"], 3)  # two in the root thread, one own message of the subagent
        self.assertEqual(record["model"], {"values": ["gpt-6-luna", "gpt-6.1-sol"], "source": "host-requested"})
        self.assertEqual(record["effort"], {"values": ["high", "medium"], "source": "host-reported"})

    def test_by_thread_id(self):
        self.check(self.read(support.host_codex(support.CODEX_ROOT_THREAD)))

    def test_by_working_directory(self):
        self.check(self.read(support.host_codex()))

    def test_declared_detection(self):
        expected = {"host": {"value": "codex", "source": "detected"}, "hostVersion": {"value": "0.161.0", "source": "detected"},
                    "model": {"value": "gpt-6-luna", "source": "detected"}, "effort": {"value": "high", "source": "detected"}}
        for host in (support.host_codex(support.CODEX_ROOT_THREAD), support.host_codex()):
            with self.subTest(ids=host["sessionIds"]):
                self.assertEqual(usage.detect_declared(host, support.T0, support.T1, env=self.env, now=support.NOW), expected)


class AmbiguousFixtureTests(unittest.TestCase):
    """fixtures/usage/codex-ambiguous: two root threads in one working directory, both active in the window."""

    def read(self, *ids):
        env = {"USK_EVAL_CODEX_ROOT": str(support.FIXTURES / "codex-ambiguous"), "HOME": str(support.FIXTURES / "no-home")}
        return usage.read_usage(support.host_codex(*ids), support.T0, support.T1, env=env, now=support.NOW)

    def test_the_working_directory_alone_cannot_tell_them_apart(self):
        record = self.read()
        self.assertEqual((record["unavailableReason"], record["source"], record["sessionIds"]),
                         ("ambiguous_rollouts", "codex-rollout", []))
        for key in ("tokens", "raw", "messages", "subagents"):
            self.assertIsNone(record[key])

    def test_a_thread_id_settles_it(self):
        first = self.read("01a11000-0000-7000-8000-00000000a001")
        second = self.read("01a11000-0000-7000-8000-00000000a002")
        self.assertEqual(first["tokens"], dict(support.ZERO_TOKENS, input=300, output=30, processed=330))
        self.assertEqual(first["model"]["values"], ["gpt-6-luna"])
        self.assertEqual(second["tokens"], dict(support.ZERO_TOKENS, input=1600, output=200, cacheRead=400, processed=2200))
        self.assertEqual(second["model"]["values"], ["gpt-6.1-sol"])


class CodexReaderTests(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.tmp = tmp.name
        self.root = os.path.join(self.tmp, "sessions")
        self.cwd = os.path.join(self.tmp, "project")
        os.makedirs(self.cwd)
        self.tree = support.CodexTree(self.root)
        self.env = {"USK_EVAL_CODEX_ROOT": self.root, "HOME": os.path.join(self.tmp, "home")}

    def read(self, *ids, **kwargs):
        host = support.host_codex(*ids, cwd=kwargs.get("cwd", self.cwd))
        return usage.read_usage(host, kwargs.get("t0", support.T0), kwargs.get("t1", support.T1), env=self.env, now=support.NOW)

    def thread(self, ident, lines, **kwargs):
        return self.tree.rollout(ident, [meta(ident, kwargs.pop("cwd", self.cwd), **kwargs.pop("meta", {}))] + lines, **kwargs)

    def test_counters_are_differenced_between_the_window_bounds(self):
        self.thread(R, [count("02:59:00.000Z", 5000, 3000, 100, 400, 150), count("03:05:00.000Z", 12000, 9000, 300, 1000, 400),
                        count("03:10:00.000Z", 15000, 11000, 300, 1300, 500), count("03:10:00.001Z", 99999, 0, 0, 99999, 0)])
        tokens = self.read(R)["tokens"]
        self.assertEqual(tokens, {"input": 1800, "output": 900, "cacheRead": 8000, "cacheWrite": 200, "reasoning": 350,
                                  "processed": 10900})

    def test_without_an_earlier_event_the_baseline_is_zero(self):
        self.thread(R, [count("03:01:00.000Z", 100, 40, 10, 7, 3)])
        record = self.read(R)
        self.assertEqual(record["tokens"], {"input": 50, "output": 7, "cacheRead": 40, "cacheWrite": 10, "reasoning": 3,
                                            "processed": 107})

    def test_input_is_clamped_at_zero_and_missing_cache_write_counts_as_zero(self):
        event = count("03:01:00.000Z", 100, 300, 0, 5, 0)
        del event["payload"]["info"]["total_token_usage"]["cache_write_input_tokens"]
        self.thread(R, [event])
        tokens = self.read(R)["tokens"]
        self.assertEqual((tokens["input"], tokens["cacheRead"], tokens["cacheWrite"], tokens["processed"]), (0, 300, 0, 105))

    def test_events_without_info_and_other_record_types_are_ignored(self):
        null_info = {"timestamp": support.stamp("03:02:00.000Z"), "type": "event_msg",
                     "payload": {"type": "token_count", "info": None}}
        usage_record = {"timestamp": support.stamp("03:02:00.000Z"), "type": "token_usage_record",
                        "payload": {"usage": support.cx_counters(10 ** 9, 0, 0, 10 ** 9, 0)}}
        self.thread(R, [count("03:01:00.000Z", 100, 0, 0, 10, 0), null_info, usage_record])
        self.assertEqual(self.read(R)["tokens"]["processed"], 110)

    def test_decreasing_counters_are_a_counter_reset(self):
        self.thread(R, [count("02:59:00.000Z", 5000, 0, 0, 400, 0), count("03:05:00.000Z", 100, 0, 0, 10, 0)])
        record = self.read(R)
        self.assertEqual(record["unavailableReason"], "counter_reset")
        for key in ("tokens", "raw", "messages", "subagents"):
            self.assertIsNone(record[key])

    def test_a_dip_inside_the_window_is_a_counter_reset_even_when_the_bounds_look_fine(self):
        self.thread(R, [count("02:59:00.000Z", 1000, 0, 0, 100, 0), count("03:01:00.000Z", 5000, 0, 0, 500, 0),
                        count("03:02:00.000Z", 200, 0, 0, 20, 0), count("03:09:00.000Z", 3000, 0, 0, 300, 0)])
        self.assertEqual(self.read(R)["unavailableReason"], "counter_reset")

    def test_a_reset_in_a_subagent_nulls_the_whole_result(self):
        self.thread(R, [count("03:01:00.000Z", 100, 0, 0, 10, 0)])
        self.thread(S1, [count("03:01:00.000Z", 900, 0, 0, 90, 0), count("03:02:00.000Z", 10, 0, 0, 1, 0)],
                    meta={"session": R, "source": "subagent", "parent": R})
        self.assertEqual(self.read(R)["unavailableReason"], "counter_reset")

    def test_no_event_in_the_window_reports_zero_tokens(self):
        self.thread(R, [count("02:59:00.000Z", 5000, 0, 0, 400, 0), count("03:11:00.000Z", 9000, 0, 0, 800, 0)])
        record = self.read(R)
        self.assertEqual(record["unavailableReason"], "no_messages_in_window")
        self.assertEqual(record["tokens"], support.ZERO_TOKENS)
        self.assertEqual((record["messages"], record["subagents"]["files"], record["model"]), (0, 0, NO_VALUES))
        self.assertEqual(list(record["raw"].values()), [0] * 6)

    def test_malformed_counters_are_a_parse_error(self):
        bad = {"timestamp": support.stamp("03:01:00.000Z"), "type": "event_msg",
               "payload": {"type": "token_count", "info": {"total_token_usage": "n/a"}}}
        self.thread(R, [bad, dict(bad)])
        record = self.read(R)
        self.assertEqual((record["unavailableReason"], record["tokens"]), ("parse_error", None))

    def test_a_rollout_without_session_meta_is_a_parse_error(self):
        self.tree.rollout(R, [count("03:01:00.000Z", 100, 0, 0, 10, 0)])
        self.assertEqual(self.read(R)["unavailableReason"], "parse_error")

    def test_unparsable_event_times_are_reported_and_skipped(self):
        broken = count("03:01:00.000Z", 777, 0, 0, 7, 0)
        broken["timestamp"] = "not-a-time"
        self.thread(R, [broken, count("03:02:00.000Z", 100, 0, 0, 10, 0)])
        record = self.read(R)
        self.assertEqual(record["tokens"]["processed"], 110)
        self.assertEqual([(e["line"], e["file"].endswith(R + ".jsonl")) for e in record["errors"]], [(2, True)])

    def test_assistant_messages_in_the_window_are_counted(self):
        self.thread(R, [message("02:59:00.000Z"), message("03:01:00.000Z"), message("03:02:00.000Z", "user"),
                        message("03:03:00.000Z"), message("03:10:00.001Z"), count("03:04:00.000Z", 10, 0, 0, 1, 0)])
        self.assertEqual(self.read(R)["messages"], 2)

    def test_model_comes_from_turn_contexts_inside_the_window(self):
        self.thread(R, [context("02:30:00.000Z", "gpt-a", "low"), context("03:01:00.000Z", "gpt-b", "high"),
                        context("03:03:00.000Z", "gpt-c", "medium"), context("03:30:00.000Z", "gpt-z", "max"),
                        count("03:04:00.000Z", 10, 0, 0, 1, 0)])
        record = self.read(R)
        self.assertEqual(record["model"], {"values": ["gpt-b", "gpt-c"], "source": "host-requested"})
        self.assertEqual(record["effort"], {"values": ["high", "medium"], "source": "host-reported"})

    def test_model_falls_back_to_the_last_turn_before_the_window(self):
        self.thread(R, [context("02:30:00.000Z", "gpt-a", "low"), context("02:50:00.000Z", "gpt-b", "high"),
                        count("03:04:00.000Z", 10, 0, 0, 1, 0)])
        record = self.read(R)
        self.assertEqual((record["model"]["values"], record["effort"]["values"]), (["gpt-b"], ["high"]))

    def test_thread_id_not_found(self):
        self.thread(R2, [count("03:01:00.000Z", 10, 0, 0, 1, 0)])
        record = self.read(R)
        self.assertEqual((record["unavailableReason"], record["source"], record["sessionIds"]),
                         ("rollout_not_found", "codex-rollout", [R]))

    def test_two_files_for_one_thread_id_are_ambiguous(self):
        for day in (support.DAY_DIR, ("2026", "10", "16")):
            self.thread(R, [count("03:01:00.000Z", 10, 0, 0, 1, 0)], day=day)
        self.assertEqual(self.read(R)["unavailableReason"], "ambiguous_rollouts")

    def test_compressed_rollouts_cannot_be_read_and_say_so(self):
        self.tree.rollout(R, [], suffix=".jsonl.zst")
        record = self.read(R)
        self.assertEqual(record["unavailableReason"], "rollout_not_found")
        self.assertEqual(record["errors"], [{"file": "rollout-2026-10-17T11-00-00-%s.jsonl.zst" % R, "line": 0,
                                             "error": "CompressedRollout"}])

    def test_two_active_threads_in_one_directory_are_ambiguous(self):
        self.thread(R, [count("03:01:00.000Z", 100, 0, 0, 10, 0)])
        self.thread(R2, [count("03:02:00.000Z", 200, 0, 0, 20, 0)])
        record = self.read()
        self.assertEqual(record["unavailableReason"], "ambiguous_rollouts")
        self.assertEqual((record["sessionIds"], record["tokens"]), ([], None))

    def test_a_thread_needs_a_token_event_inside_the_window_to_be_a_candidate(self):
        self.thread(R, [count("03:01:00.000Z", 100, 0, 0, 10, 0)])
        self.thread(R2, [count("02:00:00.000Z", 200, 0, 0, 20, 0)])
        record = self.read()
        self.assertEqual((record["sessionIds"], record["tokens"]["processed"]), ([R], 110))

    def test_another_working_directory_is_not_a_candidate(self):
        other = os.path.join(self.tmp, "other")
        self.thread(R, [count("03:01:00.000Z", 100, 0, 0, 10, 0)], cwd=other)
        record = self.read()
        self.assertEqual((record["unavailableReason"], record["sessionIds"]), ("rollout_not_found", []))

    def test_subagent_threads_are_never_main_candidates(self):
        self.thread(S1, [count("03:01:00.000Z", 100, 0, 0, 10, 0)], meta={"session": R, "source": "subagent", "parent": R})
        self.assertEqual(self.read()["unavailableReason"], "rollout_not_found")

    def test_rollouts_without_thread_source_are_root_threads(self):
        self.thread(R, [count("03:01:00.000Z", 100, 0, 0, 10, 0)], meta={"source": None})
        self.assertEqual(self.read()["sessionIds"], [R])

    def test_working_directories_are_compared_after_normalization(self):
        self.thread(R, [count("03:01:00.000Z", 100, 0, 0, 10, 0)])
        self.assertEqual(self.read(cwd=os.path.join(self.cwd, "..", "project"))["sessionIds"], [R])

    def test_a_symbolic_link_to_the_working_directory_matches(self):
        link = os.path.join(self.tmp, "link")
        try:
            os.symlink(self.cwd, link)
        except (OSError, NotImplementedError, AttributeError):
            self.skipTest("symbolic links are not available")
        self.thread(R, [count("03:01:00.000Z", 100, 0, 0, 10, 0)], cwd=link)
        self.assertEqual(self.read()["sessionIds"], [R])

    def test_flat_layouts_fall_back_to_a_recursive_scan(self):
        flat = support.CodexTree(self.root)
        flat.rollout(R, [meta(R, self.cwd), count("03:01:00.000Z", 100, 0, 0, 10, 0)], day=())
        self.assertEqual(self.read()["sessionIds"], [R])

    def test_the_directory_scan_covers_one_day_around_the_window_when_date_folders_exist(self):
        old = ("2026", "10", "10")
        self.thread(R, [count("03:01:00.000Z", 100, 0, 0, 10, 0)], day=old)
        self.thread(R2, [count("03:01:00.000Z", 999, 0, 0, 99, 0)], cwd=os.path.join(self.tmp, "other"))
        self.assertEqual(self.read()["unavailableReason"], "rollout_not_found")  # R is outside the scanned dates
        self.assertEqual(self.read(R)["sessionIds"], [R])  # a thread id is searched in the whole tree

    def test_without_date_folders_around_the_window_the_whole_tree_is_scanned(self):
        self.thread(R, [count("03:01:00.000Z", 100, 0, 0, 10, 0)], day=("2026", "10", "10"))
        self.assertEqual(self.read()["sessionIds"], [R])

    def test_root_precedence(self):
        roots = {name: os.path.join(self.tmp, name) for name in ("override", "codex_home", "home")}
        for root, out in ((roots["override"], 1), (os.path.join(roots["codex_home"], "sessions"), 2),
                          (os.path.join(roots["home"], ".codex", "sessions"), 3)):
            support.CodexTree(root).rollout(R, [meta(R, self.cwd), count("03:01:00.000Z", 100, 0, 0, out, 0)])
        cases = [({"USK_EVAL_CODEX_ROOT": roots["override"], "CODEX_HOME": roots["codex_home"], "HOME": roots["home"]}, 1),
                 ({"CODEX_HOME": roots["codex_home"], "HOME": roots["home"]}, 2),
                 ({"HOME": roots["home"]}, 3)]
        for env, expected in cases:
            with self.subTest(env=sorted(env)):
                record = usage.read_usage(support.host_codex(R, cwd=self.cwd), support.T0, support.T1, env=env, now=support.NOW)
                self.assertEqual(record["tokens"]["output"], expected)

    def test_declared_detection_without_a_window_by_thread_id_uses_the_latest_turn(self):
        self.thread(R, [context("02:30:00.000Z", "gpt-a", "low"), context("03:30:00.000Z", "gpt-b", "high"),
                        context("04:30:00.000Z", "gpt-z", "max")], meta={"version": "0.170.0"})
        declared = usage.detect_declared(support.host_codex(R), env=self.env, now=support.NOW)
        self.assertEqual({key: value["value"] for key, value in declared.items()},
                         {"host": "codex", "hostVersion": "0.170.0", "model": "gpt-b", "effort": "high"})

    def test_declared_detection_without_a_window_by_directory_needs_activity_in_the_last_half_hour(self):
        self.thread(R, [context("03:40:00.000Z", "gpt-b", "high"), count("03:45:00.000Z", 10, 0, 0, 1, 0)])
        self.thread(R2, [context("02:40:00.000Z", "gpt-old", "low"), count("02:45:00.000Z", 10, 0, 0, 1, 0)],
                    cwd=os.path.join(self.tmp, "other"))
        declared = usage.detect_declared(support.host_codex(cwd=self.cwd), env=self.env, now=support.NOW)
        self.assertEqual((declared["model"]["value"], declared["effort"]["value"]), ("gpt-b", "high"))
        declared = usage.detect_declared(support.host_codex(cwd=os.path.join(self.tmp, "other")), env=self.env, now=support.NOW)
        self.assertEqual(declared["model"], {"value": None, "source": "unknown"})

    def lineage(self):
        counters = {R: (800, 80), S1: (100, 10), S2: (200, 20), SIB: (400, 40)}
        for ident, (inp, out) in counters.items():
            extra = {} if ident == R else {"meta": {"session": R, "source": "subagent", "parent": S1 if ident == S2 else R}}
            self.thread(ident, [count("03:01:00.000Z", inp, 0, 0, out, 0)], **extra)

    def test_a_user_thread_owns_every_subagent_of_its_session(self):
        self.lineage()
        record = self.read(R)
        self.assertEqual(totals(record), (1500, 150, 1650))
        self.assertEqual((record["subagents"]["files"], record["subagents"]["tokens"]["input"]), (3, 700))

    def test_a_subagent_caller_owns_only_its_own_descendants(self):
        self.lineage()
        record = self.read(S1)
        self.assertEqual(totals(record), (300, 30, 330))
        self.assertEqual((record["subagents"]["files"], record["subagents"]["tokens"]["input"]), (1, 200))
        self.assertEqual(record["sessionIds"], [S1])


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
