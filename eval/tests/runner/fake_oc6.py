"""OC6 in-memory graph: prefab sources, serialized probe anchors and component values.

Native import, Undo, prefab serialization and editor journal attribution remain live-gate concerns.
"""
import copy
import json
import os
import re

from .pack_offline import OpsFakeEditor
from .pack_fake_editor import FakeEditor, REPO, _fail, _g9, euler_from_q, q_from_euler
from usk_eval import verifylib as V

SURFACE = {
    "asset_create_folder": "AssetSkills.cs", "asset_get_info": "AssetSkills.cs", "asset_set_labels": "AssetImportSkills.cs",
    "component_add": "ComponentSkills.cs", "component_list": "ComponentSkills.cs",
    "component_get_properties": "ComponentSkills.cs", "component_set_property": "ComponentSkills.cs",
    "component_get_serialized_properties": "ComponentSkills.cs", "component_set_serialized_property": "ComponentSkills.cs",
    "gameobject_create": "GameObjectSkills.cs", "gameobject_delete": "GameObjectSkills.cs",
    "gameobject_duplicate": "GameObjectSkills.cs", "gameobject_get_info": "GameObjectSkills.cs",
    "gameobject_rename": "GameObjectSkills.cs", "gameobject_set_transform": "GameObjectSkills.cs",
    "gameobject_set_sibling_index": "GameObjectSkills.cs", "prefab_create": "PrefabSkills.cs",
    "prefab_unpack": "PrefabSkills.cs", "prefab_instantiate": "PrefabSkills.cs",
    "prefab_instantiate_batch": "PrefabSkills.cs", "prefab_find_instances": "PrefabSkills.cs",
    "prefab_set_transform": "PrefabSkills.cs", "prefab_get_hierarchy": "PrefabSkills.cs",
    "prefab_list_components": "PrefabSkills.cs", "prefab_get_serialized_properties": "PrefabSkills.cs",
    "prefab_get_overrides": "PrefabSkills.cs", "scene_save": "SceneSkills.cs",
}


def declarations():
    result = {}
    for skill, filename in SURFACE.items():
        with open(os.path.join(REPO, "SkillsForUnity", "Editor", "Skills", filename), encoding="utf-8-sig") as stream:
            source = stream.read()
        start = source.index('[UnitySkill("' + skill + '"')
        match = re.search(r"public\s+static\s+object\s+\w+\s*\(", source[start:])
        if match is None:
            raise AssertionError("No C# declaration: " + skill)
        opening = start + match.end() - 1
        depth, quoted, escaped, closing = 0, False, False, opening
        for closing in range(opening, len(source)):
            char = source[closing]
            if quoted:
                if escaped:
                    escaped = False
                elif char == "\\":
                    escaped = True
                elif char == '"':
                    quoted = False
                continue
            if char == '"':
                quoted = True
            elif char == "(":
                depth += 1
            elif char == ")":
                depth -= 1
                if depth == 0:
                    break
        signature = re.sub(r"\[[^\]]*\]", "", source[opening + 1:closing], flags=re.S)
        result[skill] = set(re.findall(r"\b(?:string|int|float|bool)\??\s+(\w+)", signature))
    return result


