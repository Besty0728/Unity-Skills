"""DB5: a ClickCounter script wired to a Button through a persistent onClick listener (dev, basic).

Fixture: R/Canvas_<a> (Screen Space Overlay) with a UnityEngine.UI.Text label Count_<d> showing 0 and two buttons,
Btn_<b> and Btn_<c>; the component goes on one of the buttons.
"""
import os

from usk_eval import verifylib as V
from usk_eval.task_api import PublicTask, address

WORDS = ("Kestrel", "Talon", "Vortex", "Ember", "Quill", "Harrow", "Juniper", "Marlow")
ROWS = (80.0, 0.0, -80.0)
TEXT_TYPE = "UnityEngine.UI.Text"


def class_name(script_path):
    return os.path.basename(script_path)[:-len(".cs")]


def cs_files(project_path):
    out = []
    for base, dirs, files in os.walk(os.path.join(project_path, "Assets")):
        dirs.sort()
        out += [os.path.relpath(os.path.join(base, name), project_path).replace(os.sep, "/")
                for name in sorted(files) if name.endswith(".cs")]
    return sorted(out)


def subtree_states(probe, root):
    """[[ref, go_state]] of root and every descendant, depth first."""
    out, stack = [], [root]
    while stack:
        ref = stack.pop()
        state = V.go_state(probe, ref)
        out.append([V.object_id(ref), state])
        stack.extend(reversed((state or {}).get("children") or []))
    return out


def listener_count(ctx, button_path):
    res = ctx.read("event_get_listeners", path=button_path, componentName="Button", eventName="onClick")
    return V.as_int(res.get("listenerCount"))


def label_text(probe, ref):
    return (probe.ser(ref, TEXT_TYPE) or {}).get("m_Text")


class DB5(PublicTask):
    id = "DB5"
    track = "dev"
    tier = "basic"
    case_version = 1
    minutes = 15
    requires = {"pipeline": ["URP", "BuiltIn"], "mode": "bypass", "dedicated": False, "packages": ["com.unity.ugui"]}
    needs_focus = True

    def build(self, ctx):
        c, rng = ctx.c, ctx.rng
        words = rng.sample(WORDS, 5)
        chosen = rng.randrange(2)
        canvas_name = "Canvas_" + words[1]
        canvas = V.object_id(c.write("ui_create_canvas", name=canvas_name, renderMode="ScreenSpaceOverlay"))
        c.write("gameobject_set_parent", **dict(address(canvas, "child"), **address(ctx.root, "parent")))
        # ui_create_text adds a TextMeshProUGUI wherever TextMeshPro is present (always from Unity 6), so the label is
        # an empty object given a UnityEngine.UI.Text, which brings its RectTransform and CanvasRenderer along.
        label = V.object_id(c.write("gameobject_create", name="Count_" + words[4], **address(canvas, "parent")))
        added = c.write("component_add", componentType=TEXT_TYPE, **address(label))
        if added.get("fullTypeName") != TEXT_TYPE:
            raise RuntimeError("DB5 fixture: the label got %r instead of %s" % (added.get("fullTypeName"), TEXT_TYPE))
        c.write("component_set_property", componentType=TEXT_TYPE, propertyName="text", value="0", **address(label))
        c.write("component_set_property", componentType=TEXT_TYPE, propertyName="fontSize", value="32",
                **address(label))
        c.write("gameobject_set_transform", localPosY=ROWS[0], **address(label))
        buttons = []
        for index, word in enumerate(words[2:4]):
            button = V.object_id(c.write("ui_create_button", name="Btn_" + word, parent=canvas_name, text="Click"))
            c.write("gameobject_set_transform", localPosY=ROWS[1 + index], **address(button))
            buttons.append(button)
        return {"canvas": canvas, "buttons": buttons, "button": buttons[chosen],
                "buttonPath": ctx.path(canvas_name, "Btn_" + words[2 + chosen]),
                "otherPath": ctx.path(canvas_name, "Btn_" + words[3 - chosen]),
                "label": label, "labelPath": ctx.path(canvas_name, "Count_" + words[4]),
                "script": ctx.asset("Scripts", "ClickCounter_%s.cs" % words[0])}

    def snapshot(self, ctx):
        p, h = ctx.probe, ctx.handles
        return {"data": {"states": subtree_states(p, h["canvas"]),
                         "labelText": label_text(p, h["label"]),
                         "listeners": [[path, listener_count(ctx, path)] for path in (h["buttonPath"], h["otherPath"])],
                         "scripts": cs_files(ctx.project_path)},
                "digests": {}}

    def prompt_vars(self, ctx):
        h = ctx.handles
        return {"scripts": ctx.asset("Scripts"), "cls": class_name(h["script"]), "button": h["buttonPath"],
                "other": h["otherPath"], "label": h["labelPath"]}

    def answer_schema(self):
        return {
            "type": "object",
            "required": ["scriptPath", "buttonInstanceId", "labelInstanceId"],
            "properties": {
                "scriptPath": {"type": "string", "desc": {"zh-CN": "脚本的资产路径", "en": "asset path of the script"}},
                "buttonInstanceId": {"type": "integer", "desc": {
                    "zh-CN": "挂载了组件的按钮对象的 instanceId（Unity 6000.4+ 填 entityId）",
                    "en": "instanceId of the button object carrying the component (entityId on Unity 6000.4+)"}},
                "labelInstanceId": {"type": "integer", "desc": {
                    "zh-CN": "label 字段所指 Text 组件所在对象的 instanceId（Unity 6000.4+ 填 entityId）",
                    "en": "instanceId of the object whose Text component the label field points at (entityId on "
                          "Unity 6000.4+)"}},
            },
        }


TASK = DB5()

# Producer:Betsy
