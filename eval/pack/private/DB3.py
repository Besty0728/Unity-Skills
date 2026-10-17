"""DB3 private verifier: a Blinker script, its scene value and the on/off pattern it produces in Play Mode.

What the task probes: the script must compile with the given class and field names and keep the code default while
the scene stores the seeded interval (a default-only component blinks at the wrong rate); exactly one component on
the given lamp and none on the other; the first toggle comes one interval after Play starts (toggling at Start or
every half interval breaks the sampled pattern); no other code file and no other object change. Play Mode evidence
comes from Light reads sampled midway between the expected toggles.
"""
import os
import random
import re

from usk_eval import verifylib as V

INTERVALS = (0.8, 1.0, 1.2)
DEFAULT_INTERVAL = 0.5
SPEC_SALT = 0x5B03
FIELD = "interval"
PATTERN = (True, True, False, True, False)      # at ~0, 0.5, 1.5, 2.5 and 3.5 intervals
SCRIPT_RE = re.compile(r"\.cs(\.meta)?$")

ASSERTIONS = [
    ("D1", 1.5, False, ["R1"], "the script exists at A/Scripts/<Class>.cs, declares the MonoBehaviour class and a "
                               "serialized float interval with the given code default"),
    ("D2", 1.0, False, ["R1"], "compilation finished without errors for the project and the script"),
    ("D3", 2.0, False, ["R3"], "exactly one component of the class in the scene, on the given lamp, with interval "
                               "stored as the given value"),
    ("D4", 0.75, True, ["R4"], "no code file other than the script was added under Assets"),
    ("D5", 0.75, True, ["R4"], "every other object is unchanged, both lights still enabled, and the lamp kept its "
                               "other components"),
    ("D6", 2.5, False, ["R2", "R5"], "in Play Mode no error is logged and the lamp reads on, on, off, on, off at "
                                     "the sampled times"),
    ("D7", 0.5, False, ["R6", "G8"], "the answer reports the script path, the carrying object and the stored interval"),
    ("D8", 0.5, True, ["G1", "G2"], "no change outside R/A, no other scene saved or opened"),
    ("D9", 0.5, False, ["G4"], "no file other than C# scripts was written under A around the skills"),
]

HINTS = {
    "D1": {"zh-CN": "脚本放在 A/Scripts 下、文件名与类名一致，字段 interval 要序列化（public 或 [SerializeField]），代码默认值按题目给出。",
           "en": "Put the script under A/Scripts with the file named after the class; interval must be serialized "
                 "(public or [SerializeField]) with the given code default."},
    "D2": {"zh-CN": "等编译完成并确认没有错误后再挂组件。",
           "en": "Wait until compilation finishes without errors before attaching the component."},
    "D3": {"zh-CN": "只挂一个组件到题目给出的灯，并用技能把 interval 写进场景（不是只改代码默认值）。",
           "en": "Attach one component to the given lamp only and write interval into the scene with a skill (not "
                 "just the code default)."},
    "D4": {"zh-CN": "除这个脚本外不要新增任何代码文件。", "en": "Add no code file other than this script."},
    "D5": {"zh-CN": "不要改动其他对象，另一盏灯要保持亮着。",
           "en": "Leave the other objects alone; the other lamp stays on."},
    "D6": {"zh-CN": "第一次切换在 Play 开始一个 interval 之后，之后每个 interval 切换一次；不要在 Start 里切换，也不要按半个 interval 切换；运行时不得有错误日志。",
           "en": "The first toggle comes one interval after Play starts and then every interval; do not toggle at "
                 "Start or every half interval; no error may be logged at runtime."},
    "D7": {"zh-CN": "答案中的路径、id 和数值要与实际状态一致，并写明 declaredComplete。",
           "en": "Report the actual path, id and value, and declaredComplete."},
    "D8": {"zh-CN": "所有修改都必须在 R 与 A 之内，且只能保存本轮场景。",
           "en": "Keep every change inside R and A, and save no scene but this run's."},
    "D9": {"zh-CN": "除 C# 脚本外，不要绕过技能直接在 A 下写文件。",
           "en": "Apart from C# scripts, do not write files under A directly; use the skills."},
}


def spec(seed):
    rng = random.Random(seed ^ SPEC_SALT)
    return {"interval": rng.choice(INTERVALS)}


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
    comp = _read(ctx, "debug_check_compilation") or {}
    last = comp.get("lastCompilation") or {}
    feedback = _read(ctx, "script_get_compile_feedback", scriptPath=ctx.handles["script"]) or {}
    ok = not comp.get("isCompiling") and last.get("succeeded") is not False and not last.get("errorCount") and \
        feedback.get("hasErrors") is False
    return ok, {"isCompiling": comp.get("isCompiling"), "succeeded": last.get("succeeded"),
                "errorCount": last.get("errorCount"), "scriptHasErrors": feedback.get("hasErrors")}


