"""DB1 private verifier: a Spinner script, its scene value and the turn it produces in Play Mode.

What the task probes: the script must compile with the given class and field names and keep the code default while
the scene stores the seeded speed (a default-only component turns at the wrong rate); exactly one component on the
given Blades and none on the other mill's Blades; the turn is about the object's own local Y axis (the Blades are
tilted, so a world-axis rotation shows another axis) and scaled by Time.deltaTime (a fixed step per frame turns far
too much); no other code file and no other object change. Play Mode evidence comes from two Transform reads around a
timed wait.
"""
import math
import os
import random
import re

from usk_eval import verifylib as V

SPEEDS = (30, 45, 60)
WAITS = (1.5, 2.0, 2.5)
DEFAULT_SPEED = 15
SPEC_SALT = 0x5B01
FIELD = "degreesPerSecond"
MIN_AXIS_DOT = 0.98
SCRIPT_RE = re.compile(r"\.cs(\.meta)?$")

ASSERTIONS = [
    ("C1", 1.5, False, ["R1"], "the script exists at A/Scripts/<Class>.cs, declares the MonoBehaviour class and a "
                               "serialized float degreesPerSecond with the given code default"),
    ("C2", 1.0, False, ["R1"], "compilation finished without errors for the project and the script"),
    ("C3", 2.0, False, ["R3"], "exactly one component of the class in the scene, on the given Blades, with "
                               "degreesPerSecond stored as the given speed"),
    ("C4", 0.75, True, ["R4"], "no code file other than the script was added under Assets"),
    ("C5", 0.75, True, ["R4"], "every other object is unchanged and the Blades kept its pose and other components"),
    ("C6", 2.5, False, ["R2", "R5"], "in Play Mode no error is logged and the Blades turns about its local Y axis "
                                     "by about speed x wait degrees"),
    ("C7", 0.5, False, ["R6", "G8"], "the answer reports the script path, the carrying object and the stored speed"),
    ("C8", 0.5, True, ["G1", "G2"], "no change outside R/A, no other scene saved or opened"),
    ("C9", 0.5, False, ["G4"], "no file other than C# scripts was written under A around the skills"),
]

HINTS = {
    "C1": {"zh-CN": "脚本放在 A/Scripts 下、文件名与类名一致，字段 degreesPerSecond 要序列化（public 或 [SerializeField]），代码默认值按题目给出。",
           "en": "Put the script under A/Scripts with the file named after the class; degreesPerSecond must be "
                 "serialized (public or [SerializeField]) with the given code default."},
    "C2": {"zh-CN": "等编译完成并确认没有错误后再挂组件。",
           "en": "Wait until compilation finishes without errors before attaching the component."},
    "C3": {"zh-CN": "只挂一个组件到题目给出的 Blades，并用技能把 degreesPerSecond 写进场景（不是只改代码默认值）。",
           "en": "Attach one component to the given Blades only and write degreesPerSecond into the scene with a skill "
                 "(not just the code default)."},
    "C4": {"zh-CN": "除这个脚本外不要新增任何代码文件。", "en": "Add no code file other than this script."},
    "C5": {"zh-CN": "不要改动其他对象，也不要动另一座风车的 Blades。",
           "en": "Leave the other objects, including the other mill's Blades, alone."},
    "C6": {"zh-CN": "绕自身本地 Y 轴旋转（transform.Rotate 默认 Space.Self），角度乘以 Time.deltaTime；运行时不得有错误日志。",
           "en": "Rotate about the object's own local Y axis (transform.Rotate defaults to Space.Self), scaled by "
                 "Time.deltaTime; no error may be logged at runtime."},
    "C7": {"zh-CN": "答案中的路径、id 和数值要与实际状态一致，并写明 declaredComplete。",
           "en": "Report the actual path, id and value, and declaredComplete."},
    "C8": {"zh-CN": "所有修改都必须在 R 与 A 之内，且只能保存本轮场景。",
           "en": "Keep every change inside R and A, and save no scene but this run's."},
    "C9": {"zh-CN": "除 C# 脚本外，不要绕过技能直接在 A 下写文件。",
           "en": "Apart from C# scripts, do not write files under A directly; use the skills."},
}


