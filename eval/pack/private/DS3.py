"""DS3 private verifier: a WaveConfig ScriptableObject, two assets and two spawners that follow the asset they reference.

What the task probes: the two spawners share one script but reference different assets, so hard-coded numbers or a
config created at runtime (CreateInstance, code defaults) cannot satisfy both; the asset values must be set (the code
defaults differ from both assets); the first spawn comes one interval after Play starts and spawning stops at count;
instances must be direct children of the referenced container; no code beyond the two scripts. The runtime part is
timed by events, as DB3 and DS1 are: container A read on the first step (empty: its first spawn is at least 0.8 s in),
a timeSinceStartup stamp, then for each spawn in time order a wait until its container holds that many children and a
stamp; the gaps are checked against R3's intervals and both containers are read SETTLE_S after the last spawn. The
stamps share the editor's wall clock, so the step overhead of the scenario does not pile up the way a fixed sampling
schedule does (10-21 live 2022.3: the counts ran 0.3 s ahead of nominal samples).

The counts and intervals are recomputed from the seed exactly as the public module does (wave_spec is copied verbatim).
"""
import hashlib
import math
import os
import random

from usk_eval import verifylib as V

VALUE_TOL = 1e-3
TIME_TOL = 0.3           # R5: a later gap or the span may read this much short (frame and read granularity)
LATE_TOL = 0.5           # R5: or this much long: a frame over Time.maximumDeltaTime loses game time
EARLY_SLACK = 0.7        # R5: a first spawn may read this much early (Play starts before the first stamp)
LATE_SLACK = 0.8         # R5: or this much late (the first Play frames are slow and deltaTime is capped)
SETTLE_S = 1.0           # R5: the final read this long after the last spawn
AT_LEAST = 1000          # a wait matches |childCount - (k + AT_LEAST)| <= AT_LEAST, i.e. at least k children
LOG_FAILURES = ("error", "exception", "assert")
CONTAINERS = (("a", "containerA"), ("b", "containerB"))

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
    ("D10", 2.5, False, ["R3", "R5"], "in Play Mode container A starts empty, each spawn comes one interval after the "
                                      "previous (the first one interval in) and spawning stops at count (fraction)"),
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


def spawn_events(spec):
    """[(moment, key, k)] of every spawn by R3 in time order (A first on a tie): the k-th one k intervals in."""
    return sorted((round(spec[key]["interval"] * k, 3), key, k) for key, _handle in CONTAINERS
                  for k in range(1, spec[key]["count"] + 1))


def spawns_ok(t0, stamps, count, interval):
    """(passed, total, rows) for one container's spawn stamps (a timed-out wait leaves the rest missing): the first
    within [interval - EARLY_SLACK, interval + LATE_SLACK] of t0, each later gap and the span from the first to the
    last at most TIME_TOL short or LATE_TOL long."""
    rows, passed, previous = [], 0, t0
    for k in range(1, count + 1):
        stamp = stamps[k - 1] if k - 1 < len(stamps) else None
        gap = stamp - previous if stamp is not None and previous is not None else None
        low, high = (interval - EARLY_SLACK, interval + LATE_SLACK) if k == 1 else (interval - TIME_TOL,
                                                                                    interval + LATE_TOL)
        good = gap is not None and low - 1e-9 <= gap <= high + 1e-9
        passed += good
        rows.append({"spawn": k, "gap": round(gap, 3) if gap is not None else None, "ok": good})
        previous = stamp
    want = (count - 1) * interval
    span = stamps[count - 1] - stamps[0] if len(stamps) >= count else None
    good = span is not None and want - TIME_TOL - 1e-9 <= span <= want + LATE_TOL + 1e-9
    passed += good
    rows.append({"span": round(span, 3) if span is not None else None, "expected": round(want, 3), "ok": good})
    return passed, count + 1, rows


def logged_failures(node):
    """Whether the scenario's log window holds an error, exception or assert entry, as RuntimeScenarioService counts
    them (any type/logType/severity field at any depth)."""
    if isinstance(node, dict):
        return any(str(node.get(key) or "").lower() in LOG_FAILURES for key in ("type", "logType", "severity")) or \
            any(logged_failures(value) for value in node.values())
    if isinstance(node, list):
        return any(logged_failures(value) for value in node)
    return False


