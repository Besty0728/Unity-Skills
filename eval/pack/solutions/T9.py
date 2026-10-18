"""T9 solutions: reference, negatives (each with the exact set of assertions it must fail) and alternatives."""
import os

from usk_eval import verifylib as V
from usk_eval.task_api import Negative

RENAMED_SRC = """using UnityEngine;
using UnityEngine.Serialization;

public class {cls} : MonoBehaviour
{{
    [FormerlySerializedAs("{old_speed}")]
    public float moveSpeed = 1f;
    [FormerlySerializedAs("{old_waypoint}")]
    public Transform patrolTarget;
    public int laps = 1;{extra}
}}
"""

PLAIN_SRC = """using UnityEngine;

public class {cls} : MonoBehaviour
{{
    public float moveSpeed = 1f;
    public Transform patrolTarget;
    public int laps = 1;
}}
"""

ROUTE_SRC = """using UnityEngine;

public class {cls} : MonoBehaviour
{{
    [UnityEngine.Serialization.FormerlySerializedAs("speed")]
    public float moveSpeed = 2f;
    public bool loop = true;
}}
"""

EDITOR_SRC = """using UnityEditor;
using UnityEngine;

public static class {cls}
{{
    [MenuItem("Tools/{cls}")]
    public static void Run() {{ Debug.Log("migrate"); }}
}}
"""

AWAKE = """

    void Awake()
    {
        moveSpeed = 0f;
    }"""


def _number(ident):
    return int(ident) if isinstance(ident, str) else ident


def _wait(ctx, res):
    if (res or {}).get("jobId"):
        ctx.wait_job(res["jobId"])
    ctx.wait_idle()


def _rewrite(ctx, content, path=None):
    _wait(ctx, ctx.c.write("script_replace", scriptPath=path or ctx.handles["patrolScript"], find=r"(?s).+",
                           replace=content, isRegex=True))


def _renamed(ctx, old_speed="speed", old_waypoint="waypoint", extra=""):
    patrol, _route = ctx.private.class_names(ctx.handles)
    return RENAMED_SRC.format(cls=patrol, old_speed=old_speed, old_waypoint=old_waypoint, extra=extra)


def _plain(ctx):
    return PLAIN_SRC.format(cls=ctx.private.class_names(ctx.handles)[0])


def _fixture(ctx):
    return ctx.task.snapshot(ctx)["data"]


def _answer(ctx, rows=None):
    p, h = ctx.probe, ctx.handles
    patrol, _route = ctx.private.class_names(h)
    if rows is None:
        rows = []
        for ref in h["guards"]:
            values = p.ser(ref, patrol) or {}
            speed = values.get("moveSpeed")
            rows.append({"instanceId": _number(ref), "moveSpeed": float(speed) if speed is not None else None,
                         "waypointInstanceId": _number(p.owner(values.get("patrolTarget")))})
    return {"scriptPath": h["patrolScript"], "guards": rows, "declaredComplete": True}


def _write_file(ctx, asset_path, data):
    """Writes a file under A on disk, bypassing the skills (the runner's refresh imports it at submit)."""
    target = os.path.join(ctx.project_path, *asset_path.split("/"))
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, "wb") as handle:
        handle.write(data)


def reference(ctx):
    _rewrite(ctx, _renamed(ctx))
    return _answer(ctx)


def no_attribute(ctx):
    _rewrite(ctx, _plain(ctx))
    return _answer(ctx)


def rename_and_reset(ctx):
    rows = _fixture(ctx)["guards"]
    patrol, _route = ctx.private.class_names(ctx.handles)
    _rewrite(ctx, _plain(ctx))
    for ref, was in rows:
        at = V.address(ref)
        ctx.c.write("component_set_property", componentType=patrol, propertyName="moveSpeed", value=was["speed"],
                    **at)
        ctx.c.write("component_set_serialized_property", componentType=patrol, propertyPath="patrolTarget",
                    referencePath=(ctx.probe.go(was["waypoint"]) or {}).get("path"), objectType="Transform", **at)
    return _answer(ctx)


def claims_preserved(ctx):
    rows = _fixture(ctx)["guards"]
    _rewrite(ctx, _plain(ctx))
    return _answer(ctx, [{"instanceId": _number(ref), "moveSpeed": float(was["speed"]),
                          "waypointInstanceId": _number(was["waypoint"])} for ref, was in rows])


def compile_error(ctx):
    _rewrite(ctx, _renamed(ctx).replace("public int laps = 1;", "public int laps = 1"))
    answer = _answer(ctx, [])
    answer["declaredComplete"] = False
    return answer


