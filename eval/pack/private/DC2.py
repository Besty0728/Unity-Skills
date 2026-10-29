"""DC2 private verifier: a door state machine (Closed / Opening / Open / Closing) on scaled game time.

What the task probes:
- Progress kept across switches: a reversal (Close while opening, Open while closing) continues from the current
  openness; restarting from 0 or 1 jumps.
- One motion at a time: a coroutine per command that is not stopped on a reversal keeps fighting the new one (the door
  freezes or drifts), and an unguarded repeated command doubles the speed or restarts it.
- deltaTime and timeScale: a fixed step per frame or unscaled time keeps moving while Time.timeScale is 0 and moves
  twice as fast per game second at 0.5; a WaitForSeconds-then-snap door does not move in between.
- The scene value of travelSeconds (code default 0.5, scene value 2.5 / 3 / 3.5 s).

How Play Mode is measured: a fixture recorder (LateUpdate, execution order 10000) stores the door's openness and state
with Time.time every frame, so each read step returns a consistent (game time, openness, state) triple. Only the
moment a call takes effect is uncertain, and it is bracketed by the reads before and after it (a wait_frames 1 on each
side covers implementations that move in the call itself or start one frame late). Each reading is predicted from the
previous one: openness moves at +-1/travelSeconds per game second in the commanded direction, clamped to [0, 1]; a
call between two readings may take effect anywhere between them (the prediction is monotone in that moment, so the two
ends bound it). Tolerance 0.03, and exact equality when the game clock stood still (Time.timeScale 0). The door's
methods are only invoked at Time.timeScale 1, so a scenario that stops on a throwing call never leaves the clock
scaled.
"""
import hashlib
import os
import random
import re

from usk_eval import verifylib as V

TOL = 0.03                 # R5: openness tolerance of a reading
FROZEN_TOL = 1e-6          # R5: exactly unchanged while Time.timeScale is 0
RANGE_EPS = 1e-6           # R2/R5: openness must stay within [0, 1] up to this
END_EPS = 1e-4             # openness counted as having reached 0 or 1 (float32 sums land a few ulps short)
NEAR_END = 1e-3            # within this of an end the state may already show it
VALUE_TOL = 1e-4
CODE_DEFAULT = 0.5
STATES = ("Closed", "Opening", "Open", "Closing")
SCRIPT_RE = re.compile(r"\.cs(\.meta)?$")
CODE_RE = re.compile(r"//[^\n]*|/\*.*?\*/|@\"(?:\"\"|[^\"])*\"|\"(?:\\.|[^\"\\\n])*\"|'(?:\\.|[^'\\\n])+'", re.S)
DECL_RE = re.compile(r"((?:\[[^\[\]]*\]\s*)*)((?:(?:public|private|protected|internal|static|readonly|const|new)\s+)*)"
                     r"float\s+travelSeconds\s*(?:=\s*([^;{}]*))?;")
LITERAL_RE = re.compile(r"^\+?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?[fF]$")
GROUPS = ("X1", "X2", "X3")

