"""eval/tools/build_pack_manifest.py: deterministic manifests built from the files on disk (contract 5.1, 9)."""
import io
import json
import os
import shutil
import unittest

try:
    from .test_pack_support import (GOOD, REPO, TempCase, edit_json, load_json, pack, read_bytes, read_text, save_json,
                                    write_bytes, write_text)
except ImportError:
    from test_pack_support import (GOOD, REPO, TempCase, edit_json, load_json, pack, read_bytes, read_text, save_json,
                                   write_bytes, write_text)

import build_pack_manifest as builder  # noqa: E402

FRESH = ["--pack-id", "fixture-pack", "--pack-version", "1.0.0", "--plugin-min", "2.9.0", "--plugin-max", "none"]


def run_builder(*args):
    out = io.StringIO()
    code = builder.main(list(args), out=out)
    return code, out.getvalue()


class BuilderCase(TempCase):
    def manifest_path(self, root):
        return os.path.join(root, "manifest.json")

    def clone_task(self, root, source, target):
        """tasks/<source> and private/<source>.py copied under another id."""
        shutil.copytree(os.path.join(root, "tasks", source), os.path.join(root, "tasks", target))
        for name in os.listdir(os.path.join(root, "tasks", target)):
            path = os.path.join(root, "tasks", target, name)
            write_bytes(path, read_bytes(path).replace(source.encode(), target.encode()))
        write_bytes(os.path.join(root, "private", target + ".py"),
                    read_bytes(os.path.join(root, "private", source + ".py")).replace(source.encode(), target.encode()))

    def edit_task(self, root, task_id, old, new):
        path = os.path.join(root, "tasks", task_id, "task.py")
        text = read_text(path)
        self.assertIn(old, text)
        write_bytes(path, b"\xef\xbb\xbf" + text.replace(old, new).encode("utf-8"))


class DeterminismTests(BuilderCase):
    def test_the_committed_fixture_manifest_is_current(self):
        code, text = run_builder("--pack-dir", str(GOOD), "--check")
        self.assertEqual(code, 0, text)
        self.assertIn("is up to date (2 tasks)", text)

    def test_building_from_nothing_reproduces_the_committed_manifest(self):
        root = self.copy_pack()
        os.remove(self.manifest_path(root))
        code, text = run_builder("--pack-dir", root, *FRESH)
        self.assertEqual(code, 0, text)
        self.assertEqual(read_bytes(self.manifest_path(root)), read_bytes(GOOD / "manifest.json"))

    def test_a_second_run_changes_nothing(self):
        root = self.copy_pack()
        before = read_bytes(self.manifest_path(root))
        code, text = run_builder("--pack-dir", root)
        self.assertEqual(code, 0)
        self.assertIn("unchanged", text)
        self.assertEqual(read_bytes(self.manifest_path(root)), before)

    def test_output_does_not_depend_on_where_the_pack_lives(self):
        first, second = self.copy_pack("first"), self.copy_pack("a longer name \u00e9")
        for root in (first, second):
            os.remove(self.manifest_path(root))
            self.assertEqual(run_builder("--pack-dir", root, *FRESH)[0], 0)
        self.assertEqual(read_bytes(self.manifest_path(first)), read_bytes(self.manifest_path(second)))

    def test_file_format(self):
        raw = read_bytes(GOOD / "manifest.json")
        self.assertFalse(raw.startswith(b"\xef\xbb\xbf"))
        self.assertTrue(raw.startswith(b'{\n "schema": 1,\n "packId"'))
        self.assertTrue(raw.endswith(b"}\n"))
        self.assertNotIn(b"\r", raw)
        keys = list(json.loads(raw.decode("utf-8")))
        self.assertEqual(keys, ["schema", "packId", "packVersion", "plugin", "tracks", "tasks", "private"])
        entry = json.loads(raw.decode("utf-8"))["tasks"]["F01"]
        self.assertEqual(list(entry), ["track", "tier", "caseVersion", "minutes", "seeds", "requires", "public", "private"])
        self.assertEqual(list(entry["public"]), ["task.py", "prompt.zh-CN.md", "prompt.en.md"])
        self.assertEqual(list(entry["requires"]), ["pipeline", "mode", "dedicated", "packages"])

    def test_the_result_passes_the_runner_validation(self):
        manifest = load_json(GOOD / "manifest.json")
        self.assertEqual(pack.manifest_errors(manifest, require_urls=True), [])
        opened = pack.open_pack(pack_dir=str(GOOD))
        self.assertEqual(opened.modified, [])
        for task_id in opened.task_ids():
            opened.load_task(task_id)

    def test_hashes_are_of_the_raw_bytes(self):
        manifest = load_json(GOOD / "manifest.json")
        task = manifest["tasks"]["F01"]
        import hashlib
        self.assertEqual(task["public"]["task.py"],
                         hashlib.sha256(read_bytes(GOOD / "tasks" / "F01" / "task.py")).hexdigest())
        private = read_bytes(GOOD / "private" / "F01.py")
        self.assertEqual((task["private"]["sha256"], task["private"]["bytes"]),
                         (hashlib.sha256(private).hexdigest(), len(private)))


