"""usk_eval.scope.direct_writes with a run scene: the import right after a REST save is the save's own doing."""
import unittest

try:
    from . import test_verifylib_support as support
except ImportError:
    import test_verifylib_support as support

from usk_eval import journal, scope

A, SCENE, OTHER = support.A, support.SCENE, support.OTHER_SCENE
SAVE_WINDOW = {"from": 605, "to": 606}


def seqs(records):
    return [r["seq"] for r in records]


def saved(**fields):
    return support.scene_rec("scene_saved", SCENE, **fields)


def imported(path=SCENE, **fields):
    fields.setdefault("source", "editor")
    return support.file_rec("imported", path, **fields)


class RunSceneParameterTests(unittest.TestCase):
    def test_the_default_still_counts_every_editor_sourced_import_under_a(self):
        records = [saved(), imported(), imported(SCENE + ".meta")]
        self.assertEqual(scope.direct_writes(records, A), [records[1], records[2]])
        self.assertEqual(scope.direct_writes(records, A, None), [records[1], records[2]])

    def test_the_scene_file_and_its_meta_right_after_a_rest_save_do_not_count(self):
        records = [saved(), imported(), imported(SCENE + ".meta")]
        self.assertEqual(scope.direct_writes(records, A, SCENE), [])

    def test_whichever_source_the_plugin_gave_the_import(self):
        for source in ("editor", "rest"):
            with self.subTest(source=source):
                records = [saved(), imported(source=source)]
                self.assertEqual(scope.direct_writes(records, A, SCENE), [])

    def test_other_files_in_the_same_burst_still_count(self):
        script = imported(A + "/Scripts/Direct.cs")
        records = [saved(), imported(), script, imported(SCENE + ".meta")]
        self.assertEqual(scope.direct_writes(records, A, SCENE), [script])

    def test_file_records_between_the_save_and_the_scene_import_keep_the_window_open(self):
        script = imported(A + "/Scripts/Direct.cs")
        records = [saved(), script, imported(A + "/x.mat", source="rest"), imported()]
        self.assertEqual(scope.direct_writes(records, A, SCENE), [script])

    def test_any_other_record_in_between_closes_the_window(self):
        for between in [support.prop(support.R + "/X"), support.rec("undo_redo", type="undo_redo"),
                        support.scene_rec("scene_opened", SCENE), support.rec("omitted", count=2)]:
            with self.subTest(between=between["kind"]):
                records = [saved(), between, imported()]
                self.assertEqual(scope.direct_writes(records, A, SCENE), [records[2]])

    def test_an_import_before_the_save_is_not_explained(self):
        records = [imported(), saved()]
        self.assertEqual(scope.direct_writes(records, A, SCENE), [records[0]])

    def test_only_a_rest_save_explains_its_import(self):
        records = [saved(source="editor"), imported()]
        self.assertEqual(scope.direct_writes(records, A, SCENE), [records[1]])

    def test_a_refresh_window_import_is_the_files_found_on_disk_and_still_counts(self):
        records = [saved(), imported(origin="refresh", source="rest", agent=support.RUNNER)]
        self.assertEqual(scope.direct_writes(records, A, SCENE), [records[1]])

    def test_only_imports_are_explained(self):
        deleted = support.file_rec("deleted", SCENE, source="editor")
        moved = support.file_rec("moved", SCENE, source="editor", **{"from": A + "/Old.unity"})
        records = [saved(), deleted, moved]
        self.assertEqual(scope.direct_writes(records, A, SCENE), [deleted, moved])

    def test_saving_another_scene_explains_nothing(self):
        elsewhere = support.scene_rec("scene_saved", OTHER)
        records = [elsewhere, imported()]
        self.assertEqual(scope.direct_writes(records, A, SCENE), [records[1]])

    def test_a_later_save_of_another_scene_closes_the_window(self):
        records = [saved(), support.scene_rec("scene_saved", OTHER), imported()]
        self.assertEqual(scope.direct_writes(records, A, SCENE), [records[2]])

    def test_every_save_explains_only_its_own_imports(self):
        records = [saved(seq=1), imported(seq=2), support.prop(support.R + "/X", seq=3), imported(seq=4),
                   saved(seq=5), imported(seq=6)]
        self.assertEqual(seqs(scope.direct_writes(records, A, SCENE)), [4])

    def test_path_spelling_does_not_matter(self):
        records = [support.scene_rec("scene_saved", SCENE.replace("/", "\\")), imported(SCENE.replace("/", "\\"))]
        self.assertEqual(scope.direct_writes(records, A, SCENE), [])

    def test_a_scene_with_a_longer_name_is_another_file(self):
        stray = imported(SCENE + ".bak")
        self.assertEqual(scope.direct_writes([saved(), stray], A, SCENE), [stray])

    def test_records_may_come_from_any_iterable_and_are_left_alone(self):
        records = [saved(), imported(), imported(A + "/Scripts/Direct.cs")]
        before = [dict(r) for r in records]
        self.assertEqual(len(scope.direct_writes(iter(records), A, SCENE)), 1)
        self.assertEqual(records, before)


class SaveImportFixtureTests(unittest.TestCase):
    def records(self):
        return journal.flatten(support.fixture_entries("scene_save_import.jsonl"), SAVE_WINDOW)

    def test_without_the_run_scene_the_save_import_looks_like_a_direct_write(self):
        self.assertEqual(seqs(scope.direct_writes(self.records(), A)), [603, 603, 605, 605, 606])

    def test_with_the_run_scene_only_the_real_direct_writes_remain(self):
        left = scope.direct_writes(self.records(), A, SCENE)
        self.assertEqual(seqs(left), [605, 605, 606])
        self.assertEqual([r["assetPath"] for r in left], [A + "/Scripts/Direct.cs", A + "/Scripts/Direct.cs.meta",
                                                          SCENE])
        self.assertEqual(left[-1]["origin"], "refresh")

    def test_the_other_evidence_classes_are_unchanged(self):
        recs = self.records()
        self.assertEqual(scope.non_rest(recs, A, SCENE), [])                  # editor imports under A are exempt
        self.assertEqual(scope.scope_violations(recs, support.R, A, SCENE), [])
        left = scope.write_records(recs, SCENE, support.RUNNER)               # every run scene import is left out
        self.assertEqual(seqs(left), [601, 604, 605, 605])


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
