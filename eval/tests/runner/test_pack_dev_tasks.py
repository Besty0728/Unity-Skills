"""Offline checks of the ported dev-track tasks against the in-memory editor of pack_fake_editor.

Same shape as test_pack_basic_tasks: builds and prompts render, one seed gives one fingerprint, handles and C0 carry
no role labels, and the reference, every negative and every alternative end exactly as declared, including the
SCENARIO the runner plays at submit (run on the model's Play Mode stand-in). The live selftest on a URP editor stays the
gate; scripts "compile" here by parsing their public fields.
"""
import json
import os
import unittest

from .test_pack_basic_tasks import PACK_ROOT, ROLE_RE, Offline, _detail, keys, modules
from usk_eval import prompts  # noqa: E402

DEV_TASKS = ("T1",)
UNTOUCHED = {"T1": {"B1", "B2", "B3", "B4", "B5", "B6", "B7", "B8", "B9", "B10", "B13", "B14"}}


class DevTaskTests(unittest.TestCase):
    def test_attributes(self):
        for task_id in DEV_TASKS:
            task = modules(task_id)[0]
            with self.subTest(task=task_id):
                self.assertEqual((task.track, task.tier, task.case_version), ("dev", "standard", 2))
                self.assertEqual(dict(task.requires), {"pipeline": "URP", "mode": "bypass", "dedicated": True,
                                                       "packages": []})

    def test_build_prompt_and_c0(self):
        for task_id in DEV_TASKS:
            with self.subTest(task=task_id):
                run = Offline(self, task_id, pipeline="URP")
                again = Offline(self, task_id, pipeline="URP")
                self.assertEqual(run.fingerprint, again.fingerprint)
                bad = [key for key in keys(run.handles) + keys(run.snap)
                       if isinstance(key, str) and ROLE_RE.search(key)]
                self.assertEqual(bad, [])
                variables = prompts.merge_vars({"R": run.scope.object_root, "A": run.scope.asset_root},
                                               run.task.prompt_vars(run.build_ctx))
                for lang in prompts.LANGS:
                    with open(os.path.join(PACK_ROOT, "tasks", task_id, "prompt.%s.md" % lang), encoding="utf-8") as h:
                        text = prompts.render_requirements(prompts.parse_prompt(h.read()), variables)
                    self.assertNotIn("{", text.split("```")[0])
                    self.assertIn("```csharp", text)
                    json.dumps(prompts.render_schema(prompts.with_declared(run.task.answer_schema()), lang))

    def test_solutions(self):
        for task_id in DEV_TASKS:
            _task, _private, solutions = modules(task_id)
            for repeat in (1, 2):
                run = Offline(self, task_id, pipeline="URP")
                card = run.verify(run.solve(solutions.reference))
                self.assertTrue(card.strict, "%s reference #%d: %s" % (task_id, repeat, _detail(card)))
            for negative in solutions.NEGATIVES:
                with self.subTest(task=task_id, negative=negative.name):
                    run = Offline(self, task_id, pipeline="URP")
                    card = run.verify(run.solve(negative.apply))
                    self.assertEqual(set(card.failed()), negative.expect_failed, _detail(card))
            for name, fn in solutions.ALTERNATIVES:
                with self.subTest(task=task_id, alternative=name):
                    run = Offline(self, task_id, pipeline="URP")
                    card = run.verify(run.solve(fn))
                    self.assertTrue(card.strict, _detail(card))

    def test_untouched_fixture(self):
        for task_id in DEV_TASKS:
            with self.subTest(task=task_id):
                run = Offline(self, task_id, pipeline="URP")
                card = run.verify(None)
                self.assertEqual(set(card.failed()), UNTOUCHED[task_id], _detail(card))

    def test_scenario_steps_are_valid(self):
        for task_id in DEV_TASKS:
            with self.subTest(task=task_id):
                run = Offline(self, task_id, pipeline="URP")
                run.verify(run.solve(modules(task_id)[2].reference))
                spec = run.scenario
                self.assertTrue(spec["passed"], spec)
                kinds = [step["kind"] for step in spec["steps"]]
                self.assertTrue({"assert", "wait_condition"} & set(kinds))

# Producer:Betsy
