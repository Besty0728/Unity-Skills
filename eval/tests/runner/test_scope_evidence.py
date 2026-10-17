"""usk_eval.scope evidence classes (non-REST, direct writes, write records), the run-scene save allowance (G2) and
the VerifyContext that exposes them to private verifiers."""
import unittest

try:
    from . import test_verifylib_support as support
except ImportError:
    import test_verifylib_support as support

from usk_eval import fixture, journal, scope
from usk_eval.task_api import VerifyContext

R, A, SCENE, OTHER = support.R, support.A, support.SCENE, support.OTHER_SCENE
RUNNER = support.RUNNER
WINDOW = {"from": 206, "to": 207}


def seqs(records):
    return [r["seq"] for r in records]


def noisy():
    return journal.flatten(support.fixture_entries("attempt_noisy.jsonl"), WINDOW)


def clean():
    return journal.flatten(support.fixture_entries("attempt_clean.jsonl"))


class NonRestTests(unittest.TestCase):
    def test_only_editor_changes_that_nothing_excuses_count(self):
        # 204 a manual edit inside R and 209 a manual save of another scene count; 205 is Play Mode, 206 files under A,
        # 210 the run scene
        self.assertEqual(seqs(scope.non_rest(noisy(), A, SCENE)), [204, 209])

    def test_clean_attempt_has_no_non_rest_changes(self):
        self.assertEqual(scope.non_rest(clean(), A, SCENE), [])

    def test_allowed_change_exempts_a_record(self):
        left = scope.non_rest(noisy(), A, SCENE, allowed=lambda r: r["seq"] == 204)
        self.assertEqual(seqs(left), [209])

    def test_rest_records_are_never_non_rest(self):
        records = [support.prop("Main Camera", source="rest"), support.rec("undo_redo", type="undo_redo")]
        self.assertEqual(scope.non_rest(records, A, SCENE), [])

    def test_records_without_a_source_are_not_counted(self):
        record = support.prop("Main Camera")
        del record["source"]
        self.assertEqual(scope.non_rest(records=[record], asset_root=A, run_scene=SCENE), [])

    def test_play_mode_records_are_exempt(self):
        record = support.prop("Main Camera", source="editor", isPlaying=True)
        self.assertEqual(scope.non_rest([record], A, SCENE), [])

    def test_file_records_are_exempt_only_wholly_inside_a(self):
        inside = [support.file_rec("imported", A + "/x.cs", source="editor"),
                  support.file_rec("deleted", A + "/x.cs.meta", source="editor"),
                  support.file_rec("moved", A + "/y.cs", source="editor", **{"from": A + "/x.cs"})]
        outside = [support.file_rec("imported", "Assets/Other.cs", source="editor"),
                   support.file_rec("deleted", "Assets/Other.cs", source="editor"),
                   support.file_rec("moved", A + "/y.cs", source="editor", **{"from": "Assets/x.cs"}),
                   support.file_rec("moved", "Assets/y.cs", source="editor", **{"from": A + "/x.cs"}),
                   support.file_rec("imported", "", source="editor")]
        self.assertEqual(scope.non_rest(inside, A, SCENE), [])
        self.assertEqual(scope.non_rest(outside, A, SCENE), outside)

    def test_cut_off_file_lists_count(self):
        recs = journal.flatten(support.fixture_entries("files_omitted.jsonl"))
        self.assertEqual([(r["kind"], r["source"]) for r in scope.non_rest(recs, A, SCENE)], [("omitted", "editor")])

    def test_only_the_run_scene_may_be_saved_or_opened_by_the_editor(self):
        mine = [support.scene_rec("scene_saved", SCENE, source="editor"),
                support.scene_rec("scene_opened", SCENE, source="editor")]
        theirs = [support.scene_rec("scene_saved", OTHER, source="editor"),
                  support.scene_rec("scene_opened", OTHER, source="editor")]
        self.assertEqual(scope.non_rest(mine, A, SCENE), [])
        self.assertEqual(scope.non_rest(theirs, A, SCENE), theirs)
        self.assertEqual(scope.non_rest(mine, A, None), mine)      # without a run scene nothing is exempt

    def test_scene_edits_by_the_editor_count_even_inside_r(self):
        manual = [support.rec("gameobject_created", source="editor", path=R + "/X"),
                  support.rec("undo_redo", type="undo_redo", source="editor"),
                  support.prop(R + "/X", source="editor")]
        self.assertEqual(scope.non_rest(manual, A, SCENE), manual)


