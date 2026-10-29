"""DC8 private verifier: a difficulty ScriptableObject type, three assets and a spawner switched between them at runtime.

What the task probes: the spawner must run on the asset its `config` field references, so numbers hard-coded or cached
once at Start fail when SetConfig switches the asset; `config` must hold the asset object itself after every switch, so
a config cloned at runtime (Instantiate / CreateInstance) shows up as a different object in the Play Mode reads; values
copied into the current asset instead of switching the reference leave `config` on the old asset and corrupt that
asset, which the Play Mode asset reads catch; maxAlive counts the container's children (a per-config spawn counter
keeps spawning after the switch to the low-cap asset), the wave at the cap is partial (a full wave overshoots); the
asset values must be set (the code defaults of the reference script differ from all three); the scene must reference
the middle asset.

The runtime part is timed by events, as DS3 and DB6 are (fixed sampling drifts with the per-step editor tick): phase
(a) waits for each wave of the scene asset and stamps it; (b) switches to the low-cap asset and reads the container
before and after a hold; (c) switches back and reads `config` plus the three assets; (d) switches to the high-cap
asset and waits for its waves, stamping the switch and each wave. (d) comes last because a spawner that ignores the
switch never fills its waits: the timeout then only costs (d). Verdicts read the scenario's step records by index,
rebuilding the step plan from the seed and handles.

The numbers are recomputed from the seed exactly as the public module does (difficulty_spec is copied verbatim).
"""
import hashlib
import json
import math
import os
import random

from usk_eval import verifylib as V

VALUE_TOL = 1e-3
TIME_TOL = 0.3           # R6: a later gap (and (d)'s first) may read this much short (frame and read granularity)
LATE_TOL = 0.5           # R6: or this much long: a frame over Time.maximumDeltaTime loses game time
EARLY_SLACK = 1.0        # R6: (a)'s first wave may read this much early (Play starts before the first stamp)
LATE_SLACK = 0.8         # R6: or this much late (the first Play frames are slow and deltaTime is capped)
HOLD = 1.5               # R6: the holds, in spawnInterval of the active asset
AT_LEAST = 1000          # a wait matches |childCount - (k + AT_LEAST)| <= AT_LEAST, i.e. at least k children
LOG_FAILURES = ("error", "exception", "assert")
FIELDS = ("spawnInterval", "spawnsPerWave", "maxAlive")
LEVELS = (("easy", "assetEasy"), ("normal", "assetNormal"), ("hard", "assetHard"))

ASSERTIONS = [
    ("C1", 0.75, False, ["R1"], "both scripts exist at A/Scripts/<Class>.cs, compiled without errors and both classes "
                                "are loaded"),
    ("C2", 0.25, False, ["R2"], "the three config assets exist at their paths with the config type"),
    ("C3", 0.5, False, ["R2"], "the assets carry the given spawnInterval, spawnsPerWave and maxAlive"),
    ("C4", 1.0, False, ["R3"], "the Spawner object carries one spawner component referencing the middle asset, the "
                               "prefab and the container's Transform; no other object carries one"),
    ("C5", 0.5, True, ["R7"], "no code file other than the two scripts was added under Assets"),
    ("C6", 0.5, False, ["R8"], "the prefab, the Spawner, the container and the arena are unchanged"),
    ("C7", 0.5, True, ["G1", "G2"], "no change outside R/A, no other scene saved or opened"),
    ("C8", 0.25, False, ["G4"], "no file other than C# scripts was written under A around the skills"),
    ("C9", 0.5, False, ["R9", "G8"], "the answer reports the scripts, the assets and the Spawner object"),
    ("C10", 1.25, False, ["R4", "R6"], "phase (a): the container starts empty, each wave of the scene asset adds the "
                                       "right count one interval after the previous (the first one interval in), the "
                                       "last one is partial at maxAlive and the count then holds (fraction)"),
    ("C11", 0.75, False, ["R4", "R5", "R6"], "phase (b): after the switch to the low-cap asset the child count does not "
                                             "change"),
    ("C12", 1.0, False, ["R5", "R6"], "every Play Mode read of `config` names the active asset itself, and the read in "
                                      "(c) is the object of the first read"),
    ("C13", 0.75, False, ["R5", "R6"], "in Play Mode, after two switches, the three assets still hold their values"),
    ("C14", 1.25, False, ["R4", "R5", "R6"], "phase (d): the first wave comes one interval after the switch, each wave "
                                             "adds the right count one interval after the previous, the last one is "
                                             "partial at maxAlive and the count then holds (fraction)"),
    ("C15", 0.25, False, ["R6"], "Play Mode ran the scenario and no error or exception was logged"),
]

