"""Offline OC8 checks on a local extension of the shared layer/physics model.

No Unity, HTTP, sockets, C# execution, real serialization, Undo, importers or native journal callbacks are exercised.
Temporary fake projects stay under eval/runs and are removed by unittest cleanups.
"""
import ast
import json
import os
import re
import sys
import tempfile
import unittest
from unittest import mock

from .fake_ops_t3 import FakeEditorT3
from .pack_fake_editor import REPO, _fail
from .pack_offline import OpsTaskTests, detail, load_modules
from usk_eval import fixture, pack, verifylib as V
from usk_eval.rest import RUNNER_AGENT, SOLVER_AGENT
from usk_eval.task_api import BuildContext

TAG = "ProjectSettings/TagManager.asset"
DYNAMICS = "ProjectSettings/DynamicsManager.asset"
MANIFEST = os.path.join(REPO, "SkillsForUnity", "Evaluation~", "pack", "manifest.json")
with open(MANIFEST, encoding="utf-8") as _stream:
    PUBLIC_SEED = json.load(_stream)["tasks"]["OC8"]["seeds"][0]
SECOND_SEED = 88117


class OC8Editor(FakeEditorT3):
    """Layer indices survive renames; live physics and settings files are separate observations."""

    extra_reads = FakeEditorT3.extra_reads | frozenset(("physics_get_gravity", "project_get_layers", "asset_get_labels"))

    def __init__(self, *args, **kwargs):
        super(OC8Editor, self).__init__(*args, **kwargs)
        self.layer_names.update({10: "Props", 18: "World"})
        self.tags = ["WorldTag"]
        self.gravity = {"x": 0.0, "y": -9.81, "z": 0.0}
        self.labels = {}
        self.stuck_names = False
        self.stuck_matrix = False
        self.short_properties = False
        self.unreadable_pair = False
        self.collision[(10, 18)] = False
        self.collision[(5, 5)] = False
        os.makedirs(self.disk("ProjectSettings"), exist_ok=True)
        self._save_settings()
        with open(self.disk("ProjectSettings/ProjectSettings.asset"), "w", encoding="utf-8") as stream:
            stream.write("%YAML 1.1\n--- !u!129 &1\nPlayerSettings:\n  productName: OfflineProject\n")

    def _create(self, agent, item, parent):
        obj = super(OC8Editor, self)._create(agent, item, parent)
        obj.layer_slot = 0
        return obj

    def _save_settings(self):
        layers = "".join("  - " + json.dumps(self.layer_names.get(i, "")) + "\n" for i in range(32))
        tags = "".join("  - " + json.dumps(tag) + "\n" for tag in self.tags)
        if not tags:
            tags = ""
        text = ("%YAML 1.1\n--- !u!78 &1\nTagManager:\n  serializedVersion: 3\n  tags:" +
                ("\n" + tags if self.tags else " []\n") + "  layers:\n" + layers +
                "  m_SortingLayers:\n  - name: Default\n    uniqueID: 0\n    locked: 0\n")
        with open(self.disk(TAG), "w", encoding="utf-8") as stream:
            stream.write(text)
        masks = []
        for a in range(32):
            bits = sum(1 << b for b in range(32) if self.collision.get(tuple(sorted((a, b))), True))
            masks.append("%08x" % bits)
        g = self.gravity
        text = ("%YAML 1.1\n--- !u!55 &1\nPhysicsManager:\n" +
                "  m_Gravity: {x: %.9g, y: %.9g, z: %.9g}\n" % (g["x"], g["y"], g["z"]) +
                "  m_BounceThreshold: 2\n  m_LayerCollisionMatrix: " + "".join(masks) + "\n")
        with open(self.disk(DYNAMICS), "w", encoding="utf-8") as stream:
            stream.write(text)

    def _tag_values(self):
        values = {"m_ObjectHideFlags": "0", "serializedVersion": "3", "tags": "Array",
                  "tags.Array.size": str(len(self.tags)), "layers": "Array", "layers.Array.size": "32",
                  "m_SortingLayers.Array.size": "1", "m_SortingLayers.Array.data[0].name": "Default"}
        values.update({"tags.Array.data[%d]" % i: name for i, name in enumerate(self.tags)})
        values.update({"layers.Array.data[%d]" % i: self.layer_names.get(i, "") for i in range(32)})
        return values

    def _skill_scriptableobject_get_serialized_properties(self, agent, args):
        if args["assetPath"] == TAG:
            values = self._tag_values()
        elif args["assetPath"] == DYNAMICS:
            g = self.gravity
            values = {"m_ObjectHideFlags": "0", "m_Gravity": "(%.9g,%.9g,%.9g)" % (g["x"], g["y"], g["z"]),
                      "m_BounceThreshold": "2", "m_LayerCollisionMatrix": json.dumps(sorted(self.collision.items()))}
        else:
            return super(OC8Editor, self)._skill_scriptableobject_get_serialized_properties(agent, args)
        rows = [{"propertyPath": key, "value": value} for key, value in values.items()]
        if self.short_properties:
            rows = rows[:2]
        return {"success": True, "path": args["assetPath"], "properties": rows[:args.get("limit", 200)]}

    def _skill_component_get_serialized_properties(self, agent, args):
        args = dict(args, componentType=args["componentType"].split(".")[-1])
        result = super(OC8Editor, self)._skill_component_get_serialized_properties(agent, args)
        if self.short_properties:
            result["properties"] = result["properties"][:1]
        return result

    def _skill_scriptableobject_set_serialized_property(self, agent, args):
        if args["assetPath"] != TAG:
            return super(OC8Editor, self)._skill_scriptableobject_set_serialized_property(agent, args)
        prop, value = args["propertyPath"], args.get("value") or ""
        match = re.fullmatch(r"layers\.Array\.data\[(\d+)\]", prop)
        if match:
            i = int(match.group(1))
            if not 0 <= i < 32:
                raise _fail("SEMANTIC_INVALID", "invalid layer slot")
            if not self.stuck_names:
                if value:
                    self.layer_names[i] = value
                else:
                    self.layer_names.pop(i, None)
                for obj in self.objects.values():
                    obj.layer = self.layer_names.get(obj.layer_slot, "")
        elif prop == "tags.Array.size":
            size = int(value)
            self.tags = (self.tags + [""] * size)[:size]
        else:
            match = re.fullmatch(r"tags\.Array\.data\[(\d+)\]", prop)
            if not match or not 0 <= int(match.group(1)) < len(self.tags):
                raise _fail("PROPERTY_NOT_FOUND", prop)
            self.tags[int(match.group(1))] = value
        self._save_settings()
        self._change(agent, {"kind": "property_changed", "targetType": "TagManager", "assetPath": TAG,
                             "property": prop, "value": value})
        return {"success": True, "assetPath": TAG, "propertyPath": prop, "valueSet": value}

    def _skill_gameobject_set_layer_batch(self, agent, args):
        result = super(OC8Editor, self)._skill_gameobject_set_layer_batch(agent, args)
        for item in json.loads(args["items"]):
            obj = self.find(item)
            slot = next(i for i, name in self.layer_names.items() if name == item["layer"])
            for child in (self.walk([obj]) if item.get("recursive") else [obj]):
                child.layer_slot = slot
        return result

    def _skill_physics_get_layer_collision(self, agent, args):
        if self.unreadable_pair:
            return {"collisionEnabled": None}
        return super(OC8Editor, self)._skill_physics_get_layer_collision(agent, args)

    def _skill_physics_set_layer_collision(self, agent, args):
        if self.stuck_matrix:
            return {"success": True}
        return super(OC8Editor, self)._skill_physics_set_layer_collision(agent, args)

    def _skill_physics_get_gravity(self, agent, args):
        return dict(self.gravity)

    def _skill_physics_set_gravity(self, agent, args):
        self.gravity = {key: float(args[key]) for key in ("x", "y", "z")}
        self._change(agent, {"kind": "property_changed", "targetType": "PhysicsManager", "assetPath": DYNAMICS,
                             "property": "m_Gravity", "value": str(self.gravity)})
        return {"success": True, "gravity": self.gravity}

    def _skill_project_get_layers(self, agent, args):
        names = [name for _i, name in sorted(self.layer_names.items()) if name]
        return {"success": True, "count": len(names), "layers": names}

    def _skill_project_add_tag(self, agent, args):
        self.tags.append(args["tagName"])
        self._save_settings()
        self._change(agent, {"kind": "property_changed", "targetType": "TagManager", "assetPath": TAG,
                             "property": "tags.Array.size", "value": str(len(self.tags))})
        return {"success": True, "tag": args["tagName"]}

    def _skill_asset_get_labels(self, agent, args):
        if not os.path.exists(self.disk(args["assetPath"])):
            raise _fail("ASSET_NOT_FOUND", args["assetPath"])
        return {"success": True, "assetPath": args["assetPath"], "labels": self.labels.get(args["assetPath"], [])}

    def _skill_asset_set_labels(self, agent, args):
        labels = [label.strip() for label in args["labels"].split(",") if label.strip()]
        path = args["assetPath"]
        self.labels[path] = labels
        with open(self.disk(path) + ".meta", "a", encoding="utf-8") as stream:
            stream.write("labels: " + json.dumps(labels) + "\n")
        self._imported(agent, [path])
        return {"success": True, "assetPath": path, "labels": labels}


