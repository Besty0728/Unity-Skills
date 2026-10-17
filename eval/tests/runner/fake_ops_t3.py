"""In-memory editor for the T3 offline tests: pack_fake_editor plus what the physics bridge touches.

Added to the base model: Rigidbody (mass, kinematic), HingeJoint (axis, connected body, hidden until includeHidden) and
collider components with per-component state; physic material assets with the plugin's unique-path rule; the layer
names of the tag manager and the layer collision matrix; physics_set_material (writes to the first collider);
gameobject_set_layer_batch; component_remove. Component ids in references are the owning GameObject's id here, which is
how the plugin resolves them (a component id names its GameObject).
"""
import json
import os
import re

from .pack_fake_editor import _fail, _g9, _numbers
from .pack_offline import OpsFakeEditor

TAG_MANAGER = "ProjectSettings/TagManager.asset"
COLLIDERS = ("BoxCollider", "SphereCollider", "CapsuleCollider", "MeshCollider")
KNOWN = ("Rigidbody", "HingeJoint") + COLLIDERS
CANON = {name.lower(): name for name in KNOWN}
SINGLE = ("Rigidbody",)
FRICTION_KEYS = ("dynamicFriction", "staticFriction", "bounciness")


class Row(list):
    """A component row [type, enabled] that carries its state."""
    state = None


def canonical(kind):
    return CANON.get(str(kind).lower(), str(kind))


def _initial_state(kind):
    if kind == "Rigidbody":
        return {"mass": 1.0, "kinematic": False}
    if kind == "HingeJoint":
        return {"axis": (1.0, 0.0, 0.0), "body": None}
    if kind in COLLIDERS:
        return {"material": None}
    return {}


