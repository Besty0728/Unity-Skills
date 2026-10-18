"""eval/tools/lint_pack.py: clean on the shipped pack, and each rule fails on a deliberately broken copy of it."""
import io
import os
import shutil
from unittest import mock

try:
    from .test_pack_support import TempCase, pack, read_text, save_json, write_text
except ImportError:
    from test_pack_support import TempCase, pack, read_text, save_json, write_text

import build_pack_manifest as builder  # noqa: E402
import lint_pack as lint               # noqa: E402

IGNORE = shutil.ignore_patterns("__pycache__", "*.pyc")
BASIC_TASKS = ("S01", "S03", "S04", "S06", "S08", "D01")


class LintCase(TempCase):
    """Writable copies of the manifest's tasks (public folders, private verifiers, solutions), so task folders still
    being written next to the shipped pack do not leak in; edits rebuild the manifest unless told not to, so only the
    rule under test can fail."""

    def setUp(self):
        TempCase.setUp(self)
        self.pack_root = self.path("pack")
        self.private_root = self.path("private")
        self.solutions_root = self.path("solutions")
        manifest = builder._read_previous(os.path.join(lint.DEFAULT_PACK_ROOT, "manifest.json"))
        for root in (self.private_root, self.solutions_root, os.path.join(self.pack_root, "tasks")):
            os.makedirs(root)
        shutil.copy2(os.path.join(lint.DEFAULT_PACK_ROOT, "manifest.json"), self.pack_root)
        for task_id, entry in manifest["tasks"].items():
            shutil.copytree(os.path.join(lint.DEFAULT_PACK_ROOT, "tasks", task_id),
                            os.path.join(self.pack_root, "tasks", task_id), ignore=IGNORE)
            shutil.copy2(os.path.join(lint.DEFAULT_PRIVATE_ROOT, entry["private"]["file"]), self.private_root)
            shutil.copy2(os.path.join(lint.DEFAULT_SOLUTIONS_ROOT, task_id + ".py"), self.solutions_root)

    def file(self, kind, task_id, name=None):
        if kind == "public":
            return os.path.join(self.pack_root, "tasks", task_id, name or "task.py")
        return os.path.join(self.private_root if kind == "private" else self.solutions_root, task_id + ".py")

    def edit(self, path, old, new, rebuild=True):
        text = read_text(path)
        self.assertIn(old, text, "fixture text not found in %s" % path)
        write_text(path, text.replace(old, new, 1), bom=path.endswith(".py"))
        if rebuild:
            manifest_path = os.path.join(self.pack_root, "manifest.json")
            previous = builder._read_previous(manifest_path)
            save_json(manifest_path, builder.build_manifest(self.pack_root, self.private_root, previous))

    def findings(self):
        return lint.lint(self.pack_root, self.private_root, self.solutions_root)

    def errors(self):
        return {(f.task, f.rule) for f in self.findings() if f.level == "error"}

    def assert_only(self, *expected):
        found = self.findings()
        errors = {(f.task, f.rule) for f in found if f.level == "error"}
        self.assertEqual(errors, set(expected), "\n".join(f.line() for f in found))


class ShippedPackTests(LintCase):
    def test_shipped_pack_has_no_errors(self):
        found = lint.lint()
        self.assertEqual([f.line() for f in found if f.level == "error"], [])

    def test_command_line_exit_codes(self):
        roots = ["--pack-root", self.pack_root, "--private-root", self.private_root, "--solutions-root",
                 self.solutions_root]
        out = io.StringIO()
        self.assertEqual(lint.main(roots, out=out), 0, out.getvalue())
        self.assertIn("lint_pack: OK", out.getvalue())
        out = io.StringIO()
        self.assertEqual(lint.main(["--pack-root", self.path("nowhere")], out=out), 2)
        self.edit(self.file("private", "S01"), '("A9", 0.5, False, ["R6", "G8"]', '("A9", 0.5, False, ["G8"]')
        out = io.StringIO()
        code = lint.main(roots, out=out)
        self.assertEqual(code, 1, out.getvalue())
        self.assertIn("S01 rule 3: R6 is not covered", out.getvalue())

    def test_copy_is_clean(self):
        self.assert_only()

    def test_every_task_is_linted(self):
        manifest = builder._read_previous(os.path.join(self.pack_root, "manifest.json"))
        wanted = pack.all_task_ids(manifest)
        for task_id in BASIC_TASKS:
            self.assertIn(task_id, wanted)
        linted = []
        real = lint.lint_task

        def spy(task_id, *args, **kwargs):
            linted.append(task_id)
            return real(task_id, *args, **kwargs)

        with mock.patch.object(lint, "lint_task", spy):
            lint.lint(self.pack_root, self.private_root, self.solutions_root)
        self.assertEqual(linted, wanted)


