"""DB4 solutions: reference, negatives (each with the exact set of assertions it must fail) and alternatives."""
import os

from usk_eval import verifylib as V
from usk_eval.task_api import Negative

FOLLOW_SRC = """using UnityEngine;

public class {cls} : MonoBehaviour
{{
    public Transform target;
    public Vector3 offset = new Vector3({ox}f, {oy}f, {oz}f);
    public bool smoothing = true;
    public float smoothSpeed = {speed}f;{marker}

    void {method}()
    {{
        if (target == null) return;
        Vector3 point = {point};
        {assign}
    }}
}}
"""

PRIVATE_SRC = """using UnityEngine;

public class {cls} : MonoBehaviour
{{
    [SerializeField] private Transform target;
    [Tooltip("World-space offset from the followed object")]
    [SerializeField] private Vector3 offset = new({ox}f, {oy}f, {oz}f);
    [SerializeField] private bool smoothing = true;
    [SerializeField, Min(0f)] private float smoothSpeed = {speed}.0f;

    void Update()
    {{
    }}

    private void LateUpdate()
    {{
        if (!target) return;
        var point = target.position + offset;
        if (smoothing)
            transform.position = Vector3.Lerp(transform.position, point, smoothSpeed * Time.deltaTime);
        else
            transform.position = point;
    }}
}}
"""

HELPER_SRC = """using UnityEngine;

public class {cls} : MonoBehaviour
{{
    public float bonus = 1f;
}}
"""

WORLD_ASSIGN = "transform.position = smoothing ? Vector3.Lerp(transform.position, point, smoothSpeed * " \
               "Time.deltaTime) : point;"
LOCAL_ASSIGN = "transform.localPosition = smoothing ? Vector3.Lerp(transform.localPosition, point, smoothSpeed * " \
               "Time.deltaTime) : point;"


def _number(ident):
    return int(ident) if isinstance(ident, str) else ident


def _cls(ctx):
    return ctx.private.class_name(ctx.handles["script"])


def _g(value):
    return "%g" % value


def _defaults(ctx, offset=None, speed=None):
    ox, oy, oz = ctx.private.DEFAULT_OFFSET if offset is None else offset
    return {"ox": _g(ox), "oy": _g(oy), "oz": _g(oz),
            "speed": _g(ctx.private.DEFAULT_SMOOTH_SPEED if speed is None else speed)}


def _source(ctx, method="LateUpdate", point="target.position + offset", assign=WORLD_ASSIGN, speed=None,
            variant=None):
    """The follow script; `variant` names the wrong behaviour for the offline model (a `// usk-fake:` comment)."""
    return FOLLOW_SRC.format(cls=_cls(ctx), method=method, point=point, assign=assign,
                             marker="\n    // usk-fake: %s" % variant if variant else "", **_defaults(ctx, speed=speed))


def _create_script(ctx, name, content, folder=None):
    res = ctx.c.write("script_create", scriptName=name, folder=folder or ctx.asset("Scripts"), content=content)
    if res.get("jobId"):
        ctx.wait_job(res["jobId"])
    ctx.wait_idle()
    return res.get("path")


def _attach(ctx, holder, follow=None, offset=None, smoothing=None, serialized=False):
    cls = _cls(ctx)
    at = V.address(holder)
    ctx.c.write("component_add", componentType=cls, **at)
    if follow is not None:
        ctx.c.write("component_set_serialized_property", componentType=cls, propertyPath="target",
                    referencePath=follow, objectType="Transform", **at)
    for name, value in (("offset", None if offset is None else ",".join(_g(v) for v in offset)),
                        ("smoothing", None if smoothing is None else str(smoothing).lower())):
        if value is None:
            continue
        if serialized:
            ctx.c.write("component_set_serialized_property", componentType=cls, propertyPath=name, value=value, **at)
        else:
            ctx.c.write("component_set_property", componentType=cls, propertyName=name, value=value, **at)


def _answer(ctx, holder):
    ser = ctx.probe.ser(holder, _cls(ctx)) or {}
    followed = ctx.probe.owner(ser.get("target"))
    return {"scriptPath": ctx.handles["script"], "cameraInstanceId": _number(V.object_id(holder)),
            "followedInstanceId": _number(V.object_id(followed or ctx.handles["rover"])), "declaredComplete": True}


def _solve(ctx, source=None, follow=None, offset=True, smoothing=False, serialized=False):
    h = ctx.handles
    _create_script(ctx, _cls(ctx), source or _source(ctx))
    _attach(ctx, h["cam"], follow=h["roverPath"] if follow is None else follow,
            offset=ctx.private.spec(ctx.seed)["offset"] if offset is True else offset, smoothing=smoothing,
            serialized=serialized)
    return _answer(ctx, h["cam"])


def _write_file(ctx, asset_path, data):
    """Writes a file under A on disk, bypassing the skills (the runner's refresh imports it at submit)."""
    target = os.path.join(ctx.project_path, *asset_path.split("/"))
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, "wb") as handle:
        handle.write(data)


def reference(ctx):
    return _solve(ctx)


def wrong_rover(ctx):
    return _solve(ctx, follow=ctx.handles["otherPath"])


