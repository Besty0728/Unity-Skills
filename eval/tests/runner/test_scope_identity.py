"""usk_eval.scope identity table (eval/design/scope-verifier-fix): a pathless destruction is judged by the last
create or reparent the journal observed for its identity, and unknown identities fail closed."""
import unittest

try:
    from . import test_verifylib_support as support
except ImportError:
    import test_verifylib_support as support

from usk_eval import journal, scope

R, A, SCENE, OTHER = support.R, support.A, support.SCENE, support.OTHER_SCENE
created, reparented, destroyed = support.created, support.reparented, support.destroyed


def violations(records, **kwargs):
    return scope.scope_violations(records, R, A, SCENE, **kwargs)


class ReadmeCaseTests(unittest.TestCase):
    """The five offline cases of the patch README."""

    def test_in_scope_create_rollback_then_pathless_destroy_passes(self):
        records = [created(-10, R + "/Transient"), support.rec("undo_redo", type="undo_redo"), destroyed(-10)]
        self.assertEqual(violations(records), [])

    def test_unknown_identity_destroy_fails_closed(self):
        record = destroyed(-11)
        self.assertEqual(violations([record]), [record])

    def test_outside_created_identity_destroy_fails(self):
        gone = destroyed(-12)
        records = [created(-12, "Main Camera", scene=OTHER), gone]
        self.assertIn(gone, violations(records))

    def test_in_scope_identity_moved_outside_then_destroyed_fails(self):
        gone = destroyed(-13)
        records = [created(-13, R + "/Transient"), reparented(-13, R + "/Transient", "Outside/Transient"), gone]
        bad = violations(records)
        self.assertIn(gone, bad)
        self.assertIn(records[1], bad)

    def test_outside_created_identity_adopted_into_r_then_destroyed_passes(self):
        records = [created(-14, "Canvas/Transient"), reparented(-14, "Canvas/Transient", R + "/Transient"),
                   destroyed(-14)]
        self.assertEqual(violations(records), [])


class LocationTrackingTests(unittest.TestCase):
    def test_destroy_after_reparent_inside_r_uses_the_latest_location(self):
        records = [created(-15, R + "/A"), reparented(-15, R + "/A", R + "/B/A"), destroyed(-15)]
        self.assertEqual(violations(records), [])

    def test_destroy_after_reparent_out_of_r_fails_even_if_created_inside(self):
        gone = destroyed(-16)
        records = [created(-16, R + "/A"), reparented(-16, R + "/A", "Outside/A"),
                   reparented(-16, "Outside/A", "Other/A"), gone]
        self.assertIn(gone, violations(records))

    def test_destroy_after_reparent_back_into_r_passes(self):
        records = [created(-17, R + "/A"), reparented(-17, R + "/A", "Outside/A"),
                   reparented(-17, "Outside/A", R + "/Back/A"), destroyed(-17)]
        bad = violations(records)
        self.assertEqual(bad, [records[1]])           # only the excursion itself; the destroy follows the latest path

    def test_a_scene_other_than_the_run_scene_is_out_of_scope(self):
        gone = destroyed(-18)
        records = [created(-18, R + "/A", scene=OTHER), gone]
        self.assertEqual(violations(records), records)

    def test_the_scene_is_not_compared_without_a_run_scene(self):
        records = [created(-19, R + "/A", scene=OTHER), destroyed(-19)]
        self.assertEqual(scope.scope_violations(records, R, A), [])

    def test_a_scene_name_survives_a_reparent_record_without_one(self):
        gone = destroyed(-20)
        move = reparented(-20, R + "/A", R + "/B/A", scene=None)
        records = [created(-20, R + "/A", scene=OTHER), move, gone]
        self.assertIn(gone, violations(records))

    def test_only_create_and_reparent_prove_a_location(self):
        for other in [support.prop(R + "/X", entityId="-21", instanceId=-21),
                      support.rec("components_changed", path=R + "/X", scenePath=SCENE, entityId="-21", instanceId=-21),
                      support.rec("children_reordered", path=R + "/X", scenePath=SCENE, entityId="-21",
                                  instanceId=-21)]:
            with self.subTest(kind=other["kind"]):
                gone = destroyed(-21)
                self.assertEqual(violations([other, gone]), [gone])

    def test_catalog_known_destruction_keeps_the_path_rule(self):
        inside = destroyed(-22, path=R + "/Gone")
        outside = destroyed(-23, path="Main Camera", scene=OTHER)
        self.assertEqual(violations([inside, outside]), [outside])


