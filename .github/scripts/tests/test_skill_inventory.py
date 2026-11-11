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
    private const int ModuleEntryDefaultByteBudget = 8192;
    private static readonly Dictionary<string, int> ModuleEntryByteBudgetExceptions = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        // @@EXCEPTIONS@@
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
        f"{inv.PACKAGE_DIR}/SKILL.md": "---\nname: root\ndescription: r\n---\nSee [index](skills/INDEX.md).\n",
        f"{docs}/INDEX.md": "[go](./gameobject/MODULE.md#exact-signatures)\n",
        f"{docs}/gameobject/MODULE.md": ENTRY_MD,
        f"{docs}/gameobject/reference/foo_find.md": REF_FIND,
        f"{docs}/gameobject/reference/foo_batch.md": REF_BATCH,
        f"{docs}/gameobject/reference/foo_stub.md": REF_STUB,
        f"{docs}/architecture/MODULE.md": "### Not a skill\n",
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
        f.files[f"{inv.DOCS_DIR}/gameobject/MODULE.md"] = ENTRY_MD + "\n### foo_find\ndup\n"
        self.assertIn("duplicate-in-module", codes(f.audit(), "error"))

    def test_do_not_list_denying_a_real_skill(self):
        f = Fixture()
        f.files[f"{inv.DOCS_DIR}/gameobject/MODULE.md"] = ENTRY_MD.replace("`foo_move` does", "`foo_batch` does")
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
        f.files[f"{inv.DOCS_DIR}/gameobject/MODULE.md"] = ENTRY_MD + "\n`foo_batch` is `SemiAuto`; the rest are `FullAuto`.\n"
        self.assertIn("mode-doc-vs-code", codes(f.audit(), "error"))

    def test_mixed_clause_is_not_flagged(self):
        f = Fixture()
        f.files[f"{inv.DOCS_DIR}/gameobject/MODULE.md"] = ENTRY_MD + "\n`foo_find` is `SemiAuto`; `foo_batch` is `FullAuto`.\n"
        self.assertNotIn("mode-doc-vs-code", codes(f.audit()))

    def test_pilot_missing_reference_and_budget(self):
        f = Fixture()
        del f.files[f"{inv.DOCS_DIR}/gameobject/reference/foo_stub.md"]
        f.files[f"{inv.DOCS_DIR}/gameobject/MODULE.md"] = ENTRY_MD + "x" * 5000
        found = codes(f.audit(), "error")
        self.assertIn("pilot-reference-missing", found)
        self.assertIn("pilot-missing-skill", found)
        self.assertIn("pilot-budget", found)

    def test_module_with_no_category_and_not_advisory(self):
        f = Fixture()
        f.files[f"{inv.DOCS_DIR}/mystery/MODULE.md"] = "---\nname: m\ndescription: m\n---\n"
        self.assertIn("module-unknown", codes(f.audit(), "error"))

    def test_broken_link_and_anchor(self):
        f = Fixture()
        f.files[f"{inv.DOCS_DIR}/INDEX.md"] += "[a](./nope/MODULE.md) [b](./gameobject/MODULE.md#missing)\n"
        found = codes(f.audit(), "error")
        self.assertIn("link-missing-file", found)
        self.assertIn("link-missing-anchor", found)

    def test_root_doc_budget_uses_lf_normalised_bytes(self):
        f = Fixture()
        f.files[f"{inv.PACKAGE_DIR}/SKILL.md"] += "x" * inv.ROOT_DOC_BYTE_BUDGET
        self.assertIn("root-doc-budget", codes(f.audit(), "error"))

    def test_module_entry_over_default_budget_without_exception_is_error(self):
        f = Fixture()
        f.files[f"{inv.DOCS_DIR}/architecture/MODULE.md"] = "x" * 8200
        self.assertIn("entry-budget", codes(f.audit(), "error"))

    def test_module_entry_within_default_budget_passes(self):
        f = Fixture()
        f.files[f"{inv.DOCS_DIR}/architecture/MODULE.md"] = "x" * 8000
        self.assertNotIn("entry-budget", codes(f.audit()))

    def test_module_entry_exception_budget_is_enforced(self):
        def fixture(size: int) -> Fixture:
            f = Fixture()
            f.files[inv.TEST_FILE] = TEST_CS.replace("// @@EXCEPTIONS@@", '{ "architecture", 9000 },')
            f.files[f"{inv.DOCS_DIR}/architecture/MODULE.md"] = "x" * size
            return f
        self.assertNotIn("entry-budget", codes(fixture(8500).audit()))
        self.assertIn("entry-budget", codes(fixture(9100).audit(), "error"))

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


