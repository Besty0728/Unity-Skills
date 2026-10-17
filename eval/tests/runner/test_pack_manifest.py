"""Manifest validation, plugin range and task selection (contract 5.1, 9)."""
import copy
import json
import unittest
from types import MappingProxyType

try:
    from .test_pack_support import BOM, GOOD, TempCase, load_json, pack, problems, read_bytes, store
except ImportError:
    from test_pack_support import BOM, GOOD, TempCase, load_json, pack, problems, read_bytes, store


def good_manifest():
    return load_json(GOOD / "manifest.json")


def code_of(exc):
    return exc.exception.problems[0]["code"]


def put(template):
    return lambda m: m["private"]["urlTemplates"].__setitem__(0, template)


DEFECTS = [
    ("schema missing", lambda m: m.pop("schema"), "schema:"),
    ("schema text", lambda m: m.update(schema="1"), "schema:"),
    ("schema bool", lambda m: m.update(schema=True), "schema:"),
    ("packId blank", lambda m: m.update(packId=" "), "packId:"),
    ("packVersion two parts", lambda m: m.update(packVersion="1.0"), "packVersion:"),
    ("plugin missing", lambda m: m.pop("plugin"), "plugin:"),
    ("plugin min text", lambda m: m["plugin"].update(min="x"), "plugin.min"),
    ("plugin min above max", lambda m: m["plugin"].update(min="3.0.0", max="2.0.0"), "min is greater than max"),
    ("unknown track", lambda m: m["tracks"].update(art={}), "unknown track"),
    ("unknown tier", lambda m: m["tracks"]["ops"].update(expert=[]), "unknown tier"),
    ("tier not a list", lambda m: m["tracks"]["ops"].update(basic="F01"), "expected a list"),
    ("id with a digit first", lambda m: m["tracks"]["ops"]["basic"].append("1bad"), "invalid task id"),
    ("id listed twice", lambda m: m["tracks"]["ops"]["standard"].append("F01"), "listed more than once"),
    ("listed without entry", lambda m: m["tracks"]["ops"]["standard"].append("F09"), "has no entry in tasks"),
    ("entry not listed", lambda m: m["tasks"].update(F09=copy.deepcopy(m["tasks"]["F01"])), "not listed in tracks"),
    ("track differs from position", lambda m: m["tasks"]["F01"].update(track="dev"), "differ from its tracks position"),
    ("tier differs from position", lambda m: m["tasks"]["F01"].update(tier="challenge"), "differ from its tracks"),
    ("caseVersion zero", lambda m: m["tasks"]["F01"].update(caseVersion=0), "caseVersion"),
    ("caseVersion bool", lambda m: m["tasks"]["F01"].update(caseVersion=True), "caseVersion"),
    ("caseVersion text", lambda m: m["tasks"]["F01"].update(caseVersion="2"), "caseVersion"),
    ("minutes zero", lambda m: m["tasks"]["F01"].update(minutes=0), "minutes"),
    ("minutes float", lambda m: m["tasks"]["F01"].update(minutes=7.5), "minutes"),
    ("seeds empty", lambda m: m["tasks"]["F01"].update(seeds=[]), "seeds"),
    ("seeds text", lambda m: m["tasks"]["F01"].update(seeds=["7001"]), "seeds"),
    ("seeds bool", lambda m: m["tasks"]["F01"].update(seeds=[True]), "seeds"),
    ("requires mode", lambda m: m["tasks"]["F01"]["requires"].update(mode="manual"), "requires.mode"),
    ("requires pipeline", lambda m: m["tasks"]["F01"]["requires"].update(pipeline="Standard"), "requires.pipeline"),
    ("requires pipeline list empty", lambda m: m["tasks"]["F01"]["requires"].update(pipeline=[]), "expected at least one"),
    ("requires pipeline duplicate", lambda m: m["tasks"]["F01"]["requires"].update(pipeline=["URP", "URP"]),
     "requires.pipeline[1]: duplicate pipeline URP"),
    ("requires pipeline unknown item", lambda m: m["tasks"]["F01"]["requires"].update(pipeline=["URP", "Standard"]),
     "requires.pipeline[1]: expected one of"),
    ("requires pipeline item not text", lambda m: m["tasks"]["F01"]["requires"].update(pipeline=["URP", 3]),
     "requires.pipeline[1]: expected one of"),
    ("requires pipeline wrong type", lambda m: m["tasks"]["F01"]["requires"].update(pipeline=3), "requires.pipeline:"),
    ("requires dedicated", lambda m: m["tasks"]["F01"]["requires"].update(dedicated="yes"), "requires.dedicated"),
    ("requires packages", lambda m: m["tasks"]["F01"]["requires"].update(packages=[1]), "requires.packages"),
    ("requires not an object", lambda m: m["tasks"]["F01"].update(requires=[]), "requires:"),
    ("public lacks task.py", lambda m: m["tasks"]["F01"]["public"].pop("task.py"), "missing task.py"),
    ("public lacks a prompt", lambda m: m["tasks"]["F01"]["public"].pop("prompt.en.md"), "missing prompt.en.md"),
    ("public name with slash", lambda m: m["tasks"]["F01"]["public"].update({"sub/x.py": "a" * 64}),
     "invalid file name"),
    ("public name with backslash", lambda m: m["tasks"]["F01"]["public"].update({"..\\x.py": "a" * 64}),
     "invalid file name"),
    ("public name starting with a dot", lambda m: m["tasks"]["F01"]["public"].update({".hidden": "a" * 64}),
     "invalid file name"),
    ("public hash upper case", lambda m: m["tasks"]["F01"]["public"].update({"task.py": "A" * 64}), "64 lowercase"),
    ("public hash short", lambda m: m["tasks"]["F01"]["public"].update({"task.py": "abc"}), "64 lowercase"),
    ("private file with a path", lambda m: m["tasks"]["F01"]["private"].update(file="../F01.py"), "private.file"),
    ("private file not python", lambda m: m["tasks"]["F01"]["private"].update(file="F01.txt"), "private.file"),
    ("private sha256 short", lambda m: m["tasks"]["F01"]["private"].update(sha256="abc"), "private.sha256"),
    ("private bytes zero", lambda m: m["tasks"]["F01"]["private"].update(bytes=0), "private.bytes"),
    ("private missing", lambda m: m["tasks"]["F01"].pop("private"), "private:"),
    ("template over http", put("http://example.com/{ref}/{file}"), "https"),
    ("template unknown placeholder", put("https://x.test/{branch}/{file}"), "unknown placeholder"),
    ("template without file", put("https://x.test/{ref}"), "missing {file}"),
    ("template with credentials", put("https://user:pw@x.test/{ref}/{file}"), "without placeholders or credentials"),
    ("template host placeholder", put("https://{ref}.x.test/{file}"), "without placeholders or credentials"),
    ("template stray brace", put("https://x.test/{ref}/{file}{"), "unbalanced braces"),
    ("template not text", lambda m: m["private"]["urlTemplates"].__setitem__(0, 5), "non-empty string"),
    ("refs not a list", lambda m: m["private"].update(refs="main"), "private.refs"),
    ("ref with dotdot", lambda m: m["private"]["refs"].append("../x"), "invalid ref"),
    ("ref with another placeholder", lambda m: m["private"]["refs"].append("{ref}"), "invalid ref"),
    ("ref with a space", lambda m: m["private"]["refs"].append("a b"), "invalid ref"),
]


