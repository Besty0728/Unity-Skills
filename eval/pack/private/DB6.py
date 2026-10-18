"""DB6 private verifier: a TimedSpawner script, its serialized references and numbers, and the spawn schedule in Play Mode.

What the task probes: the script must compile with the given class and four serialized fields (prefab, container,
interval with code default 2, maxCount with code default 2) while the scene stores the seeded prefab, container
Transform, interval and maxCount; exactly one component on the designated emitter and none on the other; the first
instance appears one full interval after Play starts, each instance is a direct child of the container at local
position (0, 0, 0) (world position matching the container within 0.01), and spawning stops at maxCount; no other code
file and no collateral change to other objects or prefabs. Play Mode evidence comes from container child reads at the
midpoints 0.5, 1.5, ..., (maxCount + 0.5) intervals plus a settle sample at maxCount * interval + 1 s, where the first
spawned child's world position is also read.
"""
import hashlib
import math
import os
import random
import re

from usk_eval import verifylib as V

INTERVALS = (0.8, 1.0, 1.2)
COUNTS = (3, 4, 5)
DEFAULT_INTERVAL = 2.0
DEFAULT_COUNT = 2
SPEC_SALT = 0x5B06
WINDOW_S = 0.15
SETTLE_S = 1.0
POS_TOL = 0.01
VALUE_TOL = 1e-3
SCRIPT_RE = re.compile(r"\.cs(\.meta)?$")
COMMENT_RE = re.compile(r"//[^\n]*|/\*.*?\*/|\"(?:\\.|[^\"\\])*\"", re.S)

ASSERTIONS = [
    ("F1", 1.5, False, ["R1"], "the script exists at A/Scripts/<Class>.cs, declares the MonoBehaviour class and the "
                               "serialized fields prefab, container, interval and maxCount with the given code defaults"),
    ("F2", 1.0, False, ["R1"], "compilation finished without errors for the project and the script"),
    ("F3", 2.0, False, ["R3"], "exactly one component of the class in the scene, on the given emitter, referencing "
                               "the given prefab and container Transform with the given interval and maxCount stored"),
    ("F4", 0.75, True, ["R4"], "no code file other than the script was added under Assets"),
    ("F5", 0.75, True, ["R4"], "every other object and prefab is unchanged in Edit Mode and the emitter kept its pose "
                               "and other components"),
    ("F6", 2.5, False, ["R2", "R5"], "in Play Mode no error is logged, the container child counts follow the midpoint "
                                     "schedule, and at the final sample the container holds maxCount instances of the "
                                     "prefab at the container's position"),
    ("F7", 0.5, False, ["R6", "G8"], "the answer reports the script path, the emitter, the container and the stored "
                                     "interval and maxCount"),
    ("F8", 0.5, True, ["G1", "G2"], "no change outside R/A, no other scene saved or opened"),
    ("F9", 0.5, False, ["G4"], "no file other than C# scripts was written under A around the skills"),
]

HINTS = {
    "F1": {"zh-CN": "脚本放在 A/Scripts 下、文件名与类名一致；四个字段 prefab（GameObject）、container（Transform）、"
                    "interval（float，代码默认 2）和 maxCount（int，代码默认 2）都要序列化（public 或 [SerializeField]）。",
           "en": "Put the script under A/Scripts with the file named after the class; the four fields prefab "
                 "(GameObject), container (Transform), interval (float, code default 2) and maxCount (int, code "
                 "default 2) must be serialized (public or [SerializeField])."},
    "F2": {"zh-CN": "等编译完成并确认没有错误后再挂组件和接线。",
           "en": "Wait until compilation finishes without errors before attaching and wiring the component."},
    "F3": {"zh-CN": "只挂一个组件到题目给出的发射器对象，并在场景中把 prefab、container（Transform）、interval 和 maxCount "
                    "设为题目给出的值（不能只靠代码默认值）。",
           "en": "Attach one component to the given emitter only, and set prefab, container (Transform), interval and "
                 "maxCount in the scene (not just as code defaults)."},
    "F4": {"zh-CN": "除这个脚本外不要新增任何代码文件（包括编辑器脚本）。",
           "en": "Add no code file other than this script (editor scripts included)."},
    "F5": {"zh-CN": "不要改动其他发射器、其他容器和预制体，编辑模式下容器内保持无子物体。",
           "en": "Leave the other emitter, the other container and the prefabs unchanged, and keep the container "
                 "empty in Edit Mode."},
    "F6": {"zh-CN": "每隔 interval 秒用 Instantiate(prefab, container) 生成一个直接子物体（第一次在 1 个 interval 后，"
                    "本地坐标为 (0, 0, 0)），达到 maxCount 后停止；运行时不得有错误日志。",
           "en": "Spawn one direct child every interval seconds with Instantiate(prefab, container) (the first after "
                 "1 full interval, at local position (0, 0, 0)) and stop at maxCount; no error may be logged."},
    "F7": {"zh-CN": "答案中的路径、id 和数值要与实际状态一致，并写明 declaredComplete。",
           "en": "Report the actual path, ids and values, and declaredComplete."},
    "F8": {"zh-CN": "所有修改都必须在 R 与 A 之内，且只能保存本轮场景。",
           "en": "Keep every change inside R and A, and save no scene but this run's."},
    "F9": {"zh-CN": "除 C# 脚本外，不要绕过技能直接在 A 下写文件。",
           "en": "Apart from C# scripts, do not write files under A directly; use the skills."},
}


