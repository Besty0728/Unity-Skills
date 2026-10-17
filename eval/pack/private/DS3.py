"""DS3 private verifier: a WaveConfig ScriptableObject, two assets and two spawners that follow the asset they reference.

What the task probes: the two spawners share one script but reference different assets, so hard-coded numbers or a
config created at runtime (CreateInstance, code defaults) cannot satisfy both; the asset values must be set (the code
defaults differ from both assets); the first spawn comes one interval after Play starts and spawning stops at count;
instances must be direct children of the referenced container; no code beyond the two scripts. The runtime part reads
both containers at moments halfway between spawns (where a +-WINDOW_S slack never spans a spawn) and once after the
last spawn.

The counts and intervals are recomputed from the seed exactly as the public module does (wave_spec is copied verbatim).
"""
import hashlib
import math
import os
import random

from usk_eval import verifylib as V

WINDOW_S = 0.15          # R5: time slack on each side of a sample
VALUE_TOL = 1e-3
SETTLE_S = 1.0           # R5: the final sample this long after the last spawn

ASSERTIONS = [
    ("D1", 1.0, False, ["R1"], "both scripts exist at A/Scripts/<Class>.cs, compiled without errors and both classes "
                               "are loaded"),
    ("D2", 0.5, False, ["R2"], "both config assets exist at their paths with the WaveConfig type"),
    ("D3", 1.0, False, ["R2"], "the assets carry the given count and interval"),
    ("D4", 1.5, False, ["R4"], "each Spawner object carries one spawner component referencing its asset, the prefab "
                               "and its container's Transform; no other object carries one"),
    ("D5", 0.5, True, ["R6"], "no code file other than the two scripts was added under Assets"),
    ("D6", 0.5, False, ["R7"], "the prefab, the containers, the Spawner objects and the arena are unchanged"),
    ("D7", 0.5, True, ["G1", "G2"], "no change outside R/A, no other scene saved or opened"),
    ("D8", 0.25, False, ["G4"], "no file other than C# scripts was written under A around the skills"),
    ("D9", 0.5, False, ["R8", "G8"], "the answer reports the scripts, the assets and the Spawner objects"),
    ("D10", 2.5, False, ["R3", "R5"], "in Play Mode both containers hold the due number of children at every sample "
                                      "(fraction)"),
    ("D11", 1.25, False, ["R3", "R5"], "after the last spawn each container holds exactly count children, all "
                                       "instances of the prefab, and no error was logged"),
]

HINTS = {
    "D1": {"zh-CN": "两个脚本放在 A/Scripts 下，文件名与类名一致；先建 WaveConfig 再建 Spawner，等编译通过再创建资产和组件。",
           "en": "Put both scripts under A/Scripts, named after their classes; write WaveConfig before the spawner "
                 "and wait for a clean compile before creating assets and components."},
    "D2": {"zh-CN": "用 scriptableobject_create 按题目给出的路径和类型创建两个资产。",
           "en": "Create both assets with scriptableobject_create at the given paths and type."},
    "D3": {"zh-CN": "两个资产的 count 和 interval 要按题目设置（scriptableobject_set），不能停留在代码默认值。",
           "en": "Set count and interval on both assets (scriptableobject_set); code defaults do not count."},
    "D4": {"zh-CN": "每个 Spawner 对象挂一个组件：config 指向各自的资产，prefab 指向预制体，container 指向各自容器的 Transform。",
           "en": "One component per Spawner object: config at its own asset, prefab at the prefab, container at its "
                 "own container's Transform."},
    "D5": {"zh-CN": "除这两个脚本外不要写其他代码文件（包括编辑器脚本）。",
           "en": "Write no other code file (editor scripts included)."},
    "D6": {"zh-CN": "不要改预制体、容器、Spawner 对象和 Arena。",
           "en": "Leave the prefab, the containers, the Spawner objects and the arena alone."},
    "D7": {"zh-CN": "所有修改都必须在 R 与 A 之内，且只能保存本轮场景。",
           "en": "Keep every change inside R and A, and save no scene but this run's."},
    "D8": {"zh-CN": "除 C# 脚本外，不要绕过技能直接在 A 下写文件。",
           "en": "Apart from C# scripts, do not write files under A directly; use the skills."},
    "D9": {"zh-CN": "答案里报告两个脚本、两个资产和两个 Spawner 的实际路径，并写明 declaredComplete。",
           "en": "Report the actual paths of both scripts, both assets and both Spawners, and declaredComplete."},
    "D10": {"zh-CN": "每隔 interval 秒生成一个（第一个在 interval 秒后），数值从引用的资产读取；两个 Spawner 行为不同。",
            "en": "Spawn one every interval seconds (the first after interval seconds), reading the numbers from the "
                  "referenced asset; the two spawners behave differently."},
    "D11": {"zh-CN": "达到 count 后停止；实例要用 Instantiate(prefab, container) 放到容器下；运行时不能有错误日志。",
            "en": "Stop at count; parent instances to the container (Instantiate(prefab, container)); no error may "
                  "be logged at runtime."},
}


