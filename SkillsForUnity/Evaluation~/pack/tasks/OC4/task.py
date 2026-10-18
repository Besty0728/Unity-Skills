"""OC4: 240 MeshRenderers with material arrays beneath seeded branches.

The scene contains Transform and MeshRenderer components. Materials are assets under the run folder.
"""
import json
import os

from usk_eval import verifylib as V
from usk_eval.task_api import PublicTask

COUNT = 240
CHUNK = 50
ARRAY = "m_Materials"


def _items(ctx, skill, rows):
    out = []
    for start in range(0, len(rows), CHUNK):
        part = rows[start:start + CHUNK]
        result = ctx.c.write(skill, items=json.dumps(part))
        if result.get("failCount") or result.get("successCount") != len(part):
            raise ValueError("incomplete fixture batch: " + skill)
        out.extend(result.get("results") or [])
    return out


def _reads(ctx, steps):
    out = []
    for start in range(0, len(steps), CHUNK):
        part = steps[start:start + CHUNK]
        result = ctx.c.batch(part)
        rows = result.get("results") or []
        if len(rows) != len(part) or any(row.get("status") != "success" for row in rows):
            raise ValueError("incomplete fixture read batch")
        out.extend(row["result"] for row in rows)
    return out


def _fields(result, omit=False):
    if not isinstance(result, dict) or not isinstance(result.get("properties"), list):
        return None
    return sorted([[row.get("propertyPath"), row.get("value"), row.get("referenceState")]
                   for row in result["properties"]
                   if not (omit and (row.get("propertyPath") == ARRAY or
                                     str(row.get("propertyPath")).startswith(ARRAY + ".")))], key=lambda row: row[0])


def _token(row):
    state = row.get("referenceState")
    ref = V.parse_ref(row.get("value"))
    ident = V.object_id(str(ref[0])) if ref else None
    if state == "assigned" and ident is not None:
        return [state, str(ident)]
    if state in ("none", "missing"):
        return [state, None]
    raise ValueError("unreadable material slot")


def _asset_state(ctx, path):
    docs = V.uyaml.load(os.path.join(ctx.project_path, *path.split("/")))
    return {"info": ctx.probe.asset(path), "properties": ctx.probe.material(path=path),
            "yaml": [[doc.class_id, doc.file_id, doc.stripped, doc.type_name, doc.body] for doc in docs],
            "guid": V.uyaml.meta_guid(os.path.join(ctx.project_path, *path.split("/")))}


