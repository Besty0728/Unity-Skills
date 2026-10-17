"""S04: disable one Marker among several same-name siblings (ops, basic).

Fixture: R/Hall/Markers with seven sphere children in a seeded order; R/Hall/Backup with one sphere child.
"""
from usk_eval import verifylib as V
from usk_eval.task_api import PublicTask, address

# name, has an AudioSource, active
LAYOUT = (("Marker", True, True), ("Marker", False, False), ("Marker", False, True), ("Marker", False, True),
          ("Marker_Archive", True, True), ("marker", True, True))
FIRST = ("Marker", False, True)


class S04(PublicTask):
    id = "S04"
    track = "ops"
    tier = "basic"
    case_version = 2
    minutes = 5
    requires = {"pipeline": ["URP", "BuiltIn"], "mode": "bypass", "dedicated": False, "packages": []}

    def build(self, ctx):
        c, rng = ctx.c, ctx.rng

        def make(name, parent, **args):
            args.update(address(parent, "parent"))
            return V.object_id(c.write("gameobject_create", name=name, **args))

        hall = make("Hall", ctx.root)
        markers = make("Markers", hall)
        backup = make("Backup", hall, x=0, y=0, z=6)
        layout = list(LAYOUT)
        rng.shuffle(layout)
        layout.insert(0, FIRST)
        for index, (name, audio, active) in enumerate(layout):
            ref = make(name, markers, primitiveType="Sphere", x=index * 1.5, y=0.5, z=0, scaleX=0.4, scaleY=0.4,
                       scaleZ=0.4)
            if audio:
                c.write("component_add", componentType="AudioSource", **address(ref))
            if not active:
                c.write("gameobject_set_active", active=False, **address(ref))
        spare = make("Marker", backup, primitiveType="Sphere", x=0, y=0.5, z=0, scaleX=0.4, scaleY=0.4, scaleZ=0.4)
        c.write("component_add", componentType="AudioSource", **address(spare))
        return {"hall": hall, "markers": markers, "markersPath": ctx.path("Hall", "Markers"), "backup": backup}

    def snapshot(self, ctx):
        nodes, stack = [], [ctx.handles["hall"]]
        while stack:
            ref = stack.pop()
            state = V.go_state(ctx.probe, ref)
            nodes.append([V.object_id(ref), state])
            stack.extend(reversed((state or {}).get("children") or []))
        return {"data": {"nodes": nodes}, "digests": {}}

    def prompt_vars(self, ctx):
        return {"markers": ctx.handles["markersPath"]}

    def answer_schema(self):
        return {
            "type": "object",
            "required": ["disabledInstanceId", "disabledPath"],
            "properties": {
                "disabledInstanceId": {"type": "integer", "desc": {
                    "zh-CN": "被禁用对象的 instanceId（Unity 6000.4+ 填 entityId）",
                    "en": "instanceId of the disabled object (entityId on Unity 6000.4+)"}},
                "disabledPath": {"type": "string", "desc": {"zh-CN": "被禁用对象的完整层级路径",
                                                            "en": "full hierarchy path of the disabled object"}},
            },
        }


TASK = S04()

# Producer:Betsy
