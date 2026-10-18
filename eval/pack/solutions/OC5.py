"""OC5 solutions: reference, negatives (each with the exact set of assertions it must fail) and alternatives."""
import json
import os

from usk_eval import verifylib as V
from usk_eval.task_api import Negative

ELEMENTS = ("TopBar", "Ammo", "Minimap", "Crosshair", "Compass", "Notice")


def _spec(ctx):
    return ctx.private.layout_spec(ctx.seed)


def _rect_args(rect):
    """prefab_set_transform / ui_set_rect_transform fields for one element."""
    return {"anchorMinX": rect["anchorMin"][0], "anchorMinY": rect["anchorMin"][1],
            "anchorMaxX": rect["anchorMax"][0], "anchorMaxY": rect["anchorMax"][1],
            "pivotX": rect["pivot"][0], "pivotY": rect["pivot"][1],
            "sizeDeltaX": rect["sizeDelta"][0], "sizeDeltaY": rect["sizeDelta"][1],
            "anchoredPosX": rect["anchoredPosition"][0], "anchoredPosY": rect["anchoredPosition"][1]}


def _group_items(ctx, spec):
    return [{"prefabPath": ctx.handles["prefabPath"], "componentType": "HorizontalLayoutGroup",
             "gameObjectPath": "TopBar", "propertyPath": path, "value": str(value)}
            for path, value in sorted(ctx.private.padding_props(spec).items())]


def _answer(ctx, order):
    return {"prefabPath": ctx.handles["prefabPath"], "childOrder": list(order), "declaredComplete": True}


def _asset_order_now(ctx):
    """The prefab root's children as the asset holds them after the solver acted (what an honest report says)."""
    return ctx.private._root_children(ctx.private.asset_state(ctx, ctx.handles["prefabPath"]))


def _write_file(ctx, asset_path, data):
    """Writes a file under A on disk, bypassing the skills (the runner's refresh imports it at submit)."""
    target = os.path.join(ctx.project_path, *asset_path.split("/"))
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, "wb") as handle:
        handle.write(data)


def _asset_rects(ctx, spec, rects=None):
    pp = ctx.handles["prefabPath"]
    rects = rects or spec["rects"]
    ctx.c.write("prefab_set_transform_batch", items=json.dumps(
        [dict(_rect_args(rects[name]), prefabPath=pp, gameObjectPath=name) for name in ELEMENTS]))


def _asset_order(ctx, order):
    pp = ctx.handles["prefabPath"]
    ctx.c.write("prefab_set_sibling_index_batch", items=json.dumps(
        [{"prefabPath": pp, "gameObjectPath": name, "index": index} for index, name in enumerate(order)]))


def _asset_group(ctx, spec, padding=True):
    items = _group_items(ctx, spec)
    if not padding:
        items = [item for item in items if item["propertyPath"] == "m_Spacing"]
    ctx.c.write("prefab_set_serialized_property_batch", items=json.dumps(items))


def _asset_active(ctx):
    ctx.c.write("prefab_set_active", prefabPath=ctx.handles["prefabPath"], gameObjectPath="Notice", active=True)


def _instance_edits(ctx, spec):
    """The same values written on the scene instance instead of the asset."""
    hud = ctx.handles["hudPath"]
    ctx.c.write("ui_set_rect_transform_batch", items=json.dumps(
        [dict(_rect_args(spec["rects"][name]), path=hud + "/" + name) for name in ELEMENTS]))
    ctx.c.write("gameobject_set_active", active=True, path=hud + "/Notice")
    for path, value in sorted(ctx.private.padding_props(spec).items()):
        ctx.c.write("component_set_serialized_property", componentType="HorizontalLayoutGroup", propertyPath=path,
                    value=str(value), path=hud + "/TopBar")


# ---------------------------------------------------------------- reference and alternatives


def reference(ctx):
    spec = _spec(ctx)
    _asset_rects(ctx, spec)
    _asset_order(ctx, spec["order"])
    _asset_active(ctx)
    _asset_group(ctx, spec)
    return _answer(ctx, spec["order"])