ASSERTIONS = [
    ("C1", 0.75, False, ["R1"], "the script declares the MonoBehaviour class, the nested enum DoorState with exactly "
                                "Closed/Opening/Open/Closing, a serialized float travelSeconds initialized with a literal 0.5, "
                                "public openness (float) and state (DoorState), and public void Open() / Close()"),
    ("C2", 0.5, False, ["R1"], "the script exists at A/Scripts/<cls>.cs and the project compiled without errors"),
    ("C3", 1.0, False, ["R4"], "exactly one component of the class in the scene, on the door, with travelSeconds "
                               "stored in the scene as the given value"),
    ("C4", 0.5, True, ["R6"], "no code file other than the new script was added, deleted or modified"),
    ("C5", 0.5, True, ["R6"], "the hall, frame, door (apart from the new component), spare object, monitor and its "
                              "recorder settings are unchanged"),
    ("C6", 0.25, True, ["G1", "G2"], "no change outside R/A, no other scene saved or opened"),
    ("C7", 0.25, False, ["G4"], "no file other than C# scripts was written under A around the skills"),
    ("C8", 0.25, False, ["R7", "G8"], "the answer reports the script path and the path of the object carrying the "
                                      "component"),
    ("X1", 2.0, False, ["R2", "R5"], "Play Mode, plain transitions: starts Closed at 0, opens from Closed and closes "
                                     "from Open at 1/travelSeconds per game second, the state reaching Open at 1 and "
                                     "Closed at 0 (fraction of the readings; X1-X3 are 0 when the scenario fails, "
                                     "e.g. an error is logged)"),
    ("X2", 1.75, False, ["R3", "R5"], "Play Mode, scaled time: frozen exactly at Time.timeScale 0, the same speed per "
                                      "game second at 0.5 and back at 1 (fraction of the readings; 0 when the scenario fails)"),
    ("X3", 2.25, False, ["R2", "R5"], "Play Mode, repeated and reversing calls: Open while open and Close while closed "
                                      "or closing change nothing, reversals keep the progress, run one motion at "
                                      "the normal speed, and the state follows openness (fraction of the readings; "
                                      "0 when the scenario fails)"),
]

HINTS = {
    "C1": {"zh-CN": "按 R1 声明：类内嵌套的公开枚举 DoorState（Closed、Opening、Open、Closing），可序列化的 float travelSeconds（代码默认 0.5），"
                    "公开的 openness 与 state，以及公开无参的 Open() 和 Close()。",
           "en": "Declare what R1 lists: the nested public enum DoorState (Closed, Opening, Open, Closing), a serialized "
                 "float travelSeconds (code default 0.5), public openness and state, and public parameterless Open() "
                 "and Close()."},
    "C2": {"zh-CN": "脚本放在 A/Scripts 下、文件名与类名一致，等编译完成且没有错误再挂组件。",
           "en": "Put the script under A/Scripts, named after its class, and wait for a clean compile before "
                 "attaching it."},
    "C3": {"zh-CN": "组件只挂在门对象上，并在场景中把 travelSeconds 设为题目给出的值，不能只依赖代码默认值。",
           "en": "Attach the component to the door object only and set travelSeconds to the given value in the "
                 "scene; the code default is not enough."},
    "C4": {"zh-CN": "除新建脚本外，不要新增、删除或修改其他代码文件，记录器脚本也不要动。",
           "en": "Apart from the new script, add, delete or modify no code file, the recorder script included."},
    "C5": {"zh-CN": "不要改动记录器所在对象及其设置、备用对象和门对象的其他部分。",
           "en": "Leave the recorder's object and settings, the spare object and the rest of the door object as they "
                 "are."},
    "C6": {"zh-CN": "所有修改都必须在 R 与 A 之内，且只能保存本轮场景。",
           "en": "Keep every change inside R and A, and save no scene but this run's."},
    "C7": {"zh-CN": "除 C# 脚本外，不要绕过技能直接在 A 下写文件。",
           "en": "Apart from C# scripts, do not write files under A directly; use the skills."},
    "C8": {"zh-CN": "答案里报告脚本路径和实际挂载组件的对象的完整路径，并写明 declaredComplete。",
           "en": "Report the script path and the full path of the object that really carries the component, plus "
                 "declaredComplete."},
    "X1": {"zh-CN": "openness 按 Time.deltaTime / travelSeconds 每帧线性变化，到 1 的那一帧变为 Open，到 0 的那一帧变为 Closed；travelSeconds 用场景里的值。",
           "en": "Change openness linearly by Time.deltaTime / travelSeconds each frame, become Open in the frame it "
                 "reaches 1 and Closed in the frame it reaches 0, using the scene's travelSeconds."},
    "X2": {"zh-CN": "用缩放后的 Time.deltaTime（不要用帧计数、unscaledDeltaTime 或真实时间），timeScale 为 0 时门必须完全静止。",
           "en": "Use scaled Time.deltaTime (no frame counting, unscaledDeltaTime or real time); at timeScale 0 the "
                 "door must not move at all."},
    "X3": {"zh-CN": "重复的同向调用不起作用；反向切换从当前 openness 继续，并停止旧的运动（例如先 StopCoroutine 再启动新协程），保证任何时刻只有一个运动。",
           "en": "A repeated call in the same direction does nothing; a reversal continues from the current openness "
                 "and stops the old motion (StopCoroutine before starting a new one, for example), so only one "
                 "motion ever runs."},
}


