"""DC3 private verifier: a MonoBehaviour renamed into a namespace and moved (GUID kept), two fields renamed with their
data carried over on six instances (two plain scene objects, an overridden instance of a prefab, an instance of the
prefab's variant, the prefab and the variant), and the behaviour checked in Play Mode.

What the task probes: deleting the script and creating a new file gives a new GUID, so every instance loses its
script; a field renamed without [FormerlySerializedAs] loses its data on every instance (in memory at the domain
reload and in every file still saved with the old name), which agents then paper over by setting the values again;
the file name must follow the class (a MonoBehaviour binds by file name) and the namespace must be the given one.
Values are read back through the skills: the scene components, the prefab assets through
prefab_get_serialized_properties (the in-memory assets, which went through the same reload), and LapSeconds() of
the four scene instances in Play Mode against the value their stored data gives.
"""
import hashlib
import math
import os
import random
import re

from usk_eval import verifylib as V

NAMESPACES = ("AI", "Units", "Agents", "Npc")
NEW_NAMES = ("Patroller", "Sentinel", "Warden", "Rover")
SPEED_PAIRS = (("walkSpeed", "moveSpeed"), ("pace", "speed"), ("velocity", "travelSpeed"))
ROUTE_PAIRS = (("points", "waypoints"), ("path", "route"), ("nodes", "stops"))
NAME_SALT = 0xDC03
SPEED_DEFAULT, PAUSE_DEFAULT = 1.5, 0.5
COMMENT_RE = re.compile(r"//[^\n]*|/\*.*?\*/", re.S)
SCRIPT_RE = re.compile(r"\.cs(\.meta)?$")
FLOAT_TYPES = ("float", "Single", "System.Single")
ROUTE_TYPES = ("Transform[]", "UnityEngine.Transform[]")

ASSERTIONS = [
    ("Q1", 1.0, False, ["R1"], "the script sits at the new path with its original GUID and the old file is gone"),
    ("Q2", 0.75, False, ["R1"], "the script declares the class in the given namespace, its file name follows the "
                                "class, and the project compiles"),
    ("Q3", 0.75, False, ["R2"], "renamed fields with the original types and defaults, the pause field kept, the old "
                                "names gone, RouteLength() and LapSeconds() still public"),
    ("Q4", 0.75, False, ["R3"], "FormerlySerializedAs carries each old field name to its new field"),
    ("Q5", 1.5, False, ["R3"], "the four scene instances keep their speed, waypoints (same Transforms, same order) "
                               "and pause"),
    ("Q6", 1.25, False, ["R3"], "the prefab and its variant keep their speed, waypoints and pause"),
    ("Q7", 0.5, False, ["R4"], "no skill wrote the speed, waypoint or pause fields of the component"),
    ("Q8", 0.5, True, ["R5"], "no other script changed or added; the other objects are unchanged"),
    ("Q9", 0.25, True, ["G1", "G2"], "no change outside R/A, no other scene saved or opened"),
    ("Q10", 0.5, False, ["R7", "G8"], "the answer reports the script's path, its GUID and the class's full name as "
                                      "they are"),
    ("Q11", 2.0, False, ["R6"], "in Play Mode no error is logged and LapSeconds() of each scene instance matches its "
                                "data (fraction of the four instances)"),
    ("Q12", 0.25, False, ["G4"], "no file other than C# scripts was written under A around the skills"),
]

HINTS = {
    "Q1": {"zh-CN": "移动/重命名原文件（asset_move、script_move、script_rename 都会保留 GUID），不要删除后新建。",
           "en": "Move and rename the original file (asset_move, script_move and script_rename keep the GUID); do "
                 "not delete it and create a new one."},
    "Q2": {"zh-CN": "类要放进给定的命名空间，文件名与类名一致，并等编译完成且没有错误。",
           "en": "Put the class in the given namespace, keep the file name equal to the class name, and wait for a "
                 "clean compilation."},
    "Q3": {"zh-CN": "只改两个字段的名字；类型、默认值、另一个字段和两个方法都保持原样。",
           "en": "Rename only the two fields; keep the types, defaults, the other field and both methods."},
    "Q4": {"zh-CN": "改名后要让按旧名保存的数据继续加载到新字段。",
           "en": "After the rename, data saved under the old names must still load into the new fields."},
    "Q5": {"zh-CN": "改名后场景中每个实例的数值和路点引用都应保留。",
           "en": "Every scene instance must keep its values and waypoint references through the change."},
    "Q6": {"zh-CN": "预制体及其变体的数值与路点引用也要保留。",
           "en": "The prefab and its variant must keep their values and waypoint references as well."},
    "Q7": {"zh-CN": "不要用技能把这些值重新写回实例。", "en": "Do not write these values back with skills."},
    "Q8": {"zh-CN": "不要改动其他脚本和对象，也不要新增代码文件。",
           "en": "Change no other script or object and add no code file."},
    "Q9": {"zh-CN": "所有修改都必须在 R 与 A 之内，且只能保存本轮场景。",
           "en": "Keep every change inside R and A, and save no scene but this run's."},
    "Q10": {"zh-CN": "答案中的路径、GUID 和类名要与实际状态一致，并写明 declaredComplete。",
            "en": "Report the actual path, GUID and class name, and declaredComplete."},
    "Q11": {"zh-CN": "行为保持不变：LapSeconds() 由保留下来的速度、路点和停顿时间算出；运行时不能有错误日志。",
            "en": "Keep the behaviour: LapSeconds() follows from the kept speed, waypoints and pause; no error may "
                  "be logged at runtime."},
    "Q12": {"zh-CN": "除 C# 脚本外，不要绕过技能直接在 A 下写文件。",
            "en": "Apart from C# scripts, do not write files under A directly; use the skills."},
}


