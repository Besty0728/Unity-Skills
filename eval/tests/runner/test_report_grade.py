"""report.py: grade from strictPassRate, template verdict per language, verdict line in report.md."""
import unittest

from .runner_support import EVAL_DIR  # noqa: F401  (puts usk_eval on sys.path)
from usk_eval import report


def _summary(passed, partial, fail, rate=None, **excluded):
    outcomes = passed + partial + fail
    summary = {"pass": passed, "partial": partial, "fail": fail, "invalid": 0, "contaminated": 0, "cancelled": 0}
    summary.update(excluded)
    summary["strictPassRate"] = rate if rate is not None else (round(passed / outcomes, 4) if outcomes else 0.0)
    return summary


def _row(task_id, status, requirements=()):
    return {"taskId": task_id, "status": status, "requirementsFailed": list(requirements)}


class GradeTests(unittest.TestCase):
    def test_thresholds_are_inclusive_at_the_floor(self):
        cases = [(0.9, "excellent"), (0.8999, "good"), (0.7, "good"), (0.6999, "pass"), (0.5, "pass"),
                 (0.4999, "needs_work"), (0.0, "needs_work")]
        for rate, grade in cases:
            self.assertEqual(report.grade_for(_summary(1, 0, 1, rate=rate)), grade, rate)

    def test_no_gradable_tasks_gives_null_grade(self):
        self.assertIsNone(report.grade_for(_summary(0, 0, 0)))
        self.assertIsNone(report.grade_for(_summary(0, 0, 0, invalid=3, cancelled=1)))

    def test_excluded_statuses_are_not_counted(self):
        session = {"order": ["a", "b", "c", "d"], "tasks": {
            "a": {"state": "done"}, "b": {"state": "done"}, "c": {"state": "invalid"}, "d": {"state": "cancelled"}}}
        finals = {"a": {"status": "pass"}, "b": {"status": "fail"}, "c": {"status": "invalid"},
                  "d": {"status": "cancelled"}}
        summary = report.summarize(session, finals, [])
        self.assertEqual(summary["strictPassRate"], 0.5)
        self.assertEqual(report.grade_for(summary), "pass")
        verdict = report.build_verdict(summary, [], "en")
        self.assertIn("2 task(s) invalid, contaminated or cancelled, not counted.", verdict)
        self.assertIn("1 of 2 scored tasks fully passed", verdict)


class VerdictTests(unittest.TestCase):
    def test_english_verdict_lists_unfinished_tasks_sorted(self):
        summary = _summary(8, 1, 1)
        tasks = [_row("t9", "fail", ["R3"]), _row("t2", "partial", ["R1", "R2"]), _row("t5", "pass")]
        self.assertEqual(report.build_verdict(summary, tasks, "en"),
                         "Good: 8 of 10 scored tasks fully passed. Not fully passed: t2 (partial: R1, R2); "
                         "t9 (fail: R3).")

    def test_task_without_requirement_ids_shows_dash(self):
        verdict = report.build_verdict(_summary(0, 0, 1), [_row("t1", "fail")], "en")
        self.assertIn("t1 (fail: -)", verdict)

    def test_list_is_truncated_after_five_tasks(self):
        tasks = [_row("t%d" % i, "fail", ["R1"]) for i in range(7, 0, -1)]
        verdict = report.build_verdict(_summary(0, 0, 7), tasks, "en")
        for i in range(1, 6):
            self.assertIn("t%d (fail: R1)" % i, verdict)
        self.assertNotIn("t6 (", verdict)
        self.assertIn("; and 2 more.", verdict)

    def test_no_truncation_note_at_five_tasks(self):
        tasks = [_row("t%d" % i, "fail", ["R1"]) for i in range(5)]
        self.assertNotIn("more", report.build_verdict(_summary(0, 0, 5), tasks, "en"))

    def test_each_language_uses_its_grade_word(self):
        summary = _summary(8, 0, 2)
        self.assertTrue(report.build_verdict(summary, [], "zh-CN").startswith("良好：完全通过 8 / 10 个已评分任务。"))
        self.assertTrue(report.build_verdict(summary, [], "en").startswith("Good: 8 of 10 scored tasks fully passed."))
        self.assertTrue(report.build_verdict(summary, [], "ru").startswith("Хорошо: полностью выполнены 8 из 10"))

    def test_unknown_or_missing_language_falls_back_to_english(self):
        summary = _summary(1, 0, 0)
        expected = report.build_verdict(summary, [], "en")
        self.assertEqual(report.build_verdict(summary, [], "fr"), expected)
        self.assertEqual(report.build_verdict(summary, [], None), expected)

    def test_no_gradable_tasks_verdict(self):
        self.assertEqual(report.build_verdict(_summary(0, 0, 0, invalid=2), [], "en"),
                         "No gradable tasks. 2 task(s) invalid, contaminated or cancelled, not counted.")

    def test_russian_text_avoids_forbidden_uppercase_letters(self):
        forbidden = set("ЦЧШЩЪЬЁ")
        texts = [text for grade in report.GRADE_WORDS.values() for lang, text in grade.items() if lang == "ru"]
        texts += [text for key, text in report.VERDICT_TEXT["ru"].items() if isinstance(text, str)]
        for text in texts:
            self.assertFalse(forbidden & set(text), text)


class MarkdownTests(unittest.TestCase):
    def test_report_md_has_verdict_line_near_the_top(self):
        summary = _summary(1, 0, 1)
        verdict = report.build_verdict(summary, [_row("t1", "fail", ["R2"])], "en")
        md = report.render_md({"sessionId": "s1", "status": "done", "summary": summary, "tasks": [],
                               "verdict": verdict})
        self.assertEqual(md.splitlines()[2], "- Verdict: %s" % verdict)


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