def travel_seconds(seed):
    """Verbatim copy of the public task's travel_seconds(seed) (pinned by unit tests)."""
    return random.Random("DC2:%d" % seed).choice((2.5, 3.0, 3.5))


def plan(travel):
    """The scenario as a list: ("wait_frames", n), ("wait_seconds", s), ("read", label, group), ("call", method),
    ("scale", timeScale), ("assert",). Every call sits between two reads with a wait_frames 1 on each side."""
    finish = round(0.75 * travel + 0.5, 3)
    out = [("wait_frames", 2), ("wait_seconds", 0.5), ("read", "start", "X1")]

    def call(method, label, group):
        out.extend([("wait_frames", 1), ("call", method), ("wait_frames", 1), ("read", label, group)])

    call("Open", "opened-from-closed", "X1")
    out.extend([("wait_seconds", 0.3), ("read", "opening", "X1")])
    call("Open", "open-while-opening", "X3")
    out.extend([("wait_seconds", 0.1), ("read", "opening-on", "X3"),
                ("scale", 0.0), ("wait_frames", 1), ("read", "freeze", "X2"),
                ("wait_seconds", 1.0), ("read", "frozen", "X2"),
                ("scale", 0.5), ("wait_seconds", 1.0), ("read", "half-speed", "X2"),
                ("scale", 1.0), ("wait_frames", 1), ("read", "full-speed", "X2"),
                ("wait_seconds", finish), ("read", "open", "X1")])
    call("Open", "open-again", "X3")
    call("Close", "closed-from-open", "X1")
    out.extend([("wait_seconds", 0.3), ("read", "closing", "X1")])
    call("Close", "close-again", "X3")
    out.extend([("wait_seconds", 0.3), ("read", "closing-on", "X3")])
    call("Open", "reopen", "X3")
    out.extend([("wait_seconds", 0.3), ("read", "reopening", "X3")])
    call("Close", "reclose", "X3")
    out.extend([("wait_seconds", 0.3), ("read", "reclosing", "X3"),
                ("wait_seconds", finish), ("read", "closed", "X3")])
    call("Close", "close-when-closed", "X3")
    out.extend([("wait_seconds", 0.3), ("read", "still-closed", "X3"), ("scale", 1.0), ("assert",)])
    return out


# ---------------------------------------------------------------- shared reads


def class_name(handles):
    return os.path.basename(handles["script"])[:-len(".cs")]


def cs_files(project_path):
    out = []
    for base, dirs, files in os.walk(os.path.join(project_path, "Assets")):
        dirs.sort()
        out += [os.path.relpath(os.path.join(base, name), project_path).replace(os.sep, "/")
                for name in sorted(files) if name.endswith(".cs")]
    return sorted(out)


def file_sha(project_path, asset_path):
    try:
        with open(os.path.join(project_path, *asset_path.split("/")), "rb") as handle:
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


def compiled(ctx):
    comp = _read(ctx, "debug_check_compilation") or {}
    last = comp.get("lastCompilation") or {}
    return not comp.get("isCompiling") and last.get("succeeded") is not False and not last.get("errorCount")


def holders(ctx):
    found = _read(ctx, "gameobject_find", component=class_name(ctx.handles), limit=500) or {}
    return [V.object_id(obj) for obj in found.get("objects") or [] if V.object_id(obj) is not None]


def holder(ctx, found=None):
    found = holders(ctx) if found is None else found
    door = ctx.handles["door"]
    return next((ref for ref in found if V.same_id(ref, door)), found[0] if found else None)


def recorder_rows(probe, ref, component_type):
    """[[propertyPath, value]] of the recorder without Unity's own m_ rows, sorted by path."""
    return sorted([key, value] for key, value in (probe.ser(ref, component_type) or {}).items()
                  if not str(key).startswith("m_"))


