"""T2 UGUI start button wired to a scene object and a compiled controller (composite).

Traps: a decoy canvas created first (ui_create_button without a parent lands there), a same-name
MenuCanvas without a Canvas elsewhere, three same-name Menu objects of which only the Finish-tagged one
is the listener target (event_add_listener resolves by name), call order, bool argument, call state, and
no EventSystem in the scene yet. The controller class carries a seeded token plus the run id so a run
never meets a type compiled by an earlier run.
"""
import json

from harness import checks as K
from harness.task import Negative, Task
from harness.verify import parse_ref

WORDS = ("Aster", "Brine", "Cobalt", "Delta", "Fjord", "Garnet")
CALLS = "m_OnClick.m_PersistentCalls.m_Calls"
# On the parameterless onClick, EventDefined (0, what event_add_listener writes) and Void (1) both call StartGame()
MODE_EVENT_DEFINED, MODE_VOID, MODE_BOOL = "0", "1", "6"
STATE_RUNTIME = "2"

CONTROLLER_SRC = """using UnityEngine;

public class {cls} : MonoBehaviour
{{
    public int startCount;

    public void StartGame()
    {{
        startCount++;
    }}
}}
"""


class T2(Task):
    id = "T2"
    title = "UGUI start button with ordered persistent listeners"
    tier = "composite"
    timeout_s = 2400
    budget_usd = 40.0
    needs_focus = True

    def assertions(self):
        return [
            ("D1", 1.0, False, "exactly one Button in the scene: StartButton directly under R/UI/MenuCanvas"),
            ("D2", 0.5, False, "StartButton has RectTransform, Image and Button"),
            ("D3", 0.5, False, "a TextMeshProUGUI under the button reads \"Start Game\""),
            ("D4", 1.0, False, "anchorMin = anchorMax = (0.5, 0), pivot (0.5, 0)"),
            ("D5", 1.0, False, "anchoredPosition (0, 120), size 320 x 80"),
            ("D6", 1.0, False, "exactly one EventSystem in the scene, at R/UI/EventSystem with StandaloneInputModule"),
            ("D7", 0.5, False, "onClick has exactly two persistent calls"),
            ("D8", 1.5, False, "call 0: Finish-tagged Menu GameObject.SetActive(false)"),
            ("D9", 1.0, False, "call 1: GameController_<k>.StartGame() on R/Systems/GameController, no-argument mode"),
            ("D10", 0.5, False, "both calls RuntimeOnly"),
            ("D11", 0.5, True, "menus, canvases, archive and controller unchanged; still exactly two Canvas objects"),
            ("D12", 0.5, True, "no side effects outside R/A and no scene save"),
            ("D13", 0.5, False, "answer fields match the actual state"),
        ]

    def allowed_change(self, ctx, rec):
        path = rec.get("assetPath") or ""
        return rec.get("kind") == "imported" and rec.get("source") == "editor" and \
            path.startswith(ctx.A + "/") and (path.endswith(".cs") or path.endswith(".cs.meta"))

    # ------------------------------------------------------------------ fixture

    def build(self, ctx):
        c, rng = ctx.c, ctx.rng
        k = "%s_%s" % (rng.choice(WORDS), ctx.scope.run_id)
        cls = "GameController_" + k
        res = c.write("script_create", scriptName=cls, folder=ctx.asset("Scripts"),
                      content=CONTROLLER_SRC.format(cls=cls))
        if res.get("jobId"):
            c.wait_job(res["jobId"])
        c.wait_idle()

        root = ctx.scope.root_iid
        ui = c.write("gameobject_create", name="UI", parentInstanceId=root)["instanceId"]
        canvases = {}
        for name in ("DebugCanvas", "MenuCanvas"):
            iid = c.write("ui_create_canvas", name=name)["instanceId"]
            c.write("gameobject_set_parent", childInstanceId=iid, parentInstanceId=ui)
            canvases[name] = iid
        archive = c.write("gameobject_create", name="Archive", parentInstanceId=root)["instanceId"]
        archive_canvas = c.write("gameobject_create", name="MenuCanvas", parentInstanceId=archive)["instanceId"]
        menu_parents = [("MenuCanvas", canvases["MenuCanvas"], "Finish"),
                        ("DebugCanvas", canvases["DebugCanvas"], None),
                        ("Archive", archive_canvas, "Player")]
        rng.shuffle(menu_parents)
        menus = {}
        for key, parent, tag in menu_parents:
            iid = c.write("gameobject_create", name="Menu", parentInstanceId=parent)["instanceId"]
            if tag:
                c.write("gameobject_set_tag_batch", items=json.dumps([{"instanceId": iid, "tag": tag}]))
            menus[key] = iid
        systems = c.write("gameobject_create", name="Systems", parentInstanceId=root)["instanceId"]
        gc = c.write("gameobject_create", name="GameController", parentInstanceId=systems)["instanceId"]
        c.write("component_add", instanceId=gc, componentType=cls)
        ctx.facts.update({
            "k": k, "controllerClass": cls, "uiIid": ui, "canvasIids": canvases, "archiveIid": archive,
            "archiveCanvasIid": archive_canvas, "menuIids": menus, "finishMenuIid": menus["MenuCanvas"],
            "systemsIid": systems, "gcIid": gc, "menuCanvasPath": ctx.path("UI", "MenuCanvas"),
            "uiPath": ctx.path("UI"), "gcPath": ctx.path("Systems", "GameController"),
        })

    def after_fixture(self, ctx):
        p, f = ctx.probe, ctx.facts
        ids = list(f["menuIids"].values()) + [f["archiveIid"], f["archiveCanvasIid"], f["gcIid"]]
        f["fixedC0"] = {str(i): K.go_state(p, i) for i in ids}
        f["canvasC0"] = {str(i): K.go_state(p, i, with_children=False) for i in f["canvasIids"].values()}
        f["debugChildrenC0"] = [ch["instanceId"] for ch in p.children(f["canvasIids"]["DebugCanvas"])]
        f["gcSerC0"] = p.ser(f["gcIid"], f["controllerClass"])

    # ------------------------------------------------------------------ prompt

    def prompt(self, ctx):
        f = ctx.facts
        return ("在 `{canvas}` 下创建 UGUI 按钮 `StartButton`，按钮文字用 TextMeshPro，内容为 `Start Game`；"
                "RectTransform：anchorMin 与 anchorMax 都是 (0.5, 0)，pivot (0.5, 0)，anchoredPosition (0, 120)，"
                "尺寸 320×80。\n"
                "场景里还没有 EventSystem：在 `{ui}` 下创建名为 `EventSystem` 的对象，挂 EventSystem 和 "
                "StandaloneInputModule。\n"
                "按钮 onClick 的持久监听按顺序为：① 标签为 Finish 的那个 `Menu` 对象的 GameObject.SetActive(false)；"
                "② `{gc}` 上 `{cls}` 组件的 StartGame()。两条监听都只在运行时调用（RuntimeOnly）。\n"
                "不要修改其他对象和资产。").format(canvas=f["menuCanvasPath"], ui=f["uiPath"], gc=f["gcPath"],
                                           cls=f["controllerClass"])

    def answer_schema(self):
        return ('{"buttonInstanceId": 整数, "eventSystemInstanceId": 整数, '
                '"listeners": [{"targetInstanceId": 目标 GameObject 的 instanceId, "method": "方法名"}], '
                '"declaredComplete": true 或 false}')

    # ------------------------------------------------------------------ verification

    def _find(self, ctx, component):
        try:
            res = ctx.c.read("gameobject_find", component=component, limit=500)
        except Exception:
            return []
        return [o.get("instanceId") for o in res.get("objects") or []]

    def _text(self, ctx, iid, depth=0):
        info = ctx.probe.go(iid) or {}
        if "TextMeshProUGUI" in (info.get("components") or []):
            return (ctx.probe.ser(iid, "TextMeshProUGUI") or {}).get("m_text")
        if depth < 2:
            for ch in info.get("children") or []:
                text = self._text(ctx, ch["instanceId"], depth + 1)
                if text is not None:
                    return text
        return None

    def verify(self, ctx, card):
        p, f = ctx.probe, ctx.facts
        buttons = self._find(ctx, "Button")
        menu_canvas = f["canvasIids"]["MenuCanvas"]
        under_menu = [ch["instanceId"] for ch in p.children(menu_canvas) if ch.get("name") == "StartButton"]
        named = [b for b in buttons if (p.go(b) or {}).get("name") == "StartButton"]
        button = under_menu[0] if under_menu else (named[0] if named else None)
        info = p.go(button) if button else {}
        info = info or {}
        card.check("D1", len(buttons) == 1 and buttons == under_menu,
                   expected=f["menuCanvasPath"] + "/StartButton",
                   actual={"buttons": [(p.go(b) or {}).get("path") for b in buttons]})
        comps = info.get("components") or []
        card.check("D2", all(t in comps for t in ("RectTransform", "Image", "Button")), actual=comps)
        text = self._text(ctx, button) if button else None
        card.check("D3", text == "Start Game", expected="Start Game", actual=text)

        rect = p.ser(button, "RectTransform") if button else {}
        rect = rect or {}
        anchors_ok = (K.close_tuple(rect.get("m_AnchorMin"), (0.5, 0)) and
                      K.close_tuple(rect.get("m_AnchorMax"), (0.5, 0)) and K.close_tuple(rect.get("m_Pivot"), (0.5, 0)))
        card.check("D4", anchors_ok,
                   actual={k: rect.get(k) for k in ("m_AnchorMin", "m_AnchorMax", "m_Pivot")})
        card.check("D5", K.close_tuple(rect.get("m_AnchoredPosition"), (0, 120)) and
                   K.close_tuple(rect.get("m_SizeDelta"), (320, 80)),
                   actual={k: rect.get(k) for k in ("m_AnchoredPosition", "m_SizeDelta")})

        systems = self._find(ctx, "EventSystem")
        es = p.go(systems[0]) if len(systems) == 1 else None
        card.check("D6", bool(es) and es.get("path") == f["uiPath"] + "/EventSystem" and
                   "StandaloneInputModule" in (es.get("components") or []),
                   actual={"eventSystems": [(p.go(s) or {}).get("path") for s in systems],
                           "components": (es or {}).get("components")})

        ser = p.ser(button, "Button") if button else {}
        ser = ser or {}

        def call(i, key):
            return ser.get("%s.Array.data[%d].%s" % (CALLS, i, key))

        size = ser.get(CALLS + ".Array.size")
        card.check("D7", str(size) == "2", expected=2, actual=size)
        t0 = parse_ref(call(0, "m_Target"))
        call0 = {"target": call(0, "m_Target"), "method": call(0, "m_MethodName"), "mode": call(0, "m_Mode"),
                 "bool": call(0, "m_Arguments.m_BoolArgument")}
        card.check("D8", bool(t0) and t0[0] == f["finishMenuIid"] and t0[1] == "UnityEngine.GameObject" and
                   call0["method"] == "SetActive" and str(call0["mode"]) == MODE_BOOL and
                   str(call0["bool"]).lower() == "false", expected=f["finishMenuIid"], actual=call0)
        t1 = parse_ref(call(1, "m_Target"))
        call1 = {"target": call(1, "m_Target"), "method": call(1, "m_MethodName"), "mode": call(1, "m_Mode"),
                 "owner": p.owner(call(1, "m_Target"))}
        card.check("D9", bool(t1) and t1[1] == f["controllerClass"] and call1["owner"] == f["gcIid"] and
                   call1["method"] == "StartGame" and str(call1["mode"]) in (MODE_EVENT_DEFINED, MODE_VOID),
                   expected=f["gcIid"], actual=call1)
        states = [call(i, "m_CallState") for i in range(2)]
        card.check("D10", [str(s) for s in states] == [STATE_RUNTIME, STATE_RUNTIME], actual=states)

        fixed = [i for i, s in f["fixedC0"].items() if not K.same(s, K.go_state(p, int(i)))[0]]
        canv = [i for i, s in f["canvasC0"].items() if not K.same(s, K.go_state(p, int(i), with_children=False))[0]]
        debug_kids = [ch["instanceId"] for ch in p.children(f["canvasIids"]["DebugCanvas"])]
        extra_debug = [i for i in debug_kids if i not in f["debugChildrenC0"]]
        missing_debug = [i for i in f["debugChildrenC0"] if i not in debug_kids]
        canvases = self._find(ctx, "Canvas")
        gc_ok = K.same(f["gcSerC0"], p.ser(f["gcIid"], f["controllerClass"]))[0]
        card.check("D11", not fixed and not canv and not missing_debug and
                   all(i in buttons for i in extra_debug) and len(canvases) == 2 and gc_ok,
                   actual={"changed": fixed, "canvasChanged": canv, "debugCanvasAdded": extra_debug,
                           "debugCanvasRemoved": missing_debug, "canvasCount": len(canvases), "controllerSame": gc_ok})
        bad = ctx.violations(self)
        saves = ctx.saved_scenes()
        card.check("D12", not bad and not saves, actual={"violations": bad[:10], "sceneSaves": saves[:5]})

        listeners = K.answer(ctx, "listeners") or []
        actual_targets = [p.owner(call(i, "m_Target")) for i in range(int(size or 0))]
        actual_methods = [call(i, "m_MethodName") for i in range(int(size or 0))]
        ans_ok = (K.as_int(K.answer(ctx, "buttonInstanceId")) == button and button is not None and
                  K.as_int(K.answer(ctx, "eventSystemInstanceId")) in systems and
                  isinstance(listeners, list) and len(listeners) == len(actual_targets) and
                  all(isinstance(l, dict) and K.as_int(l.get("targetInstanceId")) == t and l.get("method") == m
                      for l, t, m in zip(listeners, actual_targets, actual_methods)) and
                  K.answer(ctx, "declaredComplete") in (True, False))
        card.check("D13", ans_ok, actual=ctx.answer)

    # ------------------------------------------------------------------ solutions

    def _button(self, ctx, parent=True, width=320, height=80):
        f, c = ctx.facts, ctx.c
        iid = c.write("ui_create_button", name="StartButton", text="Start Game", width=width, height=height)["instanceId"]
        if parent:
            c.write("gameobject_set_parent", childInstanceId=iid, parentInstanceId=f["canvasIids"]["MenuCanvas"])
        else:
            c.write("gameobject_set_parent", childInstanceId=iid, parentInstanceId=f["canvasIids"]["DebugCanvas"])
        c.write("ui_set_rect_transform", instanceId=iid, anchorMinX=0.5, anchorMinY=0, anchorMaxX=0.5, anchorMaxY=0,
                pivotX=0.5, pivotY=0, anchoredPosX=0, anchoredPosY=120, sizeDeltaX=width, sizeDeltaY=height)
        return iid

    def _event_system(self, ctx, name="EventSystem", parent=None):
        c = ctx.c
        iid = c.write("gameobject_create", name=name, parentInstanceId=parent or ctx.facts["uiIid"])["instanceId"]
        c.write("component_add", instanceId=iid, componentType="EventSystem")
        c.write("component_add", instanceId=iid, componentType="StandaloneInputModule")
        return iid

    def _add_menu_call(self, ctx, button, menu, active=False, mode="RuntimeOnly"):
        c = ctx.c
        res = c.write("event_add_listener", instanceId=button, componentName="Button", eventName="onClick",
                      targetObjectName="Menu", targetComponentName="GameObject",
                      methodName="SetActive", mode=mode, argType="bool", boolArg=active)
        c.write("event_set_listener", instanceId=button, componentName="Button", eventName="onClick",
                index=int(res.get("index", 0)), targetName="Menu", targetInstanceId=menu, methodName="SetActive",
                mode=mode, argType="bool", boolArg=active)

    def _add_start_call(self, ctx, button, mode="RuntimeOnly"):
        f = ctx.facts
        ctx.c.write("event_add_listener", instanceId=button, componentName="Button", eventName="onClick",
                    targetObjectName="GameController", targetComponentName=f["controllerClass"],
                    methodName="StartGame", mode=mode)

    def _answer(self, ctx, button, es):
        p = ctx.probe
        ser = p.ser(button, "Button") or {}
        size = int(ser.get(CALLS + ".Array.size") or 0)
        listeners = [{"targetInstanceId": p.owner(ser.get("%s.Array.data[%d].m_Target" % (CALLS, i))),
                      "method": ser.get("%s.Array.data[%d].m_MethodName" % (CALLS, i))} for i in range(size)]
        return {"buttonInstanceId": button, "eventSystemInstanceId": es, "listeners": listeners,
                "declaredComplete": True}

    def _solve(self, ctx, menu=None, order=("menu", "start"), active=False, mode="RuntimeOnly", parent=True,
               width=320, height=80):
        f = ctx.facts
        button = self._button(ctx, parent=parent, width=width, height=height)
        es = self._event_system(ctx)
        for step in order:
            if step == "menu":
                self._add_menu_call(ctx, button, menu or f["finishMenuIid"], active=active, mode=mode)
            else:
                self._add_start_call(ctx, button, mode=mode)
        return self._answer(ctx, button, es)

    def reference(self, ctx):
        return self._solve(ctx)

    def negatives(self):
        def debug_canvas(ctx):
            return self._solve(ctx, parent=False)

        def wrong_menu(ctx):
            return self._solve(ctx, menu=ctx.facts["menuIids"]["DebugCanvas"])

        def order_swapped(ctx):
            return self._solve(ctx, order=("start", "menu"))

        def set_active_true(ctx):
            return self._solve(ctx, active=True)

        def two_event_systems(ctx):
            ans = self._solve(ctx)
            self._event_system(ctx, parent=ctx.scope.root_iid)
            return ans

        def default_size(ctx):
            return self._solve(ctx, width=160, height=30)

        def editor_and_runtime(ctx):
            return self._solve(ctx, mode="EditorAndRuntime")

        return [
            Negative("debug_canvas", debug_canvas, {"D1"}),
            Negative("wrong_menu", wrong_menu, {"D8"}),
            Negative("order_swapped", order_swapped, {"D8", "D9"}),
            Negative("set_active_true", set_active_true, {"D8"}),
            Negative("two_event_systems", two_event_systems, {"D6"}),
            Negative("default_size", default_size, {"D5"}),
            Negative("editor_and_runtime", editor_and_runtime, {"D10"}),
        ]

    def alternatives(self):
        def serialized_calls(ctx):
            f, c = ctx.facts, ctx.c
            button = self._button(ctx)
            es = self._event_system(ctx)
            base = dict(instanceId=button, componentType="Button")

            def put(path, **kw):
                c.write("component_set_serialized_property", propertyPath=path, **dict(base, **kw))

            put(CALLS + ".Array.size", value="2")
            put(CALLS + ".Array.data[0].m_Target", referenceInstanceId=f["finishMenuIid"])
            put(CALLS + ".Array.data[0].m_TargetAssemblyTypeName", value="UnityEngine.GameObject, UnityEngine.CoreModule")
            put(CALLS + ".Array.data[0].m_MethodName", value="SetActive")
            put(CALLS + ".Array.data[0].m_Mode", value=MODE_BOOL)
            put(CALLS + ".Array.data[0].m_Arguments.m_BoolArgument", value="false")
            put(CALLS + ".Array.data[0].m_CallState", value=STATE_RUNTIME)
            put(CALLS + ".Array.data[1].m_Target", referenceInstanceId=f["gcIid"], objectType=f["controllerClass"])
            put(CALLS + ".Array.data[1].m_TargetAssemblyTypeName", value="%s, Assembly-CSharp" % f["controllerClass"])
            put(CALLS + ".Array.data[1].m_MethodName", value="StartGame")
            put(CALLS + ".Array.data[1].m_Mode", value=MODE_VOID)
            put(CALLS + ".Array.data[1].m_CallState", value=STATE_RUNTIME)
            return self._answer(ctx, button, es)

        return [("serialized_calls", serialized_calls)]


TASK = T2()

# Producer:Betsy