class RebuildTests(BuilderCase):
    def test_an_edit_is_stale_until_the_manifest_is_rebuilt(self):
        root = self.copy_pack()
        with open(os.path.join(root, "tasks", "F01", "prompt.en.md"), "a", encoding="utf-8") as handle:
            handle.write("R3. More.\n")
        code, text = run_builder("--pack-dir", root, "--check")
        self.assertEqual(code, 1)
        self.assertIn("stale: F01: prompt.en.md changed", text)
        code, text = run_builder("--pack-dir", root)
        self.assertEqual(code, 0)
        self.assertIn("F01: prompt.en.md changed", text)
        self.assertEqual(run_builder("--pack-dir", root, "--check")[0], 0)
        self.assertEqual(pack.open_pack(pack_dir=root).modified, [])

    def test_a_private_edit_changes_hash_and_size(self):
        root = self.copy_pack()
        write_text(os.path.join(root, "private", "F02.py"), "ASSERTIONS = []\n")
        code, text = run_builder("--pack-dir", root)
        self.assertEqual(code, 0)
        self.assertIn("F02: private F02.py changed", text)
        entry = load_json(self.manifest_path(root))["tasks"]["F02"]["private"]
        self.assertEqual(entry["bytes"], len("ASSERTIONS = []\n"))

    def test_task_attributes_come_from_task_py(self):
        root = self.copy_pack()
        self.edit_task(root, "F01", "minutes = 5", "minutes = 9")
        self.edit_task(root, "F01", "case_version = 2", "case_version = 3")
        self.edit_task(root, "F02", '"dedicated": True', '"dedicated": False')
        self.assertEqual(run_builder("--pack-dir", root)[0], 0)
        manifest = load_json(self.manifest_path(root))
        self.assertEqual((manifest["tasks"]["F01"]["minutes"], manifest["tasks"]["F01"]["caseVersion"]), (9, 3))
        self.assertFalse(manifest["tasks"]["F02"]["requires"]["dedicated"])
        pack.open_pack(pack_dir=root).load_task("F01")

    def test_a_new_task_is_added_after_the_existing_ones_with_a_free_seed(self):
        root = self.copy_pack()
        self.clone_task(root, "F01", "F03")
        code, text = run_builder("--pack-dir", root)
        self.assertEqual(code, 0, text)
        self.assertIn("F03: added", text)
        manifest = load_json(self.manifest_path(root))
        self.assertEqual(manifest["tracks"]["ops"]["basic"], ["F01", "F03"])
        self.assertEqual(manifest["tasks"]["F03"]["seeds"], [7203])           # 7001 and 7102 are taken
        self.assertEqual(manifest["tasks"]["F01"]["seeds"], [7001])           # existing seeds never move
        self.assertEqual(list(manifest["tasks"]), ["F01", "F03", "F02"])

    def test_a_removed_task_is_dropped(self):
        root = self.copy_pack()
        shutil.rmtree(os.path.join(root, "tasks", "F02"))
        code, text = run_builder("--pack-dir", root)
        self.assertEqual(code, 0, text)
        self.assertIn("F02: removed", text)
        manifest = load_json(self.manifest_path(root))
        self.assertEqual(list(manifest["tasks"]), ["F01"])
        self.assertEqual(manifest["tracks"]["dev"]["standard"], [])

    def test_a_task_that_changes_tier_moves_to_the_end_of_the_new_tier(self):
        root = self.copy_pack()
        self.clone_task(root, "F01", "F03")
        self.assertEqual(run_builder("--pack-dir", root)[0], 0)
        self.edit_task(root, "F01", 'tier = "basic"', 'tier = "standard"')
        self.assertEqual(run_builder("--pack-dir", root)[0], 0)
        tracks = load_json(self.manifest_path(root))["tracks"]
        self.assertEqual(tracks["ops"]["basic"], ["F03"])
        self.assertEqual(tracks["ops"]["standard"], ["F01"])

    def test_the_order_in_the_manifest_is_the_play_order(self):
        root = self.copy_pack()
        self.clone_task(root, "F01", "F03")
        self.clone_task(root, "F01", "F04")
        edit_json(self.manifest_path(root), lambda m: m["tracks"]["ops"].update(basic=["F04", "F01", "F03"]))
        self.assertEqual(run_builder("--pack-dir", root)[0], 0)
        manifest = load_json(self.manifest_path(root))
        self.assertEqual(manifest["tracks"]["ops"]["basic"], ["F04", "F01", "F03"])
        self.assertEqual(manifest["tasks"]["F04"]["seeds"], [7203])           # first free seed, in play order

    def test_unknown_keys_survive_a_rebuild(self):
        root = self.copy_pack()

        def edit(m):
            m["notes"] = {"owner": "x"}
            m["tasks"]["F01"]["flavour"] = "kept"
        edit_json(self.manifest_path(root), edit)
        self.assertEqual(run_builder("--pack-dir", root)[0], 0)
        manifest = load_json(self.manifest_path(root))
        self.assertEqual(manifest["notes"], {"owner": "x"})
        self.assertEqual(manifest["tasks"]["F01"]["flavour"], "kept")
        self.assertEqual(list(manifest)[-1], "notes")
        self.assertEqual(list(manifest["tasks"]["F01"])[-1], "flavour")

    def test_a_skeleton_manifest_is_enough_to_start(self):
        root = self.copy_pack()
        save_json(self.manifest_path(root), {"schema": 1, "packId": "skeleton", "packVersion": "0.1.0",
                                             "plugin": {"min": "2.9.0", "max": None},
                                             "tracks": {"dev": {"standard": ["F02"]}, "ops": {"basic": ["F01"]}}})
        code, text = run_builder("--pack-dir", root)
        self.assertEqual(code, 0, text)
        manifest = load_json(self.manifest_path(root))
        self.assertEqual((manifest["packId"], manifest["packVersion"]), ("skeleton", "0.1.0"))
        self.assertEqual((manifest["tasks"]["F01"]["seeds"], manifest["tasks"]["F02"]["seeds"]), ([7001], [7102]))
        self.assertEqual(manifest["private"]["refs"], ["v{plugin}", "main", "beta"])

    def test_the_private_file_name_of_the_previous_manifest_is_kept(self):
        root = self.copy_pack()
        os.rename(os.path.join(root, "private", "F01.py"), os.path.join(root, "private", "F01_v2.py"))
        edit_json(self.manifest_path(root), lambda m: m["tasks"]["F01"]["private"].update(file="F01_v2.py"))
        code, text = run_builder("--pack-dir", root)
        self.assertEqual(code, 0, text)
        self.assertEqual(load_json(self.manifest_path(root))["tasks"]["F01"]["private"]["file"], "F01_v2.py")

    def test_extra_public_files_are_hashed_after_the_required_ones(self):
        root = self.copy_pack()
        write_text(os.path.join(root, "tasks", "F01", "data.json"), "{}\n")
        write_bytes(os.path.join(root, "tasks", "F01", ".DS_Store"), b"x")
        self.assertEqual(run_builder("--pack-dir", root)[0], 0)
        public = load_json(self.manifest_path(root))["tasks"]["F01"]["public"]
        self.assertEqual(list(public), ["task.py", "prompt.zh-CN.md", "prompt.en.md", "data.json"])
        self.assertEqual(pack.open_pack(pack_dir=root).modified, [])

    def test_separate_roots_and_a_custom_manifest_path(self):
        root = self.copy_pack()
        private_root = self.path("elsewhere")
        shutil.move(os.path.join(root, "private"), private_root)
        target = self.path("out", "m.json")
        code, text = run_builder("--pack-root", root, "--private-root", private_root, "--manifest", target, *FRESH)
        self.assertEqual(code, 0, text)
        self.assertEqual(read_bytes(target), read_bytes(GOOD / "manifest.json"))

    def test_the_default_plugin_minimum_is_the_package_version(self):
        root = self.copy_pack()
        os.remove(self.manifest_path(root))
        self.assertEqual(run_builder("--pack-dir", root, "--pack-id", "x", "--pack-version", "1.0.0")[0], 0)
        plugin = load_json(self.manifest_path(root))["plugin"]
        self.assertEqual(plugin, {"min": load_json(REPO / "SkillsForUnity" / "package.json")["version"], "max": None})


