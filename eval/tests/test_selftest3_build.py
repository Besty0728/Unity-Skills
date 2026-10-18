"""The plugin build behind a v3 gate (contract 9): /health packageRoot must be this checkout's package unless
--allow-other-package, no .cs/.asmdef in it may be newer than assembliesLoadedAtUtc, and each entry records the build.
Offline: the fake editor reports the two fields, PACKAGE_DIR points at a scratch package."""
import datetime
import os
import sys
import time
import unittest
from unittest import mock

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
sys.path.insert(0, os.path.dirname(HERE))

from test_selftest3 import Fixture  # noqa: E402
from runner.runner_support import store  # noqa: E402
import cli  # noqa: E402
import selftest3  # noqa: E402


def iso(epoch):
    return store.iso(datetime.datetime.fromtimestamp(epoch, datetime.timezone.utc))


def write(root, files):
    for rel, text in files.items():
        path = os.path.join(root, *rel.split("/"))
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as handle:
            handle.write(text)


def touch(path, epoch):
    os.utime(path, (epoch, epoch))


class BuildIdentityTests(unittest.TestCase):
    def setUp(self):
        self.fx = Fixture(self)
        self.pkg = os.path.join(self.fx.b.tmp, "checkout", "SkillsForUnity")
        write(self.pkg, {"Editor/A.cs": "class A {}\n", "Editor/Skills.asmdef": "{}\n", "Tests~/T.cs": "class T {}\n",
                         ".hidden/H.cs": "class H {}\n", "package.json": "{}\n"})
        patcher = mock.patch.object(selftest3, "PACKAGE_DIR", self.pkg)
        patcher.start()
        self.addCleanup(patcher.stop)
        self.loaded_at = time.time() + 60          # the editor loaded its assemblies after every source was saved
        self.report(self.pkg, iso(self.loaded_at))

    def report(self, root, loaded):
        self.fx.b.unity.health_extra = {"packageRoot": root, "assembliesLoadedAtUtc": loaded}

    def entry(self):
        (entry,) = self.fx.gate()["gates"].values()
        return entry

    def test_the_build_is_recorded_with_the_entry_not_in_the_key(self):
        self.assertEqual(self.fx.selftest(), 0, self.fx.text)
        entry = self.entry()
        self.assertEqual((entry["buildIdentity"], entry["packageRoot"], entry["assembliesLoadedAtUtc"]),
                         ("verified", self.pkg, iso(self.loaded_at)))
        (key,) = self.fx.gate()["gates"]
        self.assertEqual(key, selftest3.gate_key("E01", 1, "6000.3.9f1", entry["pluginVersion"], "URP",
                                                 entry["gateInputsHash"]))
        self.assertIn("build: %s" % self.pkg, self.fx.text)

    def test_another_package_is_refused_unless_allowed(self):
        other = os.path.join(self.fx.b.tmp, "elsewhere", "SkillsForUnity")
        write(other, {"Editor/A.cs": "class A {}\n"})
        self.report(other, iso(self.loaded_at))
        self.assertEqual(self.fx.selftest(), 2, self.fx.text)
        self.assertIn("the editor loads UnitySkills from %s, not from this checkout's %s" % (other, self.pkg),
                      self.fx.text)
        self.assertIn("--allow-other-package", self.fx.text)
        self.assertFalse(os.path.exists(self.fx.gate_file))
        self.assertEqual(self.fx.selftest(allow_other_package=True), 0, self.fx.text)
        self.assertEqual((self.entry()["buildIdentity"], self.entry()["packageRoot"]), ("override", other))

    def test_sources_newer_than_the_loaded_assemblies_are_refused(self):
        touch(os.path.join(self.pkg, "Tests~", "T.cs"), self.loaded_at + 10)       # never compiled: ignored
        touch(os.path.join(self.pkg, ".hidden", "H.cs"), self.loaded_at + 10)
        touch(os.path.join(self.pkg, "package.json"), self.loaded_at + 10)
        for rel in ("Editor/Skills.asmdef", "Editor/A.cs"):
            touch(os.path.join(self.pkg, *rel.split("/")), self.loaded_at + 10)
            self.assertEqual(self.fx.selftest(), 2, self.fx.text)
            self.assertIn("no gate can be recorded: %s changed after the editor loaded its assemblies" % rel,
                          self.fx.text)
            self.assertFalse(os.path.exists(self.fx.gate_file))
            touch(os.path.join(self.pkg, *rel.split("/")), self.loaded_at - 10)
        self.assertEqual(self.fx.selftest(), 0, self.fx.text)

    def test_an_edit_during_the_run_leaves_the_gate_unrecorded(self):
        original = selftest3.Selftest.run_task

        def run_task(test, task_id):
            out = original(test, task_id)
            touch(os.path.join(self.pkg, "Editor", "A.cs"), self.loaded_at + 10)
            return out
        with mock.patch.object(selftest3.Selftest, "run_task", run_task):
            self.assertEqual(self.fx.selftest(), 2, self.fx.text)
        self.assertIn("gate E01 not recorded: Editor/A.cs changed after the editor loaded its assemblies",
                      self.fx.text)
        self.assertFalse(os.path.exists(self.fx.gate_file))

    def test_an_editor_without_the_fields_is_marked_unavailable(self):
        for extra in ({}, {"packageRoot": self.pkg, "assembliesLoadedAtUtc": "0001-01-01T00:00:00.0000000Z"}):
            self.fx.b.unity.health_extra = extra
            self.assertEqual(self.fx.selftest(), 0, self.fx.text)
            self.assertIn("warning: the editor reports no packageRoot/assembliesLoadedAtUtc", self.fx.text)
            self.assertEqual(self.entry()["buildIdentity"], "unavailable")

    def test_a_run_that_records_nothing_only_warns(self):
        self.report(os.path.join(self.fx.b.tmp, "elsewhere"), iso(self.loaded_at))
        self.assertEqual(self.fx.selftest(case=["alternative"]), 0, self.fx.text)
        self.assertIn("warning: the editor loads UnitySkills from", self.fx.text)

    def test_unity_timestamps_and_folder_spelling(self):
        health = {"packageRoot": self.pkg + os.sep, "assembliesLoadedAtUtc": "2999-10-17T05:48:48.1234567Z"}
        build, problem = selftest3.build_identity(health, self.pkg)
        self.assertEqual((build["buildIdentity"], problem), ("verified", None))
        build, problem = selftest3.build_identity(dict(health, assembliesLoadedAtUtc="garbage"), self.pkg)
        self.assertEqual((build, problem), (selftest3.UNAVAILABLE_BUILD, None))


class TaskOptionTests(unittest.TestCase):
    def test_repeated_task_options_accumulate(self):
        seen = []
        for name in ("cmd_selftest3", "cmd_gate3", "cmd_suite3"):
            patcher = mock.patch.object(cli, name, lambda args: seen.append(selftest3.split_tasks(args.task)) or 0)
            patcher.start()
            self.addCleanup(patcher.stop)
        cli.main(["selftest3", "--task", "OC1", "--task", "OC3,OC5"])
        cli.main(["gate3", "--task", "A", "B", "--task", "C"])
        cli.main(["suite3", "--suite", "s", "--task", "A", "--task", "B"])
        cli.main(["gate3"])
        self.assertEqual(seen, [["OC1", "OC3", "OC5"], ["A", "B", "C"], ["A", "B"], []])


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
