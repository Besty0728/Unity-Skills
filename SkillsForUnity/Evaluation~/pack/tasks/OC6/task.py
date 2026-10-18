"""OC6 fixture: four loose gate hierarchies, pose records and three probe-anchor links."""
import hashlib
import os

from usk_eval import verifylib as V
from usk_eval.task_api import PublicTask

BOOKKEEPING = ("m_ObjectHideFlags", "m_CorrespondingSourceObject", "m_PrefabInstance", "m_PrefabAsset",
               "m_GameObject", "m_Father", "m_Children", "m_RootOrder", "m_LocalEulerAnglesHint")


def _read(ctx, skill, **args):
    try:
        result = ctx.read(skill, **args)
        return result if isinstance(result, dict) and not result.get("error") else {}
    except Exception:
        return {}


def _number(value):
    return round(float(value), 4) + 0.0


def _properties(rows):
    out = {}
    for row in rows:
        path = row.get("propertyPath") or ""
        if any(path == key or path.startswith(key + ".") for key in BOOKKEEPING):
            continue
        kind, value = row.get("propertyType"), row.get("value")
        if kind in ("Vector2", "Vector3", "Vector4", "Quaternion", "Color", "Rect", "Bounds"):
            value = [_number(v) for v in V.floats(value)]
        elif kind in ("Float", "Integer", "ArraySize", "LayerMask", "Enum"):
            nums = V.floats(value)
            value = _number(nums[0]) if len(nums) == 1 else value
        elif kind == "Boolean":
            value = str(value).lower() in ("true", "1")
        out[path] = {"value": value, "kind": kind}
        if "referenceState" in row:
            out[path]["state"] = row["referenceState"]
    return out


def _scene_row(ctx, node):
    info = ctx.probe.go(node) or {}
    parts = []
    for comp in ctx.probe.components(node):
        kind = comp.get("fullType") or comp.get("type")
        values = _read(ctx, "component_get_serialized_properties", componentType=kind, includeHidden=True,
                       includeChildren=True, limit=4096, **V.address(node)).get("properties") or []
        if not values or len(values) >= 4096:
            raise ValueError("Incomplete component properties: " + str(kind))
        parts.append({"type": kind, "enabled": comp.get("enabled"), "props": _properties(values)})
    return {"id": V.object_id(info), "path": info.get("path"), "parent": info.get("parentPath"),
            "name": info.get("name"), "active": info.get("isActive"), "tag": info.get("tag"),
            "layer": 0 if info.get("layer") == "Default" else info.get("layer"),
            "children": [V.object_id(c) for c in info.get("children") or []], "parts": parts,
            "overrides": _read(ctx, "prefab_get_overrides", includeDetails=True, limit=4096, **V.address(node))}


def _masked(row, omitted, fields):
    out = dict(row)
    out["children"] = [ident for ident in row["children"] if str(ident) not in omitted]
    out["parts"] = []
    for comp in row["parts"]:
        props = {k: v for k, v in comp["props"].items() if (str(row["id"]), comp["type"], k) not in fields}
        out["parts"].append(dict(comp, props=props))
    return out


