"""DS2 private verifier: a trigger-activated sliding door responding only to the Player tag, with repeated entries.

What the task probes:
- Unity trigger callbacks (OnTriggerEnter / OnTriggerExit) require isTrigger = true on the zone's BoxCollider AND a
  Rigidbody on at least one of the two colliding objects (the moving Player starts without a Rigidbody, so the agent
  must add a kinematic Rigidbody with useGravity = false to the Player).
- An untagged visitor (which already carries a kinematic Rigidbody) crosses the trigger zone at t ~ 0.25-0.75 s before
  the Player arrives at t ~ 0.90 s; a script that omits the "Player" tag check opens early and increments enterCount /
  exitCount on the visitor.
- The Player enters the zone [0.90, 2.70] s, exits to P2, then reverses and enters the zone a second time [4.50, 6.30] s
  before leaving to P0: the door must translate to closedPos + openOffset on entry, return to closedPos on exit, and
  re-open / re-close on the second visit while tracking enterCount and exitCount.
"""
import hashlib
import math
import os
import random

from usk_eval import verifylib as V

POSITION_TOL = 0.15     # R5: distance to expected door localPosition
VALUE_TOL = 1e-3

MIN_MARGIN_S = 0.30     # R5: each sample at least 0.30 s after door settles and away from enter/exit instants
SETTLE_HOLD_S = 0.40    # R5: time after door finishes sliding at which the sample is taken
PLAYER_PATH_Z = (-7.0, 0.0, 5.0, 0.0)
PLAYER_SPEED = 5.0
PLAYER_PAUSE = 1.0
VISITOR_PATH_X = (-5.0, 5.0)
VISITOR_SPEED = 10.0
ZONE_HALF_EXTENT = 2.0
CAPSULE_RADIUS = 0.5
TRIGGER_HALF_SPAN = ZONE_HALF_EXTENT + CAPSULE_RADIUS


def event_instants(seed):
    """Visitor/Player trigger enter/exit instants and the door slide duration derived from the fixture and seed."""
    spec = door_spec(seed)
    slide_len = math.sqrt(spec["ox"] ** 2 + spec["oy"] ** 2 + spec["oz"] ** 2)
    slide_s = slide_len / float(spec["speed"])

    v_start, v_end = VISITOR_PATH_X
    v_enter = (abs(-TRIGGER_HALF_SPAN - v_start)) / VISITOR_SPEED
    v_exit = (abs(TRIGGER_HALF_SPAN - v_start)) / VISITOR_SPEED

    p0, p1, p2, p3 = PLAYER_PATH_Z
    leg01 = abs(p1 - p0) / PLAYER_SPEED
    leg12 = abs(p2 - p1) / PLAYER_SPEED
    leg23 = abs(p3 - p2) / PLAYER_SPEED
    leg30 = abs(p0 - p3) / PLAYER_SPEED

    t_p1 = leg01
    t_leave_p1 = t_p1 + PLAYER_PAUSE
    t_p2 = t_leave_p1 + leg12
    t_leave_p2 = t_p2 + PLAYER_PAUSE
    t_p3 = t_leave_p2 + leg23
    t_leave_p3 = t_p3 + PLAYER_PAUSE

    p_enter1 = (abs(-TRIGGER_HALF_SPAN - p0)) / PLAYER_SPEED
    p_exit1 = t_leave_p1 + (TRIGGER_HALF_SPAN - p1) / PLAYER_SPEED
    p_enter2 = t_leave_p2 + (p2 - TRIGGER_HALF_SPAN) / PLAYER_SPEED
    p_exit2 = t_leave_p3 + (p3 - (-TRIGGER_HALF_SPAN)) / PLAYER_SPEED
    p_reenter = t_leave_p3 + leg30 + PLAYER_PAUSE + (abs(-TRIGGER_HALF_SPAN - p0)) / PLAYER_SPEED

    return {
        "slideSeconds": round(slide_s, 4),
        "visitorEnter": round(v_enter, 4),
        "visitorExit": round(v_exit, 4),
        "playerEnter1": round(p_enter1, 4),
        "playerOpen1": round(p_enter1 + slide_s, 4),
        "playerExit1": round(p_exit1, 4),
        "playerClose1": round(p_exit1 + slide_s, 4),
        "playerEnter2": round(p_enter2, 4),
        "playerOpen2": round(p_enter2 + slide_s, 4),
        "playerExit2": round(p_exit2, 4),
        "playerClose2": round(p_exit2 + slide_s, 4),
        "playerReenter": round(p_reenter, 4),
    }


