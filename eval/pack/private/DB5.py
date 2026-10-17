"""DB5 private verifier: a ClickCounter script, its label reference, the persistent onClick listener and the count it
produces in Play Mode.

What the task probes: the script must compile with the given class, field and method names; exactly one component on
the given button with label pointing at the Text component (not the GameObject); the listener must be persistent
(a runtime AddListener leaves the serialized event empty), bound to the component on the same button, with a call
state that fires at runtime; the other button's event stays empty and the label keeps showing 0 in the scene; no other
code file and no other object change. Play Mode evidence comes from two Button.OnSubmit invocations and a Text read.
"""
import os
import re

from usk_eval import verifylib as V

FIELD = "label"
METHOD = "OnClick"
ACTIVE_STATES = ("RuntimeOnly", "EditorAndRuntime")
TEXT_TYPE = "UnityEngine.UI.Text"
SCRIPT_RE = re.compile(r"\.cs(\.meta)?$")

ASSERTIONS = [
    ("E1", 1.5, False, ["R1"], "the script exists at A/Scripts/<Class>.cs, declares the MonoBehaviour class, a "
                               "serialized Text field label and a public parameterless OnClick()"),
    ("E2", 0.75, False, ["R1"], "compilation finished without errors for the project and the script"),
    ("E3", 1.5, False, ["R3"], "exactly one component of the class in the scene, on the given button, with label "
                               "referencing the Text component of the label object"),
    ("E4", 1.75, False, ["R4"], "the button's onClick has exactly one persistent listener: OnClick of the class on "
                                "that button, in a state that fires at runtime"),
    ("E5", 0.75, True, ["R5"], "the other button's onClick is still empty, the label still shows 0, every other "
                               "object is unchanged and no other code file was added"),
    ("E6", 2.25, False, ["R2", "R6"], "in Play Mode no error is logged and two onClick invocations leave the label "
                                      "at 2"),
    ("E7", 0.5, False, ["R7", "G8"], "the answer reports the script path, the button and the label object"),
    ("E8", 0.5, True, ["G1", "G2"], "no change outside R/A, no other scene saved or opened"),
    ("E9", 0.5, False, ["G4"], "no file other than C# scripts was written under A around the skills"),
]

