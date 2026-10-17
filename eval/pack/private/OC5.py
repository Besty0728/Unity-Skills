"""OC5 private verifier: a HUD prefab asset brought to a given layout while its scene instance keeps its overrides.

What the task probes: the values must land in the prefab asset (prefab_set_transform / prefab_set_sibling_index /
prefab_set_active / prefab_set_serialized_property, or prefab_edit_batch), not on the scene instance: editing the
instance leaves the asset untouched and adds overrides, and prefab_apply afterwards pushes the instance's own colour
override into the asset and wipes the override the task says to keep; the child order can only be set in the asset;
posX/posY write localPosition, which differs from anchoredPosition for every corner-anchored child of a 1280 x 720
root; Notice is inactive in the asset and must be activated there; spacing and padding live on the TopBar layout group
as m_Spacing and m_Padding.m_Left/Right/Top/Bottom; the asset must be edited in place (same GUID).

The layout is recomputed from the seed exactly as the public module does (layout_spec is copied verbatim); the
"everything else unchanged" assertion compares the asset's nodes and serialized values with C0 minus the fields the
task changes.
"""
import json
import random

from usk_eval import verifylib as V

ELEMENTS = ("TopBar", "Ammo", "Minimap", "Crosshair", "Compass", "Notice")
CORNERS = (((0, 1), "TopLeft"), ((1, 1), "TopRight"), ((0, 0), "BottomLeft"), ((1, 0), "BottomRight"))
RECT_FIELDS = ("anchorMin", "anchorMax", "pivot", "anchoredPosition", "sizeDelta")
RECT_TOL = 1e-3
LAYOUT_GROUP = "HorizontalLayoutGroup"
# Serialized paths the task changes (prefix match): the six elements' RectTransform values and the derived local
# position, and the TopBar layout group's spacing and padding.
RECT_PROPS = ("m_AnchorMin", "m_AnchorMax", "m_Pivot", "m_AnchoredPosition", "m_SizeDelta", "m_LocalPosition")
GROUP_PROPS = ("m_Spacing", "m_Padding")
NODE_FIELDS = ("gameObject", "parentPath", "layer", "tag", "localEulerAngles", "localScale", "childCount",
               "prefabAssetType")
NODE_RECT_FIELDS = ("localPosition", "anchoredPosition", "anchorMin", "anchorMax", "pivot", "sizeDelta")

ASSERTIONS = [
    ("P1", 2.5, False, ["R2"], "the six RectTransforms of the prefab asset carry the requested anchorMin, anchorMax, "
                               "pivot, anchoredPosition and sizeDelta (fraction of elements)"),
    ("P2", 1.0, False, ["R3"], "the prefab root's direct children are in the requested sibling order"),
    ("P3", 1.0, False, ["R4"], "Notice is active (activeSelf) in the prefab asset"),
    ("P4", 1.0, False, ["R5"], "TopBar's HorizontalLayoutGroup in the asset has the requested spacing and padding"),
    ("P5", 1.5, False, ["R6"], "the scene object is still the same connected instance of the prefab and its "
                               "overrides are exactly the ones it had"),
    ("P6", 1.0, False, ["R7"], "nothing else changed: the asset's nodes, components and other serialized values, "
                               "and the scene Canvas"),
    ("P7", 0.5, False, ["R8", "G8"], "the answer reports the prefab path and the final child order"),
    ("P8", 0.5, True, ["G1", "G2"], "no change outside R/A, no other scene saved or opened"),
    ("P9", 0.5, False, ["G4"], "no file under A was written around the skills"),
    ("P10", 0.5, False, ["R1"], "the prefab asset still exists at its path with the same GUID, as a regular prefab"),
]

