"""DS1 private verifier: a patrol script walking a guard between the world positions of a route's waypoints.

What the task probes: the waypoints and the guard sit under rotated, offset parents, so localPosition on either side
walks the wrong path; the pause must be timed (deltaTime accumulation or a coroutine), the first leg starts without a
pause and the route loops; speed and pauseSeconds are serialized fields set in the scene, not code defaults; the
component goes on the guard alone; no code beyond the one script. The runtime part samples the guard's world position
at seeded moments (mid-leg, mid-pause, into the second lap) and accepts a position the ideal schedule passes through
within +-WINDOW_S of the sample time.

The speed and pause are recomputed from the seed exactly as the public module does (patrol_spec is copied verbatim);
the waypoints' world positions come from C0.
"""
import math
import os
import random

from usk_eval import verifylib as V

FIELDS = ("route", "speed", "pauseSeconds")
POSITION_TOL = 0.1      # R4: distance to the ideal path
WINDOW_S = 0.3          # R4: time slack on each side of a sample
WINDOW_STEP = 0.02
VALUE_TOL = 1e-3

ASSERTIONS = [
    ("D1", 1.0, False, ["R1"], "the script exists at A/Scripts/<cls>.cs, compiled without errors and its class is "
                               "loaded"),
    ("D2", 1.25, False, ["R3"], "the component on the guard carries route = Route's Transform, speed and pauseSeconds "
                                "as given"),
    ("D3", 0.5, False, ["R3"], "exactly one such component in the scene, on the guard"),
    ("D4", 0.5, True, ["R5"], "no code file other than the script was added under Assets"),
    ("D5", 0.75, False, ["R6"], "the route, its waypoints, the post and the guard's own transform are unchanged"),
    ("D6", 0.5, True, ["G1", "G2"], "no change outside R/A, no other scene saved or opened"),
    ("D7", 0.25, False, ["G4"], "no file other than C# scripts was written under A around the skills"),
    ("D8", 0.5, False, ["R7", "G8"], "the answer reports the script path and the guard's path"),
    ("D9", 4.75, False, ["R2", "R4"], "in Play Mode the guard is on the ideal path at every sample (fraction) and no "
                                      "error is logged"),
]

HINTS = {
    "D1": {"zh-CN": "脚本放在 A/Scripts 下，文件名与类名一致，等编译通过（GET 返回的 waitUrl 或 script_get_compile_feedback）再挂组件。",
           "en": "Put the script under A/Scripts, named after its class, and wait for a clean compile (the returned "
                 "waitUrl or script_get_compile_feedback) before adding the component."},
    "D2": {"zh-CN": "在场景里设置 route（指向 Route 的 Transform）、speed 和 pauseSeconds 的序列化值；代码默认值不算。",
           "en": "Set route (Route's Transform), speed and pauseSeconds as serialized values in the scene; code "
                 "defaults do not count."},
    "D3": {"zh-CN": "组件只挂在 Guard 上，不要挂在 Post 或其他对象上。",
           "en": "Attach the component to the Guard only, not to the Post or anything else."},
    "D4": {"zh-CN": "除这个脚本外不要写其他代码文件（包括编辑器脚本）。",
           "en": "Write no other code file (editor scripts included)."},
    "D5": {"zh-CN": "不要移动或改名 Route、路点、Post，也不要在编辑模式下移动 Guard。",
           "en": "Do not move or rename the Route, the waypoints or the Post, and do not move the Guard in Edit Mode."},
    "D6": {"zh-CN": "所有修改都必须在 R 与 A 之内，且只能保存本轮场景。",
           "en": "Keep every change inside R and A, and save no scene but this run's."},
    "D7": {"zh-CN": "除 C# 脚本外，不要绕过技能直接在 A 下写文件。",
           "en": "Apart from C# scripts, do not write files under A directly; use the skills."},
    "D8": {"zh-CN": "答案里报告脚本路径和 Guard 的完整路径，并写明 declaredComplete。",
           "en": "Report the script path and the Guard's full path, and declaredComplete."},
    "D9": {"zh-CN": "用路点和自身的世界坐标（position，不是 localPosition）；进入 Play 后立即向第二个路点出发，到点停留 pauseSeconds 秒，"
                    "最后一个路点之后回到第一个；运行时不能有错误日志。",
           "en": "Use world positions (position, not localPosition) for the waypoints and the guard; head for the "
                 "second waypoint at once, pause pauseSeconds at each waypoint, return to the first after the last; "
                 "no error may be logged at runtime."},
}


