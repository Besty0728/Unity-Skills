"""Offline reporting checks for duplicate runs, complete grids and cohort integrity."""
import os
import sys
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

from harness.report import cell_stats, calibration_summary, cohort_integrity, discrimination, suite_table  # noqa: E402


def report(attempt, rep, status="pass", model="claude-opus-5-5", host="claude", effort="high",
           completion=100, wall=20, http=10, cost=0.2, cache=100, task="T1"):
    return {"attemptId": attempt, "rep": rep, "task": task, "kind": "model", "status": status,
            "host": host, "model": model, "effort": effort, "warm": True,
            "score": {"completion": completion},
            "subject": {"wallSeconds": wall},
            "metrics": {"httpTotal": http, "tokens": {"processed": 1200, "cacheRead": cache},
                        "result": {"total_cost_usd": cost}},
            "evalHash": {"harness": "hh", "task": "th"}, "docs": {"treeHash": "dh"},
            "envBefore": {"pluginHash": "ph", "version": "2.9.0", "unityVersion": "2022.3",
                          "pipelineType": "URP", "currentMode": "bypass", "tokenLevel": "high",
                          "surfaceProfile": "minimal", "dryRunPolicy": "off",
                          "tools": {"claude": "2.1.287", "codex": "1.0.0", "unityCli": "1.0.0",
                                    "python": "3.12.0", "os": "Darwin"}}}


class CellStatsTests(unittest.TestCase):
    def test_duplicate_valid_outcome_is_rejected(self):
        rows = [report("a", 0), report("b", 0)]
        with self.assertRaisesRegex(ValueError, "duplicate valid outcome.*host, model, effort, task, rep"):
            cell_stats(rows)

    def test_completion_cache_and_pass_cost_statistics(self):
        rows = [report("a", 0, completion=90, cost=0.2, cache=100),
                report("b", 1, status="partial", completion=30, cost=0.7, cache=300)]
        st = cell_stats(rows)
        self.assertEqual(st["completionMax"], 90)
        self.assertEqual(st["completionRange"], 60)
        self.assertTrue(st["instability"])
        self.assertEqual(st["tokensMedian"], 1200)
        self.assertEqual(st["cacheReadMedian"], 200)
        self.assertEqual(st["passingCostTotal"], 0.2)
        self.assertEqual(st["passingCostMedian"], 0.2)

    def test_missing_metrics_remain_unknown(self):
        st = cell_stats([{"attemptId": "a", "rep": 0, "task": "T1", "host": "claude", "model": "m",
                          "effort": "high", "status": "pass", "score": {"completion": 100}}])
        self.assertIsNone(st["cacheReadMedian"])
        self.assertIsNone(st["passingCostTotal"])
        self.assertIsNone(st["instability"])

    def test_cohort_mismatch_is_rejected(self):
        left, right = report("a", 0), report("b", 1)
        right["docs"]["treeHash"] = "other"
        with self.assertRaisesRegex(ValueError, "mixed calibration cohort.*docs.treeHash"):
            cell_stats([left, right])