def holders(ctx, cls):
    found = _read(ctx, "gameobject_find", component=cls, limit=500) or {}
    return [V.object_id(obj) for obj in found.get("objects") or []]


def as_bool(value):
    """Light.m_Enabled as the serialized reader renders it ("True"/"False", 1/0, true/false); None when unreadable."""
    if isinstance(value, bool):
        return value
    text = str(value).strip().lower() if value is not None else ""
    if text in ("true", "1"):
        return True
    if text in ("false", "0"):
        return False
    return None


def light_enabled(probe, ref):
    """enabled flag of the Light component as component_list reports it (None when there is no Light)."""
    row = next((row for row in probe.components(ref) if row.get("type") == "Light"), None)
    return as_bool(row.get("enabled")) if row else None


def other_states(ctx, holder):
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
    card.check("D1", declares_class(text, cls) and default is not None and abs(default - DEFAULT_INTERVAL) < 1e-6,
               expected={"class": cls, "default": DEFAULT_INTERVAL},
               actual={"present": text is not None, "declaresClass": declares_class(text, cls), "default": default})
    ok, evidence = compiled_ok(ctx)
    card.check("D2", ok, actual=evidence)

    found = holders(ctx, cls)
    count = p.component_count(h["lamp"], cls)
    ser = p.ser(h["lamp"], cls) or {}
    stored = V.floats(ser.get(FIELD))
    card.check("D3", len(found) == 1 and V.same_id(found[0], h["lamp"]) and count == 1 and
               V.close_tuple(stored, (values["interval"],), 1e-3),
               expected={"holder": h["lamp"], "count": 1, FIELD: values["interval"]},
               actual={"holders": found, "count": count, FIELD: ser.get(FIELD)})

    added = sorted(set(cs_files(ctx.project_path)) - set(ctx.c0.get("scripts") or []))
    card.check("D4", all(path == h["script"] for path in added), expected=[h["script"]], actual=added)
    diffs = other_states(ctx, h["lamp"])
    lights = {str(V.object_id(ref)): light_enabled(p, ref) for ref, _state in ctx.c0.get("lights") or []}
    lit = all(lights.get(str(V.object_id(ref))) is as_bool(state) for ref, state in ctx.c0.get("lights") or [])
    card.check("D5", not diffs and lit, actual={"diffs": diffs[:5], "lights": lights})

    on_holder = V.floats((p.ser(found[0], cls) or {}).get(FIELD)) if len(found) == 1 else []
    answer_ok = (V.norm_path(V.answer(ctx, "scriptPath")) == h["script"] and len(found) == 1 and
                 V.same_id(V.answer(ctx, "lampInstanceId"), found[0]) and len(on_holder) == 1 and
                 V.close_tuple(V.answer(ctx, "interval"), (on_holder[0],), 1e-3) and
                 isinstance(V.answer(ctx, "declaredComplete"), bool))
    card.check("D7", answer_ok, actual=ctx.answer)
    bad = ctx.violations()
    card.check("D8", not bad, actual={"violations": bad[:10]})
    direct = [rec for rec in ctx.direct_writes() if not SCRIPT_RE.search(str(rec.get("assetPath") or ""))]
    card.check("D9", not direct, actual={"directWrites": direct[:10]})


def SCENARIO(ctx):
    """Play Mode: the lamp's Light sampled right after start and midway through the next four intervals."""
    interval = spec(ctx.seed)["interval"]
    read = {"kind": "read", "skill": "component_list", "args": V.address(ctx.handles["lamp"])}
    steps = [{"kind": "wait_frames", "frames": 2}, dict(read), {"kind": "wait_seconds", "seconds": 0.5 * interval}]
    for _ in range(3):
        steps += [dict(read), {"kind": "wait_seconds", "seconds": interval}]
    steps += [dict(read), {"kind": "assert", "skill": "editor_get_state", "select": "isPlaying", "expected": True}]
    return {"executionMode": "play", "timeoutSeconds": 60, "steps": steps}


def _enabled(step):
    rows = ((step or {}).get("actual") or {}).get("components") or []
    row = next((row for row in rows if isinstance(row, dict) and row.get("type") == "Light"), None)
    return as_bool(row.get("enabled")) if row else None


def verify_runtime(ctx, card):
    scenario = ctx.scenario or {}
    samples = [_enabled(step) for step in scenario.get("steps") or [] if step.get("kind") == "read"]
    card.check("D6", scenario.get("passed") is True and tuple(samples) == PATTERN,
               expected={"pattern": list(PATTERN)},
               actual={"passed": scenario.get("passed"), "error": scenario.get("error"),
                       "status": scenario.get("status"), "samples": samples})

# Producer:Betsy
