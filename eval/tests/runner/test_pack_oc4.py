"""OC4 offline model: all solution variants, ids, seeds and malformed end states without HTTP or Unity.

The subclass adds serialized material arrays, missing-reference tokens, material identities and separate query/sample
limits. Native Unity deletion/serialization and shader availability still require the real-editor gate.
"""
import copy
import json
import os
import re
import tempfile
import unittest
from unittest import mock

from .pack_fake_editor import FakeEditor, REPO, SHADERS, _fail, remove_tree
from usk_eval import attempt, environment, fixture, journal, pack, prompts, verifylib as V
from usk_eval.rest import RUNNER_AGENT, SOLVER_AGENT
from usk_eval.task_api import BuildContext, SolverContext, VerifyContext

PACK_ROOT = os.path.join(REPO, "SkillsForUnity", "Evaluation~", "pack")
SOURCE_ROOT = os.path.join(REPO, "SkillsForUnity", "Editor", "Skills")
SEEDS = (7607, 88117)
MODULES = None
SURFACE = {
    "asset_create_folder": "AssetSkills.cs", "asset_delete": "AssetSkills.cs", "asset_get_info": "AssetSkills.cs",
    "material_create_batch": "MaterialSkills.cs", "material_get_properties": "MaterialSkills.cs",
    "material_assign": "MaterialSkills.cs", "material_set_shader": "MaterialSkills.cs",
    "component_add_batch": "ComponentSkills.cs", "component_get_serialized_properties": "ComponentSkills.cs",
    "component_set_serialized_property": "ComponentSkills.cs",
    "component_set_serialized_property_batch": "ComponentSkills.cs",
    "gameobject_create": "GameObjectSkills.cs", "gameobject_create_batch": "GameObjectSkills.cs",
    "gameobject_get_info": "GameObjectSkills.cs", "gameobject_find": "GameObjectSkills.cs",
    "gameobject_set_active": "GameObjectSkills.cs", "gameobject_delete": "GameObjectSkills.cs",
    "batch_query_gameobjects": "BatchSkills.cs", "scene_save": "SceneSkills.cs",
}


def modules():
    global MODULES
    if MODULES is None:
        task = pack.load_task_file("OC4", os.path.join(PACK_ROOT, "tasks", "OC4", "task.py"))
        private = pack.load_source_module("usk_test_private_OC4", os.path.join(REPO, "eval", "pack", "private", "OC4.py"))
        solutions = pack.load_source_module("usk_test_solutions_OC4", os.path.join(REPO, "eval", "pack", "solutions", "OC4.py"))
        MODULES = task, private, solutions
    return MODULES


def declarations():
    out = {}
    for skill, filename in SURFACE.items():
        with open(os.path.join(SOURCE_ROOT, filename), encoding="utf-8-sig") as handle:
            source = handle.read()
        offset = source.index('[UnitySkill("' + skill + '"')
        found = re.search(r"public\s+static\s+object\s+\w+\s*\((.*?)\)\s*\{", source[offset:], re.S)
        if not found:
            raise AssertionError("C# declaration not found: " + skill)
        params = re.sub(r"\[SkillParam\(.*?\)\]", "", found.group(1), flags=re.S)
        names = set(re.findall(r"\b(?:string|int|bool|float)\s+(\w+)", params))
        if {"instanceId", "path"}.issubset(names):
            names.add("entityId")  # SkillRouter.Schema.cs exposes this synthetic locator.
        out[skill] = names
    return out


def keys(value):
    out = []
    if isinstance(value, dict):
        for key, item in value.items():
            out.append(key)
            out.extend(keys(item))
    elif isinstance(value, (list, tuple)):
        for item in value:
            out.extend(keys(item))
    return out