HINTS = {
    "C1": {"zh-CN": "两个脚本放在 A/Scripts 下，文件名与类名一致，不加 namespace；先写配置类再写生成器，等编译通过再建资产和组件。",
           "en": "Put both scripts under A/Scripts, named after their classes and outside any namespace; write the "
                 "config class first and wait for a clean compile before creating assets and components."},
    "C2": {"zh-CN": "用 scriptableobject_create 按题目给出的路径和类型创建三个资产。",
           "en": "Create the three assets with scriptableobject_create at the given paths and type."},
    "C3": {"zh-CN": "三个资产的 spawnInterval、spawnsPerWave、maxAlive 都要按题目设置，不能停留在代码默认值。",
           "en": "Set spawnInterval, spawnsPerWave and maxAlive on every asset; code defaults do not count."},
    "C4": {"zh-CN": "生成器对象上只挂一个组件：config 指向题目指定的资产，prefab 指向预制体，container 指向容器的 Transform。",
           "en": "One component on the Spawner object: config at the named asset, prefab at the prefab, container at "
                 "the container's Transform."},
    "C5": {"zh-CN": "除这两个脚本外不要写其他代码文件（包括编辑器脚本）。",
           "en": "Write no other code file (editor scripts included)."},
    "C6": {"zh-CN": "不要改预制体、生成器对象、容器和它们的父物体。",
           "en": "Leave the prefab, the Spawner object, the container and their parent alone."},
    "C7": {"zh-CN": "所有修改都必须在 R 与 A 之内，且只能保存本轮场景。",
           "en": "Keep every change inside R and A, and save no scene but this run's."},
    "C8": {"zh-CN": "除 C# 脚本外，不要绕过技能直接在 A 下写文件。",
           "en": "Apart from C# scripts, do not write files under A directly; use the skills."},
    "C9": {"zh-CN": "答案里报告两个脚本、三个资产和生成器对象的实际路径，并写明 declaredComplete。",
           "en": "Report the actual paths of both scripts, the three assets and the Spawner object, and "
                 "declaredComplete."},
    "C10": {"zh-CN": "每隔 spawnInterval 发生一波（第一波在 spawnInterval 之后），每波最多 spawnsPerWave 个，container 子物体数不超过 maxAlive，最后一波只补到上限。",
            "en": "One wave every spawnInterval (the first after one interval), up to spawnsPerWave each, never more "
                  "than maxAlive children in the container: the last wave only fills up to the cap."},
    "C11": {"zh-CN": "maxAlive 统计 container 的全部直接子物体，不是切换后生成的数量；子物体数已达上限时不生成。",
            "en": "maxAlive counts all direct children of the container, not what was spawned since the switch; at "
                  "the cap nothing spawns."},
    "C12": {"zh-CN": "config 字段要直接引用资产本身：不要在运行时 Instantiate 或 CreateInstance 配置，也不要把数值拷进旧资产。",
            "en": "Keep the asset itself in `config`: do not Instantiate or CreateInstance a config at runtime, and do "
                  "not copy values into the previous asset."},
    "C13": {"zh-CN": "运行时不要给配置资产的字段赋值：编辑器里 Play 模式对资产的修改不会自动还原。",
            "en": "Never assign a config asset's fields at runtime: Play Mode changes to an asset are not reverted in "
                  "the editor."},
    "C14": {"zh-CN": "SetConfig 要立即改用新资产的数值并把计时清零；不要只在 Start 里读一次数值。",
            "en": "SetConfig must switch to the new asset's numbers at once and restart the timer; reading the numbers "
                  "once in Start is not enough."},
    "C15": {"zh-CN": "项目要能编译并进入 Play 模式，运行时不能有错误或异常日志。",
            "en": "The project must compile and enter Play Mode, with no error or exception logged at runtime."},
}


