"""usk_eval.verifylib.Scorecard: weights, requirement mapping, partial credit and the result.json shape (v1
ScorecardTests ported to the 5-tuple spec)."""
import json
import unittest

try:
    from . import test_verifylib_support as support  # noqa: F401  (puts the packaged runner on sys.path)
except ImportError:
    import test_verifylib_support as support  # noqa: F401

from usk_eval.verifylib import Scorecard

SPEC = [("a", 3, True, ["R1"], "a"), ("b", 2, False, ["R2", "G1"], "b"), ("c", 5, False, ["R3"], "c")]


class ScorecardTests(unittest.TestCase):
    def test_weights_must_sum_to_ten(self):
        with self.assertRaises(ValueError):
            Scorecard([("a", 3, False, ["R1"], ""), ("b", 3, False, ["R2"], "")])

    def test_partial_score_and_strict(self):
        card = Scorecard(SPEC)
        card.check("a", True)
        card.check("b", False)
        card.check("c", True)
        card.finish()
        self.assertEqual(card.score, 8.0)
        self.assertFalse(card.strict)
        self.assertEqual(card.failed(), ["b"])
        self.assertEqual(card.as_dict()["completion"], 80.0)

    def test_unevaluated_is_unobservable_failure(self):
        card = Scorecard(SPEC)
        card.check("a", True)
        card.check("b", True)
        card.finish()
        self.assertFalse(card.strict)
        self.assertIn("unobservable", card.results["c"]["note"])
        self.assertEqual(card.results["c"]["evidence"], {"unobservable": True})
        self.assertEqual(card.critical_failed, [])

    def test_critical_failure_listed(self):
        card = Scorecard(SPEC)
        card.check("a", False)
        card.finish()
        self.assertEqual(card.critical_failed, ["a"])

    def test_critical_failures_are_the_critical_subset_of_failed(self):
        card = Scorecard(SPEC)
        card.check("a", True)
        card.check("b", False)
        self.assertEqual(card.critical_failed, [])
        card = Scorecard(SPEC)
        card.check("b", True)
        self.assertEqual(card.failed(), ["a", "c"])
        self.assertEqual(card.critical_failed, ["a"])          # not evaluated yet counts as failed, like failed()

    def test_critical_failures_follow_the_spec_order_not_the_check_order(self):
        card = Scorecard([("x", 5, True, ["R1"], ""), ("y", 5, True, ["R2"], "")])
        card.check("y", False)
        card.check("x", False)
        self.assertEqual(card.critical_failed, ["x", "y"])

    def test_fraction_partial_credit(self):
        card = Scorecard(SPEC)
        card.check("a", True)
        card.check("b", True)
        card.fraction("c", 0.6)
        card.finish()
        self.assertEqual(card.score, 8.0)
        self.assertFalse(card.strict)
        self.assertEqual(card.failed(), ["c"])
        card.fraction("c", 1.0)
        self.assertTrue(card.strict)

    def test_check_and_fraction_report_whether_the_assertion_passed(self):
        card = Scorecard(SPEC)
        self.assertTrue(card.check("a", True))
        self.assertFalse(card.check("b", 0))
        self.assertFalse(card.fraction("c", 0.5))
        self.assertTrue(card.fraction("c", 1))

    def test_recording_again_replaces_the_earlier_result(self):
        card = Scorecard(SPEC)
        card.check("a", False, actual=1)
        card.check("a", True, actual=2)
        self.assertTrue(card.results["a"]["pass"])
        self.assertEqual(card.results["a"]["actual"], 2)

    def test_unknown_assertion_is_a_programming_error(self):
        card = Scorecard(SPEC)
        with self.assertRaises(KeyError):
            card.check("nope", True)

    def test_strict_needs_every_assertion_evaluated_and_passing(self):
        card = Scorecard(SPEC)
        card.check("a", True)
        card.check("b", True)
        self.assertFalse(card.strict)
        card.check("c", True)
        self.assertTrue(card.strict)
        self.assertEqual(card.score, 10.0)

    def test_finish_is_idempotent_and_returns_the_card(self):
        card = Scorecard(SPEC)
        self.assertIs(card.finish(), card)
        first = json.dumps(card.as_dict(), sort_keys=True)
        card.finish()
        self.assertEqual(json.dumps(card.as_dict(), sort_keys=True), first)
        self.assertEqual(card.score, 0.0)
        self.assertEqual(card.failed(), ["a", "b", "c"])

    def test_unobservable_names_its_reason(self):
        card = Scorecard(SPEC)
        card.unobservable("a", "no journal")
        self.assertEqual(card.results["a"]["note"], "unobservable: no journal")
        self.assertFalse(card.results["a"]["pass"])


