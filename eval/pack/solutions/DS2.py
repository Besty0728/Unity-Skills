"""DS2 solutions: reference, negatives (each with the exact set of assertions it must fail) and alternatives.

The C# sources compile cleanly against Unity's assemblies; each behavioural negative also carries a
`// usk-fake: <variant>` comment so the offline fake editor models the corresponding wrong behaviour.
"""
import os

from usk_eval import verifylib as V
from usk_eval.rest import SkillError
from usk_eval.task_api import Negative

DOOR_SRC = """using UnityEngine;
{marker}
public class {cls} : MonoBehaviour
{{
    public Transform door;
    public Vector3 openOffset = new Vector3(0f, 1f, 0f);
    public float speed = 2f;
    public int enterCount = 0;
    public int exitCount = 0;

    private Vector3 _closedPos;
    private int _inside = 0;

    void Start()
    {{
        if (door != null) _closedPos = door.localPosition;
    }}

    void OnTriggerEnter(Collider other)
    {{
        if ({tag_check})
        {{
            {on_enter}
        }}
    }}

    void OnTriggerExit(Collider other)
    {{
        if ({tag_check})
        {{
            {on_exit}
        }}
    }}

    void Update()
    {{
        if (door == null) return;
        Vector3 goal = (_inside > 0) ? (_closedPos + openOffset) : _closedPos;
        door.localPosition = Vector3.MoveTowards(door.localPosition, goal, speed * Time.deltaTime);
    }}
}}
"""

COROUTINE_DOOR_SRC = """using System.Collections;
using UnityEngine;

public class {cls} : MonoBehaviour
{{
    public Transform door;
    public Vector3 openOffset = new Vector3(0f, 1f, 0f);
    public float speed = 2f;
    public int enterCount = 0;
    public int exitCount = 0;

    private Vector3 _closedPos;
    private Coroutine _slide;

    void Awake()
    {{
        if (door != null) _closedPos = door.localPosition;
    }}

    void OnTriggerEnter(Collider other)
    {{
        if (!other.CompareTag("Player")) return;
        enterCount += 1;
        SlideTo(_closedPos + openOffset);
    }}

    void OnTriggerExit(Collider other)
    {{
        if (!other.CompareTag("Player")) return;
        exitCount += 1;
        SlideTo(_closedPos);
    }}

    private void SlideTo(Vector3 target)
    {{
        if (door == null) return;
        if (_slide != null) StopCoroutine(_slide);
        _slide = StartCoroutine(MoveRoutine(target));
    }}

    private IEnumerator MoveRoutine(Vector3 target)
    {{
        while (door != null && door.localPosition != target)
        {{
            door.localPosition = Vector3.MoveTowards(door.localPosition, target, speed * Time.deltaTime);
            yield return null;
        }}
    }}
}}
"""

HELPER_SRC = """using UnityEditor;
using UnityEngine;

public static class {cls}
{{
    [MenuItem("Tools/{cls}")]
    public static void Run() {{ Debug.Log("door setup"); }}
}}
"""


def _source(cls, variant=None):
    parts = {
        "marker": "// usk-fake: %s" % variant if variant else "",
        "cls": cls,
        "tag_check": 'other.CompareTag("Player")',
        "on_enter": "enterCount += 1;\n            _inside += 1;",
        "on_exit": "exitCount += 1;\n            _inside = Mathf.Max(0, _inside - 1);",
    }
    if variant == "no-tag-check":
        parts["tag_check"] = "other != null"
    elif variant == "no-close":
        parts["on_exit"] = "exitCount += 1;"
    elif variant == "once-only":
        parts["on_enter"] = "if (enterCount == 0) { enterCount = 1; _inside = 1; }"
        parts["on_exit"] = "if (exitCount == 0 && _inside > 0) { exitCount = 1; _inside = 0; }"
    elif variant == "no-count":
        parts["on_enter"] = "_inside += 1;"
        parts["on_exit"] = "_inside = Mathf.Max(0, _inside - 1);"
    return DOOR_SRC.format(**parts)


def _create_script(ctx, name, content, folder=None):
    res = ctx.c.write("script_create", scriptName=name, folder=folder or ctx.asset("Scripts"), content=content)
    if isinstance(res, dict) and res.get("jobId"):
        ctx.wait_job(res["jobId"])
    ctx.wait_idle()
    return res.get("path") if isinstance(res, dict) else None


def _configure_physics(ctx, trigger=True, rb=True, kinematic=True, gravity=False):
    h = ctx.handles
    if trigger:
        ctx.c.write("component_set_property", componentType="BoxCollider", propertyName="isTrigger", value="true",
                    **V.address(h["zone"]))
    if rb:
        ctx.c.write("component_add", componentType="Rigidbody", **V.address(h["player"]))
        ctx.c.write("component_set_property", componentType="Rigidbody", propertyName="isKinematic",
                    value="true" if kinematic else "false", **V.address(h["player"]))
        ctx.c.write("component_set_property", componentType="Rigidbody", propertyName="useGravity",
                    value="true" if gravity else "false", **V.address(h["player"]))


def _attach(ctx, target, set_values=True):
    h = ctx.handles
    spec = ctx.private.door_spec(ctx.seed)
    cls = ctx.private.class_name(h)
    at = V.address(target)
    ctx.c.write("component_add", componentType=cls, **at)
    ctx.c.write("component_set_serialized_property", componentType=cls, propertyPath="door",
                referencePath=h["doorPath"], objectType="Transform", **at)
    if set_values:
        vec = "%s,%s,%s" % (spec["ox"], spec["oy"], spec["oz"])
        ctx.c.write("component_set_property", componentType=cls, propertyName="openOffset", value=vec, **at)
        ctx.c.write("component_set_property", componentType=cls, propertyName="speed", value=str(spec["speed"]), **at)


