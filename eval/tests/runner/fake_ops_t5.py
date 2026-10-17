"""In-memory editor for the offline tests of T5: prefab assets with variants, instances and their overrides.

Built on OpsFakeEditor (and through it FakeEditor) and modelled on the plugin's PrefabSkills / PrefabAssetEditor /
GameObjectSkills code. Prefab assets are real files on disk in Unity's YAML shape (a base prefab with root GameObject,
Transform, Rigidbody and a child; a variant as a PrefabInstance document with the modifications of its source), so the
verifier's uyaml reading runs for real. Instances carry a link to their prefab and an optional mass override.

Router: from Unity 6000.4 a skill without an entityId parameter (components, prefab_create, prefab_apply, ...) gets an
entityId forwarded as a hierarchy path, and only when that path names the object alone (SkillRouter.Validation.cs
NormalizeSyntheticEntityIdLocator); earlier versions forward the legacy instanceId.

Journal: a prefab asset edit is journaled the way plugin 2.9.0 did it (a scene-less property_changed record named
after the asset's root object, no scenePath); the current plugin may emit it or not, the verifier tolerates both.
"""
import hashlib
import os

from .pack_fake_editor import Obj
from .pack_offline import OpsFakeEditor
from usk_eval.rest import SkillError  # noqa: E402

NO_ENTITY_ID = frozenset(("component_add", "component_list", "component_set_property",
                          "component_get_serialized_properties", "prefab_create", "prefab_apply", "prefab_unpack"))


def _fail(code, message):
    return SkillError("%s failed: %s" % (code, message), payload={"status": "error", "errorCode": code,
                                                                 "error": message})


def _num(value):
    return "%g" % value


def _flow(indent, key, ident, guid):
    """A `- target: {...}` line wrapped the way Unity wraps long flow mappings."""
    line = "%s- %s: {fileID: %d, guid: %s, type: 3}" % (" " * indent, key, ident, guid)
    if len(line) <= 80:
        return [line]
    head = "%s- %s: {fileID: %d, guid: %s," % (" " * indent, key, ident, guid)
    return [head, "%stype: 3}" % (" " * (indent + 4))]


