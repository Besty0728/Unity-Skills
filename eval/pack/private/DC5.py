"""DC5 private verifier: a PauseController script, two buttons built by skills and persistent onClick listeners, then a
Play Mode pause / resume sequence.

What the task probes: the host shares its name with another object (a listener bound by targetObjectName or a
component added by name can land on the wrong one; the component must sit on the named host only); listeners must be
persistent (a runtime AddListener leaves the serialized events empty); each button's onClick has exactly one listener
with the right method and a state that fires at runtime; the resume button lives inside the inactive panel; the panel
stays inactive in the scene and the game starts running; TogglePause follows the actual state, so a Resume() that
leaves a private flag behind breaks the next pause. Play Mode evidence: Time.timeScale read through a static
invocation and the panel's active state after each of six clicks; the scenario sets Time.timeScale back to 1 at the
end so a paused negative leaves nothing behind.
"""
import os
import re

from usk_eval import verifylib as V

FIELD = "panel"
METHODS = ("TogglePause", "Resume")
ACTIVE_STATES = ("RuntimeOnly", "EditorAndRuntime")
SCRIPT_RE = re.compile(r"\.cs(\.meta)?$")
COMMENT_RE = re.compile(r"//[^\n]*|/\*.*?\*/", re.S)
# The clicks in order ("pause" / "resume" button) and the (panel active, timeScale) reading after the start and after
# each click. A click on an inactive button does nothing (Button.Press checks IsActive).
CLICKS = ("pause", "pause", "pause", "resume", "pause", "resume")
SEQUENCE = ((False, 1.0), (True, 0.0), (False, 1.0), (True, 0.0), (False, 1.0), (True, 0.0), (False, 1.0))

ASSERTIONS = [
    ("P1", 1.0, False, ["R1"], "the script exists at A/Scripts/<Class>.cs and declares the class, the serialized "
                               "GameObject field panel and public parameterless TogglePause() and Resume()"),
    ("P2", 0.5, False, ["R1"], "compilation finished without errors for the project and the script"),
    ("P3", 1.25, False, ["R3"], "exactly one component of the class in the scene, on the named host, with panel "
                                "referencing the panel object"),
    ("P4", 0.75, False, ["R4"], "the pause button sits directly under the canvas and the resume button directly "
                                "under the panel, both with a Button component"),
    ("P5", 1.5, False, ["R5"], "each button's onClick has exactly one persistent listener calling TogglePause / "
                               "Resume of the component on the named host, in a state that fires at runtime"),
    ("P6", 0.75, True, ["R6"], "the panel is still inactive, the same-named object, the canvas and the other "
                               "existing objects are unchanged, and no other code file was added"),
    ("P7", 0.5, True, ["G1", "G2"], "no change outside R/A, no other scene saved or opened"),
    ("P8", 0.25, False, ["G4"], "no file other than C# scripts was written under A around the skills"),
    ("P9", 0.5, False, ["R8", "G8"], "the answer reports the script, the object carrying the component and the two "
                                     "buttons as they are"),
    ("P10", 3.0, False, ["R2", "R7"], "in Play Mode no error is logged and timeScale / panel state follow the start, "
                                      "pause, resume, pause, resume, pause, resume sequence (fraction of the seven "
                                      "readings)"),
]