REF_FIND_DEFAULTS = """### foo_find
Find foo.

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `name` | string | No | null | the name |
| `instanceId` | int | No | 1 | id |
| `map` | json string | No | null | m |

**Returns**: `{success, count, instanceId, entityId}`
"""

REF_FIND_GROUP = """### foo_find
Find foo.

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `{group}` | string | No | {cell} | the names |
| `instanceId` | int | No | 0 | id |

**Returns**: `{success, count, instanceId, entityId}`
"""


class ParamDefaultNormalisationTests(unittest.TestCase):
    def test_doc_cells_normalise_to_one_canonical_value(self):
        cases = [
            ("0.0", ("num", "0")), ("0f", ("num", "0")), ("0", ("num", "0")), ("1.50", ("num", "1.5")),
            ("1.5", ("num", "1.5")), ("-1f", ("num", "-1")), ("-1", ("num", "-1")),
            ("`null`", ("null", "")), ("—", ("null", "")), ("–", ("null", "")), ("-", ("null", "")),
            ("", ("null", "")), ("NONE", ("null", "")), ("TRUE", ("bool", "true")), ("`false`", ("bool", "false")),
            ('`"local"`', ("str", "local")), ("`local`", ("str", "local")), ('""', ("str", "")),
        ]
        for raw, expected in cases:
            with self.subTest(raw=raw):
                self.assertEqual(inv.canonical_default(raw, False), expected)

    def test_code_literals_normalise_to_the_same_values(self):
        cases = [
            ("0f", ("num", "0")), ("1.50f", ("num", "1.5")), ("-1f", ("num", "-1")),
            ("null", ("null", "")), ("true", ("bool", "true")), ("False", ("bool", "false")),
            ('"local"', ("str", "local")), ('""', ("str", "")), ("string.Empty", ("str", "")),
            ("SkillMode.Fast", ("str", "Fast")),
        ]
        for raw, expected in cases:
            with self.subTest(raw=raw):
                self.assertEqual(inv.canonical_default(raw, True), expected)

    def test_undecidable_code_expressions_are_skipped(self):
        cases = {
            "default": "default 关键字", "default(float)": "default 关键字",
            "(float)0": "调用或括号表达式", "new Vector3(0, 0, 0)": "调用或括号表达式",
            "int.MaxValue": "常量引用（如 int.MaxValue）", "Vector3.zero": "常量引用（如 int.MaxValue）",
            "DefaultMode": "标识符常量", "1 << 3": "其它表达式",
        }
        for raw, reason in cases.items():
            with self.subTest(raw=raw):
                self.assertIsNone(inv.canonical_default(raw, True))
                self.assertEqual(inv.code_skip_reason(raw), reason)

    def test_doc_free_text_is_compared_as_text(self):
        self.assertEqual(inv.canonical_default("auto-detect", False), ("str", "auto-detect"))
        self.assertNotEqual(inv.canonical_default("0 (auto)", False), inv.canonical_default("0", False))

    def test_render_code_default_for_table_cells(self):
        cases = {"0f": "0", "-1": "-1", "null": "null", "true": "true", '"log"': '"log"',
                 'string.Empty': '""', "SkillMode.Fast": "Fast"}
        for raw, expected in cases.items():
            with self.subTest(raw=raw):
                self.assertEqual(inv.render_code_default(raw), expected)
        self.assertIsNone(inv.render_code_default("default"))


