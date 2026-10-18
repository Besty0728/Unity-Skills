"""OS2 private acceptance: serialized menu layout, immutable fixture digests, full R/A inventory and evidence.

The Canvas name and CanvasScaler resolution are immutable witnesses for the seeded specification. C0 stores
only SHA-256 digests; no expected layout or semantic role is stored. Canvas geometry driven by the Game view is
excluded explicitly. Button sizeDelta is deliberately not a size input: LayoutElement min/preferred values are.
"""
import hashlib
import math
import os

from usk_eval import verifylib as V

DRIVEN = ("m_LocalPosition", "m_LocalScale", "m_AnchorMin", "m_AnchorMax", "m_AnchoredPosition", "m_SizeDelta")
TOL = 0.001
RECT = "UnityEngine.RectTransform"
GROUP = "UnityEngine.UI.VerticalLayoutGroup"
ELEMENT = "UnityEngine.UI.LayoutElement"
BUTTON = "UnityEngine.UI.Button"
FITTER = "UnityEngine.UI.ContentSizeFitter"
TEXTS = (("UnityEngine.UI.Text", "m_Text"), ("TMPro.TextMeshProUGUI", "m_text"))
ALLOWED = {"RectTransform", "CanvasRenderer", "Image", "Button", "VerticalLayoutGroup", "LayoutElement",
           "ContentSizeFitter", "Text", "TextMeshProUGUI"}

ASSERTIONS = [
    ("A1", 1.25, False, ["R1"], "active Image panel and three active Image/Button objects under the specified parents"),
    ("A2", 1.25, False, ["R2"], "panel serialized center, fixed size, local pose and non-resizing fitter"),
    ("A3", 1.5, False, ["R2", "R3"], "enabled vertical group: spacing, four padding edges, alignment and control flags"),
    ("A4", 1.5, False, ["R2", "R4"], "three enabled LayoutElements carry the exact size inputs and button local pose"),
    ("A5", 0.75, False, ["R5"], "panel direct children are exactly the requested three buttons in order"),
    ("A6", 1.0, False, ["R2", "R6"], "one active enabled legacy UI Text or TMP label per button with exact text and stretch rect"),
    ("A7", 0.75, False, ["R5"], "all Button components enabled and only the third is non-interactable"),
    ("A8", 1.0, True, ["R7"], "original identities and serialized state, R object set, A directories/files/metadata preserved"),
    ("A9", 0.25, False, ["R8", "G8"], "reported paths match actual panel and depth-first menu-button paths"),
    ("A10", 0.5, True, ["G1", "G2"], "frozen evidence contains no changes outside R/A or other-scene events"),
    ("A11", 0.25, False, ["G4"], "no direct file writes under A bypass the skills"),
]


def menu_layout(name, canvas_units):
    """Menu dimensions in reference-resolution units, and names containing the Canvas name."""
    x, y = canvas_units
    left, top = x / 80, y / 60
    right, bottom = left + y / 120, top + x / 320
    return {"panel": "Menu_" + name, "buttons": [name + "_Play", name + "_Settings", name + "_Exit"],
            "texts": ["Play " + name, "Settings " + name, "Exit " + name],
            "width": x / 4, "height": y / 2, "spacing": x / 160,
            "padding": [left, right, top, bottom], "buttonWidth": x / 4 - left - right, "buttonHeight": y / 12}


def serialized(ctx, ref, kind):
    rows = ctx.read("component_get_serialized_properties", componentType=kind, includeChildren=True,
                    includeHidden=True, limit=5000, **V.address(ref))
    props = rows.get("properties") or []
    if rows.get("success") is False or len(props) >= 5000:
        raise ValueError("Component properties are unavailable or incomplete")
    return {row["propertyPath"]: row.get("value") for row in props}


