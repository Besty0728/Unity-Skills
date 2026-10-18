"""Re-imports of unchanged files under the run's asset folder (contract 2.8 N8/S6, 2.9): Unity importing a prefab again
because a script it uses changed type is no write. A file that existed at issue and has the same bytes at submit is
explained; created, changed, deleted and moved files, a changed .meta, the agent's own REST imports and truncated
paths keep counting."""
import hashlib
import json
import os
import unittest
from unittest import mock

from . import runner_support as RS
from .runner_support import Bench, SOLVER
from usk_eval import environment as E
from usk_eval import journal as J
from usk_eval import rest
from usk_eval.rest import RUNNER_AGENT

FILES = {"Prefabs/Bot.prefab": b"%YAML 1.1\n--- !u!1 &1\nBot\n",
         "Prefabs/Bot.prefab.meta": b"fileFormatVersion: 2\nguid: 0123456789abcdef0123456789abcdef\n",
         "Prefabs/Bot_Armored.prefab": b"%YAML 1.1\n--- !u!1 &2\nBot_Armored\n",
         "Prefabs/Bot_Armored.prefab.meta": b"fileFormatVersion: 2\nguid: fedcba9876543210fedcba9876543210\n",
         "Scripts/Patroller.cs": b"class Patroller {}\n",
         "Scripts/Patroller.cs.meta": b"fileFormatVersion: 2\nguid: 00112233445566778899aabbccddeeff\n"}


class FilesTask(RS.S99):
    """S99 whose build also writes FILES under the run's asset folder, as a fixture's skills would."""
    id = "S97"

    def build(self, ctx):
        handles = RS.S99.build(self, ctx)
        for rel, data in FILES.items():
            path = os.path.join(ctx.project_path, *ctx.asset(rel).split("/"))
            os.makedirs(os.path.dirname(path), exist_ok=True)
            with open(path, "wb") as handle:
                handle.write(data)
        return handles


def write(base, rel, data):
    path = os.path.join(base, *rel.split("/"))
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as handle:
        handle.write(data)


class HashTests(unittest.TestCase):
    def setUp(self):
        self.b = Bench(self)
        self.dir = os.path.join(self.b.tmp, "A")
        for rel, data in FILES.items():
            write(self.dir, rel, data)

    def test_every_file_is_hashed_by_its_bytes_with_its_meta(self):
        hashes = E.asset_hashes(self.dir)
        self.assertEqual(sorted(hashes), sorted(FILES))
        self.assertEqual(hashes["Prefabs/Bot.prefab"], E.sha256_file(os.path.join(self.dir, "Prefabs", "Bot.prefab")))
        self.assertEqual(len(set(hashes.values())), len(FILES))
        write(self.dir, "Prefabs/Bot.prefab", FILES["Prefabs/Bot.prefab"].replace(b"\n", b"\r\n"))
        self.assertNotEqual(E.asset_hashes(self.dir)["Prefabs/Bot.prefab"], hashes["Prefabs/Bot.prefab"])  # no LF folding

    def test_nothing_is_skipped_as_hidden(self):
        write(self.dir, ".hidden/notes.txt", b"x\n")
        write(self.dir, "Tools~/tool.cs", b"y\n")
        self.assertIn(".hidden/notes.txt", E.asset_hashes(self.dir))
        self.assertIn("Tools~/tool.cs", E.asset_hashes(self.dir))

    def test_limit_missing_folder_and_unreadable_files(self):
        self.assertIsNone(E.asset_hashes(os.path.join(self.dir, "nowhere")))
        self.assertIsNone(E.asset_hashes(self.dir, limit=len(FILES) - 1))
        self.assertEqual(len(E.asset_hashes(self.dir, limit=len(FILES))), len(FILES))
        real = E.sha256_file

        def unreadable(path):
            if path.endswith("Bot.prefab"):
                raise PermissionError(path)
            return real(path)
        with mock.patch.object(E, "sha256_file", unreadable):
            hashes = E.asset_hashes(self.dir)
        self.assertNotIn("Prefabs/Bot.prefab", hashes)
        self.assertEqual(len(hashes), len(FILES) - 1)


