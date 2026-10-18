"""T9: rename two serialized fields of a script while the values stored on its three users survive (dev, standard).

Fixture: A/Scripts/Patrol_<k>.cs {float speed = 1f; Transform waypoint; int laps = 1} and A/Scripts/PatrolRoute_<k>.cs
{float speed = 2f; bool loop = true} (k = seeded word + run id), both compiled at build; R/Waypoints with W1, W2, W3;
R/Guards with Guard_1, Guard_2, Guard_3 capsules, each with the Patrol component, speeds 3.25 / 7.5 / 11.125, laps
2 / 3 / 4 and a waypoint reference in a seeded permutation of W1..W3; R/Route with the PatrolRoute component at
speed 5.5.
"""
import hashlib
import os

from usk_eval import verifylib as V
from usk_eval.task_api import PublicTask, address

SPEEDS = (3.25, 7.5, 11.125)
LAPS = (2, 3, 4)
ROUTE_SPEED = 5.5
WORDS = ("Heron", "Lynx", "Moth", "Otter", "Wren", "Ibex")

PATROL_SRC = """using UnityEngine;

public class {cls} : MonoBehaviour
{{
    public float speed = 1f;
    public Transform waypoint;
    public int laps = 1;
}}
"""

ROUTE_SRC = """using UnityEngine;

public class {cls} : MonoBehaviour
{{
    public float speed = 2f;
    public bool loop = true;
}}
"""


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


def meta_guid(project_path, asset_path):
    try:
        return V.uyaml.meta_guid(os.path.join(project_path, *asset_path.split("/")))
    except OSError:
        return None


class T9(PublicTask):
    id = "T9"
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
        patrol, route = "Patrol_" + k, "PatrolRoute_" + k
        for name, source in ((patrol, PATROL_SRC), (route, ROUTE_SRC)):
            res = c.write("script_create", scriptName=name, folder=ctx.asset("Scripts"),
                          content=source.format(cls=name))
            if res.get("jobId"):
                ctx.wait_job(res["jobId"])
            ctx.wait_idle()
        points_root = make("Waypoints", ctx.root)
        points = [make("W%d" % (index + 1), points_root, x=-6 + 6 * index, y=0, z=8) for index in range(3)]
        order = list(range(3))
        rng.shuffle(order)
        guards_root = make("Guards", ctx.root)
        guards = []
        for index in range(3):
            guard = make("Guard_%d" % (index + 1), guards_root, primitiveType="Capsule", x=-4 + 4 * index, y=1, z=0)
            at = address(guard)
            c.write("component_add", componentType=patrol, **at)
            c.write("component_set_property", componentType=patrol, propertyName="speed", value=repr(SPEEDS[index]),
                    **at)
            c.write("component_set_serialized_property", componentType=patrol, propertyPath="waypoint",
                    referencePath=ctx.path("Waypoints", "W%d" % (order[index] + 1)), objectType="Transform", **at)
            c.write("component_set_property", componentType=patrol, propertyName="laps", value=str(LAPS[index]), **at)
            guards.append(guard)
        route_obj = make("Route", ctx.root, x=0, y=0, z=12)
        c.write("component_add", componentType=route, **address(route_obj))
        c.write("component_set_property", componentType=route, propertyName="speed", value=repr(ROUTE_SPEED),
                **address(route_obj))
        return {"guardsPath": ctx.path("Guards"), "patrolScript": ctx.asset("Scripts", patrol + ".cs"),
                "routeScript": ctx.asset("Scripts", route + ".cs"), "guards": guards, "waypoints": points,
                "route": route_obj}

    def snapshot(self, ctx):
        p, h, project = ctx.probe, ctx.handles, ctx.project_path
        patrol = class_name(h["patrolScript"])
        rows = []
        for ref in h["guards"]:
            values = p.ser(ref, patrol) or {}
            rows.append([ref, {"speed": values.get("speed"), "laps": values.get("laps"),
                               "waypoint": p.owner(values.get("waypoint"))}])
        return {"data": {"objects": [[ref, V.go_state(p, ref)] for ref in h["guards"] + h["waypoints"] + [h["route"]]],
                         "guards": rows, "route": p.ser(h["route"], class_name(h["routeScript"])),
                         "patrolGuid": meta_guid(project, h["patrolScript"]),
                         "scripts": [[path, file_sha(project, path)] for path in cs_files(project)]},
                "digests": {}}

    def prompt_vars(self, ctx):
        h = ctx.handles
        return {"path": h["patrolScript"], "cls": class_name(h["patrolScript"]), "guards": h["guardsPath"]}

    def answer_schema(self):
        return {
            "type": "object",
            "required": ["scriptPath", "guards"],
            "properties": {
                "scriptPath": {"type": "string", "desc": {"zh-CN": "改名后脚本的资产路径",
                                                          "en": "asset path of the renamed script"}},
                "guards": {"type": "array", "items": {"type": "object", "properties": {
                    "instanceId": {"type": "integer", "desc": {
                        "zh-CN": "Guard 的 instanceId（Unity 6000.4+ 填 entityId）",
                        "en": "instanceId of the Guard (entityId on Unity 6000.4+)"}},
                    "moveSpeed": {"type": "number", "desc": {"zh-CN": "该 Guard 的 moveSpeed 值",
                                                             "en": "moveSpeed value of that Guard"}},
                    "waypointInstanceId": {"type": "integer", "desc": {
                        "zh-CN": "该 Guard 的 patrolTarget 所引用的 GameObject 的 instanceId（Unity 6000.4+ 填 entityId）",
                        "en": "instanceId of the GameObject that this Guard's patrolTarget references "
                              "(entityId on Unity 6000.4+)"}},
                }}},
            },
        }


TASK = T9()

# Producer:Betsy
