"""DS1 solutions: reference, negatives (each with the exact set of assertions it must fail) and alternatives.

The C# sources differ in behaviour; each negative variant also carries a `// usk-fake: <variant>` comment, which the
offline fake editor reads to model the same wrong behaviour (a real editor ignores the comment).
"""
import os

from usk_eval import verifylib as V
from usk_eval.rest import SkillError
from usk_eval.task_api import Negative

PATROL_SRC = """using UnityEngine;
{marker}
public class {cls} : MonoBehaviour
{{
    public Transform route;
    public float speed = 1f;
    public float pauseSeconds = 0.25f;

    private int _next = 1;
    private float _waited = -1f;

    void Update()
    {{
        if (route == null || route.childCount == 0) return;
        if (_waited >= 0f)
        {{
            _waited += Time.deltaTime;
            if (_waited < pauseSeconds) return;
            _waited = -1f;
            {advance}
        }}
        Vector3 goal = route.GetChild(_next % route.childCount).{waypoint};
        transform.{self} = Vector3.MoveTowards(transform.{self}, goal, speed * Time.deltaTime);
        if (transform.{self} == goal) {arrive}
    }}
}}
"""

COROUTINE_SRC = """using System.Collections;
using UnityEngine;

public class {cls} : MonoBehaviour
{{
    public Transform route;
    public float speed = 1f;
    public float pauseSeconds = 0.25f;

    IEnumerator Start()
    {{
        if (route == null || route.childCount == 0) yield break;
        int next = 1;
        while (true)
        {{
            Vector3 goal = route.GetChild(next % route.childCount).position;
            while (transform.position != goal)
            {{
                transform.position = Vector3.MoveTowards(transform.position, goal, speed * Time.deltaTime);
                yield return null;
            }}
            yield return new WaitForSeconds(pauseSeconds);
            next = (next + 1) % route.childCount;
        }}
    }}
}}
"""

HELPER_SRC = """using UnityEditor;
using UnityEngine;

public static class {cls}
{{
    [MenuItem("Tools/{cls}")]
    public static void Run() {{ Debug.Log("patrol setup"); }}
}}
"""


def _source(cls, variant=None):
    """PATROL_SRC for the reference (variant None) or one of the wrong behaviours."""
    parts = {"marker": "// usk-fake: %s" % variant if variant else "", "cls": cls, "waypoint": "position",
             "self": "position", "advance": "_next = (_next + 1) % route.childCount;", "arrive": "_waited = 0f;"}
    if variant == "local-waypoints":
        parts["waypoint"] = "localPosition"
    elif variant == "local-position":
        parts["self"] = "localPosition"
    elif variant == "no-pause":
        parts["arrive"] = "_next = (_next + 1) % route.childCount;"
    elif variant == "no-loop":
        parts["advance"] = "if (_next + 1 < route.childCount) _next += 1; else enabled = false;"
    return PATROL_SRC.format(**parts)


def _create_script(ctx, name, content, folder=None):
    res = ctx.c.write("script_create", scriptName=name, folder=folder or ctx.asset("Scripts"), content=content)
    if res.get("jobId"):
        ctx.wait_job(res["jobId"])
    ctx.wait_idle()
    return res.get("path")


def _attach(ctx, target, speed=None, pause=None):
    """Adds the component and writes the scene values (None keeps the code default)."""
    cls = ctx.private.class_name(ctx.handles)
    at = V.address(target)
    ctx.c.write("component_add", componentType=cls, **at)
    ctx.c.write("component_set_serialized_property", componentType=cls, propertyPath="route",
                referencePath=ctx.handles["routePath"], objectType="Transform", **at)
    if speed is not None:
        ctx.c.write("component_set_property", componentType=cls, propertyName="speed", value=str(speed), **at)
    if pause is not None:
        ctx.c.write("component_set_property", componentType=cls, propertyName="pauseSeconds", value=str(pause), **at)


def _answer(ctx, target=None):
    info = ctx.probe.go(target if target is not None else ctx.handles["guard"]) or {}
    return {"scriptPath": ctx.handles["script"], "guardPath": info.get("path"), "declaredComplete": True}