def smoothing_left_on(ctx):
    """smoothing keeps its code default true: the camera lerps toward the vehicle and lags behind it."""
    return _solve(ctx, smoothing=None)


def offset_default(ctx):
    return _solve(ctx, offset=None)


def no_smoothing(ctx):
    """The smoothing branch is gone: the script only ever writes the exact point."""
    return _solve(ctx, source=_source(ctx, assign="transform.position = point;"))


def stub_late_update(ctx):
    """An empty LateUpdate() satisfies a bare declaration check while the follow lives in Update."""
    source = _source(ctx, method="Update").rstrip().rsplit("}", 1)[0] + "\n    void LateUpdate() { }\n}\n"
    return _solve(ctx, source=source)


def update_follow(ctx):
    """The follow runs in Update; a vehicle teleported between frames is still followed exactly at runtime."""
    return _solve(ctx, source=_source(ctx, method="Update"))


def local_offset(ctx):
    return _solve(ctx, source=_source(ctx, point="target.position + target.rotation * offset",
                                      variant="local_offset"))


def local_position(ctx):
    return _solve(ctx, source=_source(ctx, assign=LOCAL_ASSIGN, variant="local_position"))


def wrong_default(ctx):
    return _solve(ctx, source=_source(ctx, speed=5))


def compile_error(ctx):
    h = ctx.handles
    _create_script(ctx, _cls(ctx), _source(ctx).replace("void LateUpdate()", "void LateUpdate() {"))
    return {"scriptPath": h["script"], "cameraInstanceId": _number(V.object_id(h["cam"])),
            "followedInstanceId": _number(V.object_id(h["rover"])), "declaredComplete": True}


def extra_script(ctx):
    answer = reference(ctx)
    name = "FollowHelper_" + _cls(ctx).split("_", 1)[1]
    _create_script(ctx, name, HELPER_SRC.format(cls=name))
    return answer


def rename_bystander(ctx):
    answer = reference(ctx)
    ctx.c.write("gameobject_rename", newName="RenamedVehicle", path=ctx.handles["otherPath"])
    return answer


def wrong_report(ctx):
    answer = reference(ctx)
    other = next(ref for ref in ctx.handles["rovers"] if not V.same_id(ref, ctx.handles["rover"]))
    answer["followedInstanceId"] = _number(V.object_id(other))
    return answer


def outside_root(ctx):
    answer = reference(ctx)
    ctx.c.write("gameobject_create", name="StrayCam")
    return answer


def direct_file(ctx):
    answer = reference(ctx)
    _write_file(ctx, ctx.asset("Scripts", "notes.txt"), b"following\n")
    return answer


def serialized_private(ctx):
    """[SerializeField] private fields, a target-typed new default, an empty Update stub, values written through
    component_set_serialized_property."""
    source = PRIVATE_SRC.format(cls=_cls(ctx), **_defaults(ctx))
    return _solve(ctx, source=source, serialized=True)


def preplaced_camera(ctx):
    """The camera is also moved in Edit Mode to where it will follow (the camera may change)."""
    answer = reference(ctx)
    info = ctx.probe.go(ctx.handles["rover"]) or {}
    point = [a + b for a, b in zip(V.floats(info.get("position")), ctx.private.spec(ctx.seed)["offset"])]
    ctx.c.write("gameobject_set_transform", posX=point[0], posY=point[1], posZ=point[2],
                **V.address(ctx.handles["cam"]))
    return answer


def save_run_scene(ctx):
    answer = reference(ctx)
    ctx.c.write("scene_save")
    return answer


NEGATIVES = [
    Negative("wrong_rover", wrong_rover, {"J4", "J8"}, "target references the other vehicle"),
    Negative("smoothing_left_on", smoothing_left_on, {"J5", "J8"}, "the scene keeps smoothing true; the camera lags"),
    Negative("offset_default", offset_default, {"J5", "J8"}, "the scene keeps the code default offset"),
    Negative("update_follow", update_follow, {"J2"}, "the follow runs in Update instead of LateUpdate"),
    Negative("stub_late_update", stub_late_update, {"J2"}, "an empty LateUpdate() while the follow runs in Update"),
    Negative("no_smoothing", no_smoothing, {"J12"}, "the smoothing branch is replaced by a plain assignment"),
    Negative("local_offset", local_offset, {"J8"}, "the offset is rotated with the yawed vehicle"),
    Negative("local_position", local_position, {"J8"}, "localPosition is written under an offset parent"),
    Negative("wrong_default", wrong_default, {"J1"}, "smoothSpeed has another code default"),
    Negative("compile_error", compile_error, {"J3", "J4", "J5", "J8", "J9"}, "the script does not compile"),
    Negative("extra_script", extra_script, {"J6"}, "an extra C# script was added"),
    Negative("rename_bystander", rename_bystander, {"J7"}, "the other vehicle was renamed"),
    Negative("wrong_report", wrong_report, {"J9"}, "the answer reports the other vehicle"),
    Negative("outside_root", outside_root, {"J10"}, "a GameObject was created outside the run root"),
    Negative("direct_file", direct_file, {"J11"}, "a non-script file was written directly to disk"),
]

ALTERNATIVES = [("serialized_private", serialized_private), ("preplaced_camera", preplaced_camera),
                ("save_run_scene", save_run_scene)]

# Producer:Betsy
