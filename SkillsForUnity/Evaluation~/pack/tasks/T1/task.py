"""T1: two given scripts, a ScriptableObject asset and a component wired on one of three same-name turrets (dev, standard).

Fixture: three projectile prefabs, A/Prefabs/Projectile_<k>.prefab, A/Prefabs/Projectile_<k>_Old.prefab and
A/Common/Projectile_<k>.prefab (k = seeded word + run id); R/Turrets with three Turret cubes, each with a Barrel, at
world X -4, 0 and 4 in a seeded sibling order; R/Range with three Dummy capsules with different tags in a seeded order.
"""
import hashlib
import json
import os

from usk_eval import verifylib as V
from usk_eval.task_api import PublicTask, address

TURRET_X = (-4.0, 0.0, 4.0)
DUMMY_TAGS = ("Respawn", "Untagged", "Player")
WORDS = ("Kestrel", "Talon", "Vortex", "Ember", "Quill", "Harrow")
PREFABS = (("Prefabs", "Projectile_%s.prefab", "Sphere"), ("Prefabs", "Projectile_%s_Old.prefab", "Capsule"),
           ("Common", "Projectile_%s.prefab", "Cube"))

CONFIG_SRC = """using UnityEngine;

public class {cls} : ScriptableObject
{{
    public float fireRate = 1f;
    public int burstCount = 1;
    public GameObject projectilePrefab;
}}
"""

AI_SRC = """using UnityEngine;

public class {cls} : MonoBehaviour
{{
    public {cfg} config;
    public Transform target;
    public float turnSpeed = 30f;
}}
"""


def class_name(script_path):
    return os.path.basename(script_path)[:-len(".cs")]


def sources(handles):
    """(config source, AI source) the prompt gives verbatim."""
    cfg, ai = class_name(handles["configScript"]), class_name(handles["aiScript"])
    return CONFIG_SRC.format(cls=cfg), AI_SRC.format(cls=ai, cfg=cfg)


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


def meta_guid(project_path, asset_path):
    try:
        return V.uyaml.meta_guid(os.path.join(project_path, *asset_path.split("/")))
    except OSError:
        return None


class T1(PublicTask):
    id = "T1"
    track = "dev"
    tier = "standard"
    case_version = 2
    minutes = 40
    requires = {"pipeline": "URP", "mode": "bypass", "dedicated": True, "packages": []}
    needs_focus = True

    def build(self, ctx):
        c, rng = ctx.c, ctx.rng

        def make(name, parent, **args):
            args.update(address(parent, "parent"))
            return V.object_id(c.write("gameobject_create", name=name, **args))

        k = "%s_%s" % (rng.choice(WORDS), ctx.scope.run_id)
        order = list(TURRET_X)
        while order == sorted(order):
            rng.shuffle(order)
        tags = list(DUMMY_TAGS)
        rng.shuffle(tags)
        for folder in ("Prefabs", "Common", "Data"):
            c.write("asset_create_folder", folderPath=ctx.asset(folder))
        prefabs = []
        for folder, pattern, shape in PREFABS:
            path = ctx.asset(folder, pattern % k)
            ref = make(os.path.basename(path)[:-len(".prefab")], ctx.root, primitiveType=shape)
            c.write("prefab_create", savePath=path, **address(ref))
            c.write("gameobject_delete", **address(ref))
            prefabs.append(path)
        turrets_root = make("Turrets", ctx.root)
        turrets, barrels = [], []
        for x in order:
            turret = make("Turret", turrets_root, primitiveType="Cube", x=x, y=0.5, z=0)
            barrels.append(make("Barrel", turret, primitiveType="Cylinder", x=0, y=0.6, z=0.5, scaleX=0.3,
                                scaleY=0.3, scaleZ=0.3))
            turrets.append(turret)
        range_root = make("Range", ctx.root, z=10)
        dummies = []
        for index, tag in enumerate(tags):
            dummy = make("Dummy", range_root, primitiveType="Capsule", x=-3 + 3 * index, y=1, z=0)
            if tag != "Untagged":
                c.write("gameobject_set_tag_batch", items=json.dumps([dict({"tag": tag}, **address(dummy))]))
            dummies.append(dummy)
        return {"turretsPath": ctx.path("Turrets"), "rangePath": ctx.path("Range"), "turrets": turrets,
                "barrels": barrels, "dummies": dummies,
                "configScript": ctx.asset("Scripts", "TurretConfig_%s.cs" % k),
                "aiScript": ctx.asset("Scripts", "TurretAI_%s.cs" % k),
                "configAsset": ctx.asset("Data", "MainTurretConfig.asset"), "projectilePrefab": prefabs[0],
                "prefabs": sorted(prefabs)}

    def snapshot(self, ctx):
        p, h, project = ctx.probe, ctx.handles, ctx.project_path
        return {"data": {"turrets": [[ref, V.go_state(p, ref)] for ref in h["turrets"]],
                         "barrels": [[ref, V.go_state(p, ref)] for ref in h["barrels"]],
                         "dummies": [[ref, V.go_state(p, ref)] for ref in h["dummies"]],
                         "prefabs": [[path, file_sha(project, path), meta_guid(project, path)] for path in h["prefabs"]],
                         "scripts": cs_files(project)},
                "digests": {}}

    def prompt_vars(self, ctx):
        h = ctx.handles
        cfg_src, ai_src = sources(h)
        return {"scripts": ctx.asset("Scripts"), "cfg_class": class_name(h["configScript"]),
                "ai_class": class_name(h["aiScript"]), "cfg_src": "\n```csharp\n%s```" % cfg_src,
                "ai_src": "\n```csharp\n%s```" % ai_src, "asset": h["configAsset"], "prefab": h["projectilePrefab"],
                "turrets": h["turretsPath"], "range": h["rangePath"]}

    def answer_schema(self):
        return {
            "type": "object",
            "required": ["scriptPaths", "configAssetPath", "turretInstanceId", "dummyInstanceId"],
            "properties": {
                "scriptPaths": {"type": "array", "items": {"type": "string", "desc": {
                    "zh-CN": "脚本的资产路径", "en": "asset path of a script"}}},
                "configAssetPath": {"type": "string", "desc": {"zh-CN": "配置资产的路径", "en": "path of the config asset"}},
                "turretInstanceId": {"type": "integer", "desc": {
                    "zh-CN": "添加了组件的 Turret 的 instanceId（Unity 6000.4+ 填 entityId）",
                    "en": "instanceId of the Turret that got the component (entityId on Unity 6000.4+)"}},
                "dummyInstanceId": {"type": "integer", "desc": {
                    "zh-CN": "target 所指 Dummy 的 instanceId（Unity 6000.4+ 填 entityId）",
                    "en": "instanceId of the Dummy that target points at (entityId on Unity 6000.4+)"}},
            },
        }


TASK = T1()

# Producer:Betsy