def spec(seed):
    rng = random.Random(seed ^ SPEC_SALT)
    return {"speed": rng.choice(SPEEDS), "wait": rng.choice(WAITS)}


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


def script_text(ctx):
    content = (_read(ctx, "script_read", scriptPath=ctx.handles["script"]) or {}).get("content")
    return content if isinstance(content, str) else None


def declares_class(text, cls):
    return bool(text) and re.search(r"\bpublic\s+class\s+%s\s*:\s*(?:UnityEngine\.)?MonoBehaviour\b" % re.escape(cls),
                                    text) is not None


def field_default(text, name, kind="float"):
    """Code default of a serialized field (public or [SerializeField]), or None when the field is not declared so."""
    if not text:
        return None
    pattern = r"(?P<attrs>(?:\[[^\]]*\]\s*)*)(?P<mods>(?:(?:public|private|protected|internal|static)\s+)*)" \
              r"%s\s+%s\s*=\s*(?P<value>-?[0-9]*\.?[0-9]+)\s*[fFdD]?\s*;" % (re.escape(kind), re.escape(name))
    for match in re.finditer(pattern, text):
        if "public" in match.group("mods") or "SerializeField" in match.group("attrs"):
            return float(match.group("value"))
    return None


def compiled_ok(ctx):
    """(ok, evidence): the project compiled without errors and the task's script has no diagnostics."""
    comp = _read(ctx, "debug_check_compilation") or {}
    last = comp.get("lastCompilation") or {}
    feedback = _read(ctx, "script_get_compile_feedback", scriptPath=ctx.handles["script"]) or {}
    ok = not comp.get("isCompiling") and last.get("succeeded") is not False and not last.get("errorCount") and \
        feedback.get("hasErrors") is False
    return ok, {"isCompiling": comp.get("isCompiling"), "succeeded": last.get("succeeded"),
                "errorCount": last.get("errorCount"), "scriptHasErrors": feedback.get("hasErrors")}


def holders(ctx, cls):
    """Ids of the GameObjects carrying the class (empty when the class is not loaded)."""
    found = _read(ctx, "gameobject_find", component=cls, limit=500) or {}
    return [V.object_id(obj) for obj in found.get("objects") or []]


def q_inv(q):
    x, y, z, w = q
    return (-x, -y, -z, w)


def q_mul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by, aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw, aw * bw - ax * bx - ay * by - az * bz)


def local_turn(q1, q2):
    """(angle in degrees, unit axis in the object's own frame) of the rotation taking local rotation q1 to q2."""
    a, b = V.floats(q1), V.floats(q2)
    if len(a) != 4 or len(b) != 4:
        return None, None
    delta = q_mul(q_inv(a), b)
    length = math.sqrt(sum(v * v for v in delta[:3]))
    angle = V.quat_angle_deg(a, b)
    if angle is None or length < 1e-9:
        return angle, None
    return angle, tuple(v / length for v in delta[:3])


def other_states(ctx, holder):
    """[(ref, same?)] for every object of C0 but the holder; the holder's pose and other components are compared
    apart from the added class component."""
    p, cls = ctx.probe, class_name(ctx.handles["script"])
    diffs = []
    for ref, state in ctx.c0.get("states") or []:
        now = V.go_state(p, ref)
        if V.same_id(ref, holder) and isinstance(now, dict) and isinstance(state, dict):
            now = dict(now, components=[c for c in now.get("components") or [] if c != cls])
        ok, diff = V.same(state, now)
        if not ok:
            diffs.append({"ref": ref, "diff": diff})
    return diffs


