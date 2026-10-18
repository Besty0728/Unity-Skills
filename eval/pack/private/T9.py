"""T9 private verifier: two serialized fields renamed in place with their stored values intact, then a Play Mode check.

What the task probes: renaming alone resets every Guard's moveSpeed and patrolTarget after the reload (only
[FormerlySerializedAs] carries the old data over); writing the values back through skills is forbidden and shows in the
journal; the PatrolRoute script also has a field called `speed` and must stay as it is; no helper code. The expected
values are each Guard's C0 values (speed, laps, the GameObject its waypoint pointed at). Deleting and recreating the
script is not penalised by itself: Unity hands the same path its old GUID back. Class names carry a seeded token plus
the run id, so a run never meets a type compiled by an earlier run. The runtime part enters Play Mode and reads every
Guard's component back; while scripts fail to compile Unity refuses Play Mode, so the scenario is then one edit-mode
check that fails at once instead of waiting out its timeout.
"""
import hashlib
import os
import re

from usk_eval import verifylib as V

RENAMED = (("speed", "moveSpeed", "float"), ("waypoint", "patrolTarget", "Transform"))
WATCHED = ("speed", "moveSpeed", "waypoint", "patrolTarget")
TYPE_NAMES = {"float": ("float", "Single", "System.Single"), "int": ("int", "Int32", "System.Int32"),
              "Transform": ("Transform", "UnityEngine.Transform")}
TYPE_RES = {"float": r"(?:System\.)?(?:float|Single)", "Transform": r"(?:UnityEngine\.)?Transform"}
DECL_RE = re.compile(r"\bpublic\s+([\w.]+)\s+(\w+)\s*(?:=\s*([^;\n]+?))?\s*(?:;|$)", re.M)
ATTR_RE = (r"\[[^\]]*?\b(?:UnityEngine\.Serialization\.)?FormerlySerializedAs(?:Attribute)?\s*\(\s*\"{old}\"\s*\)"
           r"[^\]]*\](?:\s*\[[^\]]*\])*\s*public\s+{type}\s+{new}\b")
COMMENT_RE = re.compile(r"/\*[\s\S]*?\*/|//[^\n]*")

ASSERTIONS = [
    ("C1", 1.0, False, ["R1"], "Patrol script: same class, fields moveSpeed / patrolTarget / laps with their types and "
                               "defaults, no speed or waypoint field"),
    ("C2", 1.0, False, ["R2"], "FormerlySerializedAs(\"speed\") on moveSpeed and (\"waypoint\") on patrolTarget"),
    ("C3", 0.5, False, ["R1"], "compilation clean and every Guard's loaded component exposes the renamed fields"),
    ("C4", 1.5, False, ["R2"], "each Guard keeps its C0 speed in moveSpeed"),
    ("C5", 1.5, False, ["R2"], "each Guard's patrolTarget references the Transform of its C0 waypoint"),
    ("C6", 0.5, False, ["R2"], "each Guard keeps its C0 laps"),
    ("C7", 1.0, False, ["R3"], "no skill wrote speed, waypoint, moveSpeed or patrolTarget on the Patrol component"),
    ("C8", 0.75, True, ["R4"], "the Patrol script keeps its GUID; no other code file added or changed"),
    ("C9", 0.5, True, ["R5"], "guards, waypoints and Route unchanged, Route's PatrolRoute values included"),
    ("C10", 0.25, True, ["G1", "G2"], "no change outside R/A, no other scene saved or opened"),
    ("C11", 0.5, False, ["R7", "G8"], "the answer reports the script and every Guard as they are"),
    ("C12", 0.75, False, ["R6"], "in Play Mode no error is logged and every Guard holds its C0 values"),
    ("C13", 0.25, False, ["G4"], "no file other than C# scripts was written under A around the skills"),
]

