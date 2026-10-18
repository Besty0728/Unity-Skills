"""OC5: bring a HUD prefab asset to a given layout while its scene instance keeps its own overrides (ops, challenge).

Fixture: R/<Canvas> (world-space Canvas, 1280 x 720) > <HUD>, a connected instance of A/Prefabs/<HUD>.prefab. The
prefab root is a 1280 x 720 panel carrying a nested Canvas; its direct children are the six RectTransforms TopBar
(with a HorizontalLayoutGroup), Ammo, Minimap, Crosshair, Compass and Notice (inactive), created in a seeded order with
seeded anchors, positions and sizes. The scene instance overrides the colour of one child's Image. The layout the task
asks for (anchors, pivots, positions, sizes, child order, spacing and padding) is derived from the seed as well.
"""
import json
import random

from usk_eval import verifylib as V
from usk_eval.task_api import PublicTask, address

ELEMENTS = ("TopBar", "Ammo", "Minimap", "Crosshair", "Compass", "Notice")
CORNERS = (((0, 1), "TopLeft"), ((1, 1), "TopRight"), ((0, 0), "BottomLeft"), ((1, 0), "BottomRight"))
CANVAS_NAMES = ("MainCanvas", "ScreenCanvas", "UIRoot")
HUD_NAMES = ("HUD", "BattleHUD", "PlayerHUD")
CANVAS_SIZE = (1280, 720)
START_SIZES = (64, 72, 88, 96)
TINT = "0.2,0.8,1,1"
RECT_FIELDS = ("anchorMin", "anchorMax", "pivot", "anchoredPosition", "sizeDelta")


def layout_spec(seed):
    """The layout the prompt asks for, from the seed alone: the five RectTransform values of each element, the child
    order of the prefab root and the TopBar layout group's spacing and padding (left, right, top, bottom)."""
    rng = random.Random("OC5:%d" % seed)
    corners = list(CORNERS)
    rng.shuffle(corners)
    margin = rng.choice((12, 16, 20, 24))
    top = rng.choice((64, 72, 80))
    pad = rng.choice((2, 4, 6))
    rects = {"TopBar": {"anchorMin": (0, 1), "anchorMax": (1, 1), "pivot": (0.5, 1),
                        "anchoredPosition": (0, -margin), "sizeDelta": (-2 * margin, rng.choice((40, 48, 56)))}}
    side = rng.choice((140, 160, 180))
    for name, size, inset in (("Ammo", (rng.choice((180, 200, 220)), rng.choice((32, 36, 40))), top),
                              ("Minimap", (side, side), top), ("Compass", (240, 32), margin)):
        corner = corners.pop()[0]
        rects[name] = {"anchorMin": corner, "anchorMax": corner, "pivot": corner,
                       "anchoredPosition": (margin if corner[0] == 0 else -margin, -inset if corner[1] == 1 else inset),
                       "sizeDelta": size}
    dot = rng.choice((20, 24, 28, 32))
    rects["Crosshair"] = {"anchorMin": (0.5, 0.5), "anchorMax": (0.5, 0.5), "pivot": (0.5, 0.5),
                          "anchoredPosition": (0, 0), "sizeDelta": (dot, dot)}
    rects["Notice"] = {"anchorMin": (0.5, 0), "anchorMax": (0.5, 0), "pivot": (0.5, 0),
                       "anchoredPosition": (0, rng.choice((80, 96, 112))), "sizeDelta": (rng.choice((360, 400, 440)), 40)}
    order = list(ELEMENTS)
    rng.shuffle(order)
    return {"rects": rects, "order": order, "spacing": rng.choice((8, 10, 12, 16)),
            "padding": (margin, margin, pad, pad)}


def layout_text(spec):
    """The six elements as prompt lines (one continuation line each)."""
    lines = []
    for name in ELEMENTS:
        rect = spec["rects"][name]
        lines.append("\n   %s: " % name + ", ".join("%s (%g, %g)" % (field, rect[field][0], rect[field][1])
                                                   for field in RECT_FIELDS))
    return "".join(lines)


