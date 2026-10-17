"""Offline checks of T3 (physics bridge) against fake_ops_t3: the common task checklist of pack_offline plus the layer,
collision-matrix and teardown behaviour that only this task has. The live selftest remains the gate."""
import sys
import unittest

from .fake_ops_t3 import FakeEditorT3
from .pack_offline import Offline, OpsTaskTests, load_modules
from usk_eval import pack, verifylib  # noqa: E402
from usk_eval.verifylib import address  # noqa: E402
from usk_eval.rest import RUNNER_AGENT  # noqa: E402
from usk_eval.task_api import BuildContext  # noqa: E402


class DisabledPairEditor(FakeEditorT3):
    def __init__(self, *args, **kwargs):
        super(DisabledPairEditor, self).__init__(*args, **kwargs)
        self.collision[(5, 30)] = False
        self.collision[(31, 31)] = False


class StuckPairEditor(DisabledPairEditor):
    def _skill_physics_set_layer_collision(self, agent, args):
        return {"success": True}


class UserLayersEditor(FakeEditorT3):
    def __init__(self, *args, **kwargs):
        super(UserLayersEditor, self).__init__(*args, **kwargs)
        self.layer_names[9] = "Props"
        self.layer_names[12] = "Enemies"


class TakenLayerEditor(FakeEditorT3):
    def __init__(self, *args, **kwargs):
        super(TakenLayerEditor, self).__init__(*args, **kwargs)
        self.layer_names[31] = "Enemies"


class StaleNamesEditor(FakeEditorT3):
    def __init__(self, *args, **kwargs):
        super(StaleNamesEditor, self).__init__(*args, **kwargs)
        self.layer_names[30] = "EvqqqPlank"


