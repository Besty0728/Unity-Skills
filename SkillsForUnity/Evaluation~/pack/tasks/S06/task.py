"""S06: a new material saved under a Windows-style folder path and given to one of two cubes (ops, basic).

Fixture: R/Display with DisplayCube and ShelfCube (seeded order), both using the material A/Shared/PanelBase.mat.
"""
from usk_eval import verifylib as V
from usk_eval.task_api import PublicTask, address

GRAY = (0.5, 0.5, 0.5, 1.0)


def _color_property(ctx):
    name = ctx.env.get("colorProperty")
    if not name:
        raise ValueError("the environment snapshot has no colorProperty")
    return name


class S06(PublicTask):
    id = "S06"
    track = "ops"
    tier = "basic"
    case_version = 2
    minutes = 7
    requires = {"pipeline": ["URP", "BuiltIn"], "mode": "bypass", "dedicated": False, "packages": []}

    def build(self, ctx):
        c, rng = ctx.c, ctx.rng
        base = ctx.asset("Shared", "PanelBase.mat")
        c.write("asset_create_folder", folderPath=ctx.asset("Shared"))
        c.write("material_create", name="PanelBase", savePath=base)
        c.write("material_set_color", path=base, propertyName=_color_property(ctx), r=GRAY[0], g=GRAY[1], b=GRAY[2],
                a=GRAY[3])
        display = V.object_id(c.write("gameobject_create", name="Display", **address(ctx.root, "parent")))
        names = ["DisplayCube", "ShelfCube"]
        rng.shuffle(names)
        cubes = {}
        for index, name in enumerate(names):
            cubes[name] = V.object_id(c.write("gameobject_create", name=name, primitiveType="Cube", x=index * 2 - 1,
                                              y=0.5, z=0, **address(display, "parent")))
            c.write("material_assign", materialPath=base, **address(cubes[name]))
        return {"displayCube": cubes["DisplayCube"], "displayCubePath": ctx.path("Display", "DisplayCube"),
                "shelfCube": cubes["ShelfCube"], "shelfCubePath": ctx.path("Display", "ShelfCube"), "base": base}

    def snapshot(self, ctx):
        p, h = ctx.probe, ctx.handles
        return {"data": {"base": V.material_state(p, h["base"]),
                         "shelfCube": V.go_state(p, h["shelfCube"]),
                         "shelfCubeMaterial": (p.material(iid=h["shelfCube"]) or {}).get("materialPath")},
                "digests": {}}

    def prompt_vars(self, ctx):
        h = ctx.handles
        return {"dir": ctx.asset("Materials", "Nested").replace("/", "\\"), "display": h["displayCubePath"],
                "shelf": h["shelfCubePath"], "base": h["base"]}

    def answer_schema(self):
        return {
            "type": "object",
            "required": ["materialPath", "colorProperty", "readback"],
            "properties": {
                "materialPath": {"type": "string", "desc": {"zh-CN": "新材质的资产路径（用正斜杠）",
                                                            "en": "asset path of the new material (forward slashes)"}},
                "colorProperty": {"type": "string", "desc": {"zh-CN": "写入主颜色的着色器属性名",
                                                             "en": "shader property the main colour was written to"}},
                "readback": {"type": "array", "items": {"type": "number", "desc": {
                    "zh-CN": "写入后读回的主颜色 r、g、b、a", "en": "main colour read back after writing: r, g, b, a"}}},
            },
        }


TASK = S06()

# Producer:Betsy