# ---------------------------------------------------------------- script shape (C1)


def code_of(source):
    return CODE_RE.sub(" ", source)


def class_body(code, cls):
    match = re.search(r"\bclass\s+%s\b[^{]*\{" % re.escape(cls), code)
    if not match:
        return ""
    depth, index = 1, match.end()
    while index < len(code) and depth:
        depth += {"{": 1, "}": -1}.get(code[index], 0)
        index += 1
    return code[match.end():index - 1]


def enum_members(body):
    match = re.search(r"\bpublic\s+enum\s+DoorState\b[^{]*\{([^}]*)\}", body)
    if not match:
        return None
    names = []
    for part in match.group(1).split(","):
        part = re.sub(r"\[[^\]]*\]", " ", part).split("=")[0].strip()
        if part:
            names.append(part)
    return names


def travel_default(code):
    """(serialized, default value) of the travelSeconds declaration, or (False, None)."""
    for attrs, mods, init in DECL_RE.findall(code):
        words = mods.split()
        serialized = ("public" in words or re.search(r"\bSerializeField\b", attrs) is not None) and \
            not {"static", "const", "readonly"} & set(words) and re.search(r"\bNonSerialized\b", attrs) is None
        text = (init or "").strip()
        value = float(text[:-1]) if LITERAL_RE.match(text) else None
        return serialized, value
    return False, None


def script_shape(text, cls):
    code = code_of(text or "")
    body = class_body(code, cls)
    members = enum_members(body)
    serialized, default = travel_default(body)
    member = r"\bpublic\s+(?:virtual\s+|override\s+|new\s+)*%s\s+%s\b"
    return {
        "present": text is not None,
        "class": re.search(r"\bpublic\s+(?:sealed\s+|partial\s+)*class\s+%s\s*:\s*(?:UnityEngine\.)?MonoBehaviour\b"
                           % re.escape(cls), code) is not None,
        "enum": members is not None and sorted(members) == sorted(STATES),
        "travelSeconds": serialized and default is not None and abs(default - CODE_DEFAULT) <= VALUE_TOL,
        "openness": re.search(member % ("float", "openness"), body) is not None,
        "state": re.search(member % (r"(?:%s\s*\.\s*)?DoorState" % re.escape(cls), "state"), body) is not None,
        "open": re.search(r"\bpublic\s+(?:virtual\s+)?void\s+Open\s*\(\s*\)", body) is not None,
        "close": re.search(r"\bpublic\s+(?:virtual\s+)?void\s+Close\s*\(\s*\)", body) is not None,
    }


# ---------------------------------------------------------------- static checks


def _without(state, cls):
    state = dict(state or {})
    state["components"] = [c for c in state.get("components") or []
                            if c != cls and not (isinstance(c, dict) and c.get("type") == cls)]
    return state


