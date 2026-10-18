"""Offline checks of the ported dev-track tasks against the in-memory editor of pack_fake_editor.

Same shape as test_pack_basic_tasks: builds and prompts render, one seed gives one fingerprint, handles and C0 carry
no role labels, and the reference, every negative and every alternative end exactly as declared, including the
SCENARIO the runner plays at submit (run on the model's Play Mode stand-in). The live selftest on a URP editor stays the
gate; scripts "compile" here by parsing their public fields.
"""
import json
import os
import shutil
import tempfile
import unittest

from .test_pack_basic_tasks import PACK_ROOT, ROLE_RE, Offline, _detail, keys, modules
from usk_eval import pack, prompts  # noqa: E402

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


STANDALONE, INPUT_SYSTEM_UI = "StandaloneInputModule", "InputSystemUIInputModule"
# (Active Input Handling in ProjectSettings.asset or None, the module T2 asks for)
INPUT_SETTINGS = ((None, STANDALONE), (0, STANDALONE), (2, STANDALONE), (1, INPUT_SYSTEM_UI))


class T2InputModuleTests(unittest.TestCase):
    """T2's EventSystem needs the UI input module that fits the project's Active Input Handling: StandaloneInputModule
    reads the Input Manager and throws on every Play Mode frame when only the Input System package is active, so that
    setting asks for InputSystemUIInputModule instead."""

    def run_t2(self, handler, package=True, **kwargs):
        return Offline(self, "T2", pipeline="URP", input_handler=handler, input_package=package, **kwargs)

    def test_the_module_follows_the_setting_and_the_package(self):
        task = pack.load_source_module("usk_test_task_T2", os.path.join(PACK_ROOT, "tasks", "T2", "task.py"))
        tmp = tempfile.mkdtemp(prefix="usk-input-test-")
        self.addCleanup(shutil.rmtree, tmp, True)
        self.assertEqual(task.input_module(tmp), STANDALONE)                  # no project files: the Input Manager
        os.makedirs(os.path.join(tmp, "ProjectSettings"))
        settings = os.path.join(tmp, "ProjectSettings", "ProjectSettings.asset")
        for handler, module in ((0, STANDALONE), (2, STANDALONE)):
            with open(settings, "w", encoding="utf-8") as handle:
                handle.write("PlayerSettings:\n  activeInputHandler: %d\n" % handler)
            self.assertEqual(task.input_module(tmp), module)
        with open(settings, "w", encoding="utf-8") as handle:
            handle.write("PlayerSettings:\n  activeInputHandler: 1\n")
        with self.assertRaises(RuntimeError) as caught:                      # the package is missing from the project
            task.input_module(tmp)
        self.assertIn("com.unity.inputsystem", str(caught.exception))
        os.makedirs(os.path.join(tmp, "Packages"))
        with open(os.path.join(tmp, "Packages", "packages-lock.json"), "w", encoding="utf-8") as handle:
            json.dump({"dependencies": {"com.unity.inputsystem": {"version": "1.14.0"}}}, handle)
        self.assertEqual(task.input_module(tmp), INPUT_SYSTEM_UI)

    def test_the_handles_and_both_prompts_name_the_module_of_the_setting(self):
        for handler, module in INPUT_SETTINGS:
            other = INPUT_SYSTEM_UI if module == STANDALONE else STANDALONE
            with self.subTest(handler=handler):
                run = self.run_t2(handler)
                self.assertEqual(run.handles["inputModule"], module)
                task_vars = run.task.prompt_vars(run.build_ctx)
                self.assertEqual(task_vars["input_module"], module)
                variables = prompts.merge_vars({"R": run.scope.object_root, "A": run.scope.asset_root}, task_vars)
                for lang in prompts.LANGS:
                    with open(os.path.join(PACK_ROOT, "tasks", "T2", "prompt.%s.md" % lang), encoding="utf-8") as h:
                        text = prompts.render_requirements(prompts.parse_prompt(h.read()), variables)
                    self.assertIn("`%s`" % module, text)
                    self.assertNotIn(other, text)

    def test_reference_negatives_and_alternatives_in_every_setting(self):
        solutions = modules("T2")[2]
        for handler, module in INPUT_SETTINGS:
            for ids in ("legacy", "6000.4"):
                with self.subTest(handler=handler, ids=ids, case="reference"):
                    run = self.run_t2(handler, entity_mode=ids)
                    card = run.verify(run.solve(solutions.reference))
                    self.assertTrue(card.strict, _detail(card))
                    self.assertTrue(run.scenario["passed"], run.scenario)
                for negative in solutions.NEGATIVES:
                    with self.subTest(handler=handler, ids=ids, negative=negative.name):
                        run = self.run_t2(handler, entity_mode=ids)
                        card = run.verify(run.solve(negative.apply))
                        self.assertEqual(set(card.failed()), negative.expect_failed, _detail(card))
                for name, fn in solutions.ALTERNATIVES:
                    with self.subTest(handler=handler, ids=ids, alternative=name):
                        run = self.run_t2(handler, entity_mode=ids)
                        card = run.verify(run.solve(fn))
                        self.assertTrue(card.strict, _detail(card))

    def test_the_module_of_the_other_setting_fails(self):
        other = modules("T2")[2].other_input_module
        run = self.run_t2(1)                                   # Standalone on Input System only: D6, and it logs errors
        card = run.verify(run.solve(other))
        self.assertEqual(set(card.failed()), {"D6", "D14"}, _detail(card))
        self.assertEqual((run.scenario["passed"], run.scenario["error"]),
                         (False, "Runtime error logs were captured."))
        self.assertTrue(run.scenario["logWindow"]["entries"])
        for handler in (0, 2):                         # these settings do not ask for the Input System module
            with self.subTest(handler=handler):
                run = self.run_t2(handler)
                self.assertEqual(set(run.verify(run.solve(other)).failed()), {"D6"})

    def test_input_system_only_without_the_package_stops_the_build(self):
        with self.assertRaises(RuntimeError) as caught:
            self.run_t2(1, package=False)
        self.assertIn("com.unity.inputsystem is not installed", str(caught.exception))
        for handler in (0, 2):                                 # the package is only needed when it is the only input
            with self.subTest(handler=handler):
                run = self.run_t2(handler, package=False)
                self.assertTrue(run.verify(run.solve(modules("T2")[2].reference)).strict)

    def test_an_untouched_fixture_fails_the_same_assertions_in_every_setting(self):
        for handler, _module in INPUT_SETTINGS:
            with self.subTest(handler=handler):
                card = self.run_t2(handler).verify(None)
                self.assertEqual(set(card.failed()), UNTOUCHED["T2"], _detail(card))


