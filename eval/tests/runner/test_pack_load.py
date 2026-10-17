"""open_pack: sources, integrity labels, task modules and their manifest cross-checks (contract 5.1, 5.2, 5.9)."""
import os
import sys
import tempfile
import unittest
import zipfile
from unittest import mock

try:
    from .test_pack_support import (GOOD, TempCase, load_json, make_zip, pack, private, problems, read_bytes, read_text,
                                    save_json, store, write_bytes, write_text, edit_json)
except ImportError:
    from test_pack_support import (GOOD, TempCase, load_json, make_zip, pack, private, problems, read_bytes, read_text,
                                   save_json, store, write_bytes, write_text, edit_json)


def code_of(exc):
    return exc.exception.problems[0]["code"]


def message_of(exc):
    return exc.exception.problems[0]["message"]


class OpenPackTests(TempCase):
    def test_pack_dir_is_a_dev_source_and_never_official(self):
        opened = pack.open_pack(pack_dir=str(GOOD))
        self.assertEqual((opened.id, opened.version), ("fixture-pack", "1.0.0"))
        self.assertEqual((opened.source, opened.private_kind), ("pack-dir", "pack-dir"))
        self.assertEqual(opened.private_path, str(GOOD))
        self.assertFalse(opened.official)
        self.assertEqual(opened.modified, [])
        self.assertEqual(opened.manifest_sha256, store.sha256_bytes(read_bytes(GOOD / "manifest.json")))
        self.assertEqual(opened.root, str(GOOD))

    def test_package_source_is_official_when_nothing_differs(self):
        opened = pack.open_pack(base_dir=str(GOOD))
        self.assertEqual((opened.source, opened.private_kind, opened.private_path), ("package", "url", None))
        self.assertTrue(opened.official)
        self.assertEqual(opened.modified, [])

    def test_private_zip_source(self):
        archive = make_zip(self.path("p.zip"), {"F01.py": b"x"})
        opened = pack.open_pack(private_zip=archive, base_dir=str(GOOD))
        self.assertEqual((opened.source, opened.private_kind, opened.private_path), ("zip", "zip", archive))
        self.assertTrue(opened.official)

    def test_private_zip_is_not_opened_until_a_verifier_is_fetched(self):
        opened = pack.open_pack(private_zip=self.path("does-not-exist.zip"), base_dir=str(GOOD))
        self.assertEqual(opened.private_kind, "zip")

    def test_private_dir_source_matching_the_manifest_is_official(self):
        opened = pack.open_pack(private_dir=str(GOOD / "private"), base_dir=str(GOOD))
        self.assertEqual((opened.source, opened.private_kind), ("package", "private-dir"))
        self.assertEqual(opened.private_path, str(GOOD / "private"))
        self.assertTrue(opened.official)
        self.assertEqual(opened.modified, [])

    def test_private_dir_with_a_differing_file_is_unofficial_and_listed(self):
        root = self.copy_pack()
        write_text(os.path.join(root, "private", "F02.py"), "ASSERTIONS = []\n")
        opened = pack.open_pack(private_dir=os.path.join(root, "private"), base_dir=root)
        self.assertEqual(opened.modified, ["private/F02.py"])
        self.assertFalse(opened.official)

    def test_private_dir_may_lack_files(self):
        root = self.copy_pack()
        os.remove(os.path.join(root, "private", "F02.py"))
        opened = pack.open_pack(private_dir=os.path.join(root, "private"), base_dir=root)
        self.assertEqual(opened.modified, [])
        self.assertTrue(opened.official)

    def test_sources_are_mutually_exclusive(self):
        cases = ({"pack_dir": str(GOOD), "private_zip": "x.zip"}, {"pack_dir": str(GOOD), "private_dir": str(GOOD)},
                 {"private_zip": "x.zip", "private_dir": str(GOOD)})
        for kwargs in cases:
            with self.subTest(kwargs=sorted(kwargs)), self.assertRaises(problems.ProblemError) as exc:
                pack.open_pack(**kwargs)
            self.assertEqual(code_of(exc), "BAD_ARGUMENTS")
            self.assertIn("mutually exclusive", exc.exception.problems[0]["args"][0])

    def test_directories_must_exist(self):
        for kwargs in ({"pack_dir": self.path("nope")}, {"private_dir": self.path("nope")},
                       {"pack_dir": str(GOOD / "manifest.json")}):
            with self.subTest(kwargs=kwargs), self.assertRaises(problems.ProblemError) as exc:
                pack.open_pack(**kwargs)
            self.assertEqual(code_of(exc), "BAD_ARGUMENTS")
            self.assertIn("not a directory", exc.exception.problems[0]["args"][0])

    def test_relative_paths_become_absolute(self):
        try:
            relative = os.path.relpath(str(GOOD / "private"))
        except ValueError:
            self.skipTest("no relative path to the fixture on this drive")
        opened = pack.open_pack(private_dir=relative, base_dir=str(GOOD))
        self.assertTrue(os.path.isabs(opened.private_path))
        self.assertEqual(os.path.realpath(opened.private_path), os.path.realpath(str(GOOD / "private")))

    def test_missing_or_broken_manifest_is_pack_invalid(self):
        empty = self.path("empty")
        os.makedirs(empty)
        broken = self.copy_pack("broken")
        write_text(os.path.join(broken, "manifest.json"), "{")
        for kwargs in ({"base_dir": empty}, {"pack_dir": empty}, {"base_dir": broken}):
            with self.subTest(kwargs=kwargs), self.assertRaises(problems.ProblemError) as exc:
                pack.open_pack(**kwargs)
            self.assertEqual(code_of(exc), "PACK_INVALID")

    def test_url_source_needs_url_templates_but_dev_sources_do_not(self):
        root = self.copy_pack()
        edit_json(os.path.join(root, "manifest.json"), lambda m: m.pop("private"))
        with self.assertRaises(problems.ProblemError) as exc:
            pack.open_pack(base_dir=root)
        self.assertEqual(code_of(exc), "PACK_INVALID")
        self.assertIn("urlTemplates", message_of(exc))
        self.assertEqual(pack.open_pack(pack_dir=root).private_kind, "pack-dir")
        self.assertEqual(pack.open_pack(private_dir=os.path.join(root, "private"), base_dir=root).modified, [])

    def test_a_newer_manifest_schema_is_reported_as_such(self):
        root = self.copy_pack()
        edit_json(os.path.join(root, "manifest.json"), lambda m: m.update(schema=2))
        with self.assertRaises(problems.ProblemError) as exc:
            pack.open_pack(base_dir=root)
        self.assertEqual(code_of(exc), "SCHEMA_UNSUPPORTED")

    def test_consumer_contract(self):
        """Attributes and methods session.py and attempt.py rely on."""
        opened = pack.open_pack(pack_dir=str(GOOD))
        for name in ("id", "version", "source", "official", "modified", "manifest", "manifest_sha256",
                     "private_kind", "private_path", "root"):
            self.assertTrue(hasattr(opened, name), name)
        self.assertIsInstance(opened.modified, list)
        self.assertTrue(os.path.isdir(opened.task_dir("F01")))
        self.assertEqual(opened.load_task("F01").id, "F01")