class ParamDefaultTests(unittest.TestCase):
    def test_drifting_default_is_error_with_location(self):
        f = Fixture()
        f.files[f"{inv.DOCS_DIR}/gameobject/reference/foo_find.md"] = REF_FIND_DEFAULTS
        audit = f.audit()
        found = [x for x in audit.findings if x.code == "param-default"]
        self.assertEqual(len(found), 1, [x.message for x in audit.findings])
        self.assertIn("foo_find", found[0].message)
        self.assertIn("instanceId", found[0].message)
        self.assertIn("reference/foo_find.md:7", found[0].where)

    def test_matching_defaults_pass(self):
        f = Fixture()
        f.files[f"{inv.DOCS_DIR}/gameobject/reference/foo_find.md"] = REF_FIND_DEFAULTS.replace("| 1 |", "| 0f |")
        self.assertNotIn("param-default", codes(f.audit(), "error"))

    def test_merged_row_with_consistent_defaults_compares_one_cell(self):
        f = Fixture()
        f.files[f"{inv.DOCS_DIR}/gameobject/reference/foo_find.md"] = REF_FIND_GROUP.replace("{group}", "name`, `map").replace("{cell}", "null")
        self.assertNotIn("param-default", codes(f.audit(), "error"))
        f.files[f"{inv.DOCS_DIR}/gameobject/reference/foo_find.md"] = REF_FIND_GROUP.replace("{group}", "name`, `map").replace("{cell}", "0")
        self.assertIn("param-default", codes(f.audit(), "error"))

    def test_merged_row_with_inconsistent_csharp_defaults_is_group_error(self):
        f = Fixture()
        f.files[inv.SKILLS_DIR + "/FooSkills.cs"] = FOO_SKILLS_CS.replace("int instanceId = 0,", "int instanceId = 5,")
        f.files[f"{inv.DOCS_DIR}/gameobject/reference/foo_find.md"] = REF_FIND_GROUP.replace(
            "{group}", "name`, `instanceId").replace("{cell}", "null").replace("| `instanceId` | int | No | 0 |", "| `instanceId` | int | No | 5 |")
        audit = f.audit()
        self.assertIn("param-default-group", codes(audit, "error"))
        self.assertNotIn("param-default", codes(audit, "error"))

    def test_per_member_cell_compares_each_member(self):
        sized = FOO_SKILLS_CS.replace('[SkillParam("the name, with a comma")] string name = null, int instanceId = 0,',
                                      "float sizeX = 10f, float sizeY = 1f,")
        def fixture(cell: str) -> Fixture:
            f = Fixture()
            f.files[inv.SKILLS_DIR + "/FooSkills.cs"] = sized
            f.files[f"{inv.DOCS_DIR}/gameobject/reference/foo_find.md"] = REF_FIND_GROUP.replace(
                "{group}", "sizeX`, `sizeY").replace("{cell}", cell).replace("| `instanceId` | int | No | 0 | id |\n", "")
            return f
        self.assertNotIn("param-default", codes(fixture("10, 1").audit(), "error"))
        found = codes(fixture("10, 9").audit(), "error")
        self.assertIn("param-default", found)
        self.assertNotIn("param-default-group", found)

    def test_per_member_cell_with_inconsistent_csharp_values_is_fine(self):
        sized = FOO_SKILLS_CS.replace('[SkillParam("the name, with a comma")] string name = null, int instanceId = 0,',
                                      "float sizeX = 10f, float sizeY = 1f,")
        f = Fixture()
        f.files[inv.SKILLS_DIR + "/FooSkills.cs"] = sized
        f.files[f"{inv.DOCS_DIR}/gameobject/reference/foo_find.md"] = REF_FIND_GROUP.replace(
            "{group}", "sizeX`, `sizeY").replace("{cell}", "10, 1").replace("| `instanceId` | int | No | 0 | id |\n", "")
        self.assertNotIn("param-default-group", codes(f.audit(), "error"))

    def test_descriptive_doc_text_stands_for_null(self):
        def fixture(cell: str) -> Fixture:
            f = Fixture()
            f.files[f"{inv.DOCS_DIR}/gameobject/reference/foo_find.md"] = REF_FIND_DEFAULTS.replace(
                "| `instanceId` | int | No | 1 |", "| `instanceId` | int | No | 0 |").replace(
                "| `name` | string | No | null |", f"| `name` | string | No | {cell} |")
            return f
        self.assertNotIn("param-default", codes(fixture("auto-detect").audit(), "error"))
        self.assertNotIn("param-default", codes(fixture("`prefab name`").audit(), "error"))
        self.assertIn("param-default", codes(fixture('"auto"').audit(), "error"))

    def test_instance_id_sentinel_zero_reads_as_null(self):
        def fixture(cell: str) -> Fixture:
            f = Fixture()
            f.files[f"{inv.DOCS_DIR}/gameobject/reference/foo_find.md"] = REF_FIND_DEFAULTS.replace(
                "| `instanceId` | int | No | 1 |", f"| `instanceId` | int | No | {cell} |")
            return f
        for cell in ("0", "-", "null"):
            self.assertNotIn("param-default", codes(fixture(cell).audit(), "error"), cell)
        self.assertIn("param-default", codes(fixture("1").audit(), "error"))

    def test_required_parameters_are_skipped(self):
        f = Fixture()
        f.files[inv.SKILLS_DIR + "/FooSkills.cs"] = FOO_SKILLS_CS.replace(
            'Tags = new[] { "foo" }, Outputs = new[] { "count", "entityId", "instanceId" },',
            'Tags = new[] { "foo" }, Outputs = new[] { "count", "entityId", "instanceId" }, RequiredParams = new[] { "instanceId" },')
        f.files[f"{inv.DOCS_DIR}/gameobject/reference/foo_find.md"] = REF_FIND_DEFAULTS
        self.assertNotIn("param-default", codes(f.audit(), "error"))

    def test_requires_input_parameters_are_skipped(self):
        f = Fixture()
        f.files[inv.SKILLS_DIR + "/FooSkills.cs"] = FOO_SKILLS_CS.replace(
            'Tags = new[] { "foo" }, Outputs = new[] { "count", "entityId", "instanceId" },',
            'Tags = new[] { "foo" }, Outputs = new[] { "count", "entityId", "instanceId" }, RequiresInput = new[] { "map" },')
        f.files[f"{inv.DOCS_DIR}/gameobject/reference/foo_find.md"] = REF_FIND_DEFAULTS.replace(
            "| `instanceId` | int | No | 1 |", "| `instanceId` | int | No | 0 |").replace(
            "| `map` | json string | No | null |", "| `map` | json string | No | 7 |")
        self.assertNotIn("param-default", codes(f.audit(), "error"))

    def test_sync_tables_rewrites_only_the_default_cell(self):
        f = Fixture()
        f.files[f"{inv.DOCS_DIR}/gameobject/reference/foo_find.md"] = REF_FIND_DEFAULTS
        with tempfile.TemporaryDirectory() as root:
            write_tree(root, f.files)
            audit = inv.Audit(root, run_frontmatter=False)
            audit.run()
            cells, files = inv.apply_table_fixes(audit.table_fixes)
            self.assertEqual((cells, files), (1, 1))
            with open(os.path.join(root, inv.DOCS_DIR, "gameobject/reference/foo_find.md"), encoding="utf-8", newline="") as fh:
                text = fh.read()
        self.assertEqual(text, REF_FIND_DEFAULTS.replace("| `instanceId` | int | No | 1 | id |",
                                                         "| `instanceId` | int | No | 0 | id |"))

    def test_sync_tables_keeps_padding_width(self):
        with tempfile.TemporaryDirectory() as root:
            path = os.path.join(root, "t.md")
            with open(path, "w", encoding="utf-8", newline="") as fh:
                fh.write("| `a` | int | No | 10 | d |\n")
            cells, files = inv.apply_table_fixes([(path, 1, 3, "2")])
            with open(path, encoding="utf-8", newline="") as fh:
                self.assertEqual(fh.read(), "| `a` | int | No | 2  | d |\n")
            self.assertEqual((cells, files), (1, 1))


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