class FakeEditorT3(OpsFakeEditor):
    extra_reads = frozenset(("physics_get_layer_collision", "editor_get_layers",
                             "scriptableobject_get_serialized_properties"))

    def __init__(self, *args, **kwargs):
        super(FakeEditorT3, self).__init__(*args, **kwargs)
        self.layer_names = {0: "Default", 1: "TransparentFX", 2: "Ignore Raycast", 4: "Water", 5: "UI"}
        self.collision = {}
        self.physic = {}

    # ------------------------------------------------------------ components

    def _row(self, kind, enabled):
        row = Row([kind, enabled])
        row.state = _initial_state(kind)
        return row

    def _create(self, agent, item, parent):
        obj = super(FakeEditorT3, self)._create(agent, item, parent)
        obj.components = [self._adopt(row) for row in obj.components]
        return obj

    def _adopt(self, row):
        if isinstance(row, Row):
            return row
        out = Row(row)
        out.state = _initial_state(row[0])
        return out

    def _first(self, obj, kind):
        return next((row for row in obj.components if row[0] == kind), None)

    def _need(self, obj, kind):
        row = self._first(obj, kind)
        if row is None:
            raise _fail("COMPONENT_NOT_FOUND", "Component not found: %s" % kind)
        return row

    def _skill_component_add(self, agent, args):
        kind = canonical(args.get("componentType"))
        if kind not in KNOWN:
            return super(FakeEditorT3, self)._skill_component_add(agent, args)
        obj = self.find(args)
        if kind in SINGLE and self._first(obj, kind) is not None:
            return {"warning": "Component %s already exists on %s" % (kind, obj.name), "gameObject": obj.name,
                    "entityId": obj.eid, "instanceId": obj.iid, "component": kind,
                    "fullTypeName": "UnityEngine." + kind, "alreadyPresent": True}
        if kind == "HingeJoint" and self._first(obj, "Rigidbody") is None:
            obj.components.append(self._row("Rigidbody", None))
        obj.components.append(self._row(kind, True if kind in COLLIDERS else None))
        self._change(agent, self._identity(obj, kind="components_changed"))
        return {"success": True, "gameObject": obj.name, "entityId": obj.eid, "instanceId": obj.iid,
                "component": kind, "fullTypeName": "UnityEngine." + kind}

    def _skill_component_remove(self, agent, args):
        kind = canonical(args.get("componentType"))
        obj = self.find(args)
        rows = [row for row in obj.components if row[0] == kind]
        if not rows:
            raise _fail("COMPONENT_NOT_FOUND", "Component not found on %s: %s" % (obj.name, kind))
        index = int(args.get("componentIndex") or 0)
        if not 0 <= index < len(rows):
            raise _fail("INDEX_OUT_OF_RANGE", "Component index %d out of range" % index)
        if kind == "Rigidbody" and self._first(obj, "HingeJoint") is not None:
            raise _fail("SEMANTIC_INVALID", "Cannot remove Rigidbody - required by: HingeJoint")
        obj.components.pop(next(i for i, row in enumerate(obj.components) if row is rows[index]))
        self._change(agent, self._identity(obj, kind="components_changed"))
        return {"success": True, "gameObject": obj.name, "removed": kind,
                "remainingOfType": len(rows) - 1}

    # ------------------------------------------------------------ property writes

    def _object_for_reference(self, args):
        """The plugin's TryFindObject: an instance id, else a path, else a name; a given locator never falls through."""
        iid = int(args.get("referenceInstanceId") or 0)
        if iid:
            found = next((o for o in self.objects.values() if o.iid == iid and o.iid != 0), None)
            if found is None:
                raise _fail("TARGET_NOT_FOUND", "Scene reference not found for instanceId %d" % iid)
            return found
        if args.get("referencePath"):
            wanted = args["referencePath"].lower()
            found = next((o for o in self.walk() if self.path(o).lower() == wanted), None)
            if found is None:
                raise _fail("TARGET_NOT_FOUND", "GameObject not found: %s" % args["referencePath"])
            return found
        if args.get("referenceName"):
            found = next((o for o in self.walk() if o.name == args["referenceName"]), None)
            if found is None:
                raise _fail("TARGET_NOT_FOUND", "GameObject not found: %s" % args["referenceName"])
            return found
        raise _fail("TARGET_REQUIRED", "Scene reference not found: pass one of entityId, instanceId, path or name")

    def _body_of(self, args):
        found = self._object_for_reference(args)
        wanted = canonical(args.get("objectType") or "Rigidbody")
        if wanted != "Rigidbody" or self._first(found, "Rigidbody") is None:
            raise _fail("COMPONENT_NOT_FOUND", "Component '%s' not found on '%s'" % (wanted, found.name))
        return found

    @staticmethod
    def _flag(value):
        return str(value).strip().lower() in ("true", "1", "yes", "on")

    def _record(self, agent, obj, kind, prop, value):
        self._change(agent, self._identity(obj, kind="property_changed", targetType=kind, property=prop,
                                           previousValue="", value=str(value)))

    def _skill_component_set_property(self, agent, args):
        kind = canonical(args.get("componentType"))
        if kind not in KNOWN:
            return super(FakeEditorT3, self)._skill_component_set_property(agent, args)
        obj = self.find(args)
        row = self._need(obj, kind)
        prop = args["propertyName"]
        if kind == "Rigidbody" and prop == "mass":
            row.state["mass"] = float(args["value"])
        elif kind == "Rigidbody" and prop == "isKinematic":
            row.state["kinematic"] = self._flag(args["value"])
        elif kind == "HingeJoint" and prop == "axis":
            row.state["axis"] = _numbers(args["value"], 3)
        elif kind == "HingeJoint" and prop == "connectedBody":
            row.state["body"] = self._body_of(args).eid
        elif kind in COLLIDERS and prop in ("sharedMaterial", "material") and args.get("assetPath"):
            if args["assetPath"] not in self.physic:
                raise _fail("ASSET_NOT_FOUND", "Asset not found or type mismatch: '%s'" % args["assetPath"])
            row.state["material"] = args["assetPath"]
        else:
            raise _fail("PROPERTY_NOT_FOUND", "Property/field not found: %s" % prop)
        self._record(agent, obj, kind, prop, args.get("value") or args.get("assetPath") or "")
        return {"success": True, "gameObject": obj.name, "component": kind, "property": prop}

    def _skill_component_set_serialized_property(self, agent, args):
        kind = canonical(args.get("componentType"))
        if kind not in KNOWN:
            return super(FakeEditorT3, self)._skill_component_set_serialized_property(agent, args)
        obj = self.find(args)
        row = self._need(obj, kind)
        path = args["propertyPath"]
        if kind == "Rigidbody" and path == "m_Mass":
            row.state["mass"] = float(args["value"])
        elif kind == "Rigidbody" and path == "m_IsKinematic":
            row.state["kinematic"] = self._flag(args["value"])
        elif kind == "HingeJoint" and path == "m_Axis":
            row.state["axis"] = _numbers(args["value"], 3)
        elif kind == "HingeJoint" and path == "m_ConnectedBody":
            clears = not any(args.get(key) for key in ("referenceInstanceId", "referencePath", "referenceName")) and \
                str(args.get("value") or "null").lower() == "null"
            row.state["body"] = None if clears else self._body_of(args).eid
        elif kind in COLLIDERS and path == "m_Material":
            if args.get("assetPath"):
                if args["assetPath"] not in self.physic:
                    raise _fail("ASSET_NOT_FOUND", "Asset not found: %s" % args["assetPath"])
                row.state["material"] = args["assetPath"]
            else:
                row.state["material"] = None
        else:
            raise _fail("PROPERTY_NOT_FOUND", "Serialized property not found: %s" % path)
        self._record(agent, obj, kind, path, args.get("value") or args.get("assetPath") or "")
        return {"success": True, "gameObject": obj.name, "component": kind, "propertyPath": path}

    # ------------------------------------------------------------ property reads

    def _ref_text(self, eid, kind, name):
        return "%s:UnityEngine.%s:%s" % (eid, kind, name)

    def _serialized(self, obj, kind, hidden=False):
        name = canonical(kind)
        if name not in KNOWN:
            return super(FakeEditorT3, self)._serialized(obj, kind)
        state = self._need(obj, name).state
        if name == "Rigidbody":
            return {"m_Mass": "%.9g" % state["mass"], "m_UseGravity": "True",
                    "m_IsKinematic": "True" if state["kinematic"] else "False"}
        if name == "HingeJoint":
            out = {"m_Axis": _g9(state["axis"])}
            if hidden:
                body = self.objects.get(state["body"]) if state["body"] is not None else None
                out["m_ConnectedBody"] = self._ref_text(body.eid, "Rigidbody", body.name) if body else "null"
            return out
        path = state["material"]
        mat = "null" if path is None else self._ref_text(int(self._guid(path)[:8], 16), "PhysicMaterial",
                                                          os.path.splitext(os.path.basename(path))[0])
        return {"m_IsTrigger": "False", "m_Material": mat}

    def _skill_component_get_serialized_properties(self, agent, args):
        obj = self.find(args)
        values = self._serialized(obj, args["componentType"], bool(args.get("includeHidden")))
        rows = []
        for key, value in values.items():
            row = {"propertyPath": key, "value": value}
            if key in ("m_ConnectedBody", "m_Material"):
                row["referenceState"] = "none" if value == "null" else "assigned"
            rows.append(row)
        return {"success": True, "gameObject": obj.name, "component": args["componentType"], "properties": rows}

    # ------------------------------------------------------------ physics, layers, project settings

    @staticmethod
    def _layer_index(args, key):
        value = args.get(key)
        if isinstance(value, bool) or not isinstance(value, int) or not 0 <= value <= 31:
            raise _fail("INVALID_LAYER", "%s must be a layer index from 0 to 31" % key)
        return value

    def _skill_physics_get_layer_collision(self, agent, args):
        first, second = self._layer_index(args, "layer1"), self._layer_index(args, "layer2")
        return {"layer1": first, "layer2": second,
                "collisionEnabled": self.collision.get(tuple(sorted((first, second))), True)}

    def _skill_physics_set_layer_collision(self, agent, args):
        first, second = self._layer_index(args, "layer1"), self._layer_index(args, "layer2")
        enabled = bool(args.get("enableCollision", True))
        self.collision[tuple(sorted((first, second)))] = enabled
        return {"success": True, "layer1": first, "layer2": second, "collisionEnabled": enabled}

    def _skill_editor_get_layers(self, agent, args):
        return {"layers": [{"index": index, "name": name} for index, name in sorted(self.layer_names.items())
                           if name]}

    def _skill_scriptableobject_set_serialized_property(self, agent, args):
        path, prop, value = args["assetPath"], args["propertyPath"], args.get("value")
        if path == TAG_MANAGER:
            match = re.match(r"^layers\.Array\.data\[(\d+)\]$", prop)
            if not match or not 8 <= int(match.group(1)) <= 31:
                raise _fail("PROPERTY_NOT_FOUND", "Serialized property not found: %s" % prop)
            if value:
                self.layer_names[int(match.group(1))] = value
            else:
                self.layer_names.pop(int(match.group(1)), None)
            return {"success": True, "assetPath": path, "propertyPath": prop, "valueSet": value or ""}
        if path in self.physic:
            if prop not in FRICTION_KEYS:
                raise _fail("PROPERTY_NOT_FOUND", "Serialized property not found: %s" % prop)
            self.physic[path][prop] = float(value)
            self._change(agent, {"kind": "property_changed", "targetType": "PhysicMaterial", "property": prop,
                                 "previousValue": "", "value": str(value), "assetPath": path})
            return {"success": True, "assetPath": path, "propertyPath": prop, "valueSet": str(value)}
        raise _fail("ASSET_NOT_FOUND", "Asset not found: %s" % path)

    def _skill_scriptableobject_get_serialized_properties(self, agent, args):
        path = args["assetPath"]
        if path == TAG_MANAGER:
            return {"success": True, "path": path, "typeName": "TagManager",
                    "properties": [{"propertyPath": "layers.Array.data[%d]" % index,
                                    "value": self.layer_names.get(index, "")} for index in range(32)]}
        if path in self.physic:
            values = self.physic[path]
            return {"success": True, "path": path, "typeName": "PhysicMaterial",
                    "properties": [{"propertyPath": key, "value": "%.9g" % values[key]} for key in FRICTION_KEYS]}
        raise _fail("ASSET_NOT_FOUND", "Asset not found: %s" % path)

    def _unique_path(self, path):
        stem, ext = os.path.splitext(path)
        candidate, index = path, 0
        while candidate in self.known or os.path.exists(self.disk(candidate)):
            index += 1
            candidate = "%s %d%s" % (stem, index, ext)
        return candidate

    def _skill_physics_create_material(self, agent, args):
        name = args.get("name") or "New PhysicMaterial"
        folder = (args.get("savePath") or "Assets").replace("\\", "/").rstrip("/")
        self._ensure_folder(folder, agent)
        path = self._unique_path("%s/%s.physicMaterial" % (folder, name))
        self.physic[path] = {"dynamicFriction": float(args.get("dynamicFriction", 0.6)),
                             "staticFriction": float(args.get("staticFriction", 0.6)),
                             "bounciness": float(args.get("bounciness", 0.0))}
        self._write_file(path, "%%YAML 1.1\nPhysicMaterial: %s\n" % name)
        self._imported(agent, [path])
        return {"success": True, "path": path}

    def _skill_physics_set_material(self, agent, args):
        obj = self.find(args)
        path = args["materialPath"]
        if path not in self.physic:
            raise _fail("ASSET_NOT_FOUND", "PhysicMaterial not found: %s" % path)
        row = next((r for r in obj.components if r[0] in COLLIDERS), None)
        if row is None:
            raise _fail("NO_COLLIDER", "No Collider on %s" % obj.name)
        row.state["material"] = path
        self._record(agent, obj, row[0], "m_Material", path)
        return {"success": True, "gameObject": obj.name, "material": path, "collider": row[0]}

    def _skill_gameobject_set_layer_batch(self, agent, args):
        items = json.loads(args["items"])
        jobs = []
        for item in items:
            obj = self.find(item)
            if item.get("layer") not in self.layer_names.values():
                raise _fail("LAYER_NOT_FOUND", "Layer not found: %s" % item.get("layer"))
            jobs.append((obj, item["layer"], bool(item.get("recursive"))))
        for obj, layer, recursive in jobs:
            for target in (self.walk([obj]) if recursive else [obj]):
                before, target.layer = target.layer, layer
                self._change(agent, self._identity(target, kind="property_changed", targetType="GameObject",
                                                   property="m_Layer", previousValue=before, value=layer))
        return {"success": True, "totalItems": len(jobs), "successCount": len(jobs), "failCount": 0,
                "results": [{"target": obj.name, "success": True, "layer": layer} for obj, layer, _r in jobs]}

    # ------------------------------------------------------------ assets

    def _skill_asset_get_info(self, agent, args):
        path = args["assetPath"]
        if path in self.physic and os.path.exists(self.disk(path)):
            return {"path": path, "name": os.path.splitext(os.path.basename(path))[0], "type": "PhysicMaterial",
                    "guid": self._guid(path), "labels": []}
        return super(FakeEditorT3, self)._skill_asset_get_info(agent, args)

    def _skill_asset_delete(self, agent, args):
        result = super(FakeEditorT3, self)._skill_asset_delete(agent, args)
        path = args["assetPath"]
        for material in [m for m in self.physic if m == path or m.startswith(path + "/")]:
            del self.physic[material]
        return result

# Producer:Betsy
