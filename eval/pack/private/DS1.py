"""DS1 private verifier: a patrol script walking a guard between the world positions of a route's waypoints.

What the task probes: the waypoints and the guard sit under rotated, offset parents, so localPosition on either side
walks the wrong path; the pause must be timed (deltaTime accumulation or a coroutine), the first leg starts without a
pause and the route loops; speed and pauseSeconds are serialized fields set in the scene, not code defaults; the
component goes on the guard alone; no code beyond the one script. The runtime part is timed by events, as DB3 and DB6
are: a timeSinceStartup stamp right after Play starts, then a stamp each time a wait_condition sees the guard on the
next of ARRIVALS (one lap and the first leg of the next). The gaps between stamps are checked against R2's travel and
pause times; the stamps share the editor's wall clock, so the step overhead of the scenario does not pile up the way
a fixed sampling schedule does (10-21 live 2022.3: nominal sampling drifted past 1 s over ten samples).

The speed and pause are recomputed from the seed exactly as the public module does (patrol_spec is copied verbatim);
the waypoints' world positions come from C0.
"""
import math
import os
import random

from usk_eval import verifylib as V

FIELDS = ("route", "speed", "pauseSeconds")
POSITION_TOL = 0.1          # R4: per-axis distance at which the guard counts as standing on a waypoint
ARRIVALS = (1, 2, 3, 0, 1)  # R4: waypoints waited for in order: W2, W3, W4, W1, then W2 again
TIME_TOL = 0.3              # R4: a later gap or the lap may read this much short (frame and read granularity)
LATE_TOL = 0.5              # R4: or this much long: a frame over Time.maximumDeltaTime loses game time
EARLY_SLACK = 0.7           # R4: the first arrival may read this much early (Play starts before the first stamp)
LATE_SLACK = 0.8            # R4: or this much late (the first Play frames are slow and deltaTime is capped)
LOG_FAILURES = ("error", "exception", "assert")
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
    ("D9", 4.75, False, ["R2", "R4"], "in Play Mode no error is logged and the guard reaches W2, W3, W4, W1 and W2 "
                                      "again at gaps of travel (+ pause) time (fraction)"),
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


def expected_gaps(waypoints, speed, pause):
    """Seconds between consecutive ARRIVALS by R2, the first counted from Play start: the W1->W2 travel at once, then
    pause plus the leg's travel for each later arrival."""
    lap = legs(waypoints, speed, pause)
    return [lap[0][2]] + [pause + lap[(index - 1) % len(lap)][2] for index in ARRIVALS[1:]]


def arrivals_ok(stamps, expected):
    """(passed, total, rows) for stamps [t0, t1, ...] (t0 right after Play starts, tk when the k-th arrival was seen):
    the first gap within [expected - EARLY_SLACK, expected + LATE_SLACK], each later gap and the lap from the first
    arrival to the last at most TIME_TOL short or LATE_TOL long. A gap whose stamp is missing (its wait timed out)
    fails."""
    rows, passed = [], 0
    for index, want in enumerate(expected):
        gap = stamps[index + 1] - stamps[index] if index + 1 < len(stamps) else None
        low, high = (want - EARLY_SLACK, want + LATE_SLACK) if index == 0 else (want - TIME_TOL, want + LATE_TOL)
        good = gap is not None and low - 1e-9 <= gap <= high + 1e-9
        passed += good
        rows.append({"arrival": index + 1, "expected": round(want, 3),
                     "gap": round(gap, 3) if gap is not None else None, "ok": good})
    lap = sum(expected[1:])
    span = stamps[len(expected)] - stamps[1] if len(stamps) > len(expected) else None
    good = span is not None and lap - TIME_TOL - 1e-9 <= span <= lap + LATE_TOL + 1e-9
    passed += good
    rows.append({"lap": round(lap, 3), "span": round(span, 3) if span is not None else None, "ok": good})
    return passed, len(expected) + 1, rows


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
    """Play Mode: a stamp right after start, then for each of ARRIVALS a wait until the guard stands on that waypoint
    and a stamp, then one assert that Play is still on."""
    spec = patrol_spec(ctx.seed)
    waypoints = waypoints_of(ctx.c0)
    guard = V.address(ctx.handles["guard"])
    stamp = {"kind": "read", "skill": "editor_get_state", "args": {}}
    steps = [dict(stamp)]
    for index in ARRIVALS:
        x, y, z = waypoints[index]
        steps += [{"kind": "wait_condition", "skill": "gameobject_get_info", "args": dict(guard), "select": "position",
                   "expected": {"x": x, "y": y, "z": z}, "tolerance": POSITION_TOL}, dict(stamp)]
    steps.append({"kind": "assert", "skill": "editor_get_state", "select": "isPlaying", "expected": True})
    total = sum(expected_gaps(waypoints, spec["speed"], spec["pause"]))
    return {"executionMode": "play", "timeoutSeconds": int(math.ceil(total)) + 30, "steps": steps}


def verify_runtime(ctx, card):
    """D9: one check per arrival gap, one for the lap and one for a completed scenario; no credit when an error is
    logged."""
    scenario = ctx.scenario or {}
    spec = patrol_spec(ctx.seed)
    expected = expected_gaps(waypoints_of(ctx.c0), spec["speed"], spec["pause"])
    stamps = []
    for step in scenario.get("steps") or []:
        if step.get("kind") != "read":
            continue
        value = V.floats(step["actual"].get("timeSinceStartup")) if isinstance(step.get("actual"), dict) else []
        if len(value) != 1:
            break
        stamps.append(value[0])
    passed, total, rows = arrivals_ok(stamps, expected)
    completed = scenario.get("passed") is True
    errors = logged_failures(scenario.get("logWindow"))
    fraction = 0.0 if errors or not stamps else (passed + completed) / float(total + 1)
    card.fraction("D9", fraction,
                  expected={"gaps": [round(gap, 3) for gap in expected], "lap": round(sum(expected[1:]), 3),
                            "positionTol": POSITION_TOL, "timeTol": TIME_TOL, "lateTol": LATE_TOL,
                            "earlySlack": EARLY_SLACK, "lateSlack": LATE_SLACK},
                  actual={"passed": scenario.get("passed"), "error": scenario.get("error"),
                          "status": scenario.get("status"), "errorsLogged": errors, "arrivals": rows})

# Producer:Betsy
