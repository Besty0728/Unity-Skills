"""S03: move R/Warehouse/Loose/Lamp under R/Warehouse/Props, keeping its local values (ops, basic).

Fixture: Warehouse > Loose > {Lamp > Bulb, Rope}; Warehouse > Props (seeded yaw, scale 1.5) > {Shelf > Lamp, Crate}.
"""
from usk_eval import verifylib as V
from usk_eval.task_api import PublicTask, address

LAMP_POS = (0.5, 1.2, -0.3)
LAMP_ROT = (10.0, 20.0, 0.0)
LAMP_SCALE = 0.8
BULB_POS = (0.0, 0.4, 0.0)


class S03(PublicTask):
    id = "S03"
    track = "ops"
    tier = "basic"
    case_version = 2
    minutes = 7
    requires = {"pipeline": ["URP", "BuiltIn"], "mode": "bypass", "dedicated": False, "packages": []}

    def build(self, ctx):
        c, rng = ctx.c, ctx.rng

        def make(name, parent, **args):
            args.update(address(parent, "parent"))
            return V.object_id(c.write("gameobject_create", name=name, **args))

        yaw = rng.randint(25, 65)
        warehouse = make("Warehouse", ctx.root, x=2, y=0, z=1)
        loose = make("Loose", warehouse, x=-3, y=1, z=0)
        props = make("Props", warehouse, x=5, y=2, z=-1, rotY=yaw, scaleX=1.5, scaleY=1.5, scaleZ=1.5)
        lamp = make("Lamp", loose, x=LAMP_POS[0], y=LAMP_POS[1], z=LAMP_POS[2], rotX=LAMP_ROT[0], rotY=LAMP_ROT[1],
                    rotZ=LAMP_ROT[2], scaleX=LAMP_SCALE, scaleY=LAMP_SCALE, scaleZ=LAMP_SCALE)
        c.write("component_add", componentType="AudioSource", **address(lamp))
        c.write("component_add", componentType="BoxCollider", **address(lamp))
        c.write("component_set_enabled", componentType="BoxCollider", enabled=False, **address(lamp))
        bulb = make("Bulb", lamp, primitiveType="Sphere", x=BULB_POS[0], y=BULB_POS[1], z=BULB_POS[2],
                    scaleX=0.2, scaleY=0.2, scaleZ=0.2)
        rope = make("Rope", loose, x=0, y=2, z=0)
        shelf = make("Shelf", props, primitiveType="Cube", x=-1, y=0.5, z=0)
        crate = make("Crate", props, primitiveType="Cube", x=1, y=0.5, z=0.5)
        shelf_lamp = make("Lamp", shelf, x=0, y=0.8, z=0)
        c.write("component_add", componentType="AudioSource", **address(shelf_lamp))
        return {"warehouse": warehouse, "loose": loose, "props": props, "lamp": lamp, "bulb": bulb, "rope": rope,
                "shelf": shelf, "crate": crate, "shelfLamp": shelf_lamp,
                "lampPath": ctx.path("Warehouse", "Loose", "Lamp"), "propsPath": ctx.path("Warehouse", "Props")}

    def snapshot(self, ctx):
        p, h = ctx.probe, ctx.handles
        components = [[comp.get("type"), comp.get("enabled")] for comp in p.components(h["lamp"])]
        others = [[h[key], V.go_state(p, h[key])] for key in ("warehouse", "rope", "shelf", "crate", "shelfLamp")]
        return {"data": {"lampComponents": components,
                         "bulbLocal": V.local_trs(p, h["bulb"]),
                         "others": others,
                         "props": V.go_state(p, h["props"], with_children=False),
                         "loose": V.go_state(p, h["loose"], with_children=False),
                         "propsChildren": [V.object_id(child) for child in p.children(h["props"])],
                         "looseChildren": [V.object_id(child) for child in p.children(h["loose"])]},
                "digests": {}}

    def prompt_vars(self, ctx):
        return {"lamp": ctx.handles["lampPath"], "props": ctx.handles["propsPath"]}

    def answer_schema(self):
        def vector(zh, en):
            return {"type": "array", "items": {"type": "number", "desc": {"zh-CN": zh, "en": en}}}

        return {
            "type": "object",
            "required": ["newPath", "localPosition", "localEulerAngles", "localScale"],
            "properties": {
                "newPath": {"type": "string", "desc": {"zh-CN": "Lamp 移动后的完整层级路径",
                                                       "en": "full hierarchy path of Lamp after the move"}},
                "localPosition": vector("移动后的本地位置 x、y、z", "local position after the move: x, y, z"),
                "localEulerAngles": vector("移动后的本地欧拉角 x、y、z", "local Euler angles after the move: x, y, z"),
                "localScale": vector("移动后的本地缩放 x、y、z", "local scale after the move: x, y, z"),
            },
        }


TASK = S03()

# Producer:Betsy