class FailureTests(BuilderCase):
    def fails(self, root, *args, expected=""):
        before = read_bytes(self.manifest_path(root))
        code, text = run_builder("--pack-dir", root, *args)
        self.assertEqual(code, 1, text)
        self.assertIn(expected, text)
        self.assertIn("FAILED", text)
        self.assertEqual(read_bytes(self.manifest_path(root)), before)       # nothing is written on failure

    def test_a_missing_private_verifier(self):
        root = self.copy_pack()
        os.remove(os.path.join(root, "private", "F02.py"))
        self.fails(root, expected="F02: private verifier F02.py is missing or unreadable")

    def test_a_task_that_cannot_be_loaded(self):
        root = self.copy_pack()
        self.edit_task(root, "F01", "TASK = F01()", "def broken(:\n")
        self.fails(root, expected="task F01: SyntaxError")

    def test_a_task_that_declares_another_id(self):
        root = self.copy_pack()
        self.edit_task(root, "F01", 'id = "F01"', 'id = "F09"')
        self.fails(root, expected="declares id 'F09'")

    def test_a_folder_that_is_not_a_task_id(self):
        root = self.copy_pack()
        shutil.copytree(os.path.join(root, "tasks", "F01"), os.path.join(root, "tasks", "1bad"))
        self.fails(root, expected="tasks/1bad: not a valid task id")

    def test_a_folder_inside_a_task_folder(self):
        root = self.copy_pack()
        os.makedirs(os.path.join(root, "tasks", "F01", "sub"))
        self.fails(root, expected="unexpected folder")

    def test_a_folder_without_task_py_is_reported_not_built(self):
        root = self.copy_pack()
        os.makedirs(os.path.join(root, "tasks", "LEFT"))
        write_text(os.path.join(root, "tasks", "LEFT", "notes.txt"), "x")
        self.assertEqual(builder.ignored_folders(root), ["LEFT"])
        code, text = run_builder("--pack-dir", root)
        self.assertEqual(code, 0, text)
        self.assertIn("warning: tasks/LEFT has no task.py and is not in the manifest", text)
        self.assertEqual(list(load_json(self.manifest_path(root))["tasks"]), ["F01", "F02"])

    def test_a_missing_required_public_file(self):
        root = self.copy_pack()
        os.remove(os.path.join(root, "tasks", "F01", "prompt.en.md"))
        self.fails(root, expected="missing prompt.en.md")

    def test_no_pack_id_or_version(self):
        root = self.copy_pack()
        save_json(self.manifest_path(root), {"schema": 1, "tracks": {}})
        code, text = run_builder("--pack-dir", root)
        self.assertEqual(code, 1)
        self.assertIn("packId:", text)
        self.assertIn("packVersion:", text)

    def test_an_unreadable_previous_manifest_is_never_overwritten(self):
        root = self.copy_pack()
        write_text(self.manifest_path(root), "{ not json")
        code, text = run_builder("--pack-dir", root)
        self.assertEqual(code, 1)
        self.assertIn("is not valid JSON", text)
        self.assertEqual(read_text(self.manifest_path(root)), "{ not json")

    def test_no_tasks_folder(self):
        empty = self.path("empty")
        os.makedirs(empty)
        code, text = run_builder("--pack-dir", empty, *FRESH)
        self.assertEqual(code, 1)
        self.assertIn("no tasks folder", text)

    def test_bad_options(self):
        root = self.copy_pack()
        for args, expected in ((["--seed", "F01=x"], "--seed F01=x"), (["--seed", "F01"], "--seed F01"),
                               (["--seed", "1bad=3"], "--seed 1bad=3"), (["--pack-version", "1.0"], "--pack-version 1.0"),
                               (["--plugin-min", "x"], "--plugin-min x"), (["--plugin-max", "1"], "--plugin-max 1")):
            with self.subTest(args):
                self.fails(root, *args, expected=expected)

    def test_option_conflicts_are_usage_errors(self):
        root = self.copy_pack()
        code, text = run_builder("--pack-dir", root, "--pack-root", root)
        self.assertEqual(code, 2)
        self.assertIn("excludes", text)

    def test_options_take_effect(self):
        root = self.copy_pack()
        code, text = run_builder("--pack-dir", root, "--seed", "F01=11,12", "--pack-version", "1.2.3", "--plugin-min",
                                 "2.0.0", "--plugin-max", "3.0.0", "--pack-id", "renamed")
        self.assertEqual(code, 0, text)
        manifest = load_json(self.manifest_path(root))
        self.assertEqual(manifest["tasks"]["F01"]["seeds"], [11, 12])
        self.assertEqual((manifest["packVersion"], manifest["packId"]), ("1.2.3", "renamed"))
        self.assertEqual(manifest["plugin"], {"min": "2.0.0", "max": "3.0.0"})


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