def verify(ctx, card):
    p, h = ctx.probe, ctx.handles
    cls, values = class_name(h["script"]), spec(ctx.seed)
    text = script_text(ctx)
    default = field_default(text, FIELD)
    card.check("C1", declares_class(text, cls) and default is not None and abs(default - DEFAULT_SPEED) < 1e-6,
               expected={"class": cls, "default": DEFAULT_SPEED},
               actual={"present": text is not None, "declaresClass": declares_class(text, cls), "default": default})
    ok, evidence = compiled_ok(ctx)
    card.check("C2", ok, actual=evidence)

    found = holders(ctx, cls)
    count = p.component_count(h["spinner"], cls)
    ser = p.ser(h["spinner"], cls) or {}
    stored = V.floats(ser.get(FIELD))
    card.check("C3", len(found) == 1 and V.same_id(found[0], h["spinner"]) and count == 1 and
               V.close_tuple(stored, (values["speed"],), 1e-3),
               expected={"holder": h["spinner"], "count": 1, FIELD: values["speed"]},
               actual={"holders": found, "count": count, FIELD: ser.get(FIELD)})

    added = sorted(set(cs_files(ctx.project_path)) - set(ctx.c0.get("scripts") or []))
    card.check("C4", all(path == h["script"] for path in added), expected=[h["script"]], actual=added)
    diffs = other_states(ctx, h["spinner"])
    card.check("C5", not diffs, actual={"diffs": diffs[:5]})

    on_holder = V.floats((p.ser(found[0], cls) or {}).get(FIELD)) if len(found) == 1 else []
    answer_ok = (V.norm_path(V.answer(ctx, "scriptPath")) == h["script"] and len(found) == 1 and
                 V.same_id(V.answer(ctx, "spinnerInstanceId"), found[0]) and len(on_holder) == 1 and
                 V.close_tuple(V.answer(ctx, "degreesPerSecond"), (on_holder[0],), 1e-3) and
                 isinstance(V.answer(ctx, "declaredComplete"), bool))
    card.check("C7", answer_ok, actual=ctx.answer)
    bad = ctx.violations()
    card.check("C8", not bad, actual={"violations": bad[:10]})
    direct = [rec for rec in ctx.direct_writes() if not SCRIPT_RE.search(str(rec.get("assetPath") or ""))]
    card.check("C9", not direct, actual={"directWrites": direct[:10]})


def SCENARIO(ctx):
    """Play Mode: the Blades' local rotation before and after a timed wait."""
    target = V.address(ctx.handles["spinner"])
    read = {"kind": "read", "skill": "component_get_serialized_properties",
            "args": dict({"componentType": "Transform"}, **target)}
    return {"executionMode": "play", "timeoutSeconds": 60, "steps": [
        {"kind": "wait_frames", "frames": 2},
        dict(read),
        {"kind": "wait_seconds", "seconds": spec(ctx.seed)["wait"]},
        dict(read),
        {"kind": "assert", "skill": "editor_get_state", "select": "isPlaying", "expected": True},
    ]}


def _rotation(step):
    rows = ((step or {}).get("actual") or {}).get("properties") or []
    return next((row.get("value") for row in rows if isinstance(row, dict)
                 and row.get("propertyPath") == "m_LocalRotation"), None)


def verify_runtime(ctx, card):
    scenario, values = ctx.scenario or {}, spec(ctx.seed)
    reads = [step for step in scenario.get("steps") or [] if step.get("kind") == "read"]
    angle, axis = local_turn(_rotation(reads[0]) if reads else None, _rotation(reads[1]) if len(reads) > 1 else None)
    wanted = values["speed"] * values["wait"]
    tolerance = max(10.0, 0.12 * wanted)
    turned = angle is not None and abs(angle - wanted) <= tolerance
    about_y = axis is not None and abs(axis[1]) >= MIN_AXIS_DOT
    card.check("C6", scenario.get("passed") is True and turned and about_y,
               expected={"angle": wanted, "tolerance": tolerance, "axis": "local Y"},
               actual={"passed": scenario.get("passed"), "error": scenario.get("error"),
                       "status": scenario.get("status"), "angle": angle, "axis": axis})

# Producer:Betsy