HINTS = {
    "P1": {"zh-CN": "用 prefab_set_transform（anchoredPosX/Y、anchorMin/Max、pivot、sizeDelta）或 prefab_edit_batch 在资产里写值；"
                    "posX/posY 写的是 localPosition，对角落锚点的元素不等于 anchoredPosition。",
           "en": "Write the values into the asset with prefab_set_transform (anchoredPosX/Y, anchorMin/Max, pivot, "
                 "sizeDelta) or prefab_edit_batch; posX/posY set localPosition, which is not anchoredPosition for a "
                 "corner-anchored element."},
    "P2": {"zh-CN": "子节点顺序只能在资产里改：按要求的顺序依次 prefab_set_sibling_index（index 0、1、2…）。",
           "en": "The child order can only be changed in the asset: prefab_set_sibling_index in the requested order "
                 "(index 0, 1, 2, ...)."},
    "P3": {"zh-CN": "用 prefab_set_active 激活资产中的 Notice；在场景实例上 set_active 只会产生覆盖。",
           "en": "Activate Notice in the asset with prefab_set_active; set_active on the scene instance only adds an "
                 "override."},
    "P4": {"zh-CN": "在资产的 TopBar 上写 HorizontalLayoutGroup 的 m_Spacing 和 m_Padding.m_Left/m_Right/m_Top/m_Bottom。",
           "en": "Write m_Spacing and m_Padding.m_Left/m_Right/m_Top/m_Bottom of the HorizontalLayoutGroup on the "
                 "asset's TopBar."},
    "P5": {"zh-CN": "不要修改场景实例，也不要 apply 或 revert 它的覆盖：prefab_apply 会把实例自己的颜色覆盖写进资产并清空覆盖。",
           "en": "Leave the scene instance alone and neither apply nor revert its overrides: prefab_apply pushes the "
                 "instance's own colour override into the asset and clears the overrides."},
    "P6": {"zh-CN": "除要求的字段外，资产的节点、组件和其他属性以及场景 Canvas 都要保持不变。",
           "en": "Apart from the requested fields, the asset's nodes, components and other values and the scene "
                 "Canvas must stay as they were."},
    "P7": {"zh-CN": "答案里报告 prefab 资产路径和修改后根节点子节点的名称顺序，并写明 declaredComplete。",
           "en": "Report the prefab asset path and the root's final child order, and declaredComplete."},
    "P8": {"zh-CN": "所有修改都必须在 R 与 A 之内，且只能保存本轮场景。",
           "en": "Keep every change inside R and A, and save no scene but this run's."},
    "P9": {"zh-CN": "不要绕过技能直接在 A 下写文件。", "en": "Do not write files under A directly; use the skills."},
    "P10": {"zh-CN": "就地编辑已有的 prefab 资产；不要删除、替换或重新创建它。",
            "en": "Edit the existing prefab asset in place; do not delete, replace or recreate it."},
}


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


def padding_props(spec):
    """Serialized path -> value of the TopBar layout group fields the task sets."""
    left, right, top, bottom = spec["padding"]
    return {"m_Spacing": spec["spacing"], "m_Padding.m_Left": left, "m_Padding.m_Right": right,
            "m_Padding.m_Top": top, "m_Padding.m_Bottom": bottom}


def _read(ctx, skill, **args):
    """Result of a read-only skill, or None when it answers with a business error."""
    envelope = ctx.probe.c.read_raw(skill, **args)
    if envelope.get("status") in ("error", "partial") or envelope.get("errorCode"):
        return None
    result = envelope.get("result", envelope)
    return result if isinstance(result, dict) and result.get("success") is not False else None


def override_rows(overrides):
    """The overrides of the instance as sorted canonical rows (kind, node path, component, property, value)."""
    rows = []
    for entry in (overrides or {}).get("overrides") or []:
        if isinstance(entry, dict):
            rows.append(json.dumps({key: entry.get(key) for key in ("kind", "gameObjectPath", "component",
                                                                     "componentIndex", "property", "value",
                                                                     "objectReference", "target")},
                                   sort_keys=True))
    return sorted(rows)


def asset_state(ctx, prefab_path):
    """Every node of the prefab asset with its components and serialized values, keyed by root-relative path; None
    when the asset cannot be read."""
    hierarchy = _read(ctx, "prefab_get_hierarchy", prefabPath=prefab_path, limit=200)
    if hierarchy is None:
        return None
    nodes, components = {}, {}
    for node in hierarchy.get("nodes") or []:
        path = node.get("gameObjectPath")
        nodes[path] = node
        listed = _read(ctx, "prefab_list_components", prefabPath=prefab_path, gameObjectPath=path,
                       includeProperties=True) or {}
        components[path] = [{"type": comp.get("componentType"), "index": comp.get("componentIndex"),
                             "enabled": comp.get("enabled"),
                             "props": sorted([prop.get("propertyPath"), prop.get("value")]
                                             for prop in comp.get("properties") or [] if isinstance(prop, dict))}
                            for comp in listed.get("components") or [] if isinstance(comp, dict)]
    return {"guid": ctx.probe.guid(prefab_path), "truncated": hierarchy.get("truncated"), "nodes": nodes,
            "components": components}


def _excluded(path, prefixes):
    return any(path == prefix or str(path).startswith(prefix + ".") for prefix in prefixes)


def _trimmed(state, root_children):
    """The asset state without the fields the task changes, for the unchanged comparison."""
    nodes, components = {}, {}
    for path, node in (state or {}).get("nodes", {}).items():
        kept = {field: node.get(field) for field in NODE_FIELDS}
        if path != "Notice":
            kept["activeSelf"] = node.get("activeSelf")
        if path not in root_children:
            kept["siblingIndex"] = node.get("siblingIndex")
        if path not in ELEMENTS:
            kept.update({field: node.get(field) for field in NODE_RECT_FIELDS})
        nodes[path] = kept
    for path, comps in (state or {}).get("components", {}).items():
        trimmed = []
        for comp in comps:
            drop = ()
            if path in ELEMENTS and str(comp.get("type")).endswith("RectTransform"):
                drop = RECT_PROPS
            elif path == "TopBar" and str(comp.get("type")).endswith(LAYOUT_GROUP):
                drop = GROUP_PROPS
            trimmed.append({"type": comp.get("type"), "index": comp.get("index"), "enabled": comp.get("enabled"),
                            "props": [pair for pair in comp.get("props") or [] if not _excluded(pair[0], drop)]})
        components[path] = trimmed
    return {"nodes": nodes, "components": components}


