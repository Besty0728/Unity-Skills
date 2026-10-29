"""DC8: a difficulty ScriptableObject type, three assets of it and a spawner component that runs on the asset it
references and can be switched to another asset at runtime (dev, challenge).

Fixture: R/<Arena> (seeded position) > <Spawner>, <Container>, two empty objects at seeded positions and in seeded
order; the prefab A/Prefabs/<Unit>_<k>.prefab (a cube); empty folders A/Data and A/Scripts. The class names and the
asset names come from the seed, and so do the numbers of the three assets.
"""
import hashlib
import os
import random

from usk_eval import verifylib as V
from usk_eval.task_api import PublicTask, address

WORDS = ("Kestrel", "Talon", "Vortex", "Ember", "Quill", "Harrow")
NAME_SETS = (("Arena", "Spawner", "Pen", "Drone", ("Easy", "Normal", "Hard")),
             ("Outpost", "Hatchery", "Nest", "Crawler", ("Rookie", "Veteran", "Elite")),
             ("Yard", "Emitter", "Holding", "Probe", ("Calm", "Steady", "Frantic")))
CLASS_SETS = (("DifficultyConfig", "DifficultySpawner"), ("LevelTuning", "TunedSpawner"),
              ("ThreatProfile", "ThreatSpawner"))


def difficulty_spec(seed):
    """spawnInterval, spawnsPerWave and maxAlive of the three assets, from the seed alone."""
    rng = random.Random("DC8:%d" % seed)
    easy = {"spawnInterval": rng.choice((1.6, 1.8, 2.0)), "spawnsPerWave": 1, "maxAlive": rng.choice((2, 3))}
    normal = {"spawnInterval": rng.choice((1.2, 1.3, 1.4)), "spawnsPerWave": 2, "maxAlive": rng.choice((5, 7))}
    hard = {"spawnInterval": rng.choice((0.8, 0.9, 1.0)), "spawnsPerWave": 3,
            "maxAlive": normal["maxAlive"] + 3 * rng.choice((1, 2)) + 2}
    return {"easy": easy, "normal": normal, "hard": hard}


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


class DC8(PublicTask):
    id = "DC8"
    track = "dev"
    tier = "challenge"
    case_version = 1
    minutes = 45
    requires = {"pipeline": "URP", "mode": "bypass", "dedicated": True, "packages": []}
    needs_focus = True

    def build(self, ctx):
        c, rng = ctx.c, ctx.rng
        arena_name, spawner_name, container_name, unit, levels = rng.choice(NAME_SETS)
        cfg_base, spawner_base = rng.choice(CLASS_SETS)
        k = "%s_%s" % (rng.choice(WORDS), ctx.scope.run_id)

        def make(name, parent, **args):
            args.update(address(parent, "parent"))
            return V.object_id(c.write("gameobject_create", name=name, **args))

        for folder in ("Prefabs", "Data", "Scripts"):
            c.write("asset_create_folder", folderPath=ctx.asset(folder))
        prefab = ctx.asset("Prefabs", "%s_%s.prefab" % (unit, k))
        seed_object = make("%s_%s" % (unit, k), ctx.root, primitiveType="Cube", scaleX=0.4, scaleY=0.4, scaleZ=0.4)
        c.write("prefab_create", savePath=prefab, **address(seed_object))
        c.write("gameobject_delete", **address(seed_object))
        arena = make(arena_name, ctx.root, x=rng.randint(-5, 5), y=0, z=rng.randint(-5, 5))
        placed = {}
        order = [(spawner_name, -3), (container_name, 3)]
        if rng.random() < 0.5:
            order.reverse()
        for name, side in order:
            placed[name] = make(name, arena, x=side, y=rng.choice((0.5, 1.0)), z=rng.randint(-2, 2))
        return {"arena": arena, "arenaPath": ctx.path(arena_name),
                "spawner": placed[spawner_name], "spawnerPath": ctx.path(arena_name, spawner_name),
                "container": placed[container_name], "containerPath": ctx.path(arena_name, container_name),
                "prefab": prefab,
                "configScript": ctx.asset("Scripts", "%s_%s.cs" % (cfg_base, k)),
                "spawnerScript": ctx.asset("Scripts", "%s_%s.cs" % (spawner_base, k)),
                "assetEasy": ctx.asset("Data", levels[0] + ".asset"),
                "assetNormal": ctx.asset("Data", levels[1] + ".asset"),
                "assetHard": ctx.asset("Data", levels[2] + ".asset")}

    def snapshot(self, ctx):
        p, h = ctx.probe, ctx.handles
        return {"data": {"arena": V.go_state(p, h["arena"]),
                         "objects": {key: V.go_state(p, h[key]) for key in ("spawner", "container")},
                         "prefab": {"sha": file_sha(ctx.project_path, h["prefab"]), "guid": p.guid(h["prefab"])},
                         "scripts": cs_files(ctx.project_path)},
                "digests": {}}

    def prompt_vars(self, ctx):
        h, spec = ctx.handles, difficulty_spec(ctx.seed)

        def cls(path):
            return os.path.basename(path)[:-len(".cs")]

        return {"scripts": ctx.asset("Scripts"), "cfgCls": cls(h["configScript"]),
                "spawnerCls": cls(h["spawnerScript"]), "prefab": h["prefab"],
                "assetE": h["assetEasy"], "assetN": h["assetNormal"], "assetH": h["assetHard"],
                "intE": spec["easy"]["spawnInterval"], "capE": spec["easy"]["maxAlive"],
                "intN": spec["normal"]["spawnInterval"], "capN": spec["normal"]["maxAlive"],
                "intH": spec["hard"]["spawnInterval"], "capH": spec["hard"]["maxAlive"],
                "arena": h["arenaPath"], "spawner": h["spawnerPath"], "container": h["containerPath"]}

    def answer_schema(self):
        def paths(zh, en):
            return {"type": "array", "items": {"type": "string", "desc": {"zh-CN": zh, "en": en}}}

        return {
            "type": "object",
            "required": ["scriptPaths", "configAssetPaths", "spawnerPath"],
            "properties": {
                "scriptPaths": paths("脚本的资产路径", "asset path of a script"),
                "configAssetPaths": paths("配置资产的路径", "path of a config asset"),
                "spawnerPath": {"type": "string", "desc": {"zh-CN": "挂载了生成组件的对象的完整层级路径",
                                                           "en": "full hierarchy path of the object carrying the "
                                                                 "spawner component"}},
            },
        }


TASK = DC8()

# Producer:Betsy