class GridTests(unittest.TestCase):
    def test_suite_table_rejects_host_or_effort_mixing_for_model(self):
        with self.assertRaisesRegex(ValueError, "mixes host/effort"):
            suite_table([report("a", 0), report("b", 1, effort="max")], ["T1"], ["claude-opus-5-5"])

    def test_discrimination_is_unknown_until_two_configs_have_samples(self):
        rows = [report("a", 0)]
        disc = discrimination(rows, "T1", ["claude-opus-5-5", "claude-sonnet-5-5"])
        self.assertIsNone(disc["D"])
        self.assertIsNone(disc["deltaC"])
        self.assertIsNone(disc["ceiling"])
        self.assertFalse(disc["complete"])

    def test_completeness_uses_actual_config_count_not_a_fixed_seven(self):
        configs = ["claude-opus-5-5", "claude-sonnet-5-5"]
        rows = [report("o%d" % i, i, model=configs[0]) for i in range(3)]
        rows += [report("s%d" % i, i, model=configs[1], completion=50, cost=0.1) for i in range(3)]
        disc = discrimination(rows, "T1", configs, expected_reps=3)
        self.assertTrue(disc["complete"])
        self.assertTrue(disc["ceiling"])
        self.assertFalse(disc["floor"])
        self.assertEqual(disc["D"], 0)
        self.assertEqual(disc["classification"], "discriminating")
        self.assertEqual(disc["deltaC"], 50)

    def test_complete_grid_with_missing_cohort_evidence_keeps_comparisons_unknown(self):
        configs = ["claude-opus-5-5", "claude-sonnet-5-5"]
        rows = [report("o%d" % i, i, model=configs[0]) for i in range(3)]
        rows += [report("s%d" % i, i, model=configs[1], completion=50) for i in range(3)]
        del rows[-1]["envBefore"]["tools"]

        disc = discrimination(rows, "T1", configs, expected_reps=3)

        self.assertTrue(disc["complete"])
        self.assertIsNone(disc["D"])
        self.assertIsNone(disc["deltaC"])
        self.assertEqual(disc["classification"], "unknown")
        self.assertIsNone(disc["ceiling"])
        self.assertIsNone(disc["floor"])

    def test_discrimination_rejects_cohort_mismatch_between_configs(self):
        configs = ["claude-opus-5-5", "claude-sonnet-5-5"]
        rows = [report("o%d" % i, i, model=configs[0]) for i in range(3)]
        rows += [report("s%d" % i, i, model=configs[1], completion=50) for i in range(3)]
        for row in rows[3:]:
            row["docs"]["treeHash"] = "other"

        with self.assertRaisesRegex(ValueError, "mixed calibration cohort.*docs.treeHash"):
            discrimination(rows, "T1", configs, expected_reps=3)

    def test_discrimination_uses_count_delta_and_requires_exact_rep_grid(self):
        configs = ["claude-opus-5-5", "claude-sonnet-5-5"]
        rows = [report("o%d" % i, i, model=configs[0], status="pass" if i < 2 else "fail")
                for i in range(3)]
        rows += [report("s%d" % i, i, model=configs[1], status="fail", completion=20) for i in range(3)]
        disc = discrimination(rows, "T1", configs, expected_reps=3)
        self.assertEqual(disc["D"], 2)
        self.assertEqual(disc["classification"], "discriminating")
        self.assertFalse(disc["ceiling"])
        self.assertFalse(disc["floor"])
        gapped = [r for r in rows if r["model"] != configs[0] or r["rep"] != 1]
        gapped.append(report("o3", 3, model=configs[0], status="pass"))
        gapped_disc = discrimination(gapped, "T1", configs, expected_reps=3)
        self.assertFalse(gapped_disc["complete"])
        self.assertIsNone(gapped_disc["D"])
        self.assertIsNone(gapped_disc["classification"] if gapped_disc["classification"] != "unknown" else None)

    def test_floor_uses_mean_over_all_valid_attempts(self):
        configs = ["claude-opus-5-5", "claude-sonnet-5-5"]
        rows = [report("o%d" % i, i, model=configs[0], status="fail", completion=20) for i in range(3)]
        rows += [report("s%d" % i, i, model=configs[1], status="fail", completion=25) for i in range(3)]
        disc = discrimination(rows, "T1", configs, expected_reps=3)
        self.assertTrue(disc["floor"])

    def test_summary_keeps_missing_cells_unknown_and_recommends_budget(self):
        rows = [report("o%d" % i, i, wall=10 + i) for i in range(3)]
        summary = calibration_summary(rows, ["T1"], ["claude-opus-5-5", "claude-sonnet-5-5"],
                                      expected_reps=3, task_budgets={"T1": 20}, task_http_budgets={"T1": 10})
        self.assertIn("0/0 · status none · C unknown", summary)
        self.assertIn("status pass:3", summary)
        self.assertIn("processed 1200", summary)
        self.assertIn("HTTP*", summary)
        self.assertIn("approximate, not authoritative", summary)
        self.assertIn("unknown", summary)
        self.assertIn("P90 12.0s × 1.5 → 18s", summary)
        self.assertIn("clamped to 0.5–2× pilot timeout", summary)
        self.assertIn("P90 10.0 × 1.5 → 15", summary)
        self.assertIn("clamped to 0.5–2× pilot HTTP cap", summary)

    def test_no_valid_attempts_is_not_reported_as_consistent_cohort(self):
        summary = calibration_summary([], ["T1"], ["claude-opus-5-5"])
        self.assertIn("no valid attempts; cohort evidence is unknown", summary)
        self.assertNotIn("observed cohort evidence is consistent", summary)

    def test_cohort_integrity_exposes_missing_evidence(self):
        state = cohort_integrity([{"attemptId": "old", "status": "pass"}])
        self.assertTrue(state["consistent"])
        self.assertIn("evalHash.harness", state["missing"])


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