HINTS = {
    "C1": {"zh-CN": "只改这两个字段的名字；类名、laps 以及各字段的类型和默认值都要保持原样。",
           "en": "Rename just these two fields; keep the class name, laps and every field's type and default value."},
    "C2": {"zh-CN": "改名后要让按旧名保存的数据继续加载到新字段。",
           "en": "After the rename, data saved under the old names must still load into the new fields."},
    "C3": {"zh-CN": "等编译完成并确认没有错误。", "en": "Wait for compilation to finish without errors."},
    "C4": {"zh-CN": "改名后每个 Guard 的 moveSpeed 应保留原来 speed 的值。",
           "en": "After the rename every Guard's moveSpeed must keep the old speed value."},
    "C5": {"zh-CN": "改名后每个 Guard 的 patrolTarget 应仍引用它原来的路点。",
           "en": "After the rename every Guard's patrolTarget must still reference its own waypoint."},
    "C6": {"zh-CN": "laps 的值不应改变。", "en": "The laps values must not change."},
    "C7": {"zh-CN": "不要用技能把这些值重新写回 Guard。", "en": "Do not write these values back onto the Guards with skills."},
    "C8": {"zh-CN": "原地修改这个脚本，不要改其他脚本，也不要新增代码文件。",
           "en": "Edit this script in place; change no other script and add no code file."},
    "C9": {"zh-CN": "不要改动 Guard、路点和 Route，包括 Route 上组件的值。",
           "en": "Leave the Guards, the waypoints and Route alone, including the values of Route's component."},
    "C10": {"zh-CN": "所有修改都必须在 R 与 A 之内，且只能保存本轮场景。",
            "en": "Keep every change inside R and A, and save no scene but this run's."},
    "C11": {"zh-CN": "答案中的路径、id 和数值要与实际状态一致，并写明 declaredComplete。",
            "en": "Report the actual path, ids and values, and declaredComplete."},
    "C12": {"zh-CN": "验收会进入 Play 模式读取每个 Guard 的组件；脚本必须能编译，运行时不能有错误日志，字段值要保持。",
            "en": "Verification enters Play Mode and reads every Guard's component: the scripts must compile, no error "
                  "may be logged and the values must hold."},
    "C13": {"zh-CN": "除 C# 脚本外，不要绕过技能直接在 A 下写文件。",
            "en": "Apart from C# scripts, do not write files under A directly; use the skills."},
}


def class_names(handles):
    """(Patrol class, PatrolRoute class) from the script paths in the handles."""
    def name(path):
        return os.path.basename(path)[:-len(".cs")]
    return name(handles["patrolScript"]), name(handles["routeScript"])


def _disk(ctx, asset_path):
    return os.path.join(ctx.project_path, *asset_path.split("/"))


def _text(ctx, asset_path):
    try:
        with open(_disk(ctx, asset_path), "r", encoding="utf-8-sig") as handle:
            return handle.read()
    except (OSError, UnicodeDecodeError):
        return None


def _digest(ctx, asset_path):
    try:
        with open(_disk(ctx, asset_path), "rb") as handle:
            return hashlib.sha256(handle.read()).hexdigest()
    except OSError:
        return None


def script_digests(ctx):
    """{asset path: sha256} of every .cs file under Assets."""
    out = {}
    for base, dirs, files in os.walk(os.path.join(ctx.project_path, "Assets")):
        dirs.sort()
        for name in sorted(files):
            if name.endswith(".cs"):
                rel = os.path.relpath(os.path.join(base, name), ctx.project_path).replace(os.sep, "/")
                out[rel] = _digest(ctx, rel)
    return out


def _read(ctx, skill, **args):
    """Result of a read-only skill, or None when it answers with a business error."""
    envelope = ctx.probe.c.read_raw(skill, **args)
    if not isinstance(envelope, dict) or envelope.get("status") in ("error", "partial") or envelope.get("errorCode"):
        return None
    result = envelope.get("result", envelope)
    return result if isinstance(result, dict) and result.get("success") is not False else None


def compiled(ctx):
    """True when no compilation is running and the last one ended without errors."""
    state = _read(ctx, "debug_check_compilation") or {}
    last = state.get("lastCompilation") or {}
    return not state.get("isCompiling") and last.get("succeeded") is not False and not last.get("errorCount")


