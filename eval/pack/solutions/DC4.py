"""DC4 solutions: reference, negatives (each with the exact set of assertions it must fail) and alternatives.

The C# sources compile cleanly against both Unity 2022.3 and Unity 6000.x; each negative variant also carries a
`// usk-fake: <variant>` comment that the offline fake editor reads to model the corresponding mistake (a real editor
ignores the comment and runs the actual C# physics math).
"""
import os

from usk_eval import verifylib as V
from usk_eval.rest import SkillError
from usk_eval.task_api import Negative

LAUNCHER_SRC = """using UnityEngine;
{marker}
public class {cls} : MonoBehaviour
{{
    {field_attr} Transform {muzzle_field};
    {field_attr} GameObject {prefab_field};
{extra_fields}
    public GameObject Fire(Transform target)
    {{
        if (target == null || {muzzle_field} == null || {prefab_field} == null) return null;
        Vector3 origin = {muzzle_field}.position;
        GameObject shell = Instantiate({prefab_field}, origin, Quaternion.identity);
        Rigidbody rb = shell.GetComponent<Rigidbody>();
        if (rb == null) return shell;
{body}
        return shell;
    }}
}}
"""

SOLVE_BODY_VELOCITY_CHANGE = """        float dt = Time.fixedDeltaTime > 0f ? Time.fixedDeltaTime : 0.02f;
        float drag = rb.drag;
        float factor = Mathf.Clamp01(1f - drag * dt);
        int steps = 60;
        float sumF = 0f;
        float curF = 1f;
        Vector3 gravVel = Vector3.zero;
        Vector3 gravDisp = Vector3.zero;
        Vector3 g = Physics.gravity;
        for (int i = 0; i < steps; i++)
        {
            curF *= factor;
            sumF += curF * dt;
            gravVel = (gravVel + g * dt) * factor;
            gravDisp += gravVel * dt;
        }
        Vector3 v0 = (target.position - origin - gravDisp) / sumF;
        rb.AddForce(v0, ForceMode.VelocityChange);"""

SOLVE_BODY_IMPULSE_MASS = """        float dt = Time.fixedDeltaTime > 0f ? Time.fixedDeltaTime : 0.02f;
        float drag = rb.drag;
        float factor = 1f / (1f + drag * dt);
        int steps = 65;
        float sumF = 0f;
        float curF = 1f;
        Vector3 gravVel = Vector3.zero;
        Vector3 gravDisp = Vector3.zero;
        Vector3 g = Physics.gravity;
        for (int i = 0; i < steps; i++)
        {
            curF *= factor;
            sumF += curF * dt;
            gravVel = (gravVel + g * dt) * factor;
            gravDisp += gravVel * dt;
        }
        Vector3 v0 = (target.position - origin - gravDisp) / sumF;
        rb.AddForce(v0 * rb.mass, ForceMode.Impulse);"""

SOLVE_BODY_NO_DRAG = """        float t = 1.2f;
        Vector3 disp = target.position - origin;
        Vector3 v0 = (disp - 0.5f * Physics.gravity * t * t) / t;
        rb.AddForce(v0, ForceMode.VelocityChange);"""

SOLVE_BODY_WRONG_FORCEMODE = """        float dt = Time.fixedDeltaTime > 0f ? Time.fixedDeltaTime : 0.02f;
        float drag = rb.drag;
        float factor = Mathf.Clamp01(1f - drag * dt);
        int steps = 60;
        float sumF = 0f;
        float curF = 1f;
        Vector3 gravVel = Vector3.zero;
        Vector3 gravDisp = Vector3.zero;
        Vector3 g = Physics.gravity;
        for (int i = 0; i < steps; i++)
        {
            curF *= factor;
            sumF += curF * dt;
            gravVel = (gravVel + g * dt) * factor;
            gravDisp += gravVel * dt;
        }
        Vector3 v0 = (target.position - origin - gravDisp) / sumF;
        rb.AddForce(v0, ForceMode.Force);"""

