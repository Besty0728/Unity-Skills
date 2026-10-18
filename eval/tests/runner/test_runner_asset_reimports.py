"""Imports under the run's asset folder that are no write around the skills (contract 2.8 N8/S6, 2.9). Unity importing
a prefab again because a script it uses changed type: a file that existed at issue and has the same bytes at submit is
explained; created, changed, deleted and moved files, a changed .meta, the agent's own REST imports and truncated
paths keep counting. The editor importing on its own time what a skill of the agent wrote or moved (after the move,
after the compile that follows a script change): explained by any record of the agent's own REST calls that names the
same file, whichever came first; a file written to disk directly has none."""
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
from usk_eval import scope as real_scope
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
        # line endings are not folded: the bytes are the bytes
        self.assertNotEqual(E.asset_hashes(self.dir)["Prefabs/Bot.prefab"], hashes["Prefabs/Bot.prefab"])

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


# The file_changes events of a live DC3 attempt that failed Q12 (6000.3 URP, the negative rename_and_reset, trimmed to
# the events that matter, paths relative to the run folder): (seq, source, agent, time, imported, moved).
LIVE_ROOT = "Assets/__UnitySkillsEval/r20261018T0727206714ce"
LIVE_PREFABS = ["Prefabs/Bot.prefab", "Prefabs/Bot_Armored.prefab"]
LIVE_SCRIPT = "Scripts/Game/AI/Patroller.cs"
LIVE_DC3 = [
    (13172, "rest", "SelftestSolver", "07:27:38.160", [],                                    # asset_move
     [("Scripts/Actors/WatchRoutine_r20261018T0727206714ce.cs", LIVE_SCRIPT)]),
    (13173, "editor", None, "07:27:43.678", [LIVE_SCRIPT], []),             # Unity imports the moved script, 5 s later
    (13174, "rest", "SelftestSolver", "07:27:46.518", [LIVE_SCRIPT], []),                     # asset_refresh
    (13175, "editor", None, "07:27:50.736", LIVE_PREFABS + [LIVE_SCRIPT], []),   # after the compile: dependents again
    (13176, "rest", "SelftestSolver", "07:27:55.316", LIVE_PREFABS, []),          # prefab_set_serialized_property
    (13177, "rest", "SelftestSolver", "07:27:55.329", LIVE_PREFABS, []),
    (13186, "rest", "SelftestSolver", "07:27:55.600", LIVE_PREFABS[1:], []),
]


