"""DC4 private verifier: a ballistic launcher that spawns a Rigidbody projectile from a rotated muzzle and launches it
through Unity physics (accounting for mass, linear drag, project gravity and fixedDeltaTime) so that each of three
shots passes within 0.25 m of its hidden target pad.

Flight is measured in Play Mode by a fixture-owned gauge component on each pad (a script that records the closest
approach of every Rigidbody to the pad and flags bodies that were spawned away from the muzzle or whose Rigidbody
settings differ from the prefab's). The scenario reads each pad's gauge before and after its shot; the pads have no
colliders, so projectiles fly through them.

What the task probes:
  * Closed-form vacuum trajectory ignores Rigidbody linear drag (~0.35-0.55) and falls 1.5-3.0 m short.
  * ForceMode confusion: passing a velocity vector with ForceMode.Force (divides by mass and multiplies by dt) or
    ForceMode.Impulse (divides by mass when mass != 1) instead of ForceMode.VelocityChange / v * mass with Impulse /
    direct velocity assignment.
  * Hard-coding Earth gravity (-9.81) instead of reading Physics.gravity (seeded between -12 and -16 m/s^2).
  * Firing along muzzle.forward or at a fixed angle / speed, or solving in the rotated muzzle's local space without
    rotating the world displacement / velocity back to world space.
  * Hitting only the first target (caching the first target or firing a fixed vector).
  * Overwriting the spawned Rigidbody's mass/drag/useGravity/isKinematic or spawning a primitive without the prefab's
    Rigidbody values.
  * Attaching the component to the wrong Muzzle/Stand decoy rig or leaving serialized references unset.
"""
import hashlib
import json
import os
import random
import re

from usk_eval import verifylib as V

COMMENT_RE = re.compile(r"//[^\n]*|/\*.*?\*/", re.S)
SCRIPT_RE = re.compile(r"\.cs(\.meta)?$")
HIT_TOL = 0.25
VALUE_TOL = 1e-3

ASSERTIONS = [
    ("D1", 0.75, False, ["R1"], "the script declares the class, the serialized fields muzzle and projectilePrefab, "
                                "and public GameObject Fire(Transform)"),
    ("D2", 0.5, False, ["R1"], "the script exists at A/Scripts/<cls>.cs and the project compiled without errors"),
    ("D3", 1.25, False, ["R3"], "exactly one component in the scene, on the battery, with muzzle and projectilePrefab "
                                "referencing the given muzzle Transform and projectile prefab"),
    ("D4", 0.75, True, ["R4"], "the projectile prefab, project gravity and all existing scene objects are unchanged"),
    ("D5", 0.5, True, ["R5"], "no code file other than the launcher script was added or modified"),
    ("D6", 0.5, True, ["G1", "G2"], "no change outside R/A, no other scene saved or opened"),
    ("D7", 0.25, False, ["G4"], "no file other than C# scripts was written under A around the skills"),
    ("D8", 0.5, False, ["R7", "G8"], "the answer reports the script path, the launcher's hierarchy path and the "
                                     "projectile prefab path"),
    ("D9", 1.25, False, ["R2", "R6"], "in Play Mode each Fire(target) spawns exactly one projectile at the muzzle "
                                      "keeping the prefab's non-kinematic Rigidbody mass, drag and useGravity, "
                                      "returns it, and the gauges and project gravity are untouched"),
    ("D10", 3.75, False, ["R2", "R6"], "in Play Mode the free flight of each of the 3 projectiles passes within "
                                       "0.25 m of its target pad (fraction of 3 shots) with no error logged"),
]

