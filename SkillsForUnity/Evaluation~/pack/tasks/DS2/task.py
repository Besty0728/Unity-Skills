"""DS2: a trigger-activated sliding door with a kinematic Rigidbody player and a non-Player visitor (dev, standard).

Fixture:
- A/Scripts/<Walker>_<run>.cs compiled at build: moves its GameObject along a Transform[] waypoints array at a given
  speed, pausing at each waypoint for pauseSeconds (no pause at the start).
- R/<Hall> (seeded offset and yaw) > <Gate> (Cube door panel), <Zone> (Cube trigger volume with BoxCollider,
  isTrigger initially false), <TrackP> > P0, P1, P2 (three waypoints that cross the zone and return, so the player
  enters and leaves the zone twice), <TrackV> > V0, V1 (two waypoints that cross the zone first while the player is
  still approaching), <Hero> (Capsule tagged Player, no Rigidbody initially, with the walker component moving along
  P0->P1->P2->P0), and <Guest> (Capsule tagged Untagged, with a kinematic Rigidbody and the walker component moving
  along V0->V1).
- The task script class name, openOffset, speed and the object names come from the seed.
"""
import hashlib
import json
import os
import random

from usk_eval import verifylib as V
from usk_eval.task_api import PublicTask, address

WORDS = ("Kestrel", "Talon", "Vortex", "Ember", "Quill", "Harrow")
HALL_NAMES = (
    ("Corridor", "BlastDoor", "SensorZone", "Agent", "Courier"),
    ("Airlock", "Bulkhead", "ScanField", "Pilot", "Drifter"),
    ("Vault", "Shutter", "GateZone", "Runner", " Bystander".strip()),
)
YAWS = (25, 40, 55, 70, -30, -45, -65)

WALKER_SRC = """using UnityEngine;

public class {cls} : MonoBehaviour
{{
    public Transform[] waypoints;
    public float speed = 4f;
    public float pauseSeconds = 0.5f;

    private int _next = 1;
    private float _waited = -1f;

    void Update()
    {{
        if (waypoints == null || waypoints.Length == 0) return;
        if (_waited >= 0f)
        {{
            _waited += Time.deltaTime;
            if (_waited < pauseSeconds) return;
            _waited = -1f;
            _next = (_next + 1) % waypoints.Length;
        }}
        Vector3 goal = waypoints[_next % waypoints.Length].position;
        transform.position = Vector3.MoveTowards(transform.position, goal, speed * Time.deltaTime);
        if (transform.position == goal) _waited = 0f;
    }}
}}
"""


def door_spec(seed):
    """Open offset (local X, Y, Z) and slide speed (units/s) the scene must carry, from the seed alone."""
    rng = random.Random("DS2:%d" % seed)
    offsets = ((0.0, 3.0, 0.0), (0.0, 3.6, 0.0), (3.0, 0.0, 0.0), (-3.0, 0.0, 0.0))
    ox, oy, oz = rng.choice(offsets)
    return {"ox": ox, "oy": oy, "oz": oz, "speed": rng.choice((6.0, 7.5, 9.0))}


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


