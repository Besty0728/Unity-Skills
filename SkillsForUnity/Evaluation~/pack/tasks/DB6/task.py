"""DB6: a TimedSpawner script that instantiates a prefab into a container at a serialized interval up to a maximum count
(dev, basic).

Fixture: R/<Rig> with two emitters and two containers at distinct world positions, plus two primitive prefabs under
A/<Folder>/. One emitter, one container and one prefab are selected by the seed.
"""
import hashlib
import os
import random

from usk_eval import verifylib as V
from usk_eval.task_api import PublicTask, address

WORDS = ("Kestrel", "Talon", "Vortex", "Ember", "Quill", "Harrow", "Juniper", "Marlow")
NAME_SETS = (("Bay", "Emitter", "Holder", "Pod", "Prefabs"),
             ("Deck", "Launcher", "Rack", "Crate", "Units"),
             ("Yard", "Feeder", "Bin", "Orb", "Catalog"))
PRIMITIVES = ("Sphere", "Cube", "Capsule")
INTERVALS = (0.8, 1.0, 1.2)
COUNTS = (3, 4, 5)
DEFAULT_INTERVAL = 2.0
DEFAULT_COUNT = 2
SPEC_SALT = 0x5B06


def spec(seed):
    """Interval and maximum count the scene must store, a pure function of the seed (shared with the private module)."""
    rng = random.Random(seed ^ SPEC_SALT)
    return {"interval": rng.choice(INTERVALS), "maxCount": rng.choice(COUNTS)}


def class_name(script_path):
    return os.path.basename(script_path)[:-len(".cs")]


def cs_files(project_path):
    out = []
    for base, dirs, files in os.walk(os.path.join(project_path, "Assets")):
        dirs.sort()
        out += [os.path.relpath(os.path.join(base, name), project_path).replace(os.sep, "/")
                for name in sorted(files) if name.endswith(".cs")]
    return sorted(out)


def file_sha(project_path, asset_path):
    try:
        with open(os.path.join(project_path, *asset_path.split("/")), "rb") as handle:
            return hashlib.sha256(handle.read()).hexdigest()
    except OSError:
        return None


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


class DB6(PublicTask):
    id = "DB6"
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

        rig_base, emit_base, hold_base, pod_base, folder_name = rng.choice(NAME_SETS)
        words = rng.sample(WORDS, 7)
        shapes = rng.sample(PRIMITIVES, 2)
        emit_pick = rng.randrange(2)
        hold_pick = rng.randrange(2)
        pod_pick = rng.randrange(2)

        for folder in (folder_name, "Scripts"):
            c.write("asset_create_folder", folderPath=ctx.asset(folder))

        prefabs = []
        for index in range(2):
            pod_name = "%s_%s" % (pod_base, words[1 + index])
            prefab_path = ctx.asset(folder_name, pod_name + ".prefab")
            temp = make(pod_name, ctx.root, primitiveType=shapes[index])
            c.write("prefab_create", savePath=prefab_path, **address(temp))
            c.write("gameobject_delete", **address(temp))
            prefabs.append(prefab_path)

        rig = make(rig_base, ctx.root, x=rng.choice((-2, 0, 2)), y=0, z=rng.choice((-2, 0, 2)))
        emitters, emit_paths = [], []
        for index in range(2):
            ename = "%s_%s" % (emit_base, words[3 + index])
            emitters.append(make(ename, rig, x=index * 8 - 4, y=1, z=-2))
            emit_paths.append(ctx.path(rig_base, ename))

        containers, hold_paths = [], []
        for index in range(2):
            hname = "%s_%s" % (hold_base, words[5 + index])
            containers.append(make(hname, rig, x=index * 8 - 4, y=0, z=3))
            hold_paths.append(ctx.path(rig_base, hname))

        return {"rig": rig, "emitters": emitters, "emitter": emitters[emit_pick],
                "emitterPath": emit_paths[emit_pick], "otherEmitterPath": emit_paths[1 - emit_pick],
                "containers": containers, "container": containers[hold_pick],
                "containerPath": hold_paths[hold_pick], "otherContainerPath": hold_paths[1 - hold_pick],
                "prefabs": prefabs, "prefab": prefabs[pod_pick], "otherPrefab": prefabs[1 - pod_pick],
                "script": ctx.asset("Scripts", "TimedSpawner_%s.cs" % words[0])}

    def snapshot(self, ctx):
        p, h = ctx.probe, ctx.handles
        return {"data": {"states": subtree_states(p, h["rig"]),
                         "prefabs": {path: {"sha": file_sha(ctx.project_path, path), "guid": p.guid(path)}
                                     for path in h["prefabs"]},
                         "scripts": cs_files(ctx.project_path)},
                "digests": {}}

    def prompt_vars(self, ctx):
        h, values = ctx.handles, spec(ctx.seed)
        return {"scripts": ctx.asset("Scripts"), "cls": class_name(h["script"]),
                "emitter": h["emitterPath"], "otherEmitter": h["otherEmitterPath"],
                "container": h["containerPath"], "otherContainer": h["otherContainerPath"],
                "prefab": h["prefab"], "otherPrefab": h["otherPrefab"],
                "defaultInterval": _num(DEFAULT_INTERVAL), "defaultCount": str(DEFAULT_COUNT),
                "interval": _num(values["interval"]), "maxCount": str(values["maxCount"])}

    def answer_schema(self):
        return {
            "type": "object",
            "required": ["scriptPath", "emitterInstanceId", "containerInstanceId", "interval", "maxCount"],
            "properties": {
                "scriptPath": {"type": "string", "desc": {"zh-CN": "脚本的资产路径", "en": "asset path of the script"}},
                "emitterInstanceId": {"type": "integer", "desc": {
                    "zh-CN": "挂载了组件的发射器对象的 instanceId（Unity 6000.4+ 填 entityId）",
                    "en": "instanceId of the emitter object carrying the component (entityId on Unity 6000.4+)"}},
                "containerInstanceId": {"type": "integer", "desc": {
                    "zh-CN": "场景中存储的容器对象的 instanceId（Unity 6000.4+ 填 entityId）",
                    "en": "instanceId of the container object referenced in the scene (entityId on Unity 6000.4+)"}},
                "interval": {"type": "number", "desc": {"zh-CN": "场景中存储的 interval 值（秒）",
                                                        "en": "interval value stored in the scene (seconds)"}},
                "maxCount": {"type": "integer", "desc": {"zh-CN": "场景中存储的 maxCount 值",
                                                         "en": "maxCount value stored in the scene"}},
            },
        }


TASK = DB6()

# Producer:Betsy