HINTS = {
    "D1": {"zh-CN": "脚本需声明类、可序列化字段 muzzle（Transform）与 projectilePrefab（GameObject），以及公开方法 "
                    "public GameObject Fire(Transform target)。",
           "en": "Declare the class, the serialized fields muzzle (Transform) and projectilePrefab (GameObject), and "
                 "the public method public GameObject Fire(Transform target)."},
    "D2": {"zh-CN": "脚本放在 A/Scripts 下，文件名与类名一致，并等待编译通过且无错误。",
           "en": "Place the script under A/Scripts with file name matching the class name, and wait for a clean "
                 "compilation."},
    "D3": {"zh-CN": "组件只挂在指定的发射架对象上，并在场景中把 muzzle 指向对应的发射口 Transform、projectilePrefab 指向弹丸预制体。",
           "en": "Attach the component only to the specified battery object, and set muzzle to its muzzle Transform "
                 "and projectilePrefab to the projectile prefab in the scene."},
    "D4": {"zh-CN": "不要改动弹丸预制体、Physics.gravity、场景中已有的对象与变换，以及目标台上已有的组件与设置。",
           "en": "Do not modify the projectile prefab, Physics.gravity, existing scene objects and transforms, or the "
                 "components and settings already on the pads."},
    "D5": {"zh-CN": "除该发射器脚本外，不要新增或改动任何其他代码文件（包括已有脚本）。",
           "en": "Do not add or modify any code file other than the launcher script, existing scripts included."},
    "D6": {"zh-CN": "所有修改都必须在 R 与 A 之内，且只能保存本轮场景。",
           "en": "Keep every change inside R and A, and save no scene but this run's."},
    "D7": {"zh-CN": "除 C# 脚本外，不要绕过技能直接在 A 下写文件。",
           "en": "Apart from C# scripts, do not write files under A directly; use the skills."},
    "D8": {"zh-CN": "在答案中如实填写 scriptPath、launcherPath、projectilePrefabPath 以及 declaredComplete。",
           "en": "Report the actual scriptPath, launcherPath, projectilePrefabPath and declaredComplete in the answer."},
    "D9": {"zh-CN": "每次 Fire(target) 必须恰好在 muzzle 的世界位置实例化一个 projectilePrefab 并返回该实例，保留预制体 "
                    "Rigidbody 的 mass、drag 与 useGravity（非 kinematic），不要把弹丸直接瞬移或改写其刚体属性，也不要改动 "
                    "Physics.gravity。",
           "en": "Each Fire(target) must instantiate exactly one projectilePrefab at muzzle.position and return that "
                 "instance with the prefab Rigidbody's mass, drag and useGravity intact (non-kinematic); do not "
                 "teleport it, overwrite its Rigidbody settings or change Physics.gravity."},
    "D10": {"zh-CN": "发射初速度需同时考虑当前 Physics.gravity、Rigidbody 的线性阻力（drag/linearDamping）、质量（若使用 "
                     "ForceMode.Impulse 需乘以 mass，或使用 ForceMode.VelocityChange）以及 Time.fixedDeltaTime，在世界坐标系下求解每个目标的弹道。",
            "en": "Compute the launch in world space for each target accounting for the active Physics.gravity, the "
                  "Rigidbody's linear drag (drag/linearDamping), mass (multiply by mass for ForceMode.Impulse or use "
                  "ForceMode.VelocityChange), and Time.fixedDeltaTime."},
}


def launcher_spec(seed):
    """Verbatim copy of the public task's launcher_spec(seed)."""
    rng = random.Random("DC4:%d" % seed)
    return {
        "mass": rng.choice((1.5, 2.0, 2.5, 3.0)),
        "drag": rng.choice((0.35, 0.45, 0.55)),
        "gravityY": rng.choice((-12.0, -14.0, -15.0, -16.0)),
    }


def gauge_settings(props):
    props = props or {}
    return {"origin": V.floats(props.get("origin")), "mass": V.floats(props.get("mass")),
            "damping": V.floats(props.get("damping"))}


def class_name(handles):
    return os.path.basename(handles["script"])[:-len(".cs")]


def _disk(ctx, asset_path):
    return os.path.join(ctx.project_path, *asset_path.split("/"))


def _text(ctx, asset_path):
    try:
        with open(_disk(ctx, asset_path), "r", encoding="utf-8-sig") as handle:
            return handle.read()
    except (OSError, UnicodeDecodeError, TypeError):
        return None


def _sha(ctx, asset_path):
    try:
        with open(_disk(ctx, asset_path), "rb") as handle:
            return hashlib.sha256(handle.read()).hexdigest()
    except OSError:
        return None


def cs_files(project_path):
    out = []
    for base, dirs, files in os.walk(os.path.join(project_path, "Assets")):
        dirs.sort()
        out += [os.path.relpath(os.path.join(base, name), project_path).replace(os.sep, "/")
                for name in sorted(files) if name.endswith(".cs")]
    return sorted(out)