def patrol_spec(seed):
    """Speed (world units per second) and pause (seconds) the scene must carry, from the seed alone."""
    rng = random.Random("DS1:%d" % seed)
    return {"speed": rng.choice((2.0, 2.5, 3.0)), "pause": rng.choice((0.5, 0.75, 1.0))}


# ---------------------------------------------------------------- the ideal schedule (R2)


def _distance(a, b):
    return math.sqrt(sum((x - y) ** 2 for x, y in zip(a, b)))


def legs(waypoints, speed, pause):
    """[(start, end, travel seconds)] of one lap, in order: W1->W2, ..., Wn->W1."""
    out = []
    for index, start in enumerate(waypoints):
        end = waypoints[(index + 1) % len(waypoints)]
        out.append((start, end, _distance(start, end) / float(speed)))
    return out


def lap_seconds(waypoints, speed, pause):
    return sum(travel + pause for _start, _end, travel in legs(waypoints, speed, pause))


def position_at(waypoints, speed, pause, t):
    """Where the ideal patrol is t seconds after Play starts: moving from W1 at once, pausing after each leg,
    looping."""
    lap = legs(waypoints, speed, pause)
    total = lap_seconds(waypoints, speed, pause)
    if total <= 0:
        return tuple(waypoints[0])
    t = t % total
    for start, end, travel in lap:
        if t <= travel:
            share = t / travel if travel > 0 else 1.0
            return tuple(s + (e - s) * share for s, e in zip(start, end))
        t -= travel
        if t <= pause:
            return tuple(end)
        t -= pause
    return tuple(waypoints[0])


def sample_times(waypoints, speed, pause):
    """Mid-leg and mid-pause moments of the first lap, then the first leg and pause of the second lap."""
    out, clock = [], 0.0
    for _start, _end, travel in legs(waypoints, speed, pause):
        out.append(clock + travel / 2.0)
        clock += travel
        out.append(clock + pause / 2.0)
        clock += pause
    first = legs(waypoints, speed, pause)[0]
    out.append(clock + first[2] / 2.0)
    out.append(clock + first[2] + pause / 2.0)
    return [round(value, 3) for value in out]


def on_path(actual, waypoints, speed, pause, t):
    """Whether `actual` is within POSITION_TOL of the ideal position at some moment in [t - WINDOW_S, t + WINDOW_S]."""
    point = V.floats(actual)
    if len(point) != 3:
        return False
    moment = max(0.0, t - WINDOW_S)
    while moment <= t + WINDOW_S + 1e-9:
        if _distance(point, position_at(waypoints, speed, pause, moment)) <= POSITION_TOL:
            return True
        moment += WINDOW_STEP
    return False


# ---------------------------------------------------------------- reading


def class_name(handles):
    return os.path.basename(handles["script"])[:-len(".cs")]


def waypoints_of(c0):
    return [tuple(V.floats((state or {}).get("position"))) for _ref, state in c0.get("waypoints") or []]


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


def holders(ctx):
    """Ids of the GameObjects carrying the class (empty when it is not loaded)."""
    found = _read(ctx, "gameobject_find", component=class_name(ctx.handles), limit=500) or {}
    return [V.object_id(obj) for obj in found.get("objects") or []]


def holder(ctx, found=None):
    """The object whose component is graded: the guard when it carries one, else the first holder."""
    found = holders(ctx) if found is None else found
    guard = ctx.handles["guard"]
    return next((ref for ref in found if V.same_id(ref, guard)), found[0] if found else None)


