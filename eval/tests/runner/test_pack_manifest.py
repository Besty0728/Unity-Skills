"""Manifest validation, plugin range and task selection (contract 5.1, 9)."""
import copy
import json
import unittest

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
