"""eval/tools/check_eval_pack.py: manifest vs files, and the caseVersion / packVersion rules against a baseline."""
import contextlib
import io
import json
import os
import subprocess
import types
import unittest

try:
    from .test_pack_support import (GOOD, TempCase, edit_json, load_json, read_bytes, read_text, save_json, write_bytes,
                                    write_text)
except ImportError:
    from test_pack_support import (GOOD, TempCase, edit_json, load_json, read_bytes, read_text, save_json, write_bytes,
                                   write_text)

import build_pack_manifest as builder  # noqa: E402
import check_eval_pack as checker      # noqa: E402


class FakeGit(object):
    """Stands in for subprocess.run: `tags` newest first, `manifests` {ref: bytes}; records every command."""

    def __init__(self, tags=(), manifests=None, missing=False):
        self.tags, self.manifests, self.missing, self.calls = list(tags), dict(manifests or {}), missing, []

    def __call__(self, command, **kwargs):
        self.calls.append(command)
        if self.missing:
            raise FileNotFoundError("git")
        args = command[3:]
        if args[:1] == ["tag"]:
            return types.SimpleNamespace(returncode=0, stdout="\n".join(self.tags).encode())
        if args[:1] == ["show"]:
            ref, _sep, path = args[1].partition(":")
            if ref in self.manifests and path == checker.MANIFEST_GIT_PATH:
                return types.SimpleNamespace(returncode=0, stdout=self.manifests[ref])
        return types.SimpleNamespace(returncode=128, stdout=b"")


class CheckerCase(TempCase):
    def setUp(self):
        TempCase.setUp(self)
        self.pack_root = self.copy_pack()
        self.package_json = self.path("package.json")
        save_json(self.package_json, {"version": "2.9.1"})

    def manifest_path(self):
        return os.path.join(self.pack_root, "manifest.json")

    def run_check(self, *args, run=None):
        out = io.StringIO()
        code = checker.main(["--pack-dir", self.pack_root, "--package-json", self.package_json] + list(args), out=out,
                            run=run or FakeGit())
        return code, out.getvalue()

    def rebuild(self, *args):
        out = io.StringIO()
        self.assertEqual(builder.main(["--pack-dir", self.pack_root] + list(args), out=out), 0, out.getvalue())

    def edit_task(self, task_id, old, new):
        path = os.path.join(self.pack_root, "tasks", task_id, "task.py")
        text = read_text(path)
        self.assertIn(old, text)
        write_bytes(path, b"\xef\xbb\xbf" + text.replace(old, new).encode("utf-8"))

    def clone_task(self, source, target):
        import shutil
        shutil.copytree(os.path.join(self.pack_root, "tasks", source), os.path.join(self.pack_root, "tasks", target))
        for name in os.listdir(os.path.join(self.pack_root, "tasks", target)):
            path = os.path.join(self.pack_root, "tasks", target, name)
            write_bytes(path, read_bytes(path).replace(source.encode(), target.encode()))
        write_bytes(os.path.join(self.pack_root, "private", target + ".py"),
                    read_bytes(os.path.join(self.pack_root, "private", source + ".py")).replace(source.encode(),
                                                                                              target.encode()))


