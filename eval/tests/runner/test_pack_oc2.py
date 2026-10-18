"""Offline OC2 skill model and exact-verdict tests; no Unity or network.

The model resolves variant inheritance, writes Unity YAML through the real verifylib parser,
preserves asset GUIDs on overwrite, propagates asset edits into scene instances, and keeps
scene overrides separate. Real Unity overwrite/fileID behavior remains a live gate.
"""
import copy
import json
import os
import tempfile
import unittest

from .pack_fake_editor import REPO, FakeClient, FakeEditor, _fail, remove_tree
from usk_eval import attempt, environment, fixture, journal, pack, prompts, verifylib as V
from usk_eval.rest import RUNNER_AGENT, SOLVER_AGENT
from usk_eval.task_api import BuildContext, SolverContext, VerifyContext

READS = frozenset(("prefab_get_hierarchy", "prefab_list_components", "prefab_get_serialized_properties",
                   "prefab_get_overrides"))
COLLIDERS = ("BoxCollider", "SphereCollider", "CapsuleCollider")
CLASS_IDS = {"GameObject": 1, "Transform": 4, "Light": 108, "BoxCollider": 65,
             "SphereCollider": 135, "CapsuleCollider": 136, "PrefabInstance": 1001}
PACK = os.path.join(REPO, "SkillsForUnity", "Evaluation~", "pack")


def keys(value):
    result = []
    if isinstance(value, dict):
        for key, item in value.items():
            result.append(key)
            result.extend(keys(item))
    elif isinstance(value, (list, tuple)):
        for item in value:
            result.extend(keys(item))
    return result


def modules():
    return (pack.load_task_file("OC2", os.path.join(PACK, "tasks", "OC2", "task.py")),
            pack.load_source_module("oc2_private_test", os.path.join(REPO, "eval", "pack", "private", "OC2.py")),
            pack.load_source_module("oc2_solutions_test", os.path.join(REPO, "eval", "pack", "solutions", "OC2.py")))


def _flow(value):
    if isinstance(value, dict):
        return "{" + ", ".join(str(k) + ": " + _flow(v) for k, v in value.items()) + "}"
    if isinstance(value, list):
        return "[" + ", ".join(_flow(v) for v in value) + "]"
    if isinstance(value, str):
        return json.dumps(value)
    return str(value)


def _vec(values):
    return dict(zip("xyzw", values))


def _text(value):
    if isinstance(value, dict):
        return ",".join(str(v) for v in value.values())
    return str(value)


class Client(FakeClient):
    def is_read_only(self, skill):
        return skill in READS or super().is_read_only(skill)

    def schema(self, names):
        return {name: {"name": name, "flags": ["readOnly"] if self.is_read_only(name) else ["mutatesScene"]}
                for name in names}

    def read(self, skill, **args):
        if skill in READS:
            return self.editor.call(skill, args, self.agent)
        return super().read(skill, **args)