class FractionTests(unittest.TestCase):
    def test_values_are_clamped_to_the_unit_interval(self):
        card = Scorecard(SPEC)
        card.fraction("a", 7)
        card.fraction("b", -3)
        self.assertEqual((card.results["a"]["fraction"], card.results["b"]["fraction"]), (1.0, 0.0))
        self.assertTrue(card.results["a"]["pass"])

    def test_nan_is_a_failure_never_a_pass(self):
        card = Scorecard(SPEC)
        self.assertFalse(card.fraction("a", float("nan")))
        self.assertEqual(card.results["a"]["fraction"], 0.0)

    def test_rounding_cannot_turn_a_miss_into_a_full_fraction(self):
        card = Scorecard(SPEC)
        card.fraction("a", 0.99996)
        self.assertFalse(card.results["a"]["pass"])
        self.assertLess(card.results["a"]["fraction"], 1.0)
        card.fraction("b", 1.0)
        self.assertEqual((card.results["b"]["pass"], card.results["b"]["fraction"]), (True, 1.0))

    def test_score_uses_weight_times_fraction(self):
        card = Scorecard(SPEC)
        card.fraction("a", 0.5)
        card.fraction("b", 0.25)
        card.fraction("c", 1.0)
        self.assertEqual(card.score, 3 * 0.5 + 2 * 0.25 + 5)


class RequirementMappingTests(unittest.TestCase):
    def test_requirements_failed_is_the_sorted_union_of_failed_assertions(self):
        card = Scorecard(SPEC)
        card.check("a", True)
        card.check("b", False)
        card.check("c", False)
        self.assertEqual(card.requirements_failed(), ["G1", "R2", "R3"])

    def test_nothing_failed_means_no_requirement_failed(self):
        card = Scorecard(SPEC)
        for aid in "abc":
            card.check(aid, True)
        self.assertEqual(card.requirements_failed(), [])

    def test_unevaluated_assertions_count_as_failed(self):
        card = Scorecard(SPEC)
        card.check("a", True)
        self.assertEqual(card.requirements_failed(), ["G1", "R2", "R3"])

    def test_a_shared_requirement_is_listed_once(self):
        card = Scorecard([("a", 5, False, ["R1", "G2"], ""), ("b", 5, False, ["G2", "R1"], "")])
        card.finish()
        self.assertEqual(card.requirements_failed(), ["G2", "R1"])

    def test_agrees_with_the_runner_computation_over_result_rows(self):
        card = Scorecard([("a", 2, False, ["R10"], ""), ("b", 2, False, ["R2", "G1"], ""),
                          ("c", 3, False, ["R1"], ""), ("d", 3, False, ["R3", "R11"], "")])
        card.check("c", True)
        card.finish()
        rows = card.as_dict()["assertions"]
        runner_side = sorted({req for row in rows if not row["pass"] for req in row["req"]})
        self.assertEqual(card.requirements_failed(), runner_side)

    def test_duplicate_requirements_of_one_assertion_collapse(self):
        card = Scorecard([("a", 10, False, ["R1", "R1", "G1"], "")])
        self.assertEqual(card.as_dict()["assertions"][0]["req"], ["R1", "G1"])