def live_events(skip=None):
    """The journal entries of LIVE_DC3 (without the seqs in skip), as read_range returns them."""
    events = []
    for seq, source, agent, at, imported, moved in LIVE_DC3:
        if seq in (skip or ()):
            continue
        events.append({"seq": seq, "type": "file_changes", "source": source, "agent": agent, "isPlaying": False,
                       "tsUtc": "2026-10-18T" + at + "0000Z",
                       "payload": {"imported": [LIVE_ROOT + "/" + rel for rel in imported], "deleted": [],
                                   "moved": [{"from": LIVE_ROOT + "/" + old, "to": LIVE_ROOT + "/" + new}
                                             for old, new in moved],
                                   "count": len(imported) + len(moved), "omitted": 0, "importedTotal": len(imported),
                                   "deletedTotal": 0, "movedTotal": len(moved)}})
    return events


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
        self.assertEqual(J.split_reimports([plugin, bot, other], state), ([other], [plugin], [bot], []))
        self.assertEqual(J.split_reimports([plugin, bot, other], {}), ([plugin, bot, other], [], [], []))
        self.assertEqual(J.split_reimports([bot], {"scope": {"assetRoot": A}, "assetTree": {"atSubmit": None}}),
                         ([bot], [], [], []))

    def test_skill_imports_are_split_after_the_byte_judgement(self):
        A = self.A
        wrote = dict(self.rec(A + "/Prefabs/Bot.prefab", source="rest", agent=SOLVER), seq=3)
        later = dict(self.rec(A + "/Prefabs/Bot.prefab"), seq=5)
        same = dict(self.rec(A + "/Prefabs/Same.prefab"), seq=7)
        also = dict(self.rec(A + "/Prefabs/Same.prefab", source="rest", agent=SOLVER), seq=6)
        state = {"scope": {"assetRoot": A}, "assetTree": {"atSubmit": {"unchanged": ["Prefabs/Same.prefab"]}}}
        kept, package, assets, skills = J.split_reimports([wrote, also, later, same], state)
        self.assertEqual((kept, package, assets, skills), ([wrote, also], [], [same], [later]))
        self.assertNotIn(same, skills)                                          # the byte judgement comes first


    def test_relative_paths_below_the_asset_folder(self):
        A = self.A
        for path, expected in ((A + "/Prefabs/Bot.prefab", "Prefabs/Bot.prefab"),
                               (A + "/Prefabs/Bot.prefab.meta", "Prefabs/Bot.prefab"),
                               ((A + "/Prefabs/Bot.prefab").replace("/", "\\"), "Prefabs/Bot.prefab"),
                               (A, None), (A + "/", None), (A + "-copy/x", None), ("Assets/x", None), ("", None),
                               (None, None)):
            self.assertEqual(J.asset_relative(path, A), expected, path)
        self.assertIsNone(J.asset_relative(A + "/x", ""))

    def test_which_records_show_a_skill_wrote_a_file(self):
        A = self.A
        move = {"kind": "moved", "assetPath": A + "/Scripts/Sentinel.cs", "from": A + "/Scripts/Patroller.cs",
                "source": "rest", "agent": SOLVER, "origin": "attempt"}
        prop = {"kind": "property_changed", "type": "scene_changes", "assetPath": A + "/Mats/Red.mat.meta",
                "source": "rest", "agent": SOLVER, "origin": "attempt"}
        scene_prop = {"kind": "property_changed", "type": "scene_changes", "path": "Run/Crate", "source": "rest",
                      "agent": SOLVER, "origin": "attempt"}
        cases = [(self.rec(A + "/Prefabs/Bot.prefab", source="rest", agent=SOLVER), "Prefabs/Bot.prefab"),
                 (move, "Scripts/Sentinel.cs"),                                    # the new path, not the old one
                 (prop, "Mats/Red.mat"),                                           # the asset, not its .meta
                 (scene_prop, None),                                               # a scene object has no asset path
                 (self.rec(A + "/Prefabs/Bot.prefab", source="rest", agent="mixed"), "Prefabs/Bot.prefab"),
                 (self.rec(A + "/Prefabs/Bot.prefab", source="rest", agent="unknown"), "Prefabs/Bot.prefab"),
                 (self.rec(A + "/Prefabs/Bot.prefab", kind="deleted", source="rest", agent=SOLVER), None),
                 (self.rec(A + "/Prefabs/Bot.prefab", source="rest", agent=RUNNER_AGENT), None),
                 (self.rec(A + "/Prefabs/Bot.prefab", source="rest", agent=SOLVER, origin="refresh"), None),
                 (self.rec(A + "/Prefabs/Bot.prefab", source="editor"), None),
                 (self.rec("Assets/Other/Bot.prefab", source="rest", agent=SOLVER), None),
                 ({"kind": "omitted", "count": 4, "source": "rest", "agent": SOLVER}, None)]
        for record, expected in cases:
            self.assertEqual(J.skill_path(record, A), expected, record)
        self.assertIsNone(J.skill_path(self.rec(A + "/x", source="rest", agent=SOLVER), ""))

    def test_an_editor_import_is_explained_by_a_skill_record_of_the_same_file_whenever_it_came(self):
        A = self.A
        wrote = dict(self.rec(A + "/Prefabs/Bot.prefab", source="rest", agent=SOLVER), seq=10)
        after = dict(self.rec(A + "/Prefabs/Bot.prefab"), seq=12)
        before = dict(self.rec(A + "/Prefabs/Bot.prefab"), seq=8)
        other = dict(self.rec(A + "/Prefabs/Other.prefab"), seq=13)
        refresh = dict(self.rec(A + "/Prefabs/Bot.prefab", source="rest", agent=RUNNER_AGENT, origin="refresh"),
                       seq=14)
        meta = dict(self.rec(A + "/Prefabs/Bot.prefab.meta"), seq=15)
        deleted = dict(self.rec(A + "/Prefabs/Bot.prefab", kind="deleted"), seq=16)
        gone = dict(self.rec(A + "/Prefabs/Gone.prefab", kind="deleted", source="rest", agent=SOLVER), seq=17)
        regained = dict(self.rec(A + "/Prefabs/Gone.prefab"), seq=18)
        no_seq = {"kind": "imported", "assetPath": A + "/Prefabs/Bot.prefab", "source": "editor"}
        records = [before, wrote, after, other, refresh, meta, deleted, gone, regained, no_seq]
        self.assertEqual(J.split_skill_imports(records, A),
                         ([wrote, other, refresh, deleted, gone, regained], [before, after, meta, no_seq]))
        # the order of the records is no evidence either way: a compile re-imports the prefabs that use the changed
        # script before the skill edits them, and the editor imports a moved file some seconds after the move
        self.assertEqual(J.split_skill_imports([after, wrote], A), ([wrote], [after]))
        self.assertEqual(J.split_skill_imports([wrote, after], A), ([wrote], [after]))
        self.assertEqual(J.split_skill_imports([before, wrote], A), ([wrote], [before]))
        self.assertEqual(J.split_skill_imports(records, ""), (records, []))
        self.assertEqual(J.split_skill_imports([], A), ([], []))

    def test_the_live_journal_of_rename_and_reset(self):
        """Replay of the journal that failed Q12 live: the editor imported the moved script, and after the compile
        both prefabs and the script, before the first REST record that names the prefabs."""
        records = J.flatten(live_events(), {"from": 13191, "to": 13191})
        state = {"scope": {"assetRoot": LIVE_ROOT}}
        self.assertEqual(len(real_scope.direct_writes(records, LIVE_ROOT)), 1 + 3)          # what failed Q12
        first_rest = min(r["seq"] for r in records if r["source"] == "rest" and "Bot.prefab" in r["assetPath"])
        self.assertLess(13175, first_rest)                          # so "the skill's record came first" is no rule
        kept, package, assets, skills = J.split_reimports(records, state)
        self.assertEqual([(r["seq"], r["assetPath"].rsplit("/", 1)[1]) for r in skills],
                         [(13173, "Patroller.cs"), (13175, "Bot.prefab"), (13175, "Bot_Armored.prefab"),
                          (13175, "Patroller.cs")])
        self.assertEqual((package, assets), ([], []))
        self.assertEqual({r["source"] for r in kept}, {"rest"})                        # the agent's own records stay
        self.assertEqual(len(kept), len(records) - 4)
        self.assertEqual(real_scope.direct_writes(kept, LIVE_ROOT), [])
        self.assertEqual(real_scope.non_rest(kept, LIVE_ROOT), [])

    def test_the_live_journal_without_the_skill_records_of_the_prefabs(self):
        """The same journal when no skill touched the prefabs: their imports by the editor are what they look like."""
        records = J.flatten(live_events(skip=(13176, 13177, 13186)), {"from": 13191, "to": 13191})
        kept, _package, _assets, skills = J.split_reimports(records, {"scope": {"assetRoot": LIVE_ROOT}})
        self.assertEqual([r["assetPath"].rsplit("/", 1)[1] for r in skills], ["Patroller.cs", "Patroller.cs"])
        self.assertEqual([r["assetPath"].rsplit("/", 1)[1] for r in real_scope.direct_writes(kept, LIVE_ROOT)],
                         ["Bot.prefab", "Bot_Armored.prefab"])