def start_order(rng, spec):
    """A creation order for the six elements that differs from the order the task asks for."""
    order = list(ELEMENTS)
    rng.shuffle(order)
    if order == spec["order"]:
        order = order[1:] + order[:1]
    return order


def start_rect(rng):
    """Centre-anchored start values for one element: a non-zero position and a size no layout asks for."""
    offsets = [value for value in range(-300, 301, 10) if value]
    return {"anchorMinX": 0.5, "anchorMinY": 0.5, "anchorMaxX": 0.5, "anchorMaxY": 0.5, "pivotX": 0.5, "pivotY": 0.5,
            "anchoredPosX": rng.choice(offsets), "anchoredPosY": rng.choice(offsets),
            "sizeDeltaX": rng.choice(START_SIZES), "sizeDeltaY": rng.choice(START_SIZES)}


class OC5(PublicTask):
    id = "OC5"
    track = "ops"
    tier = "challenge"
    case_version = 1
    minutes = 20
    requires = {"pipeline": "URP", "mode": "bypass", "dedicated": True, "packages": ["com.unity.ugui"]}

    def build(self, ctx):
        c, rng = ctx.c, ctx.rng
        spec = layout_spec(ctx.seed)
        canvas_name, hud_name = rng.choice(CANVAS_NAMES), rng.choice(HUD_NAMES)
        c.write("asset_create_folder", folderPath=ctx.asset("Prefabs"))
        canvas = V.object_id(c.write("ui_create_canvas", name=canvas_name, renderMode="WorldSpace"))
        c.write("gameobject_set_parent", **dict(address(canvas, "child"), **address(ctx.root, "parent")))
        canvas_path = ctx.path(canvas_name)
        c.write("ui_set_rect_transform", path=canvas_path, anchorMinX=0.5, anchorMinY=0.5, anchorMaxX=0.5,
                anchorMaxY=0.5, pivotX=0.5, pivotY=0.5, anchoredPosX=0, anchoredPosY=0, anchoredPosZ=0,
                sizeDeltaX=CANVAS_SIZE[0], sizeDeltaY=CANVAS_SIZE[1], localScaleX=1, localScaleY=1, localScaleZ=1)
        hud = V.object_id(c.write("ui_create_panel", name=hud_name, parent=canvas_name, r=0.1, g=0.1, b=0.1, a=0.25))
        hud_path = canvas_path + "/" + hud_name
        c.write("ui_set_rect_transform", path=hud_path, anchorMinX=0.5, anchorMinY=0.5, anchorMaxX=0.5,
                anchorMaxY=0.5, pivotX=0.5, pivotY=0.5, anchoredPosX=0, anchoredPosY=0, anchoredPosZ=0,
                sizeDeltaX=CANVAS_SIZE[0], sizeDeltaY=CANVAS_SIZE[1])
        c.write("component_add", componentType="Canvas", **address(hud))
        elements = {}
        for name in start_order(rng, spec):
            elements[name] = V.object_id(c.write("ui_create_image", name=name, parent=hud_name, width=100, height=100))
        c.write("ui_set_rect_transform_batch", items=json.dumps(
            [dict(start_rect(rng), path=hud_path + "/" + name) for name in ELEMENTS]))
        c.write("component_add", componentType="HorizontalLayoutGroup", **address(elements["TopBar"]))
        c.write("gameobject_set_active", active=False, **address(elements["Notice"]))
        prefab_path = ctx.asset("Prefabs", hud_name + ".prefab")
        saved = c.write("prefab_create", path=hud_path, savePath=prefab_path)
        if saved.get("prefabPath") != prefab_path or not saved.get("connected"):
            raise ValueError("prefab_create did not leave a connected instance of %s" % prefab_path)
        tinted = rng.choice(("Ammo", "Minimap", "Compass"))
        c.write("component_set_serialized_property", componentType="Image", propertyPath="m_Color", value=TINT,
                path=hud_path + "/" + tinted)
        return {"canvas": canvas, "canvasPath": canvas_path, "hud": hud, "hudPath": hud_path,
                "prefabPath": prefab_path}

    def snapshot(self, ctx):
        p, h = ctx.probe, ctx.handles
        overrides = ctx.read("prefab_get_overrides", path=h["hudPath"], includeDetails=True, limit=200)
        hud = p.go(h["hud"]) or {}
        return {"data": {"overrides": override_rows(overrides),
                         "instance": {"name": hud.get("name"), "path": hud.get("path"),
                                      "parentPath": hud.get("parentPath"), "active": hud.get("isActive"),
                                      "components": hud.get("components"),
                                      "prefabPath": overrides.get("prefabPath")},
                         "asset": asset_state(ctx, h["prefabPath"]),
                         "canvas": {"state": V.go_state(p, h["canvas"]), "values": value_rows(p, h["canvas"])}},
                "digests": {}}

    def prompt_vars(self, ctx):
        spec = layout_spec(ctx.seed)
        pad = spec["padding"]
        return {"prefab": ctx.handles["prefabPath"], "hud": ctx.handles["hudPath"], "layout": layout_text(spec),
                "order": ", ".join(spec["order"]), "spacing": spec["spacing"],
                "padL": pad[0], "padR": pad[1], "padT": pad[2], "padB": pad[3]}

    def answer_schema(self):
        return {
            "type": "object",
            "required": ["prefabPath", "childOrder"],
            "properties": {
                "prefabPath": {"type": "string", "desc": {"zh-CN": "被编辑的 prefab 资产路径",
                                                          "en": "asset path of the edited prefab"}},
                "childOrder": {"type": "array", "items": {"type": "string", "desc": {
                    "zh-CN": "修改后 prefab 根节点直接子节点的名称，按 sibling index 顺序",
                    "en": "names of the prefab root's direct children after the edit, in sibling index order"}}},
            },
        }