class ManifestValidationTests(TempCase):
    def test_fixture_manifest_is_valid(self):
        self.assertEqual(pack.manifest_errors(good_manifest(), require_urls=True), [])

    def test_every_defect_is_reported_with_its_path(self):
        for label, edit, expected in DEFECTS:
            with self.subTest(label):
                manifest = good_manifest()
                edit(manifest)
                errors = pack.manifest_errors(manifest, require_urls=True)
                self.assertTrue(any(expected in error for error in errors), "%s -> %s" % (label, errors))

    def test_values_are_matched_whole_so_a_trailing_newline_is_rejected(self):
        for label, edit, expected in (
                ("packVersion", lambda m: m.update(packVersion="1.0.0\n"), "packVersion"),
                ("plugin", lambda m: m["plugin"].update(min="2.9.0\n"), "plugin.min"),
                ("public hash", lambda m: m["tasks"]["F01"]["public"].update({"task.py": "a" * 64 + "\n"}), "64 lowercase"),
                ("private hash", lambda m: m["tasks"]["F01"]["private"].update(sha256="a" * 64 + "\n"), "private.sha256"),
                ("private file", lambda m: m["tasks"]["F01"]["private"].update(file="F01.py\n"), "private.file"),
                ("ref", lambda m: m["private"]["refs"].append("main\n"), "invalid ref"),
                ("track listing", lambda m: m["tracks"]["ops"]["basic"].append("F03\n"), "invalid task id")):
            with self.subTest(label):
                manifest = good_manifest()
                edit(manifest)
                self.assertTrue(any(expected in e for e in pack.manifest_errors(manifest, require_urls=True)), label)
        self.assertFalse(pack.is_task_id("F01\n"))
        self.assertFalse(pack.is_semver("1.0.0\n"))

    def test_not_an_object(self):
        for value in ([], "x", 3, None):
            self.assertEqual(pack.manifest_errors(value), ["manifest: expected a JSON object"])

    def test_unknown_fields_are_ignored(self):
        manifest = good_manifest()
        manifest["notes"] = {"anything": [1, 2]}
        manifest["tasks"]["F01"]["flavour"] = "x"
        manifest["tasks"]["F01"]["private"]["extra"] = 1
        manifest["plugin"]["note"] = "x"
        self.assertEqual(pack.manifest_errors(manifest, require_urls=True), [])

    def test_empty_tiers_and_tasks_are_fine(self):
        manifest = good_manifest()
        manifest["tracks"] = {"ops": {}, "dev": {}}
        manifest["tasks"] = {}
        self.assertEqual(pack.manifest_errors(manifest), [])

    def test_requires_may_be_omitted(self):
        manifest = good_manifest()
        manifest["tasks"]["F01"].pop("requires")
        self.assertEqual(pack.manifest_errors(manifest), [])

    def test_require_urls(self):
        manifest = good_manifest()
        manifest.pop("private")
        self.assertEqual(pack.manifest_errors(manifest), [])
        self.assertTrue(any("urlTemplates and refs" in e for e in pack.manifest_errors(manifest, require_urls=True)))
        manifest["private"] = {"urlTemplates": [], "refs": []}
        self.assertEqual(pack.manifest_errors(manifest), [])
        self.assertTrue(any("must not be empty" in e for e in pack.manifest_errors(manifest, require_urls=True)))

    def test_all_errors_are_listed(self):
        manifest = good_manifest()
        manifest["packId"] = ""
        manifest["packVersion"] = "x"
        manifest["tasks"]["F01"]["minutes"] = 0
        self.assertEqual(len(pack.manifest_errors(manifest)), 3)


