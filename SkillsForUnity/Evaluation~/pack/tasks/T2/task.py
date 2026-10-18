"""T2: a UGUI start button with two ordered persistent onClick listeners and an EventSystem (dev, standard).

Fixture: the GameController_<k> script (k = seeded word + run id), compiled at build and added to
R/Systems/GameController; R/UI with DebugCanvas (created first) and MenuCanvas; R/Archive/MenuCanvas, a plain object
without a Canvas component; three Menu objects created in a seeded order, one under each of DebugCanvas, MenuCanvas
and Archive/MenuCanvas, tagged Untagged, Finish and Player respectively; no EventSystem. Two project facts are read
from the project files at build and named in the prompt: the UI input module that fits the project's Active Input
Handling (StandaloneInputModule on Input Manager (Old) or Both, InputSystemUIInputModule when only the Input System
package is active) and the text component of a button label (TextMeshProUGUI where TextMesh Pro is installed,
UnityEngine.UI.Text otherwise).
"""
import json
import os

from usk_eval import verifylib as V
from usk_eval.task_api import PublicTask, address

WORDS = ("Aster", "Brine", "Cobalt", "Delta", "Fjord", "Garnet")
MENU_TAGS = (("MenuCanvas", "Finish"), ("DebugCanvas", None), ("Archive", "Player"))
INPUT_PACKAGE = "com.unity.inputsystem"

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


def class_name(script_path):
    return os.path.basename(script_path)[:-len(".cs")]


def input_module(project_path):
    """Name of the UI input module the project's Active Input Handling needs on an EventSystem: StandaloneInputModule
    reads the Input Manager, InputSystemUIInputModule (package com.unity.inputsystem) the Input System package. With
    only the Input System package active, the Input Manager module throws on every frame in Play Mode."""
    if V.active_input_handler(project_path) != 1:
        return "StandaloneInputModule"
    if INPUT_PACKAGE not in V.installed_packages(project_path):
        raise RuntimeError("Active Input Handling is Input System Package (New), but %s is not installed"
                           % INPUT_PACKAGE)
    return "InputSystemUIInputModule"


def label_component(project_path):
    """The text component the UI skills give a button label: TextMeshProUGUI where the project carries TextMesh Pro
    (always on Unity 6, where it ships inside uGUI 2.x), UnityEngine.UI.Text otherwise. The runner's own check of
    TextMesh Pro's Essential Resources uses the same detection."""
    return "TextMeshProUGUI" if V.has_text_mesh_pro(project_path) else "UnityEngine.UI.Text"


def fixed_state(probe, ref, children=True, local=True):
    """go_state without the world position and rotation (a Screen Space canvas follows the Game view size, and the
    objects below it with it); `local` adds the local position, rotation and scale instead."""
    state = V.go_state(probe, ref, with_children=children)
    if state is None:
        return None
    for key in ("position", "rotation"):
        state.pop(key, None)
    if local:
        state["local"] = V.local_trs(probe, ref)
    return state


class T2(PublicTask):
    id = "T2"
    track = "dev"
    tier = "standard"
    case_version = 2
    minutes = 40
    requires = {"pipeline": "URP", "mode": "bypass", "dedicated": True, "packages": ["com.unity.ugui"]}
    needs_focus = True

    def build(self, ctx):
        c, rng = ctx.c, ctx.rng
        module, label = input_module(ctx.project_path), label_component(ctx.project_path)

        def make(name, parent):
            return V.object_id(c.write("gameobject_create", name=name, **address(parent, "parent")))

        cls = "GameController_%s_%s" % (rng.choice(WORDS), ctx.scope.run_id)
        res = c.write("script_create", scriptName=cls, folder=ctx.asset("Scripts"),
                      content=CONTROLLER_SRC.format(cls=cls))
        if res.get("jobId"):
            ctx.wait_job(res["jobId"])
        ctx.wait_idle()
        ui = make("UI", ctx.root)
        parents = {}
        for name in ("DebugCanvas", "MenuCanvas"):
            canvas = V.object_id(c.write("ui_create_canvas", name=name))
            c.write("gameobject_set_parent", **dict(address(canvas, "child"), **address(ui, "parent")))
            parents[name] = canvas
        archive = make("Archive", ctx.root)
        parents["Archive"] = make("MenuCanvas", archive)
        order = list(MENU_TAGS)
        rng.shuffle(order)
        menus = []
        for parent, tag in order:
            menu = make("Menu", parents[parent])
            if tag:
                c.write("gameobject_set_tag_batch", items=json.dumps([dict({"tag": tag}, **address(menu))]))
            menus.append(menu)
        systems = make("Systems", ctx.root)
        controller = make("GameController", systems)
        c.write("component_add", componentType=cls, **address(controller))
        return {"uiPath": ctx.path("UI"), "menuCanvasPath": ctx.path("UI", "MenuCanvas"),
                "controllerPath": ctx.path("Systems", "GameController"),
                "controllerScript": ctx.asset("Scripts", cls + ".cs"), "ui": ui,
                "canvases": [parents["DebugCanvas"], parents["MenuCanvas"]], "archive": archive,
                "archiveCanvas": parents["Archive"], "menus": menus, "controller": controller,
                "inputModule": module, "labelComponent": label}

    def snapshot(self, ctx):
        p, h = ctx.probe, ctx.handles
        debug = h["canvases"][0]
        return {"data": {"objects": [[ref, fixed_state(p, ref)] for ref in
                                     h["menus"] + [h["archive"], h["archiveCanvas"], h["controller"]]],
                         "canvases": [[ref, fixed_state(p, ref, children=False, local=False)]
                                      for ref in h["canvases"]],
                         "debugChildren": [V.object_id(child) for child in p.children(debug)],
                         "controller": p.ser(h["controller"], class_name(h["controllerScript"]))},
                "digests": {}}

    def prompt_vars(self, ctx):
        h = ctx.handles
        return {"canvas": h["menuCanvasPath"], "ui": h["uiPath"], "gc": h["controllerPath"],
                "cls": class_name(h["controllerScript"]), "input_module": h["inputModule"],
                "label_component": h["labelComponent"]}

    def answer_schema(self):
        return {
            "type": "object",
            "required": ["buttonInstanceId", "eventSystemInstanceId", "listeners"],
            "properties": {
                "buttonInstanceId": {"type": "integer", "desc": {
                    "zh-CN": "StartButton 的 instanceId（Unity 6000.4+ 填 entityId）",
                    "en": "instanceId of StartButton (entityId on Unity 6000.4+)"}},
                "eventSystemInstanceId": {"type": "integer", "desc": {
                    "zh-CN": "EventSystem 对象的 instanceId（Unity 6000.4+ 填 entityId）",
                    "en": "instanceId of the EventSystem object (entityId on Unity 6000.4+)"}},
                "listeners": {"type": "array", "items": {"type": "object", "properties": {
                    "gameObjectInstanceId": {"type": "integer", "desc": {
                        "zh-CN": "该监听所调用对象所在 GameObject 的 instanceId（Unity 6000.4+ 填 entityId）",
                        "en": "instanceId of the GameObject this listener calls into (entityId on Unity 6000.4+)"}},
                    "method": {"type": "string", "desc": {"zh-CN": "方法名", "en": "method name"}},
                }}},
            },
        }


TASK = T2()

# Producer:Betsy
