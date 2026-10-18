"""T2 private verifier: StartButton, its RectTransform, the EventSystem and the two ordered onClick listeners, then a
click in Play Mode.

What the task probes: DebugCanvas is created first, so ui_create_button without a parent can land on it; the object
R/Archive/MenuCanvas has the name of the canvas asked for but no Canvas; three objects are named Menu and only the
Finish-tagged one (found from the C0 tags) is the first listener's target, while the top-most Menu, which a target
given by name binds, is never that one; listener order, the bool argument and the RuntimeOnly call state; no
EventSystem exists yet. D3 is existential over every label component below the button (eval/design/t2-verifier-fix:
a later matching label counts, an empty first one does not hide it); which component that is, TextMeshProUGUI or
UnityEngine.UI.Text in a project without TextMesh Pro, build recorded in the handles (labelComponent). D6 wants the
input module that fits the project's Active Input Handling, which build recorded too (inputModule):
StandaloneInputModule reads the Input Manager and throws on every frame when only the Input System package is active,
InputSystemUIInputModule is the module of that package. The controller class carries a seeded token plus the run id,
so a run never meets a type compiled by an earlier run. The runtime part enters Play Mode, clicks the button through
Button.Press and reads the three menus and startCount back.
"""
import os
import re

from usk_eval import verifylib as V

CALLS = "m_OnClick.m_PersistentCalls.m_Calls"
CALL_KEYS = ("m_Target", "m_TargetAssemblyTypeName", "m_MethodName", "m_Mode", "m_Arguments.m_BoolArgument",
             "m_CallState")
# On the parameterless onClick, EventDefined (0, what event_add_listener writes) and Void (1) both call StartGame()
MODE_EVENT_DEFINED, MODE_VOID, MODE_BOOL = "0", "1", "6"
STATE_RUNTIME = "2"
BUTTON_TYPE = "UnityEngine.UI.Button"

ASSERTIONS = [
    ("D1", 1.0, False, ["R1", "R5"], "exactly one Button in the scene: StartButton directly under R/UI/MenuCanvas"),
    ("D2", 0.5, False, ["R1"], "StartButton has RectTransform, Image and Button"),
    ("D3", 0.5, False, ["R1"], "a label component below the button, of the type the project's UI skills use "
                               "(TextMeshProUGUI, or UnityEngine.UI.Text without TextMesh Pro), reads \"Start Game\""),
    ("D4", 0.75, False, ["R2"], "anchorMin = anchorMax = (0.5, 0), pivot (0.5, 0)"),
    ("D5", 0.75, False, ["R2"], "anchoredPosition (0, 120), size 320 x 80"),
    ("D6", 1.0, False, ["R3"], "exactly one EventSystem in the scene, at R/UI/EventSystem, with the input module that "
                               "fits the project's Active Input Handling"),
    ("D7", 0.5, False, ["R4"], "onClick has exactly two persistent calls"),
    ("D8", 1.25, False, ["R4"], "call 0: GameObject.SetActive(false) on the Finish-tagged Menu"),
    ("D9", 1.0, False, ["R4"], "call 1: GameController_<k>.StartGame() on R/Systems/GameController, no-argument mode"),
    ("D10", 0.5, False, ["R4"], "both calls RuntimeOnly"),
    ("D11", 0.5, True, ["R5"], "menus, canvases, archive and controller unchanged; still exactly two Canvas objects"),
    ("D12", 0.25, True, ["G1", "G2"], "no change outside R/A, no other scene saved or opened"),
    ("D13", 0.5, False, ["R7", "G8"], "the answer reports the button, the EventSystem and the listeners as they are"),
    ("D14", 0.75, False, ["R6"], "in Play Mode a click deactivates only the Finish-tagged Menu and sets startCount "
                                 "to 1, with no error logged"),
    ("D15", 0.25, False, ["G4"], "no file other than C# scripts was written under A around the skills"),
]