class GateEditor(OpsFakeEditor):
    extra_reads = frozenset(("prefab_get_hierarchy", "prefab_list_components", "prefab_get_serialized_properties",
                             "prefab_get_overrides", "prefab_find_instances"))
    parameters = None

    def __init__(self, project, pipeline="URP", entity_mode="legacy"):
        OpsFakeEditor.__init__(self, project, pipeline, entity_mode)
        if GateEditor.parameters is None:
            GateEditor.parameters = declarations()
        self.calls = []
        self.labels = {}

    def call(self, skill, args, agent):
        if skill in self.parameters and set(args) - self.parameters[skill]:
            raise AssertionError("Undeclared arguments: %s %s" % (skill, set(args) - self.parameters[skill]))
        self.calls.append((skill, copy.deepcopy(args), agent))
        return OpsFakeEditor.call(self, skill, args, agent)

    def _create(self, agent, item, parent):
        obj = FakeEditor._create(self, agent, item, parent)
        obj.values = {}
        return obj

    @staticmethod
    def _short(kind):
        return kind.rpartition(".")[2]

    def _skill_component_add(self, agent, args):
        kind = self._short(args["componentType"])
        result = FakeEditor._skill_component_add(self, agent, dict(args, componentType=kind))
        obj = self.find(args)
        obj.values[kind] = {"m_Size": (1, 1, 1), "m_Center": (0, 0, 0), "m_IsTrigger": False} if kind == "BoxCollider" else {
            "m_ProbeAnchor": None, "m_CastShadows": 1}
        return result

    def _skill_component_set_property(self, agent, args):
        if self._short(args["componentType"]) == "MeshRenderer" and args["propertyName"] == "probeAnchor":
            return self._skill_component_set_serialized_property(agent, dict(args, propertyPath="m_ProbeAnchor",
                                                                             objectType="UnityEngine.Transform"))
        return FakeEditor._skill_component_set_property(self, agent, dict(args, componentType=self._short(args["componentType"])))

    def _skill_component_set_serialized_property(self, agent, args):
        kind = self._short(args["componentType"])
        if kind == "Transform":
            return FakeEditor._skill_component_set_serialized_property(self, agent, dict(args, componentType=kind))
        obj = self.find(args)
        key = args["propertyPath"]
        if kind not in obj.values or key not in obj.values[kind]:
            raise _fail("TARGET_NOT_FOUND", "unknown serialized property")
        if key == "m_ProbeAnchor":
            if any(args.get(k) for k in ("referencePath", "referenceEntityId", "referenceInstanceId")):
                other = self.find({"path": args.get("referencePath"), "entityId": args.get("referenceEntityId"),
                                   "instanceId": args.get("referenceInstanceId")})
                value = ("object", other.eid, "Transform" if args.get("objectType", "").endswith("Transform") else "GameObject")
            else:
                value = None
        elif key in ("m_Size", "m_Center"):
            value = tuple(V.floats(args["value"]))
        elif key == "m_IsTrigger":
            value = str(args["value"]).lower() == "true"
        else:
            value = int(args["value"])
        obj.values[kind][key] = value
        self._change(agent, self._identity(obj, kind="property_changed", targetType=kind, property=key,
                                           previousValue="", value=str(value)))
        return {"success": True, "valueSet": self.format_value(value)}

    def _serialized_rows(self, obj, kind):
        kind = self._short(kind)
        if kind == "Transform":
            return [{"propertyPath": key, "value": _g9(value),
                     "propertyType": "Quaternion" if key == "m_LocalRotation" else "Vector3"}
                    for key, value in (("m_LocalPosition", obj.pos), ("m_LocalRotation", obj.rot), ("m_LocalScale", obj.scale))]
        if not self.component(obj, kind):
            raise _fail("TARGET_NOT_FOUND", "component not found")
        rows = [{"propertyPath": "m_Enabled", "value": str(self.component(obj, kind)[1]), "propertyType": "Boolean"}]
        for key, value in obj.values.get(kind, {}).items():
            row = {"propertyPath": key}
            if key == "m_ProbeAnchor":
                row.update(propertyType="ObjectReference", value=self.format_value(value),
                           referenceState="none" if value is None else "assigned" if value[1] in self.objects else "missing")
            elif isinstance(value, tuple):
                row.update(propertyType="Vector3", value=_g9(value))
            elif isinstance(value, bool):
                row.update(propertyType="Boolean", value=str(value))
            else:
                row.update(propertyType="Integer", value=str(value))
            rows.append(row)
        return rows

    def _skill_component_get_serialized_properties(self, agent, args):
        obj = self.find(args)
        return {"success": True, "properties": self._serialized_rows(obj, args["componentType"])[:args.get("limit", 200)]}

    def _skill_component_get_properties(self, agent, args):
        result = FakeEditor._skill_component_get_properties(self, agent, dict(args, componentType=self._short(args["componentType"])))
        obj = self.find(args)
        result["properties"].append({"name": "lossyScale", "value": _g9(self.world(obj)[2])})
        return result

    def _snapshot_nodes(self, root):
        prefix = self.path(root)
        rows = []
        for obj in self.walk([root]):
            path = self.path(obj)[len(prefix):].lstrip("/")
            rows.append({"path": path, "name": obj.name, "pos": obj.pos, "rot": obj.rot, "scale": obj.scale,
                         "active": obj.active, "tag": obj.tag, "layer": obj.layer,
                         "components": copy.deepcopy(obj.components), "values": copy.deepcopy(obj.values)})
        return rows

    def _save(self, path, agent):
        self._ensure_folder(path.rpartition("/")[0], agent)
        # The file is a deterministic stand-in; native YAML formatting is not simulated.
        self._write_file(path, "%YAML 1.1\n" + json.dumps(self.prefabs[path], sort_keys=True))
        self._imported(agent, [path])

    def _connect(self, root, path):
        root.prefab = path
        prefix = self.path(root)
        for obj in self.walk([root]):
            obj.link = root
            obj.relative = self.path(obj)[len(prefix):].lstrip("/")

    def _skill_prefab_create(self, agent, args):
        root, path = self.find(args), args["savePath"]
        self.prefabs[path] = self._snapshot_nodes(root)
        self.prefabs[path][0]["name"] = os.path.basename(path)[:-7]
        self._save(path, agent)
        self._connect(root, path)
        return {"success": True, "prefabPath": path, "connected": True, "name": self.prefabs[path][0]["name"]}

    def _skill_prefab_unpack(self, agent, args):
        root = self.find(args)
        if not hasattr(root, "prefab"):
            raise _fail("TARGET_NOT_FOUND", "not a prefab instance")
        for obj in self.walk([root]):
            if hasattr(obj, "link"):
                del obj.link
        del root.prefab
        self._change(agent, self._identity(root, kind="property_changed", targetType="GameObject", property="prefab"))
        return {"success": True, "unpacked": root.name}

    def _clone(self, nodes, parent, agent):
        made = {}
        for node in nodes:
            obj = self._create(agent, {"name": node["name"]}, parent if node["path"] == "" else made[node["path"].rpartition("/")[0]])
            obj.pos, obj.rot, obj.scale = tuple(node["pos"]), tuple(node["rot"]), tuple(node["scale"])
            obj.active, obj.tag, obj.layer = node["active"], node["tag"], node["layer"]
            obj.components, obj.values = copy.deepcopy(node["components"]), copy.deepcopy(node["values"])
            made[node["path"]] = obj
        return made[""]

    def _skill_gameobject_duplicate(self, agent, args):
        obj = self.find(args)
        root = self._clone(self._snapshot_nodes(obj), obj.parent, agent)
        root.name = obj.name + "_Copy"
        return {"success": True, "copyEntityId": root.eid, "copyInstanceId": root.iid,
                "copyPath": self.path(root), "copyName": root.name}

    def _skill_prefab_instantiate(self, agent, args):
        path = args["prefabPath"]
        if path not in self.prefabs:
            raise _fail("TARGET_NOT_FOUND", "prefab not found")
        root = self._clone(self.prefabs[path], self.find(args, "parent", required=False), agent)
        root.name = args.get("name") or root.name
        root.pos = tuple(float(args.get(key, 0)) for key in "xyz")
        self._connect(root, path)
        return dict(self._created(root), success=True)

    def _skill_prefab_instantiate_batch(self, agent, args):
        result = []
        for item in json.loads(args["items"]):
            allowed = {"prefabPath", "name", "x", "y", "z", "rotX", "rotY", "rotZ", "scaleX", "scaleY", "scaleZ",
                       "parentName", "parentPath", "parentInstanceId", "parentEntityId"}
            if set(item) - allowed:
                raise AssertionError("invalid prefab batch fields")
            row = self._skill_prefab_instantiate(agent, item)
            obj = self.find(row)
            if any(item.get(key, 0) for key in ("rotX", "rotY", "rotZ")):
                self._set_world(obj, rot=q_from_euler(tuple(item.get(key, 0) for key in ("rotX", "rotY", "rotZ"))))
            if any(item.get(key, 1) != 1 for key in ("scaleX", "scaleY", "scaleZ")):
                obj.scale = tuple(item.get(key, 1) for key in ("scaleX", "scaleY", "scaleZ"))
            result.append(row)
        return {"success": True, "results": result, "totalItems": len(result), "successCount": len(result), "failCount": 0}

    def _skill_prefab_set_transform(self, agent, args):
        nodes = self.prefabs[args["prefabPath"]]
        node = next(n for n in nodes if n["path"] == (args.get("gameObjectPath") or ""))
        for field, keys in (("pos", ("posX", "posY", "posZ")), ("scale", ("scaleX", "scaleY", "scaleZ"))):
            node[field] = tuple(float(args.get(key, old)) for key, old in zip(keys, node[field]))
        old = euler_from_q(node["rot"])
        node["rot"] = q_from_euler(tuple(float(args.get(key, value)) for key, value in zip(("rotX", "rotY", "rotZ"), old)))
        self._save(args["prefabPath"], agent)
        return {"success": True}

    def _skill_prefab_find_instances(self, agent, args):
        if args["prefabPath"] not in self.prefabs:
            raise _fail("TARGET_NOT_FOUND", "prefab not found")
        matches = [obj for obj in self.walk() if getattr(obj, "prefab", None) == args["prefabPath"] and
                   (args.get("includeInactive") or self.active_in_hierarchy(obj))]
        limit = args.get("limit", 50)
        return {"success": True, "instances": [self.describe(obj) for obj in matches[:limit]],
                "count": min(limit, len(matches)), "totalMatched": len(matches), "truncated": len(matches) > limit}

    def _asset_node(self, args):
        if args["prefabPath"] not in self.prefabs:
            raise _fail("TARGET_NOT_FOUND", "asset not found")
        return next(n for n in self.prefabs[args["prefabPath"]] if n["path"] == (args.get("gameObjectPath") or ""))

    def _skill_prefab_get_hierarchy(self, agent, args):
        if args["prefabPath"] not in self.prefabs:
            raise _fail("TARGET_NOT_FOUND", "asset not found")
        nodes = self.prefabs[args["prefabPath"]]
        result = []
        for node in nodes:
            above = None if node["path"] == "" else node["path"].rpartition("/")[0]
            siblings = [n for n in nodes if (None if n["path"] == "" else n["path"].rpartition("/")[0]) == above]
            result.append({"gameObjectPath": node["path"], "gameObject": node["name"], "parentPath": above,
                           "activeSelf": node["active"], "tag": node["tag"], "layer": 0 if node["layer"] == "Default" else node["layer"],
                           "siblingIndex": siblings.index(node), "prefabAssetType": "Regular", "localPosition": dict(zip("xyz", node["pos"])),
                           "localEulerAngles": dict(zip("xyz", euler_from_q(node["rot"]))),
                           "localScale": dict(zip("xyz", node["scale"]))})
        limit = args.get("limit", 200)
        return {"success": True, "nodes": result[:limit], "total": len(result), "truncated": len(result) > limit}

    def _skill_prefab_list_components(self, agent, args):
        node = self._asset_node(args)
        return {"success": True, "components": [dict(componentType="UnityEngine." + kind, componentIndex=0,
                                                      **({"enabled": enabled} if enabled is not None else {}))
                                                 for kind, enabled in node["components"]]}

    def _skill_prefab_get_serialized_properties(self, agent, args):
        node = self._asset_node(args)
        class ReadNode(object):
            pass
        obj = ReadNode()
        obj.pos, obj.rot, obj.scale = node["pos"], node["rot"], node["scale"]
        obj.components, obj.values = node["components"], node["values"]
        return {"success": True, "properties": self._serialized_rows(obj, args["componentType"])[:args.get("limit", 200)]}

    def _skill_prefab_get_overrides(self, agent, args):
        obj = self.find(args)
        root = getattr(obj, "link", None)
        if root is None or root.eid not in self.objects:
            raise _fail("TARGET_NOT_FOUND", "not an instance")
        asset = {row["path"]: row for row in self.prefabs[root.prefab]}
        now = self._snapshot_nodes(root)
        mods = []
        for node in now:
            old = asset.get(node["path"])
            if old is None:
                mods.append({"kind": "addedGameObject", "gameObjectPath": node["path"], "component": "GameObject"})
                continue
            for key in ("pos", "rot", "scale", "values", "components"):
                if node[key] != old[key]:
                    mods.append({"kind": "property", "gameObjectPath": node["path"], "component": "Transform" if key != "values" else "BoxCollider",
                                 "componentIndex": 0, "property": key, "value": str(node[key])})
        return {"success": True, "prefabPath": root.prefab, "propertyOverrides": len(mods), "addedComponents": 0,
                "removedComponents": 0, "addedGameObjects": 0, "hasOverrides": bool(mods),
                "overrides": mods, "count": len(mods), "totalMatched": len(mods), "truncated": False}

    def _skill_asset_get_info(self, agent, args):
        result = FakeEditor._skill_asset_get_info(self, agent, args)
        result["labels"] = list(self.labels.get(args["assetPath"], []))
        return result

    def _skill_asset_set_labels(self, agent, args):
        self.labels[args["assetPath"]] = args["labels"].split(",")
        with open(self.disk(args["assetPath"]) + ".meta", "a", encoding="utf-8") as stream:
            stream.write("labels:\n- " + args["labels"] + "\n")
        self._imported(agent, [args["assetPath"]])
        return {"success": True, "assetPath": args["assetPath"], "labels": args["labels"].split(",")}

# Producer:Betsy
