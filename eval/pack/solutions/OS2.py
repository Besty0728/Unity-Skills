"""OS2 reference and exact-failure variants, all authored through the agent's skill client."""
import json
import os

from usk_eval import verifylib as V
from usk_eval.task_api import Negative

GROUP = "UnityEngine.UI.VerticalLayoutGroup"
ELEMENT = "UnityEngine.UI.LayoutElement"
BUTTON = "UnityEngine.UI.Button"
FITTER = "UnityEngine.UI.ContentSizeFitter"


def _value(value):
    if isinstance(value, bool):
        return str(value).lower()
    if isinstance(value, (tuple, list)):
        return ",".join(str(v) for v in value)
    if isinstance(value, float) and value.is_integer():
        return str(int(value))
    return str(value)


def _set(ctx, ref, kind, field, value):
    return ctx.c.write("component_set_serialized_property", componentType=kind, propertyPath=field,
                       value=_value(value), **V.address(ref))


def _rect(ctx, ref, label=False, **args):
    fields = {"anchorMinX": 0 if label else 0.5, "anchorMinY": 0 if label else 0.5,
              "anchorMaxX": 1 if label else 0.5, "anchorMaxY": 1 if label else 0.5,
              "pivotX": 0.5, "pivotY": 0.5, "anchoredPosX": 0, "anchoredPosY": 0, "anchoredPosZ": 0,
              "localRotX": 0, "localRotY": 0, "localRotZ": 0, "localScaleX": 1, "localScaleY": 1,
              "localScaleZ": 1}
    if label:
        fields.update(sizeDeltaX=0, sizeDeltaY=0)
    fields.update(args)
    ctx.c.write("ui_set_rect_transform", **dict(V.address(ref), **fields))


def _nodes(ctx):
    return ctx.private.locate(ctx)


def _report(ctx):
    return ctx.private.actual_report(ctx)


def _solve(ctx, parent_canvas=False, sizing=True, axis="Vertical", keep_fitter=False, components=False):
    c, spec = ctx.c, ctx.private.spec(ctx)
    canvas = ctx.handles["canvas"]
    panel = V.object_id(c.write("ui_create_panel", name=spec["panel"], parentEntityId=str(V.object_id(canvas))))
    items = [{"type": "button", "name": name, "text": text, "width": spec["buttonWidth"],
              "height": spec["buttonHeight"], "parentEntityId": str(V.object_id(canvas if parent_canvas else panel))}
             for name, text in zip(spec["buttons"], spec["texts"])]
    made = c.write("ui_create_batch", items=json.dumps(items))
    buttons = [V.object_id(row) for row in made["results"]]
    group_type = "UnityEngine.UI." + axis + "LayoutGroup"
    if components:
        c.write("component_add", componentType=group_type, **V.address(panel))
    else:
        left, right, top, bottom = spec["padding"]
        c.write("ui_layout_children", layoutType=axis, spacing=spec["spacing"], paddingLeft=left,
                paddingRight=right, paddingTop=top, paddingBottom=bottom, childForceExpandWidth=False,
                childForceExpandHeight=False, **V.address(panel))
        if not keep_fitter:
            c.write("component_remove", componentType=FITTER, **V.address(panel))
    group_fields = {"m_Spacing": spec["spacing"], "m_ChildAlignment": 1, "m_ChildControlWidth": True,
                    "m_ChildControlHeight": True, "m_ChildForceExpandWidth": False,
                    "m_ChildForceExpandHeight": False, "m_ChildScaleWidth": False, "m_ChildScaleHeight": False}
    group_fields.update(dict(zip(("m_Padding.m_Left", "m_Padding.m_Right", "m_Padding.m_Top", "m_Padding.m_Bottom"),
                                 spec["padding"])))
    for field, value in group_fields.items():
        _set(ctx, panel, group_type, field, value)
    for index, button in enumerate(buttons):
        if sizing:
            c.write("ui_add_layout_element", minWidth=spec["buttonWidth"], preferredWidth=spec["buttonWidth"],
                    minHeight=spec["buttonHeight"], preferredHeight=spec["buttonHeight"], flexibleWidth=0,
                    flexibleHeight=0, ignoreLayout=False, layoutPriority=1, **V.address(button))
        _rect(ctx, button)
        label = V.object_id(ctx.probe.children(button)[0])
        _rect(ctx, label, label=True)
        _set(ctx, button, BUTTON, "m_Interactable", index != 2)
    _rect(ctx, panel, sizeDeltaX=spec["width"], sizeDeltaY=spec["height"])
    return _report(ctx)