class DS2(PublicTask):
    id = "DS2"
    track = "dev"
    tier = "standard"
    case_version = 1
    minutes = 30
    requires = {"pipeline": "URP", "mode": "bypass", "dedicated": True, "packages": []}
    needs_focus = True

    def build(self, ctx):
        c, rng = ctx.c, ctx.rng
        hall_name, door_name, zone_name, player_name, visitor_name = rng.choice(HALL_NAMES)
        word = rng.choice(WORDS)
        door_cls = "TriggerDoor_%s_%s" % (word, ctx.scope.run_id)
        walker_cls = "PathMover_%s_%s" % (word, ctx.scope.run_id)
        scripts_dir = ctx.asset("Scripts")

        res = c.write("script_create", scriptName=walker_cls, folder=scripts_dir,
                      content=WALKER_SRC.format(cls=walker_cls))
        if isinstance(res, dict) and res.get("jobId"):
            ctx.wait_job(res["jobId"])
        ctx.wait_idle()
        walker_script = "%s/%s.cs" % (scripts_dir, walker_cls)

        def make(name, parent, **args):
            args.update(address(parent, "parent"))
            return V.object_id(c.write("gameobject_create", name=name, **args))

        hall = make(hall_name, ctx.root, x=rng.randint(-4, 4), y=0, z=rng.randint(-4, 4), rotY=rng.choice(YAWS))
        door = make(door_name, hall, primitiveType="Cube", x=rng.choice((-1.5, 0.0, 1.5)), y=1.5, z=0.0)
        zone = make(zone_name, hall, primitiveType="Cube", x=0.0, y=1.0, z=0.0)
        c.write("gameobject_set_transform", scaleX=4.0, scaleY=3.0, scaleZ=4.0, **address(zone))

        track_p = make("TrackP", hall, x=0, y=0, z=0)
        p_coords = ((0.0, 1.0, -7.0), (0.0, 1.0, 0.0), (0.0, 1.0, 5.0))
        p_wps = [make("P%d" % idx, track_p, x=x, y=y, z=z) for idx, (x, y, z) in enumerate(p_coords)]
        p_paths = [ctx.path(hall_name, "TrackP", "P%d" % idx) for idx in range(len(p_coords))]

        track_v = make("TrackV", hall, x=0, y=0, z=0)
        v_coords = ((-5.0, 1.0, 0.0), (5.0, 1.0, 0.0))
        v_wps = [make("V%d" % idx, track_v, x=x, y=y, z=z) for idx, (x, y, z) in enumerate(v_coords)]
        v_paths = [ctx.path(hall_name, "TrackV", "V%d" % idx) for idx in range(len(v_coords))]

        p0_world = V.floats((ctx.probe.go(p_wps[0]) or {}).get("position"))
        v0_world = V.floats((ctx.probe.go(v_wps[0]) or {}).get("position"))

        player = make(player_name, hall, primitiveType="Capsule",
                      x=p0_world[0], y=p0_world[1], z=p0_world[2], space="world")
        c.write("gameobject_set_tag_batch", items=json.dumps([dict({"tag": "Player"}, **address(player))]))

        visitor = make(visitor_name, hall, primitiveType="Capsule",
                       x=v0_world[0], y=v0_world[1], z=v0_world[2], space="world")
        c.write("component_add", componentType="Rigidbody", **address(visitor))
        c.write("component_set_property", componentType="Rigidbody", propertyName="useGravity", value="false",
                **address(visitor))
        c.write("component_set_property", componentType="Rigidbody", propertyName="isKinematic", value="true",
                **address(visitor))

        def setup_walker(ref, links, speed_val, pause_val):
            c.write("component_add", componentType=walker_cls, **address(ref))
            c.write("component_set_property", componentType=walker_cls, propertyName="speed", value=str(speed_val),
                    **address(ref))
            c.write("component_set_property", componentType=walker_cls, propertyName="pauseSeconds",
                    value=str(pause_val), **address(ref))
            c.write("component_set_serialized_property", componentType=walker_cls, propertyPath="waypoints.Array.size",
                    value=str(len(links)), **address(ref))
            for idx, link in enumerate(links):
                c.write("component_set_serialized_property", componentType=walker_cls,
                        propertyPath="waypoints.Array.data[%d]" % idx, referencePath=link, objectType="Transform",
                        **address(ref))

        setup_walker(player, [p_paths[0], p_paths[1], p_paths[2], p_paths[1]], 5.0, 1.0)
        setup_walker(visitor, [v_paths[0], v_paths[1]], 10.0, 20.0)
        ctx.wait_idle()

        return {
            "hall": hall, "hallPath": ctx.path(hall_name),
            "door": door, "doorPath": ctx.path(hall_name, door_name),
            "zone": zone, "zonePath": ctx.path(hall_name, zone_name),
            "player": player, "playerPath": ctx.path(hall_name, player_name),
            "visitor": visitor, "visitorPath": ctx.path(hall_name, visitor_name),
            "trackP": track_p, "pWaypoints": p_wps,
            "trackV": track_v, "vWaypoints": v_wps,
            "walkerScript": walker_script,
            "script": "%s/%s.cs" % (scripts_dir, door_cls),
        }

    def snapshot(self, ctx):
        p, h, project = ctx.probe, ctx.handles, ctx.project_path
        return {
            "data": {
                "hall": V.go_state(p, h["hall"]),
                "door": V.go_state(p, h["door"]),
                "doorLocalPos": V.local_trs(p, h["door"])["position"],
                "zone": V.go_state(p, h["zone"]),
                "player": V.go_state(p, h["player"]),
                "visitor": V.go_state(p, h["visitor"]),
                "trackP": V.go_state(p, h["trackP"]),
                "trackV": V.go_state(p, h["trackV"]),
                "pWaypoints": [[ref, V.go_state(p, ref)] for ref in h["pWaypoints"]],
                "vWaypoints": [[ref, V.go_state(p, ref)] for ref in h["vWaypoints"]],
                "scripts": [[path, file_sha(project, path)] for path in cs_files(project)],
            },
            "digests": {},
        }

    def prompt_vars(self, ctx):
        h, spec = ctx.handles, door_spec(ctx.seed)

        def fmt(val):
            return int(val) if float(val).is_integer() else val

        return {
            "scripts": ctx.asset("Scripts"),
            "cls": os.path.basename(h["script"])[:-len(".cs")],
            "zone": h["zonePath"],
            "door": h["doorPath"],
            "player": h["playerPath"],
            "visitor": h["visitorPath"],
            "ox": fmt(spec["ox"]),
            "oy": fmt(spec["oy"]),
            "oz": fmt(spec["oz"]),
            "speed": fmt(spec["speed"]),
        }

    def answer_schema(self):
        return {
            "type": "object",
            "required": ["scriptPath", "zonePath", "doorPath", "playerPath"],
            "properties": {
                "scriptPath": {"type": "string", "desc": {"zh-CN": "新建脚本的资产路径",
                                                          "en": "asset path of the new script"}},
                "zonePath": {"type": "string", "desc": {"zh-CN": "挂载了触发门组件的触发区对象的完整层级路径",
                                                        "en": "full hierarchy path of the trigger zone object carrying "
                                                              "the door component"}},
                "doorPath": {"type": "string", "desc": {"zh-CN": "门面板对象的完整层级路径",
                                                        "en": "full hierarchy path of the door panel object"}},
                "playerPath": {"type": "string", "desc": {"zh-CN": "Player 对象的完整层级路径",
                                                          "en": "full hierarchy path of the Player object"}},
            },
        }


TASK = DS2()

# Producer:Betsy