def spec(seed):
    rng = random.Random(seed ^ SPEC_SALT)
    return {"interval": rng.choice(INTERVALS), "maxCount": rng.choice(COUNTS)}


def class_name(script_path):
    return os.path.basename(script_path)[:-len(".cs")]


def cs_files(project_path):
    out = []
    for base, dirs, files in os.walk(os.path.join(project_path, "Assets")):
        dirs.sort()
        out += [os.path.relpath(os.path.join(base, name), project_path).replace(os.sep, "/")
                for name in sorted(files) if name.endswith(".cs")]
    return sorted(out)


def _sha(ctx, asset_path):
    try:
        with open(os.path.join(ctx.project_path, *asset_path.split("/")), "rb") as handle:
            return hashlib.sha256(handle.read()).hexdigest()
    except OSError:
        return None


def _asset_name(path):
    return os.path.splitext(os.path.basename(path))[0]


def due(count, interval, t):
    """Instances due t seconds after Play starts: one every interval, the first at interval, at most count."""
    if interval <= 0:
        return count
    return max(0, min(count, int(math.floor(t / float(interval) + 1e-6))))


def sample_times(values):
    """Midpoints 0.5, 1.5, ..., (maxCount + 0.5) * interval, then maxCount * interval + SETTLE_S."""
    interval, count = values["interval"], values["maxCount"]
    times = [round((index + 0.5) * interval, 3) for index in range(count + 1)]
    times.append(round(count * interval + SETTLE_S, 3))
    return times


def window_ok(actual, count, interval, t):
    """Whether a child count lies between the numbers due at t - WINDOW_S and t + WINDOW_S."""
    value = V.as_int(actual)
    return value is not None and due(count, interval, max(0.0, t - WINDOW_S)) <= value <= due(count, interval,
                                                                                              t + WINDOW_S)


def _read(ctx, skill, **args):
    """Result of a read-only skill, or None when it answers with a business error."""
    envelope = ctx.probe.c.read_raw(skill, **args)
    if not isinstance(envelope, dict) or envelope.get("status") in ("error", "partial") or envelope.get("errorCode"):
        return None
    result = envelope.get("result", envelope)
    return result if isinstance(result, dict) and result.get("success") is not False else None


def script_text(ctx):
    content = (_read(ctx, "script_read", scriptPath=ctx.handles["script"]) or {}).get("content")
    return COMMENT_RE.sub("", content) if isinstance(content, str) else None


def declares_class(text, cls):
    return bool(text) and re.search(r"\bpublic\s+class\s+%s\s*:\s*(?:UnityEngine\.)?MonoBehaviour\b" % re.escape(cls),
                                    text) is not None


def declares_ref_field(text, kind, name):
    """True when text declares a serialized field (public or [SerializeField]) of reference type `kind` named `name`."""
    if not text:
        return False
    pattern = r"(?P<attrs>(?:\[[^\]]*\]\s*)*)(?P<mods>(?:(?:public|private|protected|internal)\s+)*)" \
              r"(?:UnityEngine\.)?%s\s+%s\s*(?:=[^;]*)?;" % (re.escape(kind), re.escape(name))
    return any("public" in m.group("mods") or "SerializeField" in m.group("attrs") for m in re.finditer(pattern, text))


def field_default(text, name, kind="float"):
    """Code default of a serialized numeric field (public or [SerializeField]), or None when not declared so."""
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