def reference(ctx):
    return _solve(ctx)


def local_position(ctx):
    reference(ctx)
    panel = _nodes(ctx)[0]
    _rect(ctx, panel, anchorMinX=0, anchorMinY=1, anchorMaxX=0, anchorMaxY=1, localPosX=0, localPosY=0)
    return _report(ctx)


def size_delta_only(ctx):
    return _solve(ctx, sizing=False)


def canvas_parent(ctx):
    return _solve(ctx, parent_canvas=True)


def wrong_order(ctx):
    reference(ctx)
    ctx.c.write("gameobject_set_sibling_index", index=0, **V.address(_nodes(ctx)[3]))
    return _report(ctx)


def spacing_on_button(ctx):
    reference(ctx)
    panel, first = _nodes(ctx)[:2]
    _set(ctx, panel, GROUP, "m_Spacing", 0)
    ctx.c.write("component_add", componentType=GROUP, **V.address(first))
    ctx.c.write("component_set_enabled", componentType=GROUP, enabled=False, **V.address(first))
    _set(ctx, first, GROUP, "m_Spacing", ctx.private.spec(ctx)["spacing"])
    _rect(ctx, V.object_id(ctx.probe.children(first)[0]), label=True)
    return _report(ctx)


def swapped_padding(ctx):
    reference(ctx)
    panel = _nodes(ctx)[0]
    left, right, top, bottom = ctx.private.spec(ctx)["padding"]
    for field, value in zip(("m_Left", "m_Right", "m_Top", "m_Bottom"), (right, left, bottom, top)):
        _set(ctx, panel, GROUP, "m_Padding." + field, value)
    return _report(ctx)


def horizontal(ctx):
    return _solve(ctx, axis="Horizontal")


def third_interactable(ctx):
    reference(ctx)
    _set(ctx, _nodes(ctx)[3], BUTTON, "m_Interactable", True)
    return _report(ctx)


def wrong_button_disabled(ctx):
    reference(ctx)
    nodes = _nodes(ctx)
    _set(ctx, nodes[1], BUTTON, "m_Interactable", False)
    _set(ctx, nodes[3], BUTTON, "m_Interactable", True)
    return _report(ctx)


def empty_label(ctx):
    reference(ctx)
    label = V.object_id(ctx.probe.children(_nodes(ctx)[1])[0])
    ctx.c.write("ui_set_text", text="", **V.address(label))
    return _report(ctx)


def non_ui_text(ctx):
    reference(ctx)
    label = V.object_id(ctx.probe.children(_nodes(ctx)[1])[0])
    kind = "TMPro.TextMeshProUGUI" if ctx.probe.component_count(label, "TextMeshProUGUI") else "UnityEngine.UI.Text"
    ctx.c.write("component_remove", componentType=kind, **V.address(label))
    ctx.c.write("component_add", componentType="UnityEngine.TextMesh", **V.address(label))
    ctx.c.write("component_set_property", componentType="UnityEngine.TextMesh", propertyName="text",
                value=ctx.private.spec(ctx)["texts"][0], **V.address(label))
    return _report(ctx)


def canvas_panel(ctx):
    reference(ctx)
    panel = _nodes(ctx)[0]
    ctx.c.write("component_remove", componentType="UnityEngine.UI.Image", **V.address(panel))
    ctx.c.write("component_add", componentType="UnityEngine.Canvas", **V.address(panel))
    return _report(ctx)