class PipelineRequirementTests(TempCase):
    """requires.pipeline: a name, null (any pipeline) or a non-empty list of distinct names in task order."""

    def manifest_with(self, value):
        manifest = good_manifest()
        manifest["tasks"]["F01"]["requires"]["pipeline"] = value
        return manifest

    def errors(self, value):
        return pack.manifest_errors(self.manifest_with(value), require_urls=True)

    def test_valid_forms(self):
        for value in ("URP", None, ["URP"], ["URP", "BuiltIn"], ["Custom", "HDRP", "URP", "BuiltIn"]):
            with self.subTest(value):
                self.assertEqual(self.errors(value), [])

    def test_every_name_is_accepted_alone_and_in_a_list(self):
        self.assertEqual(pack.PIPELINES, ("BuiltIn", "URP", "HDRP", "Custom"))
        for name in pack.PIPELINES:
            with self.subTest(name):
                self.assertEqual(self.errors(name), [])
                self.assertEqual(self.errors([name]), [])

    def test_the_key_may_be_missing(self):
        manifest = good_manifest()
        del manifest["tasks"]["F01"]["requires"]["pipeline"]
        self.assertEqual(pack.manifest_errors(manifest, require_urls=True), [])

    def test_invalid_forms_name_their_position(self):
        names = "BuiltIn|URP|HDRP|Custom"
        where = "tasks.F01.requires.pipeline"
        exact = (([], "%s: expected at least one of %s" % (where, names)),
                 (["URP", "URP"], "%s[1]: duplicate pipeline URP" % where),
                 (["URP", "BuiltIn", "URP"], "%s[2]: duplicate pipeline URP" % where),
                 (["URP", "Standard"], "%s[1]: expected one of %s, got 'Standard'" % (where, names)),
                 ([3], "%s[0]: expected one of %s, got 3" % (where, names)),
                 ("Standard", "%s: expected one of %s, a list of them or null, got 'Standard'" % (where, names)))
        for value, message in exact:
            with self.subTest(value):
                self.assertEqual(self.errors(value), [message])
        prefixed = ((["urp"], where + "[0]: "), ([None], where + "[0]: "), ([["URP"]], where + "[0]: "),
                    ("", where + ": "), (3, where + ": "), (True, where + ": "), ({}, where + ": "),
                    (("URP",), where + ": "))                       # a tuple is not JSON: manifests hold lists only
        for value, prefix in prefixed:
            with self.subTest(value):
                errors = self.errors(value)
                self.assertEqual(len(errors), 1, errors)
                self.assertTrue(errors[0].startswith(prefix), errors)

    def test_every_bad_item_is_reported(self):
        errors = self.errors(["URP", "URP", "Nope", 3, "BuiltIn", "BuiltIn"])
        self.assertEqual(len(errors), 4)
        for position, expected in ((1, "duplicate pipeline URP"), (2, "got 'Nope'"), (3, "got 3"),
                                   (5, "duplicate pipeline BuiltIn")):
            self.assertTrue(any("pipeline[%d]: " % position in e and expected in e for e in errors), (position, errors))

    def test_the_order_survives_parsing_and_meta(self):
        manifest = self.manifest_with(["BuiltIn", "URP"])
        parsed = pack.parse_manifest(json.dumps(manifest).encode("utf-8"), require_urls=True)
        self.assertEqual(parsed["tasks"]["F01"]["requires"]["pipeline"], ["BuiltIn", "URP"])
        opened = pack.Pack(str(GOOD), parsed, "0" * 64, "pack-dir", "pack-dir", None, False, [])
        meta = opened.meta("F01")
        self.assertEqual(meta["requires"]["pipeline"], ["BuiltIn", "URP"])
        meta["requires"]["pipeline"].append("HDRP")                       # the caller's copy, not the manifest
        self.assertEqual(parsed["tasks"]["F01"]["requires"]["pipeline"], ["BuiltIn", "URP"])
        self.assertEqual(opened.meta("F01")["requires"]["pipeline"], ["BuiltIn", "URP"])

    def test_the_runner_reads_every_form_the_manifest_allows(self):
        """session.allowed_pipelines is what check/start/next apply; it must understand each validated shape."""
        from usk_eval import session
        for value, allowed in (("URP", ["URP"]), (["URP", "BuiltIn"], ["URP", "BuiltIn"]), (["HDRP"], ["HDRP"]),
                               (None, None)):
            with self.subTest(value):
                self.assertEqual(self.errors(value), [])
                meta = pack.Pack(str(GOOD), self.manifest_with(value), "0" * 64, "pack-dir", "pack-dir", None, False,
                                 []).meta("F01")
                self.assertEqual(session.allowed_pipelines([meta["requires"]]), allowed)
        both = [{"pipeline": ["URP", "BuiltIn"]}, {"pipeline": "BuiltIn"}]               # several tasks: the intersection
        self.assertEqual(session.allowed_pipelines(both), ["BuiltIn"])

    def test_the_fixture_keeps_the_single_name_form(self):
        self.assertEqual(good_manifest()["tasks"]["F01"]["requires"]["pipeline"], "URP")