HINTS = {
    "D1": {"zh-CN": "StartButton 必须直接放在 R/UI/MenuCanvas（带 Canvas 的那个）下，场景中不能有别的 Button；不带 parent 的"
                    "ui_create_button 不一定落在这里，R/Archive 下的同名对象也不是 Canvas。",
           "en": "StartButton must sit directly under R/UI/MenuCanvas (the one with a Canvas) and be the only Button; "
                 "ui_create_button without a parent may land elsewhere, and the same-name object under R/Archive is "
                 "not a Canvas."},
    "D2": {"zh-CN": "按钮需要 RectTransform、Image 和 Button 组件。", "en": "The button needs RectTransform, Image and Button."},
    "D3": {"zh-CN": "按钮之下要有一个提示里点名的文字组件（工程带 TextMesh Pro 时是 TextMeshProUGUI，否则是 "
                    "UnityEngine.UI.Text），文字为 Start Game。",
           "en": "A text component of the type the prompt names (TextMeshProUGUI where TextMesh Pro is installed, "
                 "UnityEngine.UI.Text otherwise) below the button must read Start Game."},
    "D4": {"zh-CN": "anchorMin、anchorMax 和 pivot 都应为 (0.5, 0)。",
           "en": "anchorMin, anchorMax and pivot must all be (0.5, 0)."},
    "D5": {"zh-CN": "anchoredPosition 应为 (0, 120)，sizeDelta 应为 (320, 80)。",
           "en": "anchoredPosition must be (0, 120) and sizeDelta (320, 80)."},
    "D6": {"zh-CN": "在 R/UI 下建一个 EventSystem 对象，挂 EventSystem 和与工程 Active Input Handling 相匹配的输入模块"
                    "（Input Manager 或 Both 用 StandaloneInputModule，只启用 Input System 包时用 InputSystemUIInputModule），"
                    "且全场景只有这一个 EventSystem。",
           "en": "Create one EventSystem object under R/UI with the EventSystem component and the input module that "
                 "fits the project's Active Input Handling (StandaloneInputModule for Input Manager or Both, "
                 "InputSystemUIInputModule when only the Input System package is active), and no other "
                 "EventSystem."},
    "D7": {"zh-CN": "onClick 恰好要有两条持久监听。", "en": "onClick needs exactly two persistent listeners."},
    "D8": {"zh-CN": "第一条监听要指向标签为 Finish 的 Menu（三个 Menu 同名，按名字找会找到别的），调用 SetActive，参数为 false。",
           "en": "The first listener must target the Menu tagged Finish (three Menus share the name, so a lookup by "
                 "name finds another one) and call SetActive with false."},
    "D9": {"zh-CN": "第二条监听要调用 GameController 上该组件的 StartGame()。",
           "en": "The second listener must call StartGame() on that component of GameController."},
    "D10": {"zh-CN": "两条监听的调用状态都应为 RuntimeOnly。", "en": "Both listeners must be RuntimeOnly."},
    "D11": {"zh-CN": "不要改动 Menu、Canvas、Archive 和 GameController，也不要新建 Canvas。",
            "en": "Leave the Menus, the canvases, Archive and GameController alone, and create no Canvas."},
    "D12": {"zh-CN": "所有修改都必须在 R 与 A 之内，且只能保存本轮场景。",
            "en": "Keep every change inside R and A, and save no scene but this run's."},
    "D13": {"zh-CN": "答案中的 id、监听目标和方法名要与实际状态一致，并写明 declaredComplete。",
            "en": "Report the actual ids, listener targets and method names, and declaredComplete."},
    "D14": {"zh-CN": "验收会进入 Play 模式点击按钮：只有 Finish 标签的 Menu 被停用，startCount 变为 1，且不能有错误日志。",
            "en": "Verification enters Play Mode and clicks the button: only the Finish-tagged Menu may turn inactive, "
                  "startCount must become 1 and no error may be logged."},
    "D15": {"zh-CN": "除 C# 脚本外，不要绕过技能直接在 A 下写文件。",
            "en": "Apart from C# scripts, do not write files under A directly; use the skills."},
}