class Editor(FakeEditor):
    def __init__(self, project, entity_mode="legacy"):
        super().__init__(project, "URP", entity_mode)
        self.prefabs = {}
        self.instances = []

    def client(self, agent):
        return Client(self, agent)

    def _create(self, agent, item, parent):
        obj = super()._create(agent, item, parent)
        obj.values = {}
        return obj

    @staticmethod
    def _defaults(kind):
        if kind == "Light":
            return {"m_Enabled": "1", "m_Intensity": "1", "m_Range": "10", "m_Color": "1,1,1,1"}
        if kind in COLLIDERS:
            return {"m_Enabled": "1", "m_IsTrigger": "0"}
        return {}

    def _skill_component_add(self, agent, args):
        result = super()._skill_component_add(agent, args)
        obj = self.find(args)
        obj.values[args["componentType"]] = self._defaults(args["componentType"])
        if hasattr(obj, "link"):
            obj.link.scene_added.append((obj.relative, args["componentType"]))
        return result

    def _serialized(self, obj, kind):
        if kind in obj.values:
            return dict(obj.values[kind])
        return super()._serialized(obj, kind)

    def _skill_component_set_serialized_property(self, agent, args):
        obj = self.find(args)
        kind, field = args["componentType"], args["propertyPath"]
        if kind not in obj.values:
            return super()._skill_component_set_serialized_property(agent, args)
        old = obj.values[kind].get(field)
        obj.values[kind][field] = str(args["value"])
        if hasattr(obj, "link"):
            obj.link.scene_mods[(obj.relative, kind, field)] = str(args["value"])
        self._change(agent, self._identity(obj, kind="property_changed", targetType=kind, property=field,
                                           previousValue=old, value=str(args["value"])))
        return {"success": True, "propertyPath": field, "valueSet": str(args["value"])}

    def resolved(self, path):
        asset = self.prefabs.get(path)
        if asset is None:
            raise _fail("TARGET_NOT_FOUND", "prefab not found")
        nodes = self.resolved(asset["source"]) if asset["source"] else copy.deepcopy(asset["nodes"])
        for (relative, kind, field), value in asset["mods"].items():
            self._assign(nodes, relative, kind, field, value)
        for relative, kind in asset["added"]:
            node = next(n for n in nodes if n["path"] == relative)
            node["components"].append(kind)
            node["values"][kind] = self._defaults(kind)
        nodes[0]["name"] = os.path.basename(path)[:-7]
        return nodes

    @staticmethod
    def _assign(nodes, relative, kind, field, value):
        node = next(n for n in nodes if n["path"] == relative)
        if kind == "Transform" and field.startswith("m_LocalScale"):
            scale = list(node["scale"])
            scale["xyz".index(field[-1])] = float(value)
            node["scale"] = tuple(scale)
        elif kind == "Transform" and field.startswith("m_LocalPosition"):
            pos = list(node["pos"])
            pos["xyz".index(field[-1])] = float(value)
            node["pos"] = tuple(pos)
        else:
            node["values"][kind][field] = str(value)

    def _asset_value(self, path, relative, kind, field):
        node = next(n for n in self.resolved(path) if n["path"] == relative)
        if kind == "Transform":
            vec = node["scale"] if field.startswith("m_LocalScale") else node["pos"]
            return str(vec["xyz".index(field[-1])])
        return node["values"][kind].get(field)

    def _ids(self, path, relative, kind):
        source = self.prefabs[path]["source"]
        if source:
            return self._ids(source, relative, kind)
        nodes = self.prefabs[path]["nodes"]
        i = next(i for i, n in enumerate(nodes) if n["path"] == relative)
        node = nodes[i]
        if kind == "GameObject":
            return 10000 + i * 100
        index = node["components"].index(kind) if kind in node["components"] else len(node["components"])
        return 10001 + i * 100 + index

    def _save(self, path, agent):
        asset = self.prefabs[path]
        docs = []
        if asset["source"]:
            source = asset["source"]
            mods = [{"target": {"fileID": self._ids(source, rel, kind), "guid": self._guid(source), "type": 3},
                     "propertyPath": field, "value": value, "objectReference": {"fileID": 0}}
                    for (rel, kind, field), value in asset["mods"].items()]
            added = []
            for i, (rel, kind) in enumerate(asset["added"]):
                owner, comp = 70000 + i, 80000 + i
                docs.append(("GameObject", owner, True, {"m_CorrespondingSourceObject": {
                    "fileID": self._ids(source, rel, "GameObject"), "guid": self._guid(source), "type": 3}}))
                docs.append((kind, comp, False, {"m_GameObject": {"fileID": owner}, **self._defaults(kind)}))
                added.append({"targetCorrespondingSourceObject": {
                    "fileID": self._ids(source, rel, "GameObject"), "guid": self._guid(source), "type": 3},
                    "insertIndex": -1, "addedObject": {"fileID": comp}})
            docs.insert(0, ("PrefabInstance", 100100000, False, {
                "m_Modification": {"m_TransformParent": {"fileID": 0}, "m_Modifications": mods,
                                   "m_RemovedComponents": [], "m_AddedComponents": added, "m_AddedGameObjects": []},
                "m_SourcePrefab": {"fileID": 100100000, "guid": self._guid(source), "type": 3}}))
        else:
            for node in self.resolved(path):
                rel = node["path"]
                go_id = self._ids(path, rel, "GameObject")
                refs = [{"component": {"fileID": self._ids(path, rel, kind)}} for kind in node["components"]]
                docs.append(("GameObject", go_id, False, {"m_Name": node["name"], "m_IsActive": 1,
                             "m_Layer": 0, "m_TagString": "Untagged", "m_Component": refs}))
                for kind in node["components"]:
                    body = {"m_GameObject": {"fileID": go_id}}
                    if kind == "Transform":
                        body.update(m_LocalPosition=_vec(node["pos"]), m_LocalRotation=_vec(node["rot"]),
                                    m_LocalScale=_vec(node["scale"]),
                                    m_Father={"fileID": self._ids(path, node["parent"], "Transform")
                                              if node["parent"] is not None else 0},
                                    m_Children=[{"fileID": self._ids(path, n["path"], "Transform")}
                                                for n in self.resolved(path) if n["parent"] == rel])
                    else:
                        body.update(node["values"][kind])
                    docs.append((kind, self._ids(path, rel, kind), False, body))
        lines = ["%YAML 1.1", "%TAG !u! tag:unity3d.com,2011:"]
        for kind, ident, stripped, body in docs:
            lines.append("--- !u!%s &%s%s" % (CLASS_IDS[kind], ident, " stripped" if stripped else ""))
            lines.append(kind + ":")
            lines.extend("  %s: %s" % (key, _flow(value)) for key, value in body.items())
        self._write_file(path, "\n".join(lines) + "\n")
        self._imported(agent, [path])
        self._propagate()

    def _propagate(self):
        for root in self.instances:
            if root.eid not in self.objects or root.prefab not in self.prefabs:
                continue
            nodes = self.resolved(root.prefab)
            for obj in self.walk([root]):
                if not hasattr(obj, "relative"):
                    continue
                node = copy.deepcopy(next(n for n in nodes if n["path"] == obj.relative))
                for (rel, kind, field), value in root.scene_mods.items():
                    if rel == obj.relative:
                        self._assign([node], rel, kind, field, value)
                obj.pos, obj.rot, obj.scale = node["pos"], node["rot"], node["scale"]
                obj.components = [[kind, True if kind != "Transform" else None] for kind in node["components"]]
                obj.values = copy.deepcopy(node["values"])
                for rel, kind in root.scene_added:
                    if rel == obj.relative:
                        obj.components.append([kind, True])
                        obj.values[kind] = self._defaults(kind)

    def _skill_prefab_create(self, agent, args):
        root, path = self.find(args), args["savePath"]
        if hasattr(root, "prefab"):
            self.prefabs[path] = {"source": root.prefab, "nodes": [], "mods": dict(root.scene_mods),
                                  "added": list(root.scene_added)}
        else:
            nodes = []
            prefix = self.path(root)
            for obj in self.walk([root]):
                rel = self.path(obj)[len(prefix):].lstrip("/")
                nodes.append({"path": rel, "name": obj.name, "parent": rel.rpartition("/")[0] if rel else None,
                              "pos": obj.pos, "rot": obj.rot, "scale": obj.scale,
                              "components": [c[0] for c in obj.components], "values": copy.deepcopy(obj.values)})
            self.prefabs[path] = {"source": None, "nodes": nodes, "mods": {}, "added": []}
        self._save(path, agent)
        root.prefab, root.scene_mods, root.scene_added = path, {}, []
        return {"success": True, "prefabPath": path, "name": os.path.basename(path)[:-7], "connected": True}

    def _skill_prefab_create_variant(self, agent, args):
        source, path = args["sourcePrefabPath"], args["variantPath"]
        self.resolved(source)
        self.prefabs[path] = {"source": source, "nodes": [], "mods": {}, "added": []}
        self._save(path, agent)
        return {"success": True, "sourcePath": source, "variantPath": path, "isVariant": True}

    def _skill_prefab_set_serialized_property(self, agent, args):
        path, rel = args["prefabPath"], args.get("gameObjectPath") or ""
        key = (rel, args["componentType"], args["propertyPath"])
        asset = self.prefabs[path]
        if asset["source"]:
            asset["mods"][key] = str(args["value"])
        else:
            self._assign(asset["nodes"], *key, args["value"])
        self._save(path, agent)
        return {"success": True, "valueSet": str(args["value"])}

    def _skill_prefab_set_transform(self, agent, args):
        for axis in "XYZ":
            if "scale" + axis in args:
                self._skill_prefab_set_serialized_property(agent, {
                    "prefabPath": args["prefabPath"], "gameObjectPath": args.get("gameObjectPath"),
                    "componentType": "Transform", "propertyPath": "m_LocalScale." + axis.lower(),
                    "value": str(args["scale" + axis])})
        return {"success": True}

    def _skill_prefab_add_component(self, agent, args):
        path, rel, kind = args["prefabPath"], args.get("gameObjectPath") or "", args["componentType"]
        asset = self.prefabs[path]
        if asset["source"]:
            asset["added"].append((rel, kind))
        else:
            node = next(n for n in asset["nodes"] if n["path"] == rel)
            node["components"].append(kind)
            node["values"][kind] = self._defaults(kind)
        self._save(path, agent)
        return {"success": True, "componentType": "UnityEngine." + kind, "componentIndex": 0}

    def _skill_prefab_edit_batch(self, agent, args):
        results = []
        for step in json.loads(args["steps"]):
            results.append(self.call(step["skill"], dict(step["args"], prefabPath=args["prefabPath"]), agent))
        return {"success": True, "results": results}

    def _skill_prefab_get_hierarchy(self, agent, args):
        path, nodes = args["prefabPath"], self.resolved(args["prefabPath"])
        result = []
        for node in nodes:
            siblings = [n for n in nodes if n["parent"] == node["parent"]]
            result.append({"gameObjectPath": node["path"], "gameObject": node["name"],
                           "parentPath": node["parent"], "activeSelf": True, "layer": 0, "tag": "Untagged",
                           "siblingIndex": siblings.index(node),
                           "childCount": sum(n["parent"] == node["path"] for n in nodes),
                           "localPosition": _vec(node["pos"]), "localEulerAngles": _vec((0, 0, 0)),
                           "localScale": _vec(node["scale"]),
                           "prefabAssetType": "Variant" if self.prefabs[path]["source"] else "Regular"})
        limit = args.get("limit", 200)
        return {"success": True, "total": len(result), "truncated": len(result) > limit, "nodes": result[:limit]}

    def _skill_prefab_list_components(self, agent, args):
        node = next(n for n in self.resolved(args["prefabPath"]) if n["path"] == (args.get("gameObjectPath") or ""))
        return {"success": True, "components": [{"componentType": "UnityEngine." + kind, "componentIndex": 0}
                                                  for kind in node["components"]]}

    def _skill_prefab_get_serialized_properties(self, agent, args):
        node = next(n for n in self.resolved(args["prefabPath"]) if n["path"] == (args.get("gameObjectPath") or ""))
        values = node["values"].get(args["componentType"])
        if values is None:
            raise _fail("TARGET_NOT_FOUND", "component not found")
        return {"success": True, "properties": [{"propertyPath": k, "value": _text(v)} for k, v in values.items()]}

    def _skill_prefab_instantiate(self, agent, args):
        nodes = self.resolved(args["prefabPath"])
        parent, created = self.find(args, "parent", required=False), {}
        for node in nodes:
            obj = self._create(agent, {"name": args.get("name", node["name"]) if not node["path"] else node["name"]},
                               parent if node["parent"] is None else created[node["parent"]])
            obj.relative = node["path"]
            created[node["path"]] = obj
        root = created[""]
        root.prefab, root.scene_mods, root.scene_added = args["prefabPath"], {}, []
        for axis, key in zip("xyz", ("x", "y", "z")):
            value = float(args.get(key, 0))
            if value != nodes[0]["pos"]["xyz".index(axis)]:
                root.scene_mods[("", "Transform", "m_LocalPosition." + axis)] = str(value)
        for obj in created.values():
            obj.link = root
        self.instances.append(root)
        self._propagate()
        return dict(self._created(root), success=True)

    def _skill_prefab_instantiate_batch(self, agent, args):
        rows = [self._skill_prefab_instantiate(agent, item) for item in json.loads(args["items"])]
        return {"success": True, "results": rows, "totalItems": len(rows), "successCount": len(rows), "failCount": 0}

    def _skill_prefab_get_overrides(self, agent, args):
        obj = self.find(args)
        if not hasattr(obj, "link"):
            raise _fail("TARGET_NOT_FOUND", "not a prefab instance")
        root = obj.link
        overrides = []
        for (rel, kind, field), value in root.scene_mods.items():
            if field == "m_Name" or value == self._asset_value(root.prefab, rel, kind, field):
                continue
            overrides.append({"kind": "property", "gameObjectPath": rel, "component": kind,
                              "componentIndex": 0, "property": field, "value": value})
        overrides.extend({"kind": "addedComponent", "gameObjectPath": rel, "component": kind, "componentIndex": 0}
                         for rel, kind in root.scene_added)
        limit = max(0, args.get("limit", 50))
        result = {"success": True, "prefabPath": root.prefab,
                  "propertyOverrides": sum(o["kind"] == "property" for o in overrides),
                  "addedComponents": len(root.scene_added), "removedComponents": 0, "addedGameObjects": 0,
                  "hasOverrides": bool(overrides)}
        if args.get("includeDetails"):
            result.update(overrides=overrides[:limit], count=min(limit, len(overrides)),
                          totalMatched=len(overrides), truncated=len(overrides) > limit)
        return result

    def _skill_prefab_apply_overrides(self, agent, args):
        root = self.find(args).link
        asset = self.prefabs[root.prefab]
        for key, value in list(root.scene_mods.items()):
            if key[1] == "Transform" and not key[0]:
                continue
            if asset["source"]:
                asset["mods"][key] = value
            else:
                self._assign(asset["nodes"], *key, value)
            del root.scene_mods[key]
        self._save(root.prefab, agent)
        return {"success": True, "appliedTo": root.prefab}