def verify(ctx, card):
    p, h, c0 = ctx.probe, ctx.handles, ctx.c0
    cls, travel = class_name(h), travel_seconds(ctx.seed)
    text = (_read(ctx, "script_read", scriptPath=h["script"]) or {}).get("content")
    shape = script_shape(text if isinstance(text, str) else None, cls)
    card.check("C1", all(shape.values()), expected={"class": cls, "travelSecondsDefault": CODE_DEFAULT,
                                                    "states": STATES}, actual=shape)

    comp = _read(ctx, "debug_check_compilation") or {}
    last = comp.get("lastCompilation") or {}
    feedback = (_read(ctx, "script_get_compile_feedback", scriptPath=h["script"]) or {}).get("hasErrors")
    present = os.path.isfile(os.path.join(ctx.project_path, *h["script"].split("/")))
    card.check("C2", present and compiled(ctx) and feedback is False,
               actual={"present": present, "isCompiling": comp.get("isCompiling"), "hasErrors": feedback,
                       "last": {k: last.get(k) for k in ("succeeded", "errorCount")}})

    found = holders(ctx)
    on_door = p.component_count(h["door"], cls)
    stored = V.floats((p.ser(h["door"], cls) or {}).get("travelSeconds")) if on_door else []
    card.check("C3", len(found) == 1 and V.same_id(found[0], h["door"]) and on_door == 1 and
               V.close_tuple(stored, (travel,), VALUE_TOL),
               expected={"holder": h["doorPath"], "travelSeconds": travel},
               actual={"holders": found, "onDoor": on_door, "travelSeconds": stored})

    before = {path: sha for path, sha in c0.get("scripts") or []}
    now = cs_files(ctx.project_path)
    changed = sorted(path for path, sha in before.items() if path not in now or
                     file_sha(ctx.project_path, path) != sha)
    extra = sorted(set(now) - set(before) - {h["script"]})
    card.check("C4", not changed and not extra, expected={"added": [h["script"]], "changed": []},
               actual={"changed": changed, "extra": extra})

    diffs = {}
    for key in ("hall", "frame", "spare", "monitor"):
        ok, diff = V.same(c0.get(key), V.go_state(p, h[key]))
        if not ok:
            diffs[key] = diff
    ok, diff = V.same(_without(c0.get("door"), cls), _without(V.go_state(p, h["door"]), cls))
    if not ok:
        diffs["door"] = diff
    rows_before = [row for row in c0.get("recorderRows") or [] if not str(row[0]).startswith("m_")]
    rows_now = recorder_rows(p, h["monitor"], h["recorder"])
    if rows_before != rows_now:
        diffs["recorder"] = {"before": rows_before, "now": rows_now}
    card.check("C5", not diffs, actual=diffs)

    bad = ctx.violations()
    card.check("C6", not bad, actual={"violations": bad[:10]})
    direct = [rec for rec in ctx.direct_writes() if not SCRIPT_RE.search(str(rec.get("assetPath") or ""))]
    card.check("C7", not direct, actual={"directWrites": direct[:10]})

    carrier = found[0] if len(found) == 1 else h["door"]
    carrier_path = (p.go(carrier) or {}).get("path")
    card.check("C8", V.norm_path(V.answer(ctx, "scriptPath")) == h["script"] and
               V.norm_path(V.answer(ctx, "doorPath")) == carrier_path and
               isinstance(V.answer(ctx, "declaredComplete"), bool),
               expected={"scriptPath": h["script"], "doorPath": carrier_path}, actual=ctx.answer)


# ---------------------------------------------------------------- Play Mode


def _time_scale(value):
    return {"kind": "invoke", "skill": "component_invoke_method",
            "args": {"componentType": "UnityEngine.Time", "methodName": "set_timeScale", "isStatic": True,
                     "parameterTypes": "[\"System.Single\"]", "arguments": "[%s]" % repr(float(value))}}


def SCENARIO(ctx):
    """Play Mode: the plan's reads of the recorder, the door's Open() / Close() on the component carrying the class
    (the one on the door when there is one) and the static Time.timeScale setter. Without a clean compile, a carrier or
    the recorder, the scenario is a single edit-mode check that fails at once."""
    h = ctx.handles
    check = {"kind": "assert", "skill": "editor_get_state", "select": "isPlaying", "expected": True}
    edit = {"executionMode": "edit", "timeoutSeconds": 30, "steps": [check]}
    carrier = holder(ctx) if compiled(ctx) else None
    if carrier is None or ctx.probe.component_count(h["monitor"], h["recorder"]) != 1:
        return edit
    cls = class_name(h)
    read = {"kind": "read", "skill": "component_get_serialized_properties",
            "args": dict({"componentType": h["recorder"]}, **V.address(h["monitor"]))}
    steps, waited = [], 0.0
    for item in plan(travel_seconds(ctx.seed)):
        kind = item[0]
        if kind == "wait_frames":
            steps.append({"kind": "wait_frames", "frames": item[1]})
        elif kind == "wait_seconds":
            steps.append({"kind": "wait_seconds", "seconds": item[1]})
            waited += item[1]
        elif kind == "read":
            steps.append(dict(read, args=dict(read["args"])))
        elif kind == "call":
            steps.append({"kind": "invoke", "skill": "component_invoke_method",
                          "args": dict({"componentType": cls, "methodName": item[1], "parameterTypes": "[]"},
                                       **V.address(carrier))})
        elif kind == "scale":
            steps.append(_time_scale(item[1]))
        else:
            steps.append(dict(check))
    return {"executionMode": "play", "timeoutSeconds": int(waited) + 120, "steps": steps}