class IntegrityTests(TempCase):
    def test_edited_public_file_is_listed_and_makes_the_pack_unofficial(self):
        root = self.copy_pack()
        with open(os.path.join(root, "tasks", "F01", "prompt.en.md"), "a", encoding="utf-8") as handle:
            handle.write("R3. One more.\n")
        opened = pack.open_pack(base_dir=root)
        self.assertEqual(opened.modified, ["F01/prompt.en.md"])
        self.assertFalse(opened.official)

    def test_every_listed_public_file_is_hashed(self):
        for name in pack.PUBLIC_REQUIRED:
            with self.subTest(name):
                root = self.copy_pack("p-" + name)
                path = os.path.join(root, "tasks", "F02", name)
                write_bytes(path, read_bytes(path) + b"\n")
                self.assertEqual(pack.open_pack(base_dir=root).modified, ["F02/" + name])

    def test_a_changed_byte_is_enough_and_line_endings_count(self):
        root = self.copy_pack()
        path = os.path.join(root, "tasks", "F01", "task.py")
        write_bytes(path, read_bytes(path).replace(b"\n", b"\r\n"))
        self.assertEqual(pack.open_pack(base_dir=root).modified, ["F01/task.py"])

    def test_unlisted_files_count_but_noise_does_not(self):
        root = self.copy_pack()
        folder = os.path.join(root, "tasks", "F01")
        for noise in (".DS_Store", "task.pyc", ".hidden", "Thumbs.db", "desktop.ini", "THUMBS.DB"):
            write_bytes(os.path.join(folder, noise), b"x")
        write_bytes(os.path.join(folder, "__pycache__", "task.cpython-313.pyc"), b"x")
        self.assertEqual(pack.open_pack(base_dir=root).modified, [])
        write_bytes(os.path.join(folder, "notes.txt"), b"x")
        os.makedirs(os.path.join(folder, "sub"))
        self.assertEqual(pack.open_pack(base_dir=root).modified, ["F01/notes.txt", "F01/sub"])

    def test_modified_labels_are_sorted_and_combined(self):
        root = self.copy_pack()
        write_bytes(os.path.join(root, "tasks", "F02", "task.py"), b"x")
        write_bytes(os.path.join(root, "tasks", "F01", "task.py"), b"x")
        write_bytes(os.path.join(root, "tasks", "F01", "a.txt"), b"x")
        self.assertEqual(pack.open_pack(base_dir=root).modified, ["F01/a.txt", "F01/task.py", "F02/task.py"])

    def test_a_missing_public_file_is_pack_invalid(self):
        root = self.copy_pack()
        os.remove(os.path.join(root, "tasks", "F02", "prompt.zh-CN.md"))
        with self.assertRaises(problems.ProblemError) as exc:
            pack.open_pack(base_dir=root)
        self.assertEqual(code_of(exc), "PACK_INVALID")
        self.assertIn("tasks/F02/prompt.zh-CN.md is missing", message_of(exc))

    def test_pack_dir_reports_modified_files_too(self):
        root = self.copy_pack()
        write_bytes(os.path.join(root, "tasks", "F01", "task.py"), b"x")
        opened = pack.open_pack(pack_dir=root)
        self.assertEqual(opened.modified, ["F01/task.py"])
        self.assertFalse(opened.official)