class DirectWritesTests(unittest.TestCase):
    def test_refresh_window_and_editor_files_under_a(self):
        # 206 Unity importing a script on its own (two records), 207 the runner's refresh picking up Notes.txt
        self.assertEqual(seqs(scope.direct_writes(noisy(), A)), [206, 206, 207])

    def test_clean_attempt_has_no_direct_writes(self):
        self.assertEqual(scope.direct_writes(clean(), A), [])

    def test_files_outside_a_are_not_direct_writes_of_a(self):
        records = [support.file_rec("imported", "Assets/Other.mat", origin="refresh"),
                   support.file_rec("imported", "Assets/Other.mat", source="editor")]
        self.assertEqual(scope.direct_writes(records, A), [])

    def test_a_move_touching_a_counts(self):
        into = support.file_rec("moved", A + "/y.mat", source="editor", **{"from": "Assets/x.mat"})
        within = support.file_rec("moved", A + "/y.mat", origin="refresh", **{"from": A + "/x.mat"})
        away = support.file_rec("moved", "Assets/y.mat", source="editor", **{"from": A + "/x.mat"})
        self.assertEqual(scope.direct_writes([into, within, away], A), [into, within, away])

    def test_meta_files_follow_their_asset(self):
        meta = support.file_rec("imported", A + "/x.mat.meta", origin="refresh")
        self.assertEqual(scope.direct_writes([meta], A), [meta])

    def test_only_file_records_are_direct_writes(self):
        records = [support.prop(R + "/X", source="editor"), support.scene_rec("scene_saved", SCENE, source="editor"),
                   support.file_rec("imported", A + "/x.mat", source="rest")]
        self.assertEqual(scope.direct_writes(records, A), [])


class WriteRecordsTests(unittest.TestCase):
    def test_clean_attempt_without_its_own_scene_save(self):
        # the run scene's scene_saved (106) and the import of the scene file and its .meta (107 x2) are left out
        self.assertEqual(seqs(scope.write_records(clean(), SCENE, RUNNER)), [101, 102, 103, 104, 104, 105])

    def test_play_mode_and_the_runners_attempt_window_records_are_left_out(self):
        left = scope.write_records(noisy(), SCENE, RUNNER)
        self.assertEqual(seqs(left), [201, 202, 203, 204, 206, 206, 207, 209])

    def test_the_runner_refresh_window_stays(self):
        self.assertIn(207, seqs(scope.write_records(noisy(), SCENE, RUNNER)))

    def test_without_a_runner_agent_its_records_stay(self):
        self.assertIn(208, seqs(scope.write_records(noisy(), SCENE)))

    def test_opening_the_run_scene_or_touching_its_file_otherwise_counts(self):
        records = [support.scene_rec("scene_opened", SCENE), support.file_rec("deleted", SCENE),
                   support.file_rec("moved", SCENE, **{"from": A + "/old.unity"})]
        self.assertEqual(scope.write_records(records, SCENE, RUNNER), records)

    def test_other_scenes_and_files_count(self):
        records = [support.scene_rec("scene_saved", OTHER), support.file_rec("imported", OTHER),
                   support.file_rec("imported", SCENE + ".bak"), support.file_rec("imported", A + "/Eval_other.unity")]
        self.assertEqual(scope.write_records(records, SCENE, RUNNER), records)

    def test_run_scene_file_and_meta_imports_are_left_out(self):
        records = [support.file_rec("imported", SCENE), support.file_rec("imported", SCENE + ".meta")]
        self.assertEqual(scope.write_records(records, SCENE, RUNNER), [])

    def test_without_a_run_scene_everything_counts(self):
        records = [support.scene_rec("scene_saved", SCENE), support.file_rec("imported", SCENE)]
        self.assertEqual(scope.write_records(records, None, RUNNER), records)


