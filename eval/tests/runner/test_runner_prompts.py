"""prompts.py: verbatim prefix v2 and session prompts, G4 per track, requirement files, schema, commands (2.5, 5.3, 5.4, 6)."""
import os
import re
import unittest

from .runner_support import REPO
from usk_eval import prompts

CONTRACT = os.path.join(REPO, "eval", "docs", "design", "v3-contracts.md")
PLACEHOLDER_RE = re.compile(r"\{[A-Za-z_][A-Za-z0-9_]*\}")


def contract_blocks():
    with open(CONTRACT, "r", encoding="utf-8") as handle:
        return re.findall(r"```text\n(.*?)```", handle.read(), re.S)


BUILTINS = {"session": "s20261017T030000a1b2c3", "k": 2, "n": 6, "track": "ops", "tier": "basic",
            "project": "/work/Proj", "unity": "6000.3.9f1", "pipeline": "URP", "port": 8091,
            "instance": "Proj_0A1B2C3D", "R": "EV_r1", "A": "Assets/__UnitySkillsEval/r1",
            "scene": "Assets/__UnitySkillsEval/r1/Eval_r1.unity", "minutes": 10}


class TemplateParityTests(unittest.TestCase):
    @unittest.skipUnless(os.path.isfile(CONTRACT), "contract not in this checkout")
    def test_templates_match_the_contract_verbatim(self):
        blocks = contract_blocks()
        by_head = {b.split("\n", 1)[0]: b.rstrip("\n") for b in blocks}
        for template in (prompts.PREFIX_ZH, prompts.PREFIX_EN, prompts.SESSION_ZH_SUITE, prompts.SESSION_ZH_STRICT,
                         prompts.SESSION_EN_SUITE, prompts.SESSION_EN_STRICT):
            self.assertEqual(by_head.get(template.split("\n", 1)[0]), template)

    def test_g4_keeps_only_the_track_line(self):
        expected = {
            ("zh-CN", "ops"): "G4 场景和资产的修改只能通过 UnitySkills 技能完成：不要直接写 .unity/.prefab/.mat/.asset/.meta "
                              "文件，不要新建或修改 C# 脚本。",
            ("zh-CN", "dev"): "G4 可以在 A/Scripts 下新建或修改 C# 脚本；其他场景和资产修改只能通过 UnitySkills 技能完成。",
            ("en", "ops"): "G4 Make scene and asset changes only through UnitySkills skills: do not write "
                           ".unity/.prefab/.mat/.asset/.meta files directly; do not create or edit C# scripts.",
            ("en", "dev"): "G4 You may create or edit C# scripts under A/Scripts; make every other scene and asset "
                           "change through UnitySkills skills.",
        }
        for (lang, track), line in expected.items():
            text = prompts.prefix_template(lang, track)
            g4 = [l for l in text.split("\n") if l.startswith("G4")]
            self.assertEqual(g4, [line])
            self.assertNotIn("[ops]", text)
            self.assertNotIn("[dev]", text)

    def test_zh_and_en_render_the_same_structure(self):
        rendered = {}
        for lang in ("zh-CN", "en"):
            text = prompts.render_prompt(lang, "ops", BUILTINS, "R1. x {not a placeholder}", '{"a": <integer>}',
                                         "python3 -B run_eval.py submit --attempt 0a1b2c3d4e5f")
            rendered[lang] = text
            self.assertIn("R1. x {not a placeholder}", text)
            self.assertIn('{"a": <integer>}', text)
            self.assertIn("   python3 -B run_eval.py submit --attempt 0a1b2c3d4e5f", text)
            for value in ("s20261017T030000a1b2c3", "2/6", "/work/Proj", "8091", "Proj_0A1B2C3D", "`EV_r1`",
                          "`Assets/__UnitySkillsEval/r1`", "Eval_r1.unity", "10"):
                self.assertIn(value, text)
            self.assertEqual(PLACEHOLDER_RE.findall(text.replace("{not a placeholder}", "")), [])
        rules = {lang: [l[:2] for l in text.split("\n") if re.match(r"^G\d ", l)] for lang, text in rendered.items()}
        self.assertEqual(rules["zh-CN"], ["G%d" % i for i in range(1, 9)])
        self.assertEqual(rules["zh-CN"], rules["en"])
        self.assertEqual(rendered["zh-CN"].count("\n"), rendered["en"].count("\n"))

    def test_session_prompts(self):
        values = {"session": "s1", "n": 3, "track": "ops", "tier": "basic", "next_cmd": "python3 -B x next"}
        self.assertIn("整套模式", prompts.session_prompt("zh-CN", "suite", values))
        self.assertIn("逐题模式", prompts.session_prompt("zh-CN", "strict", values))
        self.assertIn("suite mode", prompts.session_prompt("en", "suite", values))
        self.assertIn("strict mode", prompts.session_prompt("ru", "strict", values))       # ru falls back to en
        for lang in ("zh-CN", "en"):
            for mode in ("suite", "strict"):
                self.assertIn("python3 -B x next", prompts.session_prompt(lang, mode, values))