def _root_children(state):
    """Root-relative paths of the root's direct children in sibling order."""
    nodes = (state or {}).get("nodes") or {}
    children = [node for node in nodes.values() if node.get("parentPath") == ""]
    children.sort(key=lambda node: V.as_int(node.get("siblingIndex")) if V.as_int(node.get("siblingIndex"))
                  is not None else 1 << 30)
    return [node.get("gameObjectPath") for node in children]


def verify(ctx, card):
    p, h, c0 = ctx.probe, ctx.handles, ctx.c0
    spec = layout_spec(ctx.seed)
    prefab_path = h["prefabPath"]
    state = asset_state(ctx, prefab_path)
    nodes = (state or {}).get("nodes") or {}

    matched, drifted = 0, []
    for name in ELEMENTS:
        node = nodes.get(name) or {}
        wanted = spec["rects"][name]
        if all(V.close_tuple(node.get(field), wanted[field], RECT_TOL) for field in RECT_FIELDS):
            matched += 1
        else:
            drifted.append({name: {field: node.get(field) for field in RECT_FIELDS}})
    card.fraction("P1", matched / float(len(ELEMENTS)), expected=spec["rects"], actual=drifted[:6])

    order = _root_children(state)
    card.check("P2", order == spec["order"], expected=spec["order"], actual=order)
    card.check("P3", (nodes.get("Notice") or {}).get("activeSelf") is True,
               actual=(nodes.get("Notice") or {}).get("activeSelf"))

    group = _read(ctx, "prefab_get_serialized_properties", prefabPath=prefab_path, componentType=LAYOUT_GROUP,
                  gameObjectPath="TopBar") or {}
    values = {prop.get("propertyPath"): prop.get("value") for prop in group.get("properties") or []
              if isinstance(prop, dict)}
    wanted = padding_props(spec)
    found = {path: V.floats(values.get(path)) for path in wanted}
    card.check("P4", bool(values) and all(len(found[path]) == 1 and abs(found[path][0] - wanted[path]) <= RECT_TOL
                                          for path in wanted), expected=wanted, actual=found)

    hud = p.go(h["hud"]) or {}
    overrides = _read(ctx, "prefab_get_overrides", path=h["hudPath"], includeDetails=True, limit=200)
    before = c0.get("instance") or {}
    rows = override_rows(overrides)
    same_instance = bool(hud) and all(hud.get(field) == before.get(field) for field in ("name", "path", "parentPath")) \
        and hud.get("isActive") == before.get("active") and hud.get("components") == before.get("components")
    card.check("P5", same_instance and overrides is not None and overrides.get("prefabPath") == prefab_path and
               rows == (c0.get("overrides") or []),
               expected={"prefabPath": prefab_path, "overrides": c0.get("overrides")},
               actual={"instance": {field: hud.get(field) for field in ("name", "path", "parentPath", "isActive")},
                       "prefabPath": (overrides or {}).get("prefabPath"), "overrides": rows})

    root_children = set(order) | set(_root_children(c0.get("asset")))
    ok_asset, diff_asset = V.same(_trimmed(c0.get("asset"), root_children), _trimmed(state, root_children), RECT_TOL)
    canvas = c0.get("canvas") or {}
    ok_canvas, diff_canvas = V.same(canvas.get("state"), V.go_state(p, h["canvas"]), RECT_TOL)
    ok_rect, diff_rect = V.same(canvas.get("rect"), p.ser(h["canvas"], "RectTransform"), RECT_TOL)
    card.check("P6", state is not None and ok_asset and ok_canvas and ok_rect,
               actual={"asset": diff_asset, "canvas": diff_canvas, "canvasRect": diff_rect})

    reported = V.answer(ctx, "childOrder")
    card.check("P7", V.norm_path(V.answer(ctx, "prefabPath")) == prefab_path and isinstance(reported, list) and
               [item for item in reported if isinstance(item, str)] == order and
               isinstance(V.answer(ctx, "declaredComplete"), bool),
               expected={"prefabPath": prefab_path, "childOrder": order}, actual=ctx.answer)
    bad = ctx.violations()
    card.check("P8", not bad, actual={"violations": bad[:10]})
    direct = ctx.direct_writes()
    card.check("P9", not direct, actual={"directWrites": direct[:10]})
    asset = p.asset(prefab_path) or {}
    root = nodes.get("") or {}
    card.check("P10", bool(asset) and asset.get("guid") == (c0.get("asset") or {}).get("guid") and
               root.get("prefabAssetType") == "Regular",
               expected={"guid": (c0.get("asset") or {}).get("guid"), "prefabAssetType": "Regular"},
               actual={"guid": asset.get("guid"), "type": asset.get("type"),
                       "prefabAssetType": root.get("prefabAssetType")})

# Producer:Betsy