class OC8Tests(OpsTaskTests, unittest.TestCase):
    TASK_ID = "OC8"
    EDITOR = OC8Editor
    TIER = "challenge"
    SEED = PUBLIC_SEED
    FAILS_UNTOUCHED = {"A1", "A3", "A4", "A8"}

    def setUp(self):
        runs = os.path.join(REPO, "eval", "runs")
        os.makedirs(runs, exist_ok=True)
        temporary = tempfile.TemporaryDirectory(prefix="oc8-offline-", dir=runs)
        self.addCleanup(temporary.cleanup)
        patch = mock.patch("tempfile.tempdir", temporary.name)
        patch.start()
        self.addCleanup(patch.stop)
        patch = mock.patch("socket.socket", side_effect=AssertionError("OC8 offline tests must not open sockets"))
        patch.start()
        self.addCleanup(patch.stop)
        task = load_modules("OC8")[0]
        module = sys.modules[type(task).__module__]
        patch = mock.patch.object(module, "PAUSE", 0.0)
        patch.start()
        self.addCleanup(patch.stop)

    def test_task_attributes(self):
        task = load_modules("OC8")[0]
        self.assertEqual((task.id, task.track, task.tier, task.case_version, task.minutes, task.needs_focus),
                         ("OC8", "ops", "challenge", 1, 25, False))
        self.assertEqual(pack.normalize_requires(task.requires),
                         {"pipeline": "URP", "mode": "bypass", "dedicated": True, "packages": []})
        self.assertEqual(set(task.layer_pairs), {(a, b) for a in range(32) for b in range(a, 32)})

    def test_source_encoding_tail_and_python39_syntax(self):
        paths = ["SkillsForUnity/Evaluation~/pack/tasks/OC8/task.py", "eval/pack/private/OC8.py",
                 "eval/pack/solutions/OC8.py", "eval/tests/runner/test_pack_oc8.py"]
        for path in paths:
            with self.subTest(path=path):
                with open(os.path.join(REPO, *path.split("/")), "rb") as stream:
                    raw = stream.read()
                self.assertTrue(raw.startswith(b"\xef\xbb\xbf"))
                self.assertTrue(raw.endswith(b"\n\n# Producer:Betsy\n"))
                self.assertFalse(raw.endswith(b"\n\n\n# Producer:Betsy\n"))
                self.assertNotIn(b"\r", raw)
                ast.parse(raw.decode("utf-8-sig"), path, feature_version=(3, 9))

    def check_solutions(self, pipeline, entity_mode):
        solutions = load_modules("OC8")[2]
        for seed in (self.SEED, SECOND_SEED):
            for repeat in (1, 2):
                run = self.run_for(seed=seed, pipeline=pipeline, entity_mode=entity_mode)
                card = run.verify(run.solve(solutions.reference))
                self.assertTrue(card.strict, "seed %s repeat %s: %s" % (seed, repeat, detail(card)))
            for negative in solutions.NEGATIVES:
                with self.subTest(seed=seed, ids=entity_mode, negative=negative.name):
                    run = self.run_for(seed=seed, pipeline=pipeline, entity_mode=entity_mode)
                    card = run.verify(run.solve(negative.apply))
                    self.assertEqual(set(card.failed()), negative.expect_failed, detail(card))
            for name, fn in solutions.ALTERNATIVES:
                with self.subTest(seed=seed, ids=entity_mode, alternative=name):
                    run = self.run_for(seed=seed, pipeline=pipeline, entity_mode=entity_mode)
                    card = run.verify(run.solve(fn))
                    self.assertTrue(card.strict, detail(card))

    def test_names_structure_and_settings_vary_with_seed(self):
        first, second, repeat = self.run_for(), self.run_for(seed=SECOND_SEED), self.run_for()
        self.assertEqual(first.fingerprint, repeat.fingerprint)
        self.assertNotEqual(first.fingerprint, second.fingerprint)
        self.assertNotEqual(first.handles["names"], second.handles["names"])
        def profile(run):
            return sorted((len(row["children"]), row["path"].count("/"))
                          for row in V.structure(run.build_ctx.probe, run.scope.root_id))
        self.assertNotEqual(profile(first), profile(second))
        self.assertNotEqual(first.snap["data"]["layers"], second.snap["data"]["layers"])
        for key in ("matrix", "settings", "membership"):
            self.assertEqual(first.snap["digests"][key], repeat.snap["digests"][key])
        def normalized(run):
            value = V.hierarchy_signature(run.build_ctx.probe, run.scope.root_id, limit=32)
            return json.dumps(value, sort_keys=True).replace(run.scope.run_id, "<RUN>")
        self.assertEqual(normalized(first), normalized(repeat))
        for run in (first, second):
            rows = V.structure(run.build_ctx.probe, run.scope.root_id)
            paths = [[row["id"], row["path"]] for row in rows]
            picks = run.private.selection(paths, run.handles["site"], run.handles["names"])
            self.assertTrue(any(not row["active"] and picks[str(row["id"])] for row in rows))
            for name in run.handles["names"][:2]:
                self.assertTrue(any(picks[str(row["id"])] == name for row in rows))
            kept = [row for row in rows if "KeepLayer" in row["path"].split("/")]
            self.assertGreaterEqual(len(set(row["layer"] for row in kept)), 3)
            self.assertTrue(all(not picks[str(row["id"])] for row in kept))
            self.assertTrue(any(row["name"] == "KeepLayerCopy" and picks[str(row["id"])] for row in rows))

    def test_untouched_fixture_both_seeds_and_ids(self):
        for seed in (self.SEED, SECOND_SEED):
            for mode in ("legacy", "6000.4"):
                with self.subTest(seed=seed, ids=mode):
                    run = self.run_for(seed=seed, entity_mode=mode)
                    self.assertEqual(set(run.verify(None).failed()), self.FAILS_UNTOUCHED)
                    truthful = {"layers": [], "declaredComplete": False}
                    self.assertEqual(set(run.verify(truthful).failed()), {"A1", "A3", "A4"})

    def test_teardown_restores_all_settings_for_different_allocations_and_errors(self):
        sol = load_modules("OC8")[2]
        for seed in (self.SEED, SECOND_SEED):
            for mode in ("legacy", "6000.4"):
                for fn in (sol.recursive_restore_other_slots, sol.occupied_slot, sol.name_typo, sol.fourth_pair,
                           sol.compact_indices, sol.tag_added, sol.gravity_changed, sol.extra_layer):
                    with self.subTest(seed=seed, ids=mode, route=fn.__name__):
                        run = self.run_for(seed=seed, entity_mode=mode)
                        with open(run.editor.disk(run.handles["checkpoint"]), encoding="utf-8") as stream:
                            before = json.load(stream)
                        run.solve(fn)
                        self.assertTrue(run.task.teardown(run.build_ctx)["restored"])
                        self.assertEqual(run.task.layer_names(run.build_ctx), before["layers"])
                        self.assertEqual(run.task.matrix(run.build_ctx), run.collision)
                        self.assertEqual(V.canonical_digest(run.task.settings_view(run.build_ctx)), before["view"])
                        self.assertEqual(V.canonical_digest(run.task.files(run.build_ctx, "ProjectSettings",
                                                                          settings=True, whole=True)), before["disk"])
                        self.assertTrue(run.task.teardown(run.build_ctx)["restored"])
                        fixture.restore_collision(run.editor.client(RUNNER_AGENT), run.collision)

    def test_checkpoint_recovers_without_finished_build_handles(self):
        run = self.run_for()
        ctx = BuildContext(run.editor.client(RUNNER_AGENT), run.scope, run.seed, run.project, run.env,
                           "en", "ops", "challenge", run.task, handles={})
        self.assertTrue(run.task.teardown(ctx)["restored"])
        self.assertEqual(run.task.matrix(ctx), run.collision)
        self.assertFalse(run.task.teardown(BuildContext(run.editor.client(RUNNER_AGENT),
            fixture.Scope(fixture.new_run_id()), run.seed, run.project, run.env, "en", "ops", "challenge", run.task,
            handles={}))["restored"])

    def test_teardown_rejects_silent_restore_failures(self):
        for flag in ("stuck_names", "stuck_matrix"):
            with self.subTest(failure=flag):
                run = self.run_for()
                run.solve(run.solutions.recursive_restore_other_slots)
                setattr(run.editor, flag, True)
                with self.assertRaisesRegex(RuntimeError, "still differ"):
                    run.task.teardown(run.build_ctx)

    def test_settings_readback_does_not_depend_on_collision_file_persistence(self):
        run = self.run_for()
        run.solve(run.solutions.reference)
        with open(run.editor.disk(DYNAMICS), "rb") as stream:
            before = stream.read()
        c = run.editor.client(SOLVER_AGENT)
        c.write("physics_set_layer_collision", layer1=0, layer2=0, enableCollision=False)
        with open(run.editor.disk(DYNAMICS), "rb") as stream:
            self.assertEqual(stream.read(), before)
        answer = {"layers": run.private.changed_names(run.snap["data"]["layers"], run.task.layer_names(run.build_ctx)),
                  "declaredComplete": True}
        self.assertEqual(set(run.verify(answer).failed()), {"A6"})

    def test_transient_object_creation_is_also_prohibited(self):
        run = self.run_for()
        answer = run.solve(run.solutions.reference)
        c = run.editor.client(SOLVER_AGENT)
        made = c.write("gameobject_create", name="Temporary", **V.address(run.scope.root_id, "parent"))
        c.write("gameobject_delete", **V.address(V.object_id(made)))
        self.assertEqual(set(run.verify(answer).failed()), {"A7"})

    def test_missing_objects_reads_and_malformed_reports_fail_without_raising(self):
        run = self.run_for()
        answer = run.solve(run.solutions.reference)
        for value in (None, {}, {"layers": None}, {"layers": [{"index": True, "name": "x"}],
                                                     "declaredComplete": "true"}):
            self.assertEqual(set(run.verify(value).failed()), {"A8"})
        rows = V.structure(run.build_ctx.probe, run.scope.root_id)
        picks = run.private.selection([[r["id"], r["path"]] for r in rows], run.handles["site"], run.handles["names"])
        leaf = next(row for row in rows if picks[str(row["id"])] and not row["children"])
        run.editor.client(SOLVER_AGENT).write("gameobject_delete", **V.address(leaf["id"]))
        self.assertEqual(set(run.verify(answer).failed()), {"A4", "A7"})
        run = self.run_for()
        answer = run.solve(run.solutions.reference)
        run.editor.short_properties = True
        self.assertEqual(set(run.verify(answer).failed()), {"A6", "A7"})
        run.editor.short_properties = False
        run.editor.unreadable_pair = True
        self.assertEqual(set(run.verify(answer).failed()), {"A3", "A6"})

    def test_allowed_settings_records_do_not_excuse_other_files_or_scenes(self):
        allowed = load_modules("OC8")[1].allowed_change
        for path in (TAG, DYNAMICS, TAG + ".meta"):
            self.assertTrue(allowed(None, {"kind": "imported", "assetPath": path}))
        for rec in ({"kind": "imported", "assetPath": "ProjectSettings/ProjectSettings.asset"},
                    {"kind": "scene_saved", "assetPath": TAG},
                    {"kind": "property_changed", "assetPath": TAG, "path": "Outside"},
                    {"kind": "moved", "assetPath": TAG, "from": "Assets/Other.asset"}):
            self.assertFalse(allowed(None, rec))

# Producer:Betsy