def declarations(code):
    """{field name: (type text, initializer text or None)} of the public fields declared in `code`."""
    return {match.group(2): (match.group(1), match.group(3)) for match in DECL_RE.finditer(code)}


def _number(text):
    try:
        return float(str(text).strip().rstrip("fFdDmM"))
    except ValueError:
        return None


def field_ok(found, name, kind, default):
    """The field exists with the given type and initializer value (None: no initializer or null)."""
    if name not in found or found[name][0] not in TYPE_NAMES[kind]:
        return False
    init = found[name][1]
    if default is None:
        return init is None or init.strip() == "null"
    return init is not None and _number(init) == default


def verify(ctx, card):
    p, h, c0 = ctx.probe, ctx.handles, ctx.c0
    patrol, route = class_names(h)
    code = COMMENT_RE.sub("", _text(ctx, h["patrolScript"]) or "")
    found = declarations(code)
    class_ok = re.search(r"\bclass\s+%s\s*:\s*(?:UnityEngine\.)?MonoBehaviour\b" % re.escape(patrol), code) is not None
    fields = {"moveSpeed": field_ok(found, "moveSpeed", "float", 1.0),
              "patrolTarget": field_ok(found, "patrolTarget", "Transform", None),
              "laps": field_ok(found, "laps", "int", 1.0), "speedGone": "speed" not in found,
              "waypointGone": "waypoint" not in found}
    card.check("C1", class_ok and all(fields.values()), actual=dict(fields, classOk=class_ok))
    attrs = {old: re.search(ATTR_RE.format(old=old, type=TYPE_RES[kind], new=new), code) is not None
             for old, new, kind in RENAMED}
    card.check("C2", all(attrs.values()), actual=attrs)

    feedback = (_read(ctx, "script_get_compile_feedback", scriptPath=h["patrolScript"]) or {}).get("hasErrors")
    rows = c0.get("guards") or []
    values = [p.ser(ref, patrol) or {} for ref, _was in rows]
    exposes = bool(values) and all("moveSpeed" in got and "patrolTarget" in got and "speed" not in got and
                                   "waypoint" not in got for got in values)
    is_clean = compiled(ctx)
    card.check("C3", is_clean and feedback is False and exposes,
               actual={"compiled": is_clean, "hasErrors": feedback, "exposesRenamedFields": exposes})

    speeds = [got.get("moveSpeed") for got in values]
    card.check("C4", len(rows) == 3 and all(V.close_tuple(value, (_number(was.get("speed")),))
                                            for value, (_ref, was) in zip(speeds, rows)),
               expected=[was.get("speed") for _ref, was in rows], actual=speeds)
    holders = [p.owner(got.get("patrolTarget")) for got in values]
    kinds = [(V.parse_ref(got.get("patrolTarget")) or (None, None, None))[1] for got in values]
    card.check("C5", len(rows) == 3 and all(V.same_id(value, was.get("waypoint")) and kind == "UnityEngine.Transform"
                                            for value, kind, (_ref, was) in zip(holders, kinds, rows)),
               expected=[was.get("waypoint") for _ref, was in rows],
               actual={"owners": holders, "raw": [got.get("patrolTarget") for got in values]})
    laps = [got.get("laps") for got in values]
    card.check("C6", len(rows) == 3 and all(V.as_int(value) is not None and
                                            V.as_int(value) == V.as_int(was.get("laps"))
                                            for value, (_ref, was) in zip(laps, rows)),
               expected=[was.get("laps") for _ref, was in rows], actual=laps)

    writes = [rec for rec in ctx.write_records() if rec.get("kind") == "property_changed" and
              rec.get("source") == "rest" and rec.get("targetType") == patrol and
              str(rec.get("property") or "").split(".")[0] in WATCHED]
    card.check("C7", not writes, actual={"writes": writes[:10]})

    now = script_digests(ctx)
    before = {path: sha for path, sha in c0.get("scripts") or []}
    others = sorted(path for path in set(now) | set(before)
                    if path != h["patrolScript"] and now.get(path) != before.get(path))
    guid = V.uyaml.meta_guid(_disk(ctx, h["patrolScript"])) if h["patrolScript"] in now else None
    card.check("C8", guid is not None and guid == c0.get("patrolGuid") and not others,
               expected=c0.get("patrolGuid"), actual={"guid": guid, "otherCodeChanged": others})

    changed = [str(ref) for ref, state in c0.get("objects") or [] if not V.same(state, V.go_state(p, ref))[0]]
    route_same = V.same(c0.get("route"), p.ser(h["route"], route))[0]
    card.check("C9", not changed and route_same, actual={"changedObjects": changed, "routeUnchanged": route_same})

    bad = ctx.violations()
    card.check("C10", not bad, actual={"violations": bad[:10]})

    reported = V.answer(ctx, "guards")
    matched = []
    if isinstance(reported, list) and len(reported) == len(rows):
        for item in reported:
            index = next((i for i, (ref, _was) in enumerate(rows)
                          if isinstance(item, dict) and V.same_id(item.get("instanceId"), ref)), None)
            if index is None or index in matched:
                break
            speed_now, holder_now = _number(speeds[index]) if speeds[index] is not None else None, holders[index]
            told = item.get("waypointInstanceId")
            if not ((speed_now is None and item.get("moveSpeed") is None) or
                    (speed_now is not None and V.close_tuple(item.get("moveSpeed"), (speed_now,)))):
                break
            if not ((holder_now is None and told in (None, 0)) or V.same_id(told, holder_now)):
                break
            matched.append(index)
    answer_ok = (len(rows) == 3 and len(matched) == len(rows) and
                 V.norm_path(V.answer(ctx, "scriptPath")) == h["patrolScript"] and
                 isinstance(V.answer(ctx, "declaredComplete"), bool))
    card.check("C11", answer_ok, actual=ctx.answer)

    direct = [rec for rec in ctx.direct_writes()
              if not re.search(r"\.cs(\.meta)?$", str(rec.get("assetPath") or ""))]
    card.check("C13", not direct, actual={"directWrites": direct[:10]})


