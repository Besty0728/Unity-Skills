"""DC7 private verifier: repaired assembly definitions, editor code moved out of the runtime assembly, the combat
component wired once the project compiles, and its runtime value.

What the task probes: deleting the runtime .asmdef also "fixes" compilation (its scripts fall into Assembly-CSharp,
which references every auto-referenced assembly); `#if UNITY_EDITOR` keeps the editor code in the runtime assembly;
a folder named Editor inside an asmdef folder is still that asmdef's (the special folder only applies to the
predefined assemblies); an Editor .asmdef without the includePlatforms restriction is a runtime assembly; a copy of
the editor script instead of a move gets a new GUID. Which assembly governs a file is decided from disk: the nearest
folder (the file's own, then its parents) holding an .asmdef, or an .asmref pointing at one. Assembly definitions
are compared as JSON with Unity's defaults filled in, so rewriting the file with other formatting is fine.
"""
import hashlib
import json
import os
import random
import re

from usk_eval import verifylib as V

ALLOWED_DIRECT = re.compile(r"\.(cs|asmdef|asmref)(\.meta)?$")
ASMDEF_DEFAULTS = {"rootNamespace": "", "references": [], "includePlatforms": [], "excludePlatforms": [],
                   "allowUnsafeCode": False, "overrideReferences": False, "precompiledReferences": [],
                   "autoReferenced": True, "defineConstraints": [], "versionDefines": [], "noEngineReferences": False}
ASMDEF_KEYS = ("coreAsmdef", "dataAsmdef", "playAsmdef")
RUNTIME_SCRIPTS = ("statsScript", "tuningScript", "combatScript")
HPS = (8, 10, 12, 14)
FACTORS = (2, 3, 4, 5)
SPEC_SALT = 0xDC07

ASSERTIONS = [
    ("A1", 1.0, False, ["R1"], "compilation finished without errors for the project, the combat script and the "
                               "editor script"),
    ("A2", 1.5, False, ["R2"], "the three assembly definitions keep path, GUID and name; only references were added; "
                               "the runtime scripts keep their folders"),
    ("A3", 1.75, False, ["R3"], "a new assembly definition with the given name, includePlatforms only Editor and a "
                                "reference to the core assembly governs the editor script, which kept its GUID"),
    ("A4", 0.75, True, ["R4"], "every existing script keeps its content and GUID and no code file was added"),
    ("A5", 1.0, False, ["R5"], "exactly one combat component in the scene, on the given object"),
    ("A6", 0.5, True, ["R7"], "the arena and its other objects are unchanged"),
    ("A7", 0.5, True, ["G1", "G2"], "no change outside R/A, no other scene saved or opened"),
    ("A8", 0.25, False, ["G4"], "no file other than scripts and assembly definitions was written under A around the "
                                "skills"),
    ("A9", 0.5, False, ["R8", "G8"], "the answer reports the editor .asmdef, the editor script and the object "
                                     "carrying the combat component as they are"),
    ("A10", 2.25, False, ["R6"], "in Play Mode no error is logged and power holds hp times factor"),
]