def sample_schedule(seed):
    """[(moment, is_open, enterCount, exitCount)] derived from the seed, >= 0.30 s after door settles and away from
    every Player enter/exit instant."""
    ev = event_instants(seed)
    t0 = round(0.5 * (ev["visitorEnter"] + ev["visitorExit"]), 3)
    t1 = round(ev["playerOpen1"] + SETTLE_HOLD_S, 3)
    t2 = round(ev["playerClose1"] + SETTLE_HOLD_S, 3)
    t3 = round(ev["playerOpen2"] + SETTLE_HOLD_S, 3)
    t4 = round(ev["playerClose2"] + SETTLE_HOLD_S, 3)
    return (
        (t0, False, 0, 0),      # visitor inside zone; player has not entered yet (>= 0.30 s before playerEnter1)
        (t1, True, 1, 0),       # player inside zone (1st visit), door open (>= 0.30 s after open, before exit1)
        (t2, False, 1, 1),      # player outside at P2 (after 1st exit), door closed (>= 0.30 s after close, before enter2)
        (t3, True, 2, 1),       # player inside zone (2nd visit), door open again (>= 0.30 s after open, before exit2)
        (t4, False, 2, 2),      # player outside heading to P0 (after 2nd exit), door closed again (>= 0.30 s after close)
    )


def sample_times(seed):
    return [row[0] for row in sample_schedule(seed)]

ASSERTIONS = [
    ("D1", 1.0, False, ["R1"], "the script exists at A/Scripts/<cls>.cs, compiled without errors and its class is "
                               "loaded"),
    ("D2", 1.25, False, ["R1", "R3"], "exactly one component in the scene, on the zone, with door = Door's Transform, "
                                      "openOffset and speed as given, and enterCount = exitCount = 0"),
    ("D3", 1.25, False, ["R4"], "the zone's BoxCollider has isTrigger = true and the player has a Rigidbody with "
                                "isKinematic = true and useGravity = false"),
    ("D4", 0.5, True, ["R6"], "no other script was added, removed or modified"),
    ("D5", 0.75, False, ["R7"], "the hall, door, zone, player, visitor and waypoints keep their names, tags and "
                                "transforms"),
    ("D6", 0.5, True, ["G1", "G2"], "no change outside R/A, no other scene saved or opened"),
    ("D7", 0.25, False, ["G4"], "no file other than C# scripts was written under A around the skills"),
    ("D8", 0.5, False, ["R8", "G8"], "the answer reports the script path and the zone, door and player paths"),
    ("D9", 2.5, False, ["R2", "R5"], "in Play Mode the door's localPosition matches the expected open/closed state at "
                                     "every sample (fraction) and no error is logged"),
    ("D10", 1.5, False, ["R2", "R5"], "in Play Mode enterCount and exitCount match the expected Player trigger counts "
                                      "at every sample (fraction) and no error is logged"),
]

HINTS = {
    "D1": {"zh-CN": "脚本放在 A/Scripts 下，文件名与类名一致，等编译通过后再挂组件。",
           "en": "Put the script under A/Scripts, named after its class, and wait for a clean compile before adding "
                 "the component."},
    "D2": {"zh-CN": "组件只挂在触发区对象上，并在场景中设置 door（Door 的 Transform）、openOffset 和 speed，enterCount 与 exitCount 初始为 0。",
           "en": "Attach the component only to the trigger zone object and set door (Door's Transform), openOffset "
                 "and speed in the scene, with enterCount and exitCount starting at 0."},
    "D3": {"zh-CN": "将触发区 BoxCollider 的 isTrigger 设为 true，并给 Player 添加 Rigidbody（isKinematic = true，useGravity = false）。",
           "en": "Set isTrigger = true on the zone's BoxCollider, and add a Rigidbody (isKinematic = true, "
                 "useGravity = false) to the Player."},
    "D4": {"zh-CN": "除新建门控制脚本外，不要新增、删除或修改其他任何代码文件。",
           "en": "Apart from the new door script, do not add, delete or modify any other code file."},
    "D5": {"zh-CN": "不要改动门、触发区、Player、访客及路点的名称、标签或变换。",
           "en": "Do not modify the names, tags or transforms of the door, zone, Player, visitor or waypoints."},
    "D6": {"zh-CN": "所有修改都必须在 R 与 A 之内，且只能保存本轮场景。",
           "en": "Keep every change inside R and A, and save no scene but this run's."},
    "D7": {"zh-CN": "除 C# 脚本外，不要绕过技能直接在 A 下写文件。",
           "en": "Apart from C# scripts, do not write files under A directly; use the skills."},
    "D8": {"zh-CN": "答案里报告脚本路径以及触发区、门和 Player 的完整层级路径，并写明 declaredComplete。",
           "en": "Report the script path and the full hierarchy paths of the zone, door and Player, plus "
                 "declaredComplete."},
    "D9": {"zh-CN": "用 door.localPosition 在初始位置与 closedPos + openOffset 之间按 speed 速率平移；离开后移回初始位置，再次进入可再次打开；只响应 Player 标签。",
           "en": "Move door.localPosition between closedPos and closedPos + openOffset at rate speed; return to "
                 "closedPos on exit and re-open on repeated entry; respond only to the Player tag."},
    "D10": {"zh-CN": "只在标签为 Player 的碰撞体进入/离开触发区时分别将 enterCount / exitCount 加 1，忽略其他标签对象。",
            "en": "Increment enterCount and exitCount only when a collider tagged Player enters or leaves the trigger "
                  "zone; ignore other tags."},
}