class RuleTests(LintCase):
    def test_rule_1_requirement_missing_from_prompts(self):
        self.edit(self.file("private", "S01"), '("A1", 1.5, False, ["R1"]', '("A1", 1.5, False, ["R9"]')
        self.assert_only(("S01", 1))

    def test_rule_1_assertion_without_requirements(self):
        self.edit(self.file("private", "S03"), '("B5", 1.0, False, ["R3"]', '("B5", 1.0, False, []')
        self.assert_only(("S03", 1), ("S03", 3))

    def test_rule_1_unknown_general_rule_and_hint(self):
        self.edit(self.file("private", "S04"), '["G4"], "no file', '["G9"], "no file')
        self.edit(self.file("private", "S04"), 'HINTS = {', 'HINTS = {\n    "C99": {"zh-CN": "x", "en": "x"},')
        found = [f.message for f in self.findings() if f.level == "error"]
        self.assertTrue(any("names G9" in m for m in found), found)
        self.assertTrue(any("unknown assertion C99" in m for m in found), found)
        self.assertEqual(self.errors(), {("S04", 1)})

    def test_rule_2_numeric_literal_differs(self):
        self.edit(self.file("public", "S01", "prompt.en.md"), "(0, 0.5, 0)", "(0, 0.6, 0)")
        self.assert_only(("S01", 2))

    def test_rule_2_placeholder_differs(self):
        self.edit(self.file("public", "S06", "prompt.en.md"), "save it in the folder `{dir}`",
                  "save it in the folder `{folder}`")
        self.assert_only(("S06", 2))

    def test_rule_2_requirement_numbers_differ(self):
        path = self.file("public", "D01", "prompt.en.md")
        text = read_text(path)
        self.edit(path, text[text.index("R4. "):], "")
        found = [f.message for f in self.findings() if f.level == "error"]
        self.assertTrue(any("requirement numbers differ" in m for m in found), found)
        self.assertIn(("D01", 2), self.errors())

    def test_rule_2_prompt_format(self):
        path = self.file("public", "S03", "prompt.zh-CN.md")
        self.edit(path, "R5. ", "## 答案\nR5. ")
        self.assertIn(("S03", 2), self.errors())
        write_text(path, read_text(path).replace("\n", "\r\n"), bom=True)
        messages = [f.message for f in self.findings() if f.rule == 2]
        self.assertTrue(any("BOM" in m for m in messages), messages)
        self.assertTrue(any("CR line endings" in m for m in messages), messages)

    def test_rule_2_schema_desc_must_be_bilingual(self):
        self.edit(self.file("public", "S01"), '"en": "full hierarchy path of Crate"}', '"en": ""}')
        self.assert_only(("S01", 2))

    def test_rule_3_uncovered_requirement(self):
        self.edit(self.file("private", "S01"), '("A9", 0.5, False, ["R6", "G8"]', '("A9", 0.5, False, ["G8"]')
        self.assert_only(("S01", 3))

    def test_rule_4_weights_must_sum_to_ten(self):
        self.edit(self.file("private", "S06"), '("D7", 0.5, False', '("D7", 1.0, False')
        self.assert_only(("S06", 4))

    def test_rule_4_critical_needs_a_prohibition(self):
        self.edit(self.file("private", "S01"), '("A1", 1.5, False, ["R1"]', '("A1", 1.5, True, ["R1"]')
        self.assert_only(("S01", 4))

    def test_rule_4_critical_on_g4_is_rejected(self):
        self.edit(self.file("private", "S03"), '("B9", 0.25, False, ["G4"]', '("B9", 0.25, True, ["G4"]')
        self.assert_only(("S03", 4))

    def test_rule_5_handle_key_blacklist(self):
        self.edit(self.file("public", "S01"), '"commonMaterial": common}', '"commonMaterial": common, '
                                                                          '"decoyPath": common}')
        self.assert_only(("S01", 5))

    def test_rule_5_read_only_task_keeps_c0_to_digests(self):
        self.edit(self.file("public", "S08"), 'return {"data": {},', 'return {"data": {"count": 8},')
        self.assert_only(("S08", 5))

    def test_rule_5_read_only_must_be_declared_in_both_languages(self):
        self.edit(self.file("public", "D01", "prompt.en.md"), "This is a read-only task: do not",
                  "Do not")
        self.assertIn(("D01", 5), self.errors())

    def test_rule_6_trap_comment(self):
        self.edit(self.file("public", "S01"), "TINT = (", "# the decoy material is tinted\nTINT = (")
        self.assert_only(("S01", 6))

    def test_rule_6_role_identifier(self):
        self.edit(self.file("public", "S04"), "spare = make(", "target = make(")
        self.edit(self.file("public", "S04"), "**address(spare))", "**address(target))")
        self.assert_only(("S04", 6))

    def test_rule_6_public_import(self):
        self.edit(self.file("public", "S06"), "from usk_eval import verifylib as V",
                  "from usk_eval import private, verifylib as V")
        self.assert_only(("S06", 6))

    def test_rule_6_private_writes_files(self):
        self.edit(self.file("private", "S06"), "def material_path(ctx):",
                  "def _note(path):\n    with open(path, \"w\") as handle:\n        handle.write(\"x\")\n\n\n"
                  "def material_path(ctx):")
        self.assert_only(("S06", 6))

    def test_rule_6_private_changes_the_filesystem(self):
        self.edit(self.file("private", "S08"), "def inventory(tree):",
                  "def _drop(path):\n    os.remove(path)\n\n\ndef inventory(tree):")
        self.assert_only(("S08", 6))

    def test_rule_6_private_may_read_files(self):
        self.edit(self.file("private", "S06"), "def material_path(ctx):",
                  "def _peek(path):\n    with open(path, \"rb\") as handle:\n        return handle.read()\n\n\n"
                  "def material_path(ctx):")
        self.assert_only()

    def test_rule_6_solutions_import_the_verifier(self):
        self.edit(self.file("solutions", "S08"), "import re\n", "import re\n\nimport S08  # noqa\n", rebuild=False)
        found = [f.message for f in self.findings() if f.level == "error"]
        self.assertTrue(any("use ctx.private" in m for m in found), found)

    def test_rule_7_too_few_negatives(self):
        path = self.file("solutions", "S04")
        text = read_text(path)
        start = text.index('    Negative("rename"')
        end = text.index("]\n\nALTERNATIVES")
        self.edit(path, text[start:end], "", rebuild=False)
        self.assert_only(("S04", 7))

    def test_rule_7_unknown_expected_failure(self):
        self.edit(self.file("solutions", "D01"), '{"F3"})', '{"F3", "F9"})', rebuild=False)
        self.assert_only(("D01", 7))

    def test_rule_7_alternatives_required(self):
        path = self.file("solutions", "S03")
        text = read_text(path)
        start = text.index("ALTERNATIVES = [")
        self.edit(path, text[start:text.index("\n", start)], "ALTERNATIVES = []", rebuild=False)
        self.assert_only(("S03", 7))

    def test_rule_7_warns_about_unexercised_assertions(self):
        self.edit(self.file("solutions", "S01"), '    Negative("rotated", rotated, {"A3"}),\n', "", rebuild=False)
        warnings = [f.message for f in self.findings() if f.level == "warning" and f.rule == 7]
        self.assertIn("no negative is expected to fail A3", warnings)
        self.assert_only()

    def test_rule_8_public_file_differs_from_manifest(self):
        path = self.file("public", "S03", "prompt.en.md")
        write_text(path, read_text(path) + "   One more continuation line.\n")
        self.assert_only((None, 8))

    def test_rule_8_private_file_differs_from_manifest(self):
        self.edit(self.file("private", "S08"), "PATTERN = ", "# note\nPATTERN = ", rebuild=False)
        self.assert_only((None, 8))


    def test_rule_9_task_calls_a_ui_skill_without_ugui(self):
        self.edit(self.file("public", "S01"), "        c, rng = ctx.c, ctx.rng\n",
                  "        c, rng = ctx.c, ctx.rng\n        c.write(\"ui_create_canvas\", name=\"Overlay\")\n")
        self.assert_only(("S01", 9))
        messages = [f.message for f in self.findings() if f.rule == 9]
        self.assertEqual(len(messages), 1, messages)
        self.assertIn("task.py calls ui_create_canvas", messages[0])
        self.assertIn("does not list com.unity.ugui", messages[0])

    def test_rule_9_solutions_call_a_ui_skill_without_ugui(self):
        self.edit(self.file("solutions", "S01"), "def reference(ctx):\n",
                  "def reference(ctx):\n    ctx.c.batch([{\"skill\": \"ui_create_button\", \"args\": {}}])\n",
                  rebuild=False)
        self.assert_only(("S01", 9))
        self.assertIn("solutions S01.py calls ui_create_button", [f.message for f in self.findings()
                                                                   if f.rule == 9][0])

    def test_rule_9_a_task_that_lists_ugui_may_call_ui_skills(self):
        self.assert_only()                                                    # T2, DB5, DC5 and OC5 call them
        self.edit(self.file("public", "T2"), '"packages": ["com.unity.ugui"]', '"packages": []')
        found = [f for f in self.findings() if f.level == "error"]
        self.assertEqual({(f.task, f.rule) for f in found}, {("T2", 9)})
        self.assertEqual(len(found), 2, [f.line() for f in found])            # its task.py and its solutions

    def test_rule_9_comments_and_prose_are_not_calls(self):
        self.edit(self.file("public", "S01"), "TINT = (", "# ui_create_text would add a label\nTINT = (")
        self.edit(self.file("solutions", "S01"), "def reference(ctx):\n",
                  "def reference(ctx):\n    note = \"calls ui_create_text later\"\n", rebuild=False)
        self.assert_only()

# Producer:Betsy