class T3Tests(OpsTaskTests, unittest.TestCase):
    TASK_ID = "T3"
    EDITOR = FakeEditorT3
    SEED = 7708
    FAILS_UNTOUCHED = {"P1", "P2", "P3", "P4", "P5", "P6", "P7", "P8", "P11"}

    def setUp(self):
        task, private, _solutions = load_modules("T3")
        for module in (sys.modules[type(task).__module__], private):
            self.addCleanup(setattr, module, "PAUSE", module.PAUSE)
            module.PAUSE = 0.0

    def test_task_attributes(self):
        task = load_modules("T3")[0]
        self.assertEqual((task.id, task.track, task.tier, task.case_version, task.minutes),
                         ("T3", "ops", "standard", 2, 25))
        self.assertEqual(pack.normalize_requires(task.requires),
                         {"pipeline": "URP", "mode": "bypass", "dedicated": True, "packages": []})
        self.assertEqual(len(task.layer_pairs), 63)
        self.assertTrue(all(pair == tuple(sorted(pair)) and (30 in pair or 31 in pair) for pair in task.layer_pairs))

    def test_layers_are_named_for_the_attempt_and_cleared_by_teardown(self):
        run = self.run_for()
        names = run.editor.layer_names
        self.assertEqual((names[30], names[31]),
                         (run.handles["plankLayer"]["name"], run.handles["guardLayer"]["name"]))
        self.assertEqual(run.snap["data"]["layers"]["30"], names[30])
        run.teardown()
        self.assertNotIn(30, run.editor.layer_names)
        self.assertNotIn(31, run.editor.layer_names)

    def test_teardown_works_from_the_live_state_without_handles(self):
        run = self.run_for()
        client = run.editor.client(RUNNER_AGENT)
        ctx = BuildContext(client, run.scope, run.seed, run.project, run.env, "en", "ops", "standard", run.task,
                           handles={})
        run.task.teardown(ctx)
        self.assertEqual(sorted(run.editor.layer_names), [0, 1, 2, 4, 5])

    def test_teardown_restores_every_user_layer_to_its_name_before_the_build(self):
        run = Offline(self, "T3", UserLayersEditor, self.SEED)
        self.assertEqual(run.handles["layersAtStart"], {"9": "Props", "12": "Enemies"})
        names = run.editor.layer_names
        names[29] = "Added"
        names[9] = "Renamed"
        del names[12]
        names[31] = "Changed"
        run.teardown()
        self.assertEqual(names, {0: "Default", 1: "TransparentFX", 2: "Ignore Raycast", 4: "Water", 5: "UI",
                                 9: "Props", 12: "Enemies"})

    def test_teardown_leaves_a_foreign_layer_name_alone(self):
        run = self.run_for()
        run.editor.layer_names[30] = "Enemies"
        client = run.editor.client(RUNNER_AGENT)
        ctx = BuildContext(client, run.scope, run.seed, run.project, run.env, "en", "ops", "standard", run.task)
        run.task.teardown(ctx)
        self.assertEqual(run.editor.layer_names[30], "Enemies")
        self.assertNotIn(31, run.editor.layer_names)

    def test_build_refuses_a_slot_named_by_something_else(self):
        with self.assertRaises(ValueError):
            Offline(self, "T3", TakenLayerEditor, self.SEED)

    def test_build_takes_over_stale_names_of_its_own_kind(self):
        run = Offline(self, "T3", StaleNamesEditor, self.SEED)
        self.assertEqual(run.editor.layer_names[30], run.handles["plankLayer"]["name"])

    def test_pairs_start_enabled_and_the_runner_restore_puts_them_back(self):
        run = Offline(self, "T3", DisabledPairEditor, self.SEED)
        self.assertTrue(all(run.snap["data"]["collision"].values()))
        self.assertEqual(len(run.snap["data"]["collision"]), 63)
        self.assertEqual(run.collision["5,30"], False)
        run.teardown()
        self.assertFalse(run.editor.collision[(5, 30)])
        self.assertFalse(run.editor.collision[(31, 31)])
        self.assertTrue(run.editor.collision.get((30, 31), True))

    def test_build_fails_when_the_pairs_cannot_be_enabled(self):
        with self.assertRaises(ValueError):
            Offline(self, "T3", StuckPairEditor, self.SEED)

    def test_the_plank_with_the_sphere_collider_has_it_ahead_of_its_box(self):
        run = self.run_for()
        holders = [ref for ref in run.handles["planks"]
                   if [c["type"] for c in run.build_ctx.probe.components(ref)].count("SphereCollider")]
        self.assertEqual(len(holders), 1)
        kinds = [c["type"] for c in run.build_ctx.probe.components(holders[0])]
        self.assertLess(kinds.index("SphereCollider"), kinds.index("BoxCollider"))

    def test_sibling_order_differs_from_the_order_by_x(self):
        for seed in (7708, 7709, 7710):
            run = self.run_for(seed=seed)
            states = run.snap["data"]["planks"]
            by_x = run.private.by_x([(row[0], row[1]["position"]["x"]) for row in states])
            self.assertNotEqual(by_x, list(run.handles["planks"]))
            self.assertEqual(len(set(map(str, by_x))), 5)

    def test_connected_body_needs_the_hidden_property_listing(self):
        run = self.run_for()
        run.solve(run.solutions.reference)
        client = run.editor.client(RUNNER_AGENT)
        address = verifylib.address(run.handles["planks"][0])
        plain = client.read("component_get_serialized_properties", componentType="HingeJoint", **address)
        hidden = client.read("component_get_serialized_properties", componentType="HingeJoint", includeHidden=True,
                             **address)
        self.assertNotIn("m_ConnectedBody", [row["propertyPath"] for row in plain["properties"]])
        row = [row for row in hidden["properties"] if row["propertyPath"] == "m_ConnectedBody"][0]
        self.assertEqual(row["referenceState"], "assigned")

    def test_edge_cases_fail_exactly_the_assertions_they_break(self):
        solutions = load_modules("T3")[2]

        def sphere_gets_material(ctx):
            answer = solutions.reference(ctx)
            ctx.c.write("component_set_serialized_property", componentType="SphereCollider", propertyPath="m_Material",
                        assetPath=ctx.handles["materialPath"], **address(solutions._numbering(ctx)[2]))
            return answer

        def plank_renamed(ctx):
            answer = solutions.reference(ctx)
            ctx.c.write("gameobject_rename", newName="Plank_3", **address(solutions._numbering(ctx)[2]))
            return answer

        def rail_deleted(ctx):
            answer = solutions.reference(ctx)
            ctx.c.write("gameobject_delete", **address(ctx.handles["rails"][1]))
            return answer

        def planks_reordered(ctx):
            answer = solutions.reference(ctx)
            ctx.c.write("gameobject_set_sibling_index", index=0, **address(ctx.handles["planks"][-1]))
            return answer

        def anchor_released(ctx):
            answer = solutions.reference(ctx)
            ctx.c.write("component_set_property", componentType="Rigidbody", propertyName="isKinematic",
                        value="false", **address(ctx.handles["anchor"]))
            return answer

        def plank_deactivated(ctx):
            answer = solutions.reference(ctx)
            ctx.c.write("gameobject_set_active", active=False, **address(ctx.handles["planks"][0]))
            return answer

        def one_joint_unlinked(ctx):
            answer = solutions.reference(ctx)
            ctx.c.write("component_set_serialized_property", componentType="HingeJoint",
                        propertyPath="m_ConnectedBody", **address(solutions._numbering(ctx)[3]))
            return answer

        def repeated_row(ctx):
            answer = solutions.reference(ctx)
            answer["planks"][1] = dict(answer["planks"][0])
            return answer

        def flag_as_index(ctx):
            answer = solutions.reference(ctx)
            answer["plankLayerIndex"] = True
            return answer

        def no_plank_list(ctx):
            answer = solutions.reference(ctx)
            del answer["planks"]
            return answer

        cases = (("sphere_gets_material", sphere_gets_material, {"P6"}), ("plank_renamed", plank_renamed, {"P9"}),
                 ("rail_deleted", rail_deleted, {"P7", "P9"}), ("planks_reordered", planks_reordered, {"P9"}),
                 ("anchor_released", anchor_released, {"P9"}), ("plank_deactivated", plank_deactivated, {"P9"}),
                 ("one_joint_unlinked", one_joint_unlinked, {"P2"}), ("repeated_row", repeated_row, {"P11"}),
                 ("flag_as_index", flag_as_index, {"P11"}), ("no_plank_list", no_plank_list, {"P11"}))
        for mode in ("legacy", "6000.4"):
            for name, fn, failed in cases:
                with self.subTest(case=name, ids=mode):
                    run = self.run_for(entity_mode=mode)
                    self.assertEqual(run.failed(fn), failed)

    def test_allowed_change_only_tolerates_the_two_settings_files(self):
        private = load_modules("T3")[1]
        for path in ("ProjectSettings/DynamicsManager.asset", "ProjectSettings/DynamicsManager.asset.meta",
                     "ProjectSettings/TagManager.asset"):
            self.assertTrue(private.allowed_change(None, {"assetPath": path}), path)
        for path in ("ProjectSettings/ProjectSettings.asset", "Assets/Other.mat", "", None):
            self.assertFalse(private.allowed_change(None, {"assetPath": path}), path)

# Producer:Betsy