SOLVE_BODY_WRONG_GRAVITY = """        float dt = Time.fixedDeltaTime > 0f ? Time.fixedDeltaTime : 0.02f;
        float drag = rb.drag;
        float factor = Mathf.Clamp01(1f - drag * dt);
        int steps = 60;
        float sumF = 0f;
        float curF = 1f;
        Vector3 gravVel = Vector3.zero;
        Vector3 gravDisp = Vector3.zero;
        Vector3 g = new Vector3(0f, -9.81f, 0f);
        for (int i = 0; i < steps; i++)
        {
            curF *= factor;
            sumF += curF * dt;
            gravVel = (gravVel + g * dt) * factor;
            gravDisp += gravVel * dt;
        }
        Vector3 v0 = (target.position - origin - gravDisp) / sumF;
        rb.AddForce(v0, ForceMode.VelocityChange);"""

SOLVE_BODY_FIXED_ANGLE = """        Vector3 dir = (muzzle.forward + Vector3.up * 0.5f).normalized;
        rb.AddForce(dir * 12f, ForceMode.VelocityChange);"""

SOLVE_BODY_FIRST_TARGET = """        if (_cachedGoal == Vector3.zero) _cachedGoal = target.position;
        float dt = Time.fixedDeltaTime > 0f ? Time.fixedDeltaTime : 0.02f;
        float drag = rb.drag;
        float factor = Mathf.Clamp01(1f - drag * dt);
        int steps = 60;
        float sumF = 0f;
        float curF = 1f;
        Vector3 gravVel = Vector3.zero;
        Vector3 gravDisp = Vector3.zero;
        Vector3 g = Physics.gravity;
        for (int i = 0; i < steps; i++)
        {
            curF *= factor;
            sumF += curF * dt;
            gravVel = (gravVel + g * dt) * factor;
            gravDisp += gravVel * dt;
        }
        Vector3 v0 = (_cachedGoal - origin - gravDisp) / sumF;
        rb.AddForce(v0, ForceMode.VelocityChange);"""

SOLVE_BODY_LOCAL_SPACE = """        Vector3 localDisp = muzzle.InverseTransformPoint(target.position);
        float dt = Time.fixedDeltaTime > 0f ? Time.fixedDeltaTime : 0.02f;
        float drag = rb.drag;
        float factor = Mathf.Clamp01(1f - drag * dt);
        int steps = 60;
        float sumF = 0f;
        float curF = 1f;
        Vector3 gravVel = Vector3.zero;
        Vector3 gravDisp = Vector3.zero;
        Vector3 g = Physics.gravity;
        for (int i = 0; i < steps; i++)
        {
            curF *= factor;
            sumF += curF * dt;
            gravVel = (gravVel + g * dt) * factor;
            gravDisp += gravVel * dt;
        }
        Vector3 v0 = (localDisp - gravDisp) / sumF;
        rb.AddForce(v0, ForceMode.VelocityChange);"""

SOLVE_BODY_STRIP_RIGIDBODY = """        rb.mass = 1f;
        rb.drag = 0f;
        float t = 1.2f;
        Vector3 disp = target.position - origin;
        Vector3 v0 = (disp - 0.5f * Physics.gravity * t * t) / t;
        rb.AddForce(v0, ForceMode.VelocityChange);"""

HELPER_SRC = """using UnityEngine;

public class {cls} : MonoBehaviour
{{
    public float wind = 0f;
}}
"""


def _source(cls, variant=None, serialize_private=False, muzzle_field="muzzle", prefab_field="projectilePrefab"):
    field_attr = "[SerializeField] private" if serialize_private else "public"
    extra = ""
    body = SOLVE_BODY_VELOCITY_CHANGE
    if variant == "impulse-mass":
        body = SOLVE_BODY_IMPULSE_MASS
    elif variant == "no-drag":
        body = SOLVE_BODY_NO_DRAG
    elif variant == "wrong-forcemode":
        body = SOLVE_BODY_WRONG_FORCEMODE
    elif variant == "wrong-gravity":
        body = SOLVE_BODY_WRONG_GRAVITY
    elif variant == "fixed-angle":
        body = SOLVE_BODY_FIXED_ANGLE
    elif variant == "first-target":
        extra = "    private Vector3 _cachedGoal = Vector3.zero;\n"
        body = SOLVE_BODY_FIRST_TARGET
    elif variant == "local-space":
        body = SOLVE_BODY_LOCAL_SPACE
    elif variant == "strip-rigidbody":
        body = SOLVE_BODY_STRIP_RIGIDBODY
    marker = "// usk-fake: %s" % variant if variant else ""
    return LAUNCHER_SRC.format(
        marker=marker,
        cls=cls,
        field_attr=field_attr,
        muzzle_field=muzzle_field,
        prefab_field=prefab_field,
        extra_fields=extra,
        body=body,
    )