def verify(ctx, card):
    p, h, c0 = ctx.probe, ctx.handles, ctx.c0
    cls, spec = class_name(h), patrol_spec(ctx.seed)
    found = holders(ctx)
    comp = _read(ctx, "debug_check_compilation") or {}
    last = comp.get("lastCompilation") or {}
    feedback = (_read(ctx, "script_get_compile_feedback", scriptPath=h["script"]) or {}).get("hasErrors")
    present = os.path.isfile(os.path.join(ctx.project_path, *h["script"].split("/")))
    card.check("D1", present and not comp.get("isCompiling") and last.get("succeeded") is not False and
               not last.get("errorCount") and feedback is False and bool(found),
               actual={"present": present, "isCompiling": comp.get("isCompiling"), "hasErrors": feedback,
                       "last": {k: last.get(k) for k in ("succeeded", "errorCount")}, "holders": found})

    graded = holder(ctx, found)
    ser = (p.ser(graded, cls) or {}) if graded is not None else {}
    route_ref = V.parse_ref(ser.get("route"))
    route_owner = p.owner(ser.get("route"))
    card.check("D2", bool(route_ref) and route_ref[1] == "UnityEngine.Transform" and V.same_id(route_owner, h["route"])
               and V.close_tuple(ser.get("speed"), (spec["speed"],), VALUE_TOL)
               and V.close_tuple(ser.get("pauseSeconds"), (spec["pause"],), VALUE_TOL),
               expected={"route": h["routePath"], "speed": spec["speed"], "pauseSeconds": spec["pause"]},
               actual={key: ser.get(key) for key in FIELDS})
    on_guard = p.component_count(h["guard"], cls)
    card.check("D3", len(found) == 1 and V.same_id(found[0], h["guard"]) and on_guard == 1,
               expected=h["guardPath"], actual={"holders": found, "onGuard": on_guard})

    added = sorted(set(cs_files(ctx.project_path)) - set(c0.get("scripts") or []))
    card.check("D4", set(added) <= {h["script"]}, expected=[h["script"]], actual=added)

    diffs = {}
    ok, diff = V.same(c0.get("route"), V.go_state(p, h["route"]))
    if not ok:
        diffs["route"] = diff
    for ref, state in c0.get("waypoints") or []:
        ok, diff = V.same(state, V.go_state(p, ref))
        if not ok:
            diffs[str(ref)] = diff
    for key in ("guard", "post"):
        before, now = dict(c0.get(key) or {}), V.go_state(p, h[key]) or {}
        for state in (before, now):
            state.pop("components", None)
        ok, diff = V.same(before, now)
        if not ok:
            diffs[key] = diff
    card.check("D5", not diffs, actual=diffs)

    bad = ctx.violations()
    card.check("D6", not bad, actual={"violations": bad[:10]})
    direct = [rec for rec in ctx.direct_writes() if not str(rec.get("assetPath") or "").endswith((".cs", ".cs.meta"))]
    card.check("D7", not direct, actual={"directWrites": direct[:10]})
    card.check("D8", V.norm_path(V.answer(ctx, "scriptPath")) == h["script"] and
               V.norm_path(V.answer(ctx, "guardPath")) == (p.go(h["guard"]) or {}).get("path") and
               isinstance(V.answer(ctx, "declaredComplete"), bool),
               expected={"scriptPath": h["script"], "guardPath": h["guardPath"]}, actual=ctx.answer)


def SCENARIO(ctx):
    """Play Mode: the guard's world position at the seeded sample moments, then one assert that Play is still on."""
    spec = patrol_spec(ctx.seed)
    times = sample_times(waypoints_of(ctx.c0), spec["speed"], spec["pause"])
    steps, clock = [], 0.0
    for moment in times:
        steps.append({"kind": "wait_seconds", "seconds": round(moment - clock, 3)})
        steps.append({"kind": "read", "skill": "gameobject_get_info", "args": V.address(ctx.handles["guard"])})
        clock = moment
    steps.append({"kind": "assert", "skill": "editor_get_state", "select": "isPlaying", "expected": True})
    return {"executionMode": "play", "timeoutSeconds": min(600, int(clock) + 60), "steps": steps}


def verify_runtime(ctx, card):
    scenario = ctx.scenario or {}
    spec = patrol_spec(ctx.seed)
    waypoints = waypoints_of(ctx.c0)
    times = sample_times(waypoints, spec["speed"], spec["pause"])
    reads = [step for step in scenario.get("steps") or [] if step.get("kind") == "read"]
    results = []
    for moment, step in zip(times, reads):
        actual = (step.get("actual") or {}).get("position") if isinstance(step.get("actual"), dict) else None
        results.append({"t": moment, "position": actual, "ok": on_path(actual, waypoints, spec["speed"], spec["pause"],
                                                                        moment)})
    good = sum(1 for row in results if row["ok"])
    fraction = good / float(len(times)) if scenario.get("passed") is True and times else 0.0
    card.fraction("D9", fraction, expected={"samples": len(times), "ideal": [position_at(waypoints, spec["speed"],
                                                                                        spec["pause"], t) for t in times]},
                  actual={"passed": scenario.get("passed"), "error": scenario.get("error"), "status": scenario.get("status"),
                          "samples": results})

# Producer:Betsy