class NormalizeRequiresTests(unittest.TestCase):
    DEFAULTS = {"pipeline": "URP", "mode": "bypass", "dedicated": False, "packages": []}

    def test_defaults_fill_the_gaps(self):
        for value in (None, {}, {"unrelated": 1}):
            self.assertEqual({k: v for k, v in pack.normalize_requires(value).items() if k != "unrelated"},
                             self.DEFAULTS)
        self.assertEqual(pack.normalize_requires({"pipeline": "HDRP"}), dict(self.DEFAULTS, pipeline="HDRP"))
        self.assertEqual(pack.normalize_requires({"pipeline": None})["pipeline"], None)

    def test_any_mapping_is_read_not_only_dicts(self):
        read_only = MappingProxyType({"pipeline": ("HDRP", "URP"), "dedicated": True, "packages": ("a",)})
        self.assertEqual(pack.normalize_requires(read_only), {"pipeline": ["HDRP", "URP"], "mode": "bypass",
                                                              "dedicated": True, "packages": ["a"]})

    def test_what_is_not_a_mapping_means_the_defaults(self):
        for value in ([], "URP", 3, [("pipeline", "HDRP")]):
            self.assertEqual(pack.normalize_requires(value), self.DEFAULTS)

    def test_sequences_become_new_lists(self):
        source = {"pipeline": ("URP", "BuiltIn"), "packages": ("a", "b")}
        out = pack.normalize_requires(source)
        self.assertEqual((out["pipeline"], out["packages"]), (["URP", "BuiltIn"], ["a", "b"]))
        out["pipeline"].append("HDRP")
        out["packages"].append("c")
        self.assertEqual(source, {"pipeline": ("URP", "BuiltIn"), "packages": ("a", "b")})
        listed = {"pipeline": ["URP"], "packages": ["a"]}
        again = pack.normalize_requires(listed)
        self.assertIsNot(again["pipeline"], listed["pipeline"])
        self.assertIsNot(again["packages"], listed["packages"])

    def test_a_tuple_and_a_json_array_compare_equal(self):
        self.assertEqual(pack.normalize_requires({"pipeline": ("URP", "BuiltIn")}),
                         pack.normalize_requires({"pipeline": ["URP", "BuiltIn"]}))
        self.assertNotEqual(pack.normalize_requires({"pipeline": ["BuiltIn", "URP"]}),
                            pack.normalize_requires({"pipeline": ["URP", "BuiltIn"]}))
        self.assertNotEqual(pack.normalize_requires({"pipeline": "URP"}), pack.normalize_requires({"pipeline": ["URP"]}))

    def test_the_defaults_are_never_shared(self):
        pack.normalize_requires(None)["packages"].append("x")
        self.assertEqual(pack.normalize_requires(None)["packages"], [])
        self.assertEqual(pack.DEFAULT_REQUIRES["packages"], [])

    def test_the_task_api_default_means_the_same(self):
        """task_api.PublicTask.requires and the pack default must not drift apart."""
        from usk_eval.task_api import PublicTask
        self.assertEqual(pack.normalize_requires(PublicTask.requires), pack.normalize_requires(None))