def wave_spec(seed):
    """count and interval of the two assets, from the seed alone: A spawns few and slowly, B many and quickly."""
    rng = random.Random("DS3:%d" % seed)
    return {"a": {"count": rng.choice((3, 4)), "interval": rng.choice((0.8, 1.0, 1.2))},
            "b": {"count": rng.choice((5, 6, 7)), "interval": rng.choice((0.5, 0.6, 0.7))}}


# ---------------------------------------------------------------- the ideal schedule (R3)


def due(count, interval, t):
    """Instances due t seconds after Play starts: one every interval, the first at interval, at most count."""
    if interval <= 0:
        return count
    return max(0, min(count, int(math.floor(t / float(interval) + 1e-6))))


def sample_times(spec):
    """Moments halfway between spawns of either spawner (and after its last one), then a settle sample."""
    out = set()
    end = 0.0
    for key in ("a", "b"):
        count, interval = spec[key]["count"], spec[key]["interval"]
        for index in range(count + 1):
            out.add(round(interval * (index + 0.5), 3))
        end = max(end, count * interval)
    out.add(round(end + SETTLE_S, 3))
    return sorted(out)


def window_ok(actual, count, interval, t):
    """Whether a child count lies between the numbers due at t - WINDOW_S and t + WINDOW_S."""
    value = V.as_int(actual)
    return value is not None and due(count, interval, max(0.0, t - WINDOW_S)) <= value <= due(count, interval,
                                                                                              t + WINDOW_S)


# ---------------------------------------------------------------- reading


def class_names(handles):
    def name(path):
        return os.path.basename(path)[:-len(".cs")]
    return name(handles["configScript"]), name(handles["spawnerScript"])


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


def _asset_name(path):
    return os.path.splitext(os.path.basename(path))[0]


def _wiring(ctx, spawner_key):
    """(ok, actual) of one Spawner object's component references."""
    p, h = ctx.probe, ctx.handles
    cfg_class, spawner_class = class_names(h)
    suffix = spawner_key[-1]
    ser = p.ser(h[spawner_key], spawner_class) or {}
    config = V.parse_ref(ser.get("config"))
    prefab = V.parse_ref(ser.get("prefab"))
    container = V.parse_ref(ser.get("container"))
    owner = p.owner(ser.get("container"))
    ok = (p.component_count(h[spawner_key], spawner_class) == 1 and
          bool(config) and config[1] == cfg_class and config[2] == _asset_name(h["asset" + suffix]) and
          bool(prefab) and prefab[1] == "UnityEngine.GameObject" and prefab[2] == _asset_name(h["prefab"]) and
          bool(container) and container[1] == "UnityEngine.Transform" and V.same_id(owner, h["container" + suffix]))
    return ok, {key: ser.get(key) for key in ("config", "prefab", "container")}


