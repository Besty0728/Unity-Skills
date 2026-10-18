"""DC4 private verifier: a ballistic launcher that spawns a Rigidbody projectile from a rotated muzzle and launches it
through Unity physics (accounting for mass, linear drag, project gravity and fixedDeltaTime) so that each of three
shots reaches its hidden target pad within 0.25 m.

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
import math
import os
import random
import re

from usk_eval import verifylib as V

COMMENT_RE = re.compile(r"//[^\n]*|/\*.*?\*/", re.S)
SCRIPT_RE = re.compile(r"\.cs(\.meta)?$")
HIT_TOL = 0.25
VALUE_TOL = 1e-3
FLIGHT_WINDOW_S = 2.5
FIXED_DT = 0.02

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
    ("D9", 1.25, False, ["R2", "R6"], "in Play Mode each Fire(target) returns a newly spawned projectile at the "
                                      "muzzle keeping the prefab's non-kinematic Rigidbody mass, drag and useGravity"),
    ("D10", 3.75, False, ["R2", "R6"], "in Play Mode all 3 shots follow a free-flight ballistic trajectory under "
                                       "active gravity and drag and reach their target within 0.25 m (fraction of 3 "
                                       "shots) with no error logged"),
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
    "D4": {"zh-CN": "不要改动弹丸预制体、Physics.gravity 或场景中已有的对象与变换。",
           "en": "Do not modify the projectile prefab, Physics.gravity, or existing scene objects and transforms."},
    "D5": {"zh-CN": "除该发射器脚本外，不要新增或改动任何其他代码文件。",
           "en": "Do not add or modify any code file other than the launcher script."},
    "D6": {"zh-CN": "所有修改都必须在 R 与 A 之内，且只能保存本轮场景。",
           "en": "Keep every change inside R and A, and save no scene but this run's."},
    "D7": {"zh-CN": "除 C# 脚本外，不要绕过技能直接在 A 下写文件。",
           "en": "Apart from C# scripts, do not write files under A directly; use the skills."},
    "D8": {"zh-CN": "在答案中如实填写 scriptPath、launcherPath、projectilePrefabPath 以及 declaredComplete。",
           "en": "Report the actual scriptPath, launcherPath, projectilePrefabPath and declaredComplete in the answer."},
    "D9": {"zh-CN": "Fire(target) 必须在 muzzle 的世界位置实例化 projectilePrefab 并返回该实例，保留预制体 Rigidbody 的 "
                    "mass、drag 与 useGravity（非 kinematic），不要把弹丸直接瞬移或改写其刚体属性。",
           "en": "Fire(target) must instantiate projectilePrefab at muzzle.position and return that instance with the "
                 "prefab Rigidbody's mass, drag and useGravity intact (non-kinematic); do not teleport it or overwrite "
                 "its Rigidbody settings."},
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


# ---------------------------------------------------------------- reference physics math


def _dist(a, b):
    return math.sqrt(sum((x - y) ** 2 for x, y in zip(a, b)))


def simulate_trajectory(origin, velocity, gravity_y, drag, steps=130, dt=FIXED_DT):
    """Discrete Symplectic Euler trajectory matching PhysX with linear drag under FixedUpdate step dt:
    each step applies gravity v += g * dt, damping v *= 1 / (1 + drag * dt) (or clamp(1 - drag * dt)),
    and position integration p += v * dt. Returns list of (t, (x, y, z), (vx, vy, vz)) for both PhysX
    damping conventions so a small solver difference between 1/(1+d*dt) and (1-d*dt) is accepted."""
    trajectories = []
    for mode in ("linear", "reciprocal"):
        px, py, pz = float(origin[0]), float(origin[1]), float(origin[2])
        vx, vy, vz = float(velocity[0]), float(velocity[1]), float(velocity[2])
        points = [(0.0, (px, py, pz), (vx, vy, vz))]
        for step in range(1, steps + 1):
            vy += float(gravity_y) * dt
            factor = max(0.0, 1.0 - float(drag) * dt) if mode == "linear" else 1.0 / (1.0 + float(drag) * dt)
            vx *= factor
            vy *= factor
            vz *= factor
            px += vx * dt
            py += vy * dt
            pz += vz * dt
            points.append((round(step * dt, 4), (px, py, pz), (vx, vy, vz)))
        trajectories.append(points)
    return trajectories


def solve_launch_velocity(origin, goal, gravity_y, drag, flight_steps=60, dt=FIXED_DT, mode="linear"):
    """Exact discrete inverse for initial velocity v0 so that after `flight_steps` FixedUpdate steps of length `dt`
    the projectile starting at `origin` lands exactly at `goal`."""
    factor = max(0.0, 1.0 - float(drag) * dt) if mode == "linear" else 1.0 / (1.0 + float(drag) * dt)
    sum_f = 0.0
    grav_y = 0.0
    cur_f = 1.0
    vy_g = 0.0
    for _ in range(flight_steps):
        cur_f *= factor
        sum_f += cur_f * dt
        vy_g = (vy_g + float(gravity_y) * dt) * factor
        grav_y += vy_g * dt
    dx = float(goal[0]) - float(origin[0])
    dy = float(goal[1]) - float(origin[1])
    dz = float(goal[2]) - float(origin[2])
    return (dx / sum_f, (dy - grav_y) / sum_f, dz / sum_f)


def min_distance_to_goal(points, goal):
    return min(_dist(pos, goal) for _t, pos, _vel in points)


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
        if key == "battery":
            before.pop("components", None)
            now.pop("components", None)
        ok, diff = V.same(before, now)
        if not ok:
            diffs[key] = diff
    for ref, state in c0.get("pads") or []:
        ok, diff = V.same(state, V.go_state(p, ref))
        if not ok:
            diffs["pad_%s" % str(ref)] = diff
    card.check("D4", not diffs, actual=diffs)

    added = sorted(set(cs_files(ctx.project_path)) - set(c0.get("scripts") or []))
    card.check("D5", set(added) <= {h["script"]}, expected=[h["script"]], actual=added)

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


def SCENARIO(ctx):
    """Play Mode: invokes Fire(pad) on the launcher component for each of the 3 target pads, waiting 2.6 s after each
    shot so the projectile completes its flight, and ends with an isPlaying assert."""
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
        arg_ref = V.address(pad, "reference")
        steps.append({
            "kind": "invoke",
            "skill": "component_invoke_method",
            "args": dict({
                "componentType": cls,
                "methodName": "Fire",
                "parameterTypes": json.dumps(["UnityEngine.Transform"]),
                "arguments": json.dumps([arg_ref]),
            }, **where),
        })
        steps.append({"kind": "wait_seconds", "seconds": 2.6})
    steps.append({"kind": "read", "skill": "physics_get_gravity", "args": {}})
    steps.append(check)
    return {"executionMode": "play", "timeoutSeconds": 180, "steps": steps}


def _extract_shot_metrics(ret_val, c0_muzzle_pos, pad_pos, spec):
    """Evaluates a single Fire(pad) invocation return dict. In the offline model (and live runner when extended with
    spawned-object inspection or trajectory telemetry), returnValue holds the returned GameObject descriptor and may
    carry the spawned Rigidbody state and impact / trajectory summary."""
    if not isinstance(ret_val, dict):
        return {"spawned": False, "rbOk": False, "hit": False, "minDist": None}
    obj_id = V.object_id(ret_val)
    obj_name = str(ret_val.get("name") or "")
    if obj_id is None and not obj_name:
        return {"spawned": False, "rbOk": False, "hit": False, "minDist": None}
    rb = ret_val.get("rigidbody") if isinstance(ret_val.get("rigidbody"), dict) else {}
    spawn_pos = V.floats(ret_val.get("spawnPosition") or c0_muzzle_pos)
    at_muzzle = len(spawn_pos) == 3 and _dist(spawn_pos, c0_muzzle_pos) <= 0.15
    mass_ok = V.close_tuple(rb.get("mass", spec["mass"]), (spec["mass"],), 1e-2)
    drag_ok = V.close_tuple(rb.get("drag", spec["drag"]), (spec["drag"],), 1e-2)
    grav_ok = rb.get("useGravity", True) is True and rb.get("isKinematic", False) is False
    vel = V.floats(ret_val.get("launchVelocity") or [])
    speed_ok = len(vel) == 3 and math.sqrt(sum(v * v for v in vel)) >= 1.0
    rb_ok = at_muzzle and mass_ok and drag_ok and grav_ok and speed_ok

    min_dist = ret_val.get("minDistance")
    if isinstance(min_dist, (int, float)):
        min_dist = float(min_dist)
    elif len(vel) == 3 and len(spawn_pos) == 3:
        sim_grav = float(ret_val.get("gravityY", spec["gravityY"]))
        sim_drag = float(rb.get("drag", spec["drag"]))
        trajs = simulate_trajectory(spawn_pos, vel, sim_grav, sim_drag)
        min_dist = min(min_distance_to_goal(pts, pad_pos) for pts in trajs)
    else:
        min_dist = None
    hit = min_dist is not None and min_dist <= HIT_TOL and rb_ok
    return {
        "id": obj_id,
        "name": obj_name,
        "spawned": True,
        "rbOk": rb_ok,
        "hit": hit,
        "minDist": round(min_dist, 4) if min_dist is not None else None,
    }


def verify_runtime(ctx, card):
    scenario = ctx.scenario or {}
    passed = scenario.get("passed") is True
    spec = launcher_spec(ctx.seed)
    code = COMMENT_RE.sub("", _text(ctx, ctx.handles["script"]) or "")
    cheating = _cheating_source(code)

    c0_muzzle_pos = V.floats((ctx.c0.get("muzzle") or {}).get("position"))
    pad_positions = [V.floats((state or {}).get("position")) for _ref, state in ctx.c0.get("pads") or []]
    invokes = [step for step in scenario.get("steps") or []
               if isinstance(step, dict) and step.get("kind") == "invoke"]
    grav_reads = [step for step in scenario.get("steps") or []
                  if isinstance(step, dict) and step.get("kind") == "read" and
                  step.get("skill") == "physics_get_gravity"]
    grav_ok = True
    if grav_reads:
        actual_grav = grav_reads[-1].get("actual") or {}
        grav_ok = V.close_tuple(actual_grav.get("y"), (spec["gravityY"],), VALUE_TOL)

    shots = []
    seen_ids = set()
    for index, pad_pos in enumerate(pad_positions):
        step = invokes[index] if index < len(invokes) else {}
        actual = step.get("actual") if isinstance(step.get("actual"), dict) else {}
        ret_val = actual.get("returnValue") if isinstance(actual, dict) else None
        metrics = _extract_shot_metrics(ret_val, c0_muzzle_pos, pad_pos, spec)
        distinct = metrics["id"] is None or metrics["id"] not in seen_ids
        if metrics["id"] is not None:
            seen_ids.add(metrics["id"])
        metrics["distinct"] = distinct
        shots.append(metrics)

    d9_ok = (passed and not cheating and grav_ok and len(shots) == 3 and
             all(s["spawned"] and s["rbOk"] and s["distinct"] for s in shots))
    card.check("D9", d9_ok, actual={"passed": passed, "cheating": cheating, "gravityOk": grav_ok, "shots": shots})

    hits = sum(1 for s in shots if s["hit"] and s["distinct"]) if (passed and not cheating and grav_ok) else 0
    fraction = hits / 3.0 if len(pad_positions) == 3 else 0.0
    card.fraction("D10", fraction,
                  expected={"hits": 3, "tolerance": HIT_TOL},
                  actual={"passed": passed, "error": scenario.get("error"), "status": scenario.get("status"),
                          "hits": hits, "shots": shots})

# Producer:Betsy
