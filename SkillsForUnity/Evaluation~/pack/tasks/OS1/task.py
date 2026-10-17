"""OS1: a three-point lighting rig (key, fill, rim) around a subject, placed in the subject's own frame (ops, standard).

Fixture: R/<Stage> (seeded position and yaw) > <Subject>, a capsule with its own seeded yaw; <Stage> > <Rig>, an empty
container with a seeded pose; <Stage> > <Lamp>, an enabled Point light. The lights the task asks for (names, angles,
distances, intensities, colours, cone angles, range) are derived from the seed as well.
"""
import random

from usk_eval import verifylib as V
from usk_eval.task_api import PublicTask, address

ROLES = ("key", "fill", "rim")
NAME_SETS = (("KeyLight", "FillLight", "RimLight"), ("Key", "Fill", "Rim"), ("Light_Key", "Light_Fill", "Light_Rim"))
STAGE_NAMES = (("Stage", "Subject", "Rig", "OldLamp"), ("Set", "Actor", "LightRig", "WorkLamp"),
               ("Booth", "Model", "Lights", "Bulb"))
STAGE_YAWS = (25, 40, 55, 70, 110, 140)
SUBJECT_YAWS = (0, 15, -20, 30)


def rig_spec(seed):
    """The rig the prompt asks for, from the seed alone: per role the light type, azimuth / elevation / distance in
    the subject's frame, intensity (fill and rim as a percentage of the key), colour, cone angle and shadows, plus
    the common range."""
    rng = random.Random("OS1:%d" % seed)
    key, fill, rim = rng.choice(NAME_SETS)
    lights = {
        "key": {"name": key, "type": "Spot", "az": rng.choice((30, 35, 40, 45)), "el": rng.choice((25, 30, 35)),
                "d": rng.choice((3.5, 4.0, 4.5)), "intensity": rng.choice((2.0, 2.5, 3.0)),
                "color": rng.choice(((1, 0.93, 0.8), (1, 0.9, 0.75), (1, 0.95, 0.85))),
                "spot": rng.choice((40, 45, 50)), "shadows": "Soft"},
        "fill": {"name": fill, "type": "Point", "az": -rng.choice((40, 45, 50, 60)), "el": rng.choice((10, 15, 20)),
                 "d": rng.choice((4.5, 5.0, 5.5)), "pct": rng.choice((30, 40, 50)),
                 "color": rng.choice(((0.7, 0.8, 1), (0.75, 0.85, 1), (0.6, 0.75, 1))), "spot": None,
                 "shadows": "None"},
        "rim": {"name": rim, "type": "Spot", "az": rng.choice((150, 160, 170, 180, -160)),
                "el": rng.choice((40, 45, 50)), "d": rng.choice((3.0, 3.5, 4.0)), "pct": rng.choice((70, 80, 120)),
                "color": rng.choice(((1, 1, 1), (0.9, 0.95, 1), (1, 0.98, 0.9))), "spot": rng.choice((20, 25, 35)),
                "shadows": "Hard"},
    }
    lights["fill"]["intensity"] = round(lights["key"]["intensity"] * lights["fill"]["pct"] / 100.0, 4)
    lights["rim"]["intensity"] = round(lights["key"]["intensity"] * lights["rim"]["pct"] / 100.0, 4)
    return {"lights": lights, "range": rng.choice((10, 12, 15))}


class OS1(PublicTask):
    id = "OS1"
    track = "ops"
    tier = "standard"
    case_version = 1
    minutes = 15
    requires = {"pipeline": "URP", "mode": "bypass", "dedicated": True, "packages": []}

    def build(self, ctx):
        c, rng = ctx.c, ctx.rng
        stage_name, subject_name, rig_name, lamp_name = rng.choice(STAGE_NAMES)

        def make(name, parent, **args):
            args.update(address(parent, "parent"))
            return V.object_id(c.write("gameobject_create", name=name, **args))

        stage = make(stage_name, ctx.root, x=rng.randint(-6, 6), y=0, z=rng.randint(-6, 6), rotY=rng.choice(STAGE_YAWS))
        subject = make(subject_name, stage, primitiveType="Capsule", x=rng.randint(-2, 2), y=1, z=rng.randint(-2, 2),
                       rotY=rng.choice(SUBJECT_YAWS))
        rig = make(rig_name, stage, x=rng.randint(-3, 3), y=rng.randint(0, 2), z=rng.randint(-3, 3),
                   rotY=rng.choice((0, 45, 90, -30)))
        lamp = V.object_id(c.write("light_create", name=lamp_name, lightType="Point", x=rng.randint(-4, 4), y=3,
                                   z=rng.randint(-4, 4), r=1, g=0.85, b=0.6, intensity=rng.choice((0.8, 1.2, 1.5)),
                                   range=8, shadows="None"))
        c.write("gameobject_set_parent", **dict(address(lamp, "child"), **address(stage, "parent")))
        return {"stage": stage, "stagePath": ctx.path(stage_name), "subject": subject,
                "subjectPath": ctx.path(stage_name, subject_name), "rig": rig,
                "rigPath": ctx.path(stage_name, rig_name), "lamp": lamp, "lampPath": ctx.path(stage_name, lamp_name)}

    def snapshot(self, ctx):
        p, h = ctx.probe, ctx.handles
        lights = p.query('{"componentType": "Light", "includeInactive": true, "limit": 500}')
        return {"data": {"subject": V.go_state(p, h["subject"]),
                         "stage": V.go_state(p, h["stage"]),
                         "rig": V.go_state(p, h["rig"]),
                         "lamp": {"state": V.go_state(p, h["lamp"]), "local": V.local_trs(p, h["lamp"]),
                                  "light": ctx.read("light_get_info", path=h["lampPath"])},
                         "lights": [V.object_id(row) for row in lights.get("objects") or []
                                    if V.under(row.get("path"), ctx.R)]},
                "digests": {}}

    def prompt_vars(self, ctx):
        h = ctx.handles
        spec = rig_spec(ctx.seed)
        out = {"subject": h["subjectPath"], "rig": h["rigPath"], "old": h["lampPath"], "range": spec["range"]}
        for role in ROLES:
            light = spec["lights"][role]
            out[role] = light["name"]
            out[role + "D"] = light["d"]
            out[role + "Az"] = light["az"]
            out[role + "El"] = light["el"]
            out[role + "Color"] = "%g, %g, %g" % tuple(light["color"])
        out["keyIntensity"] = spec["lights"]["key"]["intensity"]
        out["fillPct"] = spec["lights"]["fill"]["pct"]
        out["rimPct"] = spec["lights"]["rim"]["pct"]
        out["keySpot"] = spec["lights"]["key"]["spot"]
        out["rimSpot"] = spec["lights"]["rim"]["spot"]
        return out

    def answer_schema(self):
        def path(zh, en):
            return {"type": "string", "desc": {"zh-CN": zh, "en": en}}

        return {
            "type": "object",
            "required": ["keyPath", "fillPath", "rimPath"],
            "properties": {
                "keyPath": path("主光（key）的完整层级路径", "full hierarchy path of the key light"),
                "fillPath": path("辅光（fill）的完整层级路径", "full hierarchy path of the fill light"),
                "rimPath": path("轮廓光（rim）的完整层级路径", "full hierarchy path of the rim light"),
            },
        }


TASK = OS1()

# Producer:Betsy