def subtree_ids(probe, root):
    out, stack = [], [root]
    while stack:
        ref = stack.pop()
        state = V.go_state(probe, ref)
        out.append(V.object_id(ref))
        stack.extend(reversed((state or {}).get("children") or []))
    return out


def other_states(ctx, holder):
    p, h, cls = ctx.probe, ctx.handles, class_name(ctx.handles["script"])
    diffs = []
    before_states = ctx.c0.get("states") or []
    for ref, state in before_states:
        now = V.go_state(p, ref)
        if V.same_id(ref, holder) and isinstance(now, dict) and isinstance(state, dict):
            now = dict(now, components=[c for c in now.get("components") or [] if c != cls])
        ok, diff = V.same(state, now)
        if not ok:
            diffs.append({"ref": ref, "diff": diff})
    before_ids = [ref for ref, _s in before_states]
    now_ids = subtree_ids(p, h["rig"])
    if len(now_ids) != len(before_ids) or not all(V.same_id(a, b) for a, b in zip(before_ids, now_ids)):
        diffs.append({"subtreeIds": [str(x) for x in now_ids]})
    for path, before_meta in (ctx.c0.get("prefabs") or {}).items():
        sha = _sha(ctx, path)
        guid = p.guid(path)
        if sha != (before_meta or {}).get("sha") or guid != (before_meta or {}).get("guid"):
            diffs.append({"prefab": path, "sha": sha, "guid": guid})
    return diffs


def verify(ctx, card):
    p, h = ctx.probe, ctx.handles
    cls, values = class_name(h["script"]), spec(ctx.seed)
    text = script_text(ctx)
    has_prefab = declares_ref_field(text, "GameObject", "prefab")
    has_container = declares_ref_field(text, "Transform", "container")
    def_interval = field_default(text, "interval", "float")
    def_count = field_default(text, "maxCount", "int")
    f1_ok = (declares_class(text, cls) and has_prefab and has_container and
             def_interval is not None and abs(def_interval - DEFAULT_INTERVAL) < 1e-6 and
             def_count is not None and abs(def_count - DEFAULT_COUNT) < 1e-6 and
             def_count == int(def_count))
    card.check("F1", f1_ok,
               expected={"class": cls, "prefab": "GameObject", "container": "Transform",
                         "defaultInterval": DEFAULT_INTERVAL, "defaultCount": DEFAULT_COUNT},
               actual={"present": text is not None, "declaresClass": declares_class(text, cls),
                       "hasPrefab": has_prefab, "hasContainer": has_container,
                       "defaultInterval": def_interval, "defaultCount": def_count})
    ok, evidence = compiled_ok(ctx)
    card.check("F2", ok, actual=evidence)

    found = holders(ctx, cls)
    count = p.component_count(h["emitter"], cls)
    ser = p.ser(h["emitter"], cls) or {}
    prefab_ref = V.parse_ref(ser.get("prefab"))
    container_ref = V.parse_ref(ser.get("container"))
    container_owner = p.owner(ser.get("container"))
    stored_interval = V.floats(ser.get("interval"))
    stored_count = V.as_int(ser.get("maxCount"))
    wiring_ok = (len(found) == 1 and V.same_id(found[0], h["emitter"]) and count == 1 and
                 bool(prefab_ref) and prefab_ref[1] == "UnityEngine.GameObject" and
                 prefab_ref[2] == _asset_name(h["prefab"]) and
                 bool(container_ref) and container_ref[1] == "UnityEngine.Transform" and
                 V.same_id(container_owner, h["container"]) and
                 V.close_tuple(stored_interval, (values["interval"],), VALUE_TOL) and
                 stored_count == values["maxCount"])
    card.check("F3", wiring_ok,
               expected={"holder": h["emitter"], "count": 1, "prefab": _asset_name(h["prefab"]),
                         "container": h["container"], "interval": values["interval"], "maxCount": values["maxCount"]},
               actual={"holders": found, "count": count,
                       "ser": {k: ser.get(k) for k in ("prefab", "container", "interval", "maxCount")}})

    added = sorted(set(cs_files(ctx.project_path)) - set(ctx.c0.get("scripts") or []))
    card.check("F4", set(added) <= {h["script"]}, expected=[h["script"]], actual=added)
    diffs = other_states(ctx, h["emitter"])
    card.check("F5", not diffs, actual={"diffs": diffs[:5]})

    holder_ser = (p.ser(found[0], cls) or {}) if len(found) == 1 else {}
    holder_container = p.owner(holder_ser.get("container")) if holder_ser else None
    holder_interval = V.floats(holder_ser.get("interval"))
    holder_count = V.as_int(holder_ser.get("maxCount"))
    answer_ok = (V.norm_path(V.answer(ctx, "scriptPath")) == h["script"] and len(found) == 1 and
                 V.same_id(V.answer(ctx, "emitterInstanceId"), found[0]) and
                 holder_container is not None and V.same_id(V.answer(ctx, "containerInstanceId"), holder_container) and
                 len(holder_interval) == 1 and
                 V.close_tuple(V.answer(ctx, "interval"), (holder_interval[0],), VALUE_TOL) and
                 holder_count is not None and V.as_int(V.answer(ctx, "maxCount")) == holder_count and
                 isinstance(V.answer(ctx, "declaredComplete"), bool))
    card.check("F7", answer_ok, actual=ctx.answer)
    bad = ctx.violations()
    card.check("F8", not bad, actual={"violations": bad[:10]})
    direct = [rec for rec in ctx.direct_writes() if not SCRIPT_RE.search(str(rec.get("assetPath") or ""))]
    card.check("F9", not direct, actual={"directWrites": direct[:10]})