class ParseManifestTests(TempCase):
    def test_bytes_with_and_without_bom(self):
        raw = read_bytes(GOOD / "manifest.json")
        self.assertEqual(pack.parse_manifest(raw), pack.parse_manifest(BOM + raw))

    def test_not_json(self):
        for raw in (b"{", b"", b"\xff\xfe", b"[1,2]", b"3"):
            with self.subTest(raw=raw), self.assertRaises(problems.ProblemError) as exc:
                pack.parse_manifest(raw)
            self.assertEqual(code_of(exc), "PACK_INVALID")

    def test_a_newer_schema_is_unsupported_not_invalid(self):
        manifest = good_manifest()
        manifest["schema"] = 2
        with self.assertRaises(problems.ProblemError) as exc:
            pack.parse_manifest(json.dumps(manifest).encode("utf-8"))
        self.assertEqual(code_of(exc), "SCHEMA_UNSUPPORTED")

    def test_invalid_names_the_first_errors_and_counts_the_rest(self):
        manifest = good_manifest()
        manifest.update(packId="", packVersion="x", plugin=5, tracks=[], tasks=[])
        with self.assertRaises(problems.ProblemError) as exc:
            pack.parse_manifest(json.dumps(manifest).encode("utf-8"), label="custom.json")
        message = exc.exception.problems[0]["message"]
        self.assertEqual(code_of(exc), "PACK_INVALID")
        self.assertIn("custom.json: packId:", message)
        self.assertIn("(+2 more)", message)

    def test_read_manifest_returns_the_hash_of_the_raw_bytes(self):
        manifest, digest = pack.read_manifest(str(GOOD / "manifest.json"), require_urls=True)
        self.assertEqual(digest, store.sha256_bytes(read_bytes(GOOD / "manifest.json")))
        self.assertEqual(manifest["packId"], "fixture-pack")

    def test_read_manifest_missing_and_unreadable(self):
        for target in (self.path("nothing.json"), self.root):
            with self.subTest(target=target), self.assertRaises(problems.ProblemError) as exc:
                pack.read_manifest(target)
            self.assertEqual(code_of(exc), "PACK_INVALID")


