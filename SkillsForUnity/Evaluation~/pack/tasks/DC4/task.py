"""DC4: a ballistic launcher that fires a Rigidbody projectile from a rotated muzzle to hit any given Transform pad
within 0.25 m on its first impact, accounting for mass, linear drag, project gravity and fixedDeltaTime (dev,
challenge).

Fixture: R/<Range> (seeded position and yaw) > <Battery> (seeded position and yaw) > <Muzzle> (seeded local offset,
pitch and yaw); R/<Range>/<Deck> (seeded position and yaw) > three cylindrical landing pads at distinct world heights
and distances; R/<Range>/<Stand> (a second rig in the range); A/Prefabs/<Shell>_<k>.prefab (a Sphere with a Rigidbody
whose mass and linear drag are set on the prefab asset, plus an IsTrigger=false SphereCollider); and a seeded project
gravity vector set via physics_set_gravity. Class name, object names, prefab parameters, gravity and pad coordinates
come from the seed.
"""
import hashlib
import os
import random

from usk_eval import verifylib as V
from usk_eval.task_api import PublicTask, address

WORDS = ("Kestrel", "Talon", "Vortex", "Ember", "Quill", "Harrow")
RIG_NAMES = (
    ("Range", "Battery", "Muzzle", "Deck", "Stand", "Shell", "Beacon"),
    ("Field", "Turret", "Barrel", "Course", "Spare", "Round", "Pad"),
    ("Proving", "Mount", "Nozzle", "Gallery", "Reserve", "Orb", "Plate"),
)
YAWS = (25, 40, 55, 70, 115, 145, -35, -60)
PITCHES = (-12, -8, 10, 14, 18)


def launcher_spec(seed):
    """Rigidbody mass, linear drag and project gravity (x, y, z) from the seed alone."""
    rng = random.Random("DC4:%d" % seed)
    return {
        "mass": rng.choice((1.5, 2.0, 2.5, 3.0)),
        "drag": rng.choice((0.35, 0.45, 0.55)),
        "gravityY": rng.choice((-12.0, -14.0, -15.0, -16.0)),
    }


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