def difficulty_spec(seed):
    """spawnInterval, spawnsPerWave and maxAlive of the three assets, from the seed alone."""
    rng = random.Random("DC8:%d" % seed)
    easy = {"spawnInterval": rng.choice((1.6, 1.8, 2.0)), "spawnsPerWave": 1, "maxAlive": rng.choice((2, 3))}
    normal = {"spawnInterval": rng.choice((1.2, 1.3, 1.4)), "spawnsPerWave": 2, "maxAlive": rng.choice((5, 7))}
    hard = {"spawnInterval": rng.choice((0.8, 0.9, 1.0)), "spawnsPerWave": 3,
            "maxAlive": normal["maxAlive"] + 3 * rng.choice((1, 2)) + 2}
    return {"easy": easy, "normal": normal, "hard": hard}


def wave_counts(start, values):
    """The container's child count after each wave of `values` from `start` children until it reaches maxAlive."""
    out, count = [], start
    while count < values["maxAlive"] and values["spawnsPerWave"] > 0:
        count = min(values["maxAlive"], count + values["spawnsPerWave"])
        out.append(count)
    return out


def gap_ok(gap, interval, first_slack=None):
    """Whether a gap between stamps fits R6: (a)'s first wave within EARLY_SLACK/LATE_SLACK, every other one within
    TIME_TOL short / LATE_TOL long."""
    if gap is None:
        return False
    low, high = (interval - first_slack[0], interval + first_slack[1]) if first_slack else (interval - TIME_TOL,
                                                                                            interval + LATE_TOL)
    return low - 1e-9 <= gap <= high + 1e-9


def logged_failures(node):
    """Whether the scenario's log window holds an error, exception or assert entry, as RuntimeScenarioService counts
    them (any type/logType/severity field at any depth)."""
    if isinstance(node, dict):
        return any(str(node.get(key) or "").lower() in LOG_FAILURES for key in ("type", "logType", "severity")) or \
            any(logged_failures(value) for value in node.values())
    if isinstance(node, list):
        return any(logged_failures(value) for value in node)
    return False


# ---------------------------------------------------------------- reading


def class_names(handles):
    def name(path):
        return os.path.basename(path)[:-len(".cs")]
    return name(handles["configScript"]), name(handles["spawnerScript"])


def asset_name(path):
    return os.path.splitext(os.path.basename(path))[0]


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


def _read(ctx, skill, **args):
    """Result of a read-only skill, or None when it answers with a business error."""
    envelope = ctx.probe.c.read_raw(skill, **args)
    if not isinstance(envelope, dict) or envelope.get("status") in ("error", "partial") or envelope.get("errorCode"):
        return None
    result = envelope.get("result", envelope)
    return result if isinstance(result, dict) and result.get("success") is not False else None


def holders(ctx):
    """Ids of the GameObjects carrying the spawner class (empty when it is not loaded)."""
    _cfg, spawner = class_names(ctx.handles)
    found = _read(ctx, "gameobject_find", component=spawner, limit=500) or {}
    return [V.object_id(obj) for obj in found.get("objects") or []]


def property_values(info):
    """{propertyPath: value} of a serialized-properties read."""
    return {row.get("propertyPath"): row.get("value") for row in (info or {}).get("properties") or []
            if isinstance(row, dict)}


def values_match(props, want):
    return V.as_int(props.get("spawnsPerWave")) == want["spawnsPerWave"] and \
        V.as_int(props.get("maxAlive")) == want["maxAlive"] and \
        V.close_tuple(props.get("spawnInterval"), (want["spawnInterval"],), VALUE_TOL)