class AuditEditor(FakeEditor):
    def __init__(self, project, entity_mode="legacy"):
        self.material_ids, self.calls, self.paths, self.ids = {}, [], {}, {}
        self.parameters = declarations()
        self.clip_properties = False
        FakeEditor.__init__(self, project, "URP", entity_mode)

    def _create(self, agent, item, parent):
        obj = FakeEditor._create(self, agent, item, parent)
        self.paths[self.path(obj)] = obj
        self.ids[str(V.object_id({"entityId": obj.eid, "instanceId": obj.iid}))] = obj
        return obj

    def find(self, args, prefix="", required=True):
        def arg(name):
            return args.get(prefix + name[0].upper() + name[1:] if prefix else name)
        ident = V.object_id({"entityId": arg("entityId"), "instanceId": arg("instanceId")})
        obj = self.ids.get(str(ident)) if ident is not None else self.paths.get(arg("path"))
        if obj is not None and obj.eid in self.objects:
            return obj
        return FakeEditor.find(self, args, prefix, required)

    def call(self, skill, args, agent):
        if skill in self.parameters:
            unknown = set(args) - self.parameters[skill]
            if unknown:
                raise AssertionError("undeclared parameters for %s: %s" % (skill, sorted(unknown)))
        self.calls.append((skill, copy.deepcopy(args), agent))
        return FakeEditor.call(self, skill, args, agent)

    def _new_material(self, agent, path, shader, colors=None, floats=None):
        FakeEditor._new_material(self, agent, path, shader, colors, floats)
        eid, iid = self._new_ids()
        self.material_ids[path] = str(V.object_id({"entityId": eid, "instanceId": iid}))
        self._write_file(path, "%%YAML 1.1\n--- !u!21 &2100000\nMaterial:\n  m_Name: %s\n  m_Shader: %s\n" %
                         (os.path.splitext(os.path.basename(path))[0], shader))

    def _skill_material_create_batch(self, agent, args):
        results = [self._skill_material_create(agent, item) for item in json.loads(args["items"])]
        return {"success": True, "totalItems": len(results), "successCount": len(results), "failCount": 0,
                "results": results}

    def _skill_material_set_shader(self, agent, args):
        path, material = self._target_material(args)
        shader = args["shaderName"]
        if shader not in SHADERS:
            raise _fail("SHADER_NOT_FOUND", "unknown shader")
        material["shader"] = shader
        material["colors"] = dict(SHADERS[shader]["colors"])
        material["floats"] = dict(SHADERS[shader]["floats"])
        self._change(agent, {"kind": "property_changed", "targetType": "Material", "property": "m_Shader",
                             "previousValue": "", "value": shader, "assetPath": path})
        return {"success": True, "shader": shader, "materialPath": path}

    def _skill_component_add_batch(self, agent, args):
        results = []
        for item in json.loads(args["items"]):
            if set(item) - {"name", "instanceId", "path", "componentType"}:
                raise AssertionError("component batch item uses an unsupported key")
            results.append(self._skill_component_add(agent, item))
        return {"success": True, "totalItems": len(results), "successCount": len(results), "failCount": 0,
                "results": results}

    def _skill_component_get_serialized_properties(self, agent, args):
        obj, kind = self.find(args), args["componentType"]
        if kind == "Transform":
            values = self._serialized(obj, kind)
            rows = [{"propertyPath": key, "value": value,
                     "propertyType": "Quaternion" if "Rotation" in key else "Vector3"}
                    for key, value in values.items()]
        elif kind == "MeshRenderer" and self.component(obj, kind):
            rows = [{"propertyPath": "m_Enabled", "value": str(self.component(obj, kind)[1]), "propertyType": "Boolean"},
                    {"propertyPath": "m_CastShadows", "value": "1", "propertyType": "Integer"},
                    {"propertyPath": "m_Materials", "value": "Array size: %d" % len(obj.materials),
                     "propertyType": "Generic", "isArray": True},
                    {"propertyPath": "m_Materials.Array.size", "value": str(len(obj.materials)), "propertyType": "ArraySize"}]
            for index, path in enumerate(obj.materials):
                state = "none" if path is None else "assigned" if path in self.materials else "missing"
                value = (self.material_ids[path] + ":UnityEngine.Material:" + os.path.splitext(os.path.basename(path))[0]
                         if state == "assigned" else "null")
                rows.append({"propertyPath": "m_Materials.Array.data[%d]" % index, "propertyType": "ObjectReference",
                             "value": value, "referenceState": state})
        else:
            raise _fail("COMPONENT_NOT_FOUND", "component not found")
        limit = 2 if self.clip_properties else args.get("limit", 200)
        return {"success": True, "component": kind, "properties": rows[:max(1, limit)]}

    def _skill_component_set_serialized_property(self, agent, args):
        if args["componentType"] != "MeshRenderer":
            return FakeEditor._skill_component_set_serialized_property(self, agent, args)
        obj, prop = self.find(args), args["propertyPath"]
        if not self.component(obj, "MeshRenderer"):
            raise _fail("COMPONENT_NOT_FOUND", "component not found")
        if prop == "m_Materials.Array.size":
            size = int(args["value"])
            obj.materials = obj.materials[:size] + [obj.materials[-1] if obj.materials else None] * max(0, size - len(obj.materials))
        else:
            match = re.fullmatch(r"m_Materials\.Array\.data\[([0-9]+)\]", prop)
            if not match or int(match.group(1)) >= len(obj.materials):
                raise _fail("PROPERTY_NOT_FOUND", "material slot not found")
            path = args.get("assetPath")
            if path:
                self._material(path)
            elif args.get("value") not in (None, "null"):
                raise _fail("INVALID_VALUE", "object reference needs assetPath or null")
            obj.materials[int(match.group(1))] = path
        self._change(agent, self._identity(obj, kind="property_changed", targetType="MeshRenderer", property=prop,
                                           previousValue="", value=args.get("assetPath") or args.get("value")))
        return {"success": True, "propertyPath": prop, "valueSet": args.get("assetPath") or args.get("value")}

    def _skill_component_set_serialized_property_batch(self, agent, args):
        results = []
        allowed = {"name", "instanceId", "path", "componentType", "propertyPath", "value", "referenceName",
                   "referenceInstanceId", "referencePath", "assetPath", "objectType"}
        for item in json.loads(args["items"]):
            if set(item) - allowed:
                raise AssertionError("serialized batch item uses an unsupported key")
            results.append(self._skill_component_set_serialized_property(agent, item))
        return {"success": True, "totalItems": len(results), "successCount": len(results), "failCount": 0,
                "results": results}

    def _skill_batch_query_gameobjects(self, agent, args):
        result = FakeEditor._skill_batch_query_gameobjects(self, agent, args)
        result["objects"] = result["objects"][:max(1, int(args.get("sampleLimit", 20)))]
        for row in result["objects"]:
            obj = self.find(row)
            row["activeSelf"], row["activeInHierarchy"] = obj.active, self.active_in_hierarchy(obj)
        return result