class FakeEditorT5(OpsFakeEditor):
    extra_reads = frozenset(("prefab_find_instances",))

    def __init__(self, project, pipeline="URP", entity_mode="legacy", unity="2022.3.62f2", journal_path=None):
        OpsFakeEditor.__init__(self, project, pipeline, entity_mode, unity, journal_path)
        self.prefabs = {}

    # ------------------------------------------------------------ router

    def call(self, skill, args, agent):
        return OpsFakeEditor.call(self, skill, self._route(skill, dict(args)), agent)

    def _route(self, skill, args):
        entity = args.get("entityId")
        if skill not in NO_ENTITY_ID or entity in (None, ""):
            return args
        obj = self.objects.get(str(entity))
        if obj is None:
            raise _fail("SEMANTIC_INVALID", "Object not found for entityId: %s" % entity)
        del args["entityId"]
        if self.entity_mode != "6000.4":
            args["instanceId"] = obj.iid
            return args
        path = self.path(obj)
        sharing = [o for o in self.walk() if self.path(o).lower() == path.lower()]
        if len(sharing) != 1:
            raise _fail("SEMANTIC_INVALID", "entityId %s resolves to '%s', a path %d objects share, and this Unity "
                                            "version has no legacy instanceId" % (entity, path, len(sharing)))
        args["path"] = path
        return args

    # ------------------------------------------------------------ mass of instances and prefabs

    def prefab_mass(self, path):
        prefab = self.prefabs[path]
        return prefab["mass"] if prefab["mass"] is not None else self.prefab_mass(prefab["source"])

    def mass_of(self, obj):
        link = getattr(obj, "prefab", None)
        if link:
            return link["mass"] if link["mass"] is not None else self.prefab_mass(link["source"])
        return getattr(obj, "mass", None)

    def _skill_component_add(self, agent, args):
        result = OpsFakeEditor._skill_component_add(self, agent, args)
        obj = self.find(args)
        if args["componentType"].lower() == "rigidbody" and getattr(obj, "mass", None) is None:
            obj.mass = 1.0
        return result

    def _skill_component_set_property(self, agent, args):
        if args["componentType"].lower() != "rigidbody":
            return OpsFakeEditor._skill_component_set_property(self, agent, args)
        obj = self.find(args)
        if not self.component(obj, "Rigidbody") or args["propertyName"] != "mass":
            raise _fail("PROPERTY_NOT_FOUND", "Rigidbody.%s is not modelled" % args["propertyName"])
        before, value = self.mass_of(obj), float(args["value"])
        link = getattr(obj, "prefab", None)
        if link:
            link["mass"] = value
        else:
            obj.mass = value
        self._change(agent, self._identity(obj, kind="property_changed", targetType="Rigidbody", property="m_Mass",
                                           previousValue=_num(before), value=_num(value)))
        return {"success": True}

    # ------------------------------------------------------------ prefab files

    @staticmethod
    def _ids(path):
        base = int(hashlib.md5(path.encode("utf-8")).hexdigest()[:15], 16)
        return {"go": base, "tr": base + 1, "rb": base + 2, "label_go": base + 3, "label_tr": base + 4,
                "inst": base + 5, "asset": base % 900000 + 100000}

    def _base_of(self, path):
        prefab = self.prefabs[path]
        return prefab if prefab["source"] is None else self._base_of(prefab["source"])

    def _base_yaml(self, path, prefab):
        ids = self._ids(path)
        header = ["%YAML 1.1", "%TAG !u! tag:unity3d.com,2011:"]

        def doc(class_id, ident, kind, *lines):
            return ["--- !u!%d &%d" % (class_id, ident), kind + ":", "  m_ObjectHideFlags: 0",
                    "  m_CorrespondingSourceObject: {fileID: 0}", "  m_PrefabInstance: {fileID: 0}",
                    "  m_PrefabAsset: {fileID: 0}"] + list(lines)

        def transform(ident, owner, father, children):
            return doc(4, ident, "Transform", "  m_GameObject: {fileID: %d}" % owner, "  serializedVersion: 2",
                       "  m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}", "  m_LocalPosition: {x: 0, y: 0, z: 0}",
                       "  m_LocalScale: {x: 1, y: 1, z: 1}", "  m_Children:" if children else "  m_Children: []",
                       *(["  - {fileID: %d}" % child for child in children]),
                       "  m_Father: {fileID: %s}" % father, "  m_LocalEulerAnglesHint: {x: 0, y: 0, z: 0}")

        lines = list(header)
        components = ["  - component: {fileID: %d}" % ids["tr"], "  - component: {fileID: %d}" % ids["rb"]]
        lines += doc(1, ids["go"], "GameObject", "  serializedVersion: 6", "  m_Component:", *components,
                     "  m_Layer: 0", "  m_Name: %s" % prefab["name"], "  m_TagString: Untagged", "  m_IsActive: 1")
        children = [ids["label_tr"]] if prefab.get("label") else []
        lines += transform(ids["tr"], ids["go"], 0, children)
        lines += doc(54, ids["rb"], "Rigidbody", "  m_GameObject: {fileID: %d}" % ids["go"], "  serializedVersion: 2",
                     "  m_Mass: %s" % _num(prefab["mass"]), "  m_Drag: 0", "  m_AngularDrag: 0.05", "  m_UseGravity: 1",
                     "  m_IsKinematic: 0")
        if prefab.get("label"):
            lines += doc(1, ids["label_go"], "GameObject", "  serializedVersion: 6", "  m_Component:",
                         "  - component: {fileID: %d}" % ids["label_tr"], "  m_Layer: 0", "  m_Name: Label",
                         "  m_TagString: Untagged", "  m_IsActive: 1")
            lines += transform(ids["label_tr"], ids["label_go"], ids["tr"], [])
        return "\n".join(lines) + "\n"

    def _variant_yaml(self, path, prefab):
        ids, source = self._ids(path), prefab["source"]
        base_path = self._root_path(source)
        base_ids, base_guid = self._ids(base_path), self._guid(base_path)
        mods = [("go", "m_Name", prefab["name"])]
        if prefab["mass"] is not None:
            mods.append(("rb", "m_Mass", _num(prefab["mass"])))
        lines = ["%YAML 1.1", "%TAG !u! tag:unity3d.com,2011:", "--- !u!1001 &%d" % ids["inst"], "PrefabInstance:",
                 "  m_ObjectHideFlags: 0", "  serializedVersion: 2", "  m_Modification:",
                 "    m_TransformParent: {fileID: 0}", "    m_Modifications:"]
        for role, prop, value in mods:
            lines += _flow(4, "target", base_ids[role], base_guid)
            lines += ["      propertyPath: %s" % prop, "      value: %s" % value, "      objectReference: {fileID: 0}"]
        lines += ["    m_RemovedComponents: []", "    m_RemovedGameObjects: []", "    m_AddedGameObjects: []",
                  "    m_AddedComponents: []", "  m_SourcePrefab: {fileID: 100100000, guid: %s, type: 3}" % self._guid(source)]
        return "\n".join(lines) + "\n"

    def _root_path(self, path):
        """Asset path of the base prefab a prefab ultimately derives from (the modifications target its objects)."""
        prefab = self.prefabs[path]
        return path if prefab["source"] is None else self._root_path(prefab["source"])

    def _write_prefab(self, agent, path):
        prefab = self.prefabs[path]
        if prefab["source"] is None:
            text = self._base_yaml(path, prefab)
        else:
            text = self._variant_yaml(path, prefab)
        self._write_file(path, text)
        self._imported(agent, [path])

    def _asset_edit(self, agent, path, before, after):
        """Plugin 2.9.0's journal record of an edit made on the prefab asset itself."""
        ident = self._ids(path)["asset"]
        entity, legacy = (str(2 ** 33 + ident), 0) if self.entity_mode == "6000.4" else (str(ident), ident)
        stem = os.path.splitext(os.path.basename(path))[0]
        self._change(agent, {"kind": "property_changed", "targetType": "Rigidbody", "property": "m_Mass",
                             "previousValue": _num(before), "value": _num(after), "name": stem, "entityId": entity,
                             "instanceId": legacy, "path": stem, "scenePath": None})

    def _set_prefab_mass(self, agent, path, value):
        before = self.prefab_mass(path)
        self.prefabs[path]["mass"] = value
        self._write_prefab(agent, path)
        self._asset_edit(agent, path, before, value)

    @staticmethod
    def _prefab_path(save_path):
        save_path = save_path.replace("\\", "/")
        return save_path if save_path.lower().endswith(".prefab") else save_path + ".prefab"

    # ------------------------------------------------------------ prefab skills

    def _skill_prefab_create(self, agent, args):
        obj = self.find(args)
        path = self._prefab_path(args["savePath"])
        self._ensure_folder(path.rsplit("/", 1)[0], agent)
        stem = os.path.splitext(os.path.basename(path))[0]
        link = getattr(obj, "prefab", None)
        if link:
            self.prefabs[path] = {"name": stem, "source": link["source"], "mass": link["mass"]}
        else:
            self.prefabs[path] = {"name": stem, "source": None, "mass": self.mass_of(obj) or 1.0, "mesh": obj.mesh,
                                  "label": any(child.name == "Label" for child in obj.children)}
        obj.prefab = {"source": path, "mass": None}
        self._write_prefab(agent, path)
        return {"success": True, "prefabPath": path, "name": stem, "connected": True}

    def _skill_prefab_create_variant(self, agent, args):
        source = args["sourcePrefabPath"]
        if source not in self.prefabs:
            raise _fail("TARGET_NOT_FOUND", "Prefab not found: %s" % source)
        path = self._prefab_path(args["variantPath"])
        self._ensure_folder(path.rsplit("/", 1)[0], agent)
        stem = os.path.splitext(os.path.basename(path))[0]
        self.prefabs[path] = {"name": stem, "source": source, "mass": None}
        self._write_prefab(agent, path)
        return {"success": True, "sourcePath": source, "variantPath": path, "name": stem, "isVariant": True}

    def _skill_prefab_instantiate(self, agent, args):
        path = args["prefabPath"]
        if path not in self.prefabs:
            raise _fail("TARGET_NOT_FOUND", "Prefab not found: %s" % path)
        parent = self.find(args, "parent", required=False)
        base = self._base_of(path)
        item = {"name": args.get("name") or self.prefabs[path]["name"], "primitiveType": base.get("mesh"),
                "x": args.get("x", 0), "y": args.get("y", 0), "z": args.get("z", 0)}
        obj = self._create(agent, item, parent)
        obj.components.append(["Rigidbody", None])
        obj.prefab = {"source": path, "mass": None}
        if base.get("label"):
            eid, iid = self._new_ids()
            label = Obj(eid, iid, "Label", obj)
            label.pos = (0.0, 0.7, 0.0)
            obj.children.append(label)
            self.objects[eid] = label
        return {"success": True, "name": obj.name, "entityId": obj.eid, "instanceId": obj.iid, "path": self.path(obj)}

    def _skill_prefab_set_property(self, agent, args):
        path = args["prefabPath"]
        if path not in self.prefabs:
            raise _fail("TARGET_NOT_FOUND", "Prefab not found: %s" % path)
        if args["componentType"].lower() != "rigidbody" or args["propertyName"] not in ("mass", "m_Mass"):
            raise _fail("PROPERTY_NOT_FOUND", "%s.%s is not modelled" % (args["componentType"], args["propertyName"]))
        self._set_prefab_mass(agent, path, float(args["value"]))
        return {"success": True, "prefabPath": path, "gameObject": self.prefabs[path]["name"],
                "component": "Rigidbody", "property": "m_Mass", "valueSet": _num(self.prefab_mass(path))}

    def _skill_prefab_set_serialized_property(self, agent, args):
        if args["propertyPath"] != "m_Mass":
            raise _fail("PROPERTY_NOT_FOUND", "%s is not modelled" % args["propertyPath"])
        return self._skill_prefab_set_property(agent, dict(args, propertyName="m_Mass"))

    def _skill_prefab_apply(self, agent, args):
        obj = self.find(args)
        link = getattr(obj, "prefab", None)
        if not link:
            raise _fail("SEMANTIC_INVALID", "GameObject is not a prefab instance")
        if link["mass"] is not None:
            self._set_prefab_mass(agent, link["source"], link["mass"])
            link["mass"] = None
        return {"success": True, "appliedTo": link["source"]}

    def _skill_prefab_unpack(self, agent, args):
        obj = self.find(args)
        obj.mass = self.mass_of(obj)
        obj.prefab = None
        return {"success": True, "unpacked": obj.name}

    def _skill_prefab_find_instances(self, agent, args):
        path = args["prefabPath"]
        if path not in self.prefabs:
            raise _fail("TARGET_NOT_FOUND", "Prefab not found: %s" % path)
        include = bool(args.get("includeInactive"))
        rows = [{"name": o.name, "path": self.path(o), "entityId": o.eid, "instanceId": o.iid,
                 "active": self.active_in_hierarchy(o), "nested": False}
                for o in self.walk() if getattr(o, "prefab", None) and o.prefab["source"] == path
                and (include or self.active_in_hierarchy(o))]
        page = rows[:int(args.get("limit", 50))]
        return {"success": True, "prefabPath": path, "count": len(page), "totalMatched": len(rows),
                "truncated": len(rows) > len(page), "instances": page}

    # ------------------------------------------------------------ duplicate (no prefab link on the copy)

    def _clone(self, obj, parent, name):
        eid, iid = self._new_ids()
        copy = Obj(eid, iid, name, parent)
        for field in ("active", "pos", "rot", "scale", "mesh", "tag", "layer"):
            setattr(copy, field, getattr(obj, field))
        copy.components = [list(c) for c in obj.components]
        copy.materials = list(obj.materials)
        copy.mass = self.mass_of(obj)
        copy.prefab = None
        self.objects[eid] = copy
        for child in obj.children:
            copy.children.append(self._clone(child, copy, child.name))
        return copy

    def _skill_gameobject_duplicate(self, agent, args):
        obj = self.find(args)
        copy = self._clone(obj, obj.parent, obj.name + "_Copy")
        self._siblings(copy).append(copy)
        self._change(agent, self._identity(copy, kind="gameobject_created"))
        return {"success": True, "originalName": obj.name, "copyName": copy.name, "copyEntityId": copy.eid,
                "copyInstanceId": copy.iid, "copyPath": self.path(copy),
                "copyParentPath": self.path(copy.parent) if copy.parent else None}

# Producer:Betsy
