"""Tests for scripts/unity_eval.py, the agent entry that finds the Unity project and its UnitySkills package and runs
the package's Evaluation~/run_eval.py. Packages here are throwaway layouts whose runner only reports how it was called,
so every package source type is exercised without Unity or the real runner (which supplies the parity checks).

    python3 -B -m unittest discover -s .github/scripts/tests
"""
from __future__ import annotations

import contextlib
import importlib.util
import io
import json
import os
import re
import subprocess
import sys
import tempfile
import time
import unittest
from unittest import mock

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
ENTRY = os.path.join(REPO, "SkillsForUnity", "unity-skills~", "scripts", "unity_eval.py")
EVALUATION_DOC = os.path.join(REPO, "SkillsForUnity", "unity-skills~", "skills", "evaluation", "SKILL.md")
RUNNER_PACKAGE = os.path.join(REPO, "SkillsForUnity", "Evaluation~")
PACKAGE_NAME = "com.besty.unity-skills"

FAKE_RUNNER = (
    "import json, os, sys\n"
    "print(json.dumps({'argv': sys.argv, 'entry': os.environ.get('USK_EVAL_ENTRY'), 'file': __file__,\n"
    "                  'name': __name__, 'noBytecode': sys.dont_write_bytecode}))\n"
    "sys.exit(int(os.environ.get('FAKE_EXIT', '0')))\n"
)


def norm(path: str) -> str:
    return os.path.normcase(os.path.realpath(path))


def write(path: str, text: str = "") -> str:
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(text)
    return path


