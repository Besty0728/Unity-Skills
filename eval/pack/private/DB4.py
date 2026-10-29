"""DB4 private verifier: a follow-camera script, its scene wiring and values, and the camera position in Play Mode.

What the task probes: the script must compile with the given class, the four serialized fields and their code
defaults (offset (0, 3, -6), smoothing true, smoothSpeed 2) while the scene stores the seeded world-space offset and
smoothing false (a camera that keeps the code default smooths and lags behind); the follow runs in LateUpdate (a
source check: a target teleported between frames cannot tell Update from LateUpdate at runtime); exactly one
component, on the camera, referencing the named vehicle's Transform and not the other vehicle; the offset is added in
world space (the vehicles are yawed, so a rotated offset lands elsewhere) to the world position (the camera's parent
sits away from the origin, so writing localPosition lands elsewhere); no other code file and no change to any object
but the camera.

Play Mode evidence: the scenario reads the vehicle and the camera a few frames after Play starts, then three times
teleports the vehicle with Transform.set_position through component_invoke_method (between frames), waits a few
frames and reads both again. Each camera reading must equal the vehicle reading plus the seeded offset within 0.001,
and each vehicle reading must sit where it was teleported. A script with compile errors cannot enter Play Mode, so
the scenario is then a single edit-mode check that fails at once instead of waiting for a timeout.
"""
import json
import os
import random
import re

from usk_eval import verifylib as V

OFFSET_X = (-2.5, -1.5, 1.5, 2.5)
OFFSET_Y = (4, 4.5, 5.5)
OFFSET_Z = (-7, -8, -9)
DEFAULT_OFFSET = (0.0, 3.0, -6.0)
DEFAULT_SMOOTH_SPEED = 2.0
SPEC_SALT = 0x5B04
MOVE_SALT = 0x5B44
MOVES = ((5, 0, 3), (-4, 0, 6), (3, 1.5, -5), (-6, 0, -2), (2, 0, 7))
MOVE_COUNT = 3
SETTLE_FRAMES = 3
POS_TOL = 0.001           # R5: per axis
VALUE_TOL = 1e-3
TRANSFORM_TYPE = "UnityEngine.Transform"
SCRIPT_RE = re.compile(r"\.cs(\.meta)?$")
COMMENT_RE = re.compile(r"//[^\n]*|/\*.*?\*/|\"(?:\\.|[^\"\\])*\"", re.S)
NUMBER = r"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?[fFdDmM]?"
SIGNED = r"[-+]?\s*" + NUMBER.replace("[-+]?", "", 1)
VECTOR_INIT_RE = re.compile(r"^new\s*(?:(?:UnityEngine\.)?Vector3)?\s*\(\s*(?:x\s*:\s*)?(%s)\s*,\s*(?:y\s*:\s*)?(%s)"
                            r"\s*,\s*(?:z\s*:\s*)?(%s)\s*\)$" % (SIGNED, SIGNED, SIGNED))
NUMBER_INIT_RE = re.compile(r"^(%s)$" % NUMBER)
LATE_UPDATE_RE = re.compile(r"\bvoid\s+LateUpdate\s*\(\s*\)")
POSITION_WRITE_RE = re.compile(r"\bposition\s*=(?!=)|SetPositionAndRotation")
LERP_RE = re.compile(r"\bVector3\.Lerp\s*\(")
LERP_FACTOR_RE = re.compile(r"\bsmoothSpeed\s*\*\s*Time\.deltaTime\b|\bTime\.deltaTime\s*\*\s*smoothSpeed\b")