HINTS = {
    "E1": {"zh-CN": "脚本放在 A/Scripts 下、文件名与类名一致；字段 label 的类型是 UnityEngine.UI.Text 且要序列化；OnClick() 是 public、无参数。",
           "en": "Put the script under A/Scripts with the file named after the class; label is a serialized "
                 "UnityEngine.UI.Text field and OnClick() is public and parameterless."},
    "E2": {"zh-CN": "等编译完成并确认没有错误后再挂组件和接线。",
           "en": "Wait until compilation finishes without errors before attaching and wiring the component."},
    "E3": {"zh-CN": "只挂一个组件到题目给出的按钮；label 要指向标签对象上的 Text 组件（objectType=Text），不是 GameObject。",
           "en": "Attach one component to the given button only; label must reference the Text component of the label "
                 "object (objectType=Text), not the GameObject."},
    "E4": {"zh-CN": "用 event_add_listener 给 Button.onClick 加持久监听：目标是同一按钮上的该组件、方法 OnClick、mode 为 RuntimeOnly 或 EditorAndRuntime；运行时 AddListener 不算。",
           "en": "Add the persistent listener with event_add_listener on Button.onClick: the component on the same "
                 "button, method OnClick, mode RuntimeOnly or EditorAndRuntime; a runtime AddListener does not count."},
    "E5": {"zh-CN": "另一个按钮的 onClick 保持为空，标签在场景里保持 0，不要改动其他对象，也不要新增其他代码文件。",
           "en": "Leave the other button's onClick empty and the label at 0 in the scene; change no other object and "
                 "add no other code file."},
    "E6": {"zh-CN": "OnClick 要把递增后的计数写进 label.text；运行时不得有错误日志（例如 label 为空会抛异常）。",
           "en": "OnClick must write the incremented count into label.text; no error may be logged at runtime (an "
                 "unassigned label throws)."},
    "E7": {"zh-CN": "答案中的路径和 id 要与实际状态一致，并写明 declaredComplete。",
           "en": "Report the actual path and ids, and declaredComplete."},
    "E8": {"zh-CN": "所有修改都必须在 R 与 A 之内，且只能保存本轮场景。",
           "en": "Keep every change inside R and A, and save no scene but this run's."},
    "E9": {"zh-CN": "除 C# 脚本外，不要绕过技能直接在 A 下写文件。",
           "en": "Apart from C# scripts, do not write files under A directly; use the skills."},
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
    return content if isinstance(content, str) else None


def declares_class(text, cls):
    return bool(text) and re.search(r"\bpublic\s+class\s+%s\s*:\s*(?:UnityEngine\.)?MonoBehaviour\b" % re.escape(cls),
                                    text) is not None


def declares_field(text, kind, name):
    """A serialized field (public or [SerializeField]) of the given type and name, with or without an initializer."""
    if not text:
        return False
    pattern = r"(?P<attrs>(?:\[[^\]]*\]\s*)*)(?P<mods>(?:(?:public|private|protected|internal)\s+)*)" \
              r"(?:UnityEngine\.UI\.)?%s\s+%s\s*(?:=[^;]*)?;" % (re.escape(kind), re.escape(name))
    return any("public" in m.group("mods") or "SerializeField" in m.group("attrs") for m in re.finditer(pattern, text))


def declares_method(text, name):
    return bool(text) and re.search(r"\bpublic\s+void\s+%s\s*\(\s*\)" % re.escape(name), text) is not None


def compiled_ok(ctx):
    comp = _read(ctx, "debug_check_compilation") or {}
    last = comp.get("lastCompilation") or {}
    feedback = _read(ctx, "script_get_compile_feedback", scriptPath=ctx.handles["script"]) or {}
    ok = not comp.get("isCompiling") and last.get("succeeded") is not False and not last.get("errorCount") and \
        feedback.get("hasErrors") is False
    return ok, {"isCompiling": comp.get("isCompiling"), "succeeded": last.get("succeeded"),
                "errorCount": last.get("errorCount"), "scriptHasErrors": feedback.get("hasErrors")}


def holders(ctx, cls):
    found = _read(ctx, "gameobject_find", component=cls, limit=500) or {}
    return [V.object_id(obj) for obj in found.get("objects") or []]


def listeners(ctx, button_path):
    """Persistent listeners of Button.onClick on the object at button_path, or None when unreadable."""
    res = _read(ctx, "event_get_listeners", path=button_path, componentName="Button", eventName="onClick")
    return [row for row in (res or {}).get("listeners") or [] if isinstance(row, dict)] if res else None


def label_text(probe, ref):
    return (probe.ser(ref, "Text") or {}).get("m_Text")


def other_states(ctx, holder):
    p, cls = ctx.probe, class_name(ctx.handles["script"])
    diffs = []
    for ref, state in ctx.c0.get("states") or []:
        now = V.go_state(p, ref)
        if V.same_id(ref, holder) and isinstance(now, dict) and isinstance(state, dict):
            now = dict(now, components=[c for c in now.get("components") or [] if c != cls])
        ok, diff = V.same(state, now)
        if not ok:
            diffs.append({"ref": ref, "diff": diff})
    return diffs


def _bound_here(ctx, row, button_path):
    """The listener targets the given button: by path, or by an entityId that resolves to the button."""
    if V.norm_path(row.get("targetPath")) == button_path:
        return True
    target = row.get("targetEntityId") or row.get("targetInstanceId")
    owner = V.object_id(ctx.probe.go(target)) if V.object_id(target) is not None else None
    return owner is not None and V.same_id(owner, ctx.handles["button"])


def verify(ctx, card):
    p, h = ctx.probe, ctx.handles
    cls = class_name(h["script"])
    text = script_text(ctx)
    shape = {"declaresClass": declares_class(text, cls), "labelField": declares_field(text, "Text", FIELD),
             "onClick": declares_method(text, METHOD)}
    card.check("E1", all(shape.values()), expected={"class": cls, "field": "Text " + FIELD, "method": METHOD + "()"},
               actual=dict(shape, present=text is not None))
    ok, evidence = compiled_ok(ctx)
    card.check("E2", ok, actual=evidence)

    found = holders(ctx, cls)
    count = p.component_count(h["button"], cls)
    ser = p.ser(h["button"], cls) or {}
    ref = V.parse_ref(ser.get(FIELD))
    owner = p.owner(ser.get(FIELD))
    card.check("E3", len(found) == 1 and V.same_id(found[0], h["button"]) and count == 1 and bool(ref) and
               ref[1] == TEXT_TYPE and V.same_id(owner, h["label"]),
               expected={"holder": h["button"], "count": 1, FIELD: "%s on %s" % (TEXT_TYPE, h["labelPath"])},
               actual={"holders": found, "count": count, FIELD: ser.get(FIELD), "owner": owner})

    button_path = (p.go(h["button"]) or {}).get("path") or h["buttonPath"]
    rows = listeners(ctx, button_path)
    row = rows[0] if rows and len(rows) == 1 else None
    wired = bool(row) and row.get("method") == METHOD and row.get("state") in ACTIVE_STATES and \
        str(row.get("targetType") or "").split(".")[-1] == cls and _bound_here(ctx, row, button_path)
    card.check("E4", wired, expected={"count": 1, "method": METHOD, "targetType": cls, "state": list(ACTIVE_STATES)},
               actual={"listeners": rows})

    other_path = (p.go(next((ref for ref in h["buttons"] if not V.same_id(ref, h["button"])), None)) or {}).get("path")
    other_rows = listeners(ctx, other_path or h["otherPath"])
    before = dict((path, n) for path, n in ctx.c0.get("listeners") or [])
    other_ok = other_rows is not None and len(other_rows) == (V.as_int(before.get(h["otherPath"])) or 0)
    shown = label_text(p, h["label"])
    diffs = other_states(ctx, h["button"])
    added = sorted(set(cs_files(ctx.project_path)) - set(ctx.c0.get("scripts") or []))
    card.check("E5", other_ok and shown == ctx.c0.get("labelText") and not diffs and
               all(path == h["script"] for path in added),
               expected={"otherListeners": before.get(h["otherPath"]), "label": ctx.c0.get("labelText"),
                         "addedScripts": [h["script"]]},
               actual={"otherListeners": other_rows, "label": shown, "diffs": diffs[:5], "addedScripts": added})

    answer_ok = (V.norm_path(V.answer(ctx, "scriptPath")) == h["script"] and len(found) == 1 and
                 V.same_id(V.answer(ctx, "buttonInstanceId"), found[0]) and
                 V.same_id(V.answer(ctx, "labelInstanceId"), h["label"]) and
                 isinstance(V.answer(ctx, "declaredComplete"), bool))
    card.check("E7", answer_ok, actual=ctx.answer)
    bad = ctx.violations()
    card.check("E8", not bad, actual={"violations": bad[:10]})
    direct = [rec for rec in ctx.direct_writes() if not SCRIPT_RE.search(str(rec.get("assetPath") or ""))]
    card.check("E9", not direct, actual={"directWrites": direct[:10]})


def SCENARIO(ctx):
    """Play Mode: Button.OnSubmit twice (the pointer-free click path, which fires onClick), then the label's text."""
    h = ctx.handles
    click = {"kind": "invoke", "skill": "component_invoke_method",
             "args": dict({"componentType": "Button", "methodName": "OnSubmit", "arguments": "[null]"},
                          **V.address(h["button"]))}
    return {"executionMode": "play", "timeoutSeconds": 90, "steps": [
        {"kind": "wait_frames", "frames": 2},
        dict(click),
        dict(click),
        {"kind": "wait_frames", "frames": 1},
        {"kind": "read", "skill": "component_get_serialized_properties",
         "args": dict({"componentType": "Text"}, **V.address(h["label"]))},
        {"kind": "assert", "skill": "editor_get_state", "select": "isPlaying", "expected": True},
    ]}


def _text(step):
    rows = ((step or {}).get("actual") or {}).get("properties") or []
    return next((row.get("value") for row in rows if isinstance(row, dict) and row.get("propertyPath") == "m_Text"),
                None)


def verify_runtime(ctx, card):
    scenario = ctx.scenario or {}
    read = next((step for step in scenario.get("steps") or [] if step.get("kind") == "read"), None)
    shown = _text(read)
    card.check("E6", scenario.get("passed") is True and str(shown).strip() == "2", expected="2",
               actual={"passed": scenario.get("passed"), "error": scenario.get("error"),
                       "status": scenario.get("status"), "label": shown})

# Producer:Betsy