def override_rows(overrides):
    """The overrides of the instance as sorted canonical rows (kind, node path, component, property, value)."""
    rows = []
    for entry in overrides.get("overrides") or []:
        if isinstance(entry, dict):
            rows.append(json.dumps({key: entry.get(key) for key in ("kind", "gameObjectPath", "component",
                                                                     "componentIndex", "property", "value",
                                                                     "objectReference", "target")},
                                   sort_keys=True))
    return sorted(rows)


def value_rows(probe, ref):
    """The object's component values as sorted [type, [[propertyPath, value], ...]] rows (Unity's property names, such
    as m_TargetDisplay, stay out of C0's keys)."""
    return [[kind, sorted([path, value] for path, value in (props or {}).items())]
            for kind, props in sorted(V.component_values(probe, ref).items())]


def asset_state(ctx, prefab_path):
    """Every node of the prefab asset with its components and serialized values, keyed by root-relative path."""
    hierarchy = ctx.read("prefab_get_hierarchy", prefabPath=prefab_path, limit=200)
    nodes, components = {}, {}
    for node in hierarchy.get("nodes") or []:
        path = node.get("gameObjectPath")
        nodes[path] = node
        listed = ctx.read("prefab_list_components", prefabPath=prefab_path, gameObjectPath=path,
                          includeProperties=True)
        components[path] = [{"type": comp.get("componentType"), "index": comp.get("componentIndex"),
                             "enabled": comp.get("enabled"),
                             "props": sorted([prop.get("propertyPath"), prop.get("value")]
                                             for prop in comp.get("properties") or [] if isinstance(prop, dict))}
                            for comp in listed.get("components") or [] if isinstance(comp, dict)]
    return {"guid": ctx.probe.guid(prefab_path), "truncated": hierarchy.get("truncated"), "nodes": nodes,
            "components": components}


TASK = OC5()

# Producer:Betsy