HINTS = {
    "A1": {"zh-CN": "看编译错误找出缺少的程序集引用，补上后等编译完成并确认没有错误。",
           "en": "Read the compile errors to find the missing assembly references, add them and wait for a clean "
                 "compilation."},
    "A2": {"zh-CN": "不要删除、移动或重命名已有的 .asmdef，只能往 references 里加条目；运行时脚本不要挪。",
           "en": "Do not delete, move or rename the existing .asmdef files; only add references; leave the runtime "
                 "scripts where they are."},
    "A3": {"zh-CN": "新建指定名称的 Editor 程序集定义（includePlatforms 只有 Editor，引用 core 程序集），让编辑器脚本归它管辖；asmdef 文件夹里名为 Editor 的子文件夹本身不会改变归属。",
           "en": "Create the named Editor assembly definition (includePlatforms only Editor, referencing the core "
                 "assembly) and make it govern the editor script; a subfolder named Editor inside an asmdef folder "
                 "changes nothing by itself."},
    "A4": {"zh-CN": "不要修改已有脚本的内容（包括加 #if），移动要保留 GUID，也不要新增脚本。",
           "en": "Do not change any existing script (no #if either); a move must keep the GUID; add no script."},
    "A5": {"zh-CN": "编译通过后把战斗组件挂到题目给出的对象上，只挂一个。",
           "en": "After a clean compilation attach one combat component to the given object."},
    "A6": {"zh-CN": "不要改动场景中的其他对象。", "en": "Leave the other objects in the scene alone."},
    "A7": {"zh-CN": "所有修改都必须在 R 与 A 之内，且只能保存本轮场景。",
           "en": "Keep every change inside R and A, and save no scene but this run's."},
    "A8": {"zh-CN": "除脚本和程序集定义外，不要绕过技能直接在 A 下写文件。",
           "en": "Apart from scripts and assembly definitions, do not write files under A directly."},
    "A9": {"zh-CN": "答案里的路径要与实际状态一致，并写明 declaredComplete。",
           "en": "Report the actual paths, and declaredComplete."},
    "A10": {"zh-CN": "组件挂上后进入 Play 时 power 由 Awake 计算；运行时不能有错误日志。",
            "en": "With the component attached, Awake computes power in Play Mode; no error may be logged at runtime."},
}


def spec(seed):
    """Verbatim copy of the public task's spec(seed) (a test pins the two together)."""
    rng = random.Random(seed ^ SPEC_SALT)
    return {"hp": rng.choice(HPS), "factor": rng.choice(FACTORS)}


def class_name(script_path):
    return os.path.basename(script_path)[:-len(".cs")]


def _disk(ctx, asset_path):
    return os.path.join(ctx.project_path, *asset_path.split("/"))


def _rel(ctx, full):
    return os.path.relpath(full, ctx.project_path).replace(os.sep, "/")


def _sha(ctx, asset_path):
    try:
        with open(_disk(ctx, asset_path), "rb") as handle:
            return hashlib.sha256(handle.read()).hexdigest()
    except OSError:
        return None


def _guid(ctx, asset_path):
    try:
        return V.uyaml.meta_guid(_disk(ctx, asset_path))
    except (OSError, ValueError):
        return None


def _json(ctx, asset_path):
    try:
        with open(_disk(ctx, asset_path), "r", encoding="utf-8-sig") as handle:
            data = json.load(handle)
    except (OSError, ValueError):
        return None
    return data if isinstance(data, dict) else None


def files_under(ctx, asset_root, suffix):
    """Asset paths of the files under asset_root ending with suffix, sorted."""
    out = []
    for base, dirs, files in os.walk(_disk(ctx, asset_root)):
        dirs.sort()
        out += [_rel(ctx, os.path.join(base, name)) for name in sorted(files) if name.endswith(suffix)]
    return sorted(out)


def _loads(text):
    """An .asmdef stored in C0 as JSON text (its keys stay out of the C0 key space)."""
    try:
        data = json.loads(text) if isinstance(text, str) else None
    except ValueError:
        return None
    return data if isinstance(data, dict) else None


def normalized(data):
    """An .asmdef as Unity reads it: missing keys take their defaults; references as a list of texts."""
    out = dict(ASMDEF_DEFAULTS, **(data or {}))
    refs = out.get("references")
    out["references"] = [str(item) for item in refs] if isinstance(refs, list) else []
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
    comp = _read(ctx, "debug_check_compilation") or {}
    last = comp.get("lastCompilation") or {}
    return not comp.get("isCompiling") and last.get("succeeded") is not False and not last.get("errorCount")


def holders(ctx, cls):
    found = _read(ctx, "gameobject_find", component=cls, limit=500) or {}
    return [V.object_id(obj) for obj in found.get("objects") or []]


def by_guid(ctx, guid, suffix=".cs"):
    """The asset path under Assets whose .meta carries `guid`, or None."""
    if not guid:
        return None
    for path in files_under(ctx, "Assets", suffix):
        if _guid(ctx, path) == guid:
            return path
    return None


def assemblies(ctx):
    """{asmdef path: {"name", "guid", "data"}} of every .asmdef under Assets."""
    return {path: {"name": (_json(ctx, path) or {}).get("name"), "guid": _guid(ctx, path), "data": _json(ctx, path)}
            for path in files_under(ctx, "Assets", ".asmdef")}


