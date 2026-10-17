"""usk_eval.scope: path containment, adoption and the scope check over flattened records (v1 JournalTests ported)."""
import copy
import unittest

try:
    from . import test_verifylib_support as support
except ImportError:
    import test_verifylib_support as support

from usk_eval import journal, scope

ROOT, ASSETS = "EV_r1", "Assets/__UnitySkillsEval/r1"
RUN_SCENE = ASSETS + "/Eval_r1.unity"
MAIN = "Assets/Scenes/S.unity"


def seqs(records):
    return [r["seq"] for r in records]


class UnderTests(unittest.TestCase):
    def test_under(self):
        self.assertTrue(scope.under("EV_r1/A", "EV_r1"))
        self.assertTrue(scope.under("EV_r1", "EV_r1"))
        self.assertFalse(scope.under("EV_r10/A", "EV_r1"))
        self.assertTrue(scope.under("Assets\\X\\y.mat", "Assets/X"))
        self.assertTrue(scope.under("Assets/X/y.mat", "Assets\\X\\"))

    def test_empty_and_non_text_values_are_not_under_anything(self):
        for path, root in [(None, "EV_r1"), ("", "EV_r1"), ("EV_r1/A", None), ("EV_r1/A", ""), (5, "EV_r1"),
                           ("EV_r1/A", 5), (["EV_r1"], "EV_r1")]:
            with self.subTest(path=path, root=root):
                self.assertFalse(scope.under(path, root))


class AdoptedPathsTests(unittest.TestCase):
    def test_paths_of_objects_moved_into_r_are_adopted(self):
        records = [support.rec("gameobject_reparented", fromPath="Cube", toPath=ROOT + "/Cube"),
                   support.rec("gameobject_reparented", fromPath=ROOT + "/A", toPath=ROOT + "/B/A"),
                   support.rec("gameobject_reparented", fromPath=ROOT + "/A", toPath="Outside/A"),
                   support.rec("gameobject_reparented", fromPath=None, toPath=ROOT + "/Lost"),
                   support.rec("gameobject_reparented", fromPath="Disc", toPath="Elsewhere/Disc"),
                   support.rec("property_changed", fromPath="Sphere", toPath=ROOT + "/Sphere")]
        self.assertEqual(scope.adopted_paths(records, ROOT), {"Cube"})

    def test_adoption_excuses_the_transient_work_on_the_own_object(self):
        records = [support.rec("gameobject_created", path="Cube", scenePath=RUN_SCENE),
                   support.rec("property_changed", path="Cube", scenePath=RUN_SCENE),
                   support.rec("gameobject_reparented", fromPath="Cube", toPath=ROOT + "/Cube", newParentPath=ROOT,
                               scenePath=RUN_SCENE)]
        self.assertEqual(scope.scope_violations(records, ROOT, ASSETS, RUN_SCENE), [])

    def test_unrelated_outside_edits_stay_violations(self):
        stray = support.rec("property_changed", path="Sphere", scenePath=RUN_SCENE)
        records = [support.rec("gameobject_reparented", fromPath="Cube", toPath=ROOT + "/Cube", newParentPath=ROOT,
                               scenePath=RUN_SCENE), stray]
        self.assertEqual(scope.scope_violations(records, ROOT, ASSETS, RUN_SCENE), [stray])


