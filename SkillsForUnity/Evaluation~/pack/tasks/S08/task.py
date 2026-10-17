"""S08: inventory of R/Candidates by a name pattern and a component (ops, basic, read-only).

Fixture: R/Candidates with the subgroups Wave1, Wave1/Squad1, Wave2 and Wave2/Squad2 holding the objects listed in
ENTRIES (seeded order, seeded subgroup where none is given); R/Reserve/Enemy_09 has a BoxCollider.
"""
from usk_eval import verifylib as V
from usk_eval.task_api import PublicTask, address

PATTERN = r"^Enemy_[0-9]{2}$"
GROUPS = ("", "Wave1", "Wave1/Squad1", "Wave2", "Wave2/Squad2")
# name, primitive, collider (Hitbox/...: on a child named Hitbox), collider enabled, object active, subgroup
ENTRIES = (
    ("Enemy_01", None, "BoxCollider", True, True, None),
    ("Enemy_02", None, "BoxCollider", True, True, None),
    ("Enemy_03", None, "BoxCollider", True, True, "Wave1/Squad1"),
    ("Enemy_03", None, "BoxCollider", True, True, "Wave2/Squad2"),
    ("Enemy_10", None, "BoxCollider", False, True, None),
    ("Enemy_11", None, "BoxCollider", True, False, None),
    ("Enemy_14", None, "BoxCollider", True, True, None),
    ("Enemy_17", None, "BoxCollider", True, True, None),
    ("Enemy_06", None, "Hitbox/BoxCollider", True, True, None),
    ("Enemy_08", None, "MeshCollider", True, True, None),
    ("Enemy_12", None, None, True, True, None),
    ("Enemy_15", "Sphere", None, True, True, None),
    ("Enemy_7", None, "BoxCollider", True, True, None),
    ("Enemy_123", None, "BoxCollider", True, True, None),
    ("enemy_04", None, "BoxCollider", True, True, None),
    ("Enemy_05_old", None, "BoxCollider", True, True, None),
)


class S08(PublicTask):
    id = "S08"
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

        candidates = make("Candidates", ctx.root)
        groups = {"": candidates}
        for group in GROUPS[1:]:
            groups[group] = make(group.split("/")[-1], groups["/".join(group.split("/")[:-1])])
        entries = list(ENTRIES)
        rng.shuffle(entries)
        for index, (name, primitive, collider, enabled, active, group) in enumerate(entries):
            group = group if group is not None else rng.choice(GROUPS)
            args = {"x": index * 1.2, "y": 0.5, "z": GROUPS.index(group) * 2.0}
            if primitive:
                args["primitiveType"] = primitive
            ref = make(name, groups[group], **args)
            if collider and collider.startswith("Hitbox/"):
                holder = make("Hitbox", ref)
                c.write("component_add", componentType=collider.split("/", 1)[1], **address(holder))
            elif collider:
                c.write("component_add", componentType=collider, **address(ref))
                if not enabled:
                    c.write("component_set_enabled", componentType=collider, enabled=False, **address(ref))
            if not active:
                c.write("gameobject_set_active", active=False, **address(ref))
        reserve = make("Reserve", ctx.root)
        enemy = make("Enemy_09", reserve)
        c.write("component_add", componentType="BoxCollider", **address(enemy))
        return {"candidates": candidates, "candidatesPath": ctx.path("Candidates"), "reserve": reserve}

    def snapshot(self, ctx):
        return {"data": {},
                "digests": {"candidates": V.canonical_digest(V.structure(ctx.probe, [ctx.handles["candidates"]])),
                            "reserve": V.canonical_digest(V.structure(ctx.probe, [ctx.handles["reserve"]]))}}

    def prompt_vars(self, ctx):
        return {"root": ctx.handles["candidatesPath"], "pattern": PATTERN}

    def answer_schema(self):
        return {
            "type": "object",
            "required": ["total", "matches"],
            "properties": {
                "total": {"type": "integer", "desc": {"zh-CN": "符合条件的对象总数", "en": "number of matching objects"}},
                "matches": {"type": "array", "items": {"type": "object", "properties": {
                    "path": {"type": "string", "desc": {"zh-CN": "完整层级路径", "en": "full hierarchy path"}},
                    "instanceId": {"type": "integer", "desc": {"zh-CN": "instanceId（Unity 6000.4+ 填 entityId）",
                                                               "en": "instanceId (entityId on Unity 6000.4+)"}},
                }}},
            },
        }


TASK = S08()

# Producer:Betsy
