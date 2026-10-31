#!/usr/bin/env python3
"""Tests for skill_inventory.py: the C#/doc parsers and the cross-checks, on a small fixture repo.

    python3 -m unittest discover -s .github/scripts/tests -t .github/scripts/tests
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile
import unittest

SCRIPTS = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, SCRIPTS)
import skill_inventory as inv  # noqa: E402

ATTRIBUTE_CS = """namespace UnitySkills
{
    public enum SkillCategory
    {
        Uncategorized = 0,
        GameObject,
        Scene
    }
}
"""

FOO_SKILLS_CS = """using System.Collections.Generic;
namespace UnitySkills
{
    public static class FooSkills
    {
        // [UnitySkill("commented_out", "x")] -- must not count
        [UnitySkill("foo_find", "Find foo, a \\"quoted\\" // not a comment. [UnitySkill(\\"inside_string\\")]",
            Category = SkillCategory.GameObject, Operation = SkillOperation.Query, // trailing, comment
            Tags = new[] { "foo" }, Outputs = new[] { "count", "entityId", "instanceId" },
            ReadOnly = true, Mode = SkillMode.SemiAuto)]
        public static object FooFind([SkillParam("the name, with a comma")] string name = null, int instanceId = 0,
            Dictionary<string, int> map = null)
        {
            if (name == "{") return new { error = "bad" };
            return new { success = true, count = 1, instanceId = 3, entityId = "x" };
        }

        [UnitySkill("foo_batch", "Batch foo", Category = SkillCategory.GameObject, Operation = SkillOperation.Modify,
            Tags = new[] { "foo" }, Outputs = new[] { "totalItems" }, TracksWorkflow = true)]
        public static object FooBatch(string items)
        {
            return BatchExecutor.Execute<FooItem>(items, item => new { ok = item.name }, item => item.name);
        }

        private class FooItem
        {
            public string name { get; set; }
            public float x, y = 2;
            public string @namespace { get; set; }
        }

#if FOO_PACKAGE
        [UnitySkill("foo_stub", "Real", Category = SkillCategory.GameObject, Operation = SkillOperation.Execute)]
        public static object FooStub() { return new { done = true }; }
#else
        [UnitySkill("foo_stub", "Stub", Category = SkillCategory.GameObject, Operation = SkillOperation.Execute)]
        public static object FooStubNone() { return new { error = "MISSING_PACKAGE" }; }
#endif
    }
}
"""

TEST_CS = """public class SkillDocumentationConsistencyTests
{
    private static readonly HashSet<string> AdvisoryModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "architecture",
        // "commented"
    };
    private static readonly Dictionary<string, SkillCategory[]> ModuleCategoryExceptions =
        new Dictionary<string, SkillCategory[]>(StringComparer.OrdinalIgnoreCase)
        {
        };
    private static readonly HashSet<string> KnownCrossModuleDuplicates = new HashSet<string>(StringComparer.Ordinal)
    {
    };
    private static readonly HashSet<string> KnownBrokenDocLinks = new HashSet<string>(StringComparer.Ordinal);
    private static readonly (string Module, Func<CodeSkill, bool> Owns)[] PilotModules =
    {
        ("gameobject", skill => skill.Attribute.Category == SkillCategory.GameObject),
    };
    private static readonly Dictionary<string, int> PilotEntryByteBudgets = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        { "gameobject", 4000 }
    };
}
"""

ENTRY_MD = """---
name: unity-gameobject
description: Foo things
---
# GameObject

## Skills Overview
`foo_find`, `foo_batch`, `foo_stub`

**DO NOT** (common hallucinations):
- `foo_move` does not exist -> use `foo_find`

## Exact Signatures
Use the schema.
"""

REF_FIND = """### foo_find
Find foo.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `name` | string | No | the name |
| `instanceId` | int | No | id |
| `map` | json string | No | m |

**Returns**: `{success, count, instanceId, entityId}`
"""

REF_BATCH = """### foo_batch
Batch.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `items` | json string | Yes | array |

