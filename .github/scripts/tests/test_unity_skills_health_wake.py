#!/usr/bin/env python3
"""Tests for wait_for_health's one-time wake: after wake_after seconds without /health, the Editor that has the
project open is brought to the front once; import workers, other projects and wake_after=0 are left alone.

    python3 -m unittest discover -s .github/scripts/tests -t .github/scripts/tests
"""
from __future__ import annotations

import importlib.util
import os
import subprocess
import sys
import types
import unittest
from unittest import mock

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
CLI = os.path.join(REPO, "SkillsForUnity", "unity-skills~", "scripts", "unity_skills.py")


def load_cli():
    try:
        import requests  # noqa: F401
    except ImportError:
        sys.modules["requests"] = types.ModuleType("requests")
    spec = importlib.util.spec_from_file_location("unity_skills_under_test", CLI)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


PS = """\
  101 /Applications/Unity/Hub/Editor/6000.3.9f1/Unity.app/Contents/MacOS/Unity -projectpath /Users/me/Game -logFile x.log
  102 /Applications/Unity/Hub/Editor/6000.3.9f1/Unity.app/Contents/MacOS/Unity -adb2 -batchMode -name AssetImportWorker0 -projectPath /Users/me/Game -logFile w.log
  103 /Applications/Unity/Hub/Editor/6000.3.9f1/Unity.app/Contents/MacOS/Unity -projectPath /Users/me/My Game -useHub
  104 /Applications/Unity/Hub/Editor/2022.3.62f2/Unity.app/Contents/MacOS/Unity -projectpath /Users/me/Game2
  105 /bin/zsh -c echo -projectpath
"""


class EditorPidsTests(unittest.TestCase):
    def setUp(self):
        self.cli = load_cli()
        self.ps = mock.patch.object(self.cli.subprocess, "run",
                                    return_value=subprocess.CompletedProcess([], 0, stdout=PS, stderr=""))
        self.ps.start()
        self.addCleanup(self.ps.stop)
        self.real = mock.patch.object(self.cli.os.path, "realpath", side_effect=lambda p: p)
        self.real.start()
        self.addCleanup(self.real.stop)

    @unittest.skipIf(os.name == "nt", "the process scan is macOS / Linux only")
    def test_finds_the_editor_but_not_its_import_worker(self):
        self.assertEqual(self.cli._editor_pids("/Users/me/Game"), [101])

    @unittest.skipIf(os.name == "nt", "the process scan is macOS / Linux only")
    def test_a_path_with_spaces_and_either_flag_case_matches(self):
        self.assertEqual(self.cli._editor_pids("/Users/me/My Game"), [103])

    @unittest.skipIf(os.name == "nt", "the process scan is macOS / Linux only")
    def test_another_project_is_not_matched(self):
        self.assertEqual(self.cli._editor_pids("/Users/me/Other"), [])


class WakeTests(unittest.TestCase):
    def setUp(self):
        self.cli = load_cli()
        self.now = [0.0]
        patches = [
            mock.patch.object(self.cli.time, "time", side_effect=lambda: self.now[0]),
            mock.patch.object(self.cli.time, "sleep", side_effect=self.tick),
            mock.patch.object(self.cli, "_get_default_client", side_effect=ConnectionError("no server")),
            mock.patch.object(self.cli, "_editor_pids", return_value=[101]),
            mock.patch.object(self.cli, "_bring_editor_to_front", return_value=True),
        ]
        self.mocks = [p.start() for p in patches]
        for p in patches:
            self.addCleanup(p.stop)
        self.front = self.mocks[4]

    def tick(self, seconds):
        self.now[0] += seconds

    def test_the_editor_is_fronted_once_after_wake_after(self):
        self.assertIsNone(self.cli.wait_for_health(timeout=200, check_interval=5, project_root="/p", wake_after=45))
        self.front.assert_called_once_with(101)

    def test_zero_turns_the_wake_off(self):
        self.assertIsNone(self.cli.wait_for_health(timeout=200, check_interval=5, project_root="/p", wake_after=0))
        self.front.assert_not_called()

    def test_no_wake_before_wake_after(self):
        self.assertIsNone(self.cli.wait_for_health(timeout=30, check_interval=5, project_root="/p", wake_after=45))
        self.front.assert_not_called()


class BringToFrontTests(unittest.TestCase):
    def test_other_platforms_do_nothing(self):
        cli = load_cli()
        with mock.patch.object(cli.sys, "platform", "linux"), mock.patch.object(cli.subprocess, "run") as run:
            self.assertFalse(cli._bring_editor_to_front(101))
        run.assert_not_called()


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
