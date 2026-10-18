"""T2 solutions: reference, negatives (each with the exact set of assertions it must fail) and alternatives."""
import os

from usk_eval import verifylib as V
from usk_eval.task_api import Negative

CALLS = "m_OnClick.m_PersistentCalls.m_Calls"


def _number(ident):
    return int(ident) if isinstance(ident, str) else ident


def _fixture(ctx):
    return ctx.task.snapshot(ctx)["data"]


def _menu(fixture, parent_name):
    """Id of the fixture Menu whose parent is named `parent_name` (DebugCanvas, MenuCanvas; Archive's is MenuCanvas)."""
    return next(ref for ref, state in fixture["objects"]
                if state["name"] == "Menu" and state["parentPath"].endswith("/UI/" + parent_name))


def _button(ctx, parent="MenuCanvas", width=320, height=80, anchor=(0.5, 0)):
    c = ctx.c
    button = V.object_id(c.write("ui_create_button", name="StartButton", text="Start Game", width=width,
                                 height=height))
    holder = ctx.private.canvas(_fixture(ctx), parent)
    c.write("gameobject_set_parent", **dict(V.address(button, "child"), **V.address(holder, "parent")))
    c.write("ui_set_rect_transform", anchorMinX=anchor[0], anchorMinY=anchor[1], anchorMaxX=anchor[0],
            anchorMaxY=anchor[1], pivotX=0.5, pivotY=0, anchoredPosX=0, anchoredPosY=120, sizeDeltaX=width,
            sizeDeltaY=height, **V.address(button))
    return button


def _event_system(ctx, parent=None):
    c = ctx.c
    system = V.object_id(c.write("gameobject_create", name="EventSystem",
                                 **V.address(parent if parent is not None else ctx.handles["ui"], "parent")))
    c.write("component_add", componentType="EventSystem", **V.address(system))
    c.write("component_add", componentType="StandaloneInputModule", **V.address(system))
    return system


def _menu_call(ctx, button, menu, active=False, state="RuntimeOnly"):
    ctx.c.write("event_add_listener", componentName="Button", eventName="onClick", targetComponentName="GameObject",
                methodName="SetActive", mode=state, argType="bool", boolArg=active,
                **dict(V.address(button), **V.address(menu, "target")))


def _start_call(ctx, button, state="RuntimeOnly"):
    ctx.c.write("event_add_listener", componentName="Button", eventName="onClick",
                targetComponentName=ctx.private.class_name(ctx.handles), methodName="StartGame", mode=state,
                **dict(V.address(button), **V.address(ctx.handles["controller"], "target")))


def _answer(ctx, button, system):
    p = ctx.probe
    calls = ctx.private.listener_calls(p.ser(button, "Button") or {})
    return {"buttonInstanceId": _number(button), "eventSystemInstanceId": _number(system),
            "listeners": [{"gameObjectInstanceId": _number(p.owner(call.get("m_Target"))),
                           "method": call.get("m_MethodName")} for call in calls],
            "declaredComplete": True}


def _solve(ctx, menu=None, order=("menu", "start"), active=False, state="RuntimeOnly", parent="MenuCanvas",
           width=320, height=80, anchor=(0.5, 0)):
    fixture = _fixture(ctx)
    button = _button(ctx, parent, width, height, anchor)
    system = _event_system(ctx)
    for step in order:
        if step == "menu":
            _menu_call(ctx, button, menu if menu is not None else ctx.private.finish_menu(fixture, ctx.handles),
                       active, state)
        else:
            _start_call(ctx, button, state)
    return _answer(ctx, button, system)


def _write_file(ctx, asset_path, data):
    """Writes a file under A on disk, bypassing the skills (the runner's refresh imports it at submit)."""
    target = os.path.join(ctx.project_path, *asset_path.split("/"))
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, "wb") as handle:
        handle.write(data)


def reference(ctx):
    return _solve(ctx)


def debug_canvas(ctx):
    return _solve(ctx, parent="DebugCanvas")


def wrong_menu(ctx):
    return _solve(ctx, menu=_menu(_fixture(ctx), "DebugCanvas"))


def order_swapped(ctx):
    return _solve(ctx, order=("start", "menu"))


def set_active_true(ctx):
    return _solve(ctx, active=True)


def two_event_systems(ctx):
    answer = _solve(ctx)
    _event_system(ctx, parent=ctx.root)
    return answer


def no_image(ctx):
    answer = _solve(ctx)
    ctx.c.write("component_remove", componentType="Image", **V.address(answer["buttonInstanceId"]))
    return answer


def default_size(ctx):
    return _solve(ctx, width=160, height=30)


def centre_anchors(ctx):
    return _solve(ctx, anchor=(0.5, 0.5))