HINTS = {
    "P1": {"zh-CN": "脚本放在 A/Scripts 下、文件名与类名一致；panel 是可序列化的 GameObject 字段；TogglePause() 和 Resume() 都是 public 无参方法。",
           "en": "Put the script under A/Scripts, named after the class; panel is a serialized GameObject field; "
                 "TogglePause() and Resume() are public and parameterless."},
    "P2": {"zh-CN": "等编译完成并确认没有错误后再挂组件和接线。",
           "en": "Wait until compilation finishes without errors before attaching and wiring the component."},
    "P3": {"zh-CN": "组件只挂在题目给出路径的那个对象上（场景里有一个同名对象），panel 指向题目给出的面板对象。",
           "en": "Attach the component to the object at the given path only (another object shares the name) and "
                 "point panel at the given panel object."},
    "P4": {"zh-CN": "用 ui_create_button 新建两个按钮：暂停按钮直接在 Canvas 下，继续按钮直接在面板下。",
           "en": "Create both buttons with ui_create_button: the pause button directly under the Canvas, the resume "
                 "button directly under the panel."},
    "P5": {"zh-CN": "用 event_add_listener 加持久监听，目标用 targetEntityId/targetInstanceId 或 targetPath 指向正确的宿主（按名字会命中同名对象），"
                    "方法分别是 TogglePause 与 Resume，mode 为 RuntimeOnly 或 EditorAndRuntime，每个 onClick 只留一个监听。",
           "en": "Add persistent listeners with event_add_listener, targeting the right host by targetEntityId / "
                 "targetInstanceId or targetPath (a name lookup can hit the same-named object), methods TogglePause "
                 "and Resume, mode RuntimeOnly or EditorAndRuntime, one listener per onClick."},
    "P6": {"zh-CN": "面板在场景里保持未激活；不要改动同名对象、Canvas 和其他对象，也不要新增其他代码文件。",
           "en": "Keep the panel inactive in the scene; leave the same-named object, the Canvas and the other "
                 "objects alone and add no other code file."},
    "P7": {"zh-CN": "所有修改都必须在 R 与 A 之内，且只能保存本轮场景。",
           "en": "Keep every change inside R and A, and save no scene but this run's."},
    "P8": {"zh-CN": "除 C# 脚本外，不要绕过技能直接在 A 下写文件。",
           "en": "Apart from C# scripts, do not write files under A directly; use the skills."},
    "P9": {"zh-CN": "答案里报告脚本路径、实际挂载组件的对象和两个按钮的实际完整路径，并写明 declaredComplete。",
           "en": "Report the script path and the actual full paths of the object carrying the component and of both "
                 "buttons, and declaredComplete."},
    "P10": {"zh-CN": "进入 Play 时必须处于运行状态（timeScale 1、面板未激活）；TogglePause 按当前实际状态在暂停（timeScale 0、面板激活）与运行之间切换，"
                     "Resume 总是恢复运行（之后再 TogglePause 应当暂停）；运行时不能有错误日志。",
            "en": "The game must start running (timeScale 1, panel inactive); TogglePause switches between paused "
                  "(timeScale 0, panel active) and running from the actual state, Resume always returns to running "
                  "(the next TogglePause pauses again); no error may be logged at runtime."},
}


def class_name(script_path):
    return os.path.basename(script_path)[:-len(".cs")]


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


def script_text(ctx):
    content = (_read(ctx, "script_read", scriptPath=ctx.handles["script"]) or {}).get("content")
    return COMMENT_RE.sub("", content) if isinstance(content, str) else None


def declares_class(text, cls):
    return bool(text) and re.search(r"\bpublic\s+class\s+%s\s*:\s*(?:UnityEngine\.)?MonoBehaviour\b" % re.escape(cls),
                                    text) is not None


def declares_field(text, kind, name):
    """A serialized field (public or [SerializeField]) of the given type and name, with or without an initializer."""
    if not text:
        return False
    pattern = r"(?P<attrs>(?:\[[^\]]*\]\s*)*)(?P<mods>(?:(?:public|private|protected|internal)\s+)*)" \
              r"(?:UnityEngine\.)?%s\s+%s\s*(?:=[^;]*)?;" % (re.escape(kind), re.escape(name))
    return any("public" in m.group("mods") or "SerializeField" in m.group("attrs") for m in re.finditer(pattern, text))


def declares_method(text, name):
    return bool(text) and re.search(r"\bpublic\s+void\s+%s\s*\(\s*\)" % re.escape(name), text) is not None


def compiled(ctx):
    """True when no compilation is running and the last one ended without errors."""
    comp = _read(ctx, "debug_check_compilation") or {}
    last = comp.get("lastCompilation") or {}
    return not comp.get("isCompiling") and last.get("succeeded") is not False and not last.get("errorCount")


def compiled_ok(ctx):
    feedback = _read(ctx, "script_get_compile_feedback", scriptPath=ctx.handles["script"]) or {}
    comp = _read(ctx, "debug_check_compilation") or {}
    last = comp.get("lastCompilation") or {}
    ok = compiled(ctx) and feedback.get("hasErrors") is False
    return ok, {"isCompiling": comp.get("isCompiling"), "succeeded": last.get("succeeded"),
                "errorCount": last.get("errorCount"), "scriptHasErrors": feedback.get("hasErrors")}


def holders(ctx, cls):
    found = _read(ctx, "gameobject_find", component=cls, limit=500) or {}
    return [V.object_id(obj) for obj in found.get("objects") or []]


def button_under(ctx, parent_ref, name):
    """The direct child named `name` of `parent_ref` that carries a Button, or None."""
    p = ctx.probe
    for child in p.children(parent_ref):
        if child.get("name") == name and p.component_count(child, "Button") == 1:
            return V.object_id(child)
    return None


def find_button(ctx, name):
    """The only object named `name` below the canvas that carries a Button (any depth), or None."""
    p, found = ctx.probe, []
    stack = list(p.children(ctx.handles["canvas"]))
    while stack:
        child = stack.pop()
        if child.get("name") == name and p.component_count(child, "Button") == 1:
            found.append(V.object_id(child))
        stack.extend(p.children(child))
    return found[0] if len(found) == 1 else None


