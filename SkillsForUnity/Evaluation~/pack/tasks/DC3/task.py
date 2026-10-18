"""DC3: a MonoBehaviour to rename and move into a namespace, with two serialized fields renamed, while six instances
keep their data (dev, challenge).

Fixture: A/Scripts/<OldDir>/<Old>_<run>.cs compiled at build (a speed float, a Transform[] of waypoints, a pause float,
RouteLength() and LapSeconds()); R/<Route> > three waypoints; R/<Squad> > two plain objects with the component (own
values and waypoint lists), an instance of A/<PrefabDir>/<Unit>.prefab (root component pointing at the prefab's own
children P0 and P1) whose speed is overridden, and an instance of the variant A/<PrefabDir>/<Unit>_<Kind>.prefab,
which overrides the speed of the base. Names, field names, the namespace and class to move to, positions, values and
the order of the squad's children come from the seed.
"""
import hashlib
import os
import random

from usk_eval import verifylib as V
from usk_eval.task_api import PublicTask, address

OLD_NAMES = ("GuardBrain", "WatchRoutine", "SentryLogic", "PatrolUnit")
OLD_DIRS = ("Enemies", "Actors", "Mobs", "Creatures")
NAMESPACES = ("AI", "Units", "Agents", "Npc")
NEW_NAMES = ("Patroller", "Sentinel", "Warden", "Rover")
SPEED_PAIRS = (("walkSpeed", "moveSpeed"), ("pace", "speed"), ("velocity", "travelSpeed"))
ROUTE_PAIRS = (("points", "waypoints"), ("path", "route"), ("nodes", "stops"))
PAUSES = ("pauseTime", "dwellTime", "waitTime")
SQUADS = ("Squad", "Patrol", "Garrison")
ROUTES = ("Route", "Circuit", "Track")
GUARDS = (("GuardA", "GuardB"), ("Scout", "Lookout"), ("North", "South"))
UNITS = (("Drone", "Heavy"), ("Walker", "Elite"), ("Bot", "Armored"))
PREFAB_DIRS = ("Prefabs", "Spawnables", "Blueprints")
SPEEDS = (2.0, 2.5, 3.0, 3.5, 4.0, 4.5)
PAUSE_VALUES = (0.25, 0.75, 1.0, 1.25)
NAME_SALT = 0xDC03

OLD_SRC = """using UnityEngine;

public class {cls} : MonoBehaviour
{{
    public float {speed} = 1.5f;
    public Transform[] {route};
    public float {pause} = 0.5f;

    public float RouteLength()
    {{
        float total = 0f;
        for (int i = 1; i < {route}.Length; i++)
            total += Vector3.Distance({route}[i - 1].position, {route}[i].position);
        return total;
    }}

    public float LapSeconds()
    {{
        return RouteLength() / {speed} + {pause} * {route}.Length;
    }}
}}
"""


def names(seed):
    """The namespace and class to move to, a pure function of the seed (shared with the private module)."""
    rng = random.Random(seed ^ NAME_SALT)
    return {"namespace": "Game." + rng.choice(NAMESPACES), "class": rng.choice(NEW_NAMES)}


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


def declares(project_path, path, namespace, cls):
    try:
        with open(os.path.join(project_path, *path.split("/")), "r", encoding="utf-8-sig") as handle:
            text = handle.read()
    except (OSError, UnicodeDecodeError):
        return False
    return ("namespace " + namespace) in text and ("class " + cls) in text


def stored(values, speed, route, pause):
    """{speed, pause, links} from serialized properties read with the original field names."""
    size = V.as_int(values.get(route + ".Array.size")) or 0
    return {"speed": values.get(speed), "pause": values.get(pause),
            "links": [values.get("%s.Array.data[%d]" % (route, index)) for index in range(size)]}


