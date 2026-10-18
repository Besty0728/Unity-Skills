"""DB2: Health MonoBehaviour with TakeDamage, Heal, and a one-shot Died event (dev, basic).

Fixture: R/<Zone>/<Unit_a> (x = -3) and R/<Zone>/<Unit_b> (x = 3), capsule units that each carry a Shield child
cube, plus an optional bystander prop; which unit the prompt names, the names, whether the prop exists and maxHealth
come from the seed.
"""
import os
import random

from usk_eval import verifylib as V
from usk_eval.task_api import PublicTask, address

WORDS = ("Kestrel", "Talon", "Vortex", "Ember", "Quill", "Harrow", "Juniper", "Marlow")
ZONES = ("Arena", "Courtyard", "Barracks", "Outpost")
UNITS = ("Guard", "Sentry", "Knight", "Warden")
PROPS = ("Banner", "Crate", "Pillar")
MAX_HEALTHS = (80, 120, 150)
SPEC_SALT = 0xDB02


def spec(seed):
    """Task parameters from the seed (shared with the private module): maxHealth, defaultMaxHealth."""
    rng = random.Random(seed ^ SPEC_SALT)
    return {"maxHealth": rng.choice(MAX_HEALTHS), "defaultMaxHealth": 100}


def subtree_states(probe, root):
    """[[iid, go_state]] for root and every descendant, depth first."""
    out, stack = [], [root]
    while stack:
        iid = stack.pop()
        state = V.go_state(probe, iid)
        out.append([V.object_id(iid), state])
        stack.extend(reversed((state or {}).get("children") or []))
    return out


def cs_files(project_path):
    """Project-relative paths of every .cs file under Assets/."""
    out = []
    for base, dirs, files in os.walk(os.path.join(project_path, "Assets")):
        dirs.sort()
        for name in sorted(files):
            if name.endswith(".cs"):
                rel = os.path.relpath(os.path.join(base, name), project_path).replace(os.sep, "/")
                out.append(rel)
    return sorted(out)


class DB2(PublicTask):
    id = "DB2"
    track = "dev"
    tier = "basic"
    case_version = 1
    minutes = 15
    requires = {"pipeline": ["URP", "BuiltIn"], "mode": "bypass", "dedicated": False, "packages": []}
    needs_focus = True

    def build(self, ctx):
        c, rng = ctx.c, ctx.rng
        w_a, w_b, w_cls = rng.sample(WORDS, 3)
        zone_name = rng.choice(ZONES)
        u_a, u_b = rng.sample(UNITS, 2)
        prop_name = rng.choice(PROPS)
        pick = rng.choice((0, 1))
        has_prop = rng.choice((True, False))

        def make(name, parent, **args):
            args.update(address(parent, "parent"))
            return V.object_id(c.write("gameobject_create", name=name, **args))

        zone = make(zone_name, ctx.root, x=0, y=0, z=0)
        specs = [("%s_%s" % (u_a, w_a), -3), ("%s_%s" % (u_b, w_b), 3)]
        units = []
        for name, x in specs:
            u = make(name, zone, primitiveType="Capsule", x=x, y=1, z=0)
            make("Shield", u, primitiveType="Cube", x=0.4, y=0.2, z=0.4)
            units.append((u, name))
        if has_prop:
            make(prop_name, zone, primitiveType="Cylinder", x=0, y=0.5, z=2)

        chosen_unit, chosen_name = units[pick]
        other_unit, other_name = units[1 - pick]
        cls = "Health_%s_%s" % (w_cls, ctx.scope.run_id)
        return {
            "zone": zone,
            "unit": chosen_unit,
            "unitPath": ctx.path(zone_name, chosen_name),
            "otherUnit": other_unit,
            "otherUnitPath": ctx.path(zone_name, other_name),
            "className": cls,
            "script": ctx.asset("Scripts", cls + ".cs"),
            "scripts": ctx.asset("Scripts"),
        }

    def snapshot(self, ctx):
        return {
            "data": {
                "states": subtree_states(ctx.probe, ctx.handles["zone"]),
                "scripts": cs_files(ctx.project_path),
            },
            "digests": {},
        }

    def prompt_vars(self, ctx):
        h = ctx.handles
        s = spec(ctx.seed)
        return {
            "scripts": h["scripts"],
            "cls": h["className"],
            "unit": h["unitPath"],
            "other": h["otherUnitPath"],
            "max_health": s["maxHealth"],
            "default": s["defaultMaxHealth"],
        }

    def answer_schema(self):
        return {
            "type": "object",
            "required": ["scriptPath", "unitInstanceId", "maxHealth"],
            "properties": {
                "scriptPath": {
                    "type": "string",
                    "desc": {"zh-CN": "新建脚本的资产路径", "en": "asset path of the created script"},
                },
                "unitInstanceId": {
                    "type": "integer",
                    "desc": {
                        "zh-CN": "挂载了组件的单位对象的 instanceId（Unity 6000.4+ 填 entityId）",
                        "en": "instanceId of the unit object carrying the component (entityId on Unity 6000.4+)",
                    },
                },
                "maxHealth": {
                    "type": "integer",
                    "desc": {
                        "zh-CN": "在场景中为该组件设置的 maxHealth 值",
                        "en": "maxHealth value stored on the component in the scene",
                    },
                },
            },
        }


TASK = DB2()

# Producer:Betsy