ASSERTIONS = [
    ("J1", 1.25, False, ["R1"], "the script exists at A/Scripts/<Class>.cs, declares the MonoBehaviour class and the "
                                "serialized fields target (Transform), offset (Vector3, code default (0, 3, -6)), "
                                "smoothing (bool, code default true) and smoothSpeed (float, code default 2)"),
    ("J2", 0.5, False, ["R2"], "the class declares a LateUpdate() method and no Update() or FixedUpdate() method "
                              "writes the position"),
    ("J3", 0.75, False, ["R1"], "compilation finished without errors for the project and the script"),
    ("J4", 1.5, False, ["R3", "R4"], "exactly one component of the class in the scene, on the given camera, with "
                                     "target referencing the Transform of the given vehicle"),
    ("J5", 1.0, False, ["R3"], "the camera's component stores the seeded offset and smoothing false"),
    ("J6", 0.5, True, ["R4"], "no code file other than the script was added under Assets"),
    ("J7", 0.5, True, ["R4"], "every object of the fixture other than the camera is unchanged"),
    ("J8", 2.25, False, ["R2", "R5"], "in Play Mode no error is logged and, after Play starts and after each teleport "
                                     "of the vehicle, the camera sits at the vehicle's position plus the offset "
                                     "within 0.001"),
    ("J9", 0.5, False, ["R6", "G8"], "the answer reports the script path, the carrying object and the referenced "
                                     "object"),
    ("J10", 0.5, True, ["G1", "G2"], "no change outside R/A, no other scene saved or opened"),
    ("J11", 0.25, False, ["G4"], "no file other than C# scripts was written under A around the skills"),
    ("J12", 0.5, False, ["R2"], "the source moves toward the point with Vector3.Lerp and a smoothSpeed * "
                                "Time.deltaTime factor"),
]

HINTS = {
    "J1": {"zh-CN": "脚本放在 A/Scripts 下、文件名与类名一致；四个字段 target（Transform）、offset（Vector3，代码默认 (0, 3, -6)）、"
                    "smoothing（bool，代码默认 true）和 smoothSpeed（float，代码默认 2）都要序列化（public 或 [SerializeField]）。",
           "en": "Put the script under A/Scripts with the file named after the class; the four fields target "
                 "(Transform), offset (Vector3, code default (0, 3, -6)), smoothing (bool, code default true) and "
                 "smoothSpeed (float, code default 2) must be serialized (public or [SerializeField])."},
    "J2": {"zh-CN": "跟随逻辑写在 LateUpdate() 中，而不是 Update 或 FixedUpdate；后两者里不要写位置。",
           "en": "Do the follow in LateUpdate(), not in Update or FixedUpdate; write no position in those two."},
    "J3": {"zh-CN": "等编译完成并确认没有错误后再挂组件和接线。",
           "en": "Wait until compilation finishes without errors before attaching and wiring the component."},
    "J4": {"zh-CN": "只挂一个组件到题目给出的相机对象，并把 target 指向题目给出的那辆车的 Transform（不是另一辆）。",
           "en": "Attach one component to the given camera only and point target at the Transform of the given "
                 "vehicle (not the other one)."},
    "J5": {"zh-CN": "在场景中把 offset 设为题目给出的值、smoothing 设为 false（不能只靠代码默认值）。",
           "en": "Store the given offset and smoothing false in the scene (not just as code defaults)."},
    "J6": {"zh-CN": "除这个脚本外不要新增任何代码文件（包括编辑器脚本）。",
           "en": "Add no code file other than this script (editor scripts included)."},
    "J7": {"zh-CN": "除相机外不要改动任何对象，包括两辆车。", "en": "Change no object but the camera, including both vehicles."},
    "J8": {"zh-CN": "smoothing 为 false 时每帧在 LateUpdate 中直接设置 transform.position = target.position + offset"
                    "（offset 不随 target 旋转，写世界坐标而不是 localPosition）；运行时不得有错误日志。",
           "en": "With smoothing false, set transform.position = target.position + offset in LateUpdate every frame "
                 "(the offset is not rotated with the target; write the world position, not localPosition); no "
                 "error may be logged."},
    "J9": {"zh-CN": "答案中的路径和 id 要与实际状态一致，并写明 declaredComplete。",
           "en": "Report the actual path and ids, and declaredComplete."},
    "J10": {"zh-CN": "所有修改都必须在 R 与 A 之内，且只能保存本轮场景。",
            "en": "Keep every change inside R and A, and save no scene but this run's."},
    "J11": {"zh-CN": "除 C# 脚本外，不要绕过技能直接在 A 下写文件。",
            "en": "Apart from C# scripts, do not write files under A directly; use the skills."},
    "J12": {"zh-CN": "smoothing 为 true 时用 Vector3.Lerp(当前位置, 该点, smoothSpeed * Time.deltaTime) 向该点移动。",
            "en": "With smoothing true, move toward the point with Vector3.Lerp(current position, point, "
                  "smoothSpeed * Time.deltaTime)."},
}