def listeners(ctx, button_path):
    """Persistent listeners of Button.onClick on the object at button_path, or None when unreadable."""
    res = _read(ctx, "event_get_listeners", path=button_path, componentName="Button", eventName="onClick")
    return [row for row in (res or {}).get("listeners") or [] if isinstance(row, dict)] if res else None


def _bound_to(ctx, row, host):
    """The listener targets the named host: by path, or by an id that resolves to it."""
    host_path = (ctx.probe.go(host) or {}).get("path")
    if host_path and V.norm_path(row.get("targetPath")) == host_path:
        return True
    target = row.get("targetEntityId") or row.get("targetInstanceId")
    owner = V.object_id(ctx.probe.go(target)) if V.object_id(target) is not None else None
    return owner is not None and V.same_id(owner, host)


def wired(ctx, rows, method, cls):
    row = rows[0] if rows and len(rows) == 1 else None
    return bool(row) and row.get("method") == method and row.get("state") in ACTIVE_STATES and \
        str(row.get("targetType") or "").split(".")[-1] == cls and _bound_to(ctx, row, ctx.handles["host"])


def _unchanged(ctx, states):
    """Diffs of C0 object states: the canvas and the panel are compared without their children (the buttons are new
    there), the named host without its components (the new component is expected there)."""
    p, h, diffs = ctx.probe, ctx.handles, []
    for ref, state in states or []:
        now = V.go_state(p, ref)
        before = dict(state or {})
        now = dict(now) if isinstance(now, dict) else now
        drop = ("children",) if any(V.same_id(ref, item) for item in (h["canvas"], h["panel"])) else \
            ("components",) if V.same_id(ref, h["host"]) else ()
        for key in drop:
            before.pop(key, None)
            if isinstance(now, dict):
                now.pop(key, None)
        ok, diff = V.same(before, now)
        if not ok:
            diffs.append({"ref": ref, "diff": diff})
    return diffs


def verify(ctx, card):
    p, h = ctx.probe, ctx.handles
    cls = class_name(h["script"])
    text = script_text(ctx)
    shape = {"declaresClass": declares_class(text, cls), "panelField": declares_field(text, "GameObject", FIELD),
             "togglePause": declares_method(text, METHODS[0]), "resume": declares_method(text, METHODS[1])}
    card.check("P1", all(shape.values()), expected={"class": cls, "field": "GameObject " + FIELD, "methods": METHODS},
               actual=dict(shape, present=text is not None))
    ok, evidence = compiled_ok(ctx)
    card.check("P2", ok, actual=evidence)

    found = holders(ctx, cls)
    count = p.component_count(h["host"], cls)
    ser = p.ser(h["host"], cls) or {}
    ref = V.parse_ref(ser.get(FIELD))
    owner = p.owner(ser.get(FIELD))
    card.check("P3", len(found) == 1 and V.same_id(found[0], h["host"]) and count == 1 and bool(ref) and
               ref[1] == "UnityEngine.GameObject" and V.same_id(owner, h["panel"]),
               expected={"holder": h["hostPath"], "count": 1, FIELD: h["panelPath"]},
               actual={"holders": found, "count": count, FIELD: ser.get(FIELD), "owner": owner})

    placed = {"pause": button_under(ctx, h["canvas"], h["pauseName"]),
              "resume": button_under(ctx, h["panel"], h["resumeName"])}
    card.check("P4", all(value is not None for value in placed.values()),
               expected={"pause": "%s/%s" % (h["canvasPath"], h["pauseName"]),
                         "resume": "%s/%s" % (h["panelPath"], h["resumeName"])},
               actual={"canvasChildren": [c.get("name") for c in p.children(h["canvas"])],
                       "panelChildren": [c.get("name") for c in p.children(h["panel"])]})
    pause, resume = find_button(ctx, h["pauseName"]), find_button(ctx, h["resumeName"])
    pause_path = (p.go(pause) or {}).get("path") if pause is not None else None
    resume_path = (p.go(resume) or {}).get("path") if resume is not None else None
    pause_rows = listeners(ctx, pause_path) if pause_path else None
    resume_rows = listeners(ctx, resume_path) if resume_path else None
    card.check("P5", wired(ctx, pause_rows, METHODS[0], cls) and wired(ctx, resume_rows, METHODS[1], cls),
               expected={"pause": METHODS[0], "resume": METHODS[1], "targetType": cls, "state": list(ACTIVE_STATES)},
               actual={"pause": pause_rows, "resume": resume_rows})

    panel = p.go(h["panel"]) or {}
    diffs = []
    for key in ("canvas", "archive", "systems"):
        diffs += _unchanged(ctx, ctx.c0.get(key))
    added = sorted(set(cs_files(ctx.project_path)) - set(ctx.c0.get("scripts") or []))
    card.check("P6", panel.get("isActive") is False and not diffs and set(added) <= {h["script"]},
               expected={"panelActive": False, "addedScripts": [h["script"]]},
               actual={"panelActive": panel.get("isActive"), "diffs": diffs[:5], "addedScripts": added})

    bad = ctx.violations()
    card.check("P7", not bad, actual={"violations": bad[:10]})
    direct = [rec for rec in ctx.direct_writes() if not SCRIPT_RE.search(str(rec.get("assetPath") or ""))]
    card.check("P8", not direct, actual={"directWrites": direct[:10]})
    carrier = found[0] if len(found) == 1 else h["host"]
    answer_ok = (V.norm_path(V.answer(ctx, "scriptPath")) == h["script"] and
                 V.norm_path(V.answer(ctx, "hostPath")) == (p.go(carrier) or {}).get("path") and
                 pause_path is not None and V.norm_path(V.answer(ctx, "pauseButtonPath")) == pause_path and
                 resume_path is not None and V.norm_path(V.answer(ctx, "resumeButtonPath")) == resume_path and
                 isinstance(V.answer(ctx, "declaredComplete"), bool))
    card.check("P9", answer_ok, actual=ctx.answer)