def reading(step):
    """(game time, openness, state, count) of a recorder read step, or None."""
    actual = step.get("actual") if isinstance(step, dict) else None
    props = actual.get("properties") if isinstance(actual, dict) else None
    values = {row.get("propertyPath"): row.get("value") for row in props or [] if isinstance(row, dict)}
    when, open_, count = V.floats(values.get("sampleTime")), V.floats(values.get("sampleOpenness")), \
        V.as_int(values.get("sampleCount"))
    if len(when) != 1 or len(open_) != 1 or count is None:
        return None
    return {"t": when[0], "o": open_[0], "state": str(values.get("sampleState") or ""), "count": count}


def _move(value, direction, span, travel):
    return min(1.0, max(0.0, value + direction * span / travel))


def predict(start, direction, span, call, travel):
    """(low, high) openness `span` game seconds after a reading of `start` while moving in `direction`, with a call
    switching to `call` (+1 Open, -1 Close) at some moment in between, or None for no call."""
    if call is None:
        value = _move(start, direction, span, travel)
        return value, value
    early = _move(start, call, span, travel)
    late = _move(_move(start, direction, span, travel), call, 0.0, travel)
    return min(early, late), max(early, late)


def allowed_states(direction, value):
    if direction > 0:
        return {"Open"} if value >= 1.0 - END_EPS else {"Open", "Opening"} if value >= 1.0 - NEAR_END else {"Opening"}
    return {"Closed"} if value <= END_EPS else {"Closed", "Closing"} if value <= NEAR_END else {"Closing"}


def grade(steps, travel):
    """One row per planned read: label, group, ok and the evidence, predicted from the previous reading."""
    by_index = {}
    for position, step in enumerate(steps or []):
        if isinstance(step, dict):
            by_index[step.get("index", position)] = step
    rows, last, direction, call, broken = [], None, -1, None, False
    for index, item in enumerate(plan(travel)):
        if item[0] == "call":
            call = 1 if item[1] == "Open" else -1
            continue
        if item[0] != "read":
            continue
        now = None if broken else reading(by_index.get(index))
        row = {"label": item[1], "group": item[2], "reading": now, "ok": False}
        if now is None:
            broken = True
            rows.append(row)
            continue
        heading = call if call is not None else direction
        if last is None:
            low = high = 0.0
            tol, span = FROZEN_TOL, 0.0
        else:
            span = now["t"] - last["t"]
            low, high = predict(last["o"], direction, span, call, travel)
            tol = FROZEN_TOL if call is None and abs(span) <= 1e-7 else TOL
        row.update(predicted=[round(low, 5), round(high, 5)], tolerance=tol, span=round(span, 5),
                   states=sorted(allowed_states(heading, now["o"])))
        row["ok"] = (now["count"] == 1 and span >= -1e-6 and -RANGE_EPS <= now["o"] <= 1.0 + RANGE_EPS and
                     low - tol <= now["o"] <= high + tol and
                     now["state"] in allowed_states(heading, now["o"]))
        rows.append(row)
        last, direction, call = now, heading, None
    return rows


def verify_runtime(ctx, card):
    scenario = ctx.scenario or {}
    passed = scenario.get("passed") is True
    travel = travel_seconds(ctx.seed)
    rows = grade(scenario.get("steps") or [], travel)
    for group in GROUPS:
        mine = [row for row in rows if row["group"] == group]
        good = sum(1 for row in mine if row["ok"])
        card.fraction(group, good / float(len(mine)) if passed and mine else 0.0,
                      expected={"travelSeconds": travel, "tolerance": TOL, "readings": len(mine)},
                      actual={"passed": passed, "error": scenario.get("error"), "status": scenario.get("status"),
                              "rows": mine})

# Producer:Betsy
