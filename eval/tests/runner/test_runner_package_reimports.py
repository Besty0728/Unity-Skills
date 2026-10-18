"""Re-imports of unchanged plugin package files (contract 2.8 N7/S6, 5.7): Unity rebuilding the plugin's assemblies
during an attempt re-imports its scripts; while the package tree is the one of the issue those records are no
evidence, any change to the package keeps them counted, and truncated or unknown paths always count."""
import json
import os
import shutil
import unittest
from unittest import mock

from .runner_support import Bench
from usk_eval import environment as E
from usk_eval import journal as J

PREFIX = "Packages/com.besty.unity-skills"
FILES = {"package.json": '{"name": "com.besty.unity-skills", "version": "0.0.1"}\n',
         "Editor/Skills/A.cs": "class A {}\n", "Editor/Skills/A.cs.meta": "fileFormatVersion: 2\n",
         "Runtime/B.cs": "class B {}\n", "Tests~/Hidden.cs": "class Hidden {}\n", ".hidden/notes.txt": "x\n"}


def write(root, files):
    for rel, text in files.items():
        path = os.path.join(root, *rel.split("/"))
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(text)


class PackageTreeTests(unittest.TestCase):
    def setUp(self):
        self.b = Bench(self)
        self.pkg = os.path.join(self.b.tmp, "pkg")
        write(self.pkg, FILES)

    def test_only_what_unity_imports_is_hashed(self):
        tree = E.package_tree(self.pkg)
        self.assertEqual(tree["paths"], {"", "package.json", "Editor", "Editor/Skills", "Editor/Skills/A.cs",
                                         "Editor/Skills/A.cs.meta", "Runtime", "Runtime/B.cs"})
        imported = sorted(p for p in tree["paths"] if os.path.isfile(os.path.join(self.pkg, p)))
        self.assertEqual(tree["hash"], E.hash_files([(p, os.path.join(self.pkg, p)) for p in imported]))
        self.assertEqual(tree["newestMtimeNs"], max(os.stat(os.path.join(self.pkg, p)).st_mtime_ns for p in imported))
        write(self.pkg, {"Tests~/Hidden.cs": "changed\n", ".hidden/notes.txt": "changed\n"})
        self.assertEqual(E.package_tree(self.pkg), tree)
        write(self.pkg, {"Runtime/B.cs": "class B { }\n"})
        self.assertNotEqual(E.package_tree(self.pkg)["hash"], tree["hash"])

    def test_package_paths_of_records(self):
        cases = [({"kind": "imported", "assetPath": PREFIX + "/Editor/Skills/A.cs"}, "Editor/Skills/A.cs"),
                 ({"kind": "imported", "assetPath": PREFIX.replace("/", "\\") + "\\Runtime\\B.cs"}, "Runtime/B.cs"),
                 ({"kind": "imported", "assetPath": PREFIX}, ""),
                 ({"kind": "imported", "assetPath": PREFIX + "-extra/A.cs"}, None),
                 ({"kind": "imported", "assetPath": "Assets/A.cs"}, None),
                 ({"kind": "deleted", "assetPath": PREFIX + "/Editor/Skills/A.cs"}, None),
                 ({"kind": "omitted", "count": 131}, None)]
        for rec, expected in cases:
            self.assertEqual(J.package_path(rec), expected, rec)