def _click(ref):
    return {"kind": "invoke", "skill": "component_invoke_method",
            "args": dict({"componentType": "UnityEngine.UI.Button", "methodName": "OnSubmit", "arguments": "[null]"},
                         **V.address(ref))}


def _probe_steps(panel):
    return [{"kind": "wait_frames", "frames": 1},
            {"kind": "read", "skill": "gameobject_get_info", "args": V.address(panel)},
            {"kind": "invoke", "skill": "component_invoke_method",
             "args": {"componentType": "UnityEngine.Time", "methodName": "get_timeScale", "isStatic": True}}]


def SCENARIO(ctx):
    """Play Mode: the initial state, then the six clicks of CLICKS, reading the panel and Time.timeScale after each;
    a missing button's clicks are left out (its readings then fail). The buttons are found by name below the canvas.
    With compile errors Unity refuses Play Mode, so the scenario is then a single edit-mode check that fails at once."""
    h = ctx.handles
    check = {"kind": "assert", "skill": "editor_get_state", "select": "isPlaying", "expected": True}
    if not compiled(ctx):
        return {"executionMode": "edit", "timeoutSeconds": 30, "steps": [check]}
    buttons = {"pause": find_button(ctx, h["pauseName"]), "resume": find_button(ctx, h["resumeName"])}
    steps = [{"kind": "wait_frames", "frames": 2}] + _probe_steps(h["panel"])
    for which in CLICKS:
        if buttons[which] is not None:
            steps.append(_click(buttons[which]))
        steps += _probe_steps(h["panel"])
    steps.append({"kind": "invoke", "skill": "component_invoke_method",
                  "args": {"componentType": "UnityEngine.Time", "methodName": "set_timeScale", "isStatic": True,
                           "parameterTypes": "[\"System.Single\"]", "arguments": "[1]"}})
    steps.append(check)
    return {"executionMode": "play", "timeoutSeconds": 120, "steps": steps}


def readings(steps):
    """[(panel isActive, timeScale)] from each panel read and the get_timeScale invocation that follows it."""
    out = []
    for index, step in enumerate(steps):
        if step.get("kind") != "read":
            continue
        info = step.get("actual") if isinstance(step.get("actual"), dict) else {}
        follow = steps[index + 1] if index + 1 < len(steps) else {}
        scale = follow.get("actual").get("returnValue") if follow.get("kind") == "invoke" and \
            isinstance(follow.get("actual"), dict) else None
        out.append((info.get("isActive"), scale))
    return out


def verify_runtime(ctx, card):
    scenario = ctx.scenario or {}
    seen = readings(scenario.get("steps") or [])
    good = sum(1 for (active, scale), (want_active, want_scale) in zip(seen, SEQUENCE)
               if active is want_active and V.close_tuple(scale, (want_scale,), 1e-3))
    passed = scenario.get("passed") is True
    card.fraction("P10", good / float(len(SEQUENCE)) if passed else 0.0, expected=SEQUENCE,
                  actual={"passed": passed, "error": scenario.get("error"), "status": scenario.get("status"),
                          "readings": seen})

# Producer:Betsy