def extra_object(ctx):
    reference(ctx)
    ctx.c.write("gameobject_create", name="Helper", **V.address(ctx.root, "parent"))
    return _report(ctx)


def extra_folder(ctx):
    reference(ctx)
    ctx.c.write("asset_create_folder", folderPath=ctx.asset("Helper"))
    return _report(ctx)


def _bystander(ctx):
    return next(ref for ref in ctx.handles["objects"]
                if ctx.probe.component_count(ref, "UnityEngine.BoxCollider"))


def bystander_property(ctx):
    reference(ctx)
    _set(ctx, _bystander(ctx), "UnityEngine.BoxCollider", "m_IsTrigger", True)
    return _report(ctx)


def bystander_depth(ctx):
    reference(ctx)
    ref = _bystander(ctx)
    pos = V.floats((ctx.probe.ser(ref, "UnityEngine.Transform") or {})["m_LocalPosition"])
    _set(ctx, ref, "UnityEngine.Transform", "m_LocalPosition", (pos[0], pos[1], pos[2] + 1))
    return _report(ctx)


def folder_labels(ctx):
    reference(ctx)
    ctx.c.write("asset_set_labels", assetPath=ctx.handles["folders"][0], labels="menu")
    return _report(ctx)


def default_fitter(ctx):
    return _solve(ctx, keep_fitter=True)


def wrong_report(ctx):
    result = reference(ctx)
    result["buttonPaths"] = []
    return result


def outside_root(ctx):
    result = reference(ctx)
    ctx.c.write("gameobject_create", name="Helper")
    return result


def direct_file(ctx):
    result = reference(ctx)
    path = os.path.join(ctx.project_path, *ctx.asset("menu.txt").split("/"))
    with open(path, "wb") as handle:
        handle.write(b"menu\n")
    return result


def component_group(ctx):
    return _solve(ctx, components=True)


def unconstrained_fitter(ctx):
    reference(ctx)
    panel = _nodes(ctx)[0]
    ctx.c.write("component_add", componentType=FITTER, **V.address(panel))
    _set(ctx, panel, FITTER, "m_HorizontalFit", 0)
    _set(ctx, panel, FITTER, "m_VerticalFit", 0)
    return _report(ctx)


def save_run_scene(ctx):
    result = reference(ctx)
    ctx.c.write("scene_save")
    return result


NEGATIVES = [
    Negative("local_position", local_position, {"A2"}),
    Negative("size_delta_only", size_delta_only, {"A4"}),
    Negative("canvas_parent", canvas_parent, {"A1", "A5"}),
    Negative("wrong_order", wrong_order, {"A5"}),
    Negative("spacing_on_button", spacing_on_button, {"A3"}),
    Negative("swapped_padding", swapped_padding, {"A3"}),
    Negative("horizontal", horizontal, {"A3", "A8"}),
    Negative("third_interactable", third_interactable, {"A7"}),
    Negative("wrong_button_disabled", wrong_button_disabled, {"A7"}),
    Negative("empty_label", empty_label, {"A6"}),
    Negative("non_ui_text", non_ui_text, {"A6", "A8"}),
    Negative("canvas_panel", canvas_panel, {"A1", "A8"}),
    Negative("extra_object", extra_object, {"A8"}),
    Negative("extra_folder", extra_folder, {"A8"}),
    Negative("bystander_property", bystander_property, {"A8"}),
    Negative("bystander_depth", bystander_depth, {"A8"}),
    Negative("folder_labels", folder_labels, {"A8"}),
    Negative("default_fitter", default_fitter, {"A2"}),
    Negative("wrong_report", wrong_report, {"A9"}),
    Negative("outside_root", outside_root, {"A10"}),
    Negative("direct_file", direct_file, {"A8", "A11"}),
]

ALTERNATIVES = [("component_group", component_group), ("unconstrained_fitter", unconstrained_fitter),
                ("save_run_scene", save_run_scene)]

# Producer:Betsy