class OC6(PublicTask):
    id = "OC6"
    track = "ops"
    tier = "challenge"
    case_version = 1
    minutes = 25
    requires = {"pipeline": "URP", "mode": "bypass", "dedicated": True, "packages": []}

    def omit_fields(self, row, omitted, fields):
        return _masked(row, omitted, fields)

    def read_scene(self, ctx):
        return [_scene_row(ctx, row["id"]) for row in V.structure(ctx.probe, [ctx.probe.go_path(ctx.R)])]

    def read_asset(self, ctx, path):
        tree = _read(ctx, "prefab_get_hierarchy", prefabPath=path, limit=4096)
        if tree.get("truncated") or not tree.get("nodes"):
            return []
        out = []
        for node in tree["nodes"]:
            rel = node.get("gameObjectPath")
            comps = _read(ctx, "prefab_list_components", prefabPath=path, gameObjectPath=rel).get("components")
            if not comps:
                return []
            parts, local = [], {}
            for comp in comps:
                kind = comp.get("componentType")
                props = _read(ctx, "prefab_get_serialized_properties", prefabPath=path, gameObjectPath=rel,
                              componentType=kind, componentIndex=comp.get("componentIndex", 0), includeHidden=True,
                              includeChildren=True, limit=4096).get("properties") or []
                if not props or len(props) >= 4096:
                    return []
                parts.append({"type": kind, "enabled": comp.get("enabled"), "props": _properties(props)})
                if kind == "UnityEngine.Transform":
                    raw = {r.get("propertyPath"): r.get("value") for r in props}
                    local = {key: V.floats(raw.get(field)) for key, field in
                             (("position", "m_LocalPosition"), ("rotation", "m_LocalRotation"), ("scale", "m_LocalScale"))}
            out.append({"path": rel, "name": node.get("gameObject"), "active": node.get("activeSelf"),
                        "tag": node.get("tag"), "layer": node.get("layer"), "index": node.get("siblingIndex"), "parts": parts,
                        "assetType": node.get("prefabAssetType"), "local": local})
        return out

    def read_files(self, ctx):
        root = os.path.join(ctx.project_path, *ctx.A.split("/"))
        out = {}
        if not os.path.isdir(root):
            return out
        for base, dirs, files in os.walk(root):
            dirs.sort()
            rel = os.path.relpath(base, ctx.project_path).replace(os.sep, "/")
            out[rel] = None
            for name in sorted(files):
                path = rel + "/" + name
                if path in (ctx.scene, ctx.scene + ".meta"):
                    continue
                with open(os.path.join(base, name), "rb") as stream:
                    out[path] = hashlib.sha256(stream.read()).hexdigest()
        if os.path.isfile(root + ".meta"):
            with open(root + ".meta", "rb") as stream:
                out[ctx.A + ".meta"] = hashlib.sha256(stream.read()).hexdigest()
        return out

    def read_metadata(self, ctx, paths):
        out = {}
        for path in paths:
            if path.endswith(".meta") or path == ctx.scene:
                continue
            info = _read(ctx, "asset_get_info", assetPath=path)
            out[path] = {"guid": info.get("guid"), "labels": sorted(info.get("labels") or [])}
        return out

    def build(self, ctx):
        c, rng = ctx.c, ctx.rng
        words = ("Birch", "Copper", "Elm", "Flint", "Grove", "Jade", "Larch", "Moss", "Oak", "Pine")
        serials = iter(rng.sample(range(100, 9999), 200))

        def name():
            return rng.choice(words) + "_" + str(next(serials))

        def vec():
            return [rng.choice((-3, -2, -1, 1, 2, 3)) * rng.choice((0.25, 0.5, 1.0)) for _ in range(3)]

        def make(label, parent, pos=None, scale=None, yaw=0):
            pos, scale = pos or vec(), scale or [1, 1, 1]
            return V.object_id(c.write("gameobject_create", name=label, x=pos[0], y=pos[1], z=pos[2],
                rotY=yaw, scaleX=scale[0], scaleY=scale[1], scaleZ=scale[2], **V.address(parent, "parent")))

        def collider(ident, size):
            c.write("component_add", componentType="UnityEngine.BoxCollider", **V.address(ident))
            c.write("component_set_serialized_property", componentType="UnityEngine.BoxCollider", propertyPath="m_Size",
                    value=",".join(str(v) for v in size), **V.address(ident))

        folders = []
        for _ in range(rng.randint(2, 4)):
            folder = ctx.asset(name())
            c.write("asset_create_folder", folderPath=folder)
            if rng.choice((False, True)):
                folder += "/" + name()
                c.write("asset_create_folder", folderPath=folder)
            folders.append(folder)
        file_path = rng.choice(folders) + "/" + name() + ".prefab"
        stage = make(name(), ctx.root, yaw=rng.choice((15, 30, 45, 60, -25)))
        hinge_name = "Hinge_" + str(next(serials))
        shape = [(hinge_name, "", vec(), None)]
        for _ in range(rng.randint(1, 3)):
            label = name()
            shape.append((label, "", vec(), [rng.choice((0.5, 0.75, 1.25, 1.5)) for _ in range(3)]))
            if rng.choice((False, True)):
                shape.append((name(), label, vec(), None))
        rng.shuffle(shape)
        shape.sort(key=lambda row: bool(row[1]))
        size = [rng.choice((0.5, 0.75, 1.25, 1.5)) for _ in range(3)]
        bindings, hinges, objects = [], {}, []
        for _ in range(4):
            parent = stage
            for _ in range(rng.randint(1, 3)):
                parent = make(name(), parent)
            c.write("gameobject_set_transform", rotY=rng.choice((25, 55, 85, 125, -40)),
                    scaleX=rng.choice((1.5, 2.0, 2.5)), scaleY=rng.choice((0.5, 0.75)),
                    scaleZ=rng.choice((1.0, 1.25)), **V.address(parent))
            for _ in range(rng.randint(1, 3)):
                collider(make(name(), parent), [rng.choice((0.25, 0.5, 0.75)) for _ in range(3)])
            gate_name = "Gate_" + str(next(serials))
            gate = make(gate_name, parent, scale=[rng.choice((0.75, 1, 1.25, 1.5)) for _ in range(3)])
            collider(gate, size)
            nodes = {"": gate}
            for label, above, pos, dims in shape:
                nodes[label] = make(label, nodes[above], pos=pos)
                if dims:
                    collider(nodes[label], dims)
            hinges[str(gate)] = nodes[hinge_name]
            path = rng.choice(folders) + "/" + gate_name + ".prefab"
            c.write("prefab_create", savePath=path, **V.address(gate))
            c.write("prefab_unpack", completely=True, **V.address(gate))
            info = ctx.probe.go(gate)
            props = ctx.read("component_get_properties", componentType="UnityEngine.Transform", **V.address(gate))
            scale = V.floats(next((r.get("value") for r in props.get("properties") or [] if r.get("name") == "lossyScale"), None))
            if len(scale) != 3 or any(v <= 0 for v in scale):
                raise ValueError("Transform lossyScale is unavailable")
            pos, rot = V.floats(info["position"]), V.floats(info["rotation"])
            c.write("prefab_set_transform", prefabPath=path, posX=pos[0], posY=pos[1], posZ=pos[2],
                    rotX=rot[0], rotY=rot[1], rotZ=rot[2], scaleX=scale[0], scaleY=scale[1], scaleZ=scale[2])
            bindings.append([gate, path])
            objects.append(gate)
            for _ in range(rng.randint(1, 3)):
                collider(make(name(), parent), [rng.choice((0.25, 0.5, 0.75)) for _ in range(3)])
        sample = V.object_id(c.write("prefab_instantiate", prefabPath=rng.choice(bindings)[1], name=name(),
                                    **V.address(stage, "parent")))
        c.write("component_set_serialized_property", componentType="UnityEngine.BoxCollider", propertyPath="m_Size",
                value=",".join(str(rng.choice((2.0, 2.5, 3.0))) for _ in range(3)), **V.address(sample))
        edges = []
        for gate in rng.sample(objects, 3):
            lever = make(name(), stage)
            c.write("component_add", componentType="UnityEngine.MeshRenderer", **V.address(lever))
            c.write("component_set_property", componentType="UnityEngine.MeshRenderer", propertyName="probeAnchor",
                    referenceEntityId=str(hinges[str(gate)]), **V.address(lever))
            rows = ctx.read("component_get_serialized_properties", componentType="UnityEngine.MeshRenderer",
                            includeHidden=True, includeChildren=True, limit=4096, **V.address(lever))["properties"]
            fields = [row for row in rows if row.get("referenceState") == "assigned" and
                      V.same_id(ctx.probe.owner(row.get("value")), hinges[str(gate)])]
            if len(fields) != 1 or (V.parse_ref(fields[0].get("value")) or (None, None))[1] != "UnityEngine.Transform":
                raise ValueError("MeshRenderer probe anchor did not round-trip as a Transform")
            edges.append([lever, fields[0]["propertyPath"], hinges[str(gate)]])
        rng.shuffle(bindings)
        rng.shuffle(edges)
        return {"bindings": bindings, "file": file_path, "edges": edges}

    def snapshot(self, ctx):
        scene = self.read_scene(ctx)
        roots = {str(ident) for ident, path in ctx.handles["bindings"]}
        paths = [row["path"] for row in scene if str(row["id"]) in roots]
        fields = {(str(ident), "UnityEngine.MeshRenderer", path) for ident, path, obj in ctx.handles["edges"]}
        bystanders = [row for row in scene if not any(V.under(row["path"], path) for path in paths)]
        files = self.read_files(ctx)
        return {"data": {"nodes": [[row["id"], row["path"], row["children"]] for row in scene],
                         "paths": sorted(files)},
                "digests": {"scene": {str(row["id"]): V.canonical_digest(_masked(row, roots, fields))
                                      for row in bystanders},
                            "files": {path: digest for path, digest in files.items() if digest is not None},
                            "metadata": {path: V.canonical_digest(info)
                                         for path, info in self.read_metadata(ctx, sorted(files)).items()}}}

    def prompt_vars(self, ctx):
        scene = {str(row["id"]): row for row in self.read_scene(ctx)}
        pairs = [scene[str(ident)]["path"] + " => " + path for ident, path in ctx.handles["bindings"]]
        edges = [scene[str(ident)]["path"] + " :: UnityEngine.MeshRenderer." + field + " => " +
                 scene[str(obj)]["path"] for ident, field, obj in ctx.handles["edges"]]
        return {"locations": "; ".join(pairs), "links": "; ".join(edges), "prefab": ctx.handles["file"]}

    def answer_schema(self):
        return {"type": "object", "required": ["prefabPath", "inspectedOwners"], "properties": {
            "prefabPath": {"type": "string", "desc": {"zh-CN": "新预制体的资产路径", "en": "asset path of the new prefab"}},
            "inspectedOwners": {"type": "array", "items": {"type": "string", "desc": {
                "zh-CN": "已检查的引用所属对象的完整层级路径", "en": "full hierarchy path of each inspected reference owner"}}}}}


TASK = OC6()

# Producer:Betsy