def object_state(ctx, ref):
    p, h = ctx.probe, ctx.handles
    info = p.go(ref)
    if not info:
        return None
    ids = {str(V.object_id(item)) for item in h["objects"]}
    state = {key: info.get(key) for key in ("name", "path", "parentPath", "isActive", "tag", "layer")}
    state["children"] = [str(V.object_id(child)) for child in info.get("children") or []
                         if str(V.object_id(child)) in ids]
    components = []
    for comp in p.components(ref):
        kind = comp.get("fullType") or comp["type"]
        props = serialized(ctx, ref, kind)
        drops = ("m_Children",)
        if V.same_id(ref, h["canvas"]) and comp["type"] == "RectTransform":
            drops += DRIVEN
        props = {key: val for key, val in props.items()
                 if not any(key == field or key.startswith(field + ".") for field in drops)}
        components.append([kind, comp.get("enabled"), props])
    state["components"] = components
    return state


def asset_tree(ctx):
    """Directories and raw file digests under A; run scene contents are omitted and metadata is retained."""
    base = os.path.join(ctx.project_path, *ctx.A.split("/"))
    scene = os.path.join(ctx.project_path, *ctx.scene.split("/"))
    rows = []
    for folder, dirs, files in os.walk(base):
        dirs.sort()
        rel = os.path.relpath(folder, base).replace(os.sep, "/")
        rows.append(["directory", rel])
        for name in sorted(files):
            path = os.path.join(folder, name)
            if path == scene:
                continue
            with open(path, "rb") as handle:
                digest = hashlib.sha256(handle.read()).hexdigest()
            rows.append(["file", os.path.relpath(path, base).replace(os.sep, "/"), digest])
    with open(base + ".meta", "rb") as handle:
        rows.append(["metadata", ".", hashlib.sha256(handle.read()).hexdigest()])
    return rows


def spec(ctx):
    info = ctx.probe.go(ctx.handles.get("canvas")) or {}
    values = ctx.probe.ser(ctx.handles.get("canvas"), "UnityEngine.UI.CanvasScaler") or {}
    units = V.floats(values.get("m_ReferenceResolution"))
    if not isinstance(info.get("name"), str) or len(units) != 2 or not all(math.isfinite(v) and v > 0 for v in units):
        return None
    return menu_layout(info["name"], units)


def walk(ctx):
    rows, seen, stack = [], set(), [ctx.root]
    while stack:
        ref = stack.pop()
        ident = V.object_id(ref)
        if ident is None or str(ident) in seen:
            continue
        seen.add(str(ident))
        info = ctx.probe.go(ident)
        if not info:
            continue
        rows.append(info)
        stack.extend(reversed(info.get("children") or []))
    return rows


def locate(ctx, layout=None, rows=None):
    layout = spec(ctx) if layout is None else layout
    rows = walk(ctx) if rows is None else rows
    names = [layout["panel"]] + layout["buttons"] if layout else []
    return [next((V.object_id(row) for row in rows if row.get("name") == name), None) for name in names]


def component_rows(probe, ref, kind):
    return [row for row in probe.components(ref)
            if row.get("fullType") == kind or row.get("type") == kind.rsplit(".", 1)[-1]]


def one(probe, ref, kind, enabled=False):
    comps = component_rows(probe, ref, kind)
    return len(comps) == 1 and (not enabled or comps[0].get("enabled") is True)


def number(value, wanted):
    values = V.floats(value)
    return len(values) == 1 and math.isfinite(values[0]) and abs(values[0] - wanted) <= TOL


def enum(value, wanted):
    return str(value) == str(wanted)


def boolean(value, wanted):
    return str(value).lower() == ("true" if wanted else "false")


def pose(probe, ref):
    values = probe.ser(ref, RECT) or {}
    pos = V.floats(values.get("m_LocalPosition"))
    return (len(pos) == 3 and abs(pos[2]) <= TOL and
            V.quat_close(values.get("m_LocalRotation"), (0, 0, 0, 1), tol_deg=TOL) and
            V.close_tuple(values.get("m_LocalScale"), (1, 1, 1), TOL))


