"""DB1 solutions: reference, negatives (each with the exact set of assertions it must fail) and alternatives."""
import os

from usk_eval import verifylib as V
from usk_eval.task_api import Negative

SPINNER_SRC = """using UnityEngine;

public class {cls} : MonoBehaviour
{{
    public float degreesPerSecond = {default}f;{marker}

    void Update()
    {{
        transform.Rotate(0f, degreesPerSecond * {step}, 0f, {space});
    }}
}}
"""

QUATERNION_SRC = """using UnityEngine;

public class {cls} : MonoBehaviour
{{
    [SerializeField] public float degreesPerSecond = {default}f;

    void Update()
    {{
        transform.localRotation *= Quaternion.AngleAxis(degreesPerSecond * Time.deltaTime, Vector3.up);
    }}
}}
"""

HELPER_SRC = """using UnityEngine;

public class {cls} : MonoBehaviour
{{
    public float bonus = 1f;
}}
"""


def _number(ident):
    return int(ident) if isinstance(ident, str) else ident


def _cls(ctx):
    return ctx.private.class_name(ctx.handles["script"])


def _source(ctx, default=None, step="Time.deltaTime", space="Space.Self", variant=None):
    """The Spinner source; `variant` names the wrong behaviour for the offline model (a `// usk-fake:` comment)."""
    return SPINNER_SRC.format(cls=_cls(ctx), default=ctx.private.DEFAULT_SPEED if default is None else default,
                              step=step, space=space, marker="\n    // usk-fake: %s" % variant if variant else "")


def _create_script(ctx, name, content, folder=None):
    res = ctx.c.write("script_create", scriptName=name, folder=folder or ctx.asset("Scripts"), content=content)
    if res.get("jobId"):
        ctx.wait_job(res["jobId"])
    ctx.wait_idle()
    return res.get("path")


def _attach(ctx, ref, speed=None):
    cls = _cls(ctx)
    ctx.c.write("component_add", componentType=cls, **V.address(ref))
    if speed is not None:
        ctx.c.write("component_set_property", componentType=cls, propertyName="degreesPerSecond", value=str(speed),
                    **V.address(ref))


def _answer(ctx, holder):
    ser = ctx.probe.ser(holder, _cls(ctx)) or {}
    stored = V.floats(ser.get("degreesPerSecond"))
    return {"scriptPath": ctx.handles["script"], "spinnerInstanceId": _number(V.object_id(holder)),
            "degreesPerSecond": stored[0] if stored else None, "declaredComplete": True}


def _solve(ctx, source=None, holder=None, speed=None):
    h = ctx.handles
    _create_script(ctx, _cls(ctx), source or _source(ctx))
    holder = h["spinner"] if holder is None else holder
    _attach(ctx, holder, ctx.private.spec(ctx.seed)["speed"] if speed is None else speed)
    return _answer(ctx, holder)


def _write_file(ctx, asset_path, data):
    """Writes a file under A on disk, bypassing the skills (the runner's refresh imports it at submit)."""
    target = os.path.join(ctx.project_path, *asset_path.split("/"))
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, "wb") as handle:
        handle.write(data)


def reference(ctx):
    return _solve(ctx)


def wrong_blades(ctx):
    other = next(ref for ref in ctx.handles["blades"] if not V.same_id(ref, ctx.handles["spinner"]))
    return _solve(ctx, holder=other)


def default_only(ctx):
    h = ctx.handles
    _create_script(ctx, _cls(ctx), _source(ctx))
    _attach(ctx, h["spinner"])
    return _answer(ctx, h["spinner"])


def per_frame(ctx):
    return _solve(ctx, source=_source(ctx, step="1f", variant="per_frame"))


def world_axis(ctx):
    return _solve(ctx, source=_source(ctx, space="Space.World", variant="world_axis"))


def wrong_default(ctx):
    return _solve(ctx, source=_source(ctx, default=45))


def compile_error(ctx):
    h = ctx.handles
    _create_script(ctx, _cls(ctx), _source(ctx).replace("void Update()", "void Update() {"))
    return {"scriptPath": h["script"], "spinnerInstanceId": _number(V.object_id(h["spinner"])),
            "degreesPerSecond": ctx.private.spec(ctx.seed)["speed"], "declaredComplete": True}


def extra_script(ctx):
    answer = reference(ctx)
    name = "SpinHelper_" + _cls(ctx).split("_", 1)[1]
    _create_script(ctx, name, HELPER_SRC.format(cls=name))
    return answer


def two_components(ctx):
    answer = reference(ctx)
    _attach(ctx, ctx.handles["spinner"], ctx.private.spec(ctx.seed)["speed"])
    return answer


def wrong_report(ctx):
    answer = reference(ctx)
    answer["degreesPerSecond"] = ctx.private.DEFAULT_SPEED
    return answer


def outside_root(ctx):
    answer = reference(ctx)
    ctx.c.write("gameobject_create", name="StrayMill")
    return answer


def direct_file(ctx):
    answer = reference(ctx)
    _write_file(ctx, ctx.asset("Scripts", "notes.txt"), b"spinning\n")
    return answer


def quaternion_rotation(ctx):
    return _solve(ctx, source=QUATERNION_SRC.format(cls=_cls(ctx), default=ctx.private.DEFAULT_SPEED))


def save_run_scene(ctx):
    answer = reference(ctx)
    ctx.c.write("scene_save")
    return answer


NEGATIVES = [
    Negative("wrong_blades", wrong_blades, {"C3", "C5", "C6"}, "the other mill's Blades carries the component"),
    Negative("default_only", default_only, {"C3", "C6"}, "the scene keeps the code default"),
    Negative("per_frame", per_frame, {"C6"}, "a fixed angle per frame instead of Time.deltaTime"),
    Negative("world_axis", world_axis, {"C6"}, "rotation about the world Y axis of a tilted object"),
    Negative("wrong_default", wrong_default, {"C1"}),
    Negative("compile_error", compile_error, {"C2", "C3", "C6", "C7"}, "the script does not compile"),
    Negative("extra_script", extra_script, {"C4"}),
    Negative("two_components", two_components, {"C3", "C6"}, "two components turn twice as fast"),
    Negative("wrong_report", wrong_report, {"C7"}),
    Negative("outside_root", outside_root, {"C8"}),
    Negative("direct_file", direct_file, {"C9"}, "a non-script file written to disk next to the skills"),
]

ALTERNATIVES = [("quaternion_rotation", quaternion_rotation), ("save_run_scene", save_run_scene)]

# Producer:Betsy
