"""DB2 private verification and Play-mode scenario (Health MonoBehaviour with TakeDamage, Heal, and death event)."""
import json
import os
import random
import re

from usk_eval import verifylib as V

MAX_HEALTHS = (80, 120, 150)
SPEC_SALT = 0xDB02
SCRIPT_RE = re.compile(r"\.cs(\.meta)?$")

ASSERTIONS = [
    ("H1", 1.5, False, ["R1"], "script exists at the requested path, declares the MonoBehaviour class with serialized maxHealth (default 100), currentHealth, deathCount, onDied, and public TakeDamage(int)/Heal(int)"),
    ("H2", 0.75, False, ["R1"], "script and project compile without errors"),
    ("H3", 1.5, False, ["R5"], "exactly one Health component is attached to the target unit and its serialized maxHealth matches the requested value"),
    ("H4", 0.75, True, ["R6"], "no other C# file was added under Assets"),
    ("H5", 0.75, True, ["R6"], "the other unit and every existing object keep their initial transform, active state, hierarchy, and components"),
    ("H6", 3.25, False, ["R2", "R3", "R4", "R7"], "in Play Mode, initial state and the TakeDamage/Heal invocation sequence produce exact return values, currentHealth, and deathCount with no error logged"),
    ("H7", 0.5, False, ["R8", "G8"], "answer reports scriptPath, unitInstanceId, and maxHealth matching the final state"),
    ("H8", 0.5, True, ["G1", "G2"], "no mutation outside R/A and no other scene saved"),
    ("H9", 0.5, False, ["G4"], "changes made via UnitySkills rather than direct non-script file edits"),
]

HINTS = {
    "H1": {
        "zh-CN": "脚本路径、类名、基类 MonoBehaviour、序列化字段 maxHealth（默认值 100）/currentHealth/deathCount/onDied 或公共方法 TakeDamage(int)/Heal(int) 不符合 R1–R4。",
        "en": "Script path, class name, base class MonoBehaviour, serialized fields maxHealth (default 100)/currentHealth/deathCount/onDied, or public methods TakeDamage(int)/Heal(int) do not match R1–R4.",
    },
    "H2": {
        "zh-CN": "脚本仍在编译或存在编译错误。",
        "en": "Scripts are still compiling or have compile errors.",
    },
    "H3": {
        "zh-CN": "目标单位上未挂载恰好一个该组件，或其场景序列化字段 maxHealth 不等于要求的值（不能只依赖代码默认值）。",
        "en": "The target unit does not carry exactly one instance of the component, or its scene-serialized maxHealth does not equal the requested value.",
    },
    "H4": {
        "zh-CN": "Assets 下新增了要求之外的 .cs 脚本文件。",
        "en": "Additional .cs script files were created under Assets beyond the single requested script.",
    },
    "H5": {
        "zh-CN": "组件被挂到了其他对象上，或场景中的其他对象被修改。",
        "en": "The component was attached to another object, or other objects in the scene were modified.",
    },
    "H6": {
        "zh-CN": "Play 模式下初始生命值/死亡计数，或 TakeDamage/Heal 调用序列的返回值、currentHealth（含下限 0 与上限 maxHealth 夹紧、死后禁止复活）或 deathCount（仅在从存活变为 0 时加 1）不符合要求，或记录了错误日志。",
        "en": "In Play Mode, initial health/deathCount, or return values, currentHealth (clamped to [0, maxHealth], no heal after death), or deathCount (incremented only once on transition from >0 to 0) across the TakeDamage/Heal call sequence did not match the specification, or errors were logged.",
    },
    "H7": {
        "zh-CN": "答案中的 scriptPath、unitInstanceId 或 maxHealth 与实际终态不一致。",
        "en": "The reported scriptPath, unitInstanceId, or maxHealth in the answer does not match the actual final state.",
    },
    "H8": {
        "zh-CN": "存在 R/A 作用域外的改动，或保存了其他场景。",
        "en": "Mutations occurred outside the R/A scope, or another scene was saved.",
    },
    "H9": {
        "zh-CN": "检测到绕过 UnitySkills 直接写入非脚本文件。",
        "en": "Detected direct non-script file writes bypassing UnitySkills.",
    },
}


def spec(seed):
    """Same deterministic function of the seed as in the public task module."""
    rng = random.Random(seed ^ SPEC_SALT)
    return {"maxHealth": rng.choice(MAX_HEALTHS), "defaultMaxHealth": 100}


