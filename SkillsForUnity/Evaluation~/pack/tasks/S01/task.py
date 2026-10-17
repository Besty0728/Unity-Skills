"""S01: a Cube named Crate under an offset parent, with a new material saved as an asset (ops, basic).

Fixture: R/Props at a seeded position; R/Props/Barrel, a sphere using the material A/Common/CrateMaterial.mat.
"""
from usk_eval import verifylib as V
from usk_eval.task_api import PublicTask, address

TINT = (0.3, 0.6, 0.2, 1.0)


def _color_property(ctx):
    name = ctx.env.get("colorProperty")
    if not name:
        raise ValueError("the environment snapshot has no colorProperty")
    return name


class S01(PublicTask):
    id = "S01"
    track = "ops"
    tier = "basic"
    case_version = 2
    minutes = 10
    requires = {"pipeline": ["URP", "BuiltIn"], "mode": "bypass", "dedicated": False, "packages": []}

    def build(self, ctx):
        c, rng = ctx.c, ctx.rng
        x, z = rng.choice([3, 4, -4, 5]), rng.choice([-2, 2, -3])
        props = V.object_id(c.write("gameobject_create", name="Props", x=x, y=0, z=z, **address(ctx.root, "parent")))
        common = ctx.asset("Common", "CrateMaterial.mat")
        c.write("material_create", name="CrateMaterial", savePath=common)
        c.write("material_set_color", path=common, propertyName=_color_property(ctx), r=TINT[0], g=TINT[1], b=TINT[2],
                a=TINT[3])
        barrel = V.object_id(c.write("gameobject_create", name="Barrel", primitiveType="Sphere", x=1.5, y=0.5, z=0,
                                     **address(props, "parent")))
        c.write("material_assign", materialPath=common, **address(barrel))
        return {"props": props, "propsPath": ctx.path("Props"), "barrel": barrel, "commonMaterial": common}

    def snapshot(self, ctx):
        p, h = ctx.probe, ctx.handles
        return {"data": {"propsChildren": [V.object_id(child) for child in p.children(h["props"])],
                         "commonMaterial": V.material_state(p, h["commonMaterial"]),
                         "barrel": V.go_state(p, h["barrel"]),
                         "barrelMaterial": (p.material(iid=h["barrel"]) or {}).get("materialPath")},
                "digests": {}}

    def prompt_vars(self, ctx):
        return {"props": ctx.handles["propsPath"], "mat": ctx.asset("Materials", "CrateMaterial.mat")}

    def answer_schema(self):
        return {
            "type": "object",
            "required": ["objectPath", "objectInstanceId", "materialPath"],
            "properties": {
                "objectPath": {"type": "string", "desc": {"zh-CN": "Crate 的完整层级路径",
                                                          "en": "full hierarchy path of Crate"}},
                "objectInstanceId": {"type": "integer", "desc": {
                    "zh-CN": "Crate 的 instanceId（Unity 6000.4+ 填 entityId）",
                    "en": "instanceId of Crate (entityId on Unity 6000.4+)"}},
                "materialPath": {"type": "string", "desc": {"zh-CN": "Crate 所用材质的资产路径",
                                                            "en": "asset path of the material Crate uses"}},
            },
        }


TASK = S01()

# Producer:Betsy