def _solve(ctx, variant=None, target=None, speed=True, pause=True):
    spec = ctx.private.patrol_spec(ctx.seed)
    cls = ctx.private.class_name(ctx.handles)
    _create_script(ctx, cls, _source(cls, variant))
    _attach(ctx, target if target is not None else ctx.handles["guard"], spec["speed"] if speed else None,
            spec["pause"] if pause else None)
    return _answer(ctx)


def _write_file(ctx, asset_path, data):
    """Writes a file under A on disk, bypassing the skills (the runner's refresh imports it at submit)."""
    target = os.path.join(ctx.project_path, *asset_path.split("/"))
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, "wb") as handle:
        handle.write(data)


# ---------------------------------------------------------------- reference and alternatives


def reference(ctx):
    return _solve(ctx)


def coroutine_patrol(ctx):
    spec = ctx.private.patrol_spec(ctx.seed)
    cls = ctx.private.class_name(ctx.handles)
    _create_script(ctx, cls, COROUTINE_SRC.format(cls=cls))
    _attach(ctx, ctx.handles["guard"], spec["speed"], spec["pause"])
    return _answer(ctx)


def save_run_scene(ctx):
    answer = reference(ctx)
    ctx.c.write("scene_save")
    return answer


# ---------------------------------------------------------------- negatives


def local_waypoints(ctx):
    return _solve(ctx, variant="local-waypoints")


def local_position(ctx):
    return _solve(ctx, variant="local-position")


def no_pause(ctx):
    return _solve(ctx, variant="no-pause")


def no_loop(ctx):
    return _solve(ctx, variant="no-loop")


def defaults_only(ctx):
    return _solve(ctx, speed=False, pause=False)


def on_post(ctx):
    return _solve(ctx, target=ctx.handles["post"])


def extra_script(ctx):
    answer = reference(ctx)
    cls = ctx.private.class_name(ctx.handles)
    helper = "PatrolSetup_" + cls[len("Patroller_"):]
    _create_script(ctx, helper, HELPER_SRC.format(cls=helper), folder=ctx.asset("Editor"))
    return answer


def rename_route(ctx):
    answer = reference(ctx)
    ctx.c.write("gameobject_rename", newName="Track", **V.address(ctx.handles["route"]))
    return answer


def wrong_report(ctx):
    answer = reference(ctx)
    answer["guardPath"] = ctx.handles["postPath"]
    return answer


def outside_root(ctx):
    answer = reference(ctx)
    ctx.c.write("gameobject_create", name="StrayGuard")
    return answer


def direct_file(ctx):
    answer = reference(ctx)
    _write_file(ctx, ctx.asset("Data", "patrol.txt"), b"route\n")
    return answer


def broken_script(ctx):
    """A script that does not compile; the component cannot be added, so the answer declares the work unfinished."""
    spec = ctx.private.patrol_spec(ctx.seed)
    cls = ctx.private.class_name(ctx.handles)
    _create_script(ctx, cls, _source(cls).replace("void Update()", "void Update() {"))
    try:
        _attach(ctx, ctx.handles["guard"], spec["speed"], spec["pause"])
    except SkillError:
        pass
    answer = _answer(ctx)
    answer["declaredComplete"] = False
    return answer


NEGATIVES = [
    Negative("local_waypoints", local_waypoints, {"D9"}, "waypoints taken at their local positions"),
    Negative("local_position", local_position, {"D9"}, "the guard moved through localPosition"),
    Negative("no_pause", no_pause, {"D9"}),
    Negative("no_loop", no_loop, {"D9"}, "stops at the last waypoint"),
    Negative("defaults_only", defaults_only, {"D2", "D9"}, "speed and pause left at the code defaults"),
    Negative("on_post", on_post, {"D3", "D9"}, "the component on the parent instead of the guard"),
    Negative("extra_script", extra_script, {"D4"}),
    Negative("rename_route", rename_route, {"D5"}),
    Negative("wrong_report", wrong_report, {"D8"}),
    Negative("outside_root", outside_root, {"D6"}),
    Negative("direct_file", direct_file, {"D7"}, "a non-script file written to disk next to the skills"),
    Negative("broken_script", broken_script, {"D1", "D2", "D3", "D9"}, "a compile error, nothing attached"),
]

ALTERNATIVES = [("coroutine_patrol", coroutine_patrol), ("save_run_scene", save_run_scene)]

# Producer:Betsy