def names(seed):
    """Verbatim copy of the public task's names(seed) (a test pins the two together)."""
    rng = random.Random(seed ^ NAME_SALT)
    return {"namespace": "Game." + rng.choice(NAMESPACES), "class": rng.choice(NEW_NAMES)}


def renames(fields):
    """The new names of the speed and route fields."""
    return dict(SPEED_PAIRS)[fields[0]], dict(ROUTE_PAIRS)[fields[1]]


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


def _guid(ctx, asset_path):
    try:
        return V.uyaml.meta_guid(_disk(ctx, asset_path))
    except (OSError, ValueError, TypeError):
        return None


def cs_files(ctx):
    out = []
    for base, dirs, files in os.walk(os.path.join(ctx.project_path, "Assets")):
        dirs.sort()
        out += [os.path.relpath(os.path.join(base, name), ctx.project_path).replace(os.sep, "/")
                for name in sorted(files) if name.endswith(".cs")]
    return sorted(out)


def _num(value):
    try:
        number = float(str(value).strip().rstrip("fF"))
    except (TypeError, ValueError):
        return None
    return number if math.isfinite(number) else None


def _close(a, b, tol=1e-4):
    a, b = _num(a), _num(b)
    return a is not None and b is not None and abs(a - b) <= tol


def _read(ctx, skill, **args):
    """Result of a read-only skill, or None when it answers with a business error."""
    envelope = ctx.probe.c.read_raw(skill, **args)
    if not isinstance(envelope, dict) or envelope.get("status") in ("error", "partial") or envelope.get("errorCode"):
        return None
    result = envelope.get("result", envelope)
    return result if isinstance(result, dict) and result.get("success") is not False else None


def compiled(ctx):
    """True when no compilation is running and the last one ended without errors."""
    comp = _read(ctx, "debug_check_compilation") or {}
    last = comp.get("lastCompilation") or {}
    return not comp.get("isCompiling") and last.get("succeeded") is not False and not last.get("errorCount")


def script_now(ctx):
    """The .cs that carries the original GUID, else the file at the new path, else None."""
    guid = ctx.c0.get("guid")
    for path in cs_files(ctx):
        if guid and _guid(ctx, path) == guid:
            return path
    return ctx.handles["newPath"] if _text(ctx, ctx.handles["newPath"]) is not None else None


def declaration(code, name):
    """(attribute text, type, initializer or None) of `public <type> <name> [= value];` in code, or None."""
    match = re.search(r"((?:\[[^\]]*\]\s*)*)public\s+([\w.\[\]]+)\s+%s\s*(?:=\s*([^;]+))?;" % re.escape(name), code)
    if not match:
        return None
    return match.group(1), match.group(2), (match.group(3) or "").strip() or None


def full_name(code):
    """'Namespace.Class' of the MonoBehaviour class the code declares (or just 'Class'), or None."""
    cls = re.search(r"\bclass\s+(\w+)\s*:\s*(?:UnityEngine\.)?MonoBehaviour\b", code or "")
    if not cls:
        return None
    space = re.search(r"\bnamespace\s+([\w.]+)\s*[{;]", code[:cls.start()])
    return "%s.%s" % (space.group(1), cls.group(1)) if space else cls.group(1)


def carried(attrs, old):
    return re.search(r"FormerlySerializedAs(?:Attribute)?\s*\(\s*\"%s\"\s*\)" % re.escape(old), attrs or "") is not None


def holder_values(values, speed, route, pause):
    size = V.as_int(values.get(route + ".Array.size"))
    links = [values.get("%s.Array.data[%d]" % (route, index)) for index in range(size or 0)]
    return {"speed": values.get(speed), "pause": values.get(pause), "size": size, "links": links}