def _answer(ctx):
    h = ctx.handles
    return {
        "scriptPath": h["script"],
        "zonePath": (ctx.probe.go(h["zone"]) or {}).get("path"),
        "doorPath": (ctx.probe.go(h["door"]) or {}).get("path"),
        "playerPath": (ctx.probe.go(h["player"]) or {}).get("path"),
        "declaredComplete": True,
    }


def _solve(ctx, variant=None, target=None, set_values=True, trigger=True, rb=True, kinematic=True, gravity=False):
    h = ctx.handles
    cls = ctx.private.class_name(h)
    _create_script(ctx, cls, _source(cls, variant))
    _configure_physics(ctx, trigger=trigger, rb=rb, kinematic=kinematic, gravity=gravity)
    _attach(ctx, target if target is not None else h["zone"], set_values=set_values)
    return _answer(ctx)


def _write_file(ctx, asset_path, data):
    target = os.path.join(ctx.project_path, *asset_path.split("/"))
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, "wb") as handle:
        handle.write(data)


# ---------------------------------------------------------------- reference and alternatives


def reference(ctx):
    return _solve(ctx)


def coroutine_slide(ctx):
    h = ctx.handles
    cls = ctx.private.class_name(h)
    _create_script(ctx, cls, COROUTINE_DOOR_SRC.format(cls=cls))
    _configure_physics(ctx)
    _attach(ctx, h["zone"], set_values=True)
    return _answer(ctx)


def save_run_scene(ctx):
    out = reference(ctx)
    ctx.c.write("scene_save")
    return out


# ---------------------------------------------------------------- negatives


def no_tag_check(ctx):
    return _solve(ctx, variant="no-tag-check")


def no_close(ctx):
    return _solve(ctx, variant="no-close")


def once_only(ctx):
    return _solve(ctx, variant="once-only")


def no_count(ctx):
    return _solve(ctx, variant="no-count")


def missing_rigidbody(ctx):
    return _solve(ctx, rb=False)


def non_trigger_collider(ctx):
    return _solve(ctx, trigger=False)


def defaults_only(ctx):
    return _solve(ctx, set_values=False)


def on_door_object(ctx):
    return _solve(ctx, target=ctx.handles["door"])


def extra_script(ctx):
    out = reference(ctx)
    cls = ctx.private.class_name(ctx.handles)
    helper = "DoorSetup_" + cls[len("TriggerDoor_"):]
    _create_script(ctx, helper, HELPER_SRC.format(cls=helper), folder=ctx.asset("Editor"))
    return out


def rename_door(ctx):
    out = reference(ctx)
    ctx.c.write("gameobject_rename", newName="MovedDoor", **V.address(ctx.handles["door"]))
    out["doorPath"] = ctx.handles["doorPath"]
    return out


def wrong_report(ctx):
    out = reference(ctx)
    out["zonePath"] = ctx.handles["doorPath"]
    return out


def outside_root(ctx):
    out = reference(ctx)
    ctx.c.write("gameobject_create", name="StraySensor")
    return out


def direct_file(ctx):
    out = reference(ctx)
    _write_file(ctx, ctx.asset("Data", "door.txt"), b"sensor\n")
    return out


def broken_script(ctx):
    h = ctx.handles
    cls = ctx.private.class_name(h)
    _create_script(ctx, cls, _source(cls).replace("void Update()", "void Update() {"))
    _configure_physics(ctx)
    try:
        _attach(ctx, h["zone"], set_values=True)
    except SkillError:
        pass
    out = _answer(ctx)
    out["declaredComplete"] = False
    return out


NEGATIVES = [
    Negative("no_tag_check", no_tag_check, {"D9", "D10"}, "responds to the non-Player visitor as well"),
    Negative("no_close", no_close, {"D9"}, "opens on entry but never closes on exit"),
    Negative("once_only", once_only, {"D9", "D10"}, "handles only the first entry/exit"),
    Negative("no_count", no_count, {"D10"}, "moves the door without updating enterCount/exitCount"),
    Negative("missing_rigidbody", missing_rigidbody, {"D3", "D9", "D10"}, "no Rigidbody added to the Player"),
    Negative("non_trigger_collider", non_trigger_collider, {"D3", "D9", "D10"}, "BoxCollider.isTrigger left false"),
    Negative("defaults_only", defaults_only, {"D2", "D9"}, "openOffset and speed left at code defaults"),
    Negative("on_door_object", on_door_object, {"D2", "D5", "D9", "D10"}, "component attached to the door instead of zone"),
    Negative("extra_script", extra_script, {"D4"}),
    Negative("rename_door", rename_door, {"D5", "D8"}),
    Negative("wrong_report", wrong_report, {"D8"}),
    Negative("outside_root", outside_root, {"D6"}),
    Negative("direct_file", direct_file, {"D7"}, "a non-script file written to disk next to the skills"),
    Negative("broken_script", broken_script, {"D1", "D2", "D9", "D10"}, "a compile error, component not attached"),
]

ALTERNATIVES = [("coroutine_slide", coroutine_slide), ("save_run_scene", save_run_scene)]

# Producer:Betsy