def call_plan(max_health):
    """Deterministic 9-call sequence of (method, arg, expected_health, expected_deaths) for a given max_health M."""
    d1 = 30
    h1 = 15
    d2 = max_health // 2
    after_d2 = max_health - d2
    return [
        ("TakeDamage", d1, max_health - d1, 0),
        ("TakeDamage", 0, max_health - d1, 0),
        ("TakeDamage", -15, max_health - d1, 0),
        ("Heal", h1, max_health - d1 + h1, 0),
        ("Heal", max_health, max_health, 0),
        ("TakeDamage", d2, after_d2, 0),
        ("TakeDamage", after_d2 + 25, 0, 1),
        ("TakeDamage", 20, 0, 1),
        ("Heal", 30, 0, 1),
    ]


def _cs_files(project_path):
    out = []
    for base, dirs, files in os.walk(os.path.join(project_path, "Assets")):
        dirs.sort()
        for name in sorted(files):
            if name.endswith(".cs"):
                rel = os.path.relpath(os.path.join(base, name), project_path).replace(os.sep, "/")
                out.append(rel)
    return sorted(out)


def _strip_comments(source):
    text = re.sub(r"/\*.*?\*/", "", source, flags=re.S)
    return re.sub(r"//[^\n]*", "", text)


def _read(ctx, skill, **args):
    """Result of a read-only skill, or None when it answers with a business error."""
    envelope = ctx.probe.c.read_raw(skill, **args)
    if not isinstance(envelope, dict) or envelope.get("status") in ("error", "partial") or envelope.get("errorCode"):
        return None
    result = envelope.get("result", envelope)
    return result if isinstance(result, dict) and result.get("success") is not False else None


def _check_script(ctx, path, cls, default_max):
    res = _read(ctx, "script_read", scriptPath=path)
    content = res.get("content") if isinstance(res, dict) else None
    if not isinstance(content, str):
        return False, None
    code = _strip_comments(content)
    if re.search(r"^\s*namespace\s+[\w.]+", code, flags=re.M):
        return False, {"reason": "declared inside a namespace"}
    class_ok = bool(re.search(r"\bpublic\s+class\s+%s\s*:\s*(?:UnityEngine\.)?MonoBehaviour\b" % re.escape(cls), code))
    max_match = re.search(
        r"(?:\[SerializeField\]\s*(?:private|protected|public)?\s*|public\s+)int\s+maxHealth\s*"
        r"(?:=\s*([-+]?\d+(?:\.\d+)?)f?)?\s*;",
        code,
    )
    max_ok = bool(max_match and max_match.group(1) is not None and V.as_int(max_match.group(1)) == default_max)
    cur_ok = bool(
        re.search(r"(?:\[SerializeField\]\s*(?:private|protected|public)?\s*|public\s+)int\s+currentHealth\b", code)
    )
    death_ok = bool(
        re.search(r"(?:\[SerializeField\]\s*(?:private|protected|public)?\s*|public\s+)int\s+deathCount\b", code)
    )
    event_ok = bool(
        re.search(
            r"(?:\[SerializeField\]\s*(?:private|protected|public)?\s*|public\s+)"
            r"(?:UnityEngine\.Events\.)?UnityEvent\s+onDied\b",
            code,
        )
    )
    td_ok = bool(re.search(r"\bpublic\s+int\s+TakeDamage\s*\(\s*int\s+\w+\s*\)", code))
    heal_ok = bool(re.search(r"\bpublic\s+int\s+Heal\s*\(\s*int\s+\w+\s*\)", code))
    ok = class_ok and max_ok and cur_ok and death_ok and event_ok and td_ok and heal_ok
    return ok, {
        "classOk": class_ok,
        "maxHealthDefaultOk": max_ok,
        "currentHealthFieldOk": cur_ok,
        "deathCountFieldOk": death_ok,
        "onDiedFieldOk": event_ok,
        "takeDamageSigOk": td_ok,
        "healSigOk": heal_ok,
    }