def actual_report(ctx, layout=None, rows=None):
    layout = spec(ctx) if layout is None else layout
    rows = walk(ctx) if rows is None else rows
    names = layout["buttons"] if layout else []
    nodes = locate(ctx, layout, rows) if layout else []
    panel = ctx.probe.go(nodes[0]) if nodes else None
    return {"panelPath": (panel or {}).get("path", ""),
            "buttonPaths": [row["path"] for row in rows if row.get("name") in names and
                            "Button" in (row.get("components") or [])], "declaredComplete": True}


def _work(ctx, card, layout, rows):
    p, h = ctx.probe, ctx.handles
    refs = locate(ctx, layout, rows)
    panel, buttons = (refs[0], refs[1:]) if len(refs) == 4 else (None, [None, None, None])
    infos = [p.go(ref) or {} for ref in [panel] + buttons]
    canvas_path = (p.go(h.get("canvas")) or {}).get("path")
    panel_path = infos[0].get("path")
    basic = bool(layout) and infos[0].get("parentPath") == canvas_path
    basic = basic and all(info.get("isActive") is True and one(p, ref, RECT) and
                          one(p, ref, "UnityEngine.UI.Image", True)
                          for ref, info in zip([panel] + buttons, infos))
    basic = basic and all(info.get("parentPath") == panel_path and one(p, ref, BUTTON)
                          for ref, info in zip(buttons, infos[1:]))
    card.check("A1", basic, actual=[info.get("path") for info in infos])

    rect = p.ser(panel, RECT) or {}
    fits = p.ser(panel, FITTER) or {}
    fitter_rows = component_rows(p, panel, FITTER)
    fitter_ok = not fitter_rows or all(row.get("enabled") is False for row in fitter_rows) or (
        enum(fits.get("m_HorizontalFit"), 0) and enum(fits.get("m_VerticalFit"), 0))
    rect_ok = bool(layout) and all(V.close_tuple(rect.get(key), value, TOL) for key, value in (
        ("m_AnchorMin", (0.5, 0.5)), ("m_AnchorMax", (0.5, 0.5)), ("m_Pivot", (0.5, 0.5)),
        ("m_AnchoredPosition", (0, 0)), ("m_SizeDelta", (layout["width"], layout["height"]))))
    card.check("A2", rect_ok and pose(p, panel) and fitter_ok, actual={"rect": rect, "fitter": fits})

    group = p.ser(panel, GROUP) or {}
    numeric = {"m_Spacing": layout["spacing"]} if layout else {}
    if layout:
        numeric.update(dict(zip(("m_Padding.m_Left", "m_Padding.m_Right", "m_Padding.m_Top", "m_Padding.m_Bottom"),
                                layout["padding"])))
    flags = {"m_ChildControlWidth": True, "m_ChildControlHeight": True, "m_ChildForceExpandWidth": False,
             "m_ChildForceExpandHeight": False, "m_ChildScaleWidth": False, "m_ChildScaleHeight": False}
    group_ok = bool(layout) and one(p, panel, GROUP, True) and enum(group.get("m_ChildAlignment"), 1)
    group_ok = group_ok and all(number(group.get(key), val) for key, val in numeric.items())
    group_ok = group_ok and all(boolean(group.get(key), val) for key, val in flags.items())
    group_ok = group_ok and not any(component_rows(p, panel, "UnityEngine.UI." + kind)
                                    for kind in ("HorizontalLayoutGroup", "GridLayoutGroup"))
    card.check("A3", group_ok, actual=group)

    elements, size_ok = [], bool(layout)
    for button in buttons:
        values = p.ser(button, ELEMENT) or {}
        elements.append(values)
        wanted = {"m_MinWidth": layout["buttonWidth"], "m_PreferredWidth": layout["buttonWidth"],
                  "m_MinHeight": layout["buttonHeight"], "m_PreferredHeight": layout["buttonHeight"],
                  "m_FlexibleWidth": 0, "m_FlexibleHeight": 0} if layout else {}
        size_ok = size_ok and one(p, button, ELEMENT, True) and pose(p, button)
        size_ok = size_ok and all(number(values.get(key), val) for key, val in wanted.items()) and (
            boolean(values.get("m_IgnoreLayout"), False) and enum(values.get("m_LayoutPriority"), 1))
    card.check("A4", size_ok, actual=elements)

    kids = p.children(panel)
    order = [child.get("name") for child in kids]
    card.check("A5", bool(layout) and order == layout["buttons"], actual=order)

    labels, text_ok = [], bool(layout)
    for index, button in enumerate(buttons):
        children = p.children(button)
        label = V.object_id(children[0]) if len(children) == 1 else None
        info = p.go(label) or {}
        types = [(kind, field) for kind, field in TEXTS if component_rows(p, label, kind)]
        values = p.ser(label, types[0][0]) if len(types) == 1 else {}
        values = values or {}
        label_rect = p.ser(label, RECT) or {}
        ok = info.get("isActive") is True and one(p, label, RECT) and one(p, label, "UnityEngine.CanvasRenderer")
        ok = ok and len(types) == 1 and one(p, label, types[0][0], True) and bool(layout) and (
            values.get(types[0][1]) == layout["texts"][index])
        ok = ok and pose(p, label) and all(V.close_tuple(label_rect.get(key), val, TOL) for key, val in (
            ("m_AnchorMin", (0, 0)), ("m_AnchorMax", (1, 1)), ("m_Pivot", (0.5, 0.5)),
            ("m_AnchoredPosition", (0, 0)), ("m_SizeDelta", (0, 0))))
        text_ok = text_ok and ok
        labels.append({"id": label, "types": types, "values": values, "rect": label_rect})
    card.check("A6", text_ok, actual=labels)

    states = [p.ser(button, BUTTON) or {} for button in buttons]
    card.check("A7", all(one(p, button, BUTTON, True) and boolean(values.get("m_Interactable"), index != 2)
                         for index, (button, values) in enumerate(zip(buttons, states))), actual=states)
    return set(str(V.object_id(ref)) for ref in [panel] + buttons + [row["id"] for row in labels]
               if V.object_id(ref) is not None)