def spec(seed):
    """The seeded offset (same draw as the public task module) and the vehicle's teleport steps."""
    rng = random.Random(seed ^ SPEC_SALT)
    offset = (rng.choice(OFFSET_X), rng.choice(OFFSET_Y), rng.choice(OFFSET_Z))
    return {"offset": offset, "moves": random.Random(seed ^ MOVE_SALT).sample(MOVES, MOVE_COUNT)}


def class_name(script_path):
    return os.path.basename(script_path)[:-len(".cs")]


def cs_files(project_path):
    out = []
    for base, dirs, files in os.walk(os.path.join(project_path, "Assets")):
        dirs.sort()
        out += [os.path.relpath(os.path.join(base, name), project_path).replace(os.sep, "/")
                for name in sorted(files) if name.endswith(".cs")]
    return sorted(out)


def _read(ctx, skill, **args):
    """Result of a read-only skill, or None when it answers with a business error."""
    envelope = ctx.probe.c.read_raw(skill, **args)
    if not isinstance(envelope, dict) or envelope.get("status") in ("error", "partial") or envelope.get("errorCode"):
        return None
    result = envelope.get("result", envelope)
    return result if isinstance(result, dict) and result.get("success") is not False else None


def strip_code(text):
    """Source without comments and string literals, or None."""
    return COMMENT_RE.sub("", text) if isinstance(text, str) else None


def script_text(ctx):
    return strip_code((_read(ctx, "script_read", scriptPath=ctx.handles["script"]) or {}).get("content"))


def declares_class(text, cls):
    return bool(text) and re.search(r"\bpublic\s+(?:sealed\s+|partial\s+)*class\s+%s\s*:\s*(?:UnityEngine\.)?MonoBehaviour\b" % re.escape(cls),
                                    text) is not None


def serialized_field(text, kind, name):
    """(declared, initializer): whether text declares a serialized field (public or [SerializeField]) of type `kind`
    named `name`, and its initializer text ("" when it has none)."""
    if not text:
        return False, None
    pattern = r"(?P<attrs>(?:\[[^\]]*\]\s*)*)(?P<mods>(?:(?:public|private|protected|internal)\s+)*)" \
              r"(?:UnityEngine\.)?%s\s+%s\s*(?:=\s*(?P<init>[^;]*))?;" % (re.escape(kind), re.escape(name))
    for match in re.finditer(pattern, text):
        if "public" in match.group("mods") or "SerializeField" in match.group("attrs"):
            return True, (match.group("init") or "").strip()
    return False, None


def _number(text):
    return float(re.sub(r"\s+", "", text).rstrip("fFdDmM"))


def vector_default(init):
    """(x, y, z) of a `new Vector3(x, y, z)` or `new(x, y, z)` initializer, or None."""
    match = VECTOR_INIT_RE.match(init or "")
    return tuple(_number(match.group(index)) for index in (1, 2, 3)) if match else None


def number_default(init):
    match = NUMBER_INIT_RE.match(init or "")
    return _number(match.group(1)) if match else None


def fields_report(text, cls):
    """The R1 facts of a stripped source: class, field types and code defaults."""
    has_target, _init = serialized_field(text, "Transform", "target")
    has_offset, offset_init = serialized_field(text, "Vector3", "offset")
    has_smoothing, smoothing_init = serialized_field(text, "bool", "smoothing")
    has_speed, speed_init = serialized_field(text, "float", "smoothSpeed")
    return {"declaresClass": declares_class(text, cls), "target": has_target,
            "offset": vector_default(offset_init) if has_offset else None,
            "smoothing": smoothing_init if has_smoothing else None,
            "smoothSpeed": number_default(speed_init) if has_speed else None}


def fields_ok(report):
    return (report["declaresClass"] and report["target"] and report["offset"] is not None and
            V.close_tuple(report["offset"], DEFAULT_OFFSET, 1e-6) and report["smoothing"] == "true" and
            report["smoothSpeed"] is not None and abs(report["smoothSpeed"] - DEFAULT_SMOOTH_SPEED) < 1e-6)