class ResultShapeTests(unittest.TestCase):
    ROW_KEYS = ["id", "req", "weight", "critical", "desc", "pass", "fraction", "expected", "actual", "note",
                "evidence", "hint"]

    def card(self):
        card = Scorecard(SPEC)
        card.check("a", True, expected=1, actual=1)
        card.check("b", False, expected={"x": (1, 2)}, actual=None, note="why", extra=[1])
        card.fraction("c", 0.4, actual=2)
        return card.finish()

    def test_top_level_and_row_keys(self):
        data = self.card().as_dict()
        self.assertEqual(sorted(data), ["assertions", "completion", "criticalFailed", "score", "strict"])
        for row in data["assertions"]:
            self.assertEqual(list(row), self.ROW_KEYS)
            self.assertIsNone(row["hint"])

    def test_values(self):
        data = self.card().as_dict()
        self.assertEqual(data["score"], 3 + 0.4 * 5)
        self.assertEqual(data["completion"], 50.0)
        self.assertFalse(data["strict"])
        self.assertEqual(data["criticalFailed"], [])
        rows = {row["id"]: row for row in data["assertions"]}
        self.assertEqual(rows["a"]["req"], ["R1"])
        self.assertEqual(rows["a"]["weight"], 3.0)
        self.assertTrue(rows["a"]["critical"])
        self.assertEqual(rows["b"]["req"], ["R2", "G1"])
        self.assertEqual(rows["b"]["evidence"], {"extra": [1]})
        self.assertEqual(rows["b"]["note"], "why")
        self.assertEqual(rows["c"]["fraction"], 0.4)
        self.assertEqual([r["id"] for r in data["assertions"]], ["a", "b", "c"])

    def test_rows_follow_the_spec_order(self):
        card = Scorecard([("z", 5, False, ["R1"], ""), ("a", 5, False, ["R2"], "")])
        card.check("a", True)
        card.check("z", True)
        self.assertEqual([r["id"] for r in card.as_dict()["assertions"]], ["z", "a"])

    def test_serialises_as_the_runner_writes_it(self):
        text = json.dumps(self.card().as_dict(), ensure_ascii=False, allow_nan=False, indent=1)
        self.assertEqual(json.loads(text)["assertions"][1]["expected"], {"x": [1, 2]})

    def test_an_unfinished_card_still_lists_every_assertion(self):
        card = Scorecard(SPEC)
        card.check("a", True)
        rows = card.as_dict()["assertions"]
        self.assertEqual([r["id"] for r in rows], ["a", "b", "c"])
        self.assertEqual([r["pass"] for r in rows], [True, False, False])
        self.assertEqual(list(rows[2]), self.ROW_KEYS)

    def test_completion_is_ten_times_the_score(self):
        card = Scorecard(SPEC)
        card.fraction("a", 1)
        card.fraction("b", 0.5)
        self.assertEqual(card.as_dict()["completion"], round(card.score * 10, 2))


class JsonSafetyTests(unittest.TestCase):
    """A verifier can record anything; the runner must still be able to write result.json."""

    def record(self, **kwargs):
        card = Scorecard([("a", 10, False, ["R1"], "")])
        card.check("a", False, **kwargs)
        return json.loads(json.dumps(card.as_dict(), allow_nan=False))["assertions"][0]

    def test_tuples_and_sets_become_lists(self):
        row = self.record(expected=(1, 2), actual={"s": {3, 1, 2}, "f": frozenset(["b", "a"])})
        self.assertEqual(row["expected"], [1, 2])
        self.assertEqual(row["actual"], {"s": [1, 2, 3], "f": ["a", "b"]})

    def test_non_finite_numbers_become_text(self):
        row = self.record(actual=[float("nan"), float("inf"), float("-inf"), 1.5])
        self.assertEqual(row["actual"], ["nan", "inf", "-inf", 1.5])

    def test_unknown_objects_and_keys_become_text(self):
        class Thing(object):
            def __str__(self):
                return "thing!"
        row = self.record(actual={1: Thing(), None: b"x"}, note=Thing())
        self.assertEqual(row["actual"], {"1": "thing!", "None": "b'x'"})
        self.assertEqual(row["note"], "thing!")

    def test_evidence_is_cleaned_too(self):
        row = self.record(missing={3, 1}, pair=(1, 2))
        self.assertEqual(row["evidence"], {"missing": [1, 3], "pair": [1, 2]})

    def test_self_referencing_values_terminate(self):
        loop = []
        loop.append(loop)
        row = self.record(actual=loop)
        self.assertIn("[", json.dumps(row["actual"]))

    def test_values_are_copied_when_recorded(self):
        items = [1]
        card = Scorecard([("a", 10, False, ["R1"], "")])
        card.check("a", False, actual=items, expected=(1, 2))
        items.append(2)
        self.assertEqual(card.results["a"]["actual"], [1])
        self.assertIsInstance(card.results["a"]["expected"], list)

    def test_scalars_pass_through_unchanged(self):
        row = self.record(expected=None, actual="text", note="n")
        self.assertEqual((row["expected"], row["actual"], row["note"]), (None, "text", "n"))
        row = self.record(expected=True, actual=3)
        self.assertEqual((row["expected"], row["actual"]), (True, 3))