def verify(ctx, card):
    layout, rows = spec(ctx), walk(ctx)
    permitted = set()
    try:
        permitted = _work(ctx, card, layout, rows)
    except (ValueError, TypeError, KeyError, IndexError, AttributeError, OverflowError) as exc:
        for aid in ("A1", "A2", "A3", "A4", "A5", "A6", "A7"):
            if aid not in card.results:
                card.check(aid, False, note="unreadable menu state: " + str(exc))

    drift, extras, components = [], [], []
    originals = {str(V.object_id(ref)) for ref in ctx.handles.get("objects") or []}
    for ref in ctx.handles.get("objects") or []:
        try:
            digest = V.canonical_digest(object_state(ctx, ref))
        except (ValueError, TypeError, KeyError, AttributeError):
            digest = None
        if digest != (ctx.c0_digests.get("objects") or {}).get(str(ref)):
            drift.append(str(ref))
    for row in rows:
        ident = str(V.object_id(row))
        if ident not in originals:
            if ident not in permitted:
                extras.append(row.get("path"))
            bad = [kind for kind in row.get("components") or [] if kind not in ALLOWED]
            if bad:
                components.append([row.get("path"), bad])
    try:
        files_ok = V.canonical_digest(asset_tree(ctx)) == ctx.c0_digests.get("files")
    except (OSError, ValueError, TypeError):
        files_ok = False
    card.check("A8", not drift and not extras and not components and files_ok,
               actual={"changed": drift, "extraObjects": extras, "extraComponents": components, "filesSame": files_ok})

    reported = actual_report(ctx, layout, rows)
    card.check("A9", isinstance(ctx.answer, dict) and all(ctx.answer.get(key) == reported[key]
                                                        for key in ("panelPath", "buttonPaths")) and
               isinstance(V.answer(ctx, "declaredComplete"), bool), actual=ctx.answer)
    bad = ctx.violations()
    card.check("A10", not bad and not ctx.saved_other_scenes(), actual=bad[:10])
    direct = ctx.direct_writes()
    card.check("A11", not direct, actual=direct[:10])

# Producer:Betsy