def _wiring(ctx):
    """(ok, actual) of the Spawner object's component references."""
    p, h = ctx.probe, ctx.handles
    cfg_class, spawner_class = class_names(h)
    ser = p.ser(h["spawner"], spawner_class) or {}
    config, prefab = V.parse_ref(ser.get("config")), V.parse_ref(ser.get("prefab"))
    container = V.parse_ref(ser.get("container"))
    ok = (p.component_count(h["spawner"], spawner_class) == 1 and
          bool(config) and config[1] == cfg_class and config[2] == asset_name(h["assetNormal"]) and
          bool(prefab) and prefab[1] == "UnityEngine.GameObject" and prefab[2] == asset_name(h["prefab"]) and
          bool(container) and container[1] == "UnityEngine.Transform" and
          V.same_id(p.owner(ser.get("container")), h["container"]))
    return ok, {key: ser.get(key) for key in ("config", "prefab", "container")}


def verify(ctx, card):
    p, h, c0 = ctx.probe, ctx.handles, ctx.c0
    cfg_class, _spawner_class = class_names(h)
    spec = difficulty_spec(ctx.seed)
    paths = [h["configScript"], h["spawnerScript"]]
    present = [os.path.isfile(os.path.join(ctx.project_path, *path.split("/"))) for path in paths]
    comp = _read(ctx, "debug_check_compilation") or {}
    last = comp.get("lastCompilation") or {}
    feedback = [(_read(ctx, "script_get_compile_feedback", scriptPath=path) or {}).get("hasErrors") for path in paths]
    found = holders(ctx)
    assets = {key: p.asset(h[key]) or {} for _level, key in LEVELS}
    loaded = bool(found) and any(info.get("type") == cfg_class for info in assets.values())
    card.check("C1", all(present) and not comp.get("isCompiling") and last.get("succeeded") is not False and
               not last.get("errorCount") and feedback == [False, False] and loaded,
               actual={"present": present, "isCompiling": comp.get("isCompiling"), "hasErrors": feedback,
                       "last": {k: last.get(k) for k in ("succeeded", "errorCount")}, "holders": found,
                       "assetTypes": {key: info.get("type") for key, info in assets.items()}})

    card.check("C2", all(info.get("type") == cfg_class for info in assets.values()), expected=cfg_class,
               actual={key: info.get("type") for key, info in assets.items()})
    values, good = {}, True
    for level, key in LEVELS:
        props = property_values(p.so(h[key])) if assets[key] else {}
        values[key] = {name: props.get(name) for name in FIELDS}
        good = good and values_match(props, spec[level])
    card.check("C3", good, expected=spec, actual=values)

    ok, wiring = _wiring(ctx)
    strays = [ref for ref in found if not V.same_id(ref, h["spawner"])]
    wiring["otherHolders"] = strays[:5]
    card.check("C4", ok and not strays and len(found) == 1, actual=wiring)

    added = sorted(set(cs_files(ctx.project_path)) - set(c0.get("scripts") or []))
    card.check("C5", set(added) <= set(paths), expected=sorted(paths), actual=added)

    diffs = {}
    for key, state in (c0.get("objects") or {}).items():
        before, now = dict(state or {}), V.go_state(p, h[key]) or {}
        for item in (before, now):
            item.pop("components", None)
        same, diff = V.same(before, now)
        if not same:
            diffs[key] = diff
    same, diff = V.same(c0.get("arena"), V.go_state(p, h["arena"]))
    if not same:
        diffs["arena"] = diff
    prefab_sha = _sha(ctx, h["prefab"])
    if prefab_sha != (c0.get("prefab") or {}).get("sha"):
        diffs["prefab"] = {"sha": prefab_sha}
    card.check("C6", not diffs, actual=diffs)

    bad = ctx.violations()
    card.check("C7", not bad, actual={"violations": bad[:10]})
    direct = [rec for rec in ctx.direct_writes() if not str(rec.get("assetPath") or "").endswith((".cs", ".cs.meta"))]
    card.check("C8", not direct, actual={"directWrites": direct[:10]})

    def listed(key):
        value = V.answer(ctx, key)
        return sorted(V.norm_path(item) for item in value if isinstance(item, str)) if isinstance(value, list) else None

    spawner_path = (p.go(h["spawner"]) or {}).get("path")
    reported = V.answer(ctx, "spawnerPath")
    config_paths = sorted(h[key] for _level, key in LEVELS)
    card.check("C9", listed("scriptPaths") == sorted(paths) and listed("configAssetPaths") == config_paths and
               isinstance(reported, str) and V.norm_path(reported) == spawner_path and
               isinstance(V.answer(ctx, "declaredComplete"), bool),
               expected={"scriptPaths": sorted(paths), "configAssetPaths": config_paths, "spawnerPath": spawner_path},
               actual=ctx.answer)