def _compiled_cleanly(ctx, script_path):
    comp = _read(ctx, "debug_check_compilation") or {}
    last = comp.get("lastCompilation") or {}
    fb = _read(ctx, "script_get_compile_feedback", scriptPath=script_path) or {}
    ok = (
        not comp.get("isCompiling")
        and last.get("succeeded") is not False
        and not last.get("errorCount")
        and fb.get("hasErrors") is False
    )
    return ok, {
        "isCompiling": comp.get("isCompiling"),
        "succeeded": last.get("succeeded"),
        "errorCount": last.get("errorCount"),
        "scriptHasErrors": fb.get("hasErrors"),
    }


def _holders_of(ctx, cls):
    res = _read(ctx, "gameobject_find", component=cls, limit=500) or {}
    return [V.object_id(obj) for obj in res.get("objects") or [] if V.object_id(obj) is not None]


def _other_objects_unchanged(p, c0_states, unit_id, cls):
    for iid, before in c0_states:
        now = V.go_state(p, iid)
        if now is None or before is None:
            return False, "%s missing" % iid
        if V.same_id(iid, unit_id):
            before_cmp = dict(before, components=[c for c in before.get("components") or [] if c != cls])
            now_cmp = dict(now, components=[c for c in now.get("components") or [] if c != cls])
            ok, diff = V.same(before_cmp, now_cmp, path="unit")
        else:
            ok, diff = V.same(before, now, path=str(iid))
        if not ok:
            return False, diff
    return True, None


def SCENARIO(ctx):
    h = ctx.handles
    cls = h["className"]
    s = spec(ctx.seed)
    target = V.address(h["unit"])
    read_args = dict(target, componentType=cls)
    steps = [{"kind": "read", "skill": "component_get_serialized_properties", "args": read_args}]
    for method, amount, _exp_hp, _exp_deaths in call_plan(s["maxHealth"]):
        inv_args = dict(
            target,
            componentType=cls,
            methodName=method,
            parameterTypes=json.dumps(["System.Int32"]),
            arguments=json.dumps([amount]),
        )
        steps.append({"kind": "invoke", "skill": "component_invoke_method", "args": inv_args})
        steps.append({"kind": "read", "skill": "component_get_serialized_properties", "args": read_args})
    steps.append({"kind": "assert", "skill": "editor_get_state", "select": "isPlaying", "expected": True})
    return {"executionMode": "play", "timeoutSeconds": 60, "steps": steps}


def _ser_map(step):
    actual = (step or {}).get("actual")
    if not isinstance(actual, dict):
        return {}
    return {p.get("propertyPath"): p.get("value") for p in actual.get("properties") or [] if isinstance(p, dict)}


def verify(ctx, card):
    p, h, c0 = ctx.probe, ctx.handles, ctx.c0
    s = spec(ctx.seed)
    cls = h["className"]

    # H1: script shape + default
    h1_ok, h1_info = _check_script(ctx, h["script"], cls, s["defaultMaxHealth"])
    card.check(
        "H1",
        h1_ok,
        expected={"path": h["script"], "class": cls, "defaultMaxHealth": s["defaultMaxHealth"]},
        actual=h1_info,
    )

    # H2: compiled cleanly
    h2_ok, h2_info = _compiled_cleanly(ctx, h["script"])
    card.check("H2", h2_ok, expected="compiled with 0 errors", actual=h2_info)

    # H3: attached once on unit, maxHealth set in scene
    holders = _holders_of(ctx, cls) if h2_ok else []
    count_on_unit = p.component_count(h["unit"], cls)
    ser = (p.ser(h["unit"], cls) or {}) if count_on_unit >= 1 else {}
    stored_max = V.as_int(ser.get("maxHealth"))
    only_on_unit = len(holders) == 1 and V.same_id(holders[0], h["unit"]) and count_on_unit == 1
    max_ok = stored_max == s["maxHealth"]
    card.check(
        "H3",
        only_on_unit and max_ok,
        expected={"holder": h["unit"], "count": 1, "maxHealth": s["maxHealth"]},
        actual={"holders": holders, "countOnUnit": count_on_unit, "maxHealth": stored_max},
    )

    # H4: no extra C# scripts
    before_cs = set(c0.get("scripts") or [])
    now_cs = set(_cs_files(ctx.project_path))
    extra_cs = sorted(now_cs - before_cs - {h["script"]})
    card.check("H4", not extra_cs, expected=[], actual=extra_cs)

    # H5: other objects unchanged and no component on other objects
    other_holders = [x for x in holders if not V.same_id(x, h["unit"])]
    unchanged_ok, diff = _other_objects_unchanged(p, c0.get("states") or [], h["unit"], cls)
    card.check(
        "H5",
        not other_holders and unchanged_ok,
        expected="unchanged",
        actual={"otherHolders": other_holders, "diff": diff},
    )

    # H7: answer matches actual holder and state
    ans_path = V.norm_path(V.answer(ctx, "scriptPath"))
    ans_iid = V.answer(ctx, "unitInstanceId")
    ans_max = V.as_int(V.answer(ctx, "maxHealth"))
    actual_holder = holders[0] if len(holders) == 1 else None
    actual_ser = (p.ser(actual_holder, cls) or {}) if actual_holder is not None else {}
    actual_max = V.as_int(actual_ser.get("maxHealth"))
    ans_ok = (
        not ctx.answer_schema_errors
        and ans_path == h["script"]
        and actual_holder is not None
        and V.same_id(ans_iid, actual_holder)
        and actual_max is not None
        and ans_max == actual_max
        and isinstance(V.answer(ctx, "declaredComplete"), bool)
    )
    card.check(
        "H7",
        ans_ok,
        expected={"scriptPath": h["script"], "unitInstanceId": actual_holder, "maxHealth": actual_max},
        actual={
            "scriptPath": ans_path,
            "unitInstanceId": ans_iid,
            "maxHealth": ans_max,
            "schemaErrors": ctx.answer_schema_errors,
        },
    )

    # H8: G1/G2
    viol = ctx.violations()
    other_scenes = ctx.saved_other_scenes()
    card.check("H8", not viol and not other_scenes, expected=[], actual={"violations": viol[:10], "otherScenes": other_scenes})

    # H9: G4
    dw = [rec for rec in ctx.direct_writes() if not SCRIPT_RE.search(str(rec.get("assetPath") or ""))]
    card.check("H9", not dw, expected=[], actual={"directWrites": dw[:10]})