def edit_batch(ctx):
    """One prefab_edit_batch transaction holding every step."""
    spec = _spec(ctx)
    steps = [{"skill": "prefab_set_transform", "args": dict(_rect_args(spec["rects"][name]), gameObjectPath=name)}
             for name in ELEMENTS]
    steps += [{"skill": "prefab_set_sibling_index", "args": {"gameObjectPath": name, "index": index}}
              for index, name in enumerate(spec["order"])]
    steps.append({"skill": "prefab_set_active", "args": {"gameObjectPath": "Notice", "active": True}})
    steps += [{"skill": "prefab_set_serialized_property",
               "args": {key: value for key, value in item.items() if key != "prefabPath"}}
              for item in _group_items(ctx, spec)]
    ctx.c.write("prefab_edit_batch", prefabPath=ctx.handles["prefabPath"], steps=json.dumps(steps))
    return _answer(ctx, spec["order"])


def serialized_fields(ctx):
    """The RectTransform values written as serialized Vector2 fields, one call each."""
    spec = _spec(ctx)
    pp = ctx.handles["prefabPath"]
    fields = (("m_AnchorMin", "anchorMin"), ("m_AnchorMax", "anchorMax"), ("m_Pivot", "pivot"),
              ("m_SizeDelta", "sizeDelta"), ("m_AnchoredPosition", "anchoredPosition"))
    for name in ELEMENTS:
        rect = spec["rects"][name]
        for path, field in fields:
            ctx.c.write("prefab_set_serialized_property", prefabPath=pp, componentType="RectTransform",
                        gameObjectPath=name, propertyPath=path, value="%g,%g" % tuple(rect[field]))
    for index, name in enumerate(spec["order"]):
        ctx.c.write("prefab_set_sibling_index", prefabPath=pp, gameObjectPath=name, index=index)
    _asset_active(ctx)
    for item in _group_items(ctx, spec):
        ctx.c.write("prefab_set_serialized_property", **item)
    return _answer(ctx, spec["order"])


def save_run_scene(ctx):
    answer = reference(ctx)
    ctx.c.write("scene_save")
    return answer


def _nudged(value):
    """A requested number moved by 0.0004 towards the inside of 0..1 (anchors and pivots stay in range)."""
    return value - 0.0004 if value >= 0.5 else value + 0.0004


def within_tolerance(ctx):
    """Every requested RectTransform value off by 0.0004, inside P1's 0.001."""
    spec = _spec(ctx)
    rects = {name: {field: tuple(_nudged(v) for v in values) for field, values in rect.items()}
             for name, rect in spec["rects"].items()}
    _asset_rects(ctx, spec, rects)
    _asset_order(ctx, spec["order"])
    _asset_active(ctx)
    _asset_group(ctx, spec)
    return _answer(ctx, spec["order"])


# ---------------------------------------------------------------- negatives


def local_position(ctx):
    """Anchors, pivots and sizes first, then the positions as posX/posY (localPosition) instead of anchoredPosX/Y.
    (In one prefab_set_transform call posX/posY would land before the anchors, under the centred start anchors, where
    localPosition and anchoredPosition coincide.)"""
    spec = _spec(ctx)
    pp = ctx.handles["prefabPath"]
    layout = []
    for name in ELEMENTS:
        args = _rect_args(spec["rects"][name])
        del args["anchoredPosX"], args["anchoredPosY"]
        layout.append(dict(args, prefabPath=pp, gameObjectPath=name))
    ctx.c.write("prefab_set_transform_batch", items=json.dumps(layout))
    ctx.c.write("prefab_set_transform_batch", items=json.dumps(
        [{"prefabPath": pp, "gameObjectPath": name, "posX": spec["rects"][name]["anchoredPosition"][0],
          "posY": spec["rects"][name]["anchoredPosition"][1]} for name in ELEMENTS]))
    _asset_order(ctx, spec["order"])
    _asset_active(ctx)
    _asset_group(ctx, spec)
    return _answer(ctx, spec["order"])


def instance_only(ctx):
    """The values written on the scene instance; the report gives the order the asset really has."""
    spec = _spec(ctx)
    _instance_edits(ctx, spec)
    return _answer(ctx, _asset_order_now(ctx))


def instance_then_apply(ctx):
    """The values written on the scene instance, then prefab_apply; the report gives the asset's real order."""
    spec = _spec(ctx)
    _instance_edits(ctx, spec)
    ctx.c.write("prefab_apply", path=ctx.handles["hudPath"])
    return _answer(ctx, _asset_order_now(ctx))


def wrong_order(ctx):
    spec = _spec(ctx)
    order = list(reversed(spec["order"]))
    _asset_rects(ctx, spec)
    _asset_order(ctx, order)
    _asset_active(ctx)
    _asset_group(ctx, spec)
    return _answer(ctx, order)