class PackOnDiskTests(CheckerCase):
    def test_a_clean_pack_passes(self):
        code, text = self.run_check("--no-baseline")
        self.assertEqual(code, 0, text)
        self.assertIn("check_eval_pack: OK (0 errors, 0 warnings, 2 tasks)", text)

    def test_an_edited_public_file_fails(self):
        with open(os.path.join(self.pack_root, "tasks", "F01", "prompt.en.md"), "a", encoding="utf-8") as handle:
            handle.write("R3. More.\n")
        code, text = self.run_check("--no-baseline")
        self.assertEqual(code, 1)
        self.assertIn("error: public file F01/prompt.en.md differs from the manifest", text)
        self.assertIn("FAILED (1 errors", text)

    def test_an_unlisted_public_file_fails(self):
        write_text(os.path.join(self.pack_root, "tasks", "F02", "notes.txt"), "x")
        code, text = self.run_check("--no-baseline")
        self.assertEqual(code, 1)
        self.assertIn("public file F02/notes.txt is not listed in the manifest", text)

    def test_a_task_folder_the_manifest_does_not_list_fails(self):
        os.makedirs(os.path.join(self.pack_root, "tasks", "LEFT"))
        write_text(os.path.join(self.pack_root, "tasks", "LEFT", "task.py"), "x = 1\n")
        write_text(os.path.join(self.pack_root, "tasks", ".hidden"), "x")
        code, text = self.run_check("--no-baseline")
        self.assertEqual(code, 1)
        self.assertIn("error: tasks/LEFT is not listed in the manifest", text)
        self.assertNotIn(".hidden", text)

    def test_a_missing_public_file_fails(self):
        os.remove(os.path.join(self.pack_root, "tasks", "F02", "prompt.zh-CN.md"))
        code, text = self.run_check("--no-baseline")
        self.assertEqual(code, 1)
        self.assertIn("tasks/F02/prompt.zh-CN.md is missing", text)

    def test_private_verifier_defects(self):
        private = os.path.join(self.pack_root, "private")
        write_text(os.path.join(private, "F01.py"), "ASSERTIONS = []\n")
        os.remove(os.path.join(private, "F02.py"))
        code, text = self.run_check("--no-baseline")
        self.assertEqual(code, 1)
        self.assertIn("task F01: private verifier F01.py differs from the manifest", text)
        self.assertIn("task F02: private verifier F02.py not found", text)

    def test_private_size_must_match(self):
        edit_json(self.manifest_path(), lambda m: m["tasks"]["F01"]["private"].update(bytes=5))
        code, text = self.run_check("--no-baseline")
        self.assertEqual(code, 1)
        self.assertIn("private verifier F01.py is %d bytes, the manifest says 5" % len(
            read_bytes(os.path.join(self.pack_root, "private", "F01.py"))), text)

    def test_task_attributes_must_match_the_manifest(self):
        self.edit_task("F01", "minutes = 5", "minutes = 6")
        code, text = self.run_check("--no-baseline")
        self.assertEqual(code, 1)
        self.assertIn("task F01: minutes is 6 but the manifest says 5", text)

    def test_a_task_that_does_not_load(self):
        self.edit_task("F02", "TASK = F02()", "def broken(:\n")
        code, text = self.run_check("--no-baseline")
        self.assertEqual(code, 1)
        self.assertIn("task F02: SyntaxError", text)

    def test_every_manifest_error_is_listed(self):
        edit_json(self.manifest_path(), lambda m: m.update(packId="", packVersion="x"))
        code, text = self.run_check("--no-baseline")
        self.assertEqual(code, 1)
        self.assertIn("error: manifest: packId:", text)
        self.assertIn("error: manifest: packVersion:", text)
        self.assertNotIn("tasks)", text)

    def test_manifest_url_fields_are_required(self):
        edit_json(self.manifest_path(), lambda m: m.pop("private"))
        code, text = self.run_check("--no-baseline")
        self.assertEqual(code, 1)
        self.assertIn("urlTemplates and refs are required", text)

    def test_unusable_manifest_files(self):
        write_text(self.manifest_path(), "{")
        code, text = self.run_check("--no-baseline")
        self.assertEqual(code, 1)
        self.assertIn("cannot be read (JSONDecodeError)", text)

    def test_a_missing_manifest_cannot_be_checked(self):
        os.remove(self.manifest_path())
        code, text = self.run_check("--no-baseline")
        self.assertEqual(code, 2)
        self.assertIn("no manifest at", text)

    def test_unused_private_files_only_warn(self):
        write_text(os.path.join(self.pack_root, "private", "X1.py"), "pass\n")
        write_text(os.path.join(self.pack_root, "private", "__init__.py"), "")
        write_text(os.path.join(self.pack_root, "private", "README.md"), "x")
        code, text = self.run_check("--no-baseline")
        self.assertEqual(code, 0, text)
        self.assertIn("warning: private file X1.py is not used by the manifest", text)
        self.assertEqual(text.count("warning:"), 1)

    def test_plugin_range_warns_and_fails_for_a_release(self):
        save_json(self.package_json, {"version": "2.8.0"})
        code, text = self.run_check("--no-baseline")
        self.assertEqual(code, 0, text)
        self.assertIn("warning: plugin range 2.9.0..latest does not admit package version 2.8.0", text)
        code, text = self.run_check("--no-baseline", "--release")
        self.assertEqual(code, 1)
        self.assertIn("error: plugin range 2.9.0..latest does not admit package version 2.8.0", text)

    def test_an_unknown_package_version_skips_the_range(self):
        write_text(self.package_json, "{}")
        code, text = self.run_check("--no-baseline", "--release")
        self.assertEqual(code, 0, text)
        self.assertIn("package version unknown", text)

    def test_the_default_roots_are_the_package_and_eval_pack(self):
        self.assertTrue(checker.DEFAULT_PACK_ROOT.endswith(os.path.join("Evaluation~", "pack")))
        self.assertTrue(checker.DEFAULT_PRIVATE_ROOT.endswith(os.path.join("eval", "pack", "private")))
        self.assertEqual(checker.DEFAULT_PACK_ROOT, builder.DEFAULT_PACK_ROOT)
        self.assertEqual(checker.DEFAULT_PRIVATE_ROOT, builder.DEFAULT_PRIVATE_ROOT)

    def test_conflicting_roots_are_a_usage_error(self):
        out = io.StringIO()
        self.assertEqual(checker.main(["--pack-dir", self.pack_root, "--pack-root", self.pack_root], out=out), 2)


