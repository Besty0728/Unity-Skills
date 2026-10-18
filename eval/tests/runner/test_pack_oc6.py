"""Offline OC6 matrix using only the in-memory editor and worktree-local temporary projects."""
import json
import os
import tempfile
import unittest
from unittest import mock

from .pack_offline import OpsTaskTests, detail, load_modules
from .pack_fake_editor import REPO
from .fake_oc6 import GateEditor, SURFACE
from usk_eval import verifylib as V
from usk_eval.rest import SOLVER_AGENT


class OC6Tests(OpsTaskTests, unittest.TestCase):
    TASK_ID = "OC6"
    EDITOR = GateEditor
    SEED = 9930
    SECOND_SEED = 99173
    TIER = "challenge"
    FAILS_UNTOUCHED = {"A1", "A2", "A9"}

    def setUp(self):
        folder = os.path.join(REPO, "eval", "runs")
        created = not os.path.isdir(folder)
        os.makedirs(folder, exist_ok=True)
        if created:
            self.addCleanup(os.rmdir, folder)
        patch = mock.patch.object(tempfile, "tempdir", folder)
        patch.start()
        self.addCleanup(patch.stop)
        patch = mock.patch("socket.socket", side_effect=AssertionError("No sockets in the OC6 test"))
        patch.start()
        self.addCleanup(patch.stop)

    def test_task_attributes(self):
        task = load_modules("OC6")[0]
        self.assertEqual((task.id, task.track, task.tier, task.case_version, task.minutes, task.needs_focus),
                         ("OC6", "ops", "challenge", 1, 25, False))
        self.assertEqual(task.requires, {"pipeline": "URP", "mode": "bypass", "dedicated": True, "packages": []})

    def test_second_seed_full_matrix_both_id_modes(self):
        solutions = load_modules("OC6")[2]
        for mode in ("legacy", "6000.4"):
            run = self.run_for(seed=self.SECOND_SEED, entity_mode=mode)
            card = run.verify(run.solve(solutions.reference))
            self.assertTrue(card.strict, detail(card))
            for negative in solutions.NEGATIVES:
                with self.subTest(seed=self.SECOND_SEED, mode=mode, negative=negative.name):
                    run = self.run_for(seed=self.SECOND_SEED, entity_mode=mode)
                    card = run.verify(run.solve(negative.apply))
                    self.assertEqual(set(card.failed()), negative.expect_failed, detail(card))
            for name, apply in solutions.ALTERNATIVES:
                with self.subTest(seed=self.SECOND_SEED, mode=mode, alternative=name):
                    run = self.run_for(seed=self.SECOND_SEED, entity_mode=mode)
                    card = run.verify(run.solve(apply))
                    self.assertTrue(card.strict, detail(card))

    def test_second_seed_rearranges_names_and_structure(self):
        first, second = self.run_for(), self.run_for(seed=self.SECOND_SEED)
        self.assertNotEqual(first.fingerprint, second.fingerprint)
        a, b = first.task.read_scene(first.build_ctx), second.task.read_scene(second.build_ctx)
        self.assertNotEqual([row["name"] for row in a], [row["name"] for row in b])
        self.assertNotEqual([len(row["children"]) for row in a], [len(row["children"]) for row in b])
        self.assertNotEqual([row["path"].count("/") for row in a], [row["path"].count("/") for row in b])

    def test_c0_values_are_identities_paths_and_digests(self):
        run = self.run_for()
        self.assertEqual(set(run.snap["data"]), {"nodes", "paths"})
        for ident, path, kids in run.snap["data"]["nodes"]:
            self.assertIsNotNone(V.object_id(ident))
            self.assertTrue(path.startswith(run.scope.object_root))
            self.assertTrue(all(V.object_id(kid) is not None for kid in kids))
        for group in run.snap["digests"].values():
            for digest in group.values():
                self.assertRegex(digest, r"^[0-9a-f]{64}$")
        json.dumps(run.snap, allow_nan=False)

    def test_untouched_second_seed_and_report_independence(self):
        for seed in (self.SEED, self.SECOND_SEED):
            run = self.run_for(seed=seed)
            self.assertEqual(set(run.verify(None).failed()), self.FAILS_UNTOUCHED)
            result = run.solve(run.solutions._report)
            self.assertEqual(set(run.verify(result).failed()), {"A1", "A2"})

    def test_dangling_is_missing_and_wrong_owner_type_is_rejected(self):
        run = self.run_for(entity_mode="6000.4")
        card = run.verify(run.solve(run.solutions.dangling_references))
        self.assertEqual(set(card.failed()), {"A5"}, detail(card))
        for ident, field, obj in run.handles["edges"]:
            res = run.editor.client(SOLVER_AGENT).read("component_get_serialized_properties",
                componentType="UnityEngine.MeshRenderer", includeHidden=True, **V.address(ident))
            row = next(r for r in res["properties"] if r["propertyPath"] == field)
            self.assertEqual(row["referenceState"], "missing")
        run = self.run_for()
        result = run.solve(run.solutions.reference)
        ident, field, obj = run.handles["edges"][0]
        path = run.build_ctx.probe.go_path(run.build_ctx.probe.go(ident)["path"])["path"]
        run.editor.call("component_set_serialized_property", dict(V.address(ident),
            componentType="UnityEngine.MeshRenderer", propertyPath=field, referencePath=path,
            objectType="UnityEngine.GameObject"), SOLVER_AGENT)
        self.assertEqual(set(run.verify(result).failed()), {"A5"})

    def test_surplus_objects_in_any_run_branch_and_leftovers(self):
        for spot in ("root", "gate", "leftover"):
            with self.subTest(spot=spot):
                run = self.run_for()
                gates, links = run.private.layout(run.build_ctx)
                original = gates[-1]["id"]
                if spot == "leftover":
                    row = run.editor.call("gameobject_duplicate", V.address(original), SOLVER_AGENT)
                    extra = V.object_id({"entityId": row["copyEntityId"], "instanceId": row["copyInstanceId"]})
                result = run.solve(run.solutions.reference)
                if spot != "leftover":
                    parent = run.build_ctx.probe.go_path(gates[0]["path"] if spot == "gate" else run.scope.object_root)
                    run.editor.call("gameobject_create", dict(name="Extra", **V.address(parent, "parent")), SOLVER_AGENT)
                card = run.verify(result)
                self.assertEqual(set(card.failed()), {"A6", "A8"}, detail(card))

    def test_missing_object_asset_and_bad_report_do_not_raise(self):
        for case in ("object", "asset", "report"):
            run = self.run_for()
            gates, links = run.private.layout(run.build_ctx)
            result = run.solve(run.solutions.reference)
            if case == "object":
                ident = run.build_ctx.probe.go_path(gates[0]["path"])
                run.editor.call("gameobject_delete", V.address(ident), SOLVER_AGENT)
            elif case == "asset":
                path = run.handles["file"]
                del run.editor.prefabs[path]
                run.editor._skill_asset_delete(SOLVER_AGENT, {"assetPath": path})
            else:
                result = {"prefabPath": None, "inspectedOwners": [None], "declaredComplete": "yes"}
            card = run.verify(result)
            self.assertFalse(card.strict)
            if case == "report":
                self.assertEqual(set(card.failed()), {"A9"}, detail(card))

    def test_world_pose_tolerances_are_absolute_and_independent(self):
        for field, delta, fails in (("position", 0.0005, set()), ("position", 0.002, {"A3"}),
                                    ("rotation", 0.05, set()), ("rotation", 0.2, {"A3"}),
                                    ("scale", 0.0002, set()), ("scale", 0.01, {"A3"})):
            with self.subTest(field=field, delta=delta):
                run = self.run_for()
                gates, links = run.private.layout(run.build_ctx)
                result = run.solve(run.solutions.reference)
                obj = run.build_ctx.probe.go_path(gates[0]["path"])
                if field == "position":
                    pos = V.floats(obj["position"])
                    args = {"posX": pos[0] + delta}
                elif field == "rotation":
                    rot = V.floats(obj["rotation"])
                    args = {"rotY": rot[1] + delta}
                else:
                    scale = V.floats(obj["scale"])
                    args = {"scaleX": scale[0] + delta}
                run.editor.call("gameobject_set_transform", dict(V.address(obj), **args), SOLVER_AGENT)
                card = run.verify(result)
                self.assertEqual(set(card.failed()), fails, detail(card))

    def test_metadata_readback_and_other_prefab_overrides_are_protected(self):
        run = self.run_for()
        result = run.solve(run.solutions.reference)
        run.editor.labels[run.handles["bindings"][0][1]] = ["pending-native-save"]
        self.assertEqual(set(run.verify(result).failed()), {"A7"})
        run = self.run_for()
        sample = next(obj for obj in run.editor.objects.values() if hasattr(obj, "prefab"))
        bystander = V.object_id(run.editor.describe(sample))
        result = run.solve(run.solutions.reference)
        # The preserved sample has an existing component override.
        run.editor.call("component_set_serialized_property", dict(V.address(bystander),
            componentType="UnityEngine.BoxCollider", propertyPath="m_Center", value="0.125,0,0"), SOLVER_AGENT)
        self.assertEqual(set(run.verify(result).failed()), {"A6"})

    def test_source_audit_covers_every_authored_skill(self):
        run = self.run_for()
        result = run.solve(run.solutions.reference)
        run.verify(result)
        for skill, args, agent in run.editor.calls:
            if agent == SOLVER_AGENT:
                self.assertIn(skill, SURFACE)
                self.assertFalse(set(args) - run.editor.parameters[skill])

# Producer:Betsy