def _stamp(info):
    value = V.floats((info or {}).get("timeSinceStartup"))
    return value[0] if len(value) == 1 else None


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
    """Play Mode: container A on the first step, a stamp, then for each spawn in time order a wait until its container
    holds that many children and a stamp; both containers SETTLE_S after the last spawn, then one assert that Play is
    still on."""
    h, spec = ctx.handles, wave_spec(ctx.seed)
    handles = dict(CONTAINERS)
    stamp = {"kind": "read", "skill": "editor_get_state", "args": {}}
    steps = [{"kind": "read", "skill": "gameobject_get_info", "args": V.address(h["containerA"])}, dict(stamp)]
    events = spawn_events(spec)
    for _moment, key, k in events:
        steps += [{"kind": "wait_condition", "skill": "gameobject_get_info", "args": V.address(h[handles[key]]),
                   "select": "childCount", "expected": k + AT_LEAST, "tolerance": AT_LEAST}, dict(stamp)]
    steps.append({"kind": "wait_seconds", "seconds": SETTLE_S})
    steps += [{"kind": "read", "skill": "gameobject_get_info", "args": V.address(h[handle])}
              for _key, handle in CONTAINERS]
    steps.append({"kind": "assert", "skill": "editor_get_state", "select": "isPlaying", "expected": True})
    return {"executionMode": "play", "timeoutSeconds": int(math.ceil(events[-1][0] + SETTLE_S)) + 30, "steps": steps}


def verify_runtime(ctx, card):
    """D10: container A empty at start, each container's spawn gaps, span and stop, and a completed scenario; no credit
    when an error is logged. D11: the final read holds exactly count prefab instances in each container."""
    scenario = ctx.scenario or {}
    spec = wave_spec(ctx.seed)
    events = spawn_events(spec)
    reads = [step.get("actual") if isinstance(step.get("actual"), dict) else {}
             for step in scenario.get("steps") or [] if step.get("kind") == "read"]
    start = V.as_int((reads[0] if reads else {}).get("childCount"))
    t0 = _stamp(reads[1]) if len(reads) > 1 else None
    seen = {key: [] for key, _handle in CONTAINERS}
    for (_moment, key, _k), info in zip(events, reads[2:2 + len(events)]):
        if _stamp(info) is None:
            break
        seen[key].append(_stamp(info))
    final = reads[2 + len(events):4 + len(events)]
    final = final if len(final) == 2 else [{}, {}]
    passed, total, rows = int(start == 0), 1, {"startA": start}
    for (key, _handle), info in zip(CONTAINERS, final):
        count, interval = spec[key]["count"], spec[key]["interval"]
        good, checks, spawns = spawns_ok(t0, seen[key], count, interval)
        stopped = V.as_int((info or {}).get("childCount")) == count
        passed, total = passed + good + stopped, total + checks + 1
        rows[key] = {"spawns": spawns, "final": (info or {}).get("childCount"), "stopped": stopped}
    completed = scenario.get("passed") is True
    errors = logged_failures(scenario.get("logWindow"))
    fraction = 0.0 if errors or t0 is None else (passed + completed) / float(total + 1)
    card.fraction("D10", fraction,
                  expected={"spec": spec, "timeTol": TIME_TOL, "lateTol": LATE_TOL, "earlySlack": EARLY_SLACK,
                            "lateSlack": LATE_SLACK, "settle": SETTLE_S},
                  actual={"passed": scenario.get("passed"), "error": scenario.get("error"),
                          "status": scenario.get("status"), "errorsLogged": errors, "containers": rows})
    prefab_name = _asset_name(ctx.handles["prefab"])
    exact = []
    for info, want in zip(final, (spec["a"], spec["b"])):
        children = (info or {}).get("children") or []
        exact.append(V.as_int((info or {}).get("childCount")) == want["count"] and len(children) == want["count"] and
                     all(str(child.get("name") or "").startswith(prefab_name) for child in children))
    card.check("D11", completed and all(exact),
               expected={"countA": spec["a"]["count"], "countB": spec["b"]["count"], "prefix": prefab_name},
               actual={"passed": scenario.get("passed"), "final": [{"childCount": (info or {}).get("childCount"),
                                                                     "children": [c.get("name") for c in
                                                                                  (info or {}).get("children")
                                                                                  or []][:10]} for info in final]})

# Producer:Betsy