class TemplateTests(unittest.TestCase):
    def test_accepted(self):
        for template in ("https://h.test/{ref}/{file}", "https://h.test/a@{ref}/{file}?x=1", "https://h.test:8443/{file}"):
            self.assertIsNone(pack.template_error(template), template)

    def test_http_only_on_loopback_with_the_flag(self):
        self.assertIsNotNone(pack.template_error("http://127.0.0.1:80/{file}"))
        self.assertIsNone(pack.template_error("http://127.0.0.1:80/{file}", allow_loopback_http=True))
        self.assertIsNone(pack.template_error("http://localhost/{file}", allow_loopback_http=True))
        self.assertIsNotNone(pack.template_error("http://example.com/{file}", allow_loopback_http=True))

    def test_rejected(self):
        for template in ("", " ", None, 5, "ftp://h.test/{file}", "https:///{file}", "https://h.test/{x}/{file}",
                         "https://h.test:notaport/{file}", "file:///etc/{file}"):
            self.assertIsNotNone(pack.template_error(template), repr(template))


class VersionTests(unittest.TestCase):
    def test_semver(self):
        for text in ("1.0.0", "10.20.30", "1.0.0-beta.1", "1.0.0+build.5", "1.0.0-rc.1+b"):
            self.assertTrue(pack.is_semver(text), text)
        for value in ("1.0", "v1.0.0", "1.0.0.0", "", None, 3, "1.x.0"):
            self.assertFalse(pack.is_semver(value), repr(value))

    def test_tuple_ignores_suffixes_and_defaults_to_zero(self):
        self.assertEqual(pack.version_tuple("3.0.0-beta.2+x"), (3, 0, 0))
        self.assertEqual(pack.version_tuple("2.10.1"), (2, 10, 1))
        self.assertEqual(pack.version_tuple("junk"), (0, 0, 0))
        self.assertGreater(pack.version_tuple("2.10.0"), pack.version_tuple("2.9.9"))

    def test_task_ids(self):
        for value in ("S01", "D01", "T10", "O1", "A" * 16):
            self.assertTrue(pack.is_task_id(value), value)
        for value in ("1S", "S-1", "S_1", "", "A" * 17, "S 1", "S/1", None, 1):
            self.assertFalse(pack.is_task_id(value), repr(value))