def class_name(handles):
    return os.path.basename(handles["controllerScript"])[:-len(".cs")]


def fixed_state(probe, ref, children=True, local=True):
    """The public snapshot's state form: go_state without world position and rotation, plus local TRS."""
    state = V.go_state(probe, ref, with_children=children)
    if state is None:
        return None
    for key in ("position", "rotation"):
        state.pop(key, None)
    if local:
        state["local"] = V.local_trs(probe, ref)
    return state


def canvas(c0, name):
    """Id of the fixture canvas called `name` (DebugCanvas or MenuCanvas)."""
    return next((ref for ref, state in c0.get("canvases") or [] if (state or {}).get("name") == name), None)


def finish_menu(c0, handles):
    """Id of the Menu tagged Finish in C0 (R4)."""
    states = [(ref, state) for ref, state in c0.get("objects") or []
              if any(V.same_id(ref, menu) for menu in handles["menus"])]
    hits = [ref for ref, state in states if (state or {}).get("tag") == "Finish"]
    return hits[0] if len(hits) == 1 else None


def _read(ctx, skill, **args):
    """Result of a read-only skill, or None when it answers with a business error."""
    envelope = ctx.probe.c.read_raw(skill, **args)
    if not isinstance(envelope, dict) or envelope.get("status") in ("error", "partial") or envelope.get("errorCode"):
        return None
    result = envelope.get("result", envelope)
    return result if isinstance(result, dict) and result.get("success") is not False else None


def find_all(ctx, component):
    found = _read(ctx, "gameobject_find", component=component, limit=500) or {}
    return [V.object_id(obj) for obj in found.get("objects") or []]


def graded_button(ctx, c0, buttons=None):
    """The StartButton directly under MenuCanvas, else any Button named StartButton, else None."""
    p = ctx.probe
    under = [V.object_id(child) for child in p.children(canvas(c0, "MenuCanvas")) if child.get("name") == "StartButton"]
    if under:
        return under[0]
    buttons = find_all(ctx, "Button") if buttons is None else buttons
    return next((ref for ref in buttons if (p.go(ref) or {}).get("name") == "StartButton"), None)


# label component -> (its name in a components list, its serialized text property)
LABELS = {"TextMeshProUGUI": ("TextMeshProUGUI", "m_text"), "UnityEngine.UI.Text": ("Text", "m_Text")}


def label_texts(probe, ref, component, depth=0):
    """The text of every `component` (a key of LABELS) at or below `ref`, in hierarchy order."""
    name, field = LABELS[component]
    info = probe.go(ref) or {}
    out = []
    if name in (info.get("components") or []):
        out.append((probe.ser(ref, component) or {}).get(field))
    if depth < 32:
        for child in info.get("children") or []:
            out += label_texts(probe, V.object_id(child), component, depth + 1)
    return out


def listener_calls(ser):
    """Persistent onClick calls read from the Button's serialized properties: one dict per call, in order."""
    size = V.as_int(ser.get(CALLS + ".Array.size")) or 0
    return [{key: ser.get("%s.Array.data[%d].%s" % (CALLS, index, key)) for key in CALL_KEYS}
            for index in range(max(0, min(size, 64)))]