class PackageReimportTests(unittest.TestCase):
    def setUp(self):
        self.b = Bench(self)
        self.pkg = os.path.realpath(os.path.join(self.b.tmp, "pkg"))
        write(self.pkg, FILES)
        self.point_lock_at(self.pkg)

    def point_lock_at(self, root):
        with open(os.path.join(self.b.project, "Packages", "packages-lock.json"), "w", encoding="utf-8") as handle:
            json.dump({"dependencies": {"com.besty.unity-skills": {"version": "file:" + root, "depth": 0,
                                                                   "source": "local"}}}, handle)

    def attempt(self, imported, omitted=0, before_next=None, before_submit=None):
        """One S99 attempt during which the editor itself imports `imported` (paths under the package prefix)."""
        code, env = self.b.run("start", "--lang", "en", "--tasks", "S99")
        self.assertEqual(code, 0, env)
        sid = env["data"]["sessionId"]
        if before_next:
            before_next()
        code, env = self.b.run("next", "--session", sid)
        self.assertEqual(code, 0, env)
        data = env["data"]
        self.b.solve(data)
        if imported or omitted:
            paths = [PREFIX + ("/" + p if p else "") for p in imported]
            self.b.unity.publish("file_changes", "editor", None, {
                "imported": paths, "deleted": [], "moved": [], "count": len(paths), "omitted": omitted,
                "importedTotal": len(paths) + omitted, "deletedTotal": 0, "movedTotal": 0})
        if before_submit:
            before_submit()
        code, env = self.b.run("submit", "--session", sid, "--attempt", data["attemptId"], "--answer",
                               data["answerFile"])
        self.assertEqual(code, 0, env)
        return self.b.state(sid, data["attemptId"]), self.b.result(sid, data["attemptId"])

    REBUILD = ["", "Editor", "Editor/Skills", "Editor/Skills/A.cs", "Editor/Skills/A.cs.meta", "Runtime/B.cs"]

    def test_reimports_of_an_unchanged_package_are_no_evidence(self):
        state, result = self.attempt(self.REBUILD)
        self.assertEqual((result["status"], result["reasons"]), ("pass", []))
        journal = result["evidence"]["journal"]
        self.assertEqual((journal["packageReimports"], journal["nonRest"], journal["records"]), (6, 0, 6 + 1))
        issue, judged = state["packageTree"]["atIssue"], state["packageTree"]["atSubmit"]
        self.assertEqual(issue["root"], self.pkg)
        self.assertEqual({k: judged[k] for k in issue}, issue)
        self.assertEqual((judged["unchanged"], judged["absent"]), (True, []))

    def test_a_changed_package_keeps_counting_them(self):
        state, result = self.attempt(["Editor/Skills/A.cs", "Runtime/B.cs"],
                                     before_submit=lambda: write(self.pkg, {"Editor/Skills/A.cs": "class A { }\n"}))
        self.assertEqual(result["status"], "contaminated")
        self.assertIn("non_rest_changes: 2", result["reasons"])
        self.assertEqual(result["evidence"]["journal"]["packageReimports"], 0)
        self.assertFalse(state["packageTree"]["atSubmit"]["unchanged"])
        self.assertEqual([r["id"] for r in result["score"]["assertions"] if not r["pass"]], ["A2"])

    def test_an_edit_restored_before_submit_still_counts(self):
        path = os.path.join(self.pkg, "Editor", "Skills", "A.cs")

        def edit_and_restore():
            write(self.pkg, {"Editor/Skills/A.cs": "class A { int patched; }\n"})
            write(self.pkg, {"Editor/Skills/A.cs": FILES["Editor/Skills/A.cs"]})
            later = os.stat(path).st_mtime_ns + 5 * 10 ** 9      # coarse file systems: a time surely after the issue
            os.utime(path, ns=(later, later))
        state, result = self.attempt(["Editor/Skills/A.cs"], before_submit=edit_and_restore)
        judged = state["packageTree"]["atSubmit"]
        self.assertEqual(judged["hash"], state["packageTree"]["atIssue"]["hash"])
        self.assertFalse(judged["unchanged"])
        self.assertIn("non_rest_changes: 1", result["reasons"])

    def test_files_unity_never_imports_may_change(self):
        state, result = self.attempt(["Editor/Skills/A.cs"], before_submit=lambda: write(
            self.pkg, {"Tests~/Hidden.cs": "class Hidden { }\n", ".hidden/notes.txt": "y\n", "Tests~/New.cs": "n\n"}))
        self.assertEqual((result["status"], result["evidence"]["journal"]["packageReimports"]), ("pass", 1))

    def test_truncated_and_unknown_paths_still_count(self):
        state, result = self.attempt(["Editor/Skills/A.cs", "Editor/Skills/Gone.cs"], omitted=129)
        self.assertEqual(result["status"], "contaminated")
        self.assertIn("non_rest_changes: 2", result["reasons"])           # Gone.cs and the omitted tail
        self.assertEqual(result["evidence"]["journal"]["packageReimports"], 1)
        self.assertEqual(state["packageTree"]["atSubmit"]["absent"], ["Editor/Skills/Gone.cs"])

    def test_the_tree_is_hashed_again_only_for_package_imports(self):
        with mock.patch.object(E, "package_tree", wraps=E.package_tree) as hashed:
            state, _result = self.attempt([])
            self.assertEqual(hashed.call_count, 1)
            self.assertIsNone(state["packageTree"]["atSubmit"])
            self.attempt(["Runtime/B.cs"])
            self.assertEqual(hashed.call_count, 3)

    def test_a_package_moved_elsewhere_keeps_counting(self):
        moved = self.pkg + "-moved"

        def move():
            shutil.copytree(self.pkg, moved)                   # same files, same times, another place
            self.point_lock_at(moved)
        state, result = self.attempt(["Runtime/B.cs"], before_submit=move)
        judged = state["packageTree"]["atSubmit"]
        self.assertEqual((judged["root"], judged["hash"], judged["newestMtimeNs"]),
                         (moved, state["packageTree"]["atIssue"]["hash"],
                          state["packageTree"]["atIssue"]["newestMtimeNs"]))
        self.assertIn("non_rest_changes: 1", result["reasons"])

    def test_an_unreadable_package_at_issue_keeps_counting(self):
        state, result = self.attempt(["Runtime/B.cs"], before_next=lambda: self.point_lock_at(self.pkg + "-missing"),
                                     before_submit=lambda: self.point_lock_at(self.pkg))
        self.assertIsNone(state["packageTree"]["atIssue"])
        self.assertFalse(state["packageTree"]["atSubmit"]["unchanged"])
        self.assertIn("non_rest_changes: 1", result["reasons"])


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