def SCENARIO(ctx):
    """Play Mode: read every Guard's Patrol component back. With compile errors Unity refuses Play Mode, so the
    scenario is then a single edit-mode check that fails at once."""
    h = ctx.handles
    patrol, _route = class_names(h)
    check = {"kind": "assert", "skill": "editor_get_state", "select": "isPlaying", "expected": True}
    if not compiled(ctx):
        return {"executionMode": "edit", "timeoutSeconds": 30, "steps": [check]}
    steps = [{"kind": "wait_frames", "frames": 2}]
    steps += [{"kind": "read", "skill": "component_get_serialized_properties",
               "args": dict({"componentType": patrol}, **V.address(ref))} for ref in h["guards"]]
    return {"executionMode": "play", "timeoutSeconds": 90, "steps": steps + [check]}


def verify_runtime(ctx, card):
    scenario = ctx.scenario or {}
    rows = ctx.c0.get("guards") or []
    names = {str(V.object_id(ref)): (state or {}).get("name") for ref, state in ctx.c0.get("objects") or []}
    reads = [step.get("actual") for step in scenario.get("steps") or []
             if step.get("kind") == "read" and isinstance(step.get("actual"), dict)]
    seen = []
    for got in reads:
        values = {row.get("propertyPath"): row.get("value") for row in got.get("properties") or []
                  if isinstance(row, dict)}
        seen.append({key: values.get(key) for key in ("moveSpeed", "patrolTarget", "laps")})
    held = len(rows) == 3 and len(seen) == len(rows)
    for got, (_ref, was) in zip(seen, rows):
        link = V.parse_ref(got.get("patrolTarget"))
        held = held and V.close_tuple(got.get("moveSpeed"), (_number(was.get("speed")),)) and \
            V.as_int(got.get("laps")) == V.as_int(was.get("laps")) and bool(link) and \
            link[1] == "UnityEngine.Transform" and link[2] == names.get(str(V.object_id(was.get("waypoint"))))
    card.check("C12", scenario.get("passed") is True and held,
               actual={"passed": scenario.get("passed"), "error": scenario.get("error"),
                       "status": scenario.get("status"), "guards": seen})

# Producer:Betsy
