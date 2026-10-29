"""DB4: a follow-camera script that keeps a camera at a followed object's position plus a world-space offset, with
optional smoothing (dev, basic).

Fixture: R/<Course> (seeded position) holding two capsule vehicles <Vehicle>_<a> and <Vehicle>_<b> (seeded yaw) and a
camera object <Cam> carrying a Camera component, in a seeded sibling order; the camera follows one of the two
vehicles. Names, positions, yaws and the offset come from the seed.
"""
import os
import random

from usk_eval import verifylib as V
from usk_eval.task_api import PublicTask, address

WORDS = ("Kestrel", "Talon", "Vortex", "Ember", "Quill", "Harrow", "Juniper", "Marlow")
NAME_SETS = (("Track", "Rover", "ChaseCam"), ("Course", "Kart", "FollowCam"), ("Arena", "Buggy", "TrailCam"))
CLASS_BASES = ("CameraFollow", "FollowRig", "ChaseFollow")
COURSE_X = (-6, -4, 4, 6)
COURSE_Z = (-5, 5, 7)
YAWS = (60, 90, 135, -120, 150, -45)
OFFSET_X = (-2.5, -1.5, 1.5, 2.5)
OFFSET_Y = (4, 4.5, 5.5)
OFFSET_Z = (-7, -8, -9)
DEFAULT_OFFSET = (0, 3, -6)
DEFAULT_SMOOTH_SPEED = 2
SPEC_SALT = 0x5B04


def spec(seed):
    """World-space offset the scene must store, a pure function of the seed (shared with the private module)."""
    rng = random.Random(seed ^ SPEC_SALT)
    return {"offset": (rng.choice(OFFSET_X), rng.choice(OFFSET_Y), rng.choice(OFFSET_Z))}


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


def vector_text(values):
    return "(%s)" % ", ".join("%g" % value for value in values)


class DB4(PublicTask):
    id = "DB4"
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

        course_name, vehicle, cam_name = rng.choice(NAME_SETS)
        words = rng.sample(WORDS, 3)
        yaws = rng.sample(YAWS, 2)
        chosen = rng.randrange(2)
        order = rng.sample(range(3), 3)
        names = ["%s_%s" % (vehicle, word) for word in words[1:]]
        course = make(course_name, ctx.root, x=rng.choice(COURSE_X), z=rng.choice(COURSE_Z))
        made = {}
        for slot in order:
            if slot == 2:
                made[slot] = make(cam_name, course, y=4, z=-9, rotX=20)
                c.write("component_add", componentType="Camera", **address(made[slot]))
            else:
                made[slot] = make(names[slot], course, primitiveType="Capsule", x=slot * 6 - 3, y=1, z=slot * 2,
                                  rotY=yaws[slot])
        vehicles = [made[0], made[1]]
        return {"course": course, "rovers": vehicles, "rover": vehicles[chosen],
                "roverPath": ctx.path(course_name, names[chosen]),
                "otherPath": ctx.path(course_name, names[1 - chosen]),
                "cam": made[2], "camPath": ctx.path(course_name, cam_name),
                "script": ctx.asset("Scripts", "%s_%s.cs" % (rng.choice(CLASS_BASES), words[0]))}

    def snapshot(self, ctx):
        info = ctx.probe.go(ctx.handles["rover"]) or {}
        return {"data": {"states": subtree_states(ctx.probe, ctx.handles["course"]),
                         "scripts": cs_files(ctx.project_path),
                         "origin": [round(value, 4) for value in V.floats(info.get("position"))]},
                "digests": {}}

    def prompt_vars(self, ctx):
        h = ctx.handles
        return {"scripts": ctx.asset("Scripts"), "cls": class_name(h["script"]), "cam": h["camPath"],
                "rover": h["roverPath"], "other": h["otherPath"], "offset": vector_text(spec(ctx.seed)["offset"]),
                "defaultOffset": vector_text(DEFAULT_OFFSET), "defaultSpeed": "%g" % DEFAULT_SMOOTH_SPEED}

    def answer_schema(self):
        return {
            "type": "object",
            "required": ["scriptPath", "cameraInstanceId", "followedInstanceId"],
            "properties": {
                "scriptPath": {"type": "string", "desc": {"zh-CN": "脚本的资产路径", "en": "asset path of the script"}},
                "cameraInstanceId": {"type": "integer", "desc": {
                    "zh-CN": "挂载了组件的对象的 instanceId（Unity 6000.4+ 填 entityId）",
                    "en": "instanceId of the object that carries the component (entityId on Unity 6000.4+)"}},
                "followedInstanceId": {"type": "integer", "desc": {
                    "zh-CN": "组件的 target 字段在场景中引用的对象的 instanceId（Unity 6000.4+ 填 entityId）",
                    "en": "instanceId of the object the component's target field references in the scene "
                          "(entityId on Unity 6000.4+)"}},
            },
        }


TASK = DB4()

# Producer:Betsy