class BaselineRuleTests(CheckerCase):
    def setUp(self):
        CheckerCase.setUp(self)
        self.baseline = self.path("baseline.json")
        save_json(self.baseline, load_json(GOOD / "manifest.json"))

    def check(self, *args):
        return self.run_check("--baseline", self.baseline, *args)

    def edit_baseline(self, edit):
        edit_json(self.baseline, edit)

    def test_an_unchanged_pack_passes(self):
        code, text = self.check()
        self.assertEqual(code, 0, text)
        self.assertIn("note: baseline: %s (pack 1.0.0)" % self.baseline, text)

    def test_a_changed_prompt_needs_a_higher_case_version(self):
        with open(os.path.join(self.pack_root, "tasks", "F01", "prompt.en.md"), "a", encoding="utf-8") as handle:
            handle.write("R3. More.\n")
        self.rebuild()
        code, text = self.check()
        self.assertEqual(code, 1)
        self.assertIn("error: task F01: prompt.en.md changed but caseVersion is still 2", text)
        self.assertEqual(text.count("error:"), 1)

    def test_a_changed_task_file_and_prompts_are_listed_together(self):
        for name in ("prompt.en.md", "prompt.zh-CN.md"):
            with open(os.path.join(self.pack_root, "tasks", "F02", name), "a", encoding="utf-8") as handle:
                handle.write("R9. More.\n")
        self.rebuild()
        code, text = self.check()
        self.assertEqual(code, 1)
        self.assertIn("task F02: prompt.en.md, prompt.zh-CN.md changed but caseVersion is still 1", text)

    def test_raising_the_case_version_also_needs_a_minor_pack_version(self):
        self.edit_task("F01", "case_version = 2", "case_version = 3")
        self.rebuild()
        code, text = self.check()
        self.assertEqual(code, 1)
        self.assertIn("packVersion 1.0.0 must raise at least the minor version of 1.0.0", text)
        self.rebuild("--pack-version", "1.0.1")
        self.assertEqual(self.check()[0], 1)                                   # a patch is not enough
        self.rebuild("--pack-version", "1.1.0")
        code, text = self.check()
        self.assertEqual(code, 0, text)
        self.rebuild("--pack-version", "2.0.0")
        self.assertEqual(self.check()[0], 0)

    def test_a_changed_private_verifier(self):
        write_text(os.path.join(self.pack_root, "private", "F02.py"), "ASSERTIONS = []\n")
        self.rebuild()
        code, text = self.check()
        self.assertEqual(code, 1)
        self.assertIn("task F02: private verifier changed but caseVersion is still 1", text)

    def test_changed_seeds_minutes_and_requires(self):
        self.rebuild("--seed", "F01=5,6")
        self.edit_task("F02", "minutes = 8", "minutes = 9")
        self.edit_task("F02", '"dedicated": True', '"dedicated": False')
        self.rebuild()
        code, text = self.check()
        self.assertEqual(code, 1)
        self.assertIn("task F01: seeds changed but caseVersion is still 2", text)
        self.assertRegex(text, r"task F02: .*minutes.*requires.* changed but caseVersion is still 1|"
                               r"task F02: .*changed but caseVersion is still 1")
        self.assertIn("minutes", text)
        self.assertIn("requires", text)

    def test_a_falling_case_version(self):
        self.edit_baseline(lambda m: m["tasks"]["F01"].update(caseVersion=5))
        code, text = self.check()
        self.assertEqual(code, 1)
        self.assertIn("task F01: caseVersion fell from 5 to 2", text)

    def test_a_raised_case_version_without_changes_warns(self):
        self.edit_baseline(lambda m: m["tasks"]["F01"].update(caseVersion=1))
        code, text = self.check()
        self.assertEqual(code, 1)                                              # still needs the minor bump
        self.assertIn("warning: task F01: caseVersion raised from 1 to 2 without a change", text)
        self.assertIn("packVersion 1.0.0 must raise at least the minor version", text)

    def test_new_and_removed_tasks_need_a_minor_pack_version(self):
        self.clone_task("F01", "F03")
        self.rebuild()
        code, text = self.check()
        self.assertEqual(code, 1)
        self.assertIn("note: tasks added since the baseline: F03", text)
        self.assertIn("packVersion 1.0.0 must raise at least the minor version", text)
        self.rebuild("--pack-version", "1.1.0")
        self.assertEqual(self.check()[0], 0)
        self.edit_baseline(lambda m: m["tasks"].update(F09=m["tasks"]["F01"]))
        code, text = self.check()
        self.assertEqual(code, 0, text)                                        # 1.1.0 is already a minor above 1.0.0
        self.assertIn("tasks removed since the baseline: F09", text)

    def test_metadata_only_changes_need_a_higher_pack_version(self):
        self.rebuild("--plugin-max", "4.0.0")
        code, text = self.check()
        self.assertEqual(code, 1)
        self.assertIn("packVersion 1.0.0 must be above 1.0.0: the manifest changed", text)
        self.rebuild("--pack-version", "1.0.1")
        self.assertEqual(self.check()[0], 0)

    def test_the_baseline_may_be_any_older_shape(self):
        for baseline, expected in (({"packId": "fixture-pack"}, "holds no tasks"), ([], "cannot read"),
                                   ({"packId": "other", "tasks": {}}, "not 'fixture-pack'"),
                                   ({"packId": "fixture-pack", "tasks": {"F01": 5, "F02": {}}, "packVersion": "x"},
                                    "no valid packVersion")):
            with self.subTest(baseline):
                save_json(self.baseline, baseline) if isinstance(baseline, dict) else write_text(self.baseline, "[]")
                code, text = self.check()
                if isinstance(baseline, list):
                    self.assertEqual(code, 2)
                else:
                    self.assertEqual(code, 0, text)
                self.assertIn(expected, text)

    def test_an_unreadable_baseline_cannot_be_used(self):
        code, text = self.run_check("--baseline", self.path("missing.json"))
        self.assertEqual(code, 2)
        self.assertIn("cannot read the baseline", text)

    def test_baseline_options_are_mutually_exclusive(self):
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            self.run_check("--baseline", self.baseline, "--no-baseline")

    def test_a_manifest_that_fails_its_own_checks_skips_the_comparison(self):
        edit_json(self.manifest_path(), lambda m: m.update(packId=""))
        code, text = self.check()
        self.assertEqual(code, 1)
        self.assertNotIn("baseline", text)