class Offline(object):
    def __init__(self, test, seed=7607, entity_mode="legacy"):
        self.tmp = tempfile.mkdtemp(prefix="usk-oc2-")
        test.addCleanup(remove_tree, self.tmp)
        self.project = os.path.join(self.tmp, "Proj")
        self.editor = Editor(self.project, entity_mode)
        self.task, self.private, self.solutions = modules()
        self.seed = seed
        self.scope = fixture.Scope(fixture.new_run_id())
        client = self.editor.client(RUNNER_AGENT)
        fixture.enter(client, self.scope, lambda: None)
        self.env = environment.snapshot(client, 4242)
        self.ctx = BuildContext(client, self.scope, seed, self.project, self.env, "en", "ops", "challenge", self.task)
        self.handles = self.task.build(self.ctx)
        attempt._check_handles(self.handles)
        self.ctx.handles = self.handles
        fixture.save_run_scene(client, self.scope)
        self.snap = self.task.snapshot(self.ctx)
        self.fingerprint = V.fingerprint(self.ctx)
        self.cursor0 = self.editor.seq

    def solve(self, fn):
        ctx = SolverContext(self.editor.client(SOLVER_AGENT), self.scope, self.seed, self.project, self.env,
                            "en", "ops", "challenge", self.task, handles=self.handles, private=self.private)
        return fn(ctx)

    def verify(self, answer):
        start = self.editor.seq
        self.editor.refresh()
        window = {"from": start, "to": self.editor.seq}
        records = journal.flatten([e for e in self.editor.entries if e["seq"] > self.cursor0], window)
        ctx = VerifyContext(self.editor.client(RUNNER_AGENT), self.scope, self.seed, self.project, self.env,
                            "en", "ops", "challenge", self.task, handles=self.handles, c0=self.snap["data"],
                            c0_digests=self.snap["digests"], answer=answer, records=records, journal_complete=True,
                            journal={"cursor0": self.cursor0, "cursorEnd": self.editor.seq, "refreshWindow": window})
        card = V.Scorecard(self.private.ASSERTIONS)
        self.private.verify(ctx, card)
        return card.finish()


