"""DS1: a patrol script that walks a guard along the waypoints of a route, pausing at each, in a loop (dev, standard).

Fixture: R/<Post> (seeded position and yaw) > <Guard>, a capsule standing on the first waypoint; R/<Route> (seeded
position and yaw) > WP_1..WP_4, empty objects on a seeded rectangle. The script's class name carries a seeded word
and the run id; the speed and pause the scene must carry come from the seed as well.
"""
import os
import random

from usk_eval import verifylib as V
from usk_eval.task_api import PublicTask, address

WORDS = ("Kestrel", "Talon", "Vortex", "Ember", "Quill", "Harrow")
AREA_NAMES = (("Post", "Guard", "Route"), ("Dock", "Sentry", "Path"), ("Camp", "Drone", "Loop"))
YAWS = (20, 35, 50, 65, 110, 140, -30, -55)
WAYPOINT_COUNT = 4


def patrol_spec(seed):
    """Speed (world units per second) and pause (seconds) the scene must carry, from the seed alone."""
    rng = random.Random("DS1:%d" % seed)
    return {"speed": rng.choice((2.0, 2.5, 3.0)), "pause": rng.choice((0.5, 0.75, 1.0))}


def cs_files(project_path):
    out = []
    for base, dirs, files in os.walk(os.path.join(project_path, "Assets")):
        dirs.sort()
        out += [os.path.relpath(os.path.join(base, name), project_path).replace(os.sep, "/")
                for name in sorted(files) if name.endswith(".cs")]
    return sorted(out)


class DS1(PublicTask):
    id = "DS1"
    track = "dev"
    tier = "standard"
    case_version = 1
    minutes = 30
    requires = {"pipeline": "URP", "mode": "bypass", "dedicated": True, "packages": []}
    needs_focus = True

    def build(self, ctx):
        c, rng = ctx.c, ctx.rng
        post_name, guard_name, route_name = rng.choice(AREA_NAMES)
        cls = "Patroller_%s_%s" % (rng.choice(WORDS), ctx.scope.run_id)

        def make(name, parent, **args):
            args.update(address(parent, "parent"))
            return V.object_id(c.write("gameobject_create", name=name, **args))

        length, width = rng.choice((3, 4, 5)), rng.choice((3, 4))
        corners = ((0, 0, 0), (length, 0, 0), (length, 0, width), (0, 0, width))
        route = make(route_name, ctx.root, x=rng.randint(-6, 6), y=0.5, z=rng.randint(-6, 6), rotY=rng.choice(YAWS))
        waypoints = [make("WP_%d" % (index + 1), route, x=x, y=y, z=z) for index, (x, y, z) in enumerate(corners)]
        post = make(post_name, ctx.root, x=rng.randint(-6, 6), y=0, z=rng.randint(-6, 6), rotY=rng.choice(YAWS))
        first = V.floats((ctx.probe.go(waypoints[0]) or {}).get("position"))
        guard = make(guard_name, post, primitiveType="Capsule", x=first[0], y=first[1], z=first[2], space="world")
        return {"post": post, "postPath": ctx.path(post_name), "guard": guard,
                "guardPath": ctx.path(post_name, guard_name), "route": route, "routePath": ctx.path(route_name),
                "waypoints": waypoints, "script": ctx.asset("Scripts", cls + ".cs")}

    def snapshot(self, ctx):
        p, h = ctx.probe, ctx.handles
        return {"data": {"guard": V.go_state(p, h["guard"]), "post": V.go_state(p, h["post"]),
                         "route": V.go_state(p, h["route"]),
                         "waypoints": [[ref, V.go_state(p, ref)] for ref in h["waypoints"]],
                         "scripts": cs_files(ctx.project_path)},
                "digests": {}}

    def prompt_vars(self, ctx):
        h, spec = ctx.handles, patrol_spec(ctx.seed)
        return {"scripts": ctx.asset("Scripts"), "cls": os.path.basename(h["script"])[:-len(".cs")],
                "guard": h["guardPath"], "route": h["routePath"], "speed": spec["speed"], "pause": spec["pause"]}

    def answer_schema(self):
        return {
            "type": "object",
            "required": ["scriptPath", "guardPath"],
            "properties": {
                "scriptPath": {"type": "string", "desc": {"zh-CN": "脚本的资产路径", "en": "asset path of the script"}},
                "guardPath": {"type": "string", "desc": {"zh-CN": "挂载了组件的对象的完整层级路径",
                                                         "en": "full hierarchy path of the object carrying the "
                                                               "component"}},
            },
        }


TASK = DS1()

# Producer:Betsy