class DC4(PublicTask):
    id = "DC4"
    track = "dev"
    tier = "challenge"
    case_version = 1
    minutes = 45
    requires = {"pipeline": "URP", "mode": "bypass", "dedicated": True, "packages": []}
    needs_focus = True

    def build(self, ctx):
        c, rng = ctx.c, ctx.rng
        spec = launcher_spec(ctx.seed)
        range_name, battery_name, muzzle_name, deck_name, stand_name, shell_name, pad_prefix = rng.choice(RIG_NAMES)
        k = "%s_%s" % (rng.choice(WORDS), ctx.scope.run_id)
        cls = "BallisticLauncher_%s" % k

        old_gravity = c.read("physics_get_gravity") or {}
        gy_before = float(old_gravity.get("y", -9.81))
        gx_before = float(old_gravity.get("x", 0.0))
        gz_before = float(old_gravity.get("z", 0.0))
        c.write("physics_set_gravity", x=0.0, y=spec["gravityY"], z=0.0)

        for folder in ("Prefabs", "Scripts"):
            c.write("asset_create_folder", folderPath=ctx.asset(folder))

        def make(name, parent, **args):
            args.update(address(parent, "parent"))
            return V.object_id(c.write("gameobject_create", name=name, **args))

        prefab_path = ctx.asset("Prefabs", "%s_%s.prefab" % (shell_name, k))
        seed_obj = make("%s_%s" % (shell_name, k), ctx.root, primitiveType="Sphere",
                        scaleX=0.2, scaleY=0.2, scaleZ=0.2)
        c.write("component_add", componentType="Rigidbody", **address(seed_obj))
        c.write("component_set_property", componentType="Rigidbody", propertyName="mass",
                value=str(spec["mass"]), **address(seed_obj))
        c.write("component_set_serialized_property", componentType="Rigidbody", propertyPath="m_Drag",
                value=str(spec["drag"]), **address(seed_obj))
        c.write("component_set_serialized_property", componentType="Rigidbody", propertyPath="m_AngularDrag",
                value="0.05", **address(seed_obj))
        c.write("component_set_property", componentType="Rigidbody", propertyName="useGravity",
                value="true", **address(seed_obj))
        c.write("component_set_property", componentType="Rigidbody", propertyName="isKinematic",
                value="false", **address(seed_obj))
        c.write("prefab_create", savePath=prefab_path, **address(seed_obj))
        c.write("gameobject_delete", **address(seed_obj))

        arena = make(range_name, ctx.root, x=rng.randint(-3, 3), y=0.0, z=rng.randint(-3, 3),
                     rotY=rng.choice(YAWS))
        stand_first = rng.random() < 0.5

        def make_stand():
            stand = make(stand_name, arena, x=rng.choice((-5.0, 5.0)), y=0.0, z=rng.choice((-4.0, 4.0)),
                         rotY=rng.choice(YAWS))
            stand_muzzle = make(muzzle_name, stand, x=0.0, y=1.2, z=0.4, rotX=rng.choice(PITCHES),
                                rotY=rng.choice(YAWS))
            return stand, stand_muzzle

        def make_battery():
            battery = make(battery_name, arena, x=rng.choice((-2.0, 2.0)), y=0.5, z=rng.choice((-2.0, 2.0)),
                           rotY=rng.choice(YAWS))
            muzzle = make(muzzle_name, battery, x=0.4, y=1.3, z=0.6, rotX=rng.choice(PITCHES),
                          rotY=rng.choice(YAWS))
            return battery, muzzle

        if stand_first:
            stand, stand_muzzle = make_stand()
            battery, muzzle = make_battery()
        else:
            battery, muzzle = make_battery()
            stand, stand_muzzle = make_stand()

        deck = make(deck_name, arena, x=0.0, y=0.0, z=0.0, rotY=rng.choice(YAWS))
        muzzle_pos = V.floats((ctx.probe.go(muzzle) or {}).get("position"))
        heights = rng.sample((-0.6, 0.8, 2.2, 3.1), 3)
        offsets = ((rng.choice((6.5, 7.5)), rng.choice((-3.5, 3.5))),
                   (rng.choice((-7.5, -6.5)), rng.choice((4.0, 5.5))),
                   (rng.choice((4.5, 5.5)), rng.choice((-7.5, -6.0))))
        pads = []
        for index in range(3):
            dx, dz = offsets[index]
            hy = heights[index]
            pad = make("%s_%d" % (pad_prefix, index + 1), deck, primitiveType="Cylinder",
                       x=round(muzzle_pos[0] + dx, 2), y=round(muzzle_pos[1] + hy, 2),
                       z=round(muzzle_pos[2] + dz, 2), scaleX=1.2, scaleY=0.1, scaleZ=1.2, space="world")
            pads.append(pad)

        return {
            "arena": arena,
            "arenaPath": ctx.path(range_name),
            "battery": battery,
            "batteryPath": ctx.path(range_name, battery_name),
            "muzzle": muzzle,
            "muzzlePath": ctx.path(range_name, battery_name, muzzle_name),
            "stand": stand,
            "standPath": ctx.path(range_name, stand_name),
            "standMuzzle": stand_muzzle,
            "deck": deck,
            "deckPath": ctx.path(range_name, deck_name),
            "pads": pads,
            "padPaths": [ctx.path(range_name, deck_name, "%s_%d" % (pad_prefix, i + 1)) for i in range(3)],
            "prefab": prefab_path,
            "script": ctx.asset("Scripts", cls + ".cs"),
            "prevGravity": [gx_before, gy_before, gz_before],
        }

    def snapshot(self, ctx):
        p, h = ctx.probe, ctx.handles
        return {
            "data": {
                "arena": V.go_state(p, h["arena"]),
                "battery": V.go_state(p, h["battery"]),
                "muzzle": V.go_state(p, h["muzzle"]),
                "stand": V.go_state(p, h["stand"]),
                "standMuzzle": V.go_state(p, h["standMuzzle"]),
                "deck": V.go_state(p, h["deck"]),
                "pads": [[ref, V.go_state(p, ref)] for ref in h["pads"]],
                "prefab": {"sha": file_sha(ctx.project_path, h["prefab"]), "guid": p.guid(h["prefab"])},
                "gravity": ctx.read("physics_get_gravity"),
                "scripts": cs_files(ctx.project_path),
            },
            "digests": {},
        }

    def prompt_vars(self, ctx):
        h = ctx.handles
        cls = os.path.basename(h["script"])[:-len(".cs")]
        return {
            "scripts": ctx.asset("Scripts"),
            "cls": cls,
            "battery": h["batteryPath"],
            "muzzle": h["muzzlePath"],
            "prefab": h["prefab"],
        }

    def teardown(self, ctx):
        prev = (ctx.handles or {}).get("prevGravity")
        if isinstance(prev, list) and len(prev) == 3:
            ctx.c.write("physics_set_gravity", x=float(prev[0]), y=float(prev[1]), z=float(prev[2]))

    def answer_schema(self):
        return {
            "type": "object",
            "required": ["scriptPath", "launcherPath", "projectilePrefabPath"],
            "properties": {
                "scriptPath": {"type": "string", "desc": {"zh-CN": "脚本的资产路径", "en": "asset path of the script"}},
                "launcherPath": {"type": "string", "desc": {"zh-CN": "挂载了发射器组件的对象的完整层级路径",
                                                            "en": "full hierarchy path of the object carrying the "
                                                                  "launcher component"}},
                "projectilePrefabPath": {"type": "string", "desc": {"zh-CN": "弹丸预制体的资产路径",
                                                                    "en": "asset path of the projectile prefab"}},
            },
        }


TASK = DC4()

# Producer:Betsy