class SpecValidationTests(unittest.TestCase):
    def assertRejected(self, spec, text=None):
        with self.assertRaises(ValueError) as caught:
            Scorecard(spec)
        if text:
            self.assertIn(text, str(caught.exception))

    def test_the_v1_four_field_row_is_rejected(self):
        self.assertRejected([("a", 10, True, "a")], "reqIds")

    def test_rows_must_have_exactly_five_fields(self):
        self.assertRejected([("a", 10, True, ["R1"])])
        self.assertRejected([("a", 10, True, ["R1"], "d", "extra")])
        self.assertRejected(["abcde"])
        self.assertRejected([None])

    def test_every_assertion_names_at_least_one_requirement(self):
        self.assertRejected([("a", 10, False, [], "d")], "non-empty")
        self.assertRejected([("a", 10, False, "R1", "d")], "list")
        self.assertRejected([("a", 10, False, None, "d")], "list")

    def test_requirements_are_task_requirements_or_general_rules(self):
        for bad in ["X1", "R0", "r1", "R", "R01", "G-1", "R1 ", 1, None]:
            with self.subTest(req=bad):
                self.assertRejected([("a", 10, False, [bad], "d")], "R1 or G2")

    def test_numbered_requirements_and_general_rules_are_accepted(self):
        card = Scorecard([("a", 10, False, ["R1", "R12", "G1", "G8"], "d")])
        self.assertEqual(card.spec["a"]["req"], ["R1", "R12", "G1", "G8"])

    def test_ids_are_unique_non_empty_text(self):
        self.assertRejected([("a", 5, False, ["R1"], ""), ("a", 5, False, ["R2"], "")], "duplicate")
        self.assertRejected([("", 10, False, ["R1"], "")])
        self.assertRejected([(1, 10, False, ["R1"], "")])

    def test_weights_are_finite_and_not_negative(self):
        for weight in [-1, float("nan"), float("inf"), True, "5", None]:
            with self.subTest(weight=weight):
                self.assertRejected([("a", weight, False, ["R1"], ""), ("b", 10, False, ["R2"], "")])

    def test_weights_must_sum_to_ten_within_tolerance(self):
        self.assertRejected([("a", 9.9, False, ["R1"], "")], "sum")
        Scorecard([("a", 3.3333333, False, ["R1"], ""), ("b", 3.3333333, False, ["R2"], ""),
                   ("c", 3.3333334, False, ["R3"], "")])

    def test_an_empty_spec_cannot_sum_to_ten(self):
        self.assertRejected([], "sum")

    def test_a_nan_total_cannot_slip_through(self):
        self.assertRejected([("a", float("nan"), False, ["R1"], "")])

    def test_rows_may_be_lists_and_requirements_tuples(self):
        card = Scorecard([["a", 10, 1, ("R1", "G1"), "desc"]])
        self.assertTrue(card.spec["a"]["critical"] is True)
        self.assertEqual(card.spec["a"]["weight"], 10.0)

    def test_the_spec_iterable_is_read_once(self):
        card = Scorecard(iter([("a", 4, False, ["R1"], ""), ("b", 6, False, ["R2"], "")]))
        self.assertEqual(card.order, ["a", "b"])


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
