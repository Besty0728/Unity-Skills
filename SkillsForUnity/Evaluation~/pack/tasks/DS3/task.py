"""DS3: a WaveConfig ScriptableObject, two assets of it and a spawner component that follows the asset it references
(dev, standard).

Fixture: R/<Arena> > <SpawnerA>, <SpawnerB>, <ContainerA>, <ContainerB>, four empty objects at seeded positions; the
prefab A/Prefabs/<Drone>_<k>.prefab (a sphere). The class names carry a seeded word and the run id; the counts and
intervals of the two assets come from the seed as well.
"""
import hashlib
import os
import random

from usk_eval import verifylib as V
from usk_eval.task_api import PublicTask, address

WORDS = ("Kestrel", "Talon", "Vortex", "Ember", "Quill", "Harrow")
NAME_SETS = (("Arena", "SpawnerA", "SpawnerB", "ContainerA", "ContainerB", "Drone", "Wave_Light", "Wave_Heavy"),
             ("Pit", "GateA", "GateB", "PenA", "PenB", "Beetle", "Wave_Scout", "Wave_Assault"),
             ("Yard", "PortA", "PortB", "BayA", "BayB", "Probe", "Wave_Early", "Wave_Late"))


def wave_spec(seed):
    """count and interval of the two assets, from the seed alone: A spawns few and slowly, B many and quickly."""
    rng = random.Random("DS3:%d" % seed)
    return {"a": {"count": rng.choice((3, 4)), "interval": rng.choice((0.8, 1.0, 1.2))},
            "b": {"count": rng.choice((5, 6, 7)), "interval": rng.choice((0.5, 0.6, 0.7))}}


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


class DS3(PublicTask):
    id = "DS3"
    track = "dev"
    tier = "standard"
    case_version = 1
    minutes = 35
    requires = {"pipeline": "URP", "mode": "bypass", "dedicated": True, "packages": []}
    needs_focus = True

    def build(self, ctx):
        c, rng = ctx.c, ctx.rng
        arena_name, a_name, b_name, pen_a, pen_b, drone, light, heavy = rng.choice(NAME_SETS)
        k = "%s_%s" % (rng.choice(WORDS), ctx.scope.run_id)

        def make(name, parent, **args):
            args.update(address(parent, "parent"))
            return V.object_id(c.write("gameobject_create", name=name, **args))

        for folder in ("Prefabs", "Data", "Scripts"):
            c.write("asset_create_folder", folderPath=ctx.asset(folder))
        prefab = ctx.asset("Prefabs", "%s_%s.prefab" % (drone, k))
        seed_object = make("%s_%s" % (drone, k), ctx.root, primitiveType="Sphere")
        c.write("prefab_create", savePath=prefab, **address(seed_object))
        c.write("gameobject_delete", **address(seed_object))
        arena = make(arena_name, ctx.root, x=rng.randint(-5, 5), y=0, z=rng.randint(-5, 5))
        spawner_a = make(a_name, arena, x=-4, y=1, z=rng.randint(-2, 2))
        spawner_b = make(b_name, arena, x=4, y=1, z=rng.randint(-2, 2))
        container_a = make(pen_a, arena, x=-4, y=0, z=rng.randint(3, 5))
        container_b = make(pen_b, arena, x=4, y=0, z=rng.randint(3, 5))
        return {"arena": arena, "arenaPath": ctx.path(arena_name),
                "spawnerA": spawner_a, "spawnerAPath": ctx.path(arena_name, a_name),
                "spawnerB": spawner_b, "spawnerBPath": ctx.path(arena_name, b_name),
                "containerA": container_a, "containerAPath": ctx.path(arena_name, pen_a),
                "containerB": container_b, "containerBPath": ctx.path(arena_name, pen_b),
                "prefab": prefab, "configScript": ctx.asset("Scripts", "WaveConfig_%s.cs" % k),
                "spawnerScript": ctx.asset("Scripts", "WaveSpawner_%s.cs" % k),
                "assetA": ctx.asset("Data", light + ".asset"), "assetB": ctx.asset("Data", heavy + ".asset")}

    def snapshot(self, ctx):
        p, h = ctx.probe, ctx.handles
        return {"data": {"arena": V.go_state(p, h["arena"]),
                         "objects": {key: V.go_state(p, h[key]) for key in ("spawnerA", "spawnerB", "containerA",
                                                                            "containerB")},
                         "prefab": {"sha": file_sha(ctx.project_path, h["prefab"]), "guid": p.guid(h["prefab"])},
                         "scripts": cs_files(ctx.project_path)},
                "digests": {}}

    def prompt_vars(self, ctx):
        h, spec = ctx.handles, wave_spec(ctx.seed)

        def cls(path):
            return os.path.basename(path)[:-len(".cs")]

        return {"scripts": ctx.asset("Scripts"), "cfgCls": cls(h["configScript"]), "spawnerCls": cls(h["spawnerScript"]),
                "prefab": h["prefab"], "assetA": h["assetA"], "assetB": h["assetB"],
                "countA": spec["a"]["count"], "intervalA": spec["a"]["interval"],
                "countB": spec["b"]["count"], "intervalB": spec["b"]["interval"],
                "spawnerA": h["spawnerAPath"], "spawnerB": h["spawnerBPath"],
                "containerA": h["containerAPath"], "containerB": h["containerBPath"]}

    def answer_schema(self):
        def paths(zh, en):
            return {"type": "array", "items": {"type": "string", "desc": {"zh-CN": zh, "en": en}}}

        return {
            "type": "object",
            "required": ["scriptPaths", "configAssetPaths", "spawnerPaths"],
            "properties": {
                "scriptPaths": paths("脚本的资产路径", "asset path of a script"),
                "configAssetPaths": paths("配置资产的路径", "path of a config asset"),
                "spawnerPaths": paths("挂载了生成组件的对象的完整层级路径",
                                      "full hierarchy path of an object carrying the spawner component"),
            },
        }


TASK = DS3()

# Producer:Betsy