def verify(ctx, card):
    p, h, c0 = ctx.probe, ctx.handles, ctx.c0
    cls = class_name(h)
    buttons = find_all(ctx, "Button")
    under = [V.object_id(child) for child in p.children(canvas(c0, "MenuCanvas")) if child.get("name") == "StartButton"]
    button = graded_button(ctx, c0, buttons)
    card.check("D1", len(buttons) == 1 and len(under) == 1 and V.same_id(buttons[0], under[0]),
               expected=h["menuCanvasPath"] + "/StartButton",
               actual={"buttons": [(p.go(ref) or {}).get("path") for ref in buttons]})
    comps = ((p.go(button) or {}) if button is not None else {}).get("components") or []
    card.check("D2", all(kind in comps for kind in ("RectTransform", "Image", "Button")), actual=comps)
    label = h.get("labelComponent")
    texts = label_texts(p, button, label) if button is not None and label in LABELS else []
    card.check("D3", "Start Game" in texts, expected={"component": label, "text": "Start Game"}, actual=texts)

    rect = (p.ser(button, "RectTransform") or {}) if button is not None else {}
    card.check("D4", all(V.close_tuple(rect.get(key), (0.5, 0)) for key in ("m_AnchorMin", "m_AnchorMax", "m_Pivot")),
               expected=(0.5, 0), actual={key: rect.get(key) for key in ("m_AnchorMin", "m_AnchorMax", "m_Pivot")})
    card.check("D5", V.close_tuple(rect.get("m_AnchoredPosition"), (0, 120)) and
               V.close_tuple(rect.get("m_SizeDelta"), (320, 80)),
               expected={"m_AnchoredPosition": (0, 120), "m_SizeDelta": (320, 80)},
               actual={key: rect.get(key) for key in ("m_AnchoredPosition", "m_SizeDelta")})

    systems = find_all(ctx, "EventSystem")
    system = p.go(systems[0]) if len(systems) == 1 else None
    module = h.get("inputModule")
    card.check("D6", bool(system) and system.get("path") == h["uiPath"] + "/EventSystem" and bool(module) and
               module in (system.get("components") or []),
               expected={"path": h["uiPath"] + "/EventSystem", "inputModule": module},
               actual={"eventSystems": [(p.go(ref) or {}).get("path") for ref in systems],
                       "components": (system or {}).get("components")})

    ser = (p.ser(button, "Button") or {}) if button is not None else {}
    calls = listener_calls(ser)
    card.check("D7", len(calls) == 2, expected=2, actual=ser.get(CALLS + ".Array.size"))
    first = calls[0] if calls else {}
    first_ref = V.parse_ref(first.get("m_Target"))
    finish = finish_menu(c0, h)
    card.check("D8", bool(first_ref) and V.same_id(first_ref[0], finish) and first_ref[1] == "UnityEngine.GameObject"
               and first.get("m_MethodName") == "SetActive" and str(first.get("m_Mode")) == MODE_BOOL and
               str(first.get("m_Arguments.m_BoolArgument")).lower() == "false", expected=finish, actual=first)
    second = calls[1] if len(calls) > 1 else {}
    second_ref = V.parse_ref(second.get("m_Target"))
    owner = p.owner(second.get("m_Target"))
    card.check("D9", bool(second_ref) and second_ref[1] == cls and V.same_id(owner, h["controller"]) and
               second.get("m_MethodName") == "StartGame" and
               str(second.get("m_Mode")) in (MODE_EVENT_DEFINED, MODE_VOID),
               expected=h["controller"], actual=dict(second, owner=owner))
    states = [call.get("m_CallState") for call in calls[:2]]
    card.check("D10", [str(state) for state in states] == [STATE_RUNTIME, STATE_RUNTIME], actual=states)

    changed = [str(ref) for ref, state in c0.get("objects") or [] if not V.same(state, fixed_state(p, ref))[0]]
    canvases = [str(ref) for ref, state in c0.get("canvases") or []
                if not V.same(state, fixed_state(p, ref, children=False, local=False))[0]]
    now = [V.object_id(child) for child in p.children(canvas(c0, "DebugCanvas"))]
    before = c0.get("debugChildren") or []
    added = [ref for ref in now if not any(V.same_id(ref, old) for old in before)]
    removed = [old for old in before if not any(V.same_id(ref, old) for ref in now)]
    count = len(find_all(ctx, "Canvas"))
    controller_same = V.same(c0.get("controller"), p.ser(h["controller"], cls))[0]
    card.check("D11", not changed and not canvases and not removed and count == 2 and controller_same and
               all(any(V.same_id(ref, other) for other in buttons) for ref in added),
               actual={"changed": changed, "canvasesChanged": canvases, "debugCanvasAdded": added,
                       "debugCanvasRemoved": removed, "canvasCount": count, "controllerSame": controller_same})

    bad = ctx.violations()
    card.check("D12", not bad, actual={"violations": bad[:10]})

    reported = V.answer(ctx, "listeners")
    owners = [p.owner(call.get("m_Target")) for call in calls]
    answer_ok = (button is not None and V.same_id(V.answer(ctx, "buttonInstanceId"), button) and
                 any(V.same_id(V.answer(ctx, "eventSystemInstanceId"), ref) for ref in systems) and
                 isinstance(reported, list) and len(reported) == len(calls) and
                 all(isinstance(item, dict) and V.same_id(item.get("gameObjectInstanceId"), holder) and
                     item.get("method") == call.get("m_MethodName")
                     for item, holder, call in zip(reported, owners, calls)) and
                 isinstance(V.answer(ctx, "declaredComplete"), bool))
    card.check("D13", answer_ok, actual=ctx.answer)

    direct = [rec for rec in ctx.direct_writes()
              if not re.search(r"\.cs(\.meta)?$", str(rec.get("assetPath") or ""))]
    card.check("D15", not direct, actual={"directWrites": direct[:10]})