def method_bodies(text, name):
    """Bodies (brace-matched, on the stripped source) of the parameterless `void name()` methods."""
    bodies = []
    for match in re.finditer(r"\bvoid\s+%s\s*\(\s*\)\s*\{" % re.escape(name), text or ""):
        depth, index = 1, match.end()
        while index < len(text) and depth:
            depth += {"{": 1, "}": -1}.get(text[index], 0)
            index += 1
        bodies.append(text[match.end():index])
    return bodies


def declares_late_update(text):
    """LateUpdate() is declared and neither Update() nor FixedUpdate() writes the position."""
    return (bool(text) and LATE_UPDATE_RE.search(text) is not None and
            not any(POSITION_WRITE_RE.search(body) for name in ("Update", "FixedUpdate")
                    for body in method_bodies(text, name)))


def lerps_toward_point(text):
    return bool(text) and LERP_RE.search(text) is not None and LERP_FACTOR_RE.search(text) is not None


def compiled(ctx):
    """True when no compilation is running and the last one ended without errors."""
    comp = _read(ctx, "debug_check_compilation") or {}
    last = comp.get("lastCompilation") or {}
    return not comp.get("isCompiling") and last.get("succeeded") is not False and not last.get("errorCount")


def compiled_ok(ctx):
    comp = _read(ctx, "debug_check_compilation") or {}
    last = comp.get("lastCompilation") or {}
    feedback = _read(ctx, "script_get_compile_feedback", scriptPath=ctx.handles["script"]) or {}
    ok = compiled(ctx) and feedback.get("hasErrors") is False
    return ok, {"isCompiling": comp.get("isCompiling"), "succeeded": last.get("succeeded"),
                "errorCount": last.get("errorCount"), "scriptHasErrors": feedback.get("hasErrors")}


def holders(ctx, cls):
    """Ids of the GameObjects carrying the class (empty when the class is not loaded)."""
    found = _read(ctx, "gameobject_find", component=cls, limit=500) or {}
    return [V.object_id(obj) for obj in found.get("objects") or []]


def other_states(ctx):
    """Differences from C0 for every fixture object but the camera (the camera may move and gains the component)."""
    diffs = []
    for ref, state in ctx.c0.get("states") or []:
        if V.same_id(ref, ctx.handles["cam"]):
            continue
        ok, diff = V.same(state, V.go_state(ctx.probe, ref))
        if not ok:
            diffs.append({"ref": ref, "diff": diff})
    return diffs


def destinations(ctx):
    """World positions the scenario teleports the vehicle to: its build position plus each seeded step."""
    origin = V.floats((ctx.c0 or {}).get("origin"))
    if len(origin) != 3:
        origin = V.floats((ctx.probe.go(ctx.handles["rover"]) or {}).get("position"))
    if len(origin) != 3:
        origin = [0.0, 0.0, 0.0]
    return [tuple(round(base + step, 4) for base, step in zip(origin, move)) for move in spec(ctx.seed)["moves"]]