TMP_LABEL, TEXT_LABEL = "TextMeshProUGUI", "UnityEngine.UI.Text"
# (Offline's `tmp`, the label component T2 asks for): uGUI 2.x ships TextMesh Pro, on 2022 it is the package
LABEL_SETTINGS = (("ugui", TMP_LABEL), ("package", TMP_LABEL), (None, TEXT_LABEL))


class T2LabelComponentTests(unittest.TestCase):
    """T2's button label is the text component the project's UI skills use: TextMeshProUGUI where the project carries
    TextMesh Pro (uGUI 2.x, or com.unity.textmeshpro on 2022), UnityEngine.UI.Text where it does not."""

    def run_t2(self, tmp, **kwargs):
        return Offline(self, "T2", pipeline="URP", tmp=tmp, **kwargs)

    @staticmethod
    def swap_labels(editor):
        """Gives every label the other component kind, keeping its text."""
        pairs = (("TextMeshProUGUI", "m_text", "Text", "m_Text"), ("Text", "m_Text", "TextMeshProUGUI", "m_text"))
        for obj in editor.walk():
            for old, old_field, new, new_field in pairs:
                if old in obj.fields:
                    text = obj.fields.pop(old)[old_field]
                    obj.components = [[new, True] if comp[0] == old else comp for comp in obj.components]
                    obj.fields[new] = {new_field: text}
                    break

    def test_the_handles_and_both_prompts_name_the_component_of_the_project(self):
        for tmp, component in LABEL_SETTINGS:
            other = TEXT_LABEL if component == TMP_LABEL else TMP_LABEL
            with self.subTest(tmp=tmp):
                run = self.run_t2(tmp)
                self.assertEqual(run.handles["labelComponent"], component)
                task_vars = run.task.prompt_vars(run.build_ctx)
                self.assertEqual(task_vars["label_component"], component)
                variables = prompts.merge_vars({"R": run.scope.object_root, "A": run.scope.asset_root}, task_vars)
                for lang in prompts.LANGS:
                    with open(os.path.join(PACK_ROOT, "tasks", "T2", "prompt.%s.md" % lang), encoding="utf-8") as h:
                        text = prompts.render_requirements(prompts.parse_prompt(h.read()), variables)
                    self.assertIn("`%s`" % component, text)
                    self.assertNotIn(other, text)

    def test_reference_negatives_and_alternatives_with_and_without_text_mesh_pro(self):
        solutions = modules("T2")[2]
        for tmp, component in LABEL_SETTINGS:
            for handler in (None, 1):                          # the label and the input module are independent
                for ids in ("legacy", "6000.4"):
                    with self.subTest(tmp=tmp, handler=handler, ids=ids, case="reference"):
                        run = self.run_t2(tmp, input_handler=handler, entity_mode=ids)
                        card = run.verify(run.solve(solutions.reference))
                        self.assertTrue(card.strict, _detail(card))
                        labels = [o for o in run.editor.walk() if run.editor.component(o, component.split(".")[-1])]
                        self.assertTrue(labels, component)
                    for negative in solutions.NEGATIVES:
                        with self.subTest(tmp=tmp, handler=handler, ids=ids, negative=negative.name):
                            run = self.run_t2(tmp, input_handler=handler, entity_mode=ids)
                            card = run.verify(run.solve(negative.apply))
                            self.assertEqual(set(card.failed()), negative.expect_failed, _detail(card))
                    for name, fn in solutions.ALTERNATIVES:
                        with self.subTest(tmp=tmp, handler=handler, ids=ids, alternative=name):
                            run = self.run_t2(tmp, input_handler=handler, entity_mode=ids)
                            card = run.verify(run.solve(fn))
                            self.assertTrue(card.strict, _detail(card))

    def test_the_other_component_does_not_count(self):
        """The prompt names the component: a label of the other kind with the right text fails D3 and nothing else."""
        solutions = modules("T2")[2]
        for tmp, _component in LABEL_SETTINGS:
            with self.subTest(tmp=tmp):
                run = self.run_t2(tmp)
                answer = run.solve(solutions.reference)
                self.swap_labels(run.editor)
                card = run.verify(answer)
                self.assertEqual(set(card.failed()), {"D3"}, _detail(card))

    def test_an_untouched_fixture_fails_the_same_assertions_with_and_without_text_mesh_pro(self):
        for tmp, _component in LABEL_SETTINGS:
            with self.subTest(tmp=tmp):
                card = self.run_t2(tmp).verify(None)
                self.assertEqual(set(card.failed()), UNTOUCHED["T2"], _detail(card))

# Producer:Betsy