def SCENARIO(ctx):
    """Play Mode: click the graded button once (Button.Press runs onClick), then read the menus and the controller."""
    h = ctx.handles
    button = graded_button(ctx, ctx.c0)
    steps = [{"kind": "wait_frames", "frames": 2}]
    if button is not None:
        steps += [{"kind": "invoke", "skill": "component_invoke_method",
                   "args": dict({"componentType": BUTTON_TYPE, "methodName": "Press", "executionMode": "play"},
                                **V.address(button))},
                  {"kind": "wait_frames", "frames": 1}]
    steps += [{"kind": "read", "skill": "gameobject_get_info", "args": V.address(ref)} for ref in h["menus"]]
    steps += [{"kind": "read", "skill": "component_get_serialized_properties",
               "args": dict({"componentType": class_name(h)}, **V.address(h["controller"]))},
              {"kind": "assert", "skill": "editor_get_state", "select": "isPlaying", "expected": True}]
    return {"executionMode": "play", "timeoutSeconds": 90, "steps": steps}


def verify_runtime(ctx, card):
    scenario = ctx.scenario or {}
    h = ctx.handles
    steps = scenario.get("steps") or []
    clicked = any(step.get("kind") == "invoke" and step.get("passed") is True for step in steps)
    reads = [step.get("actual") for step in steps
             if step.get("kind") == "read" and isinstance(step.get("actual"), dict)]
    finish = finish_menu(ctx.c0, h)
    active = {}
    for ref in h["menus"]:
        row = next((read for read in reads if "isActive" in read and V.same_id(read, ref)), None)
        active[str(ref)] = row.get("isActive") if row else None
    menus_ok = finish is not None and all(active[str(ref)] is (not V.same_id(ref, finish)) for ref in h["menus"])
    props = next(({row.get("propertyPath"): row.get("value") for row in read.get("properties") or []
                   if isinstance(row, dict)} for read in reads if "properties" in read), {})
    count = V.as_int(props.get("startCount"))
    card.check("D14", scenario.get("passed") is True and clicked and menus_ok and count == 1,
               expected={"finishMenuActive": False, "otherMenusActive": True, "startCount": 1},
               actual={"passed": scenario.get("passed"), "error": scenario.get("error"),
                       "status": scenario.get("status"), "clicked": clicked, "menusActive": active,
                       "startCount": props.get("startCount")})

# Producer:Betsy