def SCENARIO(ctx):
    """Play Mode: read the container at each midpoint and at the settle sample, plus the first spawned child."""
    h, values = ctx.handles, spec(ctx.seed)
    times = sample_times(values)
    target = V.address(h["container"])
    steps, clock = [], 0.0
    for moment in times:
        steps.append({"kind": "wait_seconds", "seconds": round(moment - clock, 3)})
        steps.append({"kind": "read", "skill": "gameobject_get_info", "args": dict(target)})
        clock = moment
    child_name = _asset_name(h["prefab"]) + "(Clone)"
    steps.append({"kind": "read", "skill": "gameobject_get_info",
                  "args": {"path": "%s/%s" % (h["containerPath"], child_name)}})
    steps.append({"kind": "assert", "skill": "editor_get_state", "select": "isPlaying", "expected": True})
    return {"executionMode": "play", "timeoutSeconds": min(600, int(clock) + 60), "steps": steps}


def verify_runtime(ctx, card):
    scenario = ctx.scenario or {}
    h, values = ctx.handles, spec(ctx.seed)
    interval, count = values["interval"], values["maxCount"]
    times = sample_times(values)
    reads = [step.get("actual") if isinstance(step.get("actual"), dict) else {}
             for step in scenario.get("steps") or [] if step.get("kind") == "read"]
    container_reads = reads[:len(times)]
    child_read = reads[len(times)] if len(reads) > len(times) else {}

    rows = []
    schedule_ok = len(container_reads) == len(times)
    for moment, info in zip(times[:-1], container_reads[:-1]):
        actual_count = (info or {}).get("childCount")
        ok = window_ok(actual_count, count, interval, moment)
        schedule_ok = schedule_ok and ok
        rows.append({"t": moment, "childCount": actual_count, "ok": ok})

    final_info = container_reads[-1] if container_reads else {}
    final_children = (final_info or {}).get("children") or []
    prefab_name = _asset_name(h["prefab"])
    final_count_ok = (V.as_int((final_info or {}).get("childCount")) == count and
                      len(final_children) == count and
                      all(str(c.get("name") or "").startswith(prefab_name) for c in final_children))
    rows.append({"t": times[-1] if times else None, "childCount": (final_info or {}).get("childCount"),
                 "ok": final_count_ok})

    container_pos = V.floats((final_info or {}).get("position"))
    child_pos = V.floats((child_read or {}).get("position"))
    child_parent = V.norm_path((child_read or {}).get("parentPath"))
    pos_ok = (len(container_pos) == 3 and len(child_pos) == 3 and
              child_parent == h["containerPath"] and
              V.vec_close(child_pos, container_pos, POS_TOL))

    passed = scenario.get("passed") is True
    card.check("F6", passed and schedule_ok and final_count_ok and pos_ok,
               expected={"schedule": list(range(count + 1)) + [count],
                         "prefabPrefix": prefab_name, "containerPos": container_pos, "posTol": POS_TOL},
               actual={"passed": passed, "error": scenario.get("error"), "status": scenario.get("status"),
                       "samples": rows,
                       "finalChildren": [c.get("name") for c in final_children[:10]],
                       "childPos": child_pos, "childParent": child_parent})

# Producer:Betsy
