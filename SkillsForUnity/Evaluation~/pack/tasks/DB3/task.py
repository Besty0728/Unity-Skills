"""DB3: a Blinker script that toggles a Light at a serialized interval (dev, basic).

Fixture: R/Hall with two point lights, Lamp_<a> and Lamp_<b>, both enabled; the component goes on one of them.
"""
import os
import random

from usk_eval import verifylib as V
from usk_eval.task_api import PublicTask, address

WORDS = ("Kestrel", "Talon", "Vortex", "Ember", "Quill", "Harrow", "Juniper", "Marlow")
INTERVALS = (0.8, 1.0, 1.2)
DEFAULT_INTERVAL = 0.5
SPEC_SALT = 0x5B03


def spec(seed):
    """Interval the scene must store, a pure function of the seed (shared with the private module)."""
    rng = random.Random(seed ^ SPEC_SALT)
    return {"interval": rng.choice(INTERVALS)}


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


def light_enabled(probe, ref):
    """enabled flag of the Light component as component_list reports it (None when there is no Light)."""
    row = next((row for row in probe.components(ref) if row.get("type") == "Light"), None)
    return row.get("enabled") if row else None


def _num(value):
    return "%g" % value


class DB3(PublicTask):
    id = "DB3"
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
        chosen = rng.randrange(2)
        hall = make("Hall", ctx.root)
        lamps = []
        for index, word in enumerate(words[1:]):
            lamp = make("Lamp_" + word, hall, x=index * 6 - 3, y=2.5)
            c.write("component_add", componentType="Light", **address(lamp))
            lamps.append(lamp)
        return {"hall": hall, "lamps": lamps, "lamp": lamps[chosen],
                "lampPath": ctx.path("Hall", "Lamp_" + words[1 + chosen]),
                "otherPath": ctx.path("Hall", "Lamp_" + words[2 - chosen]),
                "script": ctx.asset("Scripts", "Blinker_%s.cs" % words[0])}

    def snapshot(self, ctx):
        p, h = ctx.probe, ctx.handles
        return {"data": {"states": subtree_states(p, h["hall"]),
                         "lights": [[ref, light_enabled(p, ref)] for ref in h["lamps"]],
                         "scripts": cs_files(ctx.project_path)},
                "digests": {}}

    def prompt_vars(self, ctx):
        h, values = ctx.handles, spec(ctx.seed)
        return {"scripts": ctx.asset("Scripts"), "cls": class_name(h["script"]), "lamp": h["lampPath"],
                "other": h["otherPath"], "default": _num(DEFAULT_INTERVAL), "interval": _num(values["interval"])}

    def answer_schema(self):
        return {
            "type": "object",
            "required": ["scriptPath", "lampInstanceId", "interval"],
            "properties": {
                "scriptPath": {"type": "string", "desc": {"zh-CN": "脚本的资产路径", "en": "asset path of the script"}},
                "lampInstanceId": {"type": "integer", "desc": {
                    "zh-CN": "挂载了组件的灯对象的 instanceId（Unity 6000.4+ 填 entityId）",
                    "en": "instanceId of the light object that carries the component (entityId on Unity 6000.4+)"}},
                "interval": {"type": "number", "desc": {"zh-CN": "场景中存储的 interval 值（秒）",
                                                        "en": "interval value stored in the scene (seconds)"}},
            },
        }


TASK = DB3()

# Producer:Betsy