class Offline(object):
    def __init__(self, test, seed=SEEDS[0], ids="legacy"):
        self.tmp = tempfile.mkdtemp(prefix="usk-oc4-")
        test.addCleanup(remove_tree, self.tmp)
        self.project = os.path.join(self.tmp, "Project")
        self.editor = AuditEditor(self.project, ids)
        self.task, self.private, self.solutions = modules()
        self.scope, self.seed = fixture.Scope(fixture.new_run_id()), seed
        client = self.editor.client(RUNNER_AGENT)
        fixture.enter(client, self.scope, lambda: None)
        self.env = environment.snapshot(client, 4242)
        ctx = BuildContext(client, self.scope, seed, self.project, self.env, "en", "ops", "challenge", self.task)
        self.handles = self.task.build(ctx)
        attempt._check_handles(self.handles)
        ctx.handles = self.handles
        fixture.save_run_scene(client, self.scope)
        self.snap = self.task.snapshot(ctx)
        self.build_ctx = ctx
        self.fingerprint = V.fingerprint(ctx)
        self.cursor0 = self.editor.seq
        self.layout = [(self.editor.path(obj).replace(self.scope.run_id, "<RUN>"),
                        obj.active, [None if path is None else path.replace(self.scope.run_id, "<RUN>") for path in obj.materials])
                       for obj in self.editor.walk()]
        self.shape = sorted((self.editor.path(obj).count("/"), len(obj.children), obj.active, len(obj.materials))
                            for obj in self.editor.walk())

    def solve(self, fn):
        ctx = SolverContext(self.editor.client(SOLVER_AGENT), self.scope, self.seed, self.project, self.env,
                            "en", "ops", "challenge", self.task, handles=self.handles, private=self.private)
        return fn(ctx)

    def verify(self, answer):
        start = self.editor.seq
        self.editor.refresh()
        window = {"from": start, "to": self.editor.seq}
        entries = [row for row in self.editor.entries if row["seq"] > self.cursor0]
        ctx = VerifyContext(self.editor.client(RUNNER_AGENT), self.scope, self.seed, self.project, self.env,
                            "en", "ops", "challenge", self.task, handles=self.handles, c0=self.snap["data"],
                            c0_digests=self.snap["digests"], answer=answer, records=journal.flatten(entries, window),
                            journal_complete=True,
                            journal={"cursor0": self.cursor0, "cursorEnd": self.editor.seq, "refreshWindow": window})
        card = V.Scorecard(self.private.ASSERTIONS)
        self.private.verify(ctx, card)
        return card.finish()