class AssetCase(unittest.TestCase):
    """The runner on a task whose build writes FILES under A, with the editor publishing journal entries by hand."""

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

    def moved(self, root, pairs, source="rest", agent=SOLVER):
        moves = [{"from": root + "/" + old, "to": root + "/" + new} for old, new in pairs]
        self.b.unity.publish("file_changes", source, agent, {
            "imported": [], "deleted": [], "moved": moves, "count": len(moves), "omitted": 0, "importedTotal": 0,
            "deletedTotal": 0, "movedTotal": len(moves)})

    def property_changed(self, root, rel, source="rest", agent=SOLVER):
        """An asset's property changed through a skill: a scene_changes record that carries the asset's path."""
        self.b.unity.publish("scene_changes", source, agent, {"count": 1, "omitted": 0, "changes": [{
            "kind": "property_changed", "targetType": "Material", "property": "_Color", "previousValue": "",
            "value": "(1,0,0,1)", "assetPath": root + "/" + rel}]})

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

class AssetReimportTests(AssetCase):
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
            # a second issue hashes the folder again, S6 then the imported file and its .meta
            self.assertEqual(hashed.call_count - at_issue, at_issue + 2)

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


class SkillImportTests(AssetCase):
    """The editor imports on its own time what a skill of the agent wrote or moved. DC3's rename_and_reset saves both
    prefabs through prefab_set_serialized_property, which imports each inside its REST window (a REST record of the
    file); the editor imported them as well, earlier, when the compile after the script change was done."""

    BOT, ARMORED = "Prefabs/Bot.prefab", "Prefabs/Bot_Armored.prefab"

    def skill_wrote(self, base, root, rels):
        """What a saving skill leaves: new bytes on disk and a REST import record of each file."""
        for rel in rels:
            write(base, rel, b"%YAML 1.1\n--- !u!1 &9\nsaved by a skill\n")
        self.imported(root, rels, source="rest", agent=SOLVER)

    def test_the_editors_later_import_of_files_a_skill_wrote_is_no_direct_write(self):
        def act(base, root):
            self.skill_wrote(base, root, [self.BOT, self.ARMORED])
            self.imported(root, [self.BOT, self.ARMORED])
        state, result = self.attempt(act)
        self.assertEqual((result["status"], result["reasons"]), ("pass", []))
        journal = result["evidence"]["journal"]
        self.assertEqual((journal["skillImports"], journal["unchangedReimports"], journal["directWrites"],
                          journal["nonRest"], journal["records"]), (2, 0, 0, 0, 2 + 2 + 1))
        self.assertEqual(sorted(state["assetTree"]["atSubmit"]["changed"]), [self.BOT, self.ARMORED])  # by bytes: no
        self.assertEqual([(r["source"], r["agent"]) for r in self.seen["records"] if r.get("kind") == "imported"],
                         [("rest", SOLVER)] * 2)                                    # the skills' own records stay

    def test_a_file_written_directly_still_counts(self):
        def act(base, root):
            write(base, self.BOT, b"written around the skills\n")
            self.imported(root, [self.BOT])
        _state, result = self.attempt(act)
        journal = result["evidence"]["journal"]
        self.assertEqual((journal["skillImports"], journal["directWrites"]), (0, 1))
        self.assertEqual((result["status"], [r["id"] for r in result["score"]["assertions"] if not r["pass"]]),
                         ("partial", ["A2"]))

    def test_a_skill_that_touched_another_file_explains_nothing(self):
        def act(base, root):
            self.skill_wrote(base, root, [self.ARMORED])
            write(base, self.BOT, b"written around the skills\n")
            self.imported(root, [self.BOT])
        _state, result = self.attempt(act)
        journal = result["evidence"]["journal"]
        self.assertEqual((journal["skillImports"], journal["directWrites"]), (0, 1))
        self.assertEqual([r["assetPath"].rsplit("/", 1)[1] for r in self.seen["direct"]], ["Bot.prefab"])

    def test_the_editors_import_may_come_before_the_skills_record(self):
        def act(base, root):
            write(base, self.BOT, b"saved by a skill\n")
            self.imported(root, [self.BOT])                      # the compile re-imported the prefab first
            self.imported(root, [self.BOT], source="rest", agent=SOLVER)
        _state, result = self.attempt(act)
        journal = result["evidence"]["journal"]
        self.assertEqual((journal["skillImports"], journal["directWrites"]), (1, 0))
        self.assertEqual((result["status"], result["reasons"]), ("pass", []))

    def test_a_deletion_by_a_skill_explains_nothing(self):
        def act(base, root):
            write(base, self.BOT, b"written around the skills\n")
            self.b.unity.publish("file_changes", "rest", SOLVER, {
                "imported": [], "deleted": [root + "/" + self.BOT], "moved": [], "count": 1, "omitted": 0,
                "importedTotal": 0, "deletedTotal": 1, "movedTotal": 0})
            self.imported(root, [self.BOT])
        _state, result = self.attempt(act)
        journal = result["evidence"]["journal"]
        self.assertEqual((journal["skillImports"], journal["directWrites"]), (0, 1))

    def test_the_runners_own_calls_are_not_the_agents_skills(self):
        def act(base, root):
            write(base, self.BOT, b"written around the skills\n")
            self.imported(root, [self.BOT], source="rest", agent=RUNNER_AGENT)
            self.imported(root, [self.BOT])
        _state, result = self.attempt(act)
        journal = result["evidence"]["journal"]
        self.assertEqual((journal["skillImports"], journal["directWrites"]), (0, 1))       # the editor's import only

    def test_a_property_change_on_the_asset_names_it(self):
        def act(base, root):
            write(base, "Materials/Red.mat", b"%YAML 1.1\nRed\n")
            self.property_changed(root, "Materials/Red.mat")
            self.imported(root, ["Materials/Red.mat"])
        _state, result = self.attempt(act)
        journal = result["evidence"]["journal"]
        self.assertEqual((journal["skillImports"], journal["directWrites"]), (1, 0))
        self.assertEqual(self.seen["direct"], [])

    def test_a_move_by_a_skill_names_the_new_path(self):
        def act(base, root):
            write(base, "Scripts/Sentinel.cs", FILES["Scripts/Patroller.cs"])
            os.remove(os.path.join(base, "Scripts", "Patroller.cs"))                  # the move took it away
            os.remove(os.path.join(base, "Scripts", "Patroller.cs.meta"))
            self.moved(root, [("Scripts/Patroller.cs", "Scripts/Sentinel.cs")])
            self.imported(root, ["Scripts/Sentinel.cs", "Scripts/Elsewhere.cs", "Scripts/Patroller.cs"])
        _state, result = self.attempt(act)
        journal = result["evidence"]["journal"]
        self.assertEqual((journal["skillImports"], journal["directWrites"]), (1, 2))
        self.assertEqual(sorted(r["assetPath"].rsplit("/", 1)[1] for r in self.seen["direct"]),
                         ["Elsewhere.cs", "Patroller.cs"])                           # not the new path, not the old

    def test_a_meta_file_is_matched_with_its_asset(self):
        def act(base, root):
            write(base, self.BOT, b"saved by a skill\n")
            self.imported(root, [self.BOT + ".meta"], source="rest", agent=SOLVER)
            self.imported(root, [self.BOT])
            write(base, self.ARMORED, b"saved by a skill\n")
            self.imported(root, [self.ARMORED], source="rest", agent=SOLVER)
            self.imported(root, [self.ARMORED + ".meta"])
        _state, result = self.attempt(act)
        journal = result["evidence"]["journal"]
        self.assertEqual((journal["skillImports"], journal["directWrites"]), (2, 0))

    def test_unchanged_bytes_are_counted_as_reimports_first(self):
        def act(base, root):
            self.imported(root, [self.BOT], source="rest", agent=SOLVER)
            self.imported(root, [self.BOT])
        _state, result = self.attempt(act)
        journal = result["evidence"]["journal"]
        self.assertEqual((journal["unchangedReimports"], journal["skillImports"], journal["directWrites"]), (1, 0, 0))

    def test_the_agents_own_records_are_kept_whatever_follows_them(self):
        def act(base, root):
            self.skill_wrote(base, root, [self.BOT])
            self.skill_wrote(base, root, [self.BOT])             # a second save of the same file
        _state, result = self.attempt(act)
        self.assertEqual(result["evidence"]["journal"]["skillImports"], 0)
        self.assertEqual([r["source"] for r in self.seen["records"] if r.get("kind") == "imported"], ["rest", "rest"])

    def test_files_outside_the_asset_folder_are_not_explained(self):
        def act(base, root):
            for source, agent in (("rest", SOLVER), ("editor", None)):
                self.b.unity.publish("file_changes", source, agent, {
                    "imported": ["Assets/Other/Bot.prefab"], "deleted": [], "moved": [], "count": 1, "omitted": 0,
                    "importedTotal": 1, "deletedTotal": 0, "movedTotal": 0})
        _state, result = self.attempt(act)
        self.assertEqual(result["status"], "contaminated")
        self.assertIn("non_rest_changes: 1", result["reasons"])
        self.assertEqual(result["evidence"]["journal"]["skillImports"], 0)


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