def resolve(found, reference):
    """The .asmdef path a reference ("Name" or "GUID:<guid>") names, or None."""
    text = str(reference or "")
    for path, info in found.items():
        if (text.startswith("GUID:") and text[5:].lower() == str(info["guid"] or "").lower()) or text == info["name"]:
            return path
    return None


def governing(ctx, asset_path, found):
    """The .asmdef path that owns `asset_path`: the nearest folder holding an .asmdef, or an .asmref pointing at
    one; None when no folder up to Assets holds either (a predefined assembly)."""
    folder = os.path.dirname(asset_path)
    while folder and folder != "Assets":
        names = sorted(os.listdir(_disk(ctx, folder))) if os.path.isdir(_disk(ctx, folder)) else []
        defs = ["%s/%s" % (folder, name) for name in names if name.endswith(".asmdef")]
        if defs:
            return defs[0]
        refs = ["%s/%s" % (folder, name) for name in names if name.endswith(".asmref")]
        if refs:
            return resolve(found, (_json(ctx, refs[0]) or {}).get("reference"))
        folder = os.path.dirname(folder)
    return None


def references(found, data, target):
    return any(resolve(found, item) == target for item in normalized(data)["references"])


def editor_asmdefs(ctx, found):
    """New .asmdef paths (not in C0) under A/Scripts whose assembly name is the requested one."""
    known = {entry.get("path") for entry in (ctx.c0.get("files") or {}).values()}
    root = ctx.handles["scripts"] + "/"
    return [path for path, info in sorted(found.items())
            if path not in known and path.startswith(root) and info["name"] == ctx.handles["editorAsm"]]


def verify(ctx, card):
    p, h, c0 = ctx.probe, ctx.handles, ctx.c0
    files = c0.get("files") or {}
    report_guid = (files.get("reportScript") or {}).get("guid")
    report_now = by_guid(ctx, report_guid)
    combat = class_name(h["combatScript"])

    feedback = [(_read(ctx, "script_get_compile_feedback", scriptPath=path) or {}).get("hasErrors")
                for path in [h["combatScript"]] + ([report_now] if report_now else [])]
    card.check("A1", compiled(ctx) and feedback and all(value is False for value in feedback),
               actual={"compiled": compiled(ctx), "hasErrors": feedback})

    found = assemblies(ctx)
    kept = {}
    for key in ASMDEF_KEYS:
        before = files.get(key) or {}
        now = _json(ctx, h[key])
        old = normalized(_loads((c0.get("asmdefs") or {}).get(key)))
        new = normalized(now)
        same_rest = {k: v for k, v in old.items() if k != "references"} == \
            {k: v for k, v in new.items() if k != "references"}
        grown = set(old["references"]) <= set(new["references"])
        kept[key] = now is not None and _guid(ctx, h[key]) == before.get("guid") and same_rest and grown
    stayed = {key: _sha(ctx, h[key]) is not None and _guid(ctx, h[key]) == (files.get(key) or {}).get("guid")
              for key in RUNTIME_SCRIPTS}
    card.check("A2", all(kept.values()) and all(stayed.values()), actual={"asmdefs": kept, "runtimeScripts": stayed})

    new_defs = editor_asmdefs(ctx, found)
    core = h["coreAsmdef"]
    owner = governing(ctx, report_now, found) if report_now else None
    editor_ok = False
    if len(new_defs) == 1:
        data = normalized(found[new_defs[0]]["data"])
        editor_ok = (data.get("includePlatforms") == ["Editor"] and references(found, data, core) and
                     owner == new_defs[0])
    card.check("A3", editor_ok, expected={"name": h["editorAsm"], "includePlatforms": ["Editor"], "references": core},
               actual={"editorAsmdefs": new_defs, "editorScript": report_now, "governedBy": owner,
                       "data": [found[path]["data"] for path in new_defs]})

    scripts_ok = {}
    for key in RUNTIME_SCRIPTS + ("reportScript",):
        entry = files.get(key) or {}
        path = by_guid(ctx, entry.get("guid"))
        scripts_ok[key] = path is not None and _sha(ctx, path) == entry.get("sha")
    tracked = {by_guid(ctx, (files.get(key) or {}).get("guid")) for key in RUNTIME_SCRIPTS + ("reportScript",)}
    added = sorted(path for path in set(files_under(ctx, "Assets", ".cs")) - set(c0.get("scripts") or [])
                   if path not in tracked)
    card.check("A4", all(scripts_ok.values()) and not added, actual={"scripts": scripts_ok, "added": added})

    carriers = holders(ctx, combat)
    card.check("A5", len(carriers) == 1 and V.same_id(carriers[0], h["fighter"]) and
               p.component_count(h["fighter"], combat) == 1,
               expected={"holder": h["fighterPath"]}, actual={"holders": carriers})

    diffs = []
    for ref, state in c0.get("arena") or []:
        now, before = V.go_state(p, ref), dict(state or {})
        if V.same_id(ref, h["fighter"]):
            before.pop("components", None)
            now = dict(now or {})
            now.pop("components", None)
        ok, diff = V.same(before, now)
        if not ok:
            diffs.append({"ref": ref, "diff": diff})
    card.check("A6", not diffs, actual={"diffs": diffs[:5]})

    bad = ctx.violations()
    card.check("A7", not bad, actual={"violations": bad[:10]})
    scripts_root = h["scripts"] + "/"
    direct = [rec for rec in ctx.direct_writes()
              if not ALLOWED_DIRECT.search(str(rec.get("assetPath") or "")) and
              not (str(rec.get("assetPath") or "").startswith(scripts_root) and
                   os.path.isdir(_disk(ctx, str(rec.get("assetPath")))))]
    card.check("A8", not direct, actual={"directWrites": direct[:10]})

    told_def = V.norm_path(V.answer(ctx, "editorAsmdefPath"))
    def_ok = told_def == new_defs[0] if len(new_defs) == 1 else told_def == ""
    told_script = V.norm_path(V.answer(ctx, "editorScriptPath"))
    if report_now:
        script_ok = told_script == report_now
    else:
        script_ok = told_script.startswith(h["scripts"] + "/") and ".." not in told_script and \
            told_script.endswith(".cs") and _sha(ctx, told_script) == (files.get("reportScript") or {}).get("sha")
    carrier = carriers[0] if len(carriers) == 1 else h["fighter"]
    card.check("A9", def_ok and script_ok and V.norm_path(V.answer(ctx, "fighterPath")) == (p.go(carrier) or {}).get(
        "path") and isinstance(V.answer(ctx, "declaredComplete"), bool), actual=ctx.answer)