def verify_runtime(ctx, card):
    h = ctx.handles
    s = spec(ctx.seed)
    cls = h["className"]
    scen = ctx.scenario or {}
    steps = scen.get("steps") or []
    plan = call_plan(s["maxHealth"])
    expected_read_count = 1 + len(plan)
    reads = [st for st in steps if st.get("kind") == "read"]
    invokes = [st for st in steps if st.get("kind") == "invoke"]
    count_on_unit = ctx.probe.component_count(h["unit"], cls)
    if not scen.get("passed") or count_on_unit != 1 or len(reads) != expected_read_count or len(invokes) != len(plan):
        card.check(
            "H6",
            False,
            expected={"passed": True, "countOnUnit": 1, "reads": expected_read_count, "invokes": len(plan)},
            actual={
                "passed": scen.get("passed"),
                "error": scen.get("error"),
                "countOnUnit": count_on_unit,
                "reads": len(reads),
                "invokes": len(invokes),
            },
        )
        return

    checks = []
    init_ser = _ser_map(reads[0])
    init_hp = V.as_int(init_ser.get("currentHealth"))
    init_deaths = V.as_int(init_ser.get("deathCount"))
    init_ok = init_hp == s["maxHealth"] and init_deaths == 0
    checks.append(("init", init_ok, {"currentHealth": init_hp, "deathCount": init_deaths}))

    for idx, (method, amount, exp_hp, exp_deaths) in enumerate(plan):
        inv_actual = (invokes[idx] or {}).get("actual") or {}
        ret_val = V.as_int(inv_actual.get("returnValue")) if isinstance(inv_actual, dict) else None
        post_ser = _ser_map(reads[idx + 1])
        post_hp = V.as_int(post_ser.get("currentHealth"))
        post_deaths = V.as_int(post_ser.get("deathCount"))
        step_ok = ret_val == exp_hp and post_hp == exp_hp and post_deaths == exp_deaths
        checks.append(
            (
                "%d:%s(%d)" % (idx + 1, method, amount),
                step_ok,
                {
                    "expected": {"return": exp_hp, "currentHealth": exp_hp, "deathCount": exp_deaths},
                    "actual": {"return": ret_val, "currentHealth": post_hp, "deathCount": post_deaths},
                },
            )
        )

    passed_count = sum(1 for _, ok, _ in checks if ok)
    total_count = len(checks)
    card.fraction(
        "H6",
        float(passed_count) / float(total_count),
        expected={"passedChecks": total_count},
        actual={"passedChecks": passed_count, "details": checks},
    )

# Producer:Betsy