class OC4(PublicTask):
    id = "OC4"
    track = "ops"
    tier = "challenge"
    case_version = 1
    minutes = 30
    requires = {"pipeline": "URP", "mode": "bypass", "dedicated": True, "packages": []}

    def build(self, ctx):
        c, rng = ctx.c, ctx.rng
        used = set()

        def label():
            while True:
                text = "N_%010x" % rng.getrandbits(40)
                if text not in used:
                    used.add(text)
                    return text

        def placement(name, parent):
            return {"name": name, "parentPath": parent,
                    "x": rng.randint(-18, 18), "y": rng.randint(-4, 6), "z": rng.randint(-18, 18),
                    "rotY": rng.choice((0, 90, 180, 270))}

        folders = [ctx.asset(label()) for _ in range(rng.randint(2, 4))]
        for folder in folders:
            c.write("asset_create_folder", folderPath=folder)
        shaders = (["Standard"] * rng.randint(2, 3) +
                   ["Universal Render Pipeline/Lit"] * rng.randint(2, 3))
        rng.shuffle(shaders)
        specs = []
        for shader in shaders:
            name = label()
            specs.append({"name": name, "shaderName": shader, "savePath": rng.choice(folders) + "/" + name + ".mat"})
        name = label()
        material = rng.choice(folders) + "/" + name + ".mat"
        specs.append({"name": name, "shaderName": "Universal Render Pipeline/Lit", "savePath": material})
        name = label()
        removed = rng.choice(folders) + "/" + name + ".mat"
        specs.append({"name": name, "shaderName": rng.choice(shaders), "savePath": removed})
        rng.shuffle(specs)
        made = _items(ctx, "material_create_batch", specs)
        for spec, result in zip(specs, made):
            if result.get("shader") != spec["shaderName"]:
                raise ValueError("fixture shader is unavailable: " + spec["shaderName"])
        first = [row["savePath"] for row in specs if row["shaderName"] == "Standard" and row["savePath"] != removed]
        second = [row["savePath"] for row in specs if row["shaderName"] != "Standard" and row["savePath"] != removed]
        rng.shuffle(first)
        rng.shuffle(second)

        name = label()
        group = V.object_id(c.write("gameobject_create", **placement(name, ctx.R)))
        group_path = ctx.path(name)
        nodes = [ctx.root, group]
        plans, homes, tops = [], [], []
        for _ in range(rng.randint(5, 8)):
            parent = group_path
            chain = []
            for depth in range(rng.randint(2, 4)):
                name = label()
                plans.append(placement(name, parent))
                parent = parent + "/" + name
                if depth == 0:
                    tops.append(parent)
                chain.append(parent)
            homes.append(chain)
        rows = _items(ctx, "gameobject_create_batch", plans)
        nodes.extend(V.object_id(row) for row in rows)
        places = [rng.choice(chain) for chain in homes]
        places.extend(rng.choice(rng.choice(homes)) for _ in range(COUNT - len(places)))
        rng.shuffle(places)
        rows = _items(ctx, "gameobject_create_batch", [placement(label(), home) for home in places])
        renderers = [V.object_id(row) for row in rows]
        paths = [row["path"] for row in rows]
        nodes.extend(renderers)
        _items(ctx, "component_add_batch", [{"path": path, "componentType": "MeshRenderer"} for path in paths])
        values = []
        for index, path in enumerate(paths):
            slots = [first[index % len(first)], removed, None, second[index % len(second)]]
            slots.extend(rng.choice(first + second + [removed, None]) for _ in range(rng.randint(0, 2)))
            rng.shuffle(slots)
            values.append({"path": path, "componentType": "MeshRenderer",
                           "propertyPath": ARRAY + ".Array.size", "value": str(len(slots))})
            for slot, asset in enumerate(slots):
                item = {"path": path, "componentType": "MeshRenderer",
                        "propertyPath": ARRAY + ".Array.data[%d]" % slot}
                if asset is None:
                    item["value"] = "null"
                else:
                    item["assetPath"] = asset
                    item["objectType"] = "Material"
                values.append(item)
        _items(ctx, "component_set_serialized_property_batch", values)
        off = rng.sample(tops, rng.randint(1, len(tops) - 1))
        off += rng.sample(paths, rng.randint(12, 38))
        steps = [{"skill": "gameobject_set_active", "args": {"path": path, "active": False}} for path in off]
        for start in range(0, len(steps), CHUNK):
            result = c.batch(steps[start:start + CHUNK])
            if result.get("status") != "completed":
                raise ValueError("incomplete fixture activation batch")
        c.write("scene_save")
        c.write("asset_delete", assetPath=removed)
        materials = [row["savePath"] for row in specs if row["savePath"] != removed]
        rng.shuffle(materials)
        return {"group": group, "groupPath": group_path, "nodes": nodes, "renderers": renderers,
                "materials": materials, "material": material}

    def snapshot(self, ctx):
        h, digests, bindings = ctx.handles, {}, {}
        steps = [{"skill": "gameobject_get_info", "args": V.address(ref)} for ref in h["nodes"]]
        for ref, info in zip(h["nodes"], _reads(ctx, steps)):
            digests["object/" + str(ref)] = V.canonical_digest(info)
        steps = [{"skill": "component_get_serialized_properties",
                  "args": dict(V.address(ref), componentType="Transform", includeChildren=True,
                               includeHidden=True, limit=5000)} for ref in h["nodes"]]
        for ref, result in zip(h["nodes"], _reads(ctx, steps)):
            digests["Transform/" + str(ref)] = V.canonical_digest(_fields(result))
        steps = [{"skill": "component_get_serialized_properties",
                  "args": dict(V.address(ref), componentType="MeshRenderer", includeChildren=True,
                               includeHidden=True, limit=5000)} for ref in h["renderers"]]
        names = {os.path.splitext(os.path.basename(path))[0]: path for path in h["materials"]}
        for ref, result in zip(h["renderers"], _reads(ctx, steps)):
            props = {row["propertyPath"]: row for row in result["properties"]}
            size = V.as_int((props.get(ARRAY + ".Array.size") or {}).get("value"))
            if size is None or size < 4:
                raise ValueError("incomplete fixture materials array")
            digests["size/" + str(ref)] = V.canonical_digest(size)
            digests["MeshRenderer/" + str(ref)] = V.canonical_digest(_fields(result, True))
            seen = set()
            for index in range(size):
                row = props.get(ARRAY + ".Array.data[%d]" % index) or {}
                token = _token(row)
                seen.add(token[0])
                digests["slot/%s/%d" % (ref, index)] = V.canonical_digest(token)
                parsed = V.parse_ref(row.get("value"))
                if parsed:
                    path = names.get(parsed[2])
                    if not path:
                        raise ValueError("material asset cannot be located")
                    bindings[path] = token[1]
            if not {"none", "missing", "assigned"}.issubset(seen):
                raise ValueError("incomplete fixture slot states")
        if set(bindings) != set(h["materials"]):
            raise ValueError("incomplete fixture material identities")
        for path in h["materials"]:
            digests["shader/" + path] = V.canonical_digest((ctx.probe.material(path=path) or {}).get("shader"))
            digests["asset/" + path] = V.canonical_digest(_asset_state(ctx, path))
        return {"data": {"assets": [{"path": path, "id": bindings[path]} for path in h["materials"]]},
                "digests": digests}

    def prompt_vars(self, ctx):
        return {"root": ctx.handles["groupPath"], "material": ctx.handles["material"]}

    def answer_schema(self):
        descriptions = [
            ("renderers", "审计的原始 renderer 总数", "number of original renderers audited"),
            ("slots", "原始材质槽总数", "total original material slots"),
            ("standardSlots", "原始使用 Standard 着色器的已赋值槽数", "original assigned slots using the Standard shader"),
            ("missingSlots", "原始引用丢失的槽数", "original missing-reference slots"),
            ("emptySlots", "原始为空的槽数", "original empty slots"),
            ("otherSlots", "原始已赋值且着色器非 Standard 的槽数", "original assigned slots with a shader other than Standard"),
        ]
        return {"type": "object", "required": [row[0] for row in descriptions],
                "properties": {name: {"type": "integer", "desc": {"zh-CN": zh, "en": en}}
                               for name, zh, en in descriptions}}

    def teardown(self, ctx):
        return None


TASK = OC4()

# Producer:Betsy