class PackApiTests(TempCase):
    def setUp(self):
        TempCase.setUp(self)
        self.pack = pack.open_pack(pack_dir=str(GOOD))

    def test_task_ids_and_selection(self):
        self.assertEqual(self.pack.task_ids(), ["F01", "F02"])
        self.assertEqual(self.pack.select("ops", "basic"), ["F01"])
        self.assertEqual(self.pack.select("dev", "standard", "F02"), ["F02"])
        with self.assertRaises(problems.ProblemError):
            self.pack.select("dev", "basic")

    def test_meta_fills_requires_defaults(self):
        meta = self.pack.meta("F01")
        self.assertEqual(meta["id"], "F01")
        self.assertEqual(meta["requires"], {"pipeline": "URP", "mode": "bypass", "dedicated": False, "packages": []})
        self.assertEqual(self.pack.meta("F02")["requires"]["packages"], ["com.unity.cinemachine"])
        self.assertEqual(meta["caseVersion"], 2)
        self.assertNotIn("id", self.pack.manifest["tasks"]["F01"])

    def test_meta_defaults_apply_when_the_manifest_omits_requires(self):
        root = self.copy_pack()
        edit_json(os.path.join(root, "manifest.json"), lambda m: m["tasks"]["F01"].pop("requires"))
        meta = pack.open_pack(pack_dir=root).meta("F01")
        self.assertEqual(meta["requires"], pack.normalize_requires(None))

    def test_meta_is_independent_of_the_manifest(self):
        meta = self.pack.meta("F01")
        meta["seeds"].append(1)
        meta["public"]["extra.txt"] = "x"
        meta["private"]["file"] = "changed"
        meta["requires"]["packages"].append("p")
        entry = self.pack.manifest["tasks"]["F01"]
        self.assertEqual(entry["seeds"], [7001])
        self.assertNotIn("extra.txt", entry["public"])
        self.assertEqual(entry["private"]["file"], "F01.py")
        self.assertEqual(entry["requires"]["packages"], [])

    def test_the_runner_calls_match_the_signatures(self):
        """session.py / attempt.py / cli.py call these by keyword."""
        import inspect
        fetch, probe = inspect.signature(private.fetch).parameters, inspect.signature(private.probe).parameters
        for name in ("plugin_version", "user_agent", "private_zip", "private_dir"):
            self.assertIn(name, fetch)
            self.assertIn(name, probe)
        self.assertEqual(list(inspect.signature(private.loaded).parameters), ["fetched", "attempt_id"])
        self.assertEqual([p.name for p in inspect.signature(private.cleanup_stale).parameters.values()
                          if p.default is inspect.Parameter.empty], [])
        self.assertEqual([p.name for p in inspect.signature(pack.open_pack).parameters.values()
                          if p.default is inspect.Parameter.empty], [])
        for name in ("pack_dir", "private_zip", "private_dir"):
            self.assertIn(name, inspect.signature(pack.open_pack).parameters)

    def test_paths(self):
        self.assertEqual(self.pack.task_dir("F01"), os.path.join(str(GOOD), "tasks", "F01"))
        self.assertEqual(self.pack.prompt_path("F02", "zh-CN"), os.path.join(str(GOOD), "tasks", "F02", "prompt.zh-CN.md"))
        self.assertTrue(os.path.isfile(self.pack.prompt_path("F01", "en")))

    def test_private_entry_is_a_copy(self):
        entry = self.pack.private_entry("F01")
        self.assertEqual(sorted(entry), ["bytes", "file", "sha256"])
        entry["file"] = "changed"
        self.assertEqual(self.pack.private_entry("F01")["file"], "F01.py")

    def test_unknown_or_hostile_task_ids_are_rejected(self):
        for task_id in ("F09", "../F01", "", None, "F01/.."):
            for call in (self.pack.task_dir, self.pack.meta, self.pack.private_entry, self.pack.load_task,
                         lambda t: self.pack.prompt_path(t, "en")):
                with self.subTest(task_id=task_id), self.assertRaises(problems.ProblemError) as exc:
                    call(task_id)
                self.assertEqual(code_of(exc), "TASKS_INVALID")

    def test_plugin_range(self):
        self.assertEqual(self.pack.plugin_range, ("2.9.0", None))
        self.assertIsNone(self.pack.plugin_range_problem("2.9.1"))
        self.assertEqual(self.pack.plugin_range_problem("2.8.0")["code"], "PACK_PLUGIN_RANGE")