def lap(entry):
    """LapSeconds() of a C0 holder: route length over speed plus pause times the waypoint count."""
    spots = [V.floats(spot) for spot in entry.get("spots") or []]
    if any(len(spot) != 3 for spot in spots) or _num(entry.get("speed")) in (None, 0.0):
        return None
    length = sum(math.dist(spots[i - 1], spots[i]) for i in range(1, len(spots)))
    return length / _num(entry["speed"]) + _num(entry.get("pause")) * len(spots)


def verify(ctx, card):
    p, h, c0 = ctx.probe, ctx.handles, ctx.c0
    moved = names(ctx.seed)
    old_cls = os.path.basename(h["oldPath"])[:-len(".cs")]
    new_cls, new_full = moved["class"], "%s.%s" % (moved["namespace"], moved["class"])
    speed_old, route_old, pause = h["fields"]
    speed_new, route_new = renames(h["fields"])

    guid_new = _guid(ctx, h["newPath"])
    card.check("Q1", guid_new is not None and guid_new == c0.get("guid") and _text(ctx, h["oldPath"]) is None,
               expected={"path": h["newPath"], "guid": c0.get("guid")},
               actual={"guid": guid_new, "oldStillThere": _text(ctx, h["oldPath"]) is not None})

    path = script_now(ctx)
    code = COMMENT_RE.sub("", _text(ctx, path) or "") if path else ""
    declared = full_name(code)
    analyzed = (_read(ctx, "script_analyze", scriptName=new_cls) or {}).get("fullName")
    feedback = (_read(ctx, "script_get_compile_feedback", scriptPath=path) or {}).get("hasErrors") if path else None
    card.check("Q2", path is not None and declared == new_full and os.path.basename(path) == new_cls + ".cs" and
               compiled(ctx) and feedback is False and analyzed == new_full,
               expected={"class": new_full}, actual={"script": path, "declared": declared, "analyzed": analyzed,
                                                     "compiled": compiled(ctx), "hasErrors": feedback})

    speed_decl, route_decl, pause_decl = (declaration(code, name) for name in (speed_new, route_new, pause))
    shape = {"speed": bool(speed_decl) and speed_decl[1] in FLOAT_TYPES and _close(speed_decl[2], SPEED_DEFAULT),
             "route": bool(route_decl) and route_decl[1] in ROUTE_TYPES and
             (route_decl[2] in (None, "null") or re.match(r"new\s+(?:UnityEngine\.)?Transform\s*\[\s*0\s*\]$",
                                                          route_decl[2] or "") is not None),
             "pause": bool(pause_decl) and pause_decl[1] in FLOAT_TYPES and _close(pause_decl[2], PAUSE_DEFAULT),
             "oldGone": declaration(code, speed_old) is None and declaration(code, route_old) is None,
             "methods": all(re.search(r"\bpublic\s+float\s+%s\s*\(\s*\)" % name, code) for name in
                            ("RouteLength", "LapSeconds"))}
    card.check("Q3", all(shape.values()), actual=shape)
    attrs = {"speed": bool(speed_decl) and carried(speed_decl[0], speed_old),
             "route": bool(route_decl) and carried(route_decl[0], route_old)}
    card.check("Q4", all(attrs.values()), actual=attrs)

    rows = c0.get("holders") or []
    seen, kept = [], []
    for entry in rows:
        got = holder_values(p.ser(entry["ref"], new_cls) or {}, speed_new, route_new, pause)
        owners = [p.owner(link) for link in got["links"]]
        ok = (_close(got["speed"], entry.get("speed")) and _close(got["pause"], entry.get("pause")) and
              got["size"] == len(entry.get("links") or []) and
              all(V.same_id(a, b) for a, b in zip(owners, entry.get("links") or [])))
        kept.append(ok)
        seen.append(dict(got, owners=owners))
    card.check("Q5", len(rows) == 4 and all(kept), expected=rows, actual=seen)

    assets = {}
    for key in ("basePrefab", "variantPrefab"):
        res = _read(ctx, "prefab_get_serialized_properties", prefabPath=h[key], componentType=new_cls) or {}
        got = holder_values({row.get("propertyPath"): row.get("value") for row in res.get("properties") or []
                             if isinstance(row, dict)}, speed_new, route_new, pause)
        before = (c0.get("assets") or {}).get(key) or {}
        got["names"] = [(V.parse_ref(link) or (None, None, None))[2] for link in got["links"]]
        assets[key] = {"ok": _close(got["speed"], before.get("speed")) and _close(got["pause"], before.get("pause"))
                       and got["names"] == before.get("links"), "seen": got}
    card.check("Q6", all(item["ok"] for item in assets.values()), expected=c0.get("assets"), actual=assets)

    watched = {speed_old, speed_new, route_old, route_new, pause}
    writes = [rec for rec in ctx.write_records() if rec.get("kind") == "property_changed" and
              rec.get("source") == "rest" and rec.get("targetType") in (old_cls, new_cls) and
              str(rec.get("property") or "").split(".")[0] in watched]
    card.check("Q7", not writes, actual={"writes": writes[:10]})

    before = {item[0]: item[1] for item in c0.get("scripts") or []}
    changed = sorted(item for item in before if item != h["oldPath"] and _sha(ctx, item) != before[item])
    added = sorted(item for item in cs_files(ctx) if item not in before and item != path)
    holders = [entry["ref"] for entry in rows]
    diffs = []
    for ref, state in c0.get("objects") or []:
        now, was = V.go_state(p, ref), dict(state or {})
        if any(V.same_id(ref, item) for item in holders):
            was.pop("components", None)
            now = dict(now or {})
            now.pop("components", None)
        ok, diff = V.same(was, now)
        if not ok:
            diffs.append({"ref": ref, "diff": diff})
    card.check("Q8", not changed and not added and not diffs,
               actual={"changedScripts": changed, "addedScripts": added, "diffs": diffs[:5]})

    bad = ctx.violations()
    card.check("Q9", not bad, actual={"violations": bad[:10]})

    told = V.norm_path(V.answer(ctx, "scriptPath"))
    told_code = COMMENT_RE.sub("", _text(ctx, told) or "") if told.startswith("Assets/") and ".." not in told else ""
    card.check("Q10", bool(told_code) and str(V.answer(ctx, "scriptGuid") or "").strip().lower() ==
               str(p.guid(told) or "").lower() and V.answer(ctx, "className") == full_name(told_code) and
               isinstance(V.answer(ctx, "declaredComplete"), bool), actual=ctx.answer)

    direct = [rec for rec in ctx.direct_writes() if not SCRIPT_RE.search(str(rec.get("assetPath") or ""))]
    card.check("Q12", not direct, actual={"directWrites": direct[:10]})


