"""T10: materials from several folders moved into one folder per initial letter, GUIDs kept (ops, standard).

Fixture: A/Incoming/{Batch01, Batch02, Vendor/Old, Misc} holds 35 materials and Misc/Grip.physicMaterial;
A/Materials/B/Bolt.mat and A/Elsewhere/Brick.mat exist; R/Props holds 12 cubes that use some of these materials,
three of them with two slots.
"""
import json
import os

from usk_eval import verifylib as V
from usk_eval.task_api import PublicTask, address

POOL = ["Asphalt", "Bark", "Basalt", "Canvas", "Chrome", "Clay", "Copper", "Denim", "Ember", "Felt", "Flint",
        "Glass", "Granite", "Gravel", "Iron", "Ivory", "Jade", "Leather", "Linen", "Marble", "Nickel", "Oak",
        "Onyx", "Paper", "Pine", "Quartz", "Rubber", "Sand", "Slate", "Steel", "Teak", "Velvet", "Walnut", "Wool",
        "Zinc", "moss", "rust", "cedar", "lava", "pearl", "fern", "ash", "umber"]
# folder, fixed names, how many seeded pool names it gets (29 in total)
FOLDERS = (("Batch01", ("Brick",), 9), ("Batch02", ("Brick", "Tile"), 8), ("Vendor/Old", ("Bolt", "Tile"), 6),
           ("Misc", ("Tile",), 6))
RENDERERS = 12


def _color_property(ctx):
    name = ctx.env.get("colorProperty")
    if not name:
        raise ValueError("the environment snapshot has no colorProperty")
    return name


def _meta_guid(path):
    try:
        return V.uyaml.meta_guid(path)
    except OSError:
        return None


def _tree(ctx):
    """({asset path: guid} of every file under A, [folder paths under A]), without .meta files and the run scene."""
    root = os.path.join(ctx.project_path, *ctx.A.split("/"))
    files, folders = {}, []
    for base, dirs, names in os.walk(root):
        dirs.sort()
        rel = os.path.relpath(base, ctx.project_path).replace(os.sep, "/")
        if rel != ctx.A:
            folders.append(rel)
        for name in sorted(names):
            path = rel + "/" + name
            if name.endswith(".meta") or name.startswith(".") or path == ctx.scene:
                continue
            files[path] = _meta_guid(os.path.join(base, name)) or ctx.probe.guid(path)
    return files, sorted(folders)


def _slots(probe, ref):
    ser = probe.ser(ref, "MeshRenderer") or {}
    size = V.as_int(ser.get("m_Materials.Array.size")) or 0
    return [ser.get("m_Materials.Array.data[%d]" % index) for index in range(size)]


class T10(PublicTask):
    id = "T10"
    track = "ops"
    tier = "standard"
    case_version = 2
    minutes = 20
    requires = {"pipeline": "URP", "mode": "bypass", "dedicated": True, "packages": []}

    def build(self, ctx):
        c, rng = ctx.c, ctx.rng
        incoming, materials = ctx.asset("Incoming"), ctx.asset("Materials")
        names = rng.sample(POOL, 29)
        sources, cursor = [], 0
        for folder, fixed, count in FOLDERS:
            batch = list(fixed) + names[cursor:cursor + count]
            cursor += count
            rng.shuffle(batch)
            sources += ["%s/%s/%s.mat" % (incoming, folder, name) for name in batch]
        folders = [incoming] + ["%s/%s" % (incoming, name) for name in ("Batch01", "Batch02", "Vendor", "Vendor/Old",
                                                                         "Misc")]
        folders += [materials, materials + "/B", ctx.asset("Elsewhere")]
        c.write("asset_create_folder_batch", items=json.dumps([{"folderPath": folder} for folder in folders]))
        bolt, outside = materials + "/B/Bolt.mat", ctx.asset("Elsewhere", "Brick.mat")
        made = [{"name": os.path.basename(path)[:-4], "savePath": path} for path in sources + [bolt, outside]]
        c.write("material_create_batch", items=json.dumps(made))
        c.write("material_set_colors_batch", propertyName=_color_property(ctx), items=json.dumps([
            {"path": item["savePath"], "r": round(rng.random(), 3), "g": round(rng.random(), 3),
             "b": round(rng.random(), 3)} for item in made]))
        c.write("physics_create_material", name="Grip", savePath="%s/Misc" % incoming, dynamicFriction=0.7,
                staticFriction=0.8)

        # 12 renderers over 10 incoming materials, three with a second slot; the picks include both Bricks, one Tile
        # and the Bolt, one renderer uses the material outside Incoming and one the Bolt already under Materials
        by_name = {}
        for path in sources:
            by_name.setdefault(os.path.basename(path), []).append(path)
        must = by_name["Brick.mat"] + by_name["Tile.mat"][:1] + by_name["Bolt.mat"]
        rest = [path for path in sources if path not in must]
        refs = must + rng.sample(rest, 10 - len(must))
        rng.shuffle(refs)
        slots = [[refs[i]] for i in range(9)] + [[outside, refs[9]], [bolt, refs[0]], [refs[3], refs[7]]]
        props = V.object_id(c.write("gameobject_create", name="Props", **address(ctx.root, "parent")))
        made_cubes = c.write("gameobject_create_batch", items=json.dumps([
            dict({"name": "Prop_%02d" % index, "primitiveType": "Cube", "x": index * 1.5}, **address(props, "parent"))
            for index in range(RENDERERS)]))
        renderers = [V.object_id(row) for row in made_cubes.get("results") or []]
        at = [ctx.path("Props", "Prop_%02d" % index) for index in range(RENDERERS)]
        c.write("material_assign_batch", items=json.dumps([{"path": at[index], "materialPath": slot[0]}
                                                         for index, slot in enumerate(slots)]))
        second = []
        for index, slot in enumerate(slots):
            if len(slot) > 1:
                second += [{"path": at[index], "componentType": "MeshRenderer",
                            "propertyPath": "m_Materials.Array.size", "value": "2"},
                           {"path": at[index], "componentType": "MeshRenderer",
                            "propertyPath": "m_Materials.Array.data[1]", "assetPath": slot[1],
                            "objectType": "Material"}]
        c.write("component_set_serialized_property_batch", items=json.dumps(second))
        return {"incoming": incoming, "materials": materials, "renderers": renderers}

    def snapshot(self, ctx):
        p, h = ctx.probe, ctx.handles
        files, folders = _tree(ctx)
        states = {path: V.material_state(p, path) for path in sorted(files)
                  if path.endswith(".mat") and not V.under(path, h["incoming"])}
        slots = {str(V.object_id(ref)): _slots(p, ref) for ref in h["renderers"]}
        return {"data": {"files": files, "folders": folders, "states": states, "slots": slots}, "digests": {}}

    def prompt_vars(self, ctx):
        return {"inc": ctx.handles["incoming"], "mat": ctx.handles["materials"]}

    def answer_schema(self):
        return {
            "type": "object",
            "required": ["moves"],
            "properties": {
                "moves": {"type": "array", "items": {"type": "object", "properties": {
                    "from": {"type": "string", "desc": {"zh-CN": "移动前的资产路径", "en": "asset path before the move"}},
                    "to": {"type": "string", "desc": {"zh-CN": "移动后的资产路径", "en": "asset path after the move"}},
                }}},
            },
        }


TASK = T10()

# Producer:Betsy