class RecordTests(unittest.TestCase):
    A = "Assets/__UnitySkillsEval/r20261018T000000abcdef"

    def rec(self, path, kind="imported", source="editor", agent=None, origin="attempt"):
        return {"kind": kind, "assetPath": path, "source": source, "agent": agent, "origin": origin}

    def test_which_records_name_a_file_to_judge(self):
        A = self.A
        cases = [(self.rec(A + "/Prefabs/Bot.prefab"), "Prefabs/Bot.prefab"),
                 (self.rec(A + "/Prefabs/Bot.prefab.meta"), "Prefabs/Bot.prefab"),
                 (self.rec((A + "/Prefabs/Bot.prefab").replace("/", "\\")), "Prefabs/Bot.prefab"),
                 (self.rec(A + "/Prefabs/Bot.prefab", source="rest", agent=RUNNER_AGENT, origin="refresh"),
                  "Prefabs/Bot.prefab"),
                 (self.rec(A + "/Prefabs/Bot.prefab", source="rest", agent=SOLVER), None),
                 (self.rec(A + "/Prefabs/Bot.prefab", source="rest", agent="mixed"), None),
                 (self.rec(A + "/Prefabs/Bot.prefab", source="rest", agent="unknown"), None),
                 (self.rec(A + "/Prefabs/Bot.prefab", kind="deleted"), None),
                 (self.rec(A + "/Prefabs/Bot.prefab", kind="moved"), None),
                 (self.rec(A), None), (self.rec(A + "-copy/Bot.prefab"), None),
                 (self.rec("Assets/Other/Bot.prefab"), None), ({"kind": "omitted", "count": 3}, None)]
        for record, expected in cases:
            self.assertEqual(J.asset_reimport_path(record, A), expected, record)
        self.assertIsNone(J.asset_reimport_path(self.rec(A + "/x"), ""))

    def test_split_keeps_everything_that_is_not_judged_unchanged(self):
        A = self.A
        bot, extra = self.rec(A + "/Prefabs/Bot.prefab"), self.rec(A + "/Prefabs/Extra.prefab")
        agent = self.rec(A + "/Prefabs/Bot.prefab", source="rest", agent=SOLVER)
        deleted = self.rec(A + "/Prefabs/Bot.prefab", kind="deleted")
        records = [bot, extra, agent, deleted, {"kind": "omitted", "count": 2}]
        judged = {"checked": 2, "unchanged": ["Prefabs/Bot.prefab"], "changed": ["Prefabs/Extra.prefab"]}
        self.assertEqual(J.split_asset_reimports(records, judged, A), ([extra, agent, deleted, records[4]], [bot]))
        for nothing in (None, {}, {"unchanged": []}):
            self.assertEqual(J.split_asset_reimports(records, nothing, A), (records, []))

    def test_combined_split_and_states_that_predate_the_judgement(self):
        A = self.A
        plugin = {"kind": "imported", "assetPath": J.PACKAGE_PREFIX + "/Editor/A.cs", "source": "editor"}
        bot = self.rec(A + "/Prefabs/Bot.prefab")
        other = self.rec(A + "/Prefabs/Other.prefab")
        state = {"scope": {"assetRoot": A},
                 "packageTree": {"atSubmit": {"unchanged": True, "absent": []}},
                 "assetTree": {"atSubmit": {"unchanged": ["Prefabs/Bot.prefab"]}}}
        self.assertEqual(J.split_reimports([plugin, bot, other], state), ([other], [plugin], [bot]))
        self.assertEqual(J.split_reimports([plugin, bot, other], {}), ([plugin, bot, other], [], []))
        self.assertEqual(J.split_reimports([bot], {"scope": {"assetRoot": A}, "assetTree": {"atSubmit": None}}),
                         ([bot], [], []))