def detail(card):
    return json.dumps([row for row in card.as_dict()["assertions"] if not row["pass"]], default=str)[:4000]


class OC4Tests(unittest.TestCase):
    def setUp(self):
        blocker = mock.patch("socket.socket", side_effect=AssertionError("OC4 tests must not use sockets"))
        blocker.start()
        self.addCleanup(blocker.stop)

    def test_reference_negatives_alternatives_both_seeds_and_ids(self):
        _task, _private, solutions = modules()
        for seed in SEEDS:
            for ids in ("legacy", "6000.4"):
                with self.subTest(seed=seed, ids=ids, variant="reference"):
                    run = Offline(self, seed, ids)
                    card = run.verify(run.solve(solutions.reference))
                    self.assertTrue(card.strict, detail(card))
                for negative in solutions.NEGATIVES:
                    with self.subTest(seed=seed, ids=ids, variant=negative.name):
                        run = Offline(self, seed, ids)
                        card = run.verify(run.solve(negative.apply))
                        self.assertEqual(set(card.failed()), negative.expect_failed, detail(card))
                        self.assertFalse(card.strict)
                for name, fn in solutions.ALTERNATIVES:
                    with self.subTest(seed=seed, ids=ids, variant=name):
                        run = Offline(self, seed, ids)
                        card = run.verify(run.solve(fn))
                        self.assertTrue(card.strict, detail(card))

    def test_fingerprint_and_seeded_structure(self):
        for ids in ("legacy", "6000.4"):
            first, second, third = Offline(self, ids=ids), Offline(self, ids=ids), Offline(self, SEEDS[1], ids)
            self.assertNotEqual(first.scope.run_id, second.scope.run_id)
            self.assertEqual(first.fingerprint, second.fingerprint)
            self.assertEqual(first.layout, second.layout)
            self.assertNotEqual(first.fingerprint, third.fingerprint)
            self.assertNotEqual(first.shape, third.shape)
            self.assertTrue(first.verify(first.solve(first.solutions.reference)).strict)
            self.assertTrue(second.verify(second.solve(second.solutions.reference)).strict)

    def test_untouched_fails_only_work_assertions(self):
        for seed in SEEDS:
            for ids in ("legacy", "6000.4"):
                run = Offline(self, seed, ids)
                card = run.verify(None)
                self.assertEqual(set(card.failed()), {"A1", "A2", "A7"}, detail(card))
                counts, _entries = run.solve(lambda ctx: run.solutions.audit(ctx, run.solutions.inventory(ctx)))
                self.assertEqual(set(run.verify(counts).failed()), {"A1", "A2"})

    def test_counts_are_secondary_and_malformed_answers_do_not_raise(self):
        run = Offline(self)
        valid = run.solve(run.solutions.reference)
        for answer in (None, [], "done", {}, dict(valid, slots=True), dict(valid, missingSlots=-1)):
            with self.subTest(answer=answer):
                self.assertEqual(set(run.verify(answer).failed()), {"A7"})

    def test_deleted_renderer_and_clipped_properties_fail_without_raising(self):
        run = Offline(self)
        counts = run.solve(run.solutions.reference)
        run.editor.client(SOLVER_AGENT).write("gameobject_delete", **V.address(run.handles["renderers"][0]))
        self.assertEqual(set(run.verify(counts).failed()), {"A1", "A2", "A3", "A4", "A5"})
        clipped = Offline(self)
        counts = clipped.solve(clipped.solutions.reference)
        clipped.editor.clip_properties = True
        self.assertEqual(set(clipped.verify(counts).failed()), {"A1", "A2", "A3", "A4", "A5"})

    def test_snapshot_hygiene_and_fixture_classes(self):
        run = Offline(self)
        self.assertEqual([key for key in keys(run.handles) + keys(run.snap) if attempt.HANDLE_KEY_RE.search(key)], [])
        json.dumps(run.handles, allow_nan=False)
        json.dumps(run.snap, allow_nan=False)
        for digest in run.snap["digests"].values():
            self.assertRegex(digest, r"^[a-f0-9]{64}$")
        self.assertEqual(len(run.handles["renderers"]), 240)
        for ref in run.handles["renderers"]:
            original = run.private.original_slots(type("Basis", (), {"c0": run.snap["data"],
                                                  "c0_digests": run.snap["digests"]})(), ref)
            self.assertEqual({kind for kind, _value in original}, {"standardSlots", "missingSlots", "emptySlots", "otherSlots"})
        self.assertTrue(any(not run.editor.active_in_hierarchy(run.editor.find(V.address(ref)))
                            for ref in run.handles["renderers"]))
        self.assertTrue(any(run.editor.active_in_hierarchy(run.editor.find(V.address(ref)))
                            for ref in run.handles["renderers"]))
        calls = [call for call in run.editor.calls if call[0] == "gameobject_create_batch"]
        self.assertGreaterEqual(sum(len(json.loads(args["items"])) for _skill, args, _agent in calls), 240)

    def test_query_limit_and_sample_are_independent(self):
        run = Offline(self)
        result, rows = run.solve(lambda ctx: run.solutions.query(ctx))
        self.assertEqual((result["totalMatched"], result["count"], len(rows), result["truncated"]), (240, 100, 100, True))
        result, rows = run.solve(lambda ctx: run.solutions.query(ctx, limit=1000, sample=20))
        self.assertEqual((result["totalMatched"], result["count"], len(rows), result["truncated"]), (240, 240, 20, False))

    def test_prompts_schema_and_task_attributes(self):
        run = Offline(self)
        task = run.task
        self.assertEqual((task.id, task.case_version, task.track, task.tier, task.minutes), ("OC4", 1, "ops", "challenge", 30))
        self.assertEqual(task.requires, {"pipeline": "URP", "mode": "bypass", "dedicated": True, "packages": []})
        variables = task.prompt_vars(run.build_ctx)
        for lang in prompts.LANGS:
            with open(os.path.join(PACK_ROOT, "tasks", "OC4", "prompt.%s.md" % lang), "rb") as handle:
                raw = handle.read()
            self.assertFalse(raw.startswith(b"\xef\xbb\xbf"))
            self.assertNotIn(b"\r", raw)
            text = prompts.render_requirements(prompts.parse_prompt(raw.decode("utf-8")), variables)
            self.assertIn("240", text)
            self.assertIn(variables["material"], text)
            schema = prompts.render_schema(prompts.with_declared(task.answer_schema()), lang)
            self.assertIn('"declaredComplete": <boolean>', schema)
        self.assertIsNone(task.teardown(run.build_ctx))


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