def SCENARIO(ctx):
    """Play Mode: two frames, then the combat component's serialized values on the object that carries it (the
    given object when none or several do). With compile errors Unity refuses Play Mode, so the scenario is then a
    single edit-mode check that fails at once."""
    h = ctx.handles
    check = {"kind": "assert", "skill": "editor_get_state", "select": "isPlaying", "expected": True}
    if not compiled(ctx):
        return {"executionMode": "edit", "timeoutSeconds": 30, "steps": [check]}
    combat = class_name(h["combatScript"])
    carriers = holders(ctx, combat)
    carrier = carriers[0] if len(carriers) == 1 else h["fighter"]
    return {"executionMode": "play", "timeoutSeconds": 90, "steps": [
        {"kind": "wait_frames", "frames": 2},
        {"kind": "read", "skill": "component_get_serialized_properties",
         "args": dict({"componentType": combat}, **V.address(carrier))},
        check]}


def verify_runtime(ctx, card):
    scenario = ctx.scenario or {}
    values = spec(ctx.seed)
    read = next((step for step in scenario.get("steps") or [] if step.get("kind") == "read"), None)
    rows = ((read or {}).get("actual") or {}).get("properties") if isinstance((read or {}).get("actual"), dict) \
        else None
    power = next((row.get("value") for row in rows or [] if isinstance(row, dict) and row.get("propertyPath") ==
                  "power"), None)
    want = values["hp"] * values["factor"]
    card.check("A10", scenario.get("passed") is True and V.as_int(power) == want, expected=want,
               actual={"passed": scenario.get("passed"), "error": scenario.get("error"),
                       "status": scenario.get("status"), "power": power})

# Producer:Betsy