class LoadTaskTests(TempCase):
    def edit_task(self, root, task_id, old, new):
        path = os.path.join(root, "tasks", task_id, "task.py")
        text = read_text(path)
        self.assertIn(old, text)
        write_bytes(path, text.replace(old, new).encode("utf-8"))

    def load(self, root, task_id="F01"):
        return pack.open_pack(base_dir=root).load_task(task_id)

    def test_loads_a_public_task_under_a_stable_module_name(self):
        opened = pack.open_pack(pack_dir=str(GOOD))
        task = opened.load_task("F01")
        self.assertIsInstance(task, sys.modules["usk_eval.task_api"].PublicTask)
        self.assertEqual((task.id, task.track, task.tier, task.case_version, task.minutes),
                         ("F01", "ops", "basic", 2, 5))
        self.assertEqual(type(task).__module__, "usk_eval_task_F01")
        self.assertIs(sys.modules["usk_eval_task_F01"].TASK, task)
        self.assertEqual(task.answer_schema()["required"], ["name"])
        self.assertIs(opened.load_task("F01"), task)           # cached per pack
        self.assertEqual(opened.load_task("F02").requires["dedicated"], True)

    def test_each_pack_gets_its_own_module(self):
        first_root, second_root = self.copy_pack("first"), self.copy_pack("second")
        self.edit_task(second_root, "F01", "TASK = F01()", 'WHICH = "second"\nTASK = F01()')
        first = self.load(first_root)
        module_first = sys.modules["usk_eval_task_F01"]
        second = self.load(second_root)
        module_second = sys.modules["usk_eval_task_F01"]
        self.assertIsNot(module_first, module_second)
        self.assertFalse(hasattr(module_first, "WHICH"))
        self.assertEqual(module_second.WHICH, "second")
        self.assertIsNot(type(first), type(second))
        self.assertEqual(first.build.__globals__["__file__"], os.path.join(first_root, "tasks", "F01", "task.py"))
        self.assertEqual(second.build.__globals__["__file__"], os.path.join(second_root, "tasks", "F01", "task.py"))

    def test_no_bytecode_is_written_even_when_python_would_write_it(self):
        root = self.copy_pack()
        with mock.patch.object(sys, "dont_write_bytecode", False):
            opened = pack.open_pack(base_dir=root)
            opened.load_task("F01")
            opened.load_task("F02")
        found = [dirpath for dirpath, dirs, _files in os.walk(root) if "__pycache__" in dirs]
        self.assertEqual(found, [])

    def test_a_stale_bytecode_cache_is_not_trusted(self):
        root = self.copy_pack()
        folder = os.path.join(root, "tasks", "F01")
        stale = os.path.join(folder, "__pycache__", "task.cpython-%d%d.pyc" % sys.version_info[:2])
        write_bytes(stale, b"not bytecode at all")
        task = self.load(root)
        self.assertEqual(task.id, "F01")
        self.assertEqual(read_bytes(stale), b"not bytecode at all")

    def test_paths_with_spaces_and_non_ascii_letters(self):
        root = os.path.join(self.root, "pack dir \u00e9\u4e2d")
        import shutil
        shutil.copytree(str(GOOD), root)
        opened = pack.open_pack(pack_dir=root)
        self.assertEqual(opened.load_task("F02").id, "F02")
        self.assertEqual(opened.modified, [])
        self.assertTrue(os.path.isfile(opened.prompt_path("F01", "zh-CN")))

    def test_attribute_mismatches_are_pack_invalid(self):
        cases = (("case_version = 2", "case_version = 3", "case_version is 3 but the manifest says 2"),
                 ("minutes = 5", "minutes = 6", "minutes is 6"),
                 ('track = "ops"', 'track = "dev"', "track is 'dev'"),
                 ('tier = "basic"', 'tier = "standard"', "tier is 'standard'"),
                 ('id = "F01"', 'id = "F09"', "id is 'F09'"),
                 ('"pipeline": "URP"', '"pipeline": "HDRP"', "requires differs"),
                 ("case_version = 2", "case_version = True", "case_version is True"),
                 ("minutes = 5", "minutes = 5.0", "minutes is 5.0"))
        for old, new, expected in cases:
            with self.subTest(new):
                root = self.copy_pack("m-" + str(abs(hash(new))))
                self.edit_task(root, "F01", old, new)
                with self.assertRaises(problems.ProblemError) as exc:
                    self.load(root)
                self.assertEqual(code_of(exc), "PACK_INVALID")
                self.assertIn(expected, message_of(exc))
                self.assertIn("task F01", message_of(exc))

    def test_omitted_requires_equals_the_defaults(self):
        root = self.copy_pack()
        self.edit_task(root, "F01", '    requires = {"pipeline": "URP", "mode": "bypass", "dedicated": False, '
                                    '"packages": []}\n', "")
        self.assertEqual(self.load(root).id, "F01")

    def test_modules_that_are_not_complete_tasks(self):
        cases = (("TASK = F01()", "TASK = object()", "no TASK instance"),
                 ("TASK = F01()", "", "no TASK instance"),
                 ("    def snapshot(self, ctx):\n        return {\"data\": {}, \"digests\": {}}\n", "",
                  "snapshot() is not implemented"),
                 ("    def prompt_vars(self, ctx):\n        return {\"props\": ctx.path(\"Props\")}\n", "",
                  "prompt_vars() is not implemented"),
                 ("class F01(PublicTask):", "class F01(object):", "no TASK instance"))
        for old, new, expected in cases:
            with self.subTest(expected + new):
                root = self.copy_pack("c-" + str(abs(hash(old + new))))
                self.edit_task(root, "F01", old, new)
                with self.assertRaises(problems.ProblemError) as exc:
                    self.load(root)
                self.assertEqual(code_of(exc), "PACK_INVALID")
                self.assertIn(expected, message_of(exc))

    def test_modules_that_fail_to_import(self):
        cases = (("def broken(:\n", "SyntaxError"), ("raise RuntimeError('boom')\n", "RuntimeError: boom"),
                 ("raise SystemExit(0)\n", "SystemExit"), ("import module_that_does_not_exist\n", "ModuleNotFoundError"),
                 ("\x00\n", "null bytes"))
        for line, expected in cases:
            with self.subTest(expected):
                root = self.copy_pack("f-" + expected.replace(" ", "-"))
                self.edit_task(root, "F01", "TASK = F01()", line + "TASK = F01()")
                before = sys.modules.get("usk_eval_task_F01")
                with self.assertRaises(problems.ProblemError) as exc:
                    self.load(root)
                self.assertEqual(code_of(exc), "PACK_INVALID")
                self.assertIn(expected, message_of(exc))
                self.assertIs(sys.modules.get("usk_eval_task_F01"), before)    # a failed load leaves nothing behind

    def test_a_syntax_error_names_its_line(self):
        root = self.copy_pack()
        self.edit_task(root, "F01", "TASK = F01()", "def broken(:\n")
        with self.assertRaises(problems.ProblemError) as exc:
            self.load(root)
        self.assertRegex(message_of(exc), r"SyntaxError: .*\(line \d+\)")

    def test_the_bom_is_accepted(self):
        path = GOOD / "tasks" / "F01" / "task.py"
        self.assertTrue(read_bytes(path).startswith(b"\xef\xbb\xbf"))
        self.assertEqual(pack.load_task_file("F01", str(path)).id, "F01")

    def test_load_task_file_is_usable_without_a_manifest(self):
        task = pack.load_task_file("F02", str(GOOD / "tasks" / "F02" / "task.py"))
        self.assertEqual((task.track, task.tier), ("dev", "standard"))

    def test_load_source_module_replaces_and_restores_sys_modules(self):
        path = self.path("m.py")
        write_text(path, "VALUE = 1\n")
        module = pack.load_source_module("usk_eval_test_module_a", path)
        self.addCleanup(sys.modules.pop, "usk_eval_test_module_a", None)
        self.assertEqual(module.VALUE, 1)
        self.assertIs(sys.modules["usk_eval_test_module_a"], module)
        write_text(path, "raise ValueError('x')\n")
        with self.assertRaises(ValueError):
            pack.load_source_module("usk_eval_test_module_a", path)
        self.assertIs(sys.modules["usk_eval_test_module_a"], module)    # the earlier module is back
        with self.assertRaises(ImportError):
            pack.load_source_module("usk_eval_test_module_b", self.path("m.txt"))

    def test_load_source_module_runs_the_given_bytes_not_the_file(self):
        path = self.path("m.py")
        write_text(path, "VALUE = 'file'\n")
        module = pack.load_source_module("usk_eval_test_module_c", path, b"VALUE = 'bytes'\n")
        self.addCleanup(sys.modules.pop, "usk_eval_test_module_c", None)
        self.assertEqual(module.VALUE, "bytes")
        self.assertEqual(module.__file__, path)


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