def detail(card):
    return json.dumps([row for row in card.as_dict()["assertions"] if not row["pass"]], default=str)


class OC2Tests(unittest.TestCase):
    def test_reference_negatives_alternatives_and_rearranged_seed(self):
        for seed in (7607, 99173):
            for mode in ("legacy", "6000.4"):
                for name in ("reference",):
                    run = Offline(self, seed, mode)
                    card = run.verify(run.solve(getattr(run.solutions, name)))
                    self.assertTrue(card.strict, "%s %s %s %s" % (seed, mode, name, detail(card)))
                for negative in run.solutions.NEGATIVES:
                    with self.subTest(seed=seed, mode=mode, negative=negative.name):
                        run = Offline(self, seed, mode)
                        card = run.verify(run.solve(negative.apply))
                        self.assertEqual(set(card.failed()), negative.expect_failed, detail(card))
                for name, fn in run.solutions.ALTERNATIVES:
                    with self.subTest(seed=seed, mode=mode, alternative=name):
                        run = Offline(self, seed, mode)
                        card = run.verify(run.solve(fn))
                        self.assertTrue(card.strict, detail(card))

    def test_fingerprint_repeat_and_second_seed_rearranges_structure(self):
        first, second, third = Offline(self), Offline(self), Offline(self, 99173)
        self.assertEqual(first.fingerprint, second.fingerprint)
        self.assertNotEqual(first.fingerprint, third.fingerprint)
        a, b = first.task.observe(first.ctx), third.task.observe(third.ctx)
        self.assertNotEqual([n["gameObjectPath"] for n in a[0]["nodes"]], [n["gameObjectPath"] for n in b[0]["nodes"]])
        self.assertNotEqual([n["childCount"] for n in a[0]["nodes"]], [n["childCount"] for n in b[0]["nodes"]])
        for run in (first, second):
            card = run.verify(run.solve(run.solutions.reference))
            self.assertTrue(card.strict, detail(card))

    def test_untouched_fixture(self):
        for seed in (7607, 99173):
            run = Offline(self, seed)
            self.assertEqual(set(run.verify(None).failed()), {"A1", "A3", "A4", "A5", "A9"})

    def test_reporting_completion_does_not_satisfy_asset_work(self):
        run = Offline(self)
        answer = run.solutions._report(run.private.family(run.ctx))
        self.assertEqual(set(run.verify(answer).failed()), {"A1", "A3", "A4", "A5"})

    def test_prompts_schema_and_digest_only_c0(self):
        run = Offline(self)
        self.assertEqual(run.snap["data"], {})
        self.assertFalse([key for key in keys(run.handles) + keys(run.snap)
                          if isinstance(key, str) and attempt.HANDLE_KEY_RE.search(key)])
        json.dumps(run.snap, allow_nan=False)
        values = run.task.prompt_vars(run.ctx)
        for lang in ("zh-CN", "en"):
            with open(os.path.join(PACK, "tasks", "OC2", "prompt.%s.md" % lang), encoding="utf-8") as handle:
                text = prompts.render_requirements(prompts.parse_prompt(handle.read()), values)
            for value in values.values():
                self.assertIn(str(value), text)
            schema = prompts.render_schema(prompts.with_declared(run.task.answer_schema()), lang)
            self.assertIn('"declaredComplete": <boolean>', schema)
        self.assertEqual((run.task.case_version, run.task.tier, run.task.minutes), (1, "challenge", 20))
        self.assertEqual(run.task.requires, {"pipeline": "URP", "mode": "bypass", "dedicated": True, "packages": []})

    def test_raw_equal_value_overrides_are_rejected(self):
        run = Offline(self)
        answer = run.solve(run.solutions.reference)
        f = run.private.family(run.ctx)
        run.editor.call("prefab_set_serialized_property", {"prefabPath": f["tank"]["path"],
                        "gameObjectPath": f["base"]["node"], "componentType": "Light",
                        "propertyPath": "m_Intensity", "value": str(f["base"]["range"])}, SOLVER_AGENT)
        scale = run.private._node(f["base"], f["base"]["node"])["localScale"]
        run.editor.call("prefab_set_transform", {"prefabPath": f["tank"]["path"],
                        "gameObjectPath": f["base"]["node"], "scaleX": scale["x"],
                        "scaleY": scale["y"], "scaleZ": scale["z"]}, SOLVER_AGENT)
        card = run.verify(answer)
        self.assertEqual(set(card.failed()), {"A3", "A4"}, detail(card))

    def test_missing_assets_and_objects_fail_without_raising(self):
        for remove_base in (True, False):
            run = Offline(self)
            if remove_base:
                f = run.private.family(run.ctx)
                path = f["base"]["path"]
                del run.editor.prefabs[path]
                run.editor._skill_asset_delete(SOLVER_AGENT, {"assetPath": path})
            else:
                run.editor._skill_gameobject_delete(SOLVER_AGENT, V.address(run.handles["objects"][0]))
            card = run.verify(None)
            self.assertFalse(card.strict)
            self.assertIn("A6", card.failed())

    def test_extra_equal_fast_scale_override_fails_preservation(self):
        run = Offline(self)
        answer = run.solve(run.solutions.reference)
        f = run.private.family(run.ctx)
        scale = run.private._node(f["fast"], f["base"]["node"])["localScale"]
        run.editor.call("prefab_set_transform", {"prefabPath": f["fast"]["path"],
                        "gameObjectPath": f["base"]["node"], "scaleX": scale["x"],
                        "scaleY": scale["y"], "scaleZ": scale["z"]}, SOLVER_AGENT)
        card = run.verify(answer)
        self.assertEqual(set(card.failed()), {"A6"}, detail(card))

    def test_unrelated_document_order_is_not_asset_content(self):
        run = Offline(self)
        f = run.private.family(run.ctx)
        args = (f["light"].file_id, f["owner"], f["transform"].file_id, f["kind"])
        content = run.task.__class__.__module__
        import sys
        public = sys.modules[content]
        self.assertEqual(public._content(f["base"]["docs"], *args),
                         public._content(list(reversed(f["base"]["docs"])), *args))

    def test_block_style_variant_modifications(self):
        unused, private, unused_solutions = modules()
        docs = V.uyaml.loads("""%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!1001 &100100000
PrefabInstance:
  m_Modification:
    m_TransformParent: {fileID: 0}
    m_Modifications:
    - target: {fileID: 10801, guid: abcdef0123456789abcdef0123456789,
        type: 3}
      propertyPath: m_Intensity
      value: 8
      objectReference: {fileID: 0}
    - target: {fileID: 401, guid: abcdef0123456789abcdef0123456789, type: 3}
      propertyPath: m_LocalScale.x
      value: 2.5
      objectReference: {fileID: 0}
    m_RemovedComponents: []
    m_AddedComponents: []
  m_SourcePrefab: {fileID: 100100000, guid: abcdef0123456789abcdef0123456789, type: 3}
""")
        row = {"docs": docs}
        self.assertEqual([m["value"] for m in private._mods(row, 10801, "m_Intensity")], ["8"])
        self.assertEqual([m["propertyPath"] for m in private._mods(row, 401, "m_LocalScale")], ["m_LocalScale.x"])

# Producer:Betsy