class PluginRangeTests(unittest.TestCase):
    def manifest(self, low, high):
        return {"plugin": {"min": low, "max": high}}

    def test_bounds_are_inclusive(self):
        m = self.manifest("3.0.0", "3.2.0")
        for version, ok in (("2.9.9", False), ("3.0.0", True), ("3.1.5", True), ("3.2.0", True), ("3.2.1", False),
                            ("3.0.0-beta.1", True)):
            self.assertEqual(pack.plugin_in_range(m, version), ok, version)

    def test_open_ends(self):
        self.assertTrue(pack.plugin_in_range(self.manifest(None, None), "0.0.1"))
        self.assertTrue(pack.plugin_in_range(self.manifest("3.0.0", None), "99.0.0"))
        self.assertFalse(pack.plugin_in_range(self.manifest(None, "3.0.0"), "3.0.1"))

    def test_problem_shape(self):
        self.assertIsNone(pack.plugin_range_problem(self.manifest("3.0.0", None), "3.0.0"))
        found = pack.plugin_range_problem(self.manifest("3.0.0", None), "2.9.1")
        self.assertEqual(found["code"], "PACK_PLUGIN_RANGE")
        self.assertEqual(found["args"], ["3.0.0", "latest"])
        self.assertIn("3.0.0 to latest", found["fix"])
        self.assertEqual(pack.plugin_range_problem(self.manifest(None, "3.0.0"), "4.0.0")["args"], ["any", "3.0.0"])

    def test_unparseable_version_counts_as_zero(self):
        self.assertFalse(pack.plugin_in_range(self.manifest("1.0.0", None), None))


class SelectionTests(unittest.TestCase):
    def setUp(self):
        self.manifest = {"tracks": {"ops": {"basic": ["S01", "S03", "D01"], "standard": [], "challenge": ["O1"]},
                                    "dev": {"basic": ["X1"]}}}

    def test_default_pool_is_the_tier_in_order(self):
        self.assertEqual(pack.select_tasks(self.manifest, "ops", "basic"), ["S01", "S03", "D01"])
        self.assertEqual(pack.select_tasks(self.manifest, "ops", "basic", ""), ["S01", "S03", "D01"])
        self.assertEqual(pack.select_tasks(self.manifest, "ops", "basic", []), ["S01", "S03", "D01"])

    def test_the_pool_is_a_copy(self):
        pack.select_tasks(self.manifest, "ops", "basic").append("Z9")
        self.assertEqual(pack.tier_tasks(self.manifest, "ops", "basic"), ["S01", "S03", "D01"])

    def test_subset_keeps_the_requested_order(self):
        self.assertEqual(pack.select_tasks(self.manifest, "ops", "basic", "D01, S01"), ["D01", "S01"])
        self.assertEqual(pack.select_tasks(self.manifest, "ops", "basic", ["S03"]), ["S03"])

    def test_invalid_selections(self):
        cases = (("ops", "basic", "S01,Q9", "not in ops/basic: Q9"), ("ops", "basic", "S01,S01", "duplicate"),
                 ("ops", "basic", ",", "empty selection"), ("ops", "standard", None, "ops/standard has no tasks"),
                 ("dev", "challenge", None, "dev/challenge has no tasks"), ("ops", "basic", "X1", "not in ops/basic"))
        for track, tier, tasks, expected in cases:
            with self.subTest(tasks=tasks), self.assertRaises(problems.ProblemError) as exc:
                pack.select_tasks(self.manifest, track, tier, tasks)
            self.assertEqual(code_of(exc), "TASKS_INVALID")
            self.assertIn(expected, exc.exception.problems[0]["args"][0])

    def test_all_task_ids_follow_play_order(self):
        self.assertEqual(pack.all_task_ids(self.manifest), ["S01", "S03", "D01", "O1", "X1"])


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