def activate_instance(ctx):
    """Notice activated on the scene instance instead of in the asset."""
    spec = _spec(ctx)
    _asset_rects(ctx, spec)
    _asset_order(ctx, spec["order"])
    ctx.c.write("gameobject_set_active", active=True, path=ctx.handles["hudPath"] + "/Notice")
    _asset_group(ctx, spec)
    return _answer(ctx, spec["order"])


def spacing_only(ctx):
    spec = _spec(ctx)
    _asset_rects(ctx, spec)
    _asset_order(ctx, spec["order"])
    _asset_active(ctx)
    _asset_group(ctx, spec, padding=False)
    return _answer(ctx, spec["order"])


def revert_instance(ctx):
    answer = reference(ctx)
    ctx.c.write("prefab_revert_overrides", name=ctx.handles["hudPath"].rpartition("/")[2])
    return answer


def edit_root(ctx):
    """The prefab root resized in the asset. The size of a RectTransform instance root is a default override, so the
    scene instance keeps 1280 and now differs from its asset: a new override (P5) besides the changed root (P6)."""
    answer = reference(ctx)
    ctx.c.write("prefab_set_transform", prefabPath=ctx.handles["prefabPath"], gameObjectPath="", sizeDeltaX=1000)
    return answer


def asset_depth(ctx):
    """The reference, then one element's local Z moved in the asset (posZ; X/Y and anchoredPosition untouched)."""
    answer = reference(ctx)
    ctx.c.write("prefab_set_transform", prefabPath=ctx.handles["prefabPath"], gameObjectPath="Ammo", posZ=123)
    return answer


def canvas_sort_order(ctx):
    """The reference, then the scene Canvas the instance sits on gets another sorting order."""
    answer = reference(ctx)
    ctx.c.write("component_set_property", path=ctx.handles["canvasPath"], componentType="Canvas",
                propertyName="sortingOrder", value="123")
    return answer


def replace_asset(ctx):
    """The asset deleted and created anew from the scene instance."""
    answer = reference(ctx)
    ctx.c.write("asset_delete", assetPath=ctx.handles["prefabPath"])
    ctx.c.write("prefab_create", path=ctx.handles["hudPath"], savePath=ctx.handles["prefabPath"])
    return answer


def wrong_report(ctx):
    answer = reference(ctx)
    answer["childOrder"] = list(ELEMENTS)
    if answer["childOrder"] == _spec(ctx)["order"]:
        answer["childOrder"].reverse()
    return answer


def outside_root(ctx):
    answer = reference(ctx)
    ctx.c.write("gameobject_create", name="StrayWidget")
    return answer


def direct_file(ctx):
    answer = reference(ctx)
    _write_file(ctx, ctx.asset("Prefabs", "HUD_backup.txt"), b"layout\n")
    return answer


NEGATIVES = [
    Negative("local_position", local_position, {"P1"}, "posX/posY write localPosition, not anchoredPosition"),
    Negative("instance_only", instance_only, {"P1", "P2", "P3", "P4", "P5"}, "the scene instance edited, the asset "
                                                                             "untouched"),
    Negative("instance_then_apply", instance_then_apply, {"P2", "P5", "P6"}, "prefab_apply pushes the instance's "
                                                                              "colour override into the asset"),
    Negative("wrong_order", wrong_order, {"P2"}),
    Negative("activate_instance", activate_instance, {"P3", "P5"}, "Notice activated on the instance"),
    Negative("spacing_only", spacing_only, {"P4"}, "padding left at its old values"),
    Negative("revert_instance", revert_instance, {"P5"}, "the instance's own override reverted"),
    Negative("edit_root", edit_root, {"P5", "P6"}, "the prefab root resized; the instance root keeps its size"),
    Negative("asset_depth", asset_depth, {"P6"}, "an element's local Z changed in the asset"),
    Negative("canvas_sort_order", canvas_sort_order, {"P6"}, "the scene Canvas's sorting order changed"),
    Negative("replace_asset", replace_asset, {"P5", "P6", "P10"}, "the asset deleted and recreated"),
    Negative("wrong_report", wrong_report, {"P7"}),
    Negative("outside_root", outside_root, {"P8"}),
    Negative("direct_file", direct_file, {"P9"}, "a file written to disk next to the skills"),
]

ALTERNATIVES = [("edit_batch", edit_batch), ("serialized_fields", serialized_fields), ("save_run_scene", save_run_scene),
                ("within_tolerance", within_tolerance)]

# Producer:Betsy
