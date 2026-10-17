"""DB1: a Spinner script that turns one object at a serialized speed (dev, basic).

Fixture: R/Yard with two mills, Mill_<a> and Mill_<b>, each holding a tilted Blades cube and a Shaft cylinder; the
component goes on one of the two Blades.
"""
import os
import random

from usk_eval import verifylib as V
from usk_eval.task_api import PublicTask, address

WORDS = ("Kestrel", "Talon", "Vortex", "Ember", "Quill", "Harrow", "Juniper", "Marlow")
TILTS = (20, 30, 40)
SPEEDS = (30, 45, 60)
WAITS = (1.5, 2.0, 2.5)
DEFAULT_SPEED = 15
SPEC_SALT = 0x5B01


def spec(seed):
    """Speed the scene must store and the Play Mode wait, a pure function of the seed (shared with the private module)."""
    rng = random.Random(seed ^ SPEC_SALT)
    return {"speed": rng.choice(SPEEDS), "wait": rng.choice(WAITS)}


def class_name(script_path):
    return os.path.basename(script_path)[:-len(".cs")]


def cs_files(project_path):
    out = []
    for base, dirs, files in os.walk(os.path.join(project_path, "Assets")):
        dirs.sort()
        out += [os.path.relpath(os.path.join(base, name), project_path).replace(os.sep, "/")
                for name in sorted(files) if name.endswith(".cs")]
    return sorted(out)


def subtree_states(probe, root):
    """[[ref, go_state]] of root and every descendant, depth first."""
    out, stack = [], [root]
    while stack:
        ref = stack.pop()
        state = V.go_state(probe, ref)
        out.append([V.object_id(ref), state])
        stack.extend(reversed((state or {}).get("children") or []))
    return out


def _num(value):
    return "%g" % value


class DB1(PublicTask):
    id = "DB1"
    track = "dev"
    tier = "basic"
    case_version = 1
    minutes = 15
    requires = {"pipeline": ["URP", "BuiltIn"], "mode": "bypass", "dedicated": False, "packages": []}
    needs_focus = True

    def build(self, ctx):
        c, rng = ctx.c, ctx.rng

        def make(name, parent, **args):
            args.update(address(parent, "parent"))
            return V.object_id(c.write("gameobject_create", name=name, **args))

        words = rng.sample(WORDS, 3)
        tilt = rng.choice(TILTS)
        chosen = rng.randrange(2)
        yard = make("Yard", ctx.root)
        blades = []
        for index, word in enumerate(words[1:]):
            mill = make("Mill_" + word, yard, x=index * 6 - 3)
            blades.append(make("Blades", mill, primitiveType="Cube", y=2, rotX=tilt, scaleX=2, scaleY=0.2, scaleZ=0.2))
            make("Shaft", mill, primitiveType="Cylinder", y=1, scaleX=0.2, scaleY=1, scaleZ=0.2)
        return {"yard": yard, "blades": blades, "spinner": blades[chosen],
                "spinnerPath": ctx.path("Yard", "Mill_" + words[1 + chosen], "Blades"),
                "otherPath": ctx.path("Yard", "Mill_" + words[2 - chosen], "Blades"),
                "script": ctx.asset("Scripts", "Spinner_%s.cs" % words[0])}

    def snapshot(self, ctx):
        return {"data": {"states": subtree_states(ctx.probe, ctx.handles["yard"]),
                         "scripts": cs_files(ctx.project_path)},
                "digests": {}}

    def prompt_vars(self, ctx):
        h, values = ctx.handles, spec(ctx.seed)
        return {"scripts": ctx.asset("Scripts"), "cls": class_name(h["script"]), "blades": h["spinnerPath"],
                "other": h["otherPath"], "default": _num(DEFAULT_SPEED), "speed": _num(values["speed"]),
                "wait": _num(values["wait"]), "turn": _num(values["speed"] * values["wait"])}

    def answer_schema(self):
        return {
            "type": "object",
            "required": ["scriptPath", "spinnerInstanceId", "degreesPerSecond"],
            "properties": {
                "scriptPath": {"type": "string", "desc": {"zh-CN": "脚本的资产路径", "en": "asset path of the script"}},
                "spinnerInstanceId": {"type": "integer", "desc": {
                    "zh-CN": "挂载了组件的对象的 instanceId（Unity 6000.4+ 填 entityId）",
                    "en": "instanceId of the object that carries the component (entityId on Unity 6000.4+)"}},
                "degreesPerSecond": {"type": "number", "desc": {"zh-CN": "场景中存储的 degreesPerSecond 值",
                                                                "en": "degreesPerSecond value stored in the scene"}},
            },
        }


TASK = DB1()

# Producer:Betsy