class AssetReimportTests(unittest.TestCase):
    def setUp(self):
        self.b = Bench(self)
        self.b.stand.pack = RS.FakePack(os.path.join(self.b.tmp, "pack-files"), [FilesTask()])
        self.seen = {}
        self.b.stand.verify = RS.verify_module(self.verify)

    def verify(self, ctx, card):
        """The stand-in S99 verifier, with A2 also failing on direct writes, keeping what the context shows."""
        want = ctx.handles["propsPath"] + "/Crate"
        try:
            found = ctx.read("gameobject_get_info", path=want)
        except rest.SkillError:
            found = None
        self.seen = {"records": list(ctx.records), "direct": ctx.direct_writes(), "violations": ctx.violations()}
        card.check("A1", bool(found) and (ctx.answer or {}).get("objectPath") == want, expected=want)
        card.check("A2", not self.seen["direct"] and not self.seen["violations"],
                   actual={"directWrites": len(self.seen["direct"])})

    def imported(self, root, rels, source="editor", agent=None, omitted=0):
        paths = [root + "/" + rel if rel else root for rel in rels]
        self.b.unity.publish("file_changes", source, agent, {
            "imported": paths, "deleted": [], "moved": [], "count": len(paths), "omitted": omitted,
            "importedTotal": len(paths) + omitted, "deletedTotal": 0, "movedTotal": 0})

    def attempt(self, during=None):
        """One attempt on the task that writes FILES; during(base, root) acts between the solver and the submit."""
        code, env = self.b.run("start", "--lang", "en", "--tasks", "S97")
        self.assertEqual(code, 0, env)
        sid = env["data"]["sessionId"]
        code, env = self.b.run("next", "--session", sid)
        self.assertEqual(code, 0, env)
        data = env["data"]
        self.b.solve(data)
        root = self.b.state(sid, data["attemptId"])["scope"]["assetRoot"]
        if during:
            during(os.path.join(self.b.project, *root.split("/")), root)
        code, env = self.b.run("submit", "--session", sid, "--attempt", data["attemptId"], "--answer",
                               data["answerFile"])
        self.assertEqual(code, 0, env)
        return self.b.state(sid, data["attemptId"]), self.b.result(sid, data["attemptId"])

    def test_unchanged_files_imported_again_are_no_evidence(self):
        state, result = self.attempt(lambda base, root: self.imported(root, ["Prefabs/Bot.prefab",
                                                                             "Prefabs/Bot_Armored.prefab"]))
        self.assertEqual((result["status"], result["reasons"]), ("pass", []))
        journal = result["evidence"]["journal"]
        self.assertEqual((journal["unchangedReimports"], journal["directWrites"], journal["nonRest"],
                          journal["packageReimports"], journal["records"]), (2, 0, 0, 0, 2 + 1))
        issue, judged = state["assetTree"]["atIssue"], state["assetTree"]["atSubmit"]
        self.assertEqual(issue["root"], state["scope"]["assetRoot"])
        self.assertEqual(sorted(set(issue["files"]) - set(FILES)),
                         ["Eval_%s.unity" % state["scope"]["runId"]])           # the run scene saved by the build
        self.assertEqual(judged, {"checked": 2, "unchanged": ["Prefabs/Bot.prefab", "Prefabs/Bot_Armored.prefab"],
                                  "changed": []})
        self.assertEqual(self.seen["direct"], [])
        self.assertEqual([r.get("assetPath") for r in self.seen["records"] if r.get("kind") == "imported"], [])

    def test_the_issue_tree_holds_the_hashes_of_the_files_at_issue(self):
        state, _result = self.attempt()
        files = state["assetTree"]["atIssue"]["files"]
        for rel, data in FILES.items():
            self.assertEqual(files[rel], hashlib.sha256(data).hexdigest(), rel)
        self.assertIsNone(state["assetTree"]["atSubmit"])                       # nothing was imported

    def test_changed_bytes_keep_counting(self):
        def change(base, root):
            write(base, "Prefabs/Bot.prefab", b"%YAML 1.1\n--- !u!1 &1\nBot rewritten\n")
            self.imported(root, ["Prefabs/Bot.prefab", "Prefabs/Bot_Armored.prefab"])
        state, result = self.attempt(change)
        journal = result["evidence"]["journal"]
        self.assertEqual((journal["unchangedReimports"], journal["directWrites"]), (1, 1))
        self.assertEqual((result["status"], [r["id"] for r in result["score"]["assertions"] if not r["pass"]]),
                         ("partial", ["A2"]))
        self.assertEqual(state["assetTree"]["atSubmit"], {"checked": 2, "unchanged": ["Prefabs/Bot_Armored.prefab"],
                                                          "changed": ["Prefabs/Bot.prefab"]})
        self.assertEqual([r["assetPath"].rsplit("/", 1)[1] for r in self.seen["direct"]], ["Bot.prefab"])

    def test_a_changed_meta_keeps_counting(self):
        def change(base, root):
            write(base, "Prefabs/Bot.prefab.meta", b"fileFormatVersion: 2\nguid: ffffffffffffffffffffffffffffffff\n")
            self.imported(root, ["Prefabs/Bot.prefab"])
        state, result = self.attempt(change)
        self.assertEqual((result["evidence"]["journal"]["unchangedReimports"],
                          result["evidence"]["journal"]["directWrites"]), (0, 1))
        self.assertEqual(state["assetTree"]["atSubmit"]["changed"], ["Prefabs/Bot.prefab"])

    def test_a_removed_meta_keeps_counting(self):
        def change(base, root):
            os.remove(os.path.join(base, "Prefabs", "Bot.prefab.meta"))
            self.imported(root, ["Prefabs/Bot.prefab"])
        _state, result = self.attempt(change)
        self.assertEqual(result["evidence"]["journal"]["directWrites"], 1)

    def test_created_files_keep_counting(self):
        def create(base, root):
            write(base, "Prefabs/Extra.prefab", b"%YAML 1.1\nExtra\n")
            write(base, "Prefabs/Extra.prefab.meta", b"fileFormatVersion: 2\nguid: 1\n")
            self.imported(root, ["Prefabs/Extra.prefab", "Prefabs/Bot.prefab"])
        state, result = self.attempt(create)
        journal = result["evidence"]["journal"]
        self.assertEqual((journal["unchangedReimports"], journal["directWrites"]), (1, 1))
        self.assertEqual(state["assetTree"]["atSubmit"]["changed"], ["Prefabs/Extra.prefab"])

    def test_an_edit_restored_before_submit_is_no_write(self):
        def edit_and_restore(base, root):
            write(base, "Prefabs/Bot.prefab", b"patched\n")
            write(base, "Prefabs/Bot.prefab", FILES["Prefabs/Bot.prefab"])
            self.imported(root, ["Prefabs/Bot.prefab"])
        state, result = self.attempt(edit_and_restore)
        self.assertEqual((result["status"], result["evidence"]["journal"]["unchangedReimports"]), ("pass", 1))

    def test_deleted_and_moved_files_always_count(self):
        def act(base, root):
            self.b.unity.publish("file_changes", "editor", None, {
                "imported": [], "deleted": [root + "/Prefabs/Bot.prefab"],
                "moved": [{"from": root + "/Scripts/Patroller.cs", "to": root + "/Scripts/Sentinel.cs"}],
                "count": 2, "omitted": 0, "importedTotal": 0, "deletedTotal": 1, "movedTotal": 1})
        state, result = self.attempt(act)
        journal = result["evidence"]["journal"]
        self.assertEqual((journal["unchangedReimports"], journal["directWrites"]), (0, 2))
        self.assertIsNone(state["assetTree"]["atSubmit"])

    def test_imports_made_by_the_agents_own_calls_are_not_judged(self):
        state, result = self.attempt(lambda base, root: self.imported(root, ["Prefabs/Bot.prefab"], source="rest",
                                                                      agent=SOLVER))
        self.assertEqual(result["evidence"]["journal"]["unchangedReimports"], 0)
        self.assertIsNone(state["assetTree"]["atSubmit"])
        self.assertEqual([r["agent"] for r in self.seen["records"] if r.get("kind") == "imported"], [SOLVER])

    def test_the_runners_own_imports_are_judged_like_the_editors(self):
        state, result = self.attempt(lambda base, root: self.imported(root, ["Prefabs/Bot.prefab"], source="rest",
                                                                      agent=RUNNER_AGENT))
        self.assertEqual(result["evidence"]["journal"]["unchangedReimports"], 1)
        self.assertEqual(state["assetTree"]["atSubmit"]["unchanged"], ["Prefabs/Bot.prefab"])

    def test_imports_outside_the_asset_folder_are_not_judged(self):
        def outside(base, root):
            self.b.unity.publish("file_changes", "editor", None, {
                "imported": ["Assets/Other/Bot.prefab"], "deleted": [], "moved": [], "count": 1, "omitted": 0,
                "importedTotal": 1, "deletedTotal": 0, "movedTotal": 0})
        state, result = self.attempt(outside)
        self.assertEqual(result["status"], "contaminated")
        self.assertIn("non_rest_changes: 1", result["reasons"])
        self.assertEqual(result["evidence"]["journal"]["unchangedReimports"], 0)
        self.assertIsNone(state["assetTree"]["atSubmit"])

    def test_truncated_paths_still_count(self):
        state, result = self.attempt(lambda base, root: self.imported(root, ["Prefabs/Bot.prefab"], omitted=7))
        self.assertEqual(result["status"], "contaminated")
        self.assertIn("non_rest_changes: 1", result["reasons"])                 # the omitted tail
        self.assertEqual(result["evidence"]["journal"]["unchangedReimports"], 1)

    def test_only_the_imported_files_are_hashed_again(self):
        with mock.patch.object(E, "sha256_file", wraps=E.sha256_file) as hashed:
            state, _result = self.attempt()
            at_issue = hashed.call_count
            self.assertEqual(at_issue, len(state["assetTree"]["atIssue"]["files"]))
            self.attempt(lambda base, root: self.imported(root, ["Prefabs/Bot.prefab"]))
            self.assertEqual(hashed.call_count - at_issue, at_issue + 2)        # a second issue, then the file and its .meta

    def test_a_folder_that_cannot_be_hashed_explains_nothing(self):
        with mock.patch.object(E, "ASSET_TREE_LIMIT", 2):
            state, result = self.attempt(lambda base, root: self.imported(root, ["Prefabs/Bot.prefab"]))
        self.assertIsNone(state["assetTree"]["atIssue"])
        self.assertIsNone(state["assetTree"]["atSubmit"])
        self.assertEqual((result["evidence"]["journal"]["unchangedReimports"],
                          result["evidence"]["journal"]["directWrites"]), (0, 1))

    def test_package_and_asset_reimports_are_counted_apart(self):
        pkg = os.path.realpath(os.path.join(self.b.tmp, "pkg"))
        write(pkg, "package.json", b'{"name": "com.besty.unity-skills", "version": "0.0.1"}\n')
        write(pkg, "Editor/Skills/A.cs", b"class A {}\n")
        with open(os.path.join(self.b.project, "Packages", "packages-lock.json"), "w", encoding="utf-8") as handle:
            json.dump({"dependencies": {"com.besty.unity-skills": {"version": "file:" + pkg, "depth": 0,
                                                                   "source": "local"}}}, handle)

        def both(base, root):
            self.imported(root, ["Prefabs/Bot.prefab"])
            self.b.unity.publish("file_changes", "editor", None, {
                "imported": [J.PACKAGE_PREFIX + "/Editor/Skills/A.cs"], "deleted": [], "moved": [], "count": 1,
                "omitted": 0, "importedTotal": 1, "deletedTotal": 0, "movedTotal": 0})
        _state, result = self.attempt(both)
        journal = result["evidence"]["journal"]
        self.assertEqual((result["status"], journal["unchangedReimports"], journal["packageReimports"]),
                         ("pass", 1, 1))


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