def verify(ctx, card):
    p, h = ctx.probe, ctx.handles
    cls, values = class_name(h["script"]), spec(ctx.seed)
    text = script_text(ctx)
    report = fields_report(text, cls)
    card.check("J1", text is not None and fields_ok(report),
               expected={"class": cls, "offset": DEFAULT_OFFSET, "smoothing": "true",
                         "smoothSpeed": DEFAULT_SMOOTH_SPEED},
               actual=dict(report, present=text is not None))
    card.check("J2", declares_late_update(text), actual={"lateUpdate": declares_late_update(text)})
    card.check("J12", lerps_toward_point(text), actual={"lerp": lerps_toward_point(text)})
    ok, evidence = compiled_ok(ctx)
    card.check("J3", ok, actual=evidence)

    found = holders(ctx, cls)
    count = p.component_count(h["cam"], cls)
    ser = p.ser(h["cam"], cls) or {}
    ref = V.parse_ref(ser.get("target"))
    followed = p.owner(ser.get("target"))
    card.check("J4", len(found) == 1 and V.same_id(found[0], h["cam"]) and count == 1 and bool(ref) and
               ref[1] == TRANSFORM_TYPE and V.same_id(followed, h["rover"]),
               expected={"holder": h["cam"], "count": 1, "target": h["rover"]},
               actual={"holders": found, "count": count, "target": ser.get("target"), "followed": followed})
    stored = V.floats(ser.get("offset"))
    smoothing = ser.get("smoothing")
    card.check("J5", V.close_tuple(stored, values["offset"], VALUE_TOL) and str(smoothing).lower() == "false",
               expected={"offset": values["offset"], "smoothing": False},
               actual={"offset": ser.get("offset"), "smoothing": smoothing})

    added = sorted(set(cs_files(ctx.project_path)) - set(ctx.c0.get("scripts") or []))
    card.check("J6", set(added) <= {h["script"]}, expected=[h["script"]], actual=added)
    diffs = other_states(ctx)
    card.check("J7", not diffs, actual={"diffs": diffs[:5]})

    holder_ser = (p.ser(found[0], cls) or {}) if len(found) == 1 else {}
    holder_followed = p.owner(holder_ser.get("target")) if holder_ser else None
    answer_ok = (V.norm_path(V.answer(ctx, "scriptPath")) == h["script"] and len(found) == 1 and
                 V.same_id(V.answer(ctx, "cameraInstanceId"), found[0]) and holder_followed is not None and
                 V.same_id(V.answer(ctx, "followedInstanceId"), holder_followed) and
                 isinstance(V.answer(ctx, "declaredComplete"), bool))
    card.check("J9", answer_ok, actual=ctx.answer)
    bad = ctx.violations()
    card.check("J10", not bad, actual={"violations": bad[:10]})
    direct = [rec for rec in ctx.direct_writes() if not SCRIPT_RE.search(str(rec.get("assetPath") or ""))]
    card.check("J11", not direct, actual={"directWrites": direct[:10]})


def _position_text(point):
    return ",".join("%.4f" % value for value in point)


def SCENARIO(ctx):
    """Play Mode: the vehicle and the camera after Play starts, then after each of three teleports of the vehicle."""
    h = ctx.handles
    check = {"kind": "assert", "skill": "editor_get_state", "select": "isPlaying", "expected": True}
    if not compiled(ctx):
        return {"executionMode": "edit", "timeoutSeconds": 30, "steps": [check]}
    rover, cam = V.address(h["rover"]), V.address(h["cam"])
    sample = [{"kind": "read", "skill": "gameobject_get_info", "args": rover},
              {"kind": "read", "skill": "gameobject_get_info", "args": cam}]
    steps = [{"kind": "wait_frames", "frames": SETTLE_FRAMES}] + [dict(step) for step in sample]
    for point in destinations(ctx):
        steps.append({"kind": "invoke", "skill": "component_invoke_method",
                      "args": dict({"componentType": TRANSFORM_TYPE, "methodName": "set_position",
                                    "arguments": json.dumps([_position_text(point)])}, **rover)})
        steps.append({"kind": "wait_frames", "frames": SETTLE_FRAMES})
        steps += [dict(step) for step in sample]
    steps.append(check)
    return {"executionMode": "play", "timeoutSeconds": 90, "steps": steps}


def verify_runtime(ctx, card):
    scenario, values = ctx.scenario or {}, spec(ctx.seed)
    points = [None] + destinations(ctx)
    reads = [step.get("actual") if isinstance(step.get("actual"), dict) else {}
             for step in scenario.get("steps") or [] if isinstance(step, dict) and step.get("kind") == "read"]
    complete = len(reads) == 2 * len(points)
    reads = reads + [{}] * max(0, 2 * len(points) - len(reads))
    rows = []
    for index, point in enumerate(points):
        rover = V.floats(reads[2 * index].get("position"))
        cam = V.floats(reads[2 * index + 1].get("position"))
        moved = point is None or V.vec_close(rover, point, POS_TOL)
        wanted = [a + b for a, b in zip(rover, values["offset"])] if len(rover) == 3 else None
        followed = wanted is not None and V.vec_close(cam, wanted, POS_TOL)
        rows.append({"sample": index, "vehicle": rover, "teleportedTo": point, "camera": cam, "wanted": wanted,
                     "ok": bool(moved and followed)})
    passed = scenario.get("passed") is True
    card.check("J8", passed and complete and all(row["ok"] for row in rows),
               expected={"offset": values["offset"], "tolerance": POS_TOL, "samples": len(points)},
               actual={"passed": passed, "error": scenario.get("error"), "status": scenario.get("status"),
                       "samples": rows})

# Producer:Betsy