class IdentityReuseTests(unittest.TestCase):
    def test_the_identity_is_forgotten_once_destroyed(self):
        first, second = destroyed(-30, seq=2), destroyed(-30, seq=3)
        self.assertEqual(violations([created(-30, R + "/X"), first, second]), [second])

    def test_a_reused_id_created_outside_does_not_inherit_the_old_location(self):
        reuse = created(-31, "Outside/Y", scene=OTHER, seq=3)
        again = destroyed(-31, seq=4)
        records = [created(-31, R + "/X", seq=1), destroyed(-31, seq=2), reuse, again]
        self.assertEqual(violations(records), [reuse, again])

    def test_a_destruction_with_a_path_forgets_the_identity_too(self):
        later = destroyed(-32, seq=3)
        records = [created(-32, R + "/X"), destroyed(-32, path=R + "/X", seq=2), later]
        self.assertEqual(violations(records), [later])

    def test_an_allowed_destruction_forgets_the_identity_too(self):
        first, later = destroyed(-33, seq=2), destroyed(-33, seq=3)
        records = [created(-33, "Outside/Q", scene=OTHER, seq=1), first, later]
        bad = violations(records, extra_allowed=lambda r: r is first)
        self.assertEqual(bad, [records[0], later])

    def test_an_allowed_create_still_proves_its_location(self):
        records = [created(-35, R + "/X"), destroyed(-35)]
        self.assertEqual(violations(records, extra_allowed=lambda r: r["kind"] == "gameobject_created"), [])

    def test_a_later_create_restores_the_table_after_a_destroy(self):
        records = [created(-34, R + "/X"), destroyed(-34), created(-34, R + "/Z"), destroyed(-34)]
        self.assertEqual(violations(records), [])


class IdentityFormatTests(unittest.TestCase):
    def test_a_pathless_destroy_without_any_identity_fails(self):
        bare = support.rec("gameobject_destroyed")
        zero = support.rec("gameobject_destroyed", entityId="0", instanceId=0)
        blank = support.rec("gameobject_destroyed", entityId="", instanceId=None)
        self.assertEqual(violations([bare, zero, blank]), [bare, zero, blank])

    def test_zero_is_never_an_identity(self):
        first = support.rec("gameobject_created", entityId="0", instanceId=0, path=R + "/X", scenePath=SCENE)
        gone = support.rec("gameobject_destroyed", entityId="0", instanceId=0)
        self.assertEqual(violations([first, gone]), [gone])

    def test_instance_id_and_entity_id_spell_the_same_identity_before_6000_4(self):
        legacy_create = support.rec("gameobject_created", instanceId=-40, path=R + "/X", scenePath=SCENE)
        gone = support.rec("gameobject_destroyed", entityId="-40", instanceId=-40)
        self.assertEqual(violations([legacy_create, gone]), [])
        legacy_gone = support.rec("gameobject_destroyed", instanceId=-40)
        create = created(-40, R + "/X")
        self.assertEqual(violations([create, legacy_gone]), [])

    def test_entity_ids_of_6000_4_with_instance_id_zero(self):
        recs = journal.flatten(support.fixture_entries("ids_6000_4.jsonl"))
        bad = violations(recs)
        self.assertEqual([r["seq"] for r in bad], [404])
        self.assertEqual(bad[0]["entityId"], "18446744073709551609")

    def test_the_entity_id_decides_when_the_two_spellings_disagree(self):
        first = support.rec("gameobject_created", entityId="900", instanceId=5, path=R + "/X", scenePath=SCENE)
        other = support.rec("gameobject_destroyed", entityId="901", instanceId=5)
        same = support.rec("gameobject_destroyed", entityId="900", instanceId=6)
        self.assertEqual(violations([first, other]), [other])
        self.assertEqual(violations([first, same]), [])

    def test_distinct_entity_ids_do_not_share_a_location(self):
        a, b = "18446744073709551601", "18446744073709551602"
        records = [support.rec("gameobject_created", entityId=a, instanceId=0, path=R + "/A", scenePath=SCENE),
                   support.rec("gameobject_destroyed", entityId=b, instanceId=0)]
        self.assertEqual(violations(records), [records[1]])

    def test_a_bool_is_not_an_identity(self):
        first = support.rec("gameobject_created", entityId=True, path=R + "/X", scenePath=SCENE)
        gone = support.rec("gameobject_destroyed", entityId=True)
        self.assertEqual(violations([first, gone]), [gone])


class RollbackFixtureTests(unittest.TestCase):
    def test_rollback_journal_passes_through_the_flattened_records(self):
        recs = journal.flatten(support.fixture_entries("rollback_destroy.jsonl"))
        self.assertEqual([r["kind"] for r in recs], ["gameobject_created", "undo_redo", "gameobject_destroyed"])
        self.assertEqual(violations(recs), [])

    def test_the_same_journal_without_the_observed_create_fails_closed(self):
        entries = [e for e in support.fixture_entries("rollback_destroy.jsonl") if e["seq"] != 301]
        bad = violations(journal.flatten(entries))
        self.assertEqual([r["kind"] for r in bad], ["gameobject_destroyed"])


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