class RequirementTests(unittest.TestCase):
    def test_parse_and_render(self):
        text = ("\ufeff<!-- usk-eval prompt; task S01; lang zh-CN -->\nR1. 在 `{props}` 下新建 `Crate`。\n"
                "   以 3 个空格缩进的续行属于上一条需求。\n\nR2. 正则 `[0-9]{{2}}`，路径 `{A}`。\n")
        lines = prompts.parse_prompt(text)
        self.assertEqual(len(lines), 3)
        merged = prompts.merge_vars(BUILTINS, {"props": "EV_r1/Props"})
        self.assertEqual(prompts.render_requirements(lines, merged),
                         "R1. 在 `EV_r1/Props` 下新建 `Crate`。\n   以 3 个空格缩进的续行属于上一条需求。\n"
                         "R2. 正则 `[0-9]{2}`，路径 `Assets/__UnitySkillsEval/r1`。")

    def test_bad_prompt_files(self):
        for text in ("", "Intro\nR1. x", "R1. a\nR3. b", "R2. a"):
            with self.assertRaises(ValueError):
                prompts.parse_prompt(text)
        with self.assertRaises(ValueError):
            prompts.render_requirements(["R1. {missing}"], BUILTINS)
        with self.assertRaises(ValueError):
            prompts.merge_vars(BUILTINS, {"R": "override"})
        with self.assertRaises(ValueError):
            prompts.merge_vars(BUILTINS, {"flag": True})


class SchemaTests(unittest.TestCase):
    SCHEMA = {"type": "object", "required": ["objectPath", "objectInstanceId", "materialPath"], "properties": {
        "objectPath": {"type": "string", "desc": {"zh-CN": "Crate 的完整层级路径", "en": "full hierarchy path of Crate"}},
        "objectInstanceId": {"type": "integer"},
        "materialPath": {"type": "string", "desc": {"zh-CN": "材质资产路径", "en": "material asset path"}}}}

    def test_contract_example_rendering(self):
        full = prompts.with_declared(self.SCHEMA)
        self.assertEqual(prompts.render_schema(full, "zh-CN"),
                         '{"objectPath": "<string: Crate 的完整层级路径>", "objectInstanceId": <integer>, '
                         '"materialPath": "<string: 材质资产路径>", "declaredComplete": <boolean>}')
        self.assertIn('"<string: material asset path>"', prompts.render_schema(full, "en"))
        nested = {"type": "object", "properties": {"items": {"type": "array", "items": {
            "type": "object", "properties": {"n": {"type": "number", "desc": "count"}}}}}}
        self.assertEqual(prompts.render_schema(nested, "en"), '{"items": [{"n": <number: count>}, ...]}')

    def test_answer_validation_is_strict_on_integers(self):
        full = prompts.with_declared(self.SCHEMA)
        ok = {"objectPath": "R/Crate", "objectInstanceId": -5, "materialPath": "A/m.mat", "declaredComplete": True}
        self.assertEqual(prompts.validate_answer(full, ok), [])
        bad = {"objectPath": 3, "objectInstanceId": True, "materialPath": "x", "declaredComplete": "yes"}
        errors = prompts.validate_answer(full, bad)
        self.assertEqual(len(errors), 3)
        self.assertTrue(any("objectInstanceId: expected integer, got boolean" in e for e in errors))
        self.assertEqual(prompts.validate_answer(full, {"objectInstanceId": 1.5, "declaredComplete": False}),
                         ["answer: missing required key objectPath", "answer: missing required key materialPath",
                          "objectInstanceId: expected integer, got number"])


class CommandTests(unittest.TestCase):
    def test_quoting(self):
        self.assertEqual(prompts.quote("/a/b-c_d.py", "darwin"), "/a/b-c_d.py")
        self.assertEqual(prompts.quote("/My Projects/p", "darwin"), '"/My Projects/p"')
        self.assertEqual(prompts.quote("a;rm", "linux"), '"a;rm"')
        self.assertEqual(prompts.quote('say "hi"', "linux"), '"say \\"hi\\""')
        self.assertEqual(prompts.quote("C:\\Unity\\Proj\\", "win32"), "C:\\Unity\\Proj")
        self.assertEqual(prompts.quote("C:\\My Proj\\", "win32"), '"C:\\My Proj"')
        self.assertEqual(prompts.quote("", "linux"), '""')

    def test_next_step_uses_entry_override(self):
        step = prompts.next_step("next", ["--project", "/p", "--session", "s1"], {"USK_EVAL_ENTRY": "/x/unity_eval.py"})
        self.assertEqual(step["command"], "next")
        self.assertEqual(step["argv"][1:], ["-B", "/x/unity_eval.py", "next", "--project", "/p", "--session", "s1"])
        self.assertTrue(step["line"].endswith("-B /x/unity_eval.py next --project /p --session s1"))


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