class RunSceneSaveAllowanceTests(unittest.TestCase):
    """G2: this run's scene may be saved; no other scene may be saved or opened."""

    def violations(self, *records, **kwargs):
        return scope.scope_violations(list(records), R, A, SCENE, **kwargs)

    def test_saving_or_opening_the_run_scene_is_allowed(self):
        self.assertEqual(self.violations(support.scene_rec("scene_saved", SCENE),
                                         support.scene_rec("scene_opened", SCENE),
                                         support.file_rec("imported", SCENE),
                                         support.file_rec("imported", SCENE + ".meta")), [])

    def test_saving_or_opening_any_other_scene_is_a_violation(self):
        saved = support.scene_rec("scene_saved", OTHER)
        opened = support.scene_rec("scene_opened", OTHER)
        sibling = support.scene_rec("scene_saved", A + "/Eval_" + support.OTHER_RUN_ID + ".unity")
        self.assertEqual(self.violations(saved, opened, sibling), [saved, opened, sibling])

    def test_importing_a_scene_file_elsewhere_is_a_violation(self):
        stray = support.file_rec("imported", OTHER)
        self.assertEqual(self.violations(stray), [stray])

    def test_without_a_run_scene_every_save_is_a_violation(self):
        saved = support.scene_rec("scene_saved", SCENE)
        self.assertEqual(scope.scope_violations([saved], R, A), [saved])

    def test_path_spelling_does_not_matter(self):
        self.assertEqual(self.violations(support.scene_rec("scene_saved", SCENE.replace("/", "\\"))), [])

    def test_a_longer_name_is_another_scene(self):
        saved = support.scene_rec("scene_saved", SCENE + ".bak")
        self.assertEqual(self.violations(saved), [saved])


class VerifyContextTests(unittest.TestCase):
    """The ctx helpers a private verifier calls, over the same fixtures (task_api delegates here)."""

    def context(self, records, **kwargs):
        scope_ = fixture.Scope(support.RUN_ID, 100)
        return VerifyContext(support.sample_scene(), scope_, 7001, "/tmp/project", {}, "en", "ops", "basic", None,
                             records=records, **kwargs)

    def test_violations_leave_out_play_mode_and_the_runners_attempt_records(self):
        ctx = self.context(noisy())
        self.assertEqual(seqs(ctx.violations()), [201, 202, 203, 209])

    def test_violations_accept_an_extra_exemption_and_allowed_change(self):
        ctx = self.context(noisy(), allowed_change=lambda c, rec: rec["seq"] == 202)
        self.assertEqual(seqs(ctx.violations()), [201, 203, 209])
        self.assertEqual(seqs(ctx.violations(extra=lambda rec: rec["seq"] == 201)), [203, 209])

    def test_non_rest_direct_writes_and_write_records(self):
        ctx = self.context(noisy())
        self.assertEqual(seqs(ctx.non_rest()), [204, 209])
        self.assertEqual(seqs(ctx.direct_writes()), [206, 206, 207])
        self.assertEqual(seqs(ctx.write_records()), [201, 202, 203, 204, 206, 206, 207, 209])

    def test_allowed_change_reaches_non_rest(self):
        ctx = self.context(noisy(), allowed_change=lambda c, rec: rec["seq"] == 209)
        self.assertEqual(seqs(ctx.non_rest()), [204])

    def test_allowed_change_receives_the_context(self):
        seen = []
        ctx = self.context(noisy(), allowed_change=lambda c, rec: seen.append(c) or False)
        ctx.non_rest()
        self.assertTrue(seen)
        self.assertTrue(all(c is ctx for c in seen))

    def test_records_of_several_callers_stay_in_the_verdict(self):
        stray = support.prop("Main Camera", scene=support.OTHER_SCENE, agent="mixed")
        unknown = support.prop("Main Camera", scene=support.OTHER_SCENE, agent="unknown")
        ctx = self.context([stray, unknown])
        self.assertEqual(ctx.violations(), [stray, unknown])

    def test_saved_scenes(self):
        ctx = self.context(noisy())
        self.assertEqual(seqs(ctx.saved_scenes()), [203, 209, 210])
        self.assertEqual(seqs(ctx.saved_other_scenes()), [203, 209])

    def test_clean_attempt_through_the_context(self):
        ctx = self.context(clean())
        self.assertEqual((ctx.violations(), ctx.non_rest(), ctx.direct_writes(), ctx.saved_other_scenes()),
                         ([], [], [], []))
        self.assertEqual(len(ctx.saved_scenes()), 1)
        self.assertEqual(seqs(ctx.write_records()), [101, 102, 103, 104, 104, 105])


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