def door_spec(seed):
    """Verbatim copy of the public task's door_spec(seed) (pinned by unit tests)."""
    rng = random.Random("DS2:%d" % seed)
    offsets = ((0.0, 3.0, 0.0), (0.0, 3.6, 0.0), (3.0, 0.0, 0.0), (-3.0, 0.0, 0.0))
    ox, oy, oz = rng.choice(offsets)
    return {"ox": ox, "oy": oy, "oz": oz, "speed": rng.choice((6.0, 7.5, 9.0))}


def class_name(handles):
    return os.path.basename(handles["script"])[:-len(".cs")]


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


def _distance(a, b):
    return math.sqrt(sum((x - y) ** 2 for x, y in zip(a, b)))


def _read(ctx, skill, **args):
    """Result of a read-only skill, or None when it answers with a business error."""
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
    zone = ctx.handles["zone"]
    return next((ref for ref in found if V.same_id(ref, zone)), found[0] if found else None)


def _as_bool(val):
    if isinstance(val, bool):
        return val
    if isinstance(val, (int, float)):
        return bool(val)
    if isinstance(val, str):
        low = val.strip().lower()
        if low in ("true", "1"):
            return True
        if low in ("false", "0"):
            return False
    return None


def _read_vec3(ser, prefix):
    direct = V.floats(ser.get(prefix))
    if len(direct) == 3:
        return tuple(direct)
    coords = [ser.get("%s.%s" % (prefix, axis)) for axis in ("x", "y", "z")]
    parsed = V.floats(coords)
    return tuple(parsed) if len(parsed) == 3 else ()


def _bool_prop(ctx, ref, comp_type, ser_names, prop_name):
    ser = ctx.probe.ser(ref, comp_type) or {}
    for name in ser_names:
        if name in ser:
            b = _as_bool(ser.get(name))
            if b is not None:
                return b
    res = _read(ctx, "component_get_properties", componentType=comp_type, **V.address(ref)) or {}
    props = {row.get("name"): row.get("value") for row in res.get("properties") or [] if isinstance(row, dict)}
    if prop_name in props:
        return _as_bool(props.get(prop_name))
    return None