# ---------------------------------------------------------------- Play Mode scenario


def plan(ctx):
    """[(label, step)] of the scenario, in order; verify_runtime finds each step's record by its index."""
    h, spec = ctx.handles, difficulty_spec(ctx.seed)
    cfg_class, spawner_class = class_names(h)
    container, spawner = V.address(h["container"]), V.address(h["spawner"])
    stamp = {"kind": "read", "skill": "editor_get_state", "args": {}}
    box = {"kind": "read", "skill": "gameobject_get_info", "args": container}
    config = {"kind": "read", "skill": "component_get_serialized_properties",
              "args": dict({"componentType": spawner_class}, **spawner)}

    def call(path):
        return {"kind": "invoke", "skill": "component_invoke_method",
                "args": dict({"componentType": spawner_class, "methodName": "SetConfig",
                              "parameterTypes": json.dumps([cfg_class]),
                              "arguments": json.dumps([{"assetPath": path}])}, **spawner)}

    def waves(phase, start, values):
        out = []
        for k, count in enumerate(wave_counts(start, values), 1):
            out += [((phase, "wave", k), {"kind": "wait_condition", "skill": "gameobject_get_info", "args": container,
                                          "select": "childCount", "expected": count + AT_LEAST,
                                          "tolerance": AT_LEAST}),
                    ((phase, "stamp", k), stamp)]
        out += [((phase, "settle"), {"kind": "wait_seconds", "seconds": round(HOLD * values["spawnInterval"], 3)}),
                ((phase, "hold"), box)]
        return out

    normal, easy, hard = spec["normal"], spec["easy"], spec["hard"]
    steps = [(("a", "t0"), stamp), (("a", "start"), box), (("id", "first"), config)]
    steps += waves("a", 0, normal)
    steps += [(("b", "call"), call(h["assetEasy"])), (("b", "before"), box), (("id", "easy"), config),
              (("b", "settle"), {"kind": "wait_seconds", "seconds": round(HOLD * easy["spawnInterval"], 3)}),
              (("b", "after"), box)]
    steps += [(("c", "call"), call(h["assetNormal"])), (("id", "again"), config)]
    steps += [(("asset", key), {"kind": "read", "skill": "scriptableobject_get_serialized_properties",
                                "args": {"assetPath": h[key]}}) for _level, key in LEVELS]
    steps += [(("d", "call"), call(h["assetHard"])), (("d", "t0"), stamp), (("id", "hard"), config)]
    steps += waves("d", normal["maxAlive"], hard)
    steps.append((("end",), {"kind": "assert", "skill": "editor_get_state", "select": "isPlaying", "expected": True}))
    return steps


def SCENARIO(ctx):
    """Play Mode: phases (a)-(d) of R6, then one assert that Play is still on."""
    spec = difficulty_spec(ctx.seed)
    normal, easy, hard = spec["normal"], spec["easy"], spec["hard"]
    nominal = (len(wave_counts(0, normal)) + HOLD) * normal["spawnInterval"] + HOLD * easy["spawnInterval"] + \
        (len(wave_counts(normal["maxAlive"], hard)) + HOLD) * hard["spawnInterval"]
    steps = [step for _label, step in plan(ctx)]
    return {"executionMode": "play", "timeoutSeconds": int(math.ceil(nominal + 0.1 * len(steps))) + 60,
            "steps": steps}


def _records(ctx):
    """{label: step record} of the scenario run."""
    rows = {}
    for row in (ctx.scenario or {}).get("steps") or []:
        index = V.as_int(row.get("index")) if isinstance(row, dict) else None
        if index is not None:
            rows[index] = row
    return {label: rows.get(index) for index, (label, _step) in enumerate(plan(ctx))}


def _actual(records, label):
    row = records.get(label)
    return row.get("actual") if isinstance(row, dict) else None


def _count(records, label):
    info = _actual(records, label)
    return V.as_int(info.get("childCount")) if isinstance(info, dict) else None