def _read(ctx, skill, **args):
    envelope = ctx.probe.c.read_raw(skill, **args)
    if not isinstance(envelope, dict) or envelope.get("status") in ("error", "partial") or envelope.get("errorCode"):
        return None
    result = envelope.get("result", envelope)
    return result if isinstance(result, dict) and result.get("success") is not False else None


def compiled(ctx):
    comp = _read(ctx, "debug_check_compilation") or {}
    last = comp.get("lastCompilation") or {}
    return not comp.get("isCompiling") and last.get("succeeded") is not False and not last.get("errorCount")


def holders(ctx):
    found = _read(ctx, "gameobject_find", component=class_name(ctx.handles), limit=500) or {}
    return [V.object_id(obj) for obj in found.get("objects") or []]


def holder(ctx, found=None):
    found = holders(ctx) if found is None else found
    battery = ctx.handles["battery"]
    return next((ref for ref in found if V.same_id(ref, battery)), found[0] if found else None)


def _has_serialized_field(code, field_type, field_name):
    pattern = (r"(?:public|\[[^\]]*\bSerializeField\b[^\]]*\](?:\s*(?:private|protected|internal|public))?)\s+"
               r"(?:UnityEngine\.)?%s\s+%s\b" % (re.escape(field_type), re.escape(field_name)))
    return re.search(pattern, code) is not None


def _has_fire_method(code):
    pattern = (r"\bpublic\s+(?:UnityEngine\.)?GameObject\s+Fire\s*\(\s*"
               r"(?:UnityEngine\.)?Transform\s+\w+\s*\)")
    return re.search(pattern, code) is not None


def _cheating_source(code):
    """Detects forbidden non-ballistic manipulation in source code (driving position in Update/FixedUpdate/coroutines,
    modifying Physics.gravity or Time.fixedDeltaTime, or parenting to target)."""
    if re.search(r"\bPhysics\.gravity\s*=", code) or re.search(r"\bTime\.fixedDeltaTime\s*=", code):
        return True
    if re.search(r"\b(?:MovePosition|Translate|Lerp|MoveTowards|Slerp)\s*\(", code):
        return True
    if re.search(r"\bSetParent\s*\(", code):
        return True
    return False


# ---------------------------------------------------------------- static verification


