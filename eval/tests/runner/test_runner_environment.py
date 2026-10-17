"""environment.py: tree hash rules, gate inputs (contract 9), dedicated marker (7.4), installed skill docs (2.8)."""
import json
import os
import re
import shutil
import tempfile
import unittest

from .runner_support import REPO
from usk_eval import environment as E


class TreeHashTests(unittest.TestCase):
    def setUp(self):
        self.root = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.root, True)

    def write(self, rel, data):
        path = os.path.join(self.root, *rel.split("/"))
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "wb") as handle:
            handle.write(data)
        return path

    def test_line_endings_bytecode_and_dotfiles_do_not_matter(self):
        self.write("a.py", b"x = 1\n")
        self.write("pack/tasks/S01/task.py", b"y = 2\n")
        before = E.tree_hash(self.root)
        self.write("a.py", b"x = 1\r\n")
        self.write("__pycache__/a.cpython-313.pyc", b"\0")
        self.write(".DS_Store", b"\0")
        self.write("pack/tasks/S01/task.pyc", b"\0")
        self.assertEqual(E.tree_hash(self.root), before)
        self.write("pack/tasks/S01/task.py", b"y = 3\n")
        self.assertNotEqual(E.tree_hash(self.root), before)

    def test_exclusions(self):
        self.write("SKILL.md", b"doc\n")
        self.write("scripts/agent_config.json", b'{"installedAt": "1"}')
        before = E.tree_hash(self.root, exclude=("scripts/agent_config.json",))
        self.write("scripts/agent_config.json", b'{"installedAt": "2"}')
        self.assertEqual(E.tree_hash(self.root, exclude=("scripts/agent_config.json",)), before)


class GateTests(unittest.TestCase):
    def setUp(self):
        self.repo = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.repo, True)
        runner = os.path.join(self.repo, "SkillsForUnity", "Evaluation~")
        for rel in ("run_eval.py", "usk_eval/cli.py", "usk_eval/report.py", "usk_eval/usage/claude.py",
                    "pack/tasks/S01/task.py", "pack/tasks/S01/prompt.en.md", "pack/tasks/S03/task.py"):
            self.write(os.path.join(runner, *rel.split("/")), "# %s\n" % rel)
        for rel in ("eval/pack/private/S01.py", "eval/pack/solutions/S01.py", "eval/cli.py", "eval/driver.py"):
            self.write(os.path.join(self.repo, *rel.split("/")), "# %s\n" % rel)

    def write(self, path, text):
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as handle:
            handle.write(text)

    def touch(self, rel, text="# changed\n"):
        self.write(os.path.join(self.repo, *rel.split("/")), text)

    def test_inputs_hash_covers_only_the_gate_inputs(self):
        base = E.gate_inputs_hash(self.repo, "S01", extra=("eval/driver.py",))
        for rel in ("SkillsForUnity/Evaluation~/usk_eval/report.py", "SkillsForUnity/Evaluation~/usk_eval/usage/claude.py",
                    "SkillsForUnity/Evaluation~/pack/tasks/S03/task.py"):
            self.touch(rel)
            self.assertEqual(E.gate_inputs_hash(self.repo, "S01", extra=("eval/driver.py",)), base, rel)
        for rel in ("SkillsForUnity/Evaluation~/usk_eval/cli.py", "SkillsForUnity/Evaluation~/pack/tasks/S01/prompt.en.md",
                    "eval/pack/private/S01.py", "eval/pack/solutions/S01.py", "eval/cli.py", "eval/driver.py",
                    "SkillsForUnity/Evaluation~/run_eval.py"):
            self.touch(rel, "# %s v2\n" % rel)
            changed = E.gate_inputs_hash(self.repo, "S01", extra=("eval/driver.py",))
            self.assertNotEqual(changed, base, rel)
            base = changed

    def test_the_gate_key_has_one_definition(self):
        found = []
        for top in (os.path.join(REPO, "SkillsForUnity", "Evaluation~"), os.path.join(REPO, "eval")):
            for folder, dirs, files in os.walk(top):
                dirs[:] = [d for d in dirs if d not in ("runs", "__pycache__") and not d.startswith(".")]
                for name in files:
                    if name.endswith(".py"):
                        with open(os.path.join(folder, name), "r", encoding="utf-8-sig") as handle:
                            if re.search(r"^def gate_key\(", handle.read(), re.M):
                                found.append(os.path.relpath(os.path.join(folder, name), REPO).replace(os.sep, "/"))
        self.assertEqual(found, ["eval/selftest3.py"])


class MarkerAndDocsTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.tmp, True)

    def test_marker(self):
        path = os.path.join(self.tmp, "UnitySkillsEvaluation.json")
        self.assertEqual(E.marker(path)["environment"], "user-project")
        for content, dedicated in (({"schema": 1, "kind": "Other"}, False), ({"schema": "1", "kind": E.MARKER_KIND}, False),
                                   ({"schema": 1, "kind": E.MARKER_KIND, "createdWith": "3.0.0"}, True)):
            with open(path, "w", encoding="utf-8") as handle:
                json.dump(content, handle)
            found = E.marker(path)
            self.assertEqual(found["dedicated"], dedicated, content)
            self.assertEqual(found["markerSha256"] is not None, dedicated)

    def test_skill_docs_are_found_once_per_real_directory(self):
        project = os.path.join(self.tmp, "Proj")
        home = os.path.join(self.tmp, "home")
        shared = os.path.join(home, ".agents", "skills", "unity-skills")
        os.makedirs(os.path.join(shared, "scripts"))
        with open(os.path.join(shared, "scripts", "agent_config.json"), "w") as handle:
            json.dump({"agentId": "codex", "version": "3.0.0", "installedAt": "x"}, handle)
        os.makedirs(os.path.join(project, ".agents", "skills"))
        os.symlink(shared, os.path.join(project, ".agents", "skills", "unity-skills"))
        docs = E.skill_docs(project, {"HOME": home})
        self.assertEqual(len(docs), 1)
        self.assertEqual((docs[0]["agentId"], docs[0]["version"]), ("codex", "3.0.0"))
        self.assertEqual(len(docs[0]["treeHash"]), 64)


class DriftTests(unittest.TestCase):
    def test_drift_lists_changed_keys_only(self):
        before = {"currentMode": "bypass", "pid": 1, "version": "3.0.0", "unrelated": 1}
        after = {"currentMode": "auto", "pid": 2, "version": "3.0.0", "unrelated": 2}
        self.assertEqual(E.drift(before, after), {"currentMode": ["bypass", "auto"], "pid": [1, 2]})


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
