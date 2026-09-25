#!/usr/bin/env python3
"""Tests for the CI check scripts: each must pass a small valid tree and fail the cases it exists to catch,
including the silent one -- a walk that scans nothing, or misses files git tracks, must not print "passed".

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


def run_script(name: str, repo: str) -> subprocess.CompletedProcess:
    return subprocess.run([sys.executable, os.path.join(SCRIPTS, name), repo], capture_output=True, text=True)


def write(repo: str, rel: str, text: str) -> None:
    path = os.path.join(repo, rel)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)


def meta(guid: str) -> str:
    return f"fileFormatVersion: 2\nguid: {guid}\n"


def git_init(repo: str) -> None:
    for args in (["init", "-q"], ["add", "-A"], ["-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "fixture"]):
        subprocess.run(["git", "-C", repo, *args], check=True, capture_output=True)


class MetaCheckTests(unittest.TestCase):
    def test_valid_tree_passes(self):
        with tempfile.TemporaryDirectory() as repo:
            write(repo, "SkillsForUnity/Editor/A.cs", "class A {}\n")
            write(repo, "SkillsForUnity/Editor/A.cs.meta", meta("0" * 31 + "1"))
            result = run_script("check_meta_files.py", repo)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_empty_scan_fails(self):
        with tempfile.TemporaryDirectory() as repo:
            os.makedirs(os.path.join(repo, "SkillsForUnity"))
            result = run_script("check_meta_files.py", repo)
            self.assertNotEqual(result.returncode, 0, "scanning nothing must not pass")
            self.assertIn("scanned 0 files", result.stdout)

    def test_walk_missing_tracked_files_fails(self):
        with tempfile.TemporaryDirectory() as repo:
            write(repo, "SkillsForUnity/Editor/A.cs", "class A {}\n")
            write(repo, "SkillsForUnity/Editor/A.cs.meta", meta("0" * 31 + "1"))
            # Tracked by git, but inside a directory name the walker excludes.
            write(repo, "SkillsForUnity/Temp/B.cs", "class B {}\n")
            write(repo, "SkillsForUnity/Temp/B.cs.meta", meta("0" * 31 + "2"))
            git_init(repo)
            result = run_script("check_meta_files.py", repo)
            self.assertNotEqual(result.returncode, 0, result.stdout)
            self.assertIn("git tracks", result.stdout)

    def test_missing_meta_fails(self):
        with tempfile.TemporaryDirectory() as repo:
            write(repo, "SkillsForUnity/Editor/A.cs", "class A {}\n")
            self.assertNotEqual(run_script("check_meta_files.py", repo).returncode, 0)

    def test_duplicate_guid_fails(self):
        with tempfile.TemporaryDirectory() as repo:
            for name in ("A", "B"):
                write(repo, f"SkillsForUnity/Editor/{name}.cs", "class X {}\n")
                write(repo, f"SkillsForUnity/Editor/{name}.cs.meta", meta("a" * 32))
            self.assertNotEqual(run_script("check_meta_files.py", repo).returncode, 0)


class LocaleCheckTests(unittest.TestCase):
    @staticmethod
    def locales(repo: str, keys: dict) -> None:
        for name in ("en.json", "zh-CN.json", "ru.json"):
            write(repo, f"SkillsForUnity/Editor/Locales/{name}", json.dumps(keys))

    def test_valid_tree_passes(self):
        with tempfile.TemporaryDirectory() as repo:
            self.locales(repo, {"greeting": "hi"})
            write(repo, "SkillsForUnity/Editor/UI/A.cs", 'var s = SkillsLocalization.Get("greeting");\n')
            result = run_script("check_locales.py", repo)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_no_key_references_fails(self):
        with tempfile.TemporaryDirectory() as repo:
            self.locales(repo, {"greeting": "hi"})
            write(repo, "SkillsForUnity/Editor/UI/A.cs", "class A {}\n")
            result = run_script("check_locales.py", repo)
            self.assertNotEqual(result.returncode, 0, "finding no references at all means the matcher broke")

    def test_no_cs_files_fails(self):
        with tempfile.TemporaryDirectory() as repo:
            self.locales(repo, {"greeting": "hi"})
            self.assertNotEqual(run_script("check_locales.py", repo).returncode, 0)

    def test_missing_key_fails(self):
        with tempfile.TemporaryDirectory() as repo:
            self.locales(repo, {"greeting": "hi"})
            write(repo, "SkillsForUnity/Editor/UI/A.cs", 'var s = SkillsLocalization.Get("farewell");\n')
            self.assertNotEqual(run_script("check_locales.py", repo).returncode, 0)

    def test_misaligned_locales_fail(self):
        with tempfile.TemporaryDirectory() as repo:
            self.locales(repo, {"greeting": "hi"})
            write(repo, "SkillsForUnity/Editor/Locales/ru.json", json.dumps({}))
            write(repo, "SkillsForUnity/Editor/UI/A.cs", 'var s = SkillsLocalization.Get("greeting");\n')
            self.assertNotEqual(run_script("check_locales.py", repo).returncode, 0)


class FrontmatterCheckTests(unittest.TestCase):
    def setUp(self):
        try:
            import yaml  # noqa: F401
        except ImportError:
            self.skipTest("PyYAML is not installed for this interpreter")

    def test_no_skill_docs_fails(self):
        with tempfile.TemporaryDirectory() as repo:
            self.assertNotEqual(run_script("check_skill_frontmatter.py", repo).returncode, 0)

    def test_oversized_description_fails(self):
        with tempfile.TemporaryDirectory() as repo:
            write(repo, "SkillsForUnity/unity-skills~/SKILL.md", "---\nname: x\ndescription: " + "d" * 1025 + "\n---\n")
            self.assertNotEqual(run_script("check_skill_frontmatter.py", repo).returncode, 0)

    def test_valid_doc_passes(self):
        with tempfile.TemporaryDirectory() as repo:
            write(repo, "SkillsForUnity/unity-skills~/SKILL.md", "---\nname: x\ndescription: short\n---\nbody\n")
            result = run_script("check_skill_frontmatter.py", repo)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)


class ProjectVersionCheckTests(unittest.TestCase):
    @staticmethod
    def anchors(repo: str, versions: dict) -> None:
        write(repo, "SkillsForUnity/Editor/Skills/SkillsLogger.cs",
              f'    public const string Version = "{versions["logger"]}";\n')
        write(repo, "SkillsForUnity/package.json", json.dumps({"version": versions["package"]}))
        write(repo, "SkillsForUnity/unity-skills~/scripts/unity_skills.py", f'__version__ = "{versions["python"]}"\n')
        write(repo, "AGENTS.md", f"| Version | {versions['agents']} |\n")
        write(repo, "CHANGELOG.md", f"# Changelog\n\n## [Unreleased]\n\n## [{versions['changelog']}] - 2026-01-01\n")

    def test_consistent_anchors_pass(self):
        with tempfile.TemporaryDirectory() as repo:
            self.anchors(repo, dict.fromkeys(("logger", "package", "python", "agents", "changelog"), "1.2.3"))
            result = run_script("check_project_version.py", repo)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_one_drifting_anchor_fails(self):
        with tempfile.TemporaryDirectory() as repo:
            versions = dict.fromkeys(("logger", "package", "python", "agents", "changelog"), "1.2.3")
            versions["python"] = "1.2.4"
            self.anchors(repo, versions)
            self.assertNotEqual(run_script("check_project_version.py", repo).returncode, 0)


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