def attribute_typo(ctx):
    _rewrite(ctx, _renamed(ctx, old_speed="sped"))
    return _answer(ctx)


def awake_reset(ctx):
    _rewrite(ctx, _renamed(ctx, extra=AWAKE))
    return _answer(ctx)


def default_changed(ctx):
    _rewrite(ctx, _renamed(ctx).replace("moveSpeed = 1f;", "moveSpeed = 2f;"))
    return _answer(ctx)


def laps_changed(ctx):
    reference(ctx)
    patrol, _route = ctx.private.class_names(ctx.handles)
    ctx.c.write("component_set_property", componentType=patrol, propertyName="laps", value="9",
                **V.address(ctx.handles["guards"][0]))
    return _answer(ctx)


def editor_helper(ctx):
    answer = reference(ctx)
    name = "PatrolMigration_" + ctx.private.class_names(ctx.handles)[0][len("Patrol_"):]
    _wait(ctx, ctx.c.write("script_create", scriptName=name, folder=ctx.asset("Editor"),
                           content=EDITOR_SRC.format(cls=name)))
    return answer


def route_renamed_too(ctx):
    answer = reference(ctx)
    _patrol, route = ctx.private.class_names(ctx.handles)
    _rewrite(ctx, ROUTE_SRC.format(cls=route), path=ctx.handles["routeScript"])
    return answer


def moved_waypoint(ctx):
    answer = reference(ctx)
    ctx.c.write("gameobject_set_transform", localPosY=2, **V.address(ctx.handles["waypoints"][0]))
    return answer


def outside_root(ctx):
    answer = reference(ctx)
    ctx.c.write("gameobject_create", name="Stray")
    return answer


def direct_file(ctx):
    answer = reference(ctx)
    _write_file(ctx, ctx.asset("Data", "notes.txt"), b"renamed\n")
    return answer


def wrong_report(ctx):
    answer = reference(ctx)
    first, second = answer["guards"][0], answer["guards"][1]
    first["moveSpeed"], second["moveSpeed"] = second["moveSpeed"], first["moveSpeed"]
    return answer


def edit_file_and_refresh(ctx):
    target = os.path.join(ctx.project_path, *ctx.handles["patrolScript"].split("/"))
    with open(target, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(_renamed(ctx))
    _wait(ctx, ctx.c.write("asset_refresh"))
    return _answer(ctx)


def literal_edits(ctx):
    path = ctx.handles["patrolScript"]
    for old, new, kind, default in (("speed", "moveSpeed", "float", " = 1f"),
                                    ("waypoint", "patrolTarget", "Transform", "")):
        _wait(ctx, ctx.c.write("script_replace", scriptPath=path, find="    public %s %s%s;" % (kind, old, default),
                               replace='    [UnityEngine.Serialization.FormerlySerializedAs("%s")]\n'
                                       '    public %s %s%s;' % (old, kind, new, default)))
    return _answer(ctx)


def save_run_scene(ctx):
    answer = reference(ctx)
    ctx.c.write("scene_save")
    return answer


NEGATIVES = [
    Negative("no_attribute", no_attribute, {"C2", "C4", "C5", "C12"}, "renaming alone resets the stored values"),
    Negative("rename_and_reset", rename_and_reset, {"C2", "C7"}, "the values written back through skills"),
    Negative("claims_preserved", claims_preserved, {"C2", "C4", "C5", "C11", "C12"}),
    Negative("compile_error", compile_error, {"C3", "C4", "C5", "C11", "C12"}),
    Negative("attribute_typo", attribute_typo, {"C2", "C4", "C12"}),
    Negative("awake_reset", awake_reset, {"C12"}, "the values hold in the editor, Awake resets one at runtime"),
    Negative("default_changed", default_changed, {"C1"}, "moveSpeed's default value changed"),
    Negative("laps_changed", laps_changed, {"C6", "C12"}),
    Negative("editor_helper", editor_helper, {"C8"}),
    Negative("route_renamed_too", route_renamed_too, {"C8", "C9"}, "the other script with a speed field"),
    Negative("moved_waypoint", moved_waypoint, {"C9"}),
    Negative("outside_root", outside_root, {"C10"}),
    Negative("direct_file", direct_file, {"C13"}, "a non-script file written to disk next to the skills"),
    Negative("wrong_report", wrong_report, {"C11"}),
]

ALTERNATIVES = [("edit_file_and_refresh", edit_file_and_refresh), ("literal_edits", literal_edits),
                ("save_run_scene", save_run_scene)]

# Producer:Betsy