class DC3(PublicTask):
    id = "DC3"
    track = "dev"
    tier = "challenge"
    case_version = 1
    minutes = 45
    requires = {"pipeline": "URP", "mode": "bypass", "dedicated": True, "packages": []}
    needs_focus = True

    def build(self, ctx):
        c, rng, project = ctx.c, ctx.rng, ctx.project_path
        moved = names(ctx.seed)
        clash = [path for path in cs_files(project) if declares(project, path, moved["namespace"], moved["class"])]
        if clash:
            raise RuntimeError("DC3 fixture: %s.%s already exists (residue of an earlier run?): %s" % (
                moved["namespace"], moved["class"], ", ".join(clash)))
        old = "%s_%s" % (rng.choice(OLD_NAMES), ctx.scope.run_id)
        speed, route = rng.choice(SPEED_PAIRS)[0], rng.choice(ROUTE_PAIRS)[0]
        pause = rng.choice(PAUSES)
        squad, route_root = rng.choice(SQUADS), rng.choice(ROUTES)
        guard_a, guard_b = rng.choice(GUARDS)
        unit, kind = rng.choice(UNITS)
        prefab_dir = ctx.asset(rng.choice(PREFAB_DIRS))
        speeds, pauses = rng.sample(SPEEDS, 5), rng.sample(PAUSE_VALUES, 3)
        old_dir = ctx.asset("Scripts", rng.choice(OLD_DIRS))
        prefabs_first = rng.random() < 0.5

        res = c.write("script_create", scriptName=old, folder=old_dir,
                      content=OLD_SRC.format(cls=old, speed=speed, route=route, pause=pause))
        if isinstance(res, dict) and res.get("jobId"):
            ctx.wait_job(res["jobId"])
        ctx.wait_idle()
        old_path = "%s/%s.cs" % (old_dir, old)
        if not ctx.probe.guid(old_path):
            raise RuntimeError("DC3 fixture: the script was not imported")

        def make(name, parent, **args):
            args.update(address(parent, "parent"))
            return V.object_id(c.write("gameobject_create", name=name, **args))

        def setup(ref, speed_value, pause_value, links):
            c.write("component_add", componentType=old, **address(ref))
            for field, value in ((speed, speed_value), (pause, pause_value)):
                c.write("component_set_serialized_property", componentType=old, propertyPath=field, value=str(value),
                        **address(ref))
            c.write("component_set_serialized_property", componentType=old, propertyPath=route + ".Array.size",
                    value=str(len(links)), **address(ref))
            for index, link in enumerate(links):
                c.write("component_set_serialized_property", componentType=old,
                        propertyPath="%s.Array.data[%d]" % (route, index), referencePath=link, objectType="Transform",
                        **address(ref))

        track = make(route_root, ctx.root, x=rng.randint(-4, 4), y=0, z=rng.randint(-4, 4))
        stops = []
        for index in range(3):
            make("WP_%d" % (index + 1), track, x=rng.randint(-6, 6), y=0, z=rng.randint(-6, 6))
            stops.append(ctx.path(route_root, "WP_%d" % (index + 1)))
        team = make(squad, ctx.root, x=0, y=0, z=0)

        def plain():
            out = []
            for name, value, wait, links in ((guard_a, speeds[0], pauses[0], stops),
                                             (guard_b, speeds[1], pauses[1], [stops[2], stops[0]])):
                ref = make(name, team, x=rng.randint(-5, 5), y=0, z=rng.randint(-5, 5))
                setup(ref, value, wait, links)
                out.append(ref)
            return out

        def prefabs():
            ref = make(unit, team, x=rng.randint(-5, 5), y=0, z=rng.randint(-5, 5))
            for index in range(2):
                make("P%d" % index, ref, x=rng.randint(1, 4) * (1 if index else -1), y=0, z=rng.randint(1, 4))
            setup(ref, speeds[3], pauses[2], [ctx.path(squad, unit, "P0"), ctx.path(squad, unit, "P1")])
            base = "%s/%s.prefab" % (prefab_dir, unit)
            c.write("asset_create_folder", folderPath=prefab_dir)
            c.write("prefab_create", savePath=base, path=ctx.path(squad, unit))
            c.write("component_set_serialized_property", componentType=old, propertyPath=speed, value=str(speeds[2]),
                    **address(ref))
            variant = "%s/%s_%s.prefab" % (prefab_dir, unit, kind)
            c.write("prefab_create_variant", sourcePrefabPath=base, variantPath=variant)
            c.write("prefab_set_serialized_property", prefabPath=variant, componentType=old, propertyPath=speed,
                    value=str(speeds[4]))
            made = c.write("prefab_instantiate", prefabPath=variant, parentPath=ctx.path(squad),
                           name="%s_%s" % (unit, kind), x=rng.randint(-5, 5), y=0, z=rng.randint(-5, 5))
            return [ref, V.object_id(made)], base, variant

        if prefabs_first:
            (inst, inst_variant), base, variant = prefabs()
            first, second = plain()
        else:
            first, second = plain()
            (inst, inst_variant), base, variant = prefabs()
        ctx.wait_idle()
        return {"oldPath": old_path, "newPath": ctx.asset("Scripts", *(moved["namespace"].split(".") +
                                                                        [moved["class"] + ".cs"])),
                "fields": [speed, route, pause], "routeRoot": track, "squad": team, "guardA": first,
                "guardB": second, "unit": inst, "unitVariant": inst_variant,
                "holders": [first, second, inst, inst_variant], "basePrefab": base, "variantPrefab": variant}

    def snapshot(self, ctx):
        p, h, project = ctx.probe, ctx.handles, ctx.project_path
        old = os.path.basename(h["oldPath"])[:-len(".cs")]
        speed, route, pause = h["fields"]
        holders = []
        for ref in h["holders"]:
            data = stored(p.ser(ref, old) or {}, speed, route, pause)
            owners = [p.owner(link) for link in data["links"]]
            holders.append({"ref": ref, "speed": data["speed"], "pause": data["pause"], "links": owners,
                            "spots": [(V.go_state(p, owner) or {}).get("position") for owner in owners]})
        assets = {}
        for key in ("basePrefab", "variantPrefab"):
            res = ctx.read("prefab_get_serialized_properties", prefabPath=h[key], componentType=old)
            data = stored({row.get("propertyPath"): row.get("value") for row in res.get("properties") or []},
                          speed, route, pause)
            assets[key] = {"speed": data["speed"], "pause": data["pause"],
                           "links": [(V.parse_ref(link) or (None, None, None))[2] for link in data["links"]]}
        return {"data": {"guid": p.guid(h["oldPath"]), "holders": holders, "assets": assets,
                         "objects": subtree_states(p, h["routeRoot"]) + subtree_states(p, h["squad"]),
                         "scripts": [[path, file_sha(project, path)] for path in cs_files(project)]},
                "digests": {}}

    def prompt_vars(self, ctx):
        h = ctx.handles
        moved = names(ctx.seed)
        speed, route, pause = h["fields"]
        renamed = renames(h["fields"])

        def where(ref):
            return (ctx.probe.go(ref) or {}).get("path")

        return {"oldCls": os.path.basename(h["oldPath"])[:-len(".cs")], "oldPath": h["oldPath"],
                "newFull": "%s.%s" % (moved["namespace"], moved["class"]), "newCls": moved["class"],
                "ns": moved["namespace"], "newPath": h["newPath"], "oldSpeed": speed, "newSpeed": renamed[0],
                "oldRoute": route, "newRoute": renamed[1], "pause": pause, "guardA": where(h["guardA"]),
                "guardB": where(h["guardB"]), "unitInst": where(h["unit"]), "variantInst": where(h["unitVariant"]),
                "basePrefab": h["basePrefab"], "variantPrefab": h["variantPrefab"]}

    def answer_schema(self):
        return {
            "type": "object",
            "required": ["scriptPath", "scriptGuid", "className"],
            "properties": {
                "scriptPath": {"type": "string", "desc": {"zh-CN": "改动后脚本的资产路径",
                                                          "en": "asset path of the script after the change"}},
                "scriptGuid": {"type": "string", "desc": {"zh-CN": "该脚本当前的 GUID",
                                                          "en": "the script's current GUID"}},
                "className": {"type": "string", "desc": {"zh-CN": "类的完整名称（含命名空间）",
                                                         "en": "full name of the class, namespace included"}},
            },
        }


def renames(fields):
    """The new names of the speed and route fields."""
    speed, route = fields[0], fields[1]
    return (dict(SPEED_PAIRS)[speed], dict(ROUTE_PAIRS)[route])


TASK = DC3()

# Producer:Betsy