def verify(ctx, card):
    p, h, c0 = ctx.probe, ctx.handles, ctx.c0
    cls = class_name(h)
    code_raw = _text(ctx, h["script"])
    code = COMMENT_RE.sub("", code_raw or "")
    has_cls = re.search(r"\bclass\s+%s\s*:\s*(?:UnityEngine\.)?MonoBehaviour\b" % re.escape(cls), code) is not None
    has_muzzle = _has_serialized_field(code, "Transform", "muzzle")
    has_prefab = _has_serialized_field(code, "GameObject", "projectilePrefab")
    has_fire = _has_fire_method(code)
    card.check("D1", bool(code_raw) and has_cls and has_muzzle and has_prefab and has_fire,
               actual={"present": bool(code_raw), "class": has_cls, "muzzle": has_muzzle,
                       "projectilePrefab": has_prefab, "fire": has_fire})

    feedback = (_read(ctx, "script_get_compile_feedback", scriptPath=h["script"]) or {}).get("hasErrors")
    found = holders(ctx)
    card.check("D2", bool(code_raw) and compiled(ctx) and feedback is False and bool(found),
               actual={"present": bool(code_raw), "compiled": compiled(ctx), "hasErrors": feedback, "holders": found})

    graded = holder(ctx, found)
    ser = (p.ser(graded, cls) or {}) if graded is not None else {}
    muzzle_ref = V.parse_ref(ser.get("muzzle"))
    muzzle_owner = p.owner(ser.get("muzzle"))
    prefab_guid = (c0.get("prefab") or {}).get("guid")
    prefab_val = str(ser.get("projectilePrefab") or "")
    prefab_name = os.path.basename(h["prefab"])[:-len(".prefab")]
    prefab_ok = (bool(prefab_val) and "null" not in prefab_val.lower() and
                 (prefab_name in prefab_val or (prefab_guid and prefab_guid.lower() in prefab_val.lower())))
    on_battery = p.component_count(h["battery"], cls)
    card.check("D3", len(found) == 1 and V.same_id(found[0], h["battery"]) and on_battery == 1 and
               bool(muzzle_ref) and muzzle_ref[1] == "UnityEngine.Transform" and
               V.same_id(muzzle_owner, h["muzzle"]) and prefab_ok,
               expected={"holder": h["batteryPath"], "muzzle": h["muzzlePath"], "projectilePrefab": h["prefab"]},
               actual={"holders": found, "onBattery": on_battery, "muzzle": ser.get("muzzle"),
                       "projectilePrefab": ser.get("projectilePrefab")})

    diffs = {}
    now_prefab_sha = _sha(ctx, h["prefab"])
    now_prefab_guid = p.guid(h["prefab"])
    if now_prefab_sha != (c0.get("prefab") or {}).get("sha") or now_prefab_guid != prefab_guid:
        diffs["prefab"] = {"sha": now_prefab_sha, "guid": now_prefab_guid}
    now_grav = _read(ctx, "physics_get_gravity") or {}
    c0_grav = c0.get("gravity") or {}
    if not all(V.close_tuple(now_grav.get(axis), (c0_grav.get(axis),), VALUE_TOL) for axis in ("x", "y", "z")):
        diffs["gravity"] = now_grav
    for key in ("arena", "battery", "muzzle", "stand", "standMuzzle", "deck"):
        before, now = dict(c0.get(key) or {}), dict(V.go_state(p, h[key]) or {})
        if key in ("battery", "stand"):
            before.pop("components", None)
            now.pop("components", None)
        ok, diff = V.same(before, now)
        if not ok:
            diffs[key] = diff
    for ref, state in c0.get("pads") or []:
        ok, diff = V.same(state, V.go_state(p, ref))
        if not ok:
            diffs["pad_%s" % str(ref)] = diff
    for ref, state in c0.get("gaugeSettings") or []:
        ok, diff = V.same(state, gauge_settings(p.ser(ref, h["gauge"])))
        if not ok:
            diffs["gauge_%s" % str(ref)] = diff
    card.check("D4", not diffs, actual=diffs)

    added = sorted(set(cs_files(ctx.project_path)) - set(c0.get("scripts") or []))
    gauge_sha = _sha(ctx, h["gaugeScript"])
    card.check("D5", set(added) <= {h["script"]} and gauge_sha == c0.get("gaugeSha"), expected=[h["script"]],
               actual={"added": added, "gaugeSha": gauge_sha})

    bad = ctx.violations()
    card.check("D6", not bad, actual={"violations": bad[:10]})

    direct = [rec for rec in ctx.direct_writes() if not SCRIPT_RE.search(str(rec.get("assetPath") or ""))]
    card.check("D7", not direct, actual={"directWrites": direct[:10]})

    actual_holder = graded if graded is not None else h["battery"]
    actual_holder_path = (p.go(actual_holder) or {}).get("path")
    card.check("D8", V.norm_path(V.answer(ctx, "scriptPath")) == h["script"] and
               V.norm_path(V.answer(ctx, "launcherPath")) == actual_holder_path and
               V.norm_path(V.answer(ctx, "projectilePrefabPath")) == h["prefab"] and
               isinstance(V.answer(ctx, "declaredComplete"), bool),
               expected={"scriptPath": h["script"], "launcherPath": actual_holder_path,
                         "projectilePrefabPath": h["prefab"]},
               actual=ctx.answer)


def _gauge_step(ctx, pad):
    return {"kind": "read", "skill": "component_get_serialized_properties",
            "args": dict({"componentType": ctx.handles["gauge"]}, **V.address(pad))}