def _create_script(ctx, name, content, folder=None):
    res = ctx.c.write("script_create", scriptName=name, folder=folder or ctx.asset("Scripts"), content=content)
    if isinstance(res, dict) and res.get("jobId"):
        ctx.wait_job(res["jobId"])
    ctx.wait_idle()
    return res.get("path") if isinstance(res, dict) else None


def _attach(ctx, holder_ref, muzzle_path=None, prefab_path=None, muzzle_field="muzzle",
            prefab_field="projectilePrefab"):
    cls = ctx.private.class_name(ctx.handles)
    at = V.address(holder_ref)
    ctx.c.write("component_add", componentType=cls, **at)
    if muzzle_path is not None:
        ctx.c.write("component_set_serialized_property", componentType=cls, propertyPath=muzzle_field,
                    referencePath=muzzle_path, objectType="Transform", **at)
    if prefab_path is not None:
        ctx.c.write("component_set_serialized_property", componentType=cls, propertyPath=prefab_field,
                    assetPath=prefab_path, **at)


def _answer(ctx, holder_ref=None):
    h = ctx.handles
    target_holder = holder_ref if holder_ref is not None else h["battery"]
    info = ctx.probe.go(target_holder) or {}
    return {
        "scriptPath": h["script"],
        "launcherPath": info.get("path"),
        "projectilePrefabPath": h["prefab"],
        "declaredComplete": True,
    }


def _solve(ctx, variant=None, serialize_private=False, holder_ref=None, muzzle_path=None):
    h = ctx.handles
    cls = ctx.private.class_name(h)
    _create_script(ctx, cls, _source(cls, variant=variant, serialize_private=serialize_private))
    _attach(
        ctx,
        holder_ref if holder_ref is not None else h["battery"],
        muzzle_path if muzzle_path is not None else h["muzzlePath"],
        h["prefab"],
    )
    return _answer(ctx, holder_ref=holder_ref)


def _write_file(ctx, asset_path, data):
    target = os.path.join(ctx.project_path, *asset_path.split("/"))
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, "wb") as handle:
        handle.write(data)


# ---------------------------------------------------------------- reference and alternatives


def reference(ctx):
    return _solve(ctx)


def impulse_with_mass(ctx):
    """Uses ForceMode.Impulse scaled by rb.mass and [SerializeField] private fields with reciprocal drag factor."""
    return _solve(ctx, variant="impulse-mass", serialize_private=True)


def save_run_scene(ctx):
    answer = reference(ctx)
    ctx.c.write("scene_save")
    return answer


# ---------------------------------------------------------------- negatives


def no_drag(ctx):
    """Closed-form vacuum parabola ignoring Rigidbody linear drag."""
    return _solve(ctx, variant="no-drag")


def wrong_forcemode(ctx):
    """Passes initial velocity with ForceMode.Force instead of VelocityChange / mass-scaled Impulse."""
    return _solve(ctx, variant="wrong-forcemode")


def wrong_gravity(ctx):
    """Solves trajectory assuming -9.81 m/s^2 instead of active Physics.gravity."""
    return _solve(ctx, variant="wrong-gravity")


def fixed_angle(ctx):
    """Fires at a fixed angle and speed along muzzle.forward without solving for the target."""
    return _solve(ctx, variant="fixed-angle")


def first_target_only(ctx):
    """Caches the first target's world position and fires all subsequent shots at the first target."""
    return _solve(ctx, variant="first-target")


def local_space_muzzle(ctx):
    """Computes displacement in muzzle.InverseTransformPoint(target.position) and passes that local vector directly to
    world-space AddForce without rotating back."""
    return _solve(ctx, variant="local-space")


def strip_rigidbody_values(ctx):
    """Overwrites the spawned Rigidbody's mass to 1 and drag to 0 instead of keeping the prefab's Rigidbody values."""
    return _solve(ctx, variant="strip-rigidbody")


def on_decoy_stand(ctx):
    """Attaches the component to the Stand decoy rig instead of Battery."""
    h = ctx.handles
    stand_muzzle_path = (ctx.probe.go(h["standMuzzle"]) or {}).get("path")
    return _solve(ctx, holder_ref=h["stand"], muzzle_path=stand_muzzle_path)