def verify(ctx, card):
    p, h, c0 = ctx.probe, ctx.handles, ctx.c0
    cls, spec = class_name(h), door_spec(ctx.seed)
    found = holders(ctx)
    comp = _read(ctx, "debug_check_compilation") or {}
    last = comp.get("lastCompilation") or {}
    feedback = (_read(ctx, "script_get_compile_feedback", scriptPath=h["script"]) or {}).get("hasErrors")
    present = os.path.isfile(os.path.join(ctx.project_path, *h["script"].split("/")))
    card.check("D1", present and not comp.get("isCompiling") and last.get("succeeded") is not False and
               not last.get("errorCount") and feedback is False and bool(found),
               actual={"present": present, "isCompiling": comp.get("isCompiling"), "hasErrors": feedback,
                       "last": {k: last.get(k) for k in ("succeeded", "errorCount")}, "holders": found})

    graded = holder(ctx, found)
    ser = (p.ser(graded, cls) or {}) if graded is not None else {}
    door_ref = V.parse_ref(ser.get("door"))
    door_owner = p.owner(ser.get("door"))
    offset_val = _read_vec3(ser, "openOffset")
    speed_val = ser.get("speed")
    enter_init = V.as_int(ser.get("enterCount")) if "enterCount" in ser else None
    exit_init = V.as_int(ser.get("exitCount")) if "exitCount" in ser else None
    on_zone = p.component_count(h["zone"], cls)
    want_offset = (spec["ox"], spec["oy"], spec["oz"])
    d2_ok = (len(found) == 1 and V.same_id(found[0], h["zone"]) and on_zone == 1 and
             bool(door_ref) and door_ref[1] == "UnityEngine.Transform" and V.same_id(door_owner, h["door"]) and
             V.close_tuple(offset_val, want_offset, VALUE_TOL) and
             V.close_tuple(speed_val, (spec["speed"],), VALUE_TOL) and
             enter_init == 0 and exit_init == 0)
    card.check("D2", d2_ok,
               expected={"holder": h["zonePath"], "door": h["doorPath"], "openOffset": want_offset,
                         "speed": spec["speed"], "enterCount": 0, "exitCount": 0},
               actual={"holders": found, "onZone": on_zone, "door": ser.get("door"), "openOffset": offset_val,
                       "speed": speed_val, "enterCount": enter_init, "exitCount": exit_init})

    zone_has_box = p.component_count(h["zone"], "BoxCollider") > 0
    is_trigger = _bool_prop(ctx, h["zone"], "BoxCollider", ("m_IsTrigger", "isTrigger"), "isTrigger") if zone_has_box else None
    player_has_rb = p.component_count(h["player"], "Rigidbody") > 0
    is_kinematic = _bool_prop(ctx, h["player"], "Rigidbody", ("m_IsKinematic", "isKinematic"), "isKinematic") if player_has_rb else None
    use_gravity = _bool_prop(ctx, h["player"], "Rigidbody", ("m_UseGravity", "useGravity"), "useGravity") if player_has_rb else None
    card.check("D3", bool(zone_has_box) and is_trigger is True and bool(player_has_rb) and
               is_kinematic is True and use_gravity is False,
               expected={"zoneBoxColliderIsTrigger": True, "playerRigidbody": True,
                         "isKinematic": True, "useGravity": False},
               actual={"zoneBoxCollider": zone_has_box, "isTrigger": is_trigger, "playerRigidbody": player_has_rb,
                       "isKinematic": is_kinematic, "useGravity": use_gravity})

    before_scripts = {item[0]: item[1] for item in c0.get("scripts") or []}
    now_scripts = cs_files(ctx.project_path)
    changed_scripts = sorted(path for path, sha in before_scripts.items()
                             if path not in now_scripts or file_sha(ctx.project_path, path) != sha)
    extra_scripts = sorted(set(now_scripts) - set(before_scripts) - {h["script"]})
    card.check("D4", not changed_scripts and not extra_scripts,
               expected={"added": [h["script"]], "changed": []},
               actual={"extra": extra_scripts, "changed": changed_scripts})

    diffs = {}
    for key in ("hall", "door", "visitor", "trackP", "trackV"):
        ok, diff = V.same(c0.get(key), V.go_state(p, h[key]))
        if not ok:
            diffs[key] = diff
    for list_key in ("pWaypoints", "vWaypoints"):
        for ref, state in c0.get(list_key) or []:
            ok, diff = V.same(state, V.go_state(p, ref))
            if not ok:
                diffs["%s:%s" % (list_key, ref)] = diff
    for key in ("zone", "player"):
        before, now = dict(c0.get(key) or {}), dict(V.go_state(p, h[key]) or {})
        before.pop("components", None)
        now.pop("components", None)
        ok, diff = V.same(before, now)
        if not ok:
            diffs[key] = diff
    card.check("D5", not diffs, actual=diffs)

    bad = ctx.violations()
    card.check("D6", not bad, actual={"violations": bad[:10]})
    direct = [rec for rec in ctx.direct_writes() if not str(rec.get("assetPath") or "").endswith((".cs", ".cs.meta"))]
    card.check("D7", not direct, actual={"directWrites": direct[:10]})
    card.check("D8", V.norm_path(V.answer(ctx, "scriptPath")) == h["script"] and
               V.norm_path(V.answer(ctx, "zonePath")) == (p.go(h["zone"]) or {}).get("path") and
               V.norm_path(V.answer(ctx, "doorPath")) == (p.go(h["door"]) or {}).get("path") and
               V.norm_path(V.answer(ctx, "playerPath")) == (p.go(h["player"]) or {}).get("path") and
               isinstance(V.answer(ctx, "declaredComplete"), bool),
               expected={"scriptPath": h["script"], "zonePath": h["zonePath"],
                         "doorPath": h["doorPath"], "playerPath": h["playerPath"]},
               actual=ctx.answer)