def SCENARIO(ctx):
    """Play Mode: for each of the 3 target pads, reads its gauge, invokes Fire(pad) on the launcher component, waits
    2.6 s for the flight and reads the gauge again; ends with a gravity read and an isPlaying assert."""
    check = {"kind": "assert", "skill": "editor_get_state", "select": "isPlaying", "expected": True}
    if not compiled(ctx):
        return {"executionMode": "edit", "timeoutSeconds": 30, "steps": [check]}
    found = holders(ctx)
    target_holder = holder(ctx, found)
    if target_holder is None:
        return {"executionMode": "edit", "timeoutSeconds": 30, "steps": [check]}
    cls = class_name(ctx.handles)
    where = V.address(target_holder)
    steps = [{"kind": "wait_frames", "frames": 2}]
    for pad in ctx.handles["pads"]:
        steps.append(_gauge_step(ctx, pad))
        steps.append({
            "kind": "invoke",
            "skill": "component_invoke_method",
            "args": dict({
                "componentType": cls,
                "methodName": "Fire",
                "parameterTypes": json.dumps(["UnityEngine.Transform"]),
                "arguments": json.dumps([V.address(pad, "reference")]),
            }, **where),
        })
        steps.append({"kind": "wait_seconds", "seconds": 2.6})
        steps.append(_gauge_step(ctx, pad))
    steps.append({"kind": "read", "skill": "physics_get_gravity", "args": {}})
    steps.append(check)
    return {"executionMode": "play", "timeoutSeconds": 180, "steps": steps}


def _reading(step):
    """Gauge counters of a read step, or None when the step is missing or carries no readable gauge."""
    actual = step.get("actual") if isinstance(step, dict) else None
    props = actual.get("properties") if isinstance(actual, dict) else None
    values = {row.get("propertyPath"): row.get("value") for row in props or [] if isinstance(row, dict)}
    closest = V.floats(values.get("closest"))
    counters = {name: V.as_int(values.get(name)) for name in ("bodies", "altered", "offOrigin")}
    if len(closest) != 1 or any(count is None for count in counters.values()):
        return None
    return dict(counters, closest=closest[0], settings=gauge_settings(values))


def verify_runtime(ctx, card):
    scenario = ctx.scenario or {}
    passed = scenario.get("passed") is True
    spec = launcher_spec(ctx.seed)
    h = ctx.handles
    code = COMMENT_RE.sub("", _text(ctx, h["script"]) or "")
    cheating = _cheating_source(code)
    steps = [step for step in scenario.get("steps") or [] if isinstance(step, dict)]
    invokes = [step for step in steps if step.get("kind") == "invoke"]
    gauge_reads = [step for step in steps if step.get("kind") == "read" and
                   step.get("skill") == "component_get_serialized_properties"]
    grav_reads = [step for step in steps if step.get("kind") == "read" and step.get("skill") == "physics_get_gravity"]
    shots = len(h["pads"])

    grav = (grav_reads[-1].get("actual") or {}) if grav_reads else {}
    grav_ok = bool(grav_reads) and V.close_tuple(grav.get("y"), (spec["gravityY"],), VALUE_TOL)

    readings = [_reading(step) for step in gauge_reads]
    complete = len(readings) == 2 * shots and all(reading is not None for reading in readings)
    before = readings[0::2] if complete else []
    after = readings[1::2] if complete else []
    c0_settings = [state for _ref, state in ctx.c0.get("gaugeSettings") or []]
    settings_ok = complete and len(c0_settings) == shots and all(
        V.same(c0_settings[i], before[i]["settings"])[0] for i in range(shots))
    gauge_ok = _sha(ctx, h["gaugeScript"]) == ctx.c0.get("gaugeSha") and settings_ok

    ids = [V.object_id((step.get("actual") or {}).get("returnValue")) if isinstance(step.get("actual"), dict) else None
           for step in invokes]
    spawned = len(ids) == shots and all(ident is not None for ident in ids) and len({str(i) for i in ids}) == shots
    base = passed and grav_ok and not cheating and gauge_ok
    clean = complete and after[-1]["bodies"] == shots and after[-1]["altered"] == 0 and after[-1]["offOrigin"] == 0
    card.check("D9", base and spawned and clean,
               actual={"passed": passed, "cheating": cheating, "gravityOk": grav_ok, "gaugeOk": gauge_ok,
                       "returned": ids, "gauges": after})

    hits = []
    for index in range(shots):
        shot = after[index] if complete else None
        hits.append(bool(base and shot and shot["closest"] <= HIT_TOL and before[index]["closest"] > HIT_TOL and
                         shot["bodies"] == index + 1 and shot["altered"] == 0 and shot["offOrigin"] == 0))
    card.fraction("D10", sum(hits) / float(shots),
                  expected={"hits": shots, "tolerance": HIT_TOL},
                  actual={"passed": passed, "error": scenario.get("error"), "status": scenario.get("status"),
                          "hits": hits, "before": before, "after": after})

# Producer:Betsy