def make_project(parent: str, name: str = "Project") -> str:
    project = os.path.join(parent, name)
    os.makedirs(os.path.join(project, "Assets"), exist_ok=True)
    os.makedirs(os.path.join(project, "Packages"), exist_ok=True)
    write(os.path.join(project, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 6000.3.9f1\n")
    return project


def make_package(directory: str, name: str = PACKAGE_NAME, runner: bool = True) -> str:
    write(os.path.join(directory, "package.json"), json.dumps({"name": name, "version": "3.0.0"}))
    if runner:
        write(os.path.join(directory, "Evaluation~", "run_eval.py"), FAKE_RUNNER)
    return directory


def write_lock(project: str, entry: object) -> None:
    dependencies = {} if entry is None else {PACKAGE_NAME: entry}
    write(os.path.join(project, "Packages", "packages-lock.json"), json.dumps({"dependencies": dependencies}))


def set_mtime(package: str, seconds: int) -> None:
    os.utime(os.path.join(package, "package.json"), (seconds, seconds))


def cache_dir(project: str, suffix: str) -> str:
    return os.path.join(project, "Library", "PackageCache", PACKAGE_NAME + "@" + suffix)


def install_copy(destination: str) -> str:
    """The entry the way SkillInstaller copies it into an agent skill folder: LF, UTF-8 without a BOM."""
    with open(ENTRY, "r", encoding="utf-8-sig") as handle:
        text = handle.read().replace("\r\n", "\n")
    return write(destination, text)


def load_entry():
    saved = sys.dont_write_bytecode
    sys.dont_write_bytecode = True
    try:
        spec = importlib.util.spec_from_file_location("unity_eval_under_test", ENTRY)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
    finally:
        sys.dont_write_bytecode = saved
    return module


def load_runner_paths():
    """usk_eval.paths of the real runner, which the entry's package-root rules must agree with."""
    if not os.path.isdir(RUNNER_PACKAGE):
        raise unittest.SkipTest("the runner package is not in this checkout")
    saved = sys.dont_write_bytecode
    sys.dont_write_bytecode = True
    sys.path.insert(0, RUNNER_PACKAGE)
    try:
        from usk_eval import paths
        return paths
    finally:
        sys.path.remove(RUNNER_PACKAGE)
        sys.dont_write_bytecode = saved


# Each layout builds a project (and where it lives, a package) the way Unity records one package source.
# A layout returns (project, package root the entry must pick).

def layout_embedded(root):
    project = make_project(root)
    # "AAA" sorts before "UnitySkills": the package is found by its name, not by being the first folder.
    make_package(os.path.join(project, "Packages", "AAA"), name="com.example.other")
    package = make_package(os.path.join(project, "Packages", "UnitySkills"))
    write_lock(project, {"version": "file:UnitySkills", "depth": 0, "source": "embedded", "dependencies": {}})
    return project, package


def layout_local_absolute(root):
    project = make_project(root)
    package = make_package(os.path.join(root, "Repo", "SkillsForUnity"))
    write_lock(project, {"version": "file:" + package.replace(os.sep, "/"), "depth": 0, "source": "local",
                         "dependencies": {}})
    return project, package


def layout_local_relative(root):
    project = make_project(os.path.join(root, "Repo", "Projects"), "Game")
    package = make_package(os.path.join(root, "Repo", "SkillsForUnity"))
    relative = os.path.relpath(package, os.path.join(project, "Packages")).replace(os.sep, "/")
    write_lock(project, {"version": "file:" + relative, "depth": 0, "source": "local", "dependencies": {}})
    return project, package


def git_entry(commit):
    return {"version": "https://github.com/Besty0728/Unity-Skills.git?path=/SkillsForUnity#v2.9.1", "depth": 0,
            "source": "git", "dependencies": {}, "hash": commit}


def layout_git_single_copy(root):
    project = make_project(root)
    package = make_package(cache_dir(project, "58d3339d90"))
    write_lock(project, git_entry("58d3339d90f1e2d3c4b5a69788796a5b4c3d2e1f"))
    return project, package


def layout_git_suffix_is_hash_prefix(root):
    """Unity 2022.3: the suffix is the lock hash's first 10 characters, even when another copy is newer."""
    project = make_project(root)
    package = make_package(cache_dir(project, "58d3339d90"))
    newer = make_package(cache_dir(project, "1234567890"))
    set_mtime(package, 1_000_000_000)
    set_mtime(newer, 1_900_000_000)
    write_lock(project, git_entry("58d3339d90f1e2d3c4b5a69788796a5b4c3d2e1f"))
    return project, package


def layout_git_suffix_is_fingerprint(root):
    """Unity 6000.3: the suffix is a package.json fingerprint unrelated to the hash, so the newest copy wins."""
    project = make_project(root)
    older = make_package(cache_dir(project, "0123456789ab"))
    package = make_package(cache_dir(project, "54357c6cfac1"))
    set_mtime(older, 1_000_000_000)
    set_mtime(package, 1_900_000_000)
    write_lock(project, git_entry("c04335935d5f0a1b2c3d4e5f60718293a4b5c6d7"))
    return project, package


def layout_git_skips_cache_copy_of_another_package(root):
    project = make_project(root)
    package = make_package(cache_dir(project, "0123456789ab"))
    decoy = make_package(cache_dir(project, "54357c6cfac1"), name="com.example.other")
    set_mtime(package, 1_000_000_000)
    set_mtime(decoy, 1_900_000_000)
    write_lock(project, git_entry("c04335935d5f0a1b2c3d4e5f60718293a4b5c6d7"))
    return project, package


def layout_registry(root):
    project = make_project(root)
    package = make_package(cache_dir(project, "3.0.0"))
    write_lock(project, {"version": "3.0.0", "depth": 0, "source": "registry", "dependencies": {},
                         "url": "https://packages.unity.com"})
    return project, package


def layout_unlisted_source(root):
    project = make_project(root)
    package = make_package(cache_dir(project, "abcdef012345"))
    write_lock(project, {"version": "1.0.0", "depth": 0, "source": "somethingnew", "dependencies": {}})
    return project, package


def layout_no_lock_entry_uses_cache(root):
    project = make_project(root)
    package = make_package(cache_dir(project, "3.0.0"))
    write_lock(project, None)
    return project, package


def layout_no_lock_file_uses_embedded(root):
    project = make_project(root)
    package = make_package(os.path.join(project, "Packages", PACKAGE_NAME))
    return project, package


LAYOUTS = (
    ("embedded", layout_embedded),
    ("local, absolute file: path", layout_local_absolute),
    ("local, file: path relative to Packages", layout_local_relative),
    ("git, one cache copy", layout_git_single_copy),
    ("git, several copies, suffix is a hash prefix", layout_git_suffix_is_hash_prefix),
    ("git, several copies, suffix is a fingerprint", layout_git_suffix_is_fingerprint),
    ("git, several copies, the newest is another package", layout_git_skips_cache_copy_of_another_package),
    ("registry", layout_registry),
    ("unlisted source", layout_unlisted_source),
    ("no lock entry, cache copy", layout_no_lock_entry_uses_cache),
    ("no lock file, embedded scan", layout_no_lock_file_uses_embedded),
)


def layout_embedded_missing(root):
    project = make_project(root)
    make_package(os.path.join(project, "Packages", "AAA"), name="com.example.other")
    write_lock(project, {"version": "file:UnitySkills", "depth": 0, "source": "embedded", "dependencies": {}})
    return project


def layout_git_cache_empty(root):
    project = make_project(root)
    write_lock(project, git_entry("58d3339d90f1e2d3c4b5a69788796a5b4c3d2e1f"))
    return project


def layout_cache_copy_of_another_package(root):
    project = make_project(root)
    make_package(cache_dir(project, "58d3339d90"), name="com.example.other")
    write_lock(project, git_entry("58d3339d90f1e2d3c4b5a69788796a5b4c3d2e1f"))
    return project


def layout_local_without_file_prefix(root):
    project = make_project(root)
    make_package(os.path.join(project, "Packages", PACKAGE_NAME))
    write_lock(project, {"version": "1.0.0", "depth": 0, "source": "local", "dependencies": {}})
    return project


def layout_local_target_missing(root):
    project = make_project(root)
    write_lock(project, {"version": "file:../Nowhere", "depth": 0, "source": "local", "dependencies": {}})
    return project


def layout_nothing_installed(root):
    return make_project(root)


UNRESOLVED_LAYOUTS = (
    ("embedded entry without a package folder", layout_embedded_missing),
    ("git entry with an empty cache", layout_git_cache_empty),
    ("cache copy of another package", layout_cache_copy_of_another_package),
    ("local entry without file:", layout_local_without_file_prefix),
    ("local entry pointing nowhere", layout_local_target_missing),
    ("nothing installed", layout_nothing_installed),
)


class EntryTestCase(unittest.TestCase):
    def setUp(self):
        self._temp = tempfile.TemporaryDirectory()
        self.addCleanup(self._temp.cleanup)
        self.root = os.path.realpath(self._temp.name)
        self.registry = os.path.join(self.root, "registry-absent.json")

    def run_entry(self, args, cwd, script=ENTRY, python_flags=("-B",), **env_extra):
        env = {key: value for key, value in os.environ.items()
               if not key.startswith(("USK_EVAL_", "FAKE_")) and key != "PYTHONDONTWRITEBYTECODE"}
        env.update({"PYTHONIOENCODING": "utf-8", "USK_EVAL_REGISTRY": self.registry})
        env.update(env_extra)
        return subprocess.run([sys.executable, *python_flags, script, *args], cwd=cwd, env=env,
                              capture_output=True, text=True, encoding="utf-8")

    def runner_report(self, result):
        self.assertEqual(result.stderr, "")
        self.assertEqual(len(result.stdout.strip().splitlines()), 1, result.stdout)
        return json.loads(result.stdout)

    def assert_failure(self, result, code, command="start"):
        self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
        self.assertEqual(result.stderr, "")
        self.assertTrue(result.stdout.endswith("\n"))
        self.assertEqual(len(result.stdout.splitlines()), 1, "the envelope is the only output")
        envelope = json.loads(result.stdout)
        self.assertEqual(set(envelope), {"ok", "command", "data", "problems", "next"})
        self.assertIs(envelope["ok"], False)
        self.assertEqual(envelope["command"], command)
        self.assertIsNone(envelope["data"])
        self.assertIsNone(envelope["next"])
        self.assertEqual([problem["code"] for problem in envelope["problems"]], [code])
        self.assertTrue(envelope["problems"][0]["message"])
        self.assertTrue(envelope["problems"][0]["fix"])
        return envelope


class PackageSourceTests(EntryTestCase):
    def test_every_package_source_resolves_to_its_runner(self):
        for label, build in LAYOUTS:
            with self.subTest(label), tempfile.TemporaryDirectory() as temp:
                project, package = build(os.path.realpath(temp))
                result = self.run_entry(["start", "--track", "ops"], cwd=project)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                seen = self.runner_report(result)
                self.assertEqual(norm(seen["argv"][0]), norm(os.path.join(package, "Evaluation~", "run_eval.py")))
                self.assertEqual(norm(seen["file"]), norm(seen["argv"][0]))

    def test_unresolved_layouts_report_package_not_found(self):
        for label, build in UNRESOLVED_LAYOUTS:
            with self.subTest(label), tempfile.TemporaryDirectory() as temp:
                project = build(os.path.realpath(temp))
                envelope = self.assert_failure(self.run_entry(["start"], cwd=project), "PACKAGE_NOT_FOUND")
                self.assertIn("com.besty.unity-skills", envelope["problems"][0]["fix"])

    def test_package_without_a_runner_reports_runner_not_found(self):
        project = make_project(self.root)
        make_package(os.path.join(project, "Packages", "UnitySkills"), runner=False)
        write_lock(project, {"version": "file:UnitySkills", "depth": 0, "source": "embedded", "dependencies": {}})
        envelope = self.assert_failure(self.run_entry(["next"], cwd=project), "RUNNER_NOT_FOUND", command="next")
        self.assertIn("3.0", envelope["problems"][0]["fix"])


class RunTests(EntryTestCase):
    def embedded_project(self, parent=None):
        project = make_project(parent or self.root)
        package = make_package(os.path.join(project, "Packages", "UnitySkills"))
        write_lock(project, {"version": "file:UnitySkills", "depth": 0, "source": "embedded", "dependencies": {}})
        return project, package

    def test_project_goes_in_right_after_the_command_and_the_entry_is_published(self):
        project, package = self.embedded_project()
        result = self.run_entry(["start", "--track", "ops", "--lang", "en"], cwd=project)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        seen = self.runner_report(result)
        self.assertEqual(seen["argv"][1:3], ["start", "--project"])
        self.assertEqual(norm(seen["argv"][3]), norm(project))
        self.assertEqual(seen["argv"][4:], ["--track", "ops", "--lang", "en"])
        self.assertEqual(seen["entry"], os.path.abspath(ENTRY))
        self.assertEqual(seen["name"], "__main__")
        self.assertIs(seen["noBytecode"], True)

    def test_runner_exit_code_is_the_exit_code(self):
        project, _ = self.embedded_project()
        result = self.run_entry(["status"], cwd=project, FAKE_EXIT="5")
        self.assertEqual(result.returncode, 5, result.stdout + result.stderr)
        self.assertEqual(self.runner_report(result)["argv"][1], "status")

    def test_an_explicit_project_option_is_left_alone(self):
        project, package = self.embedded_project()
        elsewhere = os.path.join(self.root, "Elsewhere")
        os.makedirs(elsewhere)
        for args in (["start", "--tier", "basic", "--project", project], ["start", "--project=" + project, "--lang", "en"]):
            with self.subTest(args):
                result = self.run_entry(args, cwd=elsewhere)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertEqual(self.runner_report(result)["argv"][1:], args)

    def test_an_explicit_project_must_be_a_unity_project(self):
        project, _ = self.embedded_project()
        notproject = os.path.join(self.root, "Plain")
        os.makedirs(notproject)
        envelope = self.assert_failure(self.run_entry(["start", "--project", notproject], cwd=project), "PROJECT_NOT_FOUND")
        self.assertIn(notproject, envelope["problems"][0]["message"])

    def test_option_beats_environment_beats_cwd(self):
        first, _ = self.embedded_project(os.path.join(self.root, "A"))
        second, second_package = self.embedded_project(os.path.join(self.root, "B"))
        third, third_package = self.embedded_project(os.path.join(self.root, "C"))
        seen = self.runner_report(self.run_entry(["start"], cwd=first, USK_EVAL_PROJECT=second))
        self.assertEqual(norm(seen["argv"][0]), norm(os.path.join(second_package, "Evaluation~", "run_eval.py")))
        seen = self.runner_report(self.run_entry(["start", "--project", third], cwd=first, USK_EVAL_PROJECT=second))
        self.assertEqual(norm(seen["argv"][0]), norm(os.path.join(third_package, "Evaluation~", "run_eval.py")))
        self.assertEqual(seen["argv"][1:], ["start", "--project", third])

    def test_a_folder_inside_the_project_is_enough(self):
        project, package = self.embedded_project()
        deep = os.path.join(project, "Assets", "Scripts", "Deep")
        os.makedirs(deep)
        result = self.run_entry(["start"], cwd=deep)
        self.assertEqual(norm(self.runner_report(result)["argv"][3]), norm(project))

    def test_an_installed_copy_finds_the_project_it_lives_in(self):
        project, package = self.embedded_project()
        copy = install_copy(os.path.join(project, ".claude", "skills", "unity-skills", "scripts", "unity_eval.py"))
        outside = os.path.join(self.root, "Outside")
        os.makedirs(outside)
        result = self.run_entry(["start"], cwd=outside, script=copy)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        seen = self.runner_report(result)
        self.assertEqual(norm(seen["argv"][3]), norm(project))
        self.assertEqual(seen["entry"], os.path.abspath(copy))

    def test_the_working_directory_beats_the_location_of_the_script(self):
        home_project, _ = self.embedded_project(os.path.join(self.root, "Home"))
        other_project, _ = self.embedded_project(os.path.join(self.root, "Other"))
        copy = install_copy(os.path.join(home_project, ".claude", "skills", "unity-skills", "scripts", "unity_eval.py"))
        result = self.run_entry(["start"], cwd=other_project, script=copy)
        self.assertEqual(norm(self.runner_report(result)["argv"][3]), norm(other_project))

    def test_no_bytecode_is_written_even_without_the_dash_b_flag(self):
        project, package = self.embedded_project()
        write(os.path.join(package, "Evaluation~", "helper.py"), "VALUE = 1\n")
        write(os.path.join(package, "Evaluation~", "run_eval.py"),
              "import os, sys\nsys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))\n"
              "import helper\nsys.exit(helper.VALUE - 1)\n")
        result = self.run_entry(["start"], cwd=project, python_flags=())
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        leftovers = [os.path.join(base, name) for base, names, _ in os.walk(self.root)
                     for name in names if name == "__pycache__"]
        self.assertEqual(leftovers, [])


class RegistryTests(EntryTestCase):
    def setUp(self):
        super().setUp()
        self.outside = os.path.join(self.root, "Outside")
        os.makedirs(self.outside)
        self.copy = install_copy(
            os.path.join(self.root, "Home", ".claude", "skills", "unity-skills", "scripts", "unity_eval.py"))

    def project(self, name):
        project = make_project(os.path.join(self.root, "Projects"), name)
        make_package(os.path.join(project, "Packages", "UnitySkills"))
        write_lock(project, {"version": "file:UnitySkills", "depth": 0, "source": "embedded", "dependencies": {}})
        return project

    def register(self, **entries):
        registry = {}
        for path, overrides in entries.items():
            entry = {"id": "x_00000000", "name": "x", "path": path, "port": 8090, "pid": 1,
                     "last_active": int(time.time()), "unityVersion": "6000.3.9f1"}
            entry.update(overrides)
            registry[path] = entry
        write(self.registry, json.dumps(registry))

    def run_from_outside(self):
        return self.run_entry(["start"], cwd=self.outside, script=self.copy)

    def test_the_single_live_instance_is_the_project(self):
        project = self.project("Live")
        self.register(**{project: {}})
        result = self.run_from_outside()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(norm(self.runner_report(result)["argv"][3]), norm(project))

    def test_a_status_less_entry_counts_as_running(self):
        project = self.project("Old")
        self.register(**{project: {"status": None}})
        self.assertEqual(self.run_from_outside().returncode, 0)

    def test_stale_reloading_and_stopped_instances_are_not_candidates(self):
        stale, reloading, stopped = self.project("Stale"), self.project("Reloading"), self.project("Stopped")
        self.register(**{stale: {"last_active": int(time.time()) - 600},
                         reloading: {"status": "reloading"}, stopped: {"status": "stopped"}})
        self.assert_failure(self.run_from_outside(), "PROJECT_NOT_FOUND")

    def test_a_dead_path_in_the_registry_is_not_a_candidate(self):
        live = self.project("Live")
        gone = os.path.join(self.root, "Projects", "Gone")
        self.register(**{live: {}, gone: {}})
        result = self.run_from_outside()
        self.assertEqual(norm(self.runner_report(result)["argv"][3]), norm(live))

    def test_two_live_instances_are_ambiguous(self):
        first, second = self.project("First"), self.project("Second")
        self.register(**{first: {}, second: {"port": 8091}})
        envelope = self.assert_failure(self.run_from_outside(), "PROJECT_AMBIGUOUS")
        self.assertIn(first, envelope["problems"][0]["message"])
        self.assertIn(second, envelope["problems"][0]["message"])

    def test_the_working_directory_settles_what_the_registry_cannot(self):
        first, second = self.project("First"), self.project("Second")
        self.register(**{first: {}, second: {"port": 8091}})
        result = self.run_entry(["start"], cwd=os.path.join(second, "Assets"), script=self.copy)
        self.assertEqual(norm(self.runner_report(result)["argv"][3]), norm(second))

    def test_malformed_registry_entries_are_ignored(self):
        project = self.project("Live")
        write(self.registry, json.dumps({
            "junk": "text", "nothing": None, "noport": {"path": project, "last_active": int(time.time())},
            "badport": {"path": project, "port": True, "last_active": int(time.time())},
            "nodate": {"path": project, "port": 8090},
        }))
        self.assert_failure(self.run_from_outside(), "PROJECT_NOT_FOUND")
        write(self.registry, "{not json")
        self.assert_failure(self.run_from_outside(), "PROJECT_NOT_FOUND")

    def test_nothing_anywhere_reports_project_not_found(self):
        envelope = self.assert_failure(self.run_from_outside(), "PROJECT_NOT_FOUND")
        self.assertIn("--project", envelope["problems"][0]["fix"])


class FailureTests(EntryTestCase):
    def setUp(self):
        super().setUp()
        self.module = load_entry()

    def call_main(self, argv):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = self.module.main(argv)
        return code, json.loads(out.getvalue())

    def test_old_python_gets_an_envelope(self):
        with mock.patch.object(sys, "version_info", (3, 8, 18, "final", 0)):
            code, envelope = self.call_main(["start"])
        self.assertEqual(code, 2)
        self.assertEqual(envelope["problems"][0]["code"], "PYTHON_TOO_OLD")
        self.assertEqual(envelope["command"], "start")

    def test_an_empty_command_line_names_no_command(self):
        with mock.patch.object(sys, "version_info", (3, 8, 18, "final", 0)):
            _, envelope = self.call_main([])
        self.assertIsNone(envelope["command"])

    def test_an_unexpected_exception_still_answers_with_an_envelope(self):
        with mock.patch.object(self.module, "locate_project", side_effect=RuntimeError("boom")):
            code, envelope = self.call_main(["start"])
        self.assertEqual(code, 4)
        self.assertEqual([p["code"] for p in envelope["problems"]], ["INTERNAL_ERROR"])
        self.assertIn("RuntimeError", envelope["problems"][0]["message"])
        self.assertNotIn("boom", json.dumps(envelope))

    def test_project_and_package_resolution_ignore_unreadable_files(self):
        project = make_project(self.root)
        write(os.path.join(project, "Packages", "packages-lock.json"), "{broken")
        write(os.path.join(project, "Packages", "UnitySkills", "package.json"), "[]")
        with self.assertRaises(self.module.Failure) as raised:
            self.module.resolve_package_root(project)
        self.assertEqual(raised.exception.code, "PACKAGE_NOT_FOUND")


class RunnerParityTests(EntryTestCase):
    """The entry cannot import the runner before it has found the package, so its rules are a copy; keep them equal."""

    def test_problem_texts_match_the_runners(self):
        problems = load_runner_problems()
        module = load_entry()
        for code in ("PYTHON_TOO_OLD", "PROJECT_NOT_FOUND", "PROJECT_AMBIGUOUS", "PACKAGE_NOT_FOUND", "RUNNER_NOT_FOUND"):
            with self.subTest(code):
                exit_code, message, fix = problems.BLOCKING[code]
                self.assertEqual(module.PROBLEMS[code], (message, fix))
                self.assertEqual(exit_code, module.EXIT_ENVIRONMENT)
        exit_code, message, _ = problems.BLOCKING["INTERNAL_ERROR"]
        self.assertEqual(module.PROBLEMS["INTERNAL_ERROR"][0], message)
        self.assertEqual(exit_code, module.EXIT_INTERNAL)

    def test_package_root_matches_the_runners_for_every_layout(self):
        paths = load_runner_paths()
        module = load_entry()
        for label, build in LAYOUTS:
            with self.subTest(label), tempfile.TemporaryDirectory() as temp:
                project, package = build(os.path.realpath(temp))
                self.assertEqual(norm(module.resolve_package_root(project)), norm(package))
                self.assertEqual(norm(paths.resolve_package_root(project)["root"]), norm(package))

    def test_unresolved_layouts_fail_the_same_way_in_the_runner(self):
        paths = load_runner_paths()
        module = load_entry()
        for label, build in UNRESOLVED_LAYOUTS:
            with self.subTest(label), tempfile.TemporaryDirectory() as temp:
                project = build(os.path.realpath(temp))
                with self.assertRaises(module.Failure) as ours:
                    module.resolve_package_root(project)
                with self.assertRaises(Exception) as theirs:
                    paths.resolve_package_root(project)
                self.assertEqual(ours.exception.code, "PACKAGE_NOT_FOUND")
                self.assertEqual([p["code"] for p in theirs.exception.problems], ["PACKAGE_NOT_FOUND"])


def load_runner_problems():
    path = os.path.join(RUNNER_PACKAGE, "usk_eval", "problems.py")
    if not os.path.isfile(path):
        raise unittest.SkipTest("the runner package is not in this checkout")
    saved = sys.dont_write_bytecode
    sys.dont_write_bytecode = True
    try:
        spec = importlib.util.spec_from_file_location("usk_eval_problems_under_test", path)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
    finally:
        sys.dont_write_bytecode = saved
    return module


class EvaluationDocTests(unittest.TestCase):
    """The advisory doc agents read before they run the evaluation."""

    @classmethod
    def setUpClass(cls):
        with open(EVALUATION_DOC, "r", encoding="utf-8") as handle:
            cls.text = handle.read()
        match = re.match(r"---\n(.*?)\n---\n", cls.text, re.DOTALL)
        assert match, "frontmatter missing"
        cls.frontmatter = match.group(1)

    def field(self, name):
        match = re.search(r"^%s:[ \t]*(.*)$" % name, self.frontmatter, re.MULTILINE)
        self.assertIsNotNone(match, name)
        return match.group(1).strip()

    def test_frontmatter_names_the_module_and_stays_within_the_discovery_limit(self):
        self.assertEqual(self.field("name"), "unity-evaluation")
        self.assertLessEqual(len(self.field("description")), 1024)

    def test_description_asks_for_an_explicit_evaluation_request_and_excludes_normal_work(self):
        description = self.field("description")
        for phrase in ("UnitySkills evaluation", "跑测评集", "评测集测试", "Never use it for ordinary Unity work"):
            self.assertIn(phrase, description)

    def test_doc_is_plain_lf_utf8_without_rest_call_examples(self):
        with open(EVALUATION_DOC, "rb") as handle:
            raw = handle.read()
        self.assertFalse(raw.startswith(b"\xef\xbb\xbf"), "a BOM hides the frontmatter from some agents")
        self.assertNotIn(b"\r", raw)
        self.assertNotIn("call_skill", self.text)
        self.assertNotIn("/skill/", self.text)

    def test_every_option_the_doc_names_exists_in_the_runner(self):
        cli = os.path.join(RUNNER_PACKAGE, "usk_eval", "cli.py")
        if not os.path.isfile(cli):
            self.skipTest("the runner package is not in this checkout")
        with open(cli, "r", encoding="utf-8-sig") as handle:
            declared = set(re.findall(r'add_argument\("(--[a-z-]+)"', handle.read()))
        named = set(re.findall(r"`?(--[a-z][a-z-]*)", self.text))
        self.assertTrue(named, "the doc names no option at all")
        self.assertEqual(sorted(named - declared), [])

    def test_the_doc_points_at_the_entry_script_that_ships_with_it(self):
        self.assertIn("scripts/unity_eval.py", self.text)
        self.assertTrue(os.path.isfile(ENTRY))


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