def SCENARIO(ctx):
    """Play Mode: at each sample moment, read the door's transform info and the zone component's serialized fields."""
    check = {"kind": "assert", "skill": "editor_get_state", "select": "isPlaying", "expected": True}
    if not compiled(ctx):
        return {"executionMode": "edit", "timeoutSeconds": 30, "steps": [check]}
    cls = class_name(ctx.handles)
    target = holder(ctx) or ctx.handles["zone"]
    schedule = sample_schedule(ctx.seed)
    steps, clock = [], 0.0
    for moment, _open, _en, _ex in schedule:
        steps.append({"kind": "wait_seconds", "seconds": round(moment - clock, 3)})
        steps.append({"kind": "read", "skill": "component_get_serialized_properties",
                      "args": dict({"componentType": "Transform"}, **V.address(ctx.handles["door"]))})
        steps.append({"kind": "read", "skill": "component_get_serialized_properties",
                      "args": dict({"componentType": cls}, **V.address(target))})
        clock = moment
    steps.append(check)
    return {"executionMode": "play", "timeoutSeconds": min(600, int(clock) + 60), "steps": steps}


def _prop_map(step):
    actual = step.get("actual") if isinstance(step, dict) else None
    props = actual.get("properties") if isinstance(actual, dict) else None
    return {row.get("propertyPath"): row.get("value") for row in props or [] if isinstance(row, dict)}


def verify_runtime(ctx, card):
    scenario = ctx.scenario or {}
    passed = scenario.get("passed") is True
    spec = door_spec(ctx.seed)
    schedule = sample_schedule(ctx.seed)
    closed_raw = V.floats(ctx.c0.get("doorLocalPos")) or V.floats((ctx.c0.get("door") or {}).get("localPosition"))
    closed_pos = tuple(closed_raw) if len(closed_raw) == 3 else (0.0, 0.0, 0.0)
    open_pos = (closed_pos[0] + spec["ox"], closed_pos[1] + spec["oy"], closed_pos[2] + spec["oz"])

    reads = [step for step in scenario.get("steps") or [] if isinstance(step, dict) and step.get("kind") == "read"]
    pos_rows, cnt_rows = [], []
    for index, (moment, is_open, want_enter, want_exit) in enumerate(schedule):
        door_step = reads[2 * index] if 2 * index < len(reads) else {}
        comp_step = reads[2 * index + 1] if 2 * index + 1 < len(reads) else {}
        door_props = _prop_map(door_step)
        local_pos = V.floats(door_props.get("m_LocalPosition"))
        want_pos = open_pos if is_open else closed_pos
        pos_ok = len(local_pos) == 3 and _distance(local_pos, want_pos) <= POSITION_TOL
        pos_rows.append({"t": moment, "localPosition": local_pos, "expected": want_pos, "ok": pos_ok})

        props = _prop_map(comp_step)
        got_enter = V.as_int(props.get("enterCount")) if "enterCount" in props else None
        got_exit = V.as_int(props.get("exitCount")) if "exitCount" in props else None
        cnt_ok = got_enter == want_enter and got_exit == want_exit
        cnt_rows.append({"t": moment, "enterCount": got_enter, "exitCount": got_exit,
                         "expected": (want_enter, want_exit), "ok": cnt_ok})

    pos_good = sum(1 for row in pos_rows if row["ok"])
    cnt_good = sum(1 for row in cnt_rows if row["ok"])
    total = float(len(schedule))
    card.fraction("D9", pos_good / total if passed and total else 0.0,
                  expected={"closedPos": closed_pos, "openPos": open_pos, "samples": len(schedule)},
                  actual={"passed": passed, "error": scenario.get("error"), "status": scenario.get("status"),
                          "samples": pos_rows})
    card.fraction("D10", cnt_good / total if passed and total else 0.0,
                  expected=[(row[0], row[2], row[3]) for row in schedule],
                  actual={"passed": passed, "error": scenario.get("error"), "status": scenario.get("status"),
                          "samples": cnt_rows})

# Producer:Betsy
