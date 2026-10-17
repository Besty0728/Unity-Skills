"""T4: give one of two cubes that share their materials a new body material and a glowing second material (ops, standard).

Fixture: R/Showroom with the cubes Car and CarTwin; both use [A/Shared/BodyPaint.mat, A/Shared/Trim.mat] in their two
material slots. A/Archive/GlowOld.mat is an emissive material that no object uses.
"""
from usk_eval import verifylib as V
from usk_eval.task_api import PublicTask, address

BODY_PAINT = (0.2, 0.2, 0.2, 1.0)
TRIM = (0.9, 0.9, 0.85, 1.0)
TOKEN_LETTERS = "ABCDEFGHJKMNPQRSTUVWXYZ"


def _color_property(ctx):
    name = ctx.env.get("colorProperty")
    if not name:
        raise ValueError("the environment snapshot has no colorProperty")
    return name


def _used_materials(ctx, object_name):
    """[(material name, asset path)] of the materials that renderers on scene objects with this name use."""
    out = []
    for group in ctx.read("scene_materials").get("shaders") or []:
        for material in group.get("materials") or []:
            if object_name in (material.get("users") or []):
                out.append((material.get("name"), material.get("path") or None))
    return out


def _slots(ctx, ref, object_name):
    """Slot count, material names and asset paths of a cube's renderer. Slot 0 is read from the renderer; a later
    slot is the one used material with that name."""
    p = ctx.probe
    ser = p.ser(ref, "MeshRenderer") or {}
    size = V.as_int(ser.get("m_Materials.Array.size")) or 0
    names = []
    for index in range(size):
        parsed = V.parse_ref(ser.get("m_Materials.Array.data[%d]" % index))
        names.append(parsed[2] if parsed else None)
    used = _used_materials(ctx, object_name)
    paths = []
    for index, slot_name in enumerate(names):
        if index == 0:
            paths.append((p.material(iid=ref) or {}).get("materialPath") or None)
            continue
        found = sorted({path for name, path in used if name == slot_name and path})
        paths.append(found[0] if len(found) == 1 else None)
    return {"size": size, "names": names, "paths": paths}


def _set_slots(ctx, ref, paths):
    c = ctx.c
    c.write("component_set_serialized_property", componentType="MeshRenderer", propertyPath="m_Materials.Array.size",
            value=str(len(paths)), **address(ref))
    for index, path in enumerate(paths):
        c.write("component_set_serialized_property", componentType="MeshRenderer",
                propertyPath="m_Materials.Array.data[%d]" % index, assetPath=path, **address(ref))


class T4(PublicTask):
    id = "T4"
    track = "ops"
    tier = "standard"
    case_version = 2
    minutes = 15
    requires = {"pipeline": "URP", "mode": "bypass", "dedicated": True, "packages": []}

    def build(self, ctx):
        c, rng = ctx.c, ctx.rng
        token = "".join(rng.choice(TOKEN_LETTERS) for _ in range(3))
        prop = _color_property(ctx)
        body, trim = ctx.asset("Shared", "BodyPaint.mat"), ctx.asset("Shared", "Trim.mat")
        old = ctx.asset("Archive", "GlowOld.mat")
        for name, path, color in (("BodyPaint", body, BODY_PAINT), ("Trim", trim, TRIM), ("GlowOld", old, None)):
            c.write("material_create", name=name, savePath=path)
            if color:
                c.write("material_set_color", path=path, propertyName=prop, r=color[0], g=color[1], b=color[2],
                        a=color[3])
        c.write("material_set_emission", path=old, r=1, g=0.5, b=0, intensity=1.5)
        showroom = V.object_id(c.write("gameobject_create", name="Showroom", **address(ctx.root, "parent")))
        cars = {}
        for name, x in (("Car", -1.5), ("CarTwin", 1.5)):
            cars[name] = V.object_id(c.write("gameobject_create", name=name, primitiveType="Cube", x=x, y=0.5, z=0,
                                             **address(showroom, "parent")))
            _set_slots(ctx, cars[name], [body, trim])
        return {"car": cars["Car"], "carTwin": cars["CarTwin"], "showroomPath": ctx.path("Showroom"),
                "bodyPaint": body, "trim": trim, "oldGlow": old, "glowName": "Glow_%s" % token,
                "glowPath": ctx.asset("Materials", "Glow_%s.mat" % token)}

    def snapshot(self, ctx):
        p, h = ctx.probe, ctx.handles
        twin = _slots(ctx, h["carTwin"], "CarTwin")
        if twin["size"] != 2 or not all(twin["paths"]):
            raise ValueError("the material slots of CarTwin could not be resolved to asset paths: %r" % (twin,))
        return {"data": {"materials": {key: V.material_state(p, h[key]) for key in ("bodyPaint", "trim", "oldGlow")},
                         "car": V.go_state(p, h["car"]),
                         "carTwin": {"go": V.go_state(p, h["carTwin"]), "slots": twin}},
                "digests": {}}

    def prompt_vars(self, ctx):
        h = ctx.handles
        return {"room": h["showroomPath"], "body": h["bodyPaint"], "trim": h["trim"], "glow": h["glowName"],
                "glowpath": h["glowPath"]}

    def answer_schema(self):
        return {
            "type": "object",
            "required": ["bodyMaterialPath", "glowMaterialPath"],
            "properties": {
                "bodyMaterialPath": {"type": "string", "desc": {"zh-CN": "Car 槽 0 所用材质的资产路径",
                                                                "en": "asset path of the material in Car's slot 0"}},
                "glowMaterialPath": {"type": "string", "desc": {"zh-CN": "Car 槽 1 所用材质的资产路径",
                                                                "en": "asset path of the material in Car's slot 1"}},
            },
        }


TASK = T4()

# Producer:Betsy