class GitBaselineTests(CheckerCase):
    def good_bytes(self, **changes):
        manifest = load_json(GOOD / "manifest.json")
        manifest.update(changes)
        return json.dumps(manifest).encode("utf-8")

    def test_the_newest_tag_with_a_manifest_is_used(self):
        git = FakeGit(["v3.1.0", "v3.0.0", "v2.9.1"], {"v3.0.0": self.good_bytes(), "v2.9.1": self.good_bytes()})
        data, label = checker.baseline_from_git("/repo", run=git)
        self.assertEqual(label, "git v3.0.0")
        self.assertEqual(data["packId"], "fixture-pack")
        self.assertEqual(git.calls[0], ["git", "-C", "/repo", "tag", "--list", "v*", "--sort=-v:refname"])
        self.assertEqual(git.calls[1:], [["git", "-C", "/repo", "show", "v3.1.0:" + checker.MANIFEST_GIT_PATH],
                                         ["git", "-C", "/repo", "show", "v3.0.0:" + checker.MANIFEST_GIT_PATH]])

    def test_an_explicit_ref_is_the_only_candidate(self):
        git = FakeGit(["v3.0.0"], {"v3.0.0": self.good_bytes(), "origin/main": self.good_bytes(packVersion="9.9.9")})
        data, label = checker.baseline_from_git("/repo", ref="origin/main", run=git)
        self.assertEqual((label, data["packVersion"]), ("git origin/main", "9.9.9"))
        self.assertEqual(len(git.calls), 1)
        self.assertEqual(checker.baseline_from_git("/repo", ref="nope", run=git), (None, None))

    def test_no_git_no_tags_or_bad_json_mean_no_baseline(self):
        self.assertEqual(checker.baseline_from_git("/repo", run=FakeGit(missing=True)), (None, None))
        self.assertEqual(checker.baseline_from_git("/repo", run=FakeGit([])), (None, None))
        self.assertEqual(checker.baseline_from_git("/repo", run=FakeGit(["v1.0.0"], {"v1.0.0": b"{"})), (None, None))
        self.assertEqual(checker.baseline_from_git("/repo", run=FakeGit(["v1.0.0"], {"v1.0.0": b"[]"})), (None, None))

        def failing(command, **kwargs):
            raise subprocess.TimeoutExpired(command, 1)
        self.assertEqual(checker.baseline_from_git("/repo", run=failing), (None, None))

    def test_main_compares_with_the_discovered_tag(self):
        code, text = self.run_check(run=FakeGit(["v3.0.0"], {"v3.0.0": self.good_bytes()}))
        self.assertEqual(code, 0, text)
        self.assertIn("note: baseline: git v3.0.0 (pack 1.0.0)", text)
        stale = self.good_bytes()
        manifest = json.loads(stale.decode("utf-8"))
        manifest["tasks"]["F01"]["private"]["sha256"] = "0" * 64
        code, text = self.run_check(run=FakeGit(["v3.0.0"], {"v3.0.0": json.dumps(manifest).encode("utf-8")}))
        self.assertEqual(code, 1)
        self.assertIn("task F01: private verifier changed but caseVersion is still 2", text)

    def test_main_without_a_released_manifest_only_notes_it(self):
        code, text = self.run_check(run=FakeGit(["v2.9.1"]))
        self.assertEqual(code, 0, text)
        self.assertIn("no released manifest found in git tags", text)

    def test_a_required_baseline_must_exist(self):
        code, text = self.run_check("--require-baseline", run=FakeGit(["v2.9.1"]))
        self.assertEqual(code, 1)
        self.assertIn("error: no released manifest found in git tags; pass --baseline or --baseline-ref", text)
        code, text = self.run_check("--require-baseline", run=FakeGit(["v3.0.0"], {"v3.0.0": self.good_bytes()}))
        self.assertEqual(code, 0, text)
        code, text = self.run_check("--require-baseline", "--no-baseline")
        self.assertEqual(code, 2)
        self.assertIn("contradicts", text)

    def test_an_explicit_ref_that_does_not_resolve_is_an_error(self):
        code, text = self.run_check("--baseline-ref", "v9.9.9", run=FakeGit(["v3.0.0"]))
        self.assertEqual(code, 2)
        self.assertIn("no manifest at git ref v9.9.9", text)

    def test_the_default_repo_is_the_one_the_tool_lives_in(self):
        git = FakeGit()
        self.run_check(run=git)
        self.assertEqual(git.calls[0][2], checker.REPO)


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