def start_twice(ctx):
    answer = _solve(ctx)
    _start_call(ctx, answer["buttonInstanceId"])
    return _answer(ctx, answer["buttonInstanceId"], answer["eventSystemInstanceId"])


def extra_canvas(ctx):
    answer = _solve(ctx)
    overlay = V.object_id(ctx.c.write("ui_create_canvas", name="OverlayCanvas"))
    ctx.c.write("gameobject_set_parent", **dict(V.address(overlay, "child"), **V.address(ctx.handles["ui"], "parent")))
    return answer


def editor_and_runtime(ctx):
    return _solve(ctx, state="EditorAndRuntime")


def listeners_off(ctx):
    return _solve(ctx, state="Off")


def wrong_text(ctx):
    answer = _solve(ctx)
    label = V.object_id(ctx.probe.children(answer["buttonInstanceId"])[0])
    ctx.c.write("ui_set_text", text="Launch", **V.address(label))
    return answer


def outside_root(ctx):
    answer = _solve(ctx)
    ctx.c.write("gameobject_create", name="Stray")
    return answer


def direct_file(ctx):
    answer = _solve(ctx)
    _write_file(ctx, ctx.asset("Data", "notes.txt"), b"wired\n")
    return answer


def wrong_report(ctx):
    answer = _solve(ctx)
    answer["eventSystemInstanceId"] = answer["buttonInstanceId"]
    return answer


def serialized_calls(ctx):
    c, h = ctx.c, ctx.handles
    fixture = _fixture(ctx)
    cls = ctx.private.class_name(h)
    menu = ctx.private.finish_menu(fixture, h)
    menu_path = next(state["path"] for ref, state in fixture["objects"] if V.same_id(ref, menu))
    button = _button(ctx)
    system = _event_system(ctx)
    base = dict({"componentType": "Button"}, **V.address(button))

    def put(path, **args):
        c.write("component_set_serialized_property", propertyPath=CALLS + path, **dict(base, **args))

    put(".Array.size", value="2")
    put(".Array.data[0].m_Target", referencePath=menu_path)
    put(".Array.data[0].m_TargetAssemblyTypeName", value="UnityEngine.GameObject, UnityEngine.CoreModule")
    put(".Array.data[0].m_MethodName", value="SetActive")
    put(".Array.data[0].m_Mode", value="6")
    put(".Array.data[0].m_Arguments.m_BoolArgument", value="false")
    put(".Array.data[0].m_CallState", value="2")
    put(".Array.data[1].m_Target", referencePath=h["controllerPath"], objectType=cls)
    put(".Array.data[1].m_TargetAssemblyTypeName", value="%s, Assembly-CSharp" % cls)
    put(".Array.data[1].m_MethodName", value="StartGame")
    put(".Array.data[1].m_Mode", value="1")
    put(".Array.data[1].m_CallState", value="2")
    return _answer(ctx, button, system)


def later_matching_tmp(ctx):
    answer = reference(ctx)
    first = V.object_id(ctx.probe.children(answer["buttonInstanceId"])[0])
    ctx.c.write("ui_set_text", text="", **V.address(first))
    ctx.c.write("ui_create_text", name="StartButtonLabel", parent="StartButton", text="Start Game")
    return answer


def save_run_scene(ctx):
    answer = reference(ctx)
    ctx.c.write("scene_save")
    return answer


NEGATIVES = [
    Negative("debug_canvas", debug_canvas, {"D1"}, "the canvas created first"),
    Negative("wrong_menu", wrong_menu, {"D8", "D14"}, "the top-most Menu, which a target given by name binds"),
    Negative("order_swapped", order_swapped, {"D8", "D9"}),
    Negative("set_active_true", set_active_true, {"D8", "D14"}),
    Negative("two_event_systems", two_event_systems, {"D6"}),
    Negative("no_image", no_image, {"D2"}),
    Negative("default_size", default_size, {"D5"}),
    Negative("centre_anchors", centre_anchors, {"D4"}),
    Negative("start_twice", start_twice, {"D7", "D14"}, "a third listener calls StartGame again"),
    Negative("extra_canvas", extra_canvas, {"D11"}, "a third Canvas inside R"),
    Negative("editor_and_runtime", editor_and_runtime, {"D10"}),
    Negative("listeners_off", listeners_off, {"D10", "D14"}),
    Negative("wrong_text", wrong_text, {"D3"}, "the only label reads Launch"),
    Negative("outside_root", outside_root, {"D12"}),
    Negative("direct_file", direct_file, {"D15"}, "a non-script file written to disk next to the skills"),
    Negative("wrong_report", wrong_report, {"D13"}),
]

ALTERNATIVES = [("serialized_calls", serialized_calls), ("later_matching_tmp", later_matching_tmp),
                ("save_run_scene", save_run_scene)]

# Producer:Betsy