def verify(ctx, card):
    p, h, c0 = ctx.probe, ctx.handles, ctx.c0
    cfg_class, spawner_class = class_names(h)
    spec = wave_spec(ctx.seed)
    paths = [h["configScript"], h["spawnerScript"]]
    present = [os.path.isfile(os.path.join(ctx.project_path, *path.split("/"))) for path in paths]
    comp = _read(ctx, "debug_check_compilation") or {}
    last = comp.get("lastCompilation") or {}
    feedback = [(_read(ctx, "script_get_compile_feedback", scriptPath=path) or {}).get("hasErrors") for path in paths]
    found = holders(ctx)
    assets = {key: p.asset(h[key]) or {} for key in ("assetA", "assetB")}
    loaded = bool(found) and any(info.get("type") == cfg_class for info in assets.values())
    card.check("D1", all(present) and not comp.get("isCompiling") and last.get("succeeded") is not False and
               not last.get("errorCount") and feedback == [False, False] and loaded,
               actual={"present": present, "isCompiling": comp.get("isCompiling"), "hasErrors": feedback,
                       "last": {k: last.get(k) for k in ("succeeded", "errorCount")}, "holders": found,
                       "assetTypes": {key: info.get("type") for key, info in assets.items()}})

    card.check("D2", all(info.get("type") == cfg_class for info in assets.values()), expected=cfg_class,
               actual={key: info.get("type") for key, info in assets.items()})
    values, good = {}, True
    for key, want in (("assetA", spec["a"]), ("assetB", spec["b"])):
        so = (p.so(h[key]) or {}) if assets[key] else {}
        props = {row.get("propertyPath"): row.get("value") for row in so.get("properties") or []}
        values[key] = {"count": props.get("count"), "interval": props.get("interval")}
        good = good and V.as_int(props.get("count")) == want["count"] and \
            V.close_tuple(props.get("interval"), (want["interval"],), VALUE_TOL)
    card.check("D3", good, expected=spec, actual=values)

    wiring = {key: _wiring(ctx, key) for key in ("spawnerA", "spawnerB")}
    strays = [ref for ref in found if not any(V.same_id(ref, h[key]) for key in ("spawnerA", "spawnerB"))]
    shown = {key: actual for key, (_ok, actual) in wiring.items()}
    shown["otherHolders"] = strays[:5]
    card.check("D4", all(ok for ok, _actual in wiring.values()) and not strays and len(found) == 2, actual=shown)

    added = sorted(set(cs_files(ctx.project_path)) - set(c0.get("scripts") or []))
    card.check("D5", set(added) <= set(paths), expected=sorted(paths), actual=added)

    diffs = {}
    for key, state in (c0.get("objects") or {}).items():
        before, now = dict(state or {}), V.go_state(p, h[key]) or {}
        for item in (before, now):
            item.pop("components", None)
        ok, diff = V.same(before, now)
        if not ok:
            diffs[key] = diff
    ok, diff = V.same(c0.get("arena"), V.go_state(p, h["arena"]))
    if not ok:
        diffs["arena"] = diff
    prefab_sha = _sha(ctx, h["prefab"])
    if prefab_sha != (c0.get("prefab") or {}).get("sha"):
        diffs["prefab"] = {"sha": prefab_sha}
    card.check("D6", not diffs, actual=diffs)

    bad = ctx.violations()
    card.check("D7", not bad, actual={"violations": bad[:10]})
    direct = [rec for rec in ctx.direct_writes() if not str(rec.get("assetPath") or "").endswith((".cs", ".cs.meta"))]
    card.check("D8", not direct, actual={"directWrites": direct[:10]})

    def listed(key):
        return sorted(V.norm_path(item) for item in (V.answer(ctx, key) or []) if isinstance(item, str)) \
            if isinstance(V.answer(ctx, key), list) else None

    spawner_paths = sorted((p.go(h[key]) or {}).get("path") or "" for key in ("spawnerA", "spawnerB"))
    card.check("D9", listed("scriptPaths") == sorted(paths) and listed("configAssetPaths") == sorted([h["assetA"],
                                                                                                       h["assetB"]])
               and listed("spawnerPaths") == spawner_paths and isinstance(V.answer(ctx, "declaredComplete"), bool),
               expected={"scriptPaths": sorted(paths), "configAssetPaths": sorted([h["assetA"], h["assetB"]]),
                         "spawnerPaths": spawner_paths}, actual=ctx.answer)


def SCENARIO(ctx):
    """Play Mode: both containers at every sample moment, then one assert that Play is still on."""
    h = ctx.handles
    steps, clock = [], 0.0
    for moment in sample_times(wave_spec(ctx.seed)):
        steps.append({"kind": "wait_seconds", "seconds": round(moment - clock, 3)})
        for key in ("containerA", "containerB"):
            steps.append({"kind": "read", "skill": "gameobject_get_info", "args": V.address(h[key])})
        clock = moment
    steps.append({"kind": "assert", "skill": "editor_get_state", "select": "isPlaying", "expected": True})
    return {"executionMode": "play", "timeoutSeconds": min(600, int(clock) + 60), "steps": steps}


def verify_runtime(ctx, card):
    scenario = ctx.scenario or {}
    spec = wave_spec(ctx.seed)
    times = sample_times(spec)
    reads = [step.get("actual") if isinstance(step.get("actual"), dict) else {}
             for step in scenario.get("steps") or [] if step.get("kind") == "read"]
    pairs = [reads[index:index + 2] for index in range(0, len(reads), 2)]
    rows, good = [], 0
    for moment, pair in zip(times, pairs):
        counts = [(info or {}).get("childCount") for info in (pair + [{}, {}])[:2]]
        ok = window_ok(counts[0], spec["a"]["count"], spec["a"]["interval"], moment) and \
            window_ok(counts[1], spec["b"]["count"], spec["b"]["interval"], moment)
        good += 1 if ok else 0
        rows.append({"t": moment, "counts": counts, "ok": ok})
    passed = scenario.get("passed") is True
    card.fraction("D10", good / float(len(times)) if passed and times else 0.0,
                  expected={"samples": len(times), "spec": spec},
                  actual={"passed": passed, "error": scenario.get("error"), "status": scenario.get("status"),
                          "samples": rows})
    final = pairs[-1] if pairs and len(pairs[-1]) == 2 else [{}, {}]
    prefab_name = _asset_name(ctx.handles["prefab"])
    exact = []
    for info, want in zip(final, (spec["a"], spec["b"])):
        children = (info or {}).get("children") or []
        exact.append(V.as_int((info or {}).get("childCount")) == want["count"] and len(children) == want["count"] and
                     all(str(child.get("name") or "").startswith(prefab_name) for child in children))
    card.check("D11", passed and all(exact),
               expected={"countA": spec["a"]["count"], "countB": spec["b"]["count"], "prefix": prefab_name},
               actual={"passed": passed, "final": [{"childCount": (info or {}).get("childCount"),
                                                    "children": [c.get("name") for c in (info or {}).get("children")
                                                                 or []][:10]} for info in final]})

# Producer:Betsy