def SCENARIO(ctx):
    """Play Mode: LapSeconds() and the serialized values of each scene instance's component, addressed by the short
    class name. With compile errors Unity refuses Play Mode, so the scenario is then a single edit-mode check that
    fails at once."""
    check = {"kind": "assert", "skill": "editor_get_state", "select": "isPlaying", "expected": True}
    if not compiled(ctx):
        return {"executionMode": "edit", "timeoutSeconds": 30, "steps": [check]}
    cls = names(ctx.seed)["class"]
    steps = [{"kind": "wait_frames", "frames": 2}]
    for entry in ctx.c0.get("holders") or []:
        where = V.address(entry["ref"])
        steps += [{"kind": "invoke", "skill": "component_invoke_method",
                   "args": dict({"componentType": cls, "methodName": "LapSeconds"}, **where)},
                  {"kind": "read", "skill": "component_get_serialized_properties",
                   "args": dict({"componentType": cls}, **where)}]
    return {"executionMode": "play", "timeoutSeconds": 120, "steps": steps + [check]}


def verify_runtime(ctx, card):
    scenario = ctx.scenario or {}
    speed_new, route_new = renames(ctx.handles["fields"])
    pause = ctx.handles["fields"][2]
    by_index = {step.get("index"): step for step in scenario.get("steps") or [] if isinstance(step, dict)}
    rows, results = ctx.c0.get("holders") or [], []
    for number, entry in enumerate(rows):
        call, read = by_index.get(1 + 2 * number) or {}, by_index.get(2 + 2 * number) or {}
        value = (call.get("actual") or {}).get("returnValue") if isinstance(call.get("actual"), dict) else None
        want = lap(entry)
        props = (read.get("actual") or {}).get("properties") if isinstance(read.get("actual"), dict) else None
        got = holder_values({row.get("propertyPath"): row.get("value") for row in props or [] if isinstance(row, dict)},
                            speed_new, route_new, pause)
        ok = want is not None and _num(value) is not None and abs(_num(value) - want) <= 1e-3 * max(1.0, abs(want)) \
            and _close(got["speed"], entry.get("speed")) and _close(got["pause"], entry.get("pause")) and \
            got["size"] == len(entry.get("links") or [])
        results.append({"ok": ok, "value": value, "expected": want})
    passed = scenario.get("passed") is True
    good = sum(1 for item in results if item["ok"])
    card.fraction("Q11", good / float(len(rows)) if passed and rows else 0.0,
                  actual={"passed": passed, "error": scenario.get("error"), "status": scenario.get("status"),
                          "instances": results})

# Producer:Betsy
