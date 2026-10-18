"""OC2: prefab variants with a numeric Light field, local scales and an inherited collider.

The prefab hierarchy, asset folders, scene containers, names and numeric values are seeded.
"""
import copy
import json
import os

from usk_eval import verifylib as V
from usk_eval.task_api import PublicTask

COLLIDERS = ("BoxCollider", "SphereCollider", "CapsuleCollider")


def _read(ctx, skill, **args):
    try:
        result = ctx.read(skill, **args)
        return result if isinstance(result, dict) and not result.get("error") else {}
    except Exception:
        return {}


def _docs(ctx, path):
    try:
        return V.uyaml.load(os.path.join(ctx.project_path, *path.split("/")))
    except (OSError, ValueError, TypeError):
        return []


def _number(value):
    values = V.floats(value)
    return values[0] if len(values) == 1 else None


def _content(docs, light_id, go_id, transform_id, kind):
    owners = {go_id}
    owners.update(d.file_id for d in docs if d.type_name == "GameObject" and d.stripped and
                  str(d.get("m_CorrespondingSourceObject", "fileID")) == str(go_id))
    omitted = {d.file_id for d in docs if d.type_name == kind and
               int(d.get("m_GameObject", "fileID", default=0)) in owners}
    result = []
    for doc in docs:
        if doc.file_id in omitted or doc.stripped:
            continue
        body = copy.deepcopy(doc.body)
        if doc.file_id == light_id:
            body.pop("m_Intensity", None)
        if doc.file_id == transform_id:
            body.pop("m_LocalScale", None)
        if doc.type_name == "GameObject":
            body["m_Component"] = [item for item in body.get("m_Component") or []
                                   if int(item.get("component", {}).get("fileID", 0)) not in omitted]
        if doc.type_name == "PrefabInstance":
            mod = body.get("m_Modification") or {}
            properties = []
            for entry in mod.get("m_Modifications") or []:
                field = entry.get("propertyPath", "")
                ident = str(entry.get("target", {}).get("fileID"))
                if field == "m_Name" or (ident == str(light_id) and field == "m_Intensity") or \
                        (ident == str(transform_id) and field.startswith("m_LocalScale")):
                    continue
                properties.append(entry)
            added = [entry for entry in mod.get("m_AddedComponents") or []
                     if int(entry.get("addedObject", {}).get("fileID", 0)) not in omitted]
            body = {"source": body.get("m_SourcePrefab"),
                    "properties": sorted(properties, key=V.canonical_digest),
                    "removed": mod.get("m_RemovedComponents") or [],
                    "added": added, "objects": mod.get("m_AddedGameObjects") or [],
                    "removedObjects": mod.get("m_RemovedGameObjects") or []}
            result.append([doc.type_name, body])
        else:
            result.append([doc.type_name, doc.file_id, body])
    return sorted(result, key=V.canonical_digest)