def _stamp(records, label):
    info = _actual(records, label)
    value = V.floats(info.get("timeSinceStartup")) if isinstance(info, dict) else []
    return value[0] if len(value) == 1 else None


def _seen(records, label):
    """Child count a wait matched with, or None when it did not match."""
    row = records.get(label)
    return V.as_int(row.get("actual")) if isinstance(row, dict) and row.get("passed") is True else None


def phase_waves(records, phase, start, values, first_slack=None):
    """(passed, total, rows) of one phase's waves: each matched count and each gap, then the hold count."""
    passed, total, rows = 0, 0, []
    previous = _stamp(records, (phase, "t0"))
    for k, want in enumerate(wave_counts(start, values), 1):
        seen, stamp = _seen(records, (phase, "wave", k)), _stamp(records, (phase, "stamp", k))
        gap = stamp - previous if stamp is not None and previous is not None else None
        good = gap_ok(gap, values["spawnInterval"], first_slack if k == 1 else None)
        passed, total = passed + (seen == want) + good, total + 2
        rows.append({"wave": k, "count": seen, "want": want, "gap": round(gap, 3) if gap is not None else None,
                     "ok": seen == want and good})
        previous = stamp
    hold = _count(records, (phase, "hold"))
    passed, total = passed + (hold == values["maxAlive"]), total + 1
    rows.append({"hold": hold, "want": values["maxAlive"]})
    return passed, total, rows


def verify_runtime(ctx, card):
    scenario = ctx.scenario or {}
    h, spec = ctx.handles, difficulty_spec(ctx.seed)
    cfg_class, _spawner_class = class_names(h)
    records = _records(ctx)
    status = {"status": scenario.get("status"), "passed": scenario.get("passed"), "error": scenario.get("error")}

    start = _count(records, ("a", "start"))
    passed, total, rows = phase_waves(records, "a", 0, spec["normal"], (EARLY_SLACK, LATE_SLACK))
    fraction = 0.0 if _stamp(records, ("a", "t0")) is None else (passed + (start == 0)) / float(total + 1)
    card.fraction("C10", fraction, expected={"spec": spec["normal"], "timeTol": TIME_TOL, "lateTol": LATE_TOL,
                                             "earlySlack": EARLY_SLACK, "lateSlack": LATE_SLACK, "hold": HOLD},
                  actual=dict(status, start=start, waves=rows))

    before, after = _count(records, ("b", "before")), _count(records, ("b", "after"))
    card.check("C11", before is not None and before == after, actual=dict(status, before=before, after=after))

    reads = {name: V.parse_ref(property_values(_actual(records, ("id", name))).get("config"))
             for name in ("first", "easy", "again", "hard")}
    names = {"first": h["assetNormal"], "easy": h["assetEasy"], "again": h["assetNormal"], "hard": h["assetHard"]}
    named = all(reads[key] and reads[key][1] == cfg_class and reads[key][2] == asset_name(path)
                for key, path in names.items())
    card.check("C12", named and reads["again"][0] == reads["first"][0],
               expected={key: "%s:%s" % (cfg_class, asset_name(path)) for key, path in names.items()},
               actual={key: list(ref) if ref else None for key, ref in reads.items()})

    held = {key: {name: property_values(_actual(records, ("asset", key))).get(name) for name in FIELDS}
            for _level, key in LEVELS}
    card.check("C13", all(values_match(held[key], spec[level]) for level, key in LEVELS), expected=spec,
               actual=held)

    passed, total, rows = phase_waves(records, "d", spec["normal"]["maxAlive"], spec["hard"])
    fraction = 0.0 if _stamp(records, ("d", "t0")) is None else passed / float(total)
    card.fraction("C14", fraction, expected={"spec": spec["hard"], "start": spec["normal"]["maxAlive"],
                                             "timeTol": TIME_TOL, "lateTol": LATE_TOL, "hold": HOLD},
                  actual=dict(status, waves=rows))

    ran = records.get(("a", "start")) is not None
    errors = logged_failures(scenario.get("logWindow"))
    card.check("C15", ran and not errors, actual=dict(status, ran=ran, errorsLogged=errors))

# Producer:Betsy