**Item properties**: `name`, `x` (default 0), `y` (or `namespace`), each as in `foo_find`.
"""

REF_STUB = "### foo_stub\nStub.\n\n| Parameter | Type | Required | Description |\n|---|---|---|---|\n"


def base_files() -> dict[str, str]:
    docs = inv.DOCS_DIR
    return {
        f"{inv.SKILLS_DIR}/UnitySkillAttribute.cs": ATTRIBUTE_CS,
        f"{inv.SKILLS_DIR}/FooSkills.cs": FOO_SKILLS_CS,
        inv.TEST_FILE: TEST_CS,
        ".gitattributes": "*.md      text eol=lf\n",
        f"{inv.PACKAGE_DIR}/SKILL.md": "---\nname: root\ndescription: r\n---\nSee [index](skills/SKILL.md).\n",
        f"{docs}/SKILL.md": "---\nname: idx\ndescription: i\n---\n[go](./gameobject/SKILL.md#exact-signatures)\n",
        f"{docs}/gameobject/SKILL.md": ENTRY_MD,
        f"{docs}/gameobject/reference/foo_find.md": REF_FIND,
        f"{docs}/gameobject/reference/foo_batch.md": REF_BATCH,
        f"{docs}/gameobject/reference/foo_stub.md": REF_STUB,
        f"{docs}/architecture/SKILL.md": "---\nname: arch\ndescription: a\n---\n### Not a skill\n",
    }


def write_tree(root: str, files: dict[str, str]) -> None:
    for rel, text in files.items():
        path = os.path.join(root, rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            f.write(text)


class Fixture:
    def __init__(self, **overrides: str):
        self.files = base_files()
        self.files.update(overrides)

    def audit(self) -> inv.Audit:
        with tempfile.TemporaryDirectory() as root:
            write_tree(root, self.files)
            audit = inv.Audit(root, run_frontmatter=False)
            audit.run()
            audit.root = ""
            return audit


def codes(audit: inv.Audit, severity: str | None = None) -> list[str]:
    return [f.code for f in audit.findings if severity in (None, f.severity)]


class MaskingTests(unittest.TestCase):
    def test_comments_and_strings_do_not_hide_or_fake_attributes(self):
        nc, sk = inv.mask_source('a // [UnitySkill("x")]\n/* b */ "s // t" \'"\' @"v""q"')
        self.assertNotIn("UnitySkill", nc)
        self.assertEqual(len(nc), len(sk))
        self.assertIn('"s // t"', nc)
        self.assertNotIn("//", sk.split("\n")[1])

    def test_conditions_track_if_else(self):
        conds = inv.line_conditions("a\n#if X\nb\n#else\nc\n#endif\nd")
        self.assertEqual(conds[2], ["X"])
        self.assertEqual(conds[4], ["!(X)"])
        self.assertEqual(conds[6], [])


class CodeParserTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.audit = Fixture().audit()
        cls.skills = cls.audit.skills

    def test_unique_names_dedupe_stubs_and_ignore_comments(self):
        self.assertEqual(set(self.skills), {"foo_find", "foo_batch", "foo_stub"})
        self.assertEqual(self.audit.counts["attributes"], 4)
        self.assertEqual(self.skills["foo_stub"].attribute_count, 2)
        self.assertEqual(self.skills["foo_stub"].conditions, ["FOO_PACKAGE"])

    def test_attribute_metadata(self):
        s = self.skills["foo_find"]
        self.assertEqual((s.category, s.operation, s.mode), ("GameObject", ["Query"], "SemiAuto"))
        self.assertEqual(s.outputs, ["count", "entityId", "instanceId"])
        self.assertIn('"quoted"', s.description)
        self.assertFalse(self.skills["foo_batch"].flags["MutatesScene"])
        self.assertTrue(self.skills["foo_batch"].flags["TracksWorkflow"])

    def test_parameters_survive_attributes_generics_and_defaults(self):
        params = [(p.name, p.type, p.optional) for p in self.skills["foo_find"].params]
        self.assertEqual(params, [("name", "string", True), ("instanceId", "int", True),
                                  ("map", "Dictionary<string, int>", True)])

    def test_batch_item_class_with_multi_declarators_and_verbatim_names(self):
        self.assertEqual(set(self.skills["foo_batch"].item_props), {"name", "x", "y", "namespace"})

    def test_return_fields_ignore_error_branches(self):
        self.assertEqual(self.skills["foo_find"].return_fields, {"success", "count", "instanceId", "entityId"})
        self.assertIsNone(self.skills["foo_batch"].return_fields)

    def test_counts_exclude_uncategorized(self):
        self.assertEqual(self.audit.counts["cats"], 2)
        self.assertEqual((self.audit.counts["docs"], self.audit.counts["advisory"]), (2, 1))


class DocParserTests(unittest.TestCase):
    def parse(self, text: str):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "x.md")
            with open(path, "w", encoding="utf-8") as f:
                f.write(text)
            return inv.parse_doc_entries(path, "mod", "mod/x.md")

    def test_params_item_properties_and_returns(self):
        entry = self.parse(REF_FIND.replace("entityId}", "results: [{a, b}], warnings?}"))[0]
        self.assertEqual([n for n, _ in entry.params], ["name", "instanceId", "map"])
        self.assertEqual(entry.returns, ["success", "count", "instanceId", "results", "warnings"])

    def test_item_properties_stop_at_prose_and_keep_or_aliases(self):
        entry = self.parse(REF_BATCH)[0]
        self.assertEqual(entry.item_props, ["name", "x", "y", "namespace"])

    def test_item_properties_bullet_form(self):
        entry = self.parse("### s_batch\n**Item properties** (any):\n- Id: `a` / `b`\n- More: `c`\n\nText `zz`\n")[0]
        self.assertEqual(entry.item_props, ["a", "b", "c"])

    def test_headings_inside_code_fences_are_not_entries(self):
        self.assertEqual([e.name for e in self.parse("```\n### fake_skill\n```\n### real_skill\n")], ["real_skill"])

    def test_capitalised_headings_are_not_skills(self):
        self.assertEqual(self.parse("### Common Errors\n### 1. [2.9] thing\n"), [])


class CrossCheckTests(unittest.TestCase):
    def test_valid_fixture_has_no_errors(self):
        audit = Fixture().audit()
        self.assertEqual(codes(audit, "error"), [], [f.message for f in audit.findings])

    def test_ghost_skill(self):
        f = Fixture()
        f.files[f"{inv.DOCS_DIR}/gameobject/reference/foo_find.md"] += "\n### foo_ghost\nx\n"
        audit = f.audit()
        self.assertIn("ghost-skill", codes(audit, "error"))

    def test_doc_param_not_in_csharp_is_error_and_csharp_extra_is_warning(self):
        f = Fixture()
        f.files[f"{inv.DOCS_DIR}/gameobject/reference/foo_find.md"] = REF_FIND.replace("| `map` | json string | No | m |", "| `bogus` | int | No | b |")
        audit = f.audit()
        self.assertIn("param-doc-extra", codes(audit, "error"))
        self.assertIn("param-undocumented", codes(audit, "warning"))

    def test_batch_item_property_not_in_csharp(self):
        f = Fixture()
        f.files[f"{inv.DOCS_DIR}/gameobject/reference/foo_batch.md"] = REF_BATCH.replace("`y`", "`zzz`")
        self.assertIn("item-property-doc-extra", codes(f.audit(), "error"))

    def test_duplicate_definition_in_module(self):
        f = Fixture()
        f.files[f"{inv.DOCS_DIR}/gameobject/SKILL.md"] = ENTRY_MD + "\n### foo_find\ndup\n"
        self.assertIn("duplicate-in-module", codes(f.audit(), "error"))

    def test_do_not_list_denying_a_real_skill(self):
        f = Fixture()
        f.files[f"{inv.DOCS_DIR}/gameobject/SKILL.md"] = ENTRY_MD.replace("`foo_move` does", "`foo_batch` does")
        self.assertIn("do-not-real-skill", codes(f.audit(), "error"))

    def test_do_not_ignores_the_recommended_skill_right_of_the_arrow(self):
        self.assertNotIn("do-not-real-skill", codes(Fixture().audit()))

    def test_semi_auto_with_delete_is_a_conflict(self):
        f = Fixture()
        f.files[f"{inv.SKILLS_DIR}/FooSkills.cs"] = FOO_SKILLS_CS.replace(
            "SkillOperation.Query, // trailing, comment", "SkillOperation.Query | SkillOperation.Delete,")
        self.assertIn("mode-semantic-conflict", codes(f.audit(), "error"))

    def test_doc_claims_semi_auto_but_code_is_full_auto(self):
        f = Fixture()
        f.files[f"{inv.DOCS_DIR}/gameobject/SKILL.md"] = ENTRY_MD + "\n`foo_batch` is `SemiAuto`; the rest are `FullAuto`.\n"
        self.assertIn("mode-doc-vs-code", codes(f.audit(), "error"))

    def test_mixed_clause_is_not_flagged(self):
        f = Fixture()
        f.files[f"{inv.DOCS_DIR}/gameobject/SKILL.md"] = ENTRY_MD + "\n`foo_find` is `SemiAuto`; `foo_batch` is `FullAuto`.\n"
        self.assertNotIn("mode-doc-vs-code", codes(f.audit()))

    def test_pilot_missing_reference_and_budget(self):
        f = Fixture()
        del f.files[f"{inv.DOCS_DIR}/gameobject/reference/foo_stub.md"]
        f.files[f"{inv.DOCS_DIR}/gameobject/SKILL.md"] = ENTRY_MD + "x" * 5000
        found = codes(f.audit(), "error")
        self.assertIn("pilot-reference-missing", found)
        self.assertIn("pilot-missing-skill", found)
        self.assertIn("pilot-budget", found)

    def test_module_with_no_category_and_not_advisory(self):
        f = Fixture()
        f.files[f"{inv.DOCS_DIR}/mystery/SKILL.md"] = "---\nname: m\ndescription: m\n---\n"
        self.assertIn("module-unknown", codes(f.audit(), "error"))

    def test_broken_link_and_anchor(self):
        f = Fixture()
        f.files[f"{inv.DOCS_DIR}/SKILL.md"] += "[a](./nope/SKILL.md) [b](./gameobject/SKILL.md#missing)\n"
        found = codes(f.audit(), "error")
        self.assertIn("link-missing-file", found)
        self.assertIn("link-missing-anchor", found)

    def test_root_doc_budget_uses_lf_normalised_bytes(self):
        f = Fixture()
        f.files[f"{inv.PACKAGE_DIR}/SKILL.md"] += "x" * inv.ROOT_DOC_BYTE_BUDGET
        self.assertIn("root-doc-budget", codes(f.audit(), "error"))

    def test_missing_gitattributes(self):
        f = Fixture()
        del f.files[".gitattributes"]
        self.assertIn("gitattributes", codes(f.audit(), "error"))

    def test_returns_vs_return_direct_comparison(self):
        f = Fixture()
        f.files[f"{inv.DOCS_DIR}/gameobject/reference/foo_find.md"] = REF_FIND.replace("count, ", "total, ")
        self.assertIn("returns-vs-return", codes(f.audit(), "warning"))

    def test_empty_scan_is_an_error(self):
        with tempfile.TemporaryDirectory() as root:
            os.makedirs(os.path.join(root, inv.SKILLS_DIR))
            audit = inv.Audit(root, run_frontmatter=False)
            audit.run()
            self.assertIn("no-skills", codes(audit, "error"))


class CountSyncTests(unittest.TestCase):
    AGENTS = "-> 9 *Skills.cs / 8 SkillCategory / 7 skills`. 5 vs 8:\n"

    def run_sync(self, sync: bool):
        f = Fixture()
        f.files["AGENTS.md"] = self.AGENTS
        with tempfile.TemporaryDirectory() as root:
            write_tree(root, f.files)
            audit = inv.Audit(root, run_frontmatter=False)
            audit.run()
            drifts = audit.check_counts(sync)
            with open(os.path.join(root, "AGENTS.md"), encoding="utf-8") as fh:
                return audit, drifts, fh.read()

    def test_report_only_does_not_touch_files(self):
        audit, drifts, text = self.run_sync(False)
        self.assertEqual(text, self.AGENTS)
        self.assertEqual({(d["key"], d["actual"]) for d in drifts}, {("files", 1), ("cats", 2), ("total", 3)})

    def test_sync_rewrites_anchors(self):
        _, _, text = self.run_sync(True)
        self.assertEqual(text, "-> 1 *Skills.cs / 2 SkillCategory / 3 skills`. 1 vs 2:\n")


class CliTests(unittest.TestCase):
    def test_json_output_and_exit_code(self):
        f = Fixture()
        f.files[f"{inv.DOCS_DIR}/gameobject/reference/foo_find.md"] += "\n### foo_ghost\nx\n"
        with tempfile.TemporaryDirectory() as root:
            write_tree(root, f.files)
            env = dict(os.environ, PYTHONIOENCODING="utf-8")
            proc = subprocess.run([sys.executable, "-B", os.path.join(SCRIPTS, "skill_inventory.py"), root, "--json"],
                                  capture_output=True, text=True, encoding="utf-8", env=env)
            self.assertEqual(proc.returncode, 1, proc.stderr)
            data = json.loads(proc.stdout)
            self.assertEqual(data["counts"]["total"], 3)
            self.assertTrue(any(x["code"] == "ghost-skill" for x in data["findings"]))


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