class OC2(PublicTask):
    id = "OC2"
    track = "ops"
    tier = "challenge"
    case_version = 1
    minutes = 20
    requires = {"pipeline": "URP", "mode": "bypass", "dedicated": True, "packages": []}

    def build(self, ctx):
        c, rng = ctx.c, ctx.rng
        words = ("Cedar", "Quartz", "Willow", "Harbor", "Aster", "Birch", "Slate", "Vale")
        serials = iter(rng.sample(range(1000, 9999), 100))

        def label():
            return "%s_%s" % (rng.choice(words), next(serials))

        def make(parent, **args):
            return V.object_id(c.write("gameobject_create", name=label(), **dict(V.address(parent, "parent"), **args)))

        folders = []
        for unused in range(rng.randint(2, 3)):
            folder = ctx.asset(label())
            c.write("asset_create_folder", folderPath=folder)
            if rng.choice((False, True)):
                folder += "/" + label()
                c.write("asset_create_folder", folderPath=folder)
            folders.append(folder)
        paths = [rng.choice(folders) + "/" + label() + ".prefab" for unused in range(rng.randint(5, 7))]
        initial = rng.choice((1.25, 2.5, 3.25))
        speed = rng.choice((4.25, 5.5, 6.75))
        kept = rng.choice((8.0, 9.25, 10.5))
        ranges = [speed, kept, rng.choice((17.5, 19.0, 22.25))]
        ranges += [rng.uniform(40 + i * 8, 44 + i * 8) for i in range(len(paths) - 3)]
        kind = rng.choice(COLLIDERS)
        root = make(ctx.root)
        parent = root
        peer = None
        if rng.choice((False, True)):
            peer = make(parent)
        for unused in range(rng.randint(1, 3)):
            parent = make(parent, x=rng.choice((-0.75, 0.25, 1.5)), y=rng.choice((0.5, 1.25)),
                          z=rng.choice((-1.0, 0.75)))
        lamp = make(parent, scaleX=rng.choice((0.75, 1.25)), scaleY=rng.choice((1.5, 1.75)),
                    scaleZ=rng.choice((0.5, 1.25)))
        if peer is None:
            peer = make(parent)
        c.write("component_add", componentType=kind, **V.address(peer))
        c.write("component_add", componentType="Light", **V.address(lamp))
        for field, value in (("m_Intensity", initial), ("m_Range", speed)):
            c.write("component_set_serialized_property", componentType="Light", propertyPath=field, value=str(value),
                    **V.address(lamp))
        relative = ctx.probe.go(lamp)["path"][len(ctx.probe.go(root)["path"]) + 1:]
        c.write("prefab_create", savePath=paths[0], **V.address(root))
        c.write("gameobject_delete", **V.address(root))
        order = list(range(1, len(paths)))
        rng.shuffle(order)
        for i in order:
            c.write("prefab_create_variant", sourcePrefabPath=paths[0], variantPath=paths[i])
            steps = [{"skill": "prefab_set_serialized_property", "args": {
                "gameObjectPath": relative, "componentType": "Light", "propertyPath": "m_Range",
                "value": str(ranges[i])}}]
            value = kept if i == 1 else rng.choice((12.5, 14.75, 16.25)) if i == 2 else rng.uniform(23, 32)
            steps.append({"skill": "prefab_set_serialized_property", "args": {
                "gameObjectPath": relative, "componentType": "Light", "propertyPath": "m_Intensity",
                "value": str(value)}})
            if i == 2:
                steps.append({"skill": "prefab_set_transform", "args": {
                    "gameObjectPath": relative, "scaleX": rng.choice((2.5, 3.25)),
                    "scaleY": rng.choice((2.75, 4.0)), "scaleZ": rng.choice((3.5, 4.25))}})
            c.write("prefab_edit_batch", prefabPath=paths[i], steps=json.dumps(steps))
        containers = [make(ctx.root), make(ctx.root)]
        containers.append(make(rng.choice(containers)))
        assets = paths + [paths[0]] + [rng.choice(paths) for unused in range(rng.randint(0, 2))]
        rng.shuffle(assets)
        items = [{"prefabPath": path, "name": label(), "x": rng.choice((-6, -3, 2, 5)),
                  "y": rng.choice((0, 0.5, 1)), "z": rng.choice((-4, -1, 3, 6)),
                  **V.address(rng.choice(containers), "parent")} for path in assets]
        res = c.write("prefab_instantiate_batch", items=json.dumps(items))
        objects = [V.object_id(row) for row in res["results"]]
        i = assets.index(paths[0])
        instance_path = ctx.probe.go(objects[i])["path"]
        color = ",".join(str(rng.choice(values)) for values in ((0.2, 0.35), (0.6, 0.8), (0.15, 0.4), (1.0,)))
        c.write("component_set_serialized_property", path=instance_path + "/" + relative,
                componentType="Light", propertyPath="m_Color", value=color)
        rng.shuffle(paths)
        rng.shuffle(objects)
        return {"assets": paths, "objects": objects}

    def observe(self, ctx):
        rows = []
        for path in ctx.handles.get("assets", []):
            hierarchy = _read(ctx, "prefab_get_hierarchy", prefabPath=path, limit=512)
            nodes = hierarchy.get("nodes") or []
            entries = []
            for node in nodes:
                relative = node.get("gameObjectPath")
                comps = _read(ctx, "prefab_list_components", prefabPath=path, gameObjectPath=relative).get("components") or []
                entries.append([relative, comps])
            lights = [relative for relative, comps in entries
                      if any(item.get("componentType") == "UnityEngine.Light" for item in comps)]
            relative = lights[0] if len(lights) == 1 else None
            props = _read(ctx, "prefab_get_serialized_properties", prefabPath=path, gameObjectPath=relative,
                          componentType="Light", limit=4096).get("properties") or []
            values = {item.get("propertyPath"): item.get("value") for item in props}
            rows.append({"path": path, "nodes": nodes, "entries": entries, "node": relative,
                         "speed": _number(values.get("m_Intensity")), "range": _number(values.get("m_Range")),
                         "docs": _docs(ctx, path)})
        return rows

    def snapshot(self, ctx):
        rows = self.observe(ctx)
        regular = [row for row in rows if any(d.type_name == "Light" and not d.stripped for d in row["docs"])]
        data = {}
        if len(regular) == 1:
            base = regular[0]
            light = next(d for d in base["docs"] if d.type_name == "Light" and not d.stripped)
            go_id = int(light.get("m_GameObject", "fileID"))
            transform = next(d for d in base["docs"] if d.type_name == "Transform" and
                             int(d.get("m_GameObject", "fileID", default=0)) == go_id)
            kind = next(d.type_name for d in base["docs"] if d.type_name in COLLIDERS and
                        int(d.get("m_GameObject", "fileID", default=0)) != go_id)
            for row in rows:
                path, docs = row["path"], row["docs"]
                try:
                    guid = V.uyaml.meta_guid(os.path.join(ctx.project_path, *path.split("/")))
                except OSError:
                    guid = None
                tree = copy.deepcopy(row["nodes"])
                for node in tree:
                    if node.get("gameObjectPath") == base["node"]:
                        node.pop("localScale", None)
                data[path] = {
                    "content": V.canonical_digest(_content(docs, light.file_id, go_id, transform.file_id, kind)),
                    "file": V.canonical_digest([[d.type_name, d.file_id, d.stripped, d.body] for d in docs]),
                    "scale": V.canonical_digest([entry for doc in docs if doc.type_name == "PrefabInstance"
                        for entry in doc.get("m_Modification", "m_Modifications", default=[]) or []
                        if str(entry.get("target", {}).get("fileID")) == str(transform.file_id)
                        and entry.get("propertyPath", "").startswith("m_LocalScale")]),
                    "pose": V.canonical_digest([[n.get("gameObjectPath"), n.get("localScale")] for n in row["nodes"]]),
                    "tree": V.canonical_digest(tree), "guid": V.canonical_digest(guid)}
            scene = V.structure(ctx.probe, [ctx.root])
            for node in scene:
                ident = V.object_id(node["id"])
                trs = V.local_trs(ctx.probe, ident)
                if any(c[0] == "Light" for c in node["components"]):
                    node["components"] = [c for c in node["components"] if c[0] != kind]
                    trs.pop("scale", None)
                    props = dict(ctx.probe.ser(ident, "Light") or {})
                    props.pop("m_Intensity", None)
                    node["properties"] = props
                node["trs"] = trs
            own = []
            for ref in ctx.handles.get("objects", []):
                info = ctx.probe.go(ref) or {}
                values = _read(ctx, "prefab_get_overrides", path=info.get("path"), includeDetails=True, limit=4096)
                own.append([V.object_id(ref), values])
            data["scene"] = V.canonical_digest([scene, own])
        entries = []
        for folder, dirs, files in os.walk(os.path.join(ctx.project_path, *ctx.A.split("/"))):
            for name in dirs + files:
                path = os.path.relpath(os.path.join(folder, name), ctx.project_path).replace(os.sep, "/")
                if path not in (ctx.scene, ctx.scene + ".meta"):
                    entries.append(path)
        data["listing"] = V.canonical_digest(sorted(entries))
        return {"data": {}, "digests": data}

    def prompt_vars(self, ctx):
        rows = self.observe(ctx)
        base = next(row for row in rows if any(d.type_name == "Light" and not d.stripped for d in row["docs"]))
        variants = sorted([row for row in rows if row is not base], key=lambda row: row["range"])
        fast, tank = variants[:2]
        node = next(n for n in base["nodes"] if n.get("gameObjectPath") == base["node"])
        collider = next(d.type_name for d in base["docs"] if d.type_name in COLLIDERS)
        return {"base": base["path"], "fast": fast["path"], "tank": tank["path"], "node": base["node"],
                "new_speed": base["range"], "kept_speed": fast["range"], "tank_range": tank["range"],
                "scale": ", ".join(str(node["localScale"][axis]) for axis in "xyz"), "component": collider}

    def answer_schema(self):
        return {"type": "object", "required": ["checkedAssets"], "properties": {
            "checkedAssets": {"type": "array", "items": {"type": "string", "desc": {
                "en": "asset paths you inspected after editing", "zh-CN": "编辑后检查过的资产路径"}}}}}


TASK = OC2()

# Producer:Betsy