class ScopeViolationsTests(unittest.TestCase):
    def events(self):
        return [
            {"seq": 1, "type": "scene_changes", "source": "rest", "payload": {"changes": [
                {"kind": "gameobject_created", "path": "EV_r1/A", "scenePath": RUN_SCENE},
                {"kind": "gameobject_reparented", "fromPath": "EV_r1/A", "toPath": "EV_r1/B/A",
                 "newParentPath": "EV_r1/B"},
                {"kind": "property_changed", "path": "Main Camera", "scenePath": MAIN}]}},
            {"seq": 2, "type": "file_changes", "source": "rest", "payload": {
                "imported": [ASSETS + "/M.mat", ASSETS + "/M.mat.meta", "Assets/Other.mat"],
                "moved": [{"from": ASSETS + "/a.mat", "to": ASSETS + "/b/a.mat"}]}},
            {"seq": 3, "type": "scene_saved", "source": "rest", "payload": {"path": MAIN}},
            {"seq": 4, "type": "scene_saved", "source": "editor", "payload": {"path": RUN_SCENE}},
        ]

    def test_flatten_and_scope(self):
        recs = journal.flatten(self.events())
        self.assertEqual([r["kind"] for r in recs].count("imported"), 3)
        bad = scope.scope_violations(recs, ROOT, ASSETS, RUN_SCENE)
        bad_kinds = sorted((r["kind"], r.get("path") or r.get("assetPath")) for r in bad)
        self.assertEqual(bad_kinds, [("imported", "Assets/Other.mat"), ("property_changed", "Main Camera"),
                                     ("scene_saved", MAIN)])

    def test_the_run_scene_save_by_the_editor_is_no_longer_non_rest(self):
        recs = journal.flatten(self.events())
        self.assertEqual(scope.non_rest(recs, ASSETS, RUN_SCENE), [])
        recs.append(support.scene_rec("scene_saved", MAIN, source="editor"))
        self.assertEqual(len(scope.non_rest(recs, ASSETS, RUN_SCENE)), 1)

    def test_records_are_not_modified(self):
        recs = journal.flatten(self.events())
        before = copy.deepcopy(recs)
        scope.scope_violations(recs, ROOT, ASSETS, RUN_SCENE)
        scope.non_rest(recs, ASSETS, RUN_SCENE)
        scope.direct_writes(recs, ASSETS)
        scope.write_records(recs, RUN_SCENE, support.RUNNER)
        self.assertEqual(recs, before)

    def test_moves_must_stay_inside_a(self):
        inside = support.file_rec("moved", ASSETS + "/b/a.mat", **{"from": ASSETS + "/a.mat"})
        into = support.file_rec("moved", ASSETS + "/a.mat", **{"from": "Assets/Other/a.mat"})
        out = support.file_rec("moved", "Assets/Other/a.mat", **{"from": ASSETS + "/a.mat"})
        self.assertEqual(scope.scope_violations([inside, into, out], ROOT, ASSETS, RUN_SCENE), [into, out])

    def test_meta_files_follow_their_asset(self):
        ok = support.file_rec("imported", ASSETS + "/x.mat.meta")
        outside = support.file_rec("deleted", "Assets/Other/x.mat.meta")
        self.assertEqual(scope.scope_violations([ok, outside], ROOT, ASSETS, RUN_SCENE), [outside])

    def test_a_file_record_without_a_path_is_a_violation(self):
        blank = support.file_rec("imported", "")
        self.assertEqual(scope.scope_violations([blank], ROOT, ASSETS, RUN_SCENE), [blank])

    def test_cut_off_lists_cannot_be_proven_in_scope(self):
        omitted = support.rec("omitted", count=3)
        self.assertEqual(scope.scope_violations([omitted], ROOT, ASSETS, RUN_SCENE), [omitted])

    def test_extra_allowed_exempts_records(self):
        stray = support.rec("property_changed", path="Main Camera", scenePath=MAIN)
        other = support.rec("property_changed", path="Other", scenePath=MAIN)
        bad = scope.scope_violations([stray, other], ROOT, ASSETS, RUN_SCENE,
                                     extra_allowed=lambda r: r.get("path") == "Main Camera")
        self.assertEqual(bad, [other])

    def test_scene_changes_must_name_the_run_scene(self):
        elsewhere = support.rec("components_changed", path=ROOT + "/A", scenePath=MAIN)
        here = support.rec("components_changed", path=ROOT + "/A", scenePath=RUN_SCENE)
        self.assertEqual(scope.scope_violations([elsewhere, here], ROOT, ASSETS, RUN_SCENE), [elsewhere])
        self.assertEqual(scope.scope_violations([elsewhere, here], ROOT, ASSETS), [])    # no run scene: not compared

    def test_every_path_of_a_record_must_be_in_r(self):
        out = support.rec("gameobject_reparented", fromPath=ROOT + "/A", toPath="Outside/A", newParentPath="Outside",
                          scenePath=RUN_SCENE)
        into = support.rec("gameobject_reparented", fromPath="Outside/B", toPath=ROOT + "/B", newParentPath=ROOT,
                           scenePath=RUN_SCENE)
        self.assertEqual(scope.scope_violations([out], ROOT, ASSETS, RUN_SCENE), [out])
        self.assertEqual(scope.scope_violations([into], ROOT, ASSETS, RUN_SCENE), [])    # adopted into R

    def test_asset_edits_are_judged_by_the_asset_root(self):
        inside = support.rec("property_changed", assetPath=ASSETS + "/M.mat")
        outside = support.rec("property_changed", assetPath="Assets/Other.mat")
        self.assertEqual(scope.scope_violations([inside, outside], ROOT, ASSETS, RUN_SCENE), [outside])

    def test_records_without_any_path_fail_closed(self):
        pathless = support.rec("components_changed")
        self.assertEqual(scope.scope_violations([pathless], ROOT, ASSETS, RUN_SCENE), [pathless])

    def test_undo_redo_and_unknown_event_types_are_not_scope_evidence(self):
        undo = support.rec("undo_redo", type="undo_redo")
        future = support.rec("something_new", type="something_new")
        self.assertEqual(scope.scope_violations([undo, future], ROOT, ASSETS, RUN_SCENE), [])


class FixtureAttemptTests(unittest.TestCase):
    def records(self, name, refresh_window=None):
        return journal.flatten(support.fixture_entries(name), refresh_window)

    def test_clean_attempt_has_no_violations(self):
        recs = self.records("attempt_clean.jsonl")
        self.assertEqual(len(recs), 9)
        self.assertEqual(scope.scope_violations(recs, support.R, support.A, support.SCENE), [])

    def test_noisy_attempt_violations_before_the_context_filters(self):
        recs = self.records("attempt_noisy.jsonl", {"from": 206, "to": 207})
        bad = scope.scope_violations(recs, support.R, support.A, support.SCENE)
        self.assertEqual(seqs(bad), [201, 202, 203, 205, 208, 209])

    def test_journal_fields_reach_the_records(self):
        recs = self.records("attempt_noisy.jsonl", {"from": 206, "to": 207})
        by_seq = {}
        for r in recs:
            by_seq.setdefault(r["seq"], r)
        self.assertEqual(by_seq[201]["agent"], support.AGENT)
        self.assertIsNone(by_seq[204]["agent"])
        self.assertTrue(by_seq[205]["isPlaying"])
        self.assertEqual(by_seq[207]["origin"], "refresh")
        self.assertEqual(by_seq[206]["origin"], "attempt")
        self.assertEqual(by_seq[204]["entityId"], "-2002")
        self.assertEqual(by_seq[204]["scenePath"], support.SCENE)
        self.assertTrue(by_seq[201]["tsUtc"].endswith("Z"))


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
