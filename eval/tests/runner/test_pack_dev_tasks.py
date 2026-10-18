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

DEV_TASKS = ("T1", "T2", "T9")
PACKAGES = {"T1": [], "T2": ["com.unity.ugui"], "T9": []}
UNTOUCHED = {"T1": {"B1", "B2", "B3", "B4", "B5", "B6", "B7", "B8", "B9", "B10", "B13", "B14"},
             "T2": {"D1", "D2", "D3", "D4", "D5", "D6", "D7", "D8", "D9", "D10", "D13", "D14"},
             "T9": {"C1", "C2", "C3", "C4", "C5", "C11", "C12"}}


class DevTaskTests(unittest.TestCase):
    def test_attributes(self):
        for task_id in DEV_TASKS:
            task = modules(task_id)[0]
            with self.subTest(task=task_id):
                self.assertEqual((task.track, task.tier, task.case_version), ("dev", "standard", 2))
                self.assertEqual(dict(task.requires), {"pipeline": "URP", "mode": "bypass", "dedicated": True,
                                                       "packages": PACKAGES[task_id]})

    def test_build_prompt_and_c0(self):
        for task_id in DEV_TASKS:
            with self.subTest(task=task_id):
                run = Offline(self, task_id, pipeline="URP")
                again = Offline(self, task_id, pipeline="URP")
                self.assertEqual(run.fingerprint, again.fingerprint)
                bad = [key for key in keys(run.handles) + keys(run.snap)
                       if isinstance(key, str) and ROLE_RE.search(key)]
                self.assertEqual(bad, [])
                task_vars = run.task.prompt_vars(run.build_ctx)
                variables = prompts.merge_vars({"R": run.scope.object_root, "A": run.scope.asset_root}, task_vars)
                for lang in prompts.LANGS:
                    with open(os.path.join(PACK_ROOT, "tasks", task_id, "prompt.%s.md" % lang), encoding="utf-8") as h:
                        text = prompts.render_requirements(prompts.parse_prompt(h.read()), variables)
                    self.assertNotIn("{", text.split("```")[0])
                    for value in task_vars.values():
                        self.assertIn(str(value), text)
                    json.dumps(prompts.render_schema(prompts.with_declared(run.task.answer_schema()), lang))

    def test_solutions(self):
        for task_id in DEV_TASKS:
            _task, _private, solutions = modules(task_id)
            for ids in ("legacy", "6000.4"):
                for repeat in (1, 2):
                    run = Offline(self, task_id, pipeline="URP", entity_mode=ids)
                    card = run.verify(run.solve(solutions.reference))
                    self.assertTrue(card.strict, "%s reference #%d (%s ids): %s" % (task_id, repeat, ids,
                                                                                   _detail(card)))
                for negative in solutions.NEGATIVES:
                    with self.subTest(task=task_id, negative=negative.name, ids=ids):
                        run = Offline(self, task_id, pipeline="URP", entity_mode=ids)
                        card = run.verify(run.solve(negative.apply))
                        self.assertEqual(set(card.failed()), negative.expect_failed, _detail(card))
                for name, fn in solutions.ALTERNATIVES:
                    with self.subTest(task=task_id, alternative=name, ids=ids):
                        run = Offline(self, task_id, pipeline="URP", entity_mode=ids)
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