def field_renamed(ctx):
    """Renames `muzzle` to `barrel`, failing the R1 field contract and R3 serialized wiring."""
    h = ctx.handles
    cls = ctx.private.class_name(h)
    _create_script(ctx, cls, _source(cls, muzzle_field="barrel"))
    _attach(ctx, h["battery"], muzzle_path=h["muzzlePath"], prefab_path=h["prefab"], muzzle_field="barrel")
    return _answer(ctx)


def modified_gravity(ctx):
    """Changes Physics.gravity to -9.81 in Edit Mode instead of solving for the project's gravity."""
    answer = reference(ctx)
    ctx.c.write("physics_set_gravity", x=0.0, y=-9.81, z=0.0)
    return answer


def compile_error(ctx):
    """Writes a script with a C# syntax error; compilation fails and the component cannot be attached."""
    h = ctx.handles
    cls = ctx.private.class_name(h)
    broken = _source(cls).replace("return shell;", "return shell; {")
    _create_script(ctx, cls, broken)
    try:
        _attach(ctx, h["battery"], h["muzzlePath"], h["prefab"])
    except SkillError:
        pass
    out = _answer(ctx)
    out["declaredComplete"] = False
    return out


def extra_script(ctx):
    """Adds an extra helper C# script under Assets."""
    answer = reference(ctx)
    cls = ctx.private.class_name(ctx.handles)
    helper = "BallisticHelper_" + cls.split("_", 1)[1]
    _create_script(ctx, helper, HELPER_SRC.format(cls=helper))
    return answer


def wrong_report(ctx):
    """Reports the Stand path instead of the actual holder path in the answer."""
    answer = reference(ctx)
    answer["launcherPath"] = ctx.handles["standPath"]
    return answer


def outside_root(ctx):
    """Creates a stray object at the scene root outside R."""
    answer = reference(ctx)
    ctx.c.write("gameobject_create", name="StrayTarget")
    return answer


def direct_file(ctx):
    """Writes a non-script file directly under A on disk, bypassing skills."""
    answer = reference(ctx)
    _write_file(ctx, ctx.asset("Prefabs", "ballistics.txt"), b"table\n")
    return answer


NEGATIVES = [
    Negative("no_drag", no_drag, {"D10"}, "closed-form vacuum parabola ignores Rigidbody linear drag"),
    Negative("wrong_forcemode", wrong_forcemode, {"D9", "D10"},
             "ForceMode.Force instead of VelocityChange or mass-scaled Impulse"),
    Negative("wrong_gravity", wrong_gravity, {"D10"}, "launch computed for -9.81 instead of active Physics.gravity"),
    Negative("fixed_angle", fixed_angle, {"D10"}, "fires at a fixed angle and speed"),
    Negative("first_target_only", first_target_only, {"D10"}, "hits only the first target"),
    Negative("local_space_muzzle", local_space_muzzle, {"D10"},
             "solves in the rotated muzzle's local space without transforming back to world space"),
    Negative("strip_rigidbody_values", strip_rigidbody_values, {"D9", "D10"},
             "overwrites the spawned Rigidbody mass and drag instead of keeping the prefab's values"),
    Negative("on_decoy_stand", on_decoy_stand, {"D3", "D9", "D10"},
             "component attached to the decoy Stand rig instead of Battery"),
    Negative("field_renamed", field_renamed, {"D1", "D3", "D9", "D10"}, "serialized field muzzle renamed to barrel"),
    Negative("modified_gravity", modified_gravity, {"D4", "D9", "D10"}, "project gravity modified in Edit Mode"),
    Negative("compile_error", compile_error, {"D2", "D3", "D9", "D10"}, "script has a syntax error"),
    Negative("extra_script", extra_script, {"D5"}, "an extra C# file added under Assets"),
    Negative("wrong_report", wrong_report, {"D8"}, "answer reports the wrong launcher path"),
    Negative("outside_root", outside_root, {"D6"}, "stray object created outside R"),
    Negative("direct_file", direct_file, {"D7"}, "a non-script file written directly under A"),
]

ALTERNATIVES = [
    ("impulse_with_mass", impulse_with_mass),
    ("save_run_scene", save_run_scene),
]

# Producer:Betsy
