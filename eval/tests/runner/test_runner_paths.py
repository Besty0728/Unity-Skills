"""paths.py / instance.py: project discovery order, package-root rules, registry identity (contract 1.4, 1.5, 2.1)."""
import json
import os
import shutil
import tempfile
import time
import unittest

from .runner_support import Bench, expected_iid, make_project
from usk_eval import instance, paths
from usk_eval.problems import ProblemError

PKG = "com.besty.unity-skills"


def package(directory, name=PKG):
    os.makedirs(directory, exist_ok=True)
    with open(os.path.join(directory, "package.json"), "w") as handle:
        json.dump({"name": name, "version": "3.0.0"}, handle)
    return directory


class ProjectLocationTests(unittest.TestCase):
    def setUp(self):
        self.tmp = os.path.realpath(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.project = make_project(self.tmp)
        self.registry = os.path.join(self.tmp, "registry.json")
        self.env = {"USK_EVAL_REGISTRY": self.registry}

    def registry_with(self, *projects, last_active=None):
        data = {}
        for index, project in enumerate(projects):
            data[project] = {"id": expected_iid(project), "path": project, "port": 8090 + index, "pid": os.getpid(),
                             "last_active": int(last_active or time.time()), "status": "running"}
        with open(self.registry, "w") as handle:
            json.dump(data, handle)

    def locate(self, arg=None, env=None, cwd=None, runner_dir="/nowhere/Evaluation~"):
        merged = dict(self.env)
        merged.update(env or {})
        return paths.locate_project(arg, merged, cwd or self.tmp, runner_dir=runner_dir)

    def test_explicit_argument_and_environment(self):
        self.assertEqual(self.locate(self.project), self.project)
        self.assertEqual(self.locate(env={"USK_EVAL_PROJECT": self.project}), self.project)
        with self.assertRaises(ProblemError) as caught:
            self.locate(self.tmp)
        self.assertEqual(caught.exception.problems[0]["code"], "PROJECT_NOT_FOUND")

    def test_runner_location_inside_the_project(self):
        cached = os.path.join(self.project, "Library", "PackageCache", PKG + "@58d3339d90", "Evaluation~")
        embedded = os.path.join(self.project, "Packages", PKG, "Evaluation~")
        self.assertEqual(self.locate(runner_dir=cached), self.project)
        self.assertEqual(self.locate(runner_dir=embedded), self.project)

    def test_walk_up_from_cwd(self):
        nested = os.path.join(self.project, "Assets", "Scenes")
        self.assertEqual(self.locate(cwd=nested), self.project)

    def test_registry_single_live_entry_or_ambiguity(self):
        self.registry_with(self.project)
        self.assertEqual(self.locate(), self.project)
        other = make_project(os.path.join(self.tmp, "second"))
        self.registry_with(self.project, other)
        with self.assertRaises(ProblemError) as caught:
            self.locate()
        self.assertEqual(caught.exception.problems[0]["code"], "PROJECT_AMBIGUOUS")
        self.registry_with(self.project, other, last_active=time.time() - 600)
        with self.assertRaises(ProblemError) as caught:
            self.locate()
        self.assertEqual(caught.exception.problems[0]["code"], "PROJECT_NOT_FOUND")


class PackageRootTests(unittest.TestCase):
    def setUp(self):
        self.tmp = os.path.realpath(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.project = make_project(self.tmp)

    def lock(self, entry):
        with open(os.path.join(self.project, "Packages", "packages-lock.json"), "w") as handle:
            json.dump({"dependencies": {PKG: entry} if entry else {}}, handle)

    def test_local_relative_and_absolute(self):
        absolute = package(os.path.join(self.tmp, "src", "SkillsForUnity"))
        self.lock({"version": "file:" + absolute, "source": "local"})
        self.assertEqual(paths.resolve_package_root(self.project)["root"], absolute)
        package(os.path.join(self.project, "LocalPkgs", "Skills"))
        self.lock({"version": "file:../LocalPkgs/Skills", "source": "local"})
        found = paths.resolve_package_root(self.project)
        self.assertEqual((found["root"], found["source"]), (os.path.join(self.project, "LocalPkgs", "Skills"), "local"))

    def test_embedded(self):
        root = package(os.path.join(self.project, "Packages", "UnitySkills"))
        package(os.path.join(self.project, "Packages", "Other"), name="com.other")
        self.lock({"version": "3.0.0", "source": "embedded"})
        self.assertEqual(paths.resolve_package_root(self.project)["root"], root)
        self.lock(None)
        self.assertEqual(paths.resolve_package_root(self.project)["source"], "embedded")

    def test_cache_candidates(self):
        cache = os.path.join(self.project, "Library", "PackageCache")
        first = package(os.path.join(cache, PKG + "@58d3339d90"))
        self.lock({"version": "https://github.com/x.git", "source": "git", "hash": "58d3339d90aa"})
        self.assertEqual(paths.resolve_package_root(self.project)["root"], first)
        second = package(os.path.join(cache, PKG + "@54357c6cfac1"))
        found = paths.resolve_package_root(self.project)
        self.assertEqual((found["root"], found["notices"]), (first, []))      # suffix is a prefix of the lock hash
        self.lock({"version": "https://github.com/x.git", "source": "git", "hash": "c0433593ffff"})
        os.utime(os.path.join(second, "package.json"), (time.time() + 60, time.time() + 60))
        found = paths.resolve_package_root(self.project)
        self.assertEqual(found["root"], second)
        self.assertEqual(found["notices"][0]["code"], "PACKAGE_ROOT_AMBIGUOUS")
        self.assertEqual(found["notices"][0]["args"], [second])

    def test_missing(self):
        self.lock({"version": "https://github.com/x.git", "source": "git", "hash": "aa"})
        with self.assertRaises(ProblemError) as caught:
            paths.resolve_package_root(self.project)
        self.assertEqual(caught.exception.problems[0]["code"], "PACKAGE_NOT_FOUND")


class InstanceTests(unittest.TestCase):
    def setUp(self):
        self.bench = Bench(self)

    def locate(self, **kwargs):
        kwargs.setdefault("wait_s", 0.5)
        return instance.locate(self.bench.project, self.bench.env, "unity-skills-eval/test", poll_s=0.05, **kwargs)

    def code(self):
        with self.assertRaises(ProblemError) as caught:
            self.locate()
        return caught.exception.problems[0]["code"]

    def test_identity_is_verified(self):
        found = self.locate()
        self.assertEqual((found.instance_id, found.port), (self.bench.unity.instance_id, self.bench.unity.port))

    def test_expected_id_uses_the_path_unity_reports(self):
        link = os.path.join(self.bench.tmp, "linked")
        os.symlink(self.bench.project, link)
        self.bench.unity.instance_id = expected_iid(link)
        self.bench.write_registry(path=link)
        self.assertEqual(self.locate().instance_id, expected_iid(link))

    def test_mismatch_stale_and_missing(self):
        self.bench.unity.instance_id = "Other_00000000"
        self.assertEqual(self.code(), "INSTANCE_MISMATCH")
        self.bench.unity.instance_id = expected_iid(self.bench.project)
        self.bench.write_registry(iid="Wrong_11111111")
        self.assertEqual(self.code(), "INSTANCE_MISMATCH")
        self.bench.write_registry(last_active=time.time() - 600)
        self.assertEqual(self.code(), "INSTANCE_NOT_RUNNING")
        os.remove(self.bench.registry)
        self.assertEqual(self.code(), "INSTANCE_NOT_RUNNING")

    def test_an_empty_or_cut_short_health_answer_is_waited_for(self):
        self.bench.unity.spoil["/health"] = ["cut", "empty", "garbled"]
        self.assertEqual(self.locate(wait_s=5).port, self.bench.unity.port)
        self.assertEqual(self.bench.unity.spoil["/health"], [])
        self.bench.unity.spoil["/health"] = ["cut"] * 1000
        self.assertEqual(self.code(), "REST_UNREACHABLE")

    def test_reloading_entry_is_waited_for(self):
        self.bench.write_registry(status="reloading")
        self.assertEqual(self.code(), "INSTANCE_NOT_RUNNING")
        self.bench.unity.http_status = [503, 503]
        self.bench.write_registry()
        self.assertEqual(self.locate(wait_s=5).port, self.bench.unity.port)


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
